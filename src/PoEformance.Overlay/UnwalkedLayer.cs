using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Marks the ground that can still be reached and has not been walked past.
/// </summary>
/// <remarks>
/// The useful half of measuring coverage. "Forty per cent left" and "left is over THERE" are
/// different amounts of help, and only the second one gets somebody to the end of a map.
///
/// SAMPLED AT A SCREEN SPACING, not per cell. A coarse cell is four terrain cells across,
/// which on the large map is well under a pixel - drawing one mark each would be seventy
/// thousand sub-pixel rectangles a frame to produce a smear. So the stride is chosen from the
/// map's own zoom to land a mark every few pixels, which costs a few hundred and actually
/// reads as a texture.
///
/// LARGE MAP ONLY. The minimap is a few hundred pixels across with the player at its centre,
/// and covering it in marks would bury the thing it is for.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UnwalkedLayer
{
    /// <summary>Roughly how many pixels apart the marks should land.</summary>
    private const float SpacingPx = 7f;

    /// <summary>
    /// Most marks drawn in one frame.
    /// </summary>
    /// <remarks>
    /// A backstop, not a budget. The spacing already bounds the count at any sane zoom; this
    /// is here because the zoom comes from the game and a bad read would otherwise ask for a
    /// mark per cell across a whole map, on the render thread.
    /// </remarks>
    private const int MostMarks = 6_000;

    /// <summary>Whether the marks are drawn at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Draws a mark on each sampled patch of ground still to be walked.</summary>
    public void Draw(ImDrawListPtr draw, MapView map, MapCoverage coverage, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(player);

        if (!Enabled || !map.IsLargeMap || !coverage.RegionKnown
            || !Style.Visible(StyleCatalogue.Keys.Unwalked))
        {
            return;
        }

        int stride = StrideFor(map);
        uint colour = Style.Colour(StyleCatalogue.Keys.Unwalked);
        float size = Style.Sized(StyleCatalogue.Keys.Unwalked, 1.6f);

        // The player's ground height for every mark. They sit on unseen ground whose height
        // is not worth a lookup each - being a few pixels out on a hint about where to go is
        // not a mistake anybody can make use of.
        float height = player.TerrainHeight;
        int drawn = 0;

        for (int y = 0; y < coverage.CoarseHeight && drawn < MostMarks; y += stride)
        {
            for (int x = 0; x < coverage.CoarseWidth; x += stride)
            {
                if (!coverage.StillToWalk(x, y))
                {
                    continue;
                }

                Vector2 at = map.Project(
                    x * MapCoverage.CoarseStep * MapView.WorldToGrid,
                    y * MapCoverage.CoarseStep * MapView.WorldToGrid,
                    height,
                    player.WorldX,
                    player.WorldY,
                    player.TerrainHeight);

                if (!map.Contains(at))
                {
                    continue;
                }

                draw.AddRectFilled(at - new Vector2(size, size), at + new Vector2(size, size), colour);

                if (++drawn >= MostMarks)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// How many coarse cells to skip so the marks land about <see cref="SpacingPx"/> apart.
    /// </summary>
    /// <remarks>
    /// From the map's own zoom, so zooming out thins the marks instead of packing them into a
    /// solid block, and zooming in spreads them out rather than leaving gaps.
    /// </remarks>
    private static int StrideFor(MapView map)
    {
        // What one coarse cell measures on screen. A coarse cell is four grid cells.
        float perCell = map.PixelsPerGridCell * MapCoverage.CoarseStep;
        if (perCell <= 0.0001f)
        {
            return 16;   // an unreadable zoom; draw sparsely rather than not at all
        }

        return Math.Clamp((int)MathF.Ceiling(SpacingPx / perCell), 1, 64);
    }
}
