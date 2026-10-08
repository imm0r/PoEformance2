using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One place a route should lead to.</summary>
/// <param name="Target">
/// The point of interest's address, which is its identity - the position alone cannot say
/// whether the user picked a different exit that happens to sit nearby.
/// </param>
public sealed record RouteTarget(ulong Target, float WorldX, float WorldY)
{
    /// <summary>The stops the route passes on its way, in world units and in order - see <see cref="RoutePlanner.AddVia"/>.</summary>
    public IReadOnlyList<Vector2> Via { get; init; } = [];

    /// <summary>Where arriving counts, for a destination bigger than a point - a room - or null for the radius around the point.</summary>
    public RouteZone? Zone { get; init; }
}

/// <summary>
/// The tiles a destination covers: standing on any of them is having arrived.
/// </summary>
/// <remarks>
/// For a ROOM, which is somewhere you are as soon as you are inside it. The radius that suits a
/// chest or an exit would keep the route to a room alive until its middle was reached, and the
/// middle of a room is often a pillar or a pit.
/// </remarks>
public sealed class RouteZone
{
    private readonly int _tilesX;
    private readonly int[] _tiles;

    /// <param name="tilesX">The area's tiles across, which the indices are counted in.</param>
    /// <param name="tiles">The tiles, by row-major index.</param>
    public RouteZone(int tilesX, IEnumerable<int> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        _tilesX = tilesX;
        _tiles = [.. tiles.Distinct().Order()];
    }

    /// <summary>How many tiles it covers.</summary>
    public int Count => _tiles.Length;

    /// <summary>Whether a world position stands on one of its tiles. A binary search - this is asked every read tick.</summary>
    public bool Holds(float worldX, float worldY)
    {
        if (worldX < 0f || worldY < 0f || _tilesX <= 0)
        {
            return false;
        }

        int x = (int)(worldX / MapView.WorldToGrid) / TerrainGrid.CellsPerTile;
        int y = (int)(worldY / MapView.WorldToGrid) / TerrainGrid.CellsPerTile;
        return x < _tilesX && Array.BinarySearch(_tiles, (y * _tilesX) + x) >= 0;
    }
}

/// <summary>Everywhere a route should lead. Published by the overlay, read by the reader thread.</summary>
public sealed record RouteRequest(IReadOnlyList<RouteTarget> Targets)
{
    public static RouteRequest None { get; } = new([]);
}

/// <summary>One route as found, in grid cells from the player to its target.</summary>
/// <param name="Cells">Corner points, not every cell - the path is smoothed before it is sent.</param>
public sealed record RouteView(
    ulong Target,
    IReadOnlyList<(int X, int Y)> Cells,
    float LengthCells,
    string Status)
{
    /// <summary>Roughly how far the walk is, in the world units everything else is measured in.</summary>
    public float LengthWorld => LengthCells * MapView.WorldToGrid;
}

/// <summary>
/// Keeps walkable routes from the player to the chosen places, recomputed as they move.
/// </summary>
/// <remarks>
/// Several at once, because comparing them is the point: which exit is actually closer through
/// the walls is a question a straight line cannot answer and two drawn routes answer at a
/// glance.
///
/// Runs on the reader thread beside the world read, for the same reason the interface browser
/// does: A* over a few thousand cells is not a per-frame cost, and the render thread is the
/// one place it must not happen.
///
/// Recomputed on a MOVE rather than on a timer. A route is only wrong once its start has
/// moved, so following the player by distance keeps every route correct while standing still
/// costs nothing - and the far ends never move at all.
/// </remarks>
public sealed class RoutePlanner
{
    /// <summary>
    /// Routes held at once.
    /// </summary>
    /// <remarks>
    /// Each is its own search, and they are all recomputed together whenever the player has
    /// moved far enough - so this is a bound on the work per move, not just on the clutter.
    /// </remarks>
    public const int MaxRoutes = 5;

    /// <summary>Searches on the thread pool, off whatever asked for them. What the tool uses.</summary>
    public static Action<Action> Background { get; } = work => _ = Task.Run(work);

