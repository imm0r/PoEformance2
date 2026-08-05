using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One place a route should lead to.</summary>
/// <param name="Target">
/// The point of interest's address, which is its identity - the position alone cannot say
/// whether the user picked a different exit that happens to sit nearby.
/// </param>
public sealed record RouteTarget(ulong Target, float WorldX, float WorldY);

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

    /// <summary>How far the player may drift before the routes are worth finding again.</summary>
    private const float RefreshAfterCells = 6f;

    /// <summary>A floor on how often the search runs, for a player moving continuously.</summary>
    private const long MinimumIntervalMs = 250;

    private RouteRequest _request = RouteRequest.None;
    private IReadOnlyList<RouteView> _routes = [];

    private string _plannedFor = string.Empty;
    private Vector2 _plannedFrom;
    private long _plannedAt;
    private uint _plannedArea;

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
    public void Toggle(ulong address, float worldX, float worldY)
    {
        List<RouteTarget> targets = [.. Targets];
        int at = targets.FindIndex(t => t.Target == address);

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

            targets.Add(new RouteTarget(address, worldX, worldY));
        }

        Request(new RouteRequest(targets));
    }

    /// <summary>Finds the routes if any are wanted and out of date. Called on the reader thread.</summary>
    public void Service(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

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

        _plannedFor = signature;
        _plannedFrom = from;
        _plannedAt = nowMs;
        _plannedArea = snapshot.AreaHash;

        var start = Cell(player.WorldX, player.WorldY);
        var found = new List<RouteView>(request.Targets.Count);

        foreach (RouteTarget target in request.Targets.Take(MaxRoutes))
        {
            List<(int X, int Y)> cells = TerrainPathfinder.FindPath(grid, start, Cell(target.WorldX, target.WorldY));
            found.Add(cells.Count == 0
                ? new RouteView(target.Target, [], 0f, "no way there")
                : new RouteView(target.Target, cells, Length(cells), string.Empty));
        }

        Volatile.Write(ref _routes, found);
    }

    /// <summary>The route to one place, or null when there is none.</summary>
    public RouteView? For(ulong address) => Routes.FirstOrDefault(r => r.Target == address);

    /// <summary>Forgets every route, so the next area starts clean.</summary>
    public void Clear()
    {
        Request(RouteRequest.None);
        Volatile.Write(ref _routes, []);
        _plannedFor = string.Empty;
    }

    /// <summary>What the current set of destinations is, for spotting a change cheaply.</summary>
    private static string Signature(IReadOnlyList<RouteTarget> targets)
        => string.Join(',', targets.Select(t => t.Target.ToString("X", System.Globalization.CultureInfo.InvariantCulture)));

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
