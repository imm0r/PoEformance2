using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Where to draw something whose real place is off the edge of the map.
/// </summary>
/// <remarks>
/// ONE ANSWER FOR EVERY LAYER. The landmark markers and the entity dots both project a world
/// position onto a map that is smaller than the world, and both used to drop whatever missed.
/// Two copies of "pin it to the frame instead" would be two copies of the decision about how
/// much smaller a pinned marker is drawn and how much of its opacity it keeps - which is the
/// kind of thing that ends up different in each, so that a chest at the edge and a monster at
/// the edge look like two unrelated features.
///
/// WHY A PINNED MARKER IS DIMMED AND SHRUNK. It is the same marker saying a different thing:
/// not "this is here" but "this is that way, past the edge". Drawn identically it claims a
/// position it does not have, and somebody walks to the frame rather than past it. Dimmer and
/// smaller is that difference said without a second set of icons to learn.
/// </remarks>
internal static class MapEdge
{
    /// <summary>How much of its opacity a marker keeps once it is pinned to the frame.</summary>
    /// <remarks>
    /// Multiplied with whatever fade the caller already had, so a REMEMBERED thing pinned to
    /// the edge is fainter than either - which is right: both of the things it is unsure about
    /// are unsure at once.
    /// </remarks>
    public const float PinnedAlpha = 0.7f;

    /// <summary>How much of its size a pinned marker keeps, before the style's own scale.</summary>
    private const float PinnedSize = 0.8f;

    /// <summary>Which switch governs edge indicators on this map.</summary>
    public static string KeyFor(MapView map) => StyleCatalogue.EdgeKey(map.IsLargeMap);

    /// <summary>
    /// Where to draw a projected point, and how - or null when it should not be drawn at all.
    /// </summary>
    /// <remarks>
    /// The ordinary case is first and costs one rectangle test: a point on the map is returned
    /// as it came, at its own size and its own opacity. Everything below that runs only for the
    /// things that missed.
    /// </remarks>
    public static (Vector2 At, float Size, float Fade)? Place(
        OverlayStyle style, MapView map, Vector2 at, float size, float fade = 1f)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (map.Contains(at))
        {
            return (at, size, fade);
        }

        string key = KeyFor(map);
        if (!style.Visible(key))
        {
            return null;
        }

        // Sized before it is pinned, because the inset has to be the room the marker will
        // actually need - inset by the full size, a shrunk marker floats away from the frame.
        float pinned = style.Sized(key, size * PinnedSize);
        if (map.EdgeFor(at, pinned) is not Vector2 edge)
        {
            return null;
        }

        return (edge, pinned, fade * PinnedAlpha);
    }
}
