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

    /// <summary>
    /// How tall a plate is drawn, as a fraction of the screen HEIGHT.
    /// </summary>
    /// <remarks>
    /// Height, not width, and that is the whole fix for "far too big". Sizing a plate by the
    /// width means an ultrawide gets a card a third of 3440 pixels across - and since the
    /// height follows the picture's proportions, roughly 300 pixels tall EACH, three of them
    /// filling most of the screen. The height of a screen is what does not change between one
    /// monitor and the next, so the card is the same size on all of them.
    ///
    /// Seven and a half percent is about 108 pixels tall on a 1440p screen and roughly 410
    /// wide at these plates' proportions: read at a glance, gone in five seconds, and three of
    /// them stacked still leave the fight visible. It is a THIRD of what sizing by the width
    /// gave an ultrawide. A weight's Scale multiplies it, so it is the knob for anybody who
    /// wants them louder or quieter than this.
    /// </remarks>
    private const float PlateHeight = 0.075f;

    /// <summary>A plate may not take more of the width than this, whatever its shape.</summary>
    /// <remarks>
    /// A guard on somebody else's picture. The width comes from the proportions of a file this
    /// code did not choose, so a very long thin banner - or a Scale wound up - would otherwise
    /// run off both edges of the screen.
    /// </remarks>
    private const float WidestPlate = 0.45f;

    /// <summary>How much of a plate's height the writing on it takes.</summary>
    /// <remarks>
    /// Scaled to the plate rather than left at the interface's font size, which is the other
    /// half of the same complaint: text sized for a window looks lost on a painted banner, and
    /// the banner is the thing being looked at.
    /// </remarks>
    private const float TextHeight = 0.34f;

    /// <summary>Below this the writing is not worth drawing at all.</summary>
    private const float SmallestText = 10f;

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
    private const float TextLeft = 0.40f;
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

        // Whether it is OVER, which only the clock can say. Asking whether it is READABLE and
        // dropping it when the answer is zero throws the card away at age zero - where the
        // fade in begins - and announcing happens in the same frame as drawing, so that
        // destroyed every card on its own first frame.
        long age = nowMs - _atMs;
        if (!PreloadCard.Showing(age))
        {
            _saying = [];
            return;
        }

        float alpha = PreloadCard.Readability(age);

        float y = height * FromTop;
        foreach ((PreloadWeight weight, string names) in _saying)
        {
            string key = StyleCatalogue.ForWeight(weight);
            if (!Style.Visible(key))
            {
                continue;   // this weight was switched off; the others still have their say
            }

            y = DrawBlock(draw, width, height, y, key, weight, names, alpha) + BlockGap;
        }
    }

    /// <summary>One plate and its line. Returns where the next block starts.</summary>
    private float DrawBlock(
        ImDrawListPtr draw,
        int width,
        int height,
        float top,
        string key,
        PreloadWeight weight,
        string names,
        float alpha)
    {
        LayerStyle style = Style[key];
        uint colour = Fade(Style.Colour(key), alpha);
        float centre = width / 2f;

        IconCache.Picture picture = PlateFor(weight, style.Icon);

        if (picture.Ready)
        {
            // From the HEIGHT, with the width following the picture's own proportions -
            // capped, because those proportions come from a file this code did not choose.
            float tall = height * PlateHeight * (style.Scale > 0f ? style.Scale : 1f);
            float wide = Math.Min(tall * picture.Width / picture.Height, width * WidestPlate);
            tall = wide * picture.Height / picture.Width;
            float left = centre - (wide / 2f);

            // White, faded - NOT the weight's colour. Somebody supplying a painted plate
            // supplied its colours too, and multiplying it by the red that "dangerous"
            // happens to default to would make every custom banner look broken. The same
            // reasoning as the markers, where it was learned.
            draw.AddImage(
                picture.Texture,
                new Vector2(left, top),
                new Vector2(centre + (wide / 2f), top + tall),
                Vector2.Zero,
                Vector2.One,
                Fade(0xFFFFFFFF, alpha));

            // In the open cloth to the right of the emblem, written at a size the cloth
            // decides - a line sized for a window is lost on a painted banner.
            float from = left + (wide * TextLeft);
            float to = left + (wide * TextRight);
            (string said, float font) = FitFor(names, tall * TextHeight, to - from);

            if (font >= SmallestText && said.Length > 0)
            {
                Vector2 line = Measure(said, font);
                float x = Math.Clamp(
                    ((from + to) / 2f) - (line.X / 2f), from, Math.Max(from, to - line.X));

                Written(
                    draw, new Vector2(x, top + (tall * TextMiddle) - (line.Y / 2f)),
                    said, font, colour, alpha);
            }

            return top + tall;
        }

        // No picture chosen, or it could not be loaded. The weight's own name over a plain
        // backing, which is the whole point of the plate said in the plainest way there is -
        // and the names below it, since there is no cloth to write them on.
        float y = top;
        string word = weight.ToString().ToUpperInvariant();
        Vector2 heading = ImGui.CalcTextSize(word);
        draw.AddText(new Vector2(centre - (heading.X / 2f), y), colour, word);
        y += heading.Y + 2f;

        Vector2 plain = ImGui.CalcTextSize(names);
        var at = new Vector2(centre - (plain.X / 2f), y + TextPadY);

        draw.AddRectFilled(
            at - new Vector2(TextPadX, TextPadY),
            at + plain + new Vector2(TextPadX, TextPadY),
            Fade(Style.Colour(StyleCatalogue.Keys.PreloadBannerBack), alpha),
            4f);

        draw.AddText(at, colour, names);
        return at.Y + plain.Y + TextPadY;
    }

    /// <summary>How far below the height's size the writing may be shrunk to fit.</summary>
    /// <remarks>
    /// Half. Past that the line stops looking like part of the banner and starts looking like
    /// something that failed to load, and a card nobody can read from across a fight has
    /// spent its five seconds for nothing.
    /// </remarks>
    private const float SmallestShrink = 0.5f;

    /// <summary>
    /// What to write on the cloth, and how big.
    /// </summary>
    /// <remarks>
    /// THE CLOTH IS NARROWER THAN IT LOOKS - the emblem takes the left of it - so a real
    /// area's worth of names does not fit at the size the height wants. Shrinking alone is not
    /// the answer either: seven mechanics scaled to fit that width work out at a few pixels,
    /// which is not small text but no text at all.
    ///
    /// So it shrinks to a floor, and past the floor it says FEWER things: names are dropped
    /// from the end - they are sorted by how much of each the area holds, so the ones that go
    /// are the slightest - and what went is counted in a "+3". The whole list is in the corner
    /// and in the window; this is the five-second version of it.
    /// </remarks>
    private static (string Text, float Font) FitFor(string names, float tall, float wide)
    {
        float natural = ImGui.GetFontSize();
        float floor = Math.Max(tall * SmallestShrink, SmallestText);

        float Fits(string text)
        {
            float measured = ImGui.CalcTextSize(text).X;
            return measured <= 0f ? tall : Math.Min(tall, natural * wide / measured);
        }

        float font = Fits(names);
        if (font >= floor)
        {
            return (names, font);
        }

        string[] all = names.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        for (int kept = all.Length - 1; kept >= 1; kept--)
        {
            string shorter = $"{string.Join(", ", all.Take(kept))} +{all.Length - kept}";
            float room = Fits(shorter);
            if (room >= floor)
            {
                // At the size the shorter line actually allows, not at the floor. Dropping a
                // name buys width, and spending it on being readable is the point of dropping
                // one - writing the survivors at the smallest permitted size throws it away.
                return (shorter, room);
            }
        }

        // One name and it still does not fit. Written at the floor and allowed to run into the
        // cloth it was not offered - better a name slightly across the ornament than a plate
        // that says nothing at all.
        return (all.Length > 0 ? all[0] : names, floor);
    }

    /// <summary>How big a line is at a size other than the interface's own.</summary>
    /// <remarks>
    /// Scaled from a measurement at the current size, which is what ImGui offers: measuring at
    /// an arbitrary size means pushing a font, and a bitmap font scales linearly anyway.
    /// </remarks>
    private static Vector2 Measure(string text, float font)
        => ImGui.CalcTextSize(text) * (font / ImGui.GetFontSize());

    /// <summary>Writes a line with a dark edge under it, so any cloth can be read off.</summary>
    /// <remarks>
    /// The colours are the user's and the cloth is the artist's, and nothing checks that the
    /// two go together - red names on the red banner is a perfectly reachable combination. A
    /// shadow costs four extra draws and makes every pairing legible, which beats telling
    /// somebody their colour choice was wrong.
    ///
    /// The edge grows with the writing. One pixel disappears under text drawn three times the
    /// interface's size, which is exactly where the background is busiest.
    /// </remarks>
    private static void Written(
        ImDrawListPtr draw, Vector2 at, string text, float font, uint colour, float alpha)
    {
        ImFontPtr face = ImGui.GetFont();
        uint edge = Fade(0xFF000000, alpha * 0.85f);
        float step = Math.Max(1f, font / 14f);

        draw.AddText(face, font, at + new Vector2(step, 0f), edge, text);
        draw.AddText(face, font, at + new Vector2(-step, 0f), edge, text);
        draw.AddText(face, font, at + new Vector2(0f, step), edge, text);
        draw.AddText(face, font, at + new Vector2(0f, -step), edge, text);
        draw.AddText(face, font, at, colour, text);
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
