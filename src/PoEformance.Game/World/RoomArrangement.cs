using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>One room of an area given a place: the candidate it took, how far down its own list that was, and how much of it agrees that no join explains.</summary>
/// <param name="Room">The room's file.</param>
/// <param name="Where">The place.</param>
/// <param name="Rank">Its place in the room's own list - nought for the search's first choice.</param>
/// <param name="Beside">How much of it agrees that no join explains, as a whole percentage - see <see cref="RoomArrangement.Beside"/>.</param>
public sealed record RoomLaid(string Room, RoomCandidate Where, int Rank, int Beside);

/// <summary>
/// Every room an area loaded, each at the best place its search found that no surer room already holds.
/// </summary>
/// <param name="Laid">The rooms given a place, surest first.</param>
/// <param name="Crowded">The rooms whose every candidate covers a tile a surer room holds.</param>
/// <param name="Unfound">The rooms whose search found no candidate at all, or did not run.</param>
/// <remarks>
/// WHY "NO SURER ROOM ALREADY HOLDS". Two rooms of an area never share a tile - each tile is laid by
/// one room or by the map around them - but two searches can each rank the same spot first: the
/// office rooms of seepage are one module told apart by a slot or two, and each fits where the
/// other lies nearly as well as where it lies itself. Taken at face value, every such pair is drawn
/// on top of itself. So the rooms are placed one at a time, the one whose first choice agrees most
/// first, and each takes the first place on its own list that covers no tile already taken. That is
/// a rule chosen, not one the game states: the first choice being right is the working assumption
/// it was asked under, and a room pushed down its list says so by its rank.
///
/// A ROOM COVERS THE TILES OF ITS SLOTS that are not "n" - the slots the file leaves empty, where the
/// map or another room may lie - each at the cell the placement puts it on (RoomFinder.CellOf), the
/// same cell the tile check reads. The footprint's rectangle would be simpler and would make an
/// L-shaped room collide with whatever fills its notch.
/// </remarks>
public sealed record RoomArrangement(IReadOnlyList<RoomLaid> Laid, IReadOnlyList<string> Crowded, IReadOnlyList<string> Unfound)
{
    /// <summary>Nothing arranged.</summary>
    public static RoomArrangement None { get; } = new([], [], []);

    /// <summary>
    /// How much of a candidate's corners and tiles agree that no join explains, as a whole percentage rounded down - 100 where every miss is a join's.
    /// </summary>
    /// <remarks>The tile book's "beside them" figure, here so the arrangement ranks by the number a person reads.</remarks>
    public static int Beside(RoomCandidate where, RoomMisses misses)
    {
        ArgumentNullException.ThrowIfNull(misses);
        int beside = where.Corners + where.Tiles - misses.Openings.Count - misses.Caps.Count - misses.JoinCorners.Count;
        return beside > 0 ? (beside - misses.Elsewhere) * 100 / beside : 100;
    }

    /// <summary>
    /// The rooms placed surest first, each at the first candidate on its list covering no tile a surer room holds - see the remarks.
    /// </summary>
    /// <param name="rooms">Every room with its file read and its search run.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    public static RoomArrangement Arrange(IReadOnlyList<(string Room, RoomLayout Layout, RoomSearch Search)> rooms, int tilesX, int tilesY)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (tilesX <= 0 || tilesY <= 0)
        {
            return None;
        }

        var unfound = new List<string>();
        var ranked = new List<(string Room, RoomLayout Layout, RoomSearch Search, (int Beside, double Tiles, double Corners) Key)>(rooms.Count);
        foreach ((string room, RoomLayout layout, RoomSearch search) in rooms)
        {
            if (search.Why.Length > 0 || search.Candidates.Count == 0 || !layout.Ready)
            {
                unfound.Add(room);
                continue;
            }

            ranked.Add((room, layout, search, Key(search, 0)));
        }

        // SUREST FIRST: the share no join explains, then the tiles, then the corners - the order a
        // person reads a row in - and the name last, so the same area always arranges the same way.
        ranked.Sort((a, b) =>
        {
            int order = b.Key.Beside.CompareTo(a.Key.Beside);
            order = order != 0 ? order : b.Key.Tiles.CompareTo(a.Key.Tiles);
            order = order != 0 ? order : b.Key.Corners.CompareTo(a.Key.Corners);
            return order != 0 ? order : string.CompareOrdinal(a.Room, b.Room);
        });

        var taken = new bool[tilesX * tilesY];
        var laid = new List<RoomLaid>(ranked.Count);
        var crowded = new List<string>();
        var cells = new List<int>();
        foreach ((string room, RoomLayout layout, RoomSearch search, _) in ranked)
        {
            var placed = false;
            for (var rank = 0; rank < search.Candidates.Count && !placed; rank++)
            {
                RoomCandidate where = search.Candidates[rank];
                Covered(layout, where, tilesX, tilesY, cells);
                if (cells.Exists(cell => taken[cell]))
                {
                    continue;
                }

                foreach (int cell in cells)
                {
                    taken[cell] = true;
                }

                laid.Add(new RoomLaid(room, where, rank, Key(search, rank).Beside));
                placed = true;
            }

            if (!placed)
            {
                crowded.Add(room);
            }
        }

        return new RoomArrangement(laid, crowded, unfound);
    }

    /// <summary>A candidate's figures, best first: the share no join explains, the tiles agreeing, the corners agreeing.</summary>
    private static (int Beside, double Tiles, double Corners) Key(RoomSearch search, int rank)
    {
        RoomCandidate where = search.Candidates[rank];
        RoomMisses misses = rank < search.Misses.Count ? search.Misses[rank] : RoomMisses.None;
        int beside = misses.Classified ? Beside(where, misses) : 0;
        double tiles = where.Tiles > 0 ? (double)where.TilesAgree / where.Tiles : 0d;
        double corners = where.Corners > 0 ? (double)where.Matched / where.Corners : 0d;
        return (beside, tiles, corners);
    }

    /// <summary>The area tiles a room covers laid at a candidate, by row-major index, into <paramref name="cells"/> - its slots that are not "n", those off the area left out.</summary>
    internal static void Covered(RoomLayout layout, RoomCandidate where, int tilesX, int tilesY, List<int> cells)
    {
        cells.Clear();
        for (var line = 0; line < layout.Height; line++)
        {
            for (var column = 0; column < layout.Width; column++)
            {
                if (layout.SlotAt(column, line).Kind == 'n')
                {
                    continue;
                }

                (int x, int y) = RoomFinder.CellOf(column, line, layout.Width, layout.Height, where.Turn);
                x += where.X;
                y += where.Y;
                if ((uint)x < (uint)tilesX && (uint)y < (uint)tilesY)
                {
                    cells.Add((y * tilesX) + x);
                }
            }
        }
    }
}
