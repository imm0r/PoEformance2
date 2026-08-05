using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>Where the route should lead. Published by the overlay, read by the reader thread.</summary>
/// <param name="Target">
/// The chosen point of interest's address, which is its identity - the position alone cannot
/// say whether the user picked a different exit that happens to sit nearby.
/// </param>
public sealed record RouteRequest(ulong Target = 0, float WorldX = 0f, float WorldY = 0f)
{
    public static RouteRequest None { get; } = new();

    public bool Wanted => Target != 0;
}

/// <summary>The route as found, in grid cells from the player to the target.</summary>
/// <param name="Cells">Corner points, not every cell - the path is smoothed before it is sent.</param>
public sealed record RouteView(
    ulong Target,
    IReadOnlyList<(int X, int Y)> Cells,
    float LengthCells,
    string Status)
{
    public static RouteView None { get; } = new(0, [], 0f, string.Empty);

    /// <summary>Roughly how far the walk is, in the world units everything else is measured in.</summary>
    public float LengthWorld => LengthCells * MapView.WorldToGrid;
}

/// <summary>
/// Keeps a walkable route from the player to a chosen point, recomputed as they move.
/// </summary>
/// <remarks>
/// Runs on the reader thread beside the world read, for the same reason the interface browser
/// does: A* over a few thousand cells is not a per-frame cost, and the render thread is the
/// one place it must not happen.
///
/// Recomputed on a MOVE rather than on a timer. A route is only wrong once its start has
/// moved, so following the player by distance keeps it correct while standing still costs
/// nothing - and the far end never moves at all.
/// </remarks>
public sealed class RoutePlanner
{
    /// <summary>How far the player may drift before the route is worth finding again.</summary>
    private const float RefreshAfterCells = 6f;

    /// <summary>A floor on how often the search runs, for a player moving continuously.</summary>
    private const long MinimumIntervalMs = 250;

    private RouteRequest _request = RouteRequest.None;
    private RouteView _view = RouteView.None;

    private ulong _plannedFor;
    private Vector2 _plannedFrom;
    private long _plannedAt;
    private uint _plannedArea;

    /// <summary>The newest route. Never blocks, never null, never partially built.</summary>
    public RouteView View => Volatile.Read(ref _view);

    /// <summary>The point currently routed to, 0 for none.</summary>
    public ulong Target => Volatile.Read(ref _request).Target;

    /// <summary>Chooses a destination, or clears it. Called from the render thread.</summary>
    public void Request(RouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>Finds the route if it is wanted and out of date. Called on the reader thread.</summary>
    public void Service(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RouteRequest request = Volatile.Read(ref _request);
        if (!request.Wanted)
        {
            if (View.Target != 0)
            {
                Volatile.Write(ref _view, RouteView.None);
            }

            return;
        }

        if (snapshot.Terrain is not TerrainGrid grid || snapshot.Player is not WorldEntity player)
        {
            Volatile.Write(ref _view, RouteView.None with { Status = "no terrain yet" });
            return;
        }

        var from = new Vector2(player.WorldX, player.WorldY);
        bool sameTarget = _plannedFor == request.Target && _plannedArea == snapshot.AreaHash;
        if (sameTarget
            && Vector2.Distance(from, _plannedFrom) < RefreshAfterCells * MapView.WorldToGrid
            && nowMs - _plannedAt < 5_000)
        {
            return;
        }

        if (sameTarget && nowMs - _plannedAt < MinimumIntervalMs)
        {
            return;
        }

        _plannedFor = request.Target;
        _plannedFrom = from;
        _plannedAt = nowMs;
        _plannedArea = snapshot.AreaHash;

        var start = Cell(player.WorldX, player.WorldY);
        var end = Cell(request.WorldX, request.WorldY);

        List<(int X, int Y)> cells = TerrainPathfinder.FindPath(grid, start, end);

        Volatile.Write(ref _view, cells.Count == 0
            ? new RouteView(request.Target, [], 0f, "no way there")
            : new RouteView(request.Target, cells, Length(cells), string.Empty));
    }

    /// <summary>Forgets the current route, so the next area starts clean.</summary>
    public void Clear()
    {
        Request(RouteRequest.None);
        Volatile.Write(ref _view, RouteView.None);
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
