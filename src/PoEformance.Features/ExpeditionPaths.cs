using System.Collections.Concurrent;
using System.Numerics;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Walkable distances for the route planner: A* over the area's grid, with the three things
/// the planner needs that the map's own pathfinder does not have.
/// </summary>
/// <remarks>
/// WHY NOT <see cref="TerrainPathfinder"/> AS IT STANDS. The planner asks "is B within one
/// hop of A" thousands of times per plan, and most of the answers are no: a target behind a
/// wall or across the map. An unbounded search answers no by flooding everything reachable
/// first - fourteen seconds on a big map, measured for that pathfinder - so this one is
/// BOUNDED BY COST: the moment the cheapest open node's f-score exceeds the bound, no path
/// within it can remain and the search stops. A path whose smoothed length is under the hop
/// has raw grid cost under 1.5 times the hop (the diagonal-staircase worst case), so the bound
/// never refuses a real answer. It also MEMOISES every query, since one plan asks the same
/// (start, end) dozens of times from its tour matrix, its placer and its spare optimiser, and
/// it carries DOOR OVERRIDES - cells made walkable that the raw grid marks solid - for the
/// doorways the chain has to route through.
///
/// What it keeps from the map's pathfinder, because they are what make a route honest: no
/// cutting a wall's corner on a diagonal, no stepping between storeys where the grid is flat
/// but the heights are not, both ends snapped to walkable ground, and smoothing by line of
/// sight with the same corner rule the search applies - the two must agree, or smoothing
/// puts back the shortcut the search refused.
///
/// CONCURRENT, because the tour matrix is filled in parallel: every search is self-contained
/// and the memo is a concurrent dictionary, so a rare double-compute stores the same value
/// twice and nothing worse. The component labels - 8-connected without the corner rule, a
/// SUPERSET of the search's moves, so two cells in different components are provably apart
/// - are built once under a lock and answer the far-apart case before any search runs.
/// </remarks>
public sealed class ExpeditionPaths
{
    /// <summary>How far an endpoint is moved to find floor - an entity on a ledge the grid marks solid.</summary>
    public const int SnapRadius = 120;

    /// <summary>Cells beyond which no search is attempted - a guard, not a limit anybody meets.</summary>
    public const int FarthestSearch = 8_000;

    /// <summary>Most cells the labels are built for; above it the fast "different component" answer is skipped.</summary>
    private const long MostLabelledCells = 16_000_000;

    private static readonly (int Dx, int Dy, float Cost)[] Neighbours =
    [
        (0, -1, 1f), (1, -1, 1.41421356f), (1, 0, 1f), (1, 1, 1.41421356f),
        (0, 1, 1f), (-1, 1, 1.41421356f), (-1, 0, 1f), (-1, -1, 1.41421356f),
    ];

    private readonly TerrainGrid? _grid;
    private readonly HashSet<(int X, int Y)>? _opened;
    private readonly float _hop;

    private readonly ConcurrentDictionary<long, float> _length = new();
    private readonly ConcurrentDictionary<long, float> _reach = new();
    private readonly ConcurrentDictionary<long, List<Vector2>?> _paths = new();
    private readonly object _labelLock = new();
    private int[]? _labels;
    private bool _labelled;
    private long _searches;
    private long _hits;

    /// <param name="grid">The area's walkable grid, or null for an area with none - then every distance is the straight line.</param>
    /// <param name="opened">Cells to treat as walkable whatever the grid says.</param>
    /// <param name="hop">The one-hop placement reach the bounded <see cref="Reach"/> answers against.</param>
    public ExpeditionPaths(TerrainGrid? grid, IEnumerable<(int X, int Y)>? opened, float hop)
    {
        _grid = grid;
        _opened = opened is null ? null : new HashSet<(int X, int Y)>(opened);
        if (_opened is { Count: 0 })
        {
            _opened = null;
        }

        _hop = Math.Max(1f, hop);
    }

    /// <summary>Whether there is a grid to walk at all.</summary>
    public bool HasGrid => _grid is not null;

    /// <summary>The one-hop reach the bounded search answers against.</summary>
    public float Hop => _hop;

    /// <summary>Searches actually run.</summary>
    public long Searches => Interlocked.Read(ref _searches);