    /// <summary>Searches where they were asked for, so the answer is there on return.</summary>
    /// <remarks>For tests, which should not be made to wait and guess how long.</remarks>
    public static Action<Action> Immediate { get; } = work => work();

    /// <summary>How far the player may drift before the routes are worth finding again.</summary>
    private const float RefreshAfterCells = 6f;

    /// <summary>
    /// How close counts as having got there.
    /// </summary>
    /// <remarks>
    /// Four cells, which is about 43 world units - a step or two, near enough to interact with
    /// whatever was chosen. It is deliberately not tighter: a destination is a marker's
    /// position, chests and portals are reached from beside them rather than on top of them,
    /// and an arrow that will not go away until you stand on an exact point is worse than one
    /// that goes away slightly early.
    ///
    /// It is also deliberately not looser. Two places in one room are commonly six or eight
    /// cells apart, and a radius that swallows the neighbour would tick off the thing you have
    /// not been to yet.
    /// </remarks>
    public const float ArrivedWithinCells = 4f;

    /// <summary>A floor on how often the search runs, for a player moving continuously.</summary>
    private const long MinimumIntervalMs = 250;

    /// <summary>
    /// Stops one route may pass on its way.
    /// </summary>
    /// <remarks>
    /// Each stop is a search of its own - the route is found leg by leg - so this bounds the
    /// work per move the way <see cref="MaxRoutes"/> does. A stop past it replaces nothing and
    /// is not added.
    /// </remarks>
    public const int MaxStops = 8;

    /// <summary>The last identity handed to a route that leads to a bare point rather than to a place.</summary>
    private static long _points;

    private readonly Action<Action> _schedule;

    private RouteRequest _request = RouteRequest.None;
    private IReadOnlyList<RouteView> _routes = [];

    private string _plannedFor = string.Empty;
    private Vector2 _plannedFrom;
    private long _plannedAt;
    private uint _plannedArea;

    /// <summary>1 while a search is running, so only one is ever in flight.</summary>
    private int _searching;

    /// <summary>The area the reader last saw, for throwing away an answer about an old one.</summary>
    private uint _currentArea;

    /// <summary>
    /// The last real area the destinations belong to.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="_currentArea"/> because it must ignore the zeroes. The area
    /// hash reads 0 between zones, so comparing against the last value seen would either treat
    /// a loading screen as a new area - wiping the destinations of the map being loaded INTO -
    /// or, guarded the other way, miss the change entirely because the two real areas were
    /// never next to each other.
    /// </remarks>
    private uint _targetsArea;

    /// <param name="schedule">
    /// Where a search runs. <see cref="Background"/> by default, which is what keeps a long
    /// one off the thread that reads the game.
    /// </param>
    public RoutePlanner(Action<Action>? schedule = null) => _schedule = schedule ?? Background;

    /// <summary>The newest routes. Never blocks, never null, never partially built.</summary>
    public IReadOnlyList<RouteView> Routes => Volatile.Read(ref _routes);

    /// <summary>The places currently routed to, in the order they were chosen.</summary>
    public IReadOnlyList<RouteTarget> Targets => Volatile.Read(ref _request).Targets;

    /// <summary>True when this place is one of the current destinations.</summary>
    public bool IsTarget(ulong address) => Targets.Any(t => t.Target == address);

