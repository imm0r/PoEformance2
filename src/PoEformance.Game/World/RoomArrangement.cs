using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>One room of an area given a place: the candidate it took, how far down its own list that was, and how much of it agrees that no join explains.</summary>
/// <param name="Room">The room's file.</param>
/// <param name="Where">The place.</param>
/// <param name="Rank">Its place in the room's own list - nought for the search's first choice.</param>
/// <param name="Beside">How much of it agrees that no join explains, as a whole percentage - see <see cref="RoomArrangement.Beside"/>.</param>
/// <param name="Layout">The room's file read, for the tiles it covers.</param>
public sealed record RoomLaid(string Room, RoomCandidate Where, int Rank, int Beside, RoomLayout Layout);

/// <summary>Which tiles two rooms of an area may both hold - see <see cref="RoomArrangement"/>.</summary>
public enum RoomOverlap
{
    /// <summary>Rim on rim: a tile on the outermost row or column of two rooms' footprints may be both rooms'; one inside a footprint is that room's alone.</summary>
    Rims,

    /// <summary>None at all: every tile a room's slots cover is that room's alone.</summary>
    None,
}

/// <summary>How a placement and a room laid share tiles - see <see cref="RoomArrangement.Sharing"/>.</summary>
/// <param name="Laid">The room laid.</param>
/// <param name="Rims">The tiles both hold that lie on both rims.</param>
/// <param name="Elsewhere">The tiles both hold that lie inside either footprint.</param>
/// <param name="Touching">The placement's tiles that are not the room's but lie beside one of its tiles, across a side.</param>
public sealed record RoomShared(RoomLaid Laid, int Rims, int Elsewhere, int Touching);

/// <summary>
/// Every room an area loaded, each at the best place its search found that no surer room already holds.
/// </summary>
/// <param name="Laid">The rooms given a place, surest first.</param>
/// <param name="Crowded">The rooms whose every candidate covers a tile a surer room holds.</param>
/// <param name="Unfound">The rooms whose search found no candidate at all, or did not run.</param>
/// <remarks>
/// WHY "NO SURER ROOM ALREADY HOLDS". Two searches can each rank the same spot first: the office rooms
/// of seepage are one module told apart by a slot or two, and each fits where the other lies nearly as
/// well as where it lies itself. Taken at face value, every such pair is drawn on top of itself. So the
/// rooms are placed one at a time, the one whose first choice agrees most first, and each takes the
/// first place on its own list that covers no tile already taken. That is a rule chosen, not one the
/// game states: the first choice being right is the working assumption it was asked under, and a room
/// pushed down its list says so by its rank.
///
/// BUT ROOMS MEET AT THEIR RIMS WHERE THEY JOIN - which is why "taken" depends on the rule. The first
/// version let no tile be two rooms', and in seepage it pushed 2x2_offices_01 off the first row of its
/// own list, though that row is where the game laid it - and that row's join corners coincide on the
/// map with those of the boss room, which was placed right. So under <see cref="RoomOverlap.Rims"/> a
/// tile on the outermost row or column of a footprint may be held by any number of rooms' rims, and a
/// tile inside a footprint by that room alone, rim or not. The rim is the placed rectangle's, the same
/// rim RoomPlacements sorts joins on. <see cref="RoomOverlap.None"/> is the first version, kept beside
/// it to compare. Whether a join is one row both rims lie on or two rows side by side, and which room
/// it was that held the offices' tiles, is what <see cref="Sharing"/> was added to tell.
///
/// A ROOM COVERS THE TILES OF ITS SLOTS that are not "n" - the slots the file leaves empty, where the
/// map or another room may lie - each at the cell the placement puts it on (RoomFinder.CellOf), the
/// same cell the tile check reads. The footprint's rectangle would be simpler and would make an
/// L-shaped room collide with whatever fills its notch.
/// </remarks>
public sealed record RoomArrangement(IReadOnlyList<RoomLaid> Laid, IReadOnlyList<string> Crowded, IReadOnlyList<string> Unfound)
{
    /// <summary>A tile no room holds.</summary>
    private const byte Free = 0;

    /// <summary>A tile only rooms' rims hold.</summary>
    private const byte OnRim = 1;

    /// <summary>A tile inside a room's footprint.</summary>
    private const byte Inside = 2;

    /// <summary>Nothing arranged.</summary>
    public static RoomArrangement None { get; } = new([], [], []);

    /// <summary>The area's tiles across, as arranged.</summary>
    public int TilesX { get; init; }

    /// <summary>The area's tiles down, as arranged.</summary>
    public int TilesY { get; init; }

    /// <summary>The rule the rooms were placed by.</summary>
    public RoomOverlap Rule { get; init; }

    /// <summary>Every room that read and was found, by file, whether laid or crowded out - for <see cref="Sharing"/> with a room picked elsewhere.</summary>
    public IReadOnlyDictionary<string, RoomLayout> Layouts { get; init; } = new Dictionary<string, RoomLayout>();

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
    /// The rooms placed surest first, each at the first candidate on its list covering no tile a surer room holds under <paramref name="rule"/> - see the remarks.
    /// </summary>
    /// <param name="rooms">Every room with its file read and its search run.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    /// <param name="rule">Which tiles two rooms may both hold.</param>
    public static RoomArrangement Arrange(
        IReadOnlyList<(string Room, RoomLayout Layout, RoomSearch Search)> rooms, int tilesX, int tilesY, RoomOverlap rule)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (tilesX <= 0 || tilesY <= 0)
        {
            return None;
        }

