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

        if (Style.Visible(StyleCatalogue.Keys.AtlasWeb))
        {
            uint web = Style.Colour(StyleCatalogue.Keys.AtlasWeb);
            float thin = Style.Width(StyleCatalogue.Keys.AtlasWeb, 1.5f);
            foreach ((Vector2 from, Vector2 to) in view.Web)
            {
                draw.AddLine(from, to, web, thin);
            }
        }

        if (Style.Visible(StyleCatalogue.Keys.AtlasRoute))
        {
            foreach (AtlasMark mark in view.Marks)
            {
                if (mark.Route.Count >= 2)
                {
                    DrawRoute(draw, mark);
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
    private void DrawRoute(ImDrawListPtr draw, AtlasMark mark)
    {
        uint colour = ColourOf(mark, 0.55f);
        float width = Style.Width(StyleCatalogue.Keys.AtlasRoute, 4f);

        for (int i = 1; i < mark.Route.Count; i++)
        {
            draw.AddLine(mark.Route[i - 1], mark.Route[i], colour, width);
        }

        if (!Style.Visible(StyleCatalogue.Keys.AtlasEntry))
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

        // How far away it is, on the LABEL rather than beside the line. A route crosses other
        // maps, so a number floating on it belongs to whichever one it happens to be over.
        string title = mark.Hops > 0 ? $"{mark.Name} ({mark.Hops})" : mark.Name;

        Vector2 size = ImGui.CalcTextSize(title);
        Vector2 at = mark.Where + Nudge - (size * 0.5f);
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

        draw.AddText(at, text, title);

        if (mark.Contents.Count == 0 || !Style.Visible(StyleCatalogue.Keys.AtlasContent))
        {
            return;
        }

        uint said = OverlaySettings.Fade(Style.Colour(StyleCatalogue.Keys.AtlasContent), alpha);
        float below = at.Y + size.Y + 3f;

        foreach (string line in mark.Contents)
        {
            Vector2 wide = ImGui.CalcTextSize(line);
            var where = new Vector2(mark.Where.X + Nudge.X - (wide.X * 0.5f), below);

            draw.AddRectFilled(where - new Vector2(3f, 1f), where + wide + new Vector2(3f, 1f), plate, 3f);
            draw.AddText(where, said, line);
            below += wide.Y + 2f;
        }
    }

    /// <summary>A map's colour: its group's, or the plain label colour when it has no group.</summary>
    private uint ColourOf(AtlasMark mark, float alpha)
    {
        uint chosen = mark.Group is { } group ? OverlaySettings.ParseColour(group.Colour) : 0;
        return OverlaySettings.Fade(
            chosen == 0 ? Style.Colour(StyleCatalogue.Keys.AtlasLabel) : chosen,
            alpha);
    }
}