    /// <summary>Sets the destinations, replacing whatever was there. From the render thread.</summary>
    public void Request(RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>
    /// Adds a place, or drops it if it is already a destination.
    /// </summary>
    /// <remarks>
    /// Appends rather than inserting, so the existing routes keep their order - and with it
    /// their colours, which is what makes a second route readable next to the first.
    /// </remarks>
    public void Toggle(ulong address, float worldX, float worldY) => Toggle(new RouteTarget(address, worldX, worldY));

    /// <summary>
    /// Adds a destination as given - with its zone, for a room - or drops the one with its identity if it is already a destination.
    /// </summary>
    public void Toggle(RouteTarget chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        List<RouteTarget> targets = [.. Targets];
        int at = targets.FindIndex(t => t.Target == chosen.Target);

        if (at >= 0)
        {
            targets.RemoveAt(at);
        }
        else
        {
            if (targets.Count >= MaxRoutes)
            {
                targets.RemoveAt(0);   // the oldest gives way, so a click always does something
            }

            targets.Add(chosen);
        }

        Request(new RouteRequest(targets));
    }

    /// <summary>
    /// Adds a stop on the way: to the route chosen last, before its destination and after the stops it already has - or, with no route, a route to the point itself.
    /// </summary>
    /// <remarks>
    /// THE NEWEST ROUTE, because a stop is said about the route somebody has just set up: pick a
    /// destination, then pull the line through the corridor it should take. Stops are passed in
    /// the order they were added. Reaching one drops it, and reaching a later one drops every
    /// stop before it too - walked past is walked past. Reaching the destination ends the route
    /// whatever stops are left, which is the rule every route already has.
    /// </remarks>
    public void AddVia(float worldX, float worldY)
    {
        List<RouteTarget> targets = [.. Targets];
        if (targets.Count == 0)
        {
            ulong point = 0xC000_0000_0000_0000UL | (ulong)Interlocked.Increment(ref _points);
            targets.Add(new RouteTarget(point, worldX, worldY));
        }
        else
        {
            RouteTarget newest = targets[^1];
            if (newest.Via.Count >= MaxStops)
            {
                return;
            }

            targets[^1] = newest with { Via = [.. newest.Via, new Vector2(worldX, worldY)] };
        }

        Request(new RouteRequest(targets));
    }

    /// <summary>Finds the routes if any are wanted and out of date. Called on the reader thread.</summary>
    public void Service(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _currentArea, snapshot.AreaHash);
        ForgetOnLeaving(snapshot.AreaHash);

        RouteRequest request = Volatile.Read(ref _request);
        if (request.Targets.Count == 0)
        {
            if (Routes.Count > 0)
            {
                Volatile.Write(ref _routes, []);
                _plannedFor = string.Empty;
            }

            return;
        }

        if (snapshot.Terrain is not TerrainGrid grid || snapshot.Player is not WorldEntity player)
        {
            Volatile.Write(ref _routes, [new RouteView(0, [], 0f, "no terrain yet")]);
            return;
        }

        var from = new Vector2(player.WorldX, player.WorldY);

        // Before anything decides the routes are up to date: a destination reached is one the
        // player is standing at, and the check has to happen on the tick that becomes true
        // rather than on whichever later tick something else asks for a re-plan.
        if (Reached(request, from) is RouteRequest left)
        {
            Request(left);
            request = left;

            // The drawn route goes NOW, not when the next search finishes. A search runs in
            // the background and can take seconds on a real map, and until it landed the line
            // to the place just reached would keep being drawn across the screen. A test would
            // not catch that on its own: a test runs the search inline, so the overwrite that
            // hides the problem happens before anything can look.
            Volatile.Write(
                ref _routes,
                [.. Routes.Where(route => left.Targets.Any(target => target.Target == route.Target))]);

            if (request.Targets.Count == 0)
            {
                _plannedFor = string.Empty;
                return;
            }
        }

        string signature = Signature(request.Targets);
        bool same = _plannedFor == signature && _plannedArea == snapshot.AreaHash;

        if (same
            && Vector2.Distance(from, _plannedFrom) < RefreshAfterCells * MapView.WorldToGrid
            && nowMs - _plannedAt < 5_000)
        {
            return;
        }

        if (same && nowMs - _plannedAt < MinimumIntervalMs)
        {
            return;
        }

        // One search at a time. A route that is still being looked for is not a reason to
        // start looking for it again, and dropping the request while one is in flight is what
        // keeps a player walking continuously from queueing up searches faster than they finish.
        if (Interlocked.CompareExchange(ref _searching, 1, 0) != 0)
        {
            return;
        }

        _plannedFor = signature;
        _plannedFrom = from;
        _plannedAt = nowMs;
        _plannedArea = snapshot.AreaHash;

        var start = Cell(player.WorldX, player.WorldY);
        List<RouteTarget> targets = [.. request.Targets.Take(MaxRoutes)];
        uint area = snapshot.AreaHash;

        // OFF this thread, and that is not an optimisation. Measured on a 2415x2829 map: a
        // route right across it takes about 1.8 seconds, and one to somewhere unreachable
        // fourteen. This is called from the read loop, which also drives auto-flask - so a
        // search done here would not merely freeze the drawing, it would stop the flasks from
        // being watched for as long as it ran.
        _schedule(() =>
        {
            try
            {
                var found = new List<RouteView>(targets.Count);
                foreach (RouteTarget target in targets)
                {
                    found.Add(Find(grid, start, target));
                }

                // A search that finished after the player left carries an answer about a map
                // they are no longer standing in, so it is dropped rather than drawn.
                if (Volatile.Read(ref _currentArea) == area)
                {
                    Volatile.Write(ref _routes, found);
                }
            }
            finally
            {
                Volatile.Write(ref _searching, 0);
            }
        });
    }