    /// <summary>Queries answered from the memo.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Whether a point stands on walkable ground. True without a grid, so nothing is refused.</summary>
    public bool IsWalkable(Vector2 grid)
    {
        if (_grid is null)
        {
            return true;
        }

        int x = (int)MathF.Round(grid.X);
        int y = (int)MathF.Round(grid.Y);
        return x >= 0 && y >= 0 && Walkable(x, y);
    }

    /// <summary>
    /// The walkable distance from a to b when it is within one hop, else -1.
    /// </summary>
    /// <remarks>
    /// The dominant query, and the bounded one: a target beyond the hop is refused after a
    /// small local expansion - often at the first node, when its straight-line heuristic
    /// already exceeds the bound - instead of flooding the reachable component.
    /// </remarks>
    public float Reach(Vector2 a, Vector2 b)
    {
        if (_grid is null)
        {
            float straight = Vector2.Distance(a, b);
            return straight <= _hop ? straight : -1f;
        }

        long key = Key(a, b);
        if (_reach.TryGetValue(key, out float known))
        {
            Interlocked.Increment(ref _hits);
            return known;
        }

        List<Vector2>? route = Search(a, b, _hop * 1.5f);
        float result = route is null ? -1f : LengthOf(route);
        if (result > _hop)
        {
            result = -1f;
        }

        _reach[key] = result;
        return result;
    }

    /// <summary>The full walkable distance from a to b, or -1 when there is no way.</summary>
    public float Length(Vector2 a, Vector2 b)
    {
        if (_grid is null)
        {
            return Vector2.Distance(a, b);
        }

        long key = Key(a, b);
        if (_length.TryGetValue(key, out float known))
        {
            Interlocked.Increment(ref _hits);
            return known;
        }

        // Different components: no search can join them, so answer at once. Only when both
        // endpoints are themselves walkable - a blocked one would be snapped first.
        if (ApartByLabel(a, b))
        {
            _length[key] = -1f;
            return -1f;
        }

        List<Vector2>? route = Path(a, b);
        float result = route is null ? -1f : LengthOf(route);
        _length[key] = result;
        return result;
    }

    /// <summary>The smoothed walkable path from a to b, or null. Memoised.</summary>
    public List<Vector2>? Path(Vector2 a, Vector2 b)
    {
        if (_grid is null)
        {
            return [a, b];
        }

        long key = Key(a, b);
        if (_paths.TryGetValue(key, out List<Vector2>? known))
        {
            Interlocked.Increment(ref _hits);
            return known;
        }

        List<Vector2>? route = Search(a, b, float.PositiveInfinity);
        _paths[key] = route;
        return route;
    }

