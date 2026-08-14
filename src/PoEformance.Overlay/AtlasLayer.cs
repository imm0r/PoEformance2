using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Writes on the endgame atlas: what each map is called, what is in it, and how to get there.
/// </summary>
/// <remarks>
/// The atlas is a web of unnamed nodes with no indication of which line leads anywhere worth
/// going. This puts the name back on each one, lists what the game says is in it, and draws the
/// way to whichever kinds of map somebody is looking for.
///
/// EVERYTHING IS ALREADY DECIDED by the time it arrives here. Which maps to draw, what group
/// each is in, where a route runs - all of it comes from <see cref="AtlasWatch"/> on the reader
/// thread, because that is memory reading and this is the thread drawing frames. What happens
/// here is placement and colour.
///
/// DRAWN IN LAYERS, and it has to be. A label is opaque, a route crosses the whole screen, and
/// in node order every line lands on top of half the labels it passes. So it goes over the list
/// three times - connections, then routes, then labels - rather than drawing each map whole.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AtlasLayer
{
    /// <summary>How faint a finished map goes, on the occasions they are shown at all.</summary>
    private const float DoneFade = 0.4f;

    /// <summary>
    /// How everything here looks. Shared with the overlay, so one editor covers all of it.
    /// </summary>
    /// <remarks>
    /// The group colours are NOT in here - they belong to the groups, which are somebody's own
    /// list. This covers what a map with no group uses and what no group can colour: the plate,
    /// the connections, and the dot on the near end of a route.
    /// </remarks>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>
    /// Write each map's name on it.
    /// </summary>
    /// <remarks>
    /// Off still draws the contents and the routes, which is the point of having it: the
    /// names are the busiest thing on the atlas, and somebody who only wants to see WHERE the
    /// breaches are does not want two hundred labels as well.
    /// </remarks>
    public bool ShowNames { get; set; } = true;

    /// <summary>
    /// How big the writing is, against the interface's own size.
    /// </summary>
    /// <remarks>
    /// The atlas is a screen full of small text and a 4K monitor makes it smaller. Whether the
    /// default reads at a real resolution is not something that can be decided from here, so it
    /// is a number somebody can turn up rather than one to be guessed right.
    /// </remarks>
    public float TextScale { get; set; } = 1f;

    /// <summary>Where the label sits relative to the node it names.</summary>
    /// <remarks>
    /// Nudged UP, off the node's own art. The game draws an icon in the middle of each node,
    /// and a plate centred exactly on it hides the thing it is labelling.
    /// </remarks>
    public Vector2 Nudge { get; set; } = new(0f, -20f);

    /// <summary>
    /// Draws the atlas view over the game's own.
    /// </summary>
    /// <remarks>
    /// Takes the view as it was published rather than reading it twice: the reader thread
    /// replaces it whole, so reading it once here means every line and label in a frame agrees
    /// about where the atlas was.
    /// </remarks>
    public void Draw(ImDrawListPtr draw, AtlasView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!view.Anything)
        {
            return;
        }

        Vector2 screen = ImGui.GetIO().DisplaySize;

        if (Style.Visible(StyleCatalogue.Keys.AtlasWeb))
        {
            uint web = Style.Colour(StyleCatalogue.Keys.AtlasWeb);
            float thin = Style.Width(StyleCatalogue.Keys.AtlasWeb, 1.5f);
            foreach ((Vector2 from, Vector2 to) in view.Web)
            {
                if (Worth(from, to, screen))
                {
                    draw.AddLine(from, to, web, thin);
                }
            }
        }

        if (Style.Visible(StyleCatalogue.Keys.AtlasRoute))
        {
            foreach (AtlasMark mark in view.Marks)
            {
                if (mark.Route.Count > 0)
                {
                    DrawRoute(draw, mark, screen);
                }
            }
        }

        if (Style.Visible(StyleCatalogue.Keys.AtlasLabel))
        {
            foreach (AtlasMark mark in view.Marks)
            {
                DrawLabel(draw, mark);
            }
        }
    }

    /// <summary>Draws the way to one map, in its group's colour.</summary>
    /// <remarks>
    /// RUN BY RUN, with the holes left as holes. A route can cross a map the panel has no
    /// position for, and a line joining the two sides of that gap runs along no connection
    /// anybody can walk - which is what "some of the lines are arbitrary" was.
    ///
    /// With the marker on the end NEAREST YOU rather than on the destination. The destination
    /// is already named and coloured; what a route is actually asked is "which of the maps I
    /// can enter now does this start from", and that end is otherwise just where a line stops.
    /// </remarks>
    private void DrawRoute(ImDrawListPtr draw, AtlasMark mark, Vector2 screen)
    {
        uint colour = ColourOf(mark, 0.55f);
        float width = Style.Width(StyleCatalogue.Keys.AtlasRoute, 4f);

        foreach (IReadOnlyList<Vector2> run in mark.Route)
        {
            for (int i = 1; i < run.Count; i++)
            {
                if (Worth(run[i - 1], run[i], screen))
                {
                    draw.AddLine(run[i - 1], run[i], colour, width);
                }
            }
        }

        // The near end of the FIRST run, which is the nearest end that could be placed. With
        // the start of the route missing there is nothing honest to mark, and the dot is
        // better absent than sitting on a map the route does not begin at.
        Vector2 entry = mark.Route[0][0];
        if (!Style.Visible(StyleCatalogue.Keys.AtlasEntry) || !On(entry, screen))
        {
            return;
        }

        float radius = Style.Sized(StyleCatalogue.Keys.AtlasEntry, MathF.Max(3f, width * 1.3f));
        draw.AddCircleFilled(entry, radius, Style.Colour(StyleCatalogue.Keys.AtlasEntry));
        draw.AddCircle(entry, radius, 0xFF00_0000, 0, MathF.Max(1f, radius * 0.35f));
    }

    /// <summary>Draws one map's name, and what is in it underneath.</summary>
    private void DrawLabel(ImDrawListPtr draw, AtlasMark mark)
    {
        if (mark.Name.Length == 0)
        {
            return;
        }

        float alpha = mark.State == AtlasNodeState.Completed ? DoneFade : 1f;
        uint text = ColourOf(mark, alpha);
        uint plate = OverlaySettings.Fade(Style.Colour(StyleCatalogue.Keys.AtlasPlate), alpha);

        // With the names off, the contents start where the name would have been - otherwise
        // they hang below a gap nothing is in.
        Vector2 at = mark.Where + Nudge;
        float below = at.Y;

        if (ShowNames)
        {
            // How far away it is, on the LABEL rather than beside the line. A route crosses
            // other maps, so a number floating on it belongs to whichever one it is over.
            string title = mark.Hops > 0 ? $"{mark.Name} ({mark.Hops})" : mark.Name;
            below = DrawName(draw, mark, title, at, text, plate, alpha);
        }

        if (mark.Contents.Count == 0 || !Style.Visible(StyleCatalogue.Keys.AtlasContent))
        {
            return;
        }

        uint said = OverlaySettings.Fade(Style.Colour(StyleCatalogue.Keys.AtlasContent), alpha);

        float font = Font;
        foreach (string line in mark.Contents)
        {
            Vector2 wide = Measure(line, font);
            var where = new Vector2(mark.Where.X + Nudge.X - (wide.X * 0.5f), below);

            draw.AddRectFilled(where - new Vector2(3f, 1f), where + wide + new Vector2(3f, 1f), plate, 3f);
            draw.AddText(ImGui.GetFont(), font, where, said, line);
            below += wide.Y + 2f;
        }
    }

    /// <summary>Draws the name plate, and says where the next line down starts.</summary>
    /// <remarks>
    /// The rating rides ON THE PLATE, after the name, rather than on a line of its own. It is
    /// one or two characters and it is read together with the name - "is this worth running" is
    /// asked about a map, not about a row - so a second line would put an answer somewhere the
    /// question was not.
    ///
    /// TWO BORDERS, on purpose, and they say different things: the group is what somebody asked
    /// to be shown, the biome is what the map IS. So the group keeps the plate's own edge and
    /// the biome rings it from outside, clear of it - which is where the reference draws its
    /// biome border too. Sharing one edge would mean the setting somebody switched on quietly
    /// hides the fact underneath it.
    /// </remarks>
    private float DrawName(
        ImDrawListPtr draw, AtlasMark mark, string title, Vector2 middle, uint text, uint plate, float alpha)
    {
        float font = Font;
        Vector2 size = Measure(title, font);

        // The pill's width is taken BEFORE the plate is placed, so the whole thing stays
        // centred on the node. Measured and then added to the middle instead, the name would
        // shift left every time a map turned out to have a rating.
        // A pill on every map while a scale is in force, INCLUDING the ones nobody has rated.
        // "No pill" would otherwise mean two different things - the ratings are off, or this map
        // has not been judged - and the second is the one worth seeing: it is a map to go and
        // form an opinion about, and it looks identical to a map somebody deliberately skipped.
        string rated = mark.BestRating <= 0 ? string.Empty
            : mark.Rating is int worth ? worth.ToString(CultureInfo.InvariantCulture)
            : Unrated;

        Vector2 pillText = rated.Length > 0 ? Measure(rated, font) : Vector2.Zero;

        // AS WIDE AS THE WIDEST RATING rather than as wide as its own number, so every pill on
        // the atlas is the same shape. Hugging the text makes a "7" pill visibly smaller than a
        // "10" one, and a size that means nothing reads as though it means something.
        float inside = rated.Length > 0 ? MathF.Max(pillText.X, Measure(Widest, font).X) : 0f;

        // The gap is between the NAME and the pill's edge, not between the name and its number.
        // Counted from the number, the padding ate the gap and the pill sat against the last
        // letter - which is what it did.
        float pillWide = rated.Length > 0 ? PillGap + inside + (PillPad * 2f) : 0f;

        Vector2 at = middle - (new Vector2(size.X + pillWide, size.Y) * 0.5f);
        var pad = new Vector2(5f, 2f);
        Vector2 from = at - pad;
        Vector2 to = at + size + new Vector2(pillWide, 0f) + pad;

        if (AtlasBiomes.Of(mark.Biome) is { } biome)
        {
            // Outside the plate by the ring's own width, so the ring lies entirely clear of the
            // group's border rather than half on top of it. Rounded wider to match, or the
            // corners cut across the plate's.
            var beyond = new Vector2(BiomeRing, BiomeRing);
            draw.AddRect(
                from - beyond,
                to + beyond,
                OverlaySettings.Fade(biome.Colour, alpha),
                3f + BiomeRing,
                ImDrawFlags.RoundCornersAll,
                BiomeRing);
        }

        draw.AddRectFilled(from, to, plate, 3f);

        // A group's colour goes on the EDGE, not on the text. Group colours are chosen to be
        // told apart rather than to be read at ten pixels - as text half of them are illegible
        // on a dark plate, and as a border every one of them is obvious.
        if (mark.Group is { } group)
        {
            uint edge = OverlaySettings.Fade(OverlaySettings.ParseColour(group.Colour), alpha);
            if (edge != 0)
            {
                draw.AddRect(from, to, edge, 3f, ImDrawFlags.RoundCornersAll, 2f);
            }
        }

        draw.AddText(ImGui.GetFont(), font, at, text, title);

        if (rated.Length > 0)
        {
            DrawPill(
                draw,
                new Vector2(at.X + size.X + PillGap + PillPad, at.Y),
                inside,
                pillText,
                rated,
                mark,
                font,
                alpha);
        }

        return at.Y + size.Y + 3f;
    }

    /// <summary>How much room the pill leaves around its number, and before it.</summary>
    private const float PillPad = 4f;
    private const float PillGap = 7f;

    /// <summary>How thick the biome ring is, and how far outside the plate it sits.</summary>
    private const float BiomeRing = 2f;

    /// <summary>
    /// The widest rating a pill has to hold, for sizing rather than for showing.
    /// </summary>
    /// <remarks>
    /// Two digits, which is what a scale out of ten needs. Measured rather than assumed at a
    /// pixel count because the writing scales, and taken from digits rather than from the
    /// question mark because the digits are the wider of the two.
    /// </remarks>
    private const string Widest = "00";

    /// <summary>What a map nobody has judged carries instead of a number.</summary>
    /// <remarks>
    /// A question mark rather than a dash or a blank, because it asks the right thing: this is
    /// not a map rated nothing, it is one nobody has looked at - and the difference matters to
    /// whoever is filling the file in.
    /// </remarks>
    private const string Unrated = "?";

    /// <summary>
    /// The rating, as a filled pill from red at the worst to green at the best.
    /// </summary>
    /// <remarks>
    /// A FILLED shape rather than coloured text, because the colour is the whole message: a
    /// number is read one map at a time and a colour is read across the whole atlas at once,
    /// which is what somebody scanning for where to go next is actually doing. The number is
    /// there for when the answer matters rather than the impression.
    ///
    /// Black text on it, always. The ramp runs through yellow, and yellow is where white text
    /// stops being readable - so the one colour that works on all of it is the one that works
    /// on none of the plates, which is why this is the only text here that is not the plate's.
    /// </remarks>
    /// <param name="at">The top left of the pill's inside - its own padding goes outside this.</param>
    /// <param name="inside">
    /// How wide that inside is, which is the widest rating rather than this one. The number is
    /// centred in it, so a one-digit rating sits in the middle of a pill sized for two.
    /// </param>
    private void DrawPill(
        ImDrawListPtr draw,
        Vector2 at,
        float inside,
        Vector2 size,
        string rated,
        AtlasMark mark,
        float font,
        float alpha)
    {
        var pad = new Vector2(PillPad, 1f);
        Vector2 from = at - pad;
        Vector2 to = at + new Vector2(inside, size.Y) + pad;
        float round = (to.Y - from.Y) * 0.5f;
        var where = new Vector2(at.X + ((inside - size.X) * 0.5f), at.Y);

        // AN UNRATED MAP IS NOT A BAD ONE, so its pill is off the ramp entirely: a slate plate
        // with a pale question mark and an outline, rather than any shade of red. Put anywhere
        // on the scale it would be an opinion nobody holds - and at the red end it would be the
        // strongest one in the file.
        if (mark.Rating is not int worth)
        {
            draw.AddRectFilled(from, to, OverlaySettings.Fade(0xC0_2A2A32, alpha), round);
            draw.AddRect(
                from, to, OverlaySettings.Fade(0xFF_7A7A88, alpha), round,
                ImDrawFlags.RoundCornersAll, 1f);
            draw.AddText(ImGui.GetFont(), font, where, OverlaySettings.Fade(0xFF_C8C8D2, alpha), rated);
            return;
        }

        // Against the top of the scale the ratings themselves set, so any scale works: rate out
        // of five and five is green. Guarded, because a file where everything is nought would
        // otherwise divide by it.
        float share = mark.BestRating > 0 ? Math.Clamp((float)worth / mark.BestRating, 0f, 1f) : 0f;

        draw.AddRectFilled(from, to, Worth(share, alpha), round);
        draw.AddText(ImGui.GetFont(), font, where, OverlaySettings.Fade(0xFF10_1010, alpha), rated);
    }

    /// <summary>Red at nothing, amber in the middle, green at the top.</summary>
    /// <remarks>
    /// Through AMBER rather than straight from red to green, which is the difference between a
    /// scale somebody can read a middle value off and one where everything between the ends is
    /// a muddy brown. It is also the one ramp nobody has to be taught.
    /// </remarks>
    private static uint Worth(float share, float alpha)
    {
        (float red, float green) = share < 0.5f
            ? (1f, share * 2f * 0.75f)
            : (1f - ((share - 0.5f) * 2f * 0.85f), 0.75f + ((share - 0.5f) * 2f * 0.05f));

        uint colour = 0xFF00_0000
                      | ((uint)(0.15f * 255f) << 16)
                      | ((uint)(Math.Clamp(green, 0f, 1f) * 255f) << 8)
                      | (uint)(Math.Clamp(red, 0f, 1f) * 255f);

        return OverlaySettings.Fade(colour, alpha);
    }

    /// <summary>How far outside the screen a point still counts as being on it.</summary>
    /// <remarks>
    /// A margin rather than the exact edge, so a line whose far end is just past the border
    /// still anchors properly instead of popping in as the atlas is dragged.
    /// </remarks>
    private const float Margin = 64f;

    /// <summary>Whether a point is on the screen, give or take the margin.</summary>
    private static bool On(Vector2 at, Vector2 screen)
        => at.X >= -Margin && at.Y >= -Margin && at.X <= screen.X + Margin && at.Y <= screen.Y + Margin;

    /// <summary>
    /// Whether a segment is worth drawing at all - one end on the screen is enough.
    /// </summary>
    /// <remarks>
    /// A full atlas is a couple of thousand connections and most of them are somewhere else
    /// entirely, so this is what keeps the cost proportional to what is being looked at rather
    /// than to how much of the atlas has been revealed. One end is enough on purpose: a route
    /// that starts off-screen still has to be seen entering.
    /// </remarks>
    private static bool Worth(Vector2 from, Vector2 to, Vector2 screen)
        => On(from, screen) || On(to, screen);

    /// <summary>The size to write at, from the interface's own and the chosen scale.</summary>
    private float Font => ImGui.GetFontSize() * (TextScale > 0f ? TextScale : 1f);

    /// <summary>How big a line is at a size other than the interface's own.</summary>
    /// <remarks>
    /// Scaled from a measurement at the current size, which is what ImGui offers - measuring
    /// at an arbitrary size would mean pushing a font, and a bitmap font scales linearly.
    /// </remarks>
    private static Vector2 Measure(string text, float font)
        => ImGui.CalcTextSize(text) * (font / ImGui.GetFontSize());

    /// <summary>A map's colour: its group's, or the plain label colour when it has no group.</summary>
    private uint ColourOf(AtlasMark mark, float alpha)
    {
        uint chosen = mark.Group is { } group ? OverlaySettings.ParseColour(group.Colour) : 0;
        return OverlaySettings.Fade(
            chosen == 0 ? Style.Colour(StyleCatalogue.Keys.AtlasLabel) : chosen,
            alpha);
    }
}