    /// <summary>
    /// One route, leg by leg: from the player to each stop in turn, then to the destination.
    /// </summary>
    /// <remarks>
    /// A leg that finds no way fails the whole route and says which leg it was - a line drawn up
    /// to the stop it cannot get past would read as the route, not as where it ends.
    /// </remarks>
    private static RouteView Find(TerrainGrid grid, (int X, int Y) start, RouteTarget target)
    {
        var cells = new List<(int X, int Y)>();
        (int X, int Y) from = start;
        for (int leg = 0; leg <= target.Via.Count; leg++)
        {
            (int X, int Y) to = leg < target.Via.Count
                ? Cell(target.Via[leg].X, target.Via[leg].Y)
                : Cell(target.WorldX, target.WorldY);
            List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, from, to, out RouteOutcome outcome);
            if (path.Count == 0)
            {
                string why = Explain(outcome);
                return new RouteView(
                    target.Target,
                    [],
                    0f,
                    leg < target.Via.Count ? string.Create(CultureInfo.InvariantCulture, $"{why} - to stop {leg + 1}") : why);
            }

            // The legs meet at a stop, so the next one starts on the point this one ended on.
            cells.AddRange(cells.Count > 0 && cells[^1] == path[0] ? path.Skip(1) : path);
            from = path[^1];
        }

