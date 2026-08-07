using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What this area holds, said once across the screen on the way in.
/// </summary>
/// <remarks>
/// ONE PLATE PER WEIGHT, each naming what belongs to it. An area with a breach and a rogue
/// exile has two things to say and they are not the same kind of news - a single line would
/// either file the exile under a heading about loot or leave it out. This is also what makes
/// the picture worth having: a plate can say "dangerous" in a shape the eye reads before the
/// word underneath it has been focused on.
///
/// Its own layer rather than the alert banner. That one exists to interrupt with something
/// happening NOW and is deliberately one line; this is a title card for the place you just
/// walked into, and the two would fight over the same spot and the same colours.
///
/// The pictures are optional and go through the same icon mechanism as every custom marker:
/// point a weight's style entry at a file and it is drawn, leave it empty and the weight's
/// name is drawn instead. A picture that is missing, moved, or not a picture degrades to the
/// text form, because a title card that fails to load must not take the names with it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PreloadEntryBanner
{
    /// <summary>Where the first plate sits, as a fraction of the screen height.</summary>
    private const float FromTop = 0.11f;

    /// <summary>How wide a plate is drawn, as a fraction of the screen width.</summary>
    /// <remarks>
    /// A third. Wide enough that the ornament survives being scaled down from a source image
    /// that is usually over a thousand pixels, narrow enough that three stacked plates do not
    /// become the screen. A weight's Scale multiplies it, so one can be made to shout.
    /// </remarks>
    private const float PlateWidth = 0.33f;

    private const float TextPadX = 12f;
    private const float TextPadY = 5f;
    private const float BlockGap = 10f;

    /// <summary>
    /// Where a plate's writing goes, as fractions of the picture it is written on.
    /// </summary>
    /// <remarks>
    /// The shipped plates are built the same way and any replacement should be: an emblem on
    /// the LEFT - a book, a gem, a skull - and open cloth to the right of it. So the names sit
    /// in that cloth rather than centred on the whole plate, which would put them through the
    /// emblem, and rather than under the plate, which would leave a painted banner deliberately
    /// empty with its text floating below it.
    ///
    /// Numbers, not a measurement of the picture. Finding the clear area by looking at the
    /// pixels is guesswork that fails differently on every image; a stated safe area is
    /// something a plate can be DRAWN to, and it is written down beside the pictures.
    /// </remarks>
    private const float TextLeft = 0.42f;
    private const float TextRight = 0.94f;
    private const float TextMiddle = 0.45f;

    private IReadOnlyList<(PreloadWeight Weight, string Names)> _saying = [];
    private long _atMs;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Where a weight's picture comes from, when one was chosen.</summary>
    public IconCache? Icons { get; set; }

    /// <summary>Says a new set of findings, replacing anything still on screen.</summary>
    public void Announce(IReadOnlyList<(PreloadWeight Weight, string Names)> saying, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(saying);
        _saying = saying;
        _atMs = nowMs;
    }

    /// <summary>Takes it off the screen.</summary>
    public void Clear() => _saying = [];

    /// <summary>Draws the card, if there is one and it has not faded out.</summary>
    public void Draw(ImDrawListPtr draw, int width, int height, long nowMs)
    {
        if (_saying.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        float alpha = PreloadCard.Readability(nowMs - _atMs);
        if (alpha <= 0f)
        {
            _saying = [];
            return;
        }

        float y = height * FromTop;
        foreach ((PreloadWeight weight, string names) in _saying)
        {
            string key = StyleCatalogue.ForWeight(weight);
            if (!Style.Visible(key))
            {
                continue;   // this weight was switched off; the others still have their say
            }

            y = DrawBlock(draw, width, y, key, weight, names, alpha) + BlockGap;
        }
    }

    /// <summary>One plate and its line. Returns where the next block starts.</summary>
    private float DrawBlock(
        ImDrawListPtr draw, int width, float top, string key, PreloadWeight weight, string names, float alpha)
    {
        LayerStyle style = Style[key];
        uint colour = Fade(Style.Colour(key), alpha);
        float plate = width * PlateWidth * (style.Scale > 0f ? style.Scale : 1f);
        float centre = width / 2f;
        float y = top;

        IconCache.Picture picture = PlateFor(weight, style.Icon);
        Vector2 line = ImGui.CalcTextSize(names);

        if (picture.Ready)
        {
            float tall = picture.HeightAt(plate);
            float left = centre - (plate / 2f);

            // White, faded - NOT the weight's colour. Somebody supplying a painted plate
            // supplied its colours too, and multiplying it by the red that "dangerous"
            // happens to default to would make every custom banner look broken. The same
            // reasoning as the markers, where it was learned.
            draw.AddImage(
                picture.Texture,
                new Vector2(left, y),
                new Vector2(centre + (plate / 2f), y + tall),
                Vector2.Zero,
                Vector2.One,
                Fade(0xFFFFFFFF, alpha));

            // In the open cloth to the right of the emblem, centred there rather than on the
            // plate. Clamped so a long list runs into the free space instead of over the
            // emblem, and cannot leave the cloth at either end.
            float from = left + (plate * TextLeft);
            float to = left + (plate * TextRight);
            float x = Math.Clamp(
                ((from + to) / 2f) - (line.X / 2f), from, Math.Max(from, to - line.X));

            Written(draw, new Vector2(x, y + (tall * TextMiddle) - (line.Y / 2f)), names, colour, alpha);
            return y + tall;
        }

        // No picture chosen, or it could not be loaded. The weight's own name over a plain
        // backing, which is the whole point of the plate said in the plainest way there is -
        // and the names below it, since there is no cloth to write them on.
        string word = weight.ToString().ToUpperInvariant();
        Vector2 size = ImGui.CalcTextSize(word);
        draw.AddText(new Vector2(centre - (size.X / 2f), y), colour, word);
        y += size.Y + 2f;

        var at = new Vector2(centre - (line.X / 2f), y + TextPadY);

        draw.AddRectFilled(
            at - new Vector2(TextPadX, TextPadY),
            at + line + new Vector2(TextPadX, TextPadY),
            Fade(Style.Colour(StyleCatalogue.Keys.PreloadBannerBack), alpha),
            4f);

        draw.AddText(at, colour, names);
        return at.Y + line.Y + TextPadY;
    }

    /// <summary>Writes a line with a dark edge under it, so any cloth can be read off.</summary>
    /// <remarks>
    /// The colours are the user's and the cloth is the artist's, and nothing checks that the
    /// two go together - red names on the red banner is a perfectly reachable combination. A
    /// one-pixel shadow costs four extra draws and makes every pairing legible, which beats
    /// telling somebody their colour choice was wrong.
    /// </remarks>
    private static void Written(ImDrawListPtr draw, Vector2 at, string text, uint colour, float alpha)
    {
        uint edge = Fade(0xFF000000, alpha * 0.85f);
        draw.AddText(at + new Vector2(1f, 0f), edge, text);
        draw.AddText(at + new Vector2(-1f, 0f), edge, text);
        draw.AddText(at + new Vector2(0f, 1f), edge, text);
        draw.AddText(at + new Vector2(0f, -1f), edge, text);
        draw.AddText(at, colour, text);
    }

    /// <summary>
    /// The plate for a weight: the chosen file, else the one that shipped, else nothing.
    /// </summary>
    /// <remarks>
    /// That order and not the other one. A shipped plate means the card looks right the
    /// moment the tool is installed, with no path to type and no file to keep hold of; a
    /// chosen file has to beat it, or setting one would appear to do nothing.
    /// </remarks>
    private IconCache.Picture PlateFor(PreloadWeight weight, string? chosen)
    {
        if (Icons is not IconCache icons)
        {
            return default;
        }

        IconCache.Picture own = icons.PictureFor(chosen, IconCache.MaxWideEdge);
        return own.Ready
            ? own
            : icons.BuiltIn($"preload-{weight.ToString().ToLowerInvariant()}.png", IconCache.MaxWideEdge);
    }

    /// <summary>Scales a packed colour's alpha, leaving its colour alone.</summary>
    private static uint Fade(uint colour, float by)
    {
        uint alpha = (uint)Math.Clamp(((colour >> 24) & 0xFF) * by, 0f, 255f);
        return (colour & 0x00FFFFFF) | (alpha << 24);
    }
}