        var unfound = new List<string>();
        var layouts = new Dictionary<string, RoomLayout>(StringComparer.OrdinalIgnoreCase);
        var ranked = new List<(string Room, RoomLayout Layout, RoomSearch Search, (int Beside, double Tiles, double Corners) Key)>(rooms.Count);
        foreach ((string room, RoomLayout layout, RoomSearch search) in rooms)
        {
            if (search.Why.Length > 0 || search.Candidates.Count == 0 || !layout.Ready)
            {
                unfound.Add(room);
                continue;
            }

            layouts[room] = layout;
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

        var held = new byte[tilesX * tilesY];
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
                if (Collides(cells, where, tilesX, held, rule))
                {
                    continue;
                }

                foreach (int cell in cells)
                {
                    if (!Rim(where, cell % tilesX, cell / tilesX))
                    {
                        held[cell] = Inside;
                    }
                    else if (held[cell] == Free)
                    {
                        held[cell] = OnRim;
                    }
                }

                laid.Add(new RoomLaid(room, where, rank, Key(search, rank).Beside, layout));
                placed = true;
            }

            if (!placed)
            {
                crowded.Add(room);
            }
        }

        return new RoomArrangement(laid, crowded, unfound) { TilesX = tilesX, TilesY = tilesY, Rule = rule, Layouts = layouts };
    }

    /// <summary>
    /// How a room laid at a place shares tiles with each room of this arrangement - the rooms it shares or touches any, most shared first, <paramref name="except"/> left out.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES OF THE QUESTION THE RULE RESTS ON. Two rooms that join either lay their rims on the
    /// same row - the rim tiles shared, nothing inside - or side by side, sharing none and touching
    /// along the join. Held against a room known to be right, the counts say which.
    /// </remarks>
    /// <param name="layout">The room's file read.</param>
    /// <param name="where">Its place.</param>
    /// <param name="except">A room to leave out, by file - the same room as arranged.</param>
    public IReadOnlyList<RoomShared> Sharing(RoomLayout layout, RoomCandidate where, string? except = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (TilesX <= 0 || TilesY <= 0)
        {
            return [];
        }

        var mine = new List<int>();
        Covered(layout, where, TilesX, TilesY, mine);
        var theirs = new List<int>();
        var marks = new byte[TilesX * TilesY];
        var shared = new List<RoomShared>();
        foreach (RoomLaid other in Laid)
        {
            if (except is not null && string.Equals(other.Room, except, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Covered(other.Layout, other.Where, TilesX, TilesY, theirs);
            foreach (int cell in theirs)
            {
                marks[cell] = Rim(other.Where, cell % TilesX, cell / TilesX) ? OnRim : Inside;
            }

            int rims = 0, elsewhere = 0, touching = 0;
            foreach (int cell in mine)
            {
                int x = cell % TilesX;
                int y = cell / TilesX;
                if (marks[cell] != Free)
                {
                    if (marks[cell] == OnRim && Rim(where, x, y))
                    {
                        rims++;
                    }
                    else
                    {
                        elsewhere++;
                    }
                }
                else if ((x > 0 && marks[cell - 1] != Free) || (x < TilesX - 1 && marks[cell + 1] != Free)
                    || (y > 0 && marks[cell - TilesX] != Free) || (y < TilesY - 1 && marks[cell + TilesX] != Free))
                {
                    touching++;
                }
            }

            foreach (int cell in theirs)
            {
                marks[cell] = Free;
            }

            if (rims + elsewhere + touching > 0)
            {
                shared.Add(new RoomShared(other, rims, elsewhere, touching));
            }
        }

        shared.Sort((a, b) =>
        {
            int order = (b.Rims + b.Elsewhere).CompareTo(a.Rims + a.Elsewhere);
            return order != 0 ? order : b.Touching.CompareTo(a.Touching);
        });
        return shared;
    }

    /// <summary>Whether a placement's tiles meet any a surer room holds, under the rule.</summary>
    private static bool Collides(List<int> cells, RoomCandidate where, int tilesX, byte[] held, RoomOverlap rule)
    {
        foreach (int cell in cells)
        {
            byte there = held[cell];
            if (there != Free && (rule == RoomOverlap.None || there == Inside || !Rim(where, cell % tilesX, cell / tilesX)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an area tile lies on the outermost row or column of a placement's footprint - the rim RoomPlacements sorts joins on.</summary>
    private static bool Rim(RoomCandidate where, int x, int y)
        => x == where.X || x == where.X + where.Width - 1 || y == where.Y || y == where.Y + where.Height - 1;

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

    /// <summary>
    /// The area tiles a room covers laid at a candidate, by row-major index - its slots that are not "n", or the whole footprint where its file is not to hand.
    /// </summary>
    public static int[] Tiles(RoomLayout? layout, RoomCandidate where, int tilesX, int tilesY)
    {
        var cells = new List<int>(where.Width * where.Height);
        if (layout is { Ready: true })
        {
            Covered(layout, where, tilesX, tilesY, cells);
            return [.. cells];
        }

        for (int y = Math.Max(0, where.Y); y < Math.Min(tilesY, where.Y + where.Height); y++)
        {
            for (int x = Math.Max(0, where.X); x < Math.Min(tilesX, where.X + where.Width); x++)
            {
                cells.Add((y * tilesX) + x);
            }
        }

        return [.. cells];
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