    /// <summary>
    /// The farthest walkable point at most <paramref name="maxDist"/> along the path from
    /// <paramref name="from"/> toward <paramref name="toward"/>. False when it cannot move.
    /// </summary>
    /// <remarks>
    /// The path is walked RASTERISED, a cell at a time, and a sample that is not walkable is a
    /// corner cut between two smoothed waypoints - the real route bends round it - so it is
    /// skipped rather than taken as a dead end. The point returned is always one that was
    /// confirmed walkable, so a stepping-stone charge never lands on scenery.
    /// </remarks>
    public bool StepToward(Vector2 from, Vector2 toward, float maxDist, out Vector2 step)
    {
        step = from;
        if (_grid is null)
        {
            float dist = Vector2.Distance(from, toward);
            if (dist <= 1f)
            {
                return false;
            }

            step = Vector2.Lerp(from, toward, Math.Min(1f, maxDist / dist));
            return Vector2.Distance(from, step) > 1f;
        }

        List<Vector2>? route = Path(from, toward);
        if (route is null || route.Count < 2)
        {
            return false;
        }

        float acc = 0f;
        Vector2 best = from;
        Vector2 prev = route[0];
        for (int i = 1; i < route.Count; i++)
        {
            Vector2 a = route[i - 1];
            Vector2 b = route[i];
            float seg = Vector2.Distance(a, b);
            int sub = Math.Max(1, (int)MathF.Ceiling(seg));
            for (int s = 1; s <= sub; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)s / sub);
                float d = Vector2.Distance(prev, p);
                if (acc + d > maxDist)
                {
                    Vector2 candidate = Vector2.Lerp(prev, p, d <= 1e-3f ? 0f : (maxDist - acc) / d);
                    if (IsWalkable(candidate))
                    {
                        best = candidate;
                    }

                    step = best;
                    return Vector2.Distance(from, step) > 1f;
                }

                acc += d;
                prev = p;
                if (IsWalkable(p))
                {
                    best = p;
                }
            }
        }

        step = best;
        return Vector2.Distance(from, step) > 1f;
    }

    /// <summary>The length of a polyline.</summary>
    public static float LengthOf(IReadOnlyList<Vector2> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        float length = 0f;
        for (int i = 1; i < route.Count; i++)
        {
            length += Vector2.Distance(route[i - 1], route[i]);
        }

        return length;
    }

    private bool Walkable(int x, int y)
        => (_opened is not null && _opened.Contains((x, y))) || _grid!.IsWalkable(x, y);

    /// <summary>The A*: both ends snapped, cost-bounded, corner- and climb-checked, smoothed.</summary>
    private List<Vector2>? Search(Vector2 from, Vector2 to, float maxCost)
    {
        TerrainGrid grid = _grid!;
        Interlocked.Increment(ref _searches);

        var start = ((int)MathF.Round(from.X), (int)MathF.Round(from.Y));
        var end = ((int)MathF.Round(to.X), (int)MathF.Round(to.Y));
        if (!Near(start, out start) || !Near(end, out end))
        {
            return null;
        }

        long dx = end.Item1 - start.Item1;
        long dy = end.Item2 - start.Item2;
        if ((dx * dx) + (dy * dy) > (long)FarthestSearch * FarthestSearch)
        {
            return null;
        }

        if (start == end)
        {
            return [new Vector2(start.Item1, start.Item2)];
        }

        int width = grid.Width;
        int height = grid.Height;
        bool climbs = grid.HasHeights;
        int startKey = (start.Item2 * width) + start.Item1;
        int endKey = (end.Item2 * width) + end.Item1;

        var open = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var cost = new Dictionary<int, float> { [startKey] = 0f };
        open.Enqueue(startKey, Heuristic(start.Item1, start.Item2, end.Item1, end.Item2));

        while (open.TryDequeue(out int current, out float f))
        {
            if (f > maxCost)
            {
                return null;
            }

            if (current == endKey)
            {
                return Smooth(Reconstruct(cameFrom, current, startKey, width));
            }

            int cx = current % width;
            int cy = current / width;
            float here = cost[current];
            float standing = climbs ? grid.HeightAt(cx, cy) : 0f;

            foreach ((int nx, int ny, float move) in Neighbours)
            {
                int x = cx + nx;
                int y = cy + ny;
                if (x < 0 || y < 0 || x >= width || y >= height || !Walkable(x, y))
                {
                    continue;
                }

                if (climbs && Math.Abs(grid.HeightAt(x, y) - standing) > TerrainPathfinder.MaxClimbPerCell)
                {
                    continue;
                }

                if (nx != 0 && ny != 0 && (!Walkable(cx + nx, cy) || !Walkable(cx, cy + ny)))
                {
                    continue;
                }

                int next = (y * width) + x;
                float candidate = here + move;
                if (cost.TryGetValue(next, out float known) && known <= candidate)
                {
                    continue;
                }

                cost[next] = candidate;
                cameFrom[next] = current;
                open.Enqueue(next, candidate + Heuristic(x, y, end.Item1, end.Item2));
            }
        }

        return null;
    }

    private bool Near((int X, int Y) from, out (int X, int Y) found)
    {
        found = from;
        if (from.X >= 0 && from.Y >= 0 && Walkable(from.X, from.Y))
        {
            return true;
        }

        for (int radius = 1; radius <= SnapRadius; radius++)
        {
            for (int offset = -radius; offset <= radius; offset++)
            {
                if (Try(from.X + offset, from.Y - radius, out found) || Try(from.X + offset, from.Y + radius, out found)
                    || Try(from.X - radius, from.Y + offset, out found) || Try(from.X + radius, from.Y + offset, out found))
                {
                    return true;
                }
            }
        }

        return false;

        bool Try(int x, int y, out (int X, int Y) cell)
        {
            cell = (x, y);
            return x >= 0 && y >= 0 && Walkable(x, y);
        }
    }

    private static float Heuristic(int x, int y, int toX, int toY)
    {
        float dx = toX - x;
        float dy = toY - y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static List<(int X, int Y)> Reconstruct(Dictionary<int, int> cameFrom, int current, int start, int width)
    {
        var path = new List<(int X, int Y)> { (current % width, current / width) };
        while (current != start && cameFrom.TryGetValue(current, out int previous))
        {
            current = previous;
            path.Add((current % width, current / width));
        }

        path.Reverse();
        return path;
    }

    /// <summary>Line-of-sight smoothing with the search's own corner rule.</summary>
    private List<Vector2> Smooth(List<(int X, int Y)> path)
    {
        var result = new List<Vector2> { new(path[0].X, path[0].Y) };
        if (path.Count <= 2)
        {
            for (int i = 1; i < path.Count; i++)
            {
                result.Add(new Vector2(path[i].X, path[i].Y));
            }

            return result;
        }

        int at = 0;
        while (at < path.Count - 1)
        {
            int farthest = at + 1;
            for (int i = path.Count - 1; i > at; i--)
            {
                if (LineClear(path[at], path[i]))
                {
                    farthest = i;
                    break;
                }
            }

            at = farthest;
            result.Add(new Vector2(path[at].X, path[at].Y));
        }

        return result;
    }

    private bool LineClear((int X, int Y) from, (int X, int Y) to)
    {
        if (!Walkable(from.X, from.Y))
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
            if (movingX && movingY && (!Walkable(x + stepX, y) || !Walkable(x, y + stepY)))
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

            if (x < 0 || y < 0 || !Walkable(x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two walkable endpoints lie in different connected components.</summary>
    private bool ApartByLabel(Vector2 a, Vector2 b)
    {
        int[]? labels = Labels();
        if (labels is null)
        {
            return false;
        }

        int width = _grid!.Width;
        int height = _grid.Height;
        int ax = (int)MathF.Round(a.X);
        int ay = (int)MathF.Round(a.Y);
        int bx = (int)MathF.Round(b.X);
        int by = (int)MathF.Round(b.Y);
        if ((uint)ax >= (uint)width || (uint)ay >= (uint)height || (uint)bx >= (uint)width || (uint)by >= (uint)height)
        {
            return false;
        }

        int la = labels[(ay * width) + ax];
        int lb = labels[(by * width) + bx];
        return la >= 0 && lb >= 0 && la != lb;
    }

    /// <summary>The component labels, built once: -2 solid, else the component id.</summary>
    private int[]? Labels()
    {
        if (_labelled)
        {
            return _labels;
        }

        lock (_labelLock)
        {
            if (_labelled)
            {
                return _labels;
            }

            TerrainGrid grid = _grid!;
            int width = grid.Width;
            int height = grid.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MostLabelledCells)
            {
                _labelled = true;
                return null;
            }

            var labels = new int[width * height];
            Array.Fill(labels, -1);
            var stack = new Stack<int>();
            var next = 0;
            for (int sy = 0; sy < height; sy++)
            {
                for (int sx = 0; sx < width; sx++)
                {
                    int si = (sy * width) + sx;
                    if (labels[si] != -1)
                    {
                        continue;
                    }

                    if (!Walkable(sx, sy))
                    {
                        labels[si] = -2;
                        continue;
                    }

                    int id = next++;
                    labels[si] = id;
                    stack.Push(si);
                    while (stack.Count > 0)
                    {
                        int ci = stack.Pop();
                        int cx = ci % width;
                        int cy = ci / width;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }

                                int nx = cx + dx;
                                int ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                                {
                                    continue;
                                }

                                int ni = (ny * width) + nx;
                                if (labels[ni] != -1)
                                {
                                    continue;
                                }

                                if (!Walkable(nx, ny))
                                {
                                    labels[ni] = -2;
                                    continue;
                                }

                                labels[ni] = id;
                                stack.Push(ni);
                            }
                        }
                    }
                }
            }

            _labels = labels;
            _labelled = true;
            return labels;
        }
    }

    /// <summary>Four 16-bit cell coordinates in one key; grids are far under 65536 cells a side.</summary>
    private static long Key(Vector2 a, Vector2 b)
    {
        long ax = (ushort)(int)MathF.Round(a.X);
        long ay = (ushort)(int)MathF.Round(a.Y);
        long bx = (ushort)(int)MathF.Round(b.X);
        long by = (ushort)(int)MathF.Round(b.Y);
        return (ax << 48) | (ay << 32) | (bx << 16) | by;
    }
}
