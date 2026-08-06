namespace PoEformance.Game.World;

/// <summary>What came of asking for a route.</summary>
/// <remarks>
/// Only <see cref="Unreachable"/> says the place cannot be walked to. The others say the
/// question was refused, and each for a reason the user can act on - which is the difference
/// between "the tool cannot do this" and "the game will not let you".
/// </remarks>
public enum RouteOutcome
{
    /// <summary>A route was found.</summary>
    Found,

    /// <summary>The player is not on walkable ground and none is near - normally a loading area.</summary>
    NoGroundNearStart,

    /// <summary>Nothing walkable near the destination. The marker is on scenery, not on floor.</summary>
    NoGroundNearEnd,

    /// <summary>Further apart than any search will attempt.</summary>
    TooFar,

    /// <summary>The search ran out of time or steps. Says nothing about whether a way exists.</summary>
    GaveUp,

    /// <summary>Everything reachable was searched. There is genuinely no way there.</summary>
    Unreachable,
}

/// <summary>
/// The shortest walkable route between two points of an area, in grid cells.
/// </summary>
/// <remarks>
/// A* over the same walkable grid the map outline is drawn from, so a route can never cross
/// a wall the map shows. Ported from GameHelper2's Radar pathfinder, including the two parts
/// that are easy to leave out and wrong without:
///
/// - DIAGONAL moves are refused unless both cardinal neighbours are open, or the route cuts
///   the corner of a wall and reads as walking through it;
/// - the result is SMOOTHED by line of sight afterwards, because raw A* on a grid produces a
///   staircase of single-cell steps that no player would walk and that is unreadable at map
///   scale.
///
/// Pure: it takes a grid and two points and returns a list. No memory reads, no drawing, so
/// the behaviour above is testable without a game.
/// </remarks>
public static class TerrainPathfinder
{
    /// <summary>Nodes expanded before giving up - a disconnected target would search forever.</summary>
    public const int DefaultMaxIterations = 20_000_000;

    /// <summary>
    /// How long one search may run before it gives up.
    /// </summary>
    /// <remarks>
    /// The real guard, and it replaces a cap on iterations as the thing that actually bounds
    /// the work. Measured on a 2415x2829 map: a route right across it takes about 1.8 seconds,
    /// and a target that CANNOT be reached takes fourteen - because saying "there is no way"
    /// means exhausting everything reachable first. A count of nodes does not express that
    /// difference in any units anybody cares about; a clock does.
    /// </remarks>
    public const int DefaultMaxMilliseconds = 4_000;

    /// <summary>
    /// Straight-line cells beyond which no search is attempted.
    /// </summary>
    /// <remarks>
    /// Only a guard against an absurd question, and deliberately larger than any real map's
    /// diagonal. It used to be 2500 with the reasoning that a route that long is not what
    /// anybody is looking at - which turned out to be exactly wrong. A map's boss arena sits
    /// at the far end of it, and that is the one destination people most want a line to.
    /// Bounding the TIME does the job this was doing, and does it without a view about which
    /// destinations are worth having.
    /// </remarks>
    public const int DefaultMaxDistance = 8_000;

    /// <summary>
    /// How far a route may climb or drop between two neighbouring cells, in world height.
    /// </summary>
    /// <remarks>
    /// THE MULTI-LEVEL PROBLEM. The walkable grid is FLAT: a bridge and the ground beneath it
    /// occupy the same cells and both read as walkable, so a two-dimensional search steps
    /// happily between storeys and draws a route that walks through the floor. Nothing in the
    /// walkable data can say otherwise - the only thing separating the two is their height.
    ///
    /// Thirty units per cell, taken from the AutoHotkey tool where it is in daily use rather
    /// than reasoned out here. Wide enough that a staircase, a ramp and ordinary uneven ground
    /// all pass; narrow enough that a storey seam does not. Diagonals get the same allowance
    /// rather than a scaled one, which errs toward permitting - being too strict costs a route
    /// that does exist, and that is the worse failure of the two.
    ///
    /// Only applied where the area HAS heights. Without them every cell reads as zero, every
    /// step passes, and the search behaves exactly as it did before this existed.
    /// </remarks>
    public const float MaxClimbPerCell = 30f;

    private static readonly (int Dx, int Dy, float Cost)[] Neighbours =
    [
        (0, -1, 1f),
        (1, -1, 1.41421356f),
        (1, 0, 1f),
        (1, 1, 1.41421356f),
        (0, 1, 1f),
        (-1, 1, 1.41421356f),
        (-1, 0, 1f),
        (-1, -1, 1.41421356f),
    ];

