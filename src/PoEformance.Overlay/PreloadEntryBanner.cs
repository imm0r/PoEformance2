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

        IconCache.Picture picture = Icons?.PictureFor(style.Icon, IconCache.MaxWideEdge) ?? default;
        if (picture.Ready)
        {
            float tall = picture.HeightAt(plate);

            // White, faded - NOT the weight's colour. Somebody supplying a painted plate
            // supplied its colours too, and multiplying it by the red that "dangerous"
            // happens to default to would make every custom banner look broken. The same
            // reasoning as the markers, where it was learned.
            draw.AddImage(
                picture.Texture,
                new Vector2(centre - (plate / 2f), y),
                new Vector2(centre + (plate / 2f), y + tall),
                Vector2.Zero,
                Vector2.One,
                Fade(0xFFFFFFFF, alpha));

            y += tall;
        }
        else
        {
            // No picture chosen, or it could not be loaded. The weight's own name in its own
            // colour, which is the whole point of the plate said in the plainest way there is.
            string word = weight.ToString().ToUpperInvariant();
            Vector2 size = ImGui.CalcTextSize(word);
            draw.AddText(new Vector2(centre - (size.X / 2f), y), colour, word);
            y += size.Y + 2f;
        }

        // The names always, whether or not there was a picture. A plate says what KIND of
        // thing is here; the line is the thing itself, and it is the half somebody acts on.
        Vector2 line = ImGui.CalcTextSize(names);
        var at = new Vector2(centre - (line.X / 2f), y + TextPadY);

        draw.AddRectFilled(
            at - new Vector2(TextPadX, TextPadY),
            at + line + new Vector2(TextPadX, TextPadY),
            Fade(Style.Colour(StyleCatalogue.Keys.PreloadBannerBack), alpha),
            4f);

        draw.AddText(at, colour, names);
        return at.Y + line.Y + TextPadY;
    }

    /// <summary>Scales a packed colour's alpha, leaving its colour alone.</summary>
    private static uint Fade(uint colour, float by)
    {
        uint alpha = (uint)Math.Clamp(((colour >> 24) & 0xFF) * by, 0f, 255f);
        return (colour & 0x00FFFFFF) | (alpha << 24);
    }
}
