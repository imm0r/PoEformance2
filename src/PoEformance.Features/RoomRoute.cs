using PoEformance.Game.Files;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// A route to a room the tile book outlines on the large map: where it ends, and where arriving counts.
/// </summary>
/// <remarks>
/// ARRIVING IS STANDING ON ONE OF ITS TILES - the room's slots that are not "n", or its whole
/// footprint where its file is not to hand - which is what "go to that room" means. See
/// <see cref="RouteZone"/>.
///
/// SO THE LINE MUST END INSIDE IT. The pathfinder moves an end that is not on walkable ground to
/// the nearest ground it finds, up to eighty cells away, and from a room's middle that is as
/// likely to be the corridor outside as the floor inside: a route ending there would be walked to
/// its end and never arrive. So the end is chosen here, on the room's own walkable ground - the
/// walkable cell nearest the footprint's middle, in the walkable tile of the room nearest it.
/// </remarks>
public static class RoomRoute
{
    /// <summary>
    /// The identity of a route to a room laid at a place - the same room at the same place is the same route, so a second click drops it.
    /// </summary>
    /// <remarks>
    /// TerrainRooms' identity for the place with a second bit set, so it never names the route
    /// the map's own room names keep to a room laid there: those end at the room's middle and
    /// are arrived at by the radius, and one click must not drop the other.
    /// </remarks>
    public static ulong IdFor(string room, RoomCandidate where)
    {
        ArgumentNullException.ThrowIfNull(room);
        return TerrainRooms.IdFor(room, where.X, where.Y) ^ 0x4000_0000_0000_0000UL;
    }

    /// <summary>The route to a room laid at a place, or null where none of its tiles has ground to stand on.</summary>
    /// <param name="grid">The area.</param>
    /// <param name="room">The room's file, for its identity.</param>
    /// <param name="layout">The room's file read, for which slots it leaves empty - or null for its whole footprint.</param>
    /// <param name="where">Where it is laid.</param>
    public static RouteTarget? For(TerrainGrid grid, string room, RoomLayout? layout, RoomCandidate where)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int[] tiles = RoomArrangement.Tiles(layout, where, grid.TilesX, grid.TilesY);
        if (tiles.Length == 0)
        {
            return null;
        }

        const int Cells = TerrainGrid.CellsPerTile;
        float middleX = (where.X + (where.Width * 0.5f)) * Cells;
        float middleY = (where.Y + (where.Height * 0.5f)) * Cells;
        bool[] walkable = grid.WalkableTileMask();

        // The walkable tile of the room nearest the middle, by the middle of each tile.
        int best = -1;
        float nearest = float.MaxValue;
        foreach (int tile in tiles)
        {
            if (tile >= walkable.Length || !walkable[tile])
            {
                continue;
            }

            float dx = (((tile % grid.TilesX) + 0.5f) * Cells) - middleX;
            float dy = (((tile / grid.TilesX) + 0.5f) * Cells) - middleY;
            float distance = (dx * dx) + (dy * dy);
            if (distance < nearest)
            {
                nearest = distance;
                best = tile;
            }
        }

        if (best < 0)
        {
            return null;
        }

        // Its walkable cell nearest the middle - the mask says one is there.
        int left = (best % grid.TilesX) * Cells;
        int top = (best / grid.TilesX) * Cells;
        (int X, int Y) end = (left, top);
        nearest = float.MaxValue;
        for (int y = top; y < top + Cells; y++)
        {
            for (int x = left; x < left + Cells; x++)
            {
                float dx = x + 0.5f - middleX;
                float dy = y + 0.5f - middleY;
                float distance = (dx * dx) + (dy * dy);
                if (distance < nearest && grid.IsWalkable(x, y))
                {
                    nearest = distance;
                    end = (x, y);
                }
            }
        }

        return new RouteTarget(IdFor(room, where), (end.X + 0.5f) * MapView.WorldToGrid, (end.Y + 0.5f) * MapView.WorldToGrid)
        {
            Zone = new RouteZone(grid.TilesX, tiles),
            Name = TerrainRooms.NameFor(room),
        };
    }
}