    /// <summary>
    /// Finds a route from start to end, or an empty list when there is none.
    /// </summary>
    public static List<(int X, int Y)> FindPath(
        TerrainGrid grid,
        (int X, int Y) start,
        (int X, int Y) end,
        int maxIterations = DefaultMaxIterations,
        int maxDistance = DefaultMaxDistance)
        => FindPath(grid, start, end, out _, maxIterations, maxDistance);

    /// <summary>
    /// Finds a route from start to end, and says what happened when there is none.
    /// </summary>
    /// <remarks>
    /// Both ends are snapped to walkable ground first. That is not a nicety: the player stands
    /// against walls constantly, and a point of interest is frequently placed ON a terrain
    /// boundary rather than beside it, so a search that refused a blocked endpoint would
    /// refuse most of the routes actually asked for.
    ///
    /// The OUTCOME is reported because "no route" has several causes that want completely
    /// different responses, and one message for all of them tells the user nothing. "It is
    /// further than this will search", "there is nowhere to stand at that end" and "everything
    /// reachable was searched and it is not among it" are three different facts, and only the
    /// last one means the place cannot be walked to.
    /// </remarks>
    public static List<(int X, int Y)> FindPath(
        TerrainGrid grid,
        (int X, int Y) start,
        (int X, int Y) end,
        out RouteOutcome outcome,
        int maxIterations = DefaultMaxIterations,
        int maxDistance = DefaultMaxDistance,
        int maxMilliseconds = DefaultMaxMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (!TryWalkableNear(grid, start, out start))
        {
            outcome = RouteOutcome.NoGroundNearStart;
            return [];
        }

        if (!TryWalkableNear(grid, end, out end))
        {
            outcome = RouteOutcome.NoGroundNearEnd;
            return [];
        }

        long dx = end.X - start.X;
        long dy = end.Y - start.Y;
        if ((dx * dx) + (dy * dy) > (long)maxDistance * maxDistance)
        {
            outcome = RouteOutcome.TooFar;
            return [];
        }

        outcome = RouteOutcome.Found;
        if (start == end)
        {
            return [start];
        }

        // Only where the area supplies them. Without heights every cell reads as zero, so the
        // rule would pass every step anyway - the flag is here to make that explicit rather
        // than accidental, and to skip the lookups.
        bool climbs = grid.HasHeights;

        var open = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var cost = new Dictionary<(int X, int Y), float> { [start] = 0f };

        open.Enqueue(start, Heuristic(start, end));

        // Checked in batches rather than every node: reading the clock is not free, and a few
        // thousand nodes is far below the budget being enforced.
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(maxMilliseconds * 0.001 * System.Diagnostics.Stopwatch.Frequency);

        for (int step = 0; step < maxIterations && open.Count > 0; step++)
        {
            if ((step & 0xFFF) == 0xFFF && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
            {
                outcome = RouteOutcome.GaveUp;
                return [];
            }

            (int X, int Y) current = open.Dequeue();
            if (current == end)
            {
                return Smooth(grid, Reconstruct(cameFrom, current, start));
            }

            float here = cost[current];
            float standing = climbs ? grid.HeightAt(current.X, current.Y) : 0f;

            foreach ((int nx, int ny, float move) in Neighbours)
            {
                var next = (X: current.X + nx, Y: current.Y + ny);
                if (!grid.IsWalkable(next.X, next.Y))
                {
                    continue;
                }

                // The same walkable cell can belong to two storeys - see MaxClimbPerCell.
                // Without this the route steps between them and is drawn through a floor,
                // which is the one kind of wrong route that looks entirely plausible.
                if (climbs && Math.Abs(grid.HeightAt(next.X, next.Y) - standing) > MaxClimbPerCell)
                {
                    continue;
                }

                // No cutting a wall's corner: a diagonal needs both of the cells it passes
                // between. Without this the route slips through the join of two walls, which
                // is the one place a drawn route is confidently wrong.
                if (nx != 0 && ny != 0
                    && (!grid.IsWalkable(current.X + nx, current.Y) || !grid.IsWalkable(current.X, current.Y + ny)))
                {
                    continue;
                }

                float candidate = here + move;
                if (cost.TryGetValue(next, out float known) && known <= candidate)
                {
                    continue;
                }

                cost[next] = candidate;
                cameFrom[next] = current;
                open.Enqueue(next, candidate + Heuristic(next, end));
            }
        }

        // Two ways out of that loop, and they mean opposite things. An empty queue is an
        // ANSWER - everything reachable was looked at and the target was not among it. Running
        // out of steps is not an answer at all.
        outcome = open.Count == 0 ? RouteOutcome.Unreachable : RouteOutcome.GaveUp;
        return [];
    }

    /// <summary>
    /// The nearest walkable cell, searched outward in rings.
    /// </summary>
    /// <remarks>
    /// Ring by ring rather than a filled square, so the first cell found really is the
    /// nearest one - a square scan would return a corner before a neighbour directly ahead.
    /// </remarks>
    public static bool TryWalkableNear(
        TerrainGrid grid, (int X, int Y) from, out (int X, int Y) found, int maxRadius = 80)
    {
        ArgumentNullException.ThrowIfNull(grid);

        found = from;
        if (grid.IsWalkable(from.X, from.Y))
        {
            return true;
        }

        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int offset = -radius; offset <= radius; offset++)
            {
                (int X, int Y)[] ring =
                [
                    (from.X + offset, from.Y - radius),
                    (from.X + offset, from.Y + radius),
                    (from.X - radius, from.Y + offset),
                    (from.X + radius, from.Y + offset),
                ];

                foreach ((int X, int Y) cell in ring)
                {
                    if (grid.IsWalkable(cell.X, cell.Y))
                    {
                        found = cell;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when a straight line between two cells stays on walkable ground.
    /// </summary>
    /// <remarks>
    /// Bresenham, plus the SAME corner rule the search itself applies: a step that moves on
    /// both axes at once passes between two cells, and if both of those are solid the line has
    /// squeezed through the join of two walls.
    ///
    /// Without that rule the two disagree, and the disagreement is one-directional and
    /// therefore dangerous: the search refuses to cut a corner, and then smoothing - which
    /// uses this - puts the shortcut straight back in. A test that only sampled points along
    /// the line missed it entirely, because an exact diagonal has no sample between its ends.
    /// </remarks>
    public static bool IsLineClear(TerrainGrid grid, (int X, int Y) from, (int X, int Y) to)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (!grid.IsWalkable(from.X, from.Y))
        {
            return false;
        }

        int x = from.X;
        int y = from.Y;
        int dx = Math.Abs(to.X - x);
        int dy = Math.Abs(to.Y - y);
        int stepX = to.X > x ? 1 : -1;
        int stepY = to.Y > y ? 1 : -1;
        int error = dx - dy;

        while (x != to.X || y != to.Y)
        {
            int doubled = error * 2;
            bool movingX = doubled > -dy;
            bool movingY = doubled < dx;

            if (movingX && movingY
                && (!grid.IsWalkable(x + stepX, y) || !grid.IsWalkable(x, y + stepY)))
            {
                return false;
            }

            if (movingX)
            {
                error -= dy;
                x += stepX;
            }

            if (movingY)
            {
                error += dx;
                y += stepY;
            }

            if (!grid.IsWalkable(x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reduces a cell-by-cell path to the fewest points that describe the same route.
    /// </summary>
    /// <remarks>
    /// From each point, jump to the FARTHEST one still in line of sight. Raw A* output is a
    /// staircase - hundreds of single-cell steps - which is both unreadable when drawn and
    /// pointless to carry around.
    /// </remarks>
    public static List<(int X, int Y)> Smooth(TerrainGrid grid, List<(int X, int Y)> path)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count <= 2)
        {
            return path;
        }

        var result = new List<(int X, int Y)> { path[0] };
        int at = 0;

        while (at < path.Count - 1)
        {
            int farthest = at + 1;
            for (int i = path.Count - 1; i > at; i--)
            {
                if (IsLineClear(grid, path[at], path[i]))
                {
                    farthest = i;
                    break;
                }
            }

            at = farthest;
            result.Add(path[at]);
        }

        return result;
    }

    private static float Heuristic((int X, int Y) from, (int X, int Y) to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static List<(int X, int Y)> Reconstruct(
        Dictionary<(int X, int Y), (int X, int Y)> cameFrom, (int X, int Y) current, (int X, int Y) start)
    {
        var path = new List<(int X, int Y)> { current };
        while (current != start && cameFrom.TryGetValue(current, out (int X, int Y) previous))
        {
            current = previous;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }
}
