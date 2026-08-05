namespace PoEformance.Game.World;

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
    public const int DefaultMaxIterations = 400_000;

    /// <summary>
    /// Straight-line cells beyond which no search is attempted.
    /// </summary>
    /// <remarks>
    /// A cap on the QUESTION rather than on the answer: a target on the far side of a big map
    /// is a search over most of it, and the route to something that far away is not the thing
    /// anybody is looking at.
    /// </remarks>
    public const int DefaultMaxDistance = 2500;

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
    /// <remarks>
    /// Both ends are snapped to walkable ground first. That is not a nicety: the player stands
    /// against walls constantly, and a point of interest is frequently placed ON a terrain
    /// boundary rather than beside it, so a search that refused a blocked endpoint would
    /// refuse most of the routes actually asked for.
    /// </remarks>
    public static List<(int X, int Y)> FindPath(
        TerrainGrid grid,
        (int X, int Y) start,
        (int X, int Y) end,
        int maxIterations = DefaultMaxIterations,
        int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (!TryWalkableNear(grid, start, out start) || !TryWalkableNear(grid, end, out end))
        {
            return [];
        }

        long dx = end.X - start.X;
        long dy = end.Y - start.Y;
        if ((dx * dx) + (dy * dy) > (long)maxDistance * maxDistance)
        {
            return [];
        }

        if (start == end)
        {
            return [start];
        }

        var open = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var cost = new Dictionary<(int X, int Y), float> { [start] = 0f };

        open.Enqueue(start, Heuristic(start, end));

        for (int step = 0; step < maxIterations && open.Count > 0; step++)
        {
            (int X, int Y) current = open.Dequeue();
            if (current == end)
            {
                return Smooth(grid, Reconstruct(cameFrom, current, start));
            }

            float here = cost[current];

            foreach ((int nx, int ny, float move) in Neighbours)
            {
                var next = (X: current.X + nx, Y: current.Y + ny);
                if (!grid.IsWalkable(next.X, next.Y))
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
