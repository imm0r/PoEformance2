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
                if (mark.Route.Count >= 2)
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
    /// With the marker on the end NEAREST YOU rather than on the destination. The destination
    /// is already named and coloured; what a route is actually asked is "which of the maps I
    /// can enter now does this start from", and that end is otherwise just where a line stops.
    /// </remarks>
    private void DrawRoute(ImDrawListPtr draw, AtlasMark mark, Vector2 screen)
    {
        uint colour = ColourOf(mark, 0.55f);
        float width = Style.Width(StyleCatalogue.Keys.AtlasRoute, 4f);

        for (int i = 1; i < mark.Route.Count; i++)
        {
            if (Worth(mark.Route[i - 1], mark.Route[i], screen))
            {
                draw.AddLine(mark.Route[i - 1], mark.Route[i], colour, width);
            }
        }

        if (!Style.Visible(StyleCatalogue.Keys.AtlasEntry) || !On(mark.Route[0], screen))
        {
            return;
        }

        float radius = Style.Sized(StyleCatalogue.Keys.AtlasEntry, MathF.Max(3f, width * 1.3f));
        draw.AddCircleFilled(mark.Route[0], radius, Style.Colour(StyleCatalogue.Keys.AtlasEntry));
        draw.AddCircle(mark.Route[0], radius, 0xFF00_0000, 0, MathF.Max(1f, radius * 0.35f));
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
    private float DrawName(
        ImDrawListPtr draw, AtlasMark mark, string title, Vector2 middle, uint text, uint plate, float alpha)
    {
        float font = Font;
        Vector2 size = Measure(title, font);
        Vector2 at = middle - (size * 0.5f);
        var pad = new Vector2(5f, 2f);

        draw.AddRectFilled(at - pad, at + size + pad, plate, 3f);

        // A group's colour goes on the EDGE, not on the text. Group colours are chosen to be
        // told apart rather than to be read at ten pixels - as text half of them are illegible
        // on a dark plate, and as a border every one of them is obvious.
        if (mark.Group is { } group)
        {
            uint edge = OverlaySettings.Fade(OverlaySettings.ParseColour(group.Colour), alpha);
            if (edge != 0)
            {
                draw.AddRect(at - pad, at + size + pad, edge, 3f, ImDrawFlags.RoundCornersAll, 2f);
            }
        }

        draw.AddText(ImGui.GetFont(), font, at, text, title);
        return at.Y + size.Y + 3f;
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