        return new RouteView(target.Target, cells, Length(cells), string.Empty);
    }

    /// <summary>What to tell the user when no route came back.</summary>
    private static string Explain(RouteOutcome outcome) => outcome switch
    {
        RouteOutcome.NoGroundNearEnd => "nothing to stand on there",
        RouteOutcome.NoGroundNearStart => "cannot tell where you are standing",
        RouteOutcome.TooFar => "too far to search",
        RouteOutcome.GaveUp => "gave up looking - too big a search",
        _ => "no way there",
    };

    /// <summary>The route to one place, or null when there is none.</summary>
    public RouteView? For(ulong address) => Routes.FirstOrDefault(r => r.Target == address);

    /// <summary>
    /// The destinations still worth having, or null when the player has reached none.
    /// </summary>
    /// <remarks>
    /// Arriving is what finishes a destination, so it takes itself off the list. The
    /// alternative is a set of arrows that only ever grows until somebody remembers to clear
    /// them, and by then they are pointing at places already visited - which is the same
    /// "arrow to nothing" the area change produced, arrived at from the other direction.
    ///
    /// Null rather than an unchanged list, so the ordinary tick - which is every tick, and
    /// nearly always finds nothing - writes nothing and invalidates nothing.
    ///
    /// A place chosen while ALREADY standing at it goes immediately, which looks like the
    /// click doing nothing. It is the honest answer: there is no way to draw, because there is
    /// nowhere to go. The radius is small enough that this only happens when it is true.
    /// </remarks>
    private static RouteRequest? Reached(RouteRequest request, Vector2 player)
    {
        float within = ArrivedWithinCells * MapView.WorldToGrid;
        var changed = false;
        var left = new List<RouteTarget>(request.Targets.Count);
        foreach (RouteTarget target in request.Targets)
        {
            // A ROOM is arrived at on its first tile, anything else within the radius - and the
            // destination ends the route whatever stops are still ahead of it.
            bool arrived = target.Zone is { } zone
                ? zone.Holds(player.X, player.Y)
                : Vector2.Distance(player, new Vector2(target.WorldX, target.WorldY)) <= within;
            if (arrived)
            {
                changed = true;
                continue;
            }

            // The furthest stop the player stands at: it and every stop before it are behind them.
            int passed = -1;
            for (int stop = target.Via.Count - 1; stop >= 0; stop--)
            {
                if (Vector2.Distance(player, target.Via[stop]) <= within)
                {
                    passed = stop;
                    break;
                }
            }

            if (passed < 0)
            {
                left.Add(target);
                continue;
            }

            changed = true;
            left.Add(target with { Via = [.. target.Via.Skip(passed + 1)] });
        }

        return changed ? new RouteRequest(left) : null;
    }

    /// <summary>
    /// Drops the destinations when the area they belong to is left.
    /// </summary>
    /// <remarks>
    /// A destination is an ENTITY ADDRESS and a position in one map. Neither means anything in
    /// the next one - the address is freed and handed out again, the position is somewhere
    /// else entirely - so keeping them produced arrows to places that are not there, a route
    /// counter that never emptied, and, when an address happened to be reused, an unrelated
    /// place in the new map marked as chosen. That was reported from a live run, and nothing
    /// but the "clear all" button ever emptied the list.
    ///
    /// Returning to the same map instance clears them too, even though its hash is unchanged
    /// on the way back: leaving reloads the area, so the addresses that were chosen no longer
    /// point at what was chosen. Losing the arrows is the honest answer there; keeping them
    /// would be the same bug wearing the same hash.
    /// </remarks>
    private void ForgetOnLeaving(uint area)
    {
        if (area == 0 || area == _targetsArea)
        {
            return;   // between zones, or still in the one the destinations belong to
        }

        // The FIRST area is not a change - there is nothing to have left. Treating it as one
        // wipes any destination chosen before the first read came back, which is a real
        // ordering on a fresh start and was caught by the tests that already existed.
        bool left = _targetsArea != 0;
        _targetsArea = area;

        if (left && Targets.Count > 0)
        {
            Clear();
        }
    }

    /// <summary>Forgets every route, so the next area starts clean.</summary>
    public void Clear()
    {
        Request(RouteRequest.None);
        Volatile.Write(ref _routes, []);
        _plannedFor = string.Empty;
    }

    /// <summary>What the current set of destinations and their stops is, for spotting a change cheaply.</summary>
    private static string Signature(IReadOnlyList<RouteTarget> targets)
    {
        var said = new StringBuilder();
        foreach (RouteTarget target in targets)
        {
            said.Append(CultureInfo.InvariantCulture, $"{target.Target:X}");
            foreach (Vector2 stop in target.Via)
            {
                (int x, int y) = Cell(stop.X, stop.Y);
                said.Append(CultureInfo.InvariantCulture, $">{x}:{y}");
            }

            said.Append(',');
        }

        return said.ToString();
    }

    private static (int X, int Y) Cell(float worldX, float worldY)
        => ((int)(worldX / MapView.WorldToGrid), (int)(worldY / MapView.WorldToGrid));

    private static float Length(IReadOnlyList<(int X, int Y)> cells)
    {
        float total = 0f;
        for (int i = 1; i < cells.Count; i++)
        {
            float dx = cells[i].X - cells[i - 1].X;
            float dy = cells[i].Y - cells[i - 1].Y;
            total += MathF.Sqrt((dx * dx) + (dy * dy));
        }

        return total;
    }
}
