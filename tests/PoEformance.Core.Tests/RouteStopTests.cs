using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Stops on the way, and destinations bigger than a point: what ctrl + shift + click and a room outline's ctrl + click hand the planner.
/// </summary>
public class RouteStopTests
{
    private const ulong Target = 0xABCD;
    private const float W = MapView.WorldToGrid;

    private static TerrainGrid Grid(int tilesX, int tilesY, params string[] rows)
    {
        int width = rows[0].Length;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * rows.Length];
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (rows[y][x] == '.')
                {
                    cells[(y * stride) + (x / 2)] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
                }
            }
        }

        return new TerrainGrid(cells, stride, rows.Length, tilesX, tilesY);
    }

    private static TerrainGrid Open(int width, int height, int tilesX = 0, int tilesY = 0)
        => Grid(tilesX, tilesY, [.. Enumerable.Repeat(new string('.', width), height)]);

    private static WorldSnapshot Snapshot(TerrainGrid grid, int playerCellX, int playerCellY)
    {
        var player = new WorldEntity(
            0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, playerCellX * W, playerCellY * W, 0f);
        return new WorldSnapshot(true, player, [player], new float[16], Terrain: grid, AreaHash: 1);
    }

    private static Vector2 At(int cellX, int cellY) => new(cellX * W, cellY * W);

    /// <summary>The line runs from the player to the stop, then on to the destination.</summary>
    [Fact]
    public void AROUTEWithAStopRunsThroughIt()
    {
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(new([new RouteTarget(Target, 9 * W, 0f) { Via = [At(5, 4)] }]));
        planner.Service(Snapshot(Open(10, 5), 0, 0), 1000);

        RouteView route = Assert.Single(planner.Routes);
        Assert.Equal((0, 0), route.Cells[0]);
        Assert.Contains((5, 4), route.Cells);
        Assert.Equal((9, 0), route.Cells[^1]);
        Assert.True(route.LengthCells > 12f, $"length {route.LengthCells}");
    }

    /// <summary>Standing at a stop drops it and every stop before it - walked past is walked past.</summary>
    [Fact]
    public void ASTOPReachedGoesAndTakesTheOnesBeforeItWithIt()
    {
        TerrainGrid grid = Open(12, 6);
        RouteRequest twoStops = new([new RouteTarget(Target, 11 * W, 0f) { Via = [At(3, 5), At(8, 5)] }]);

        var first = new RoutePlanner(RoutePlanner.Immediate);
        first.Request(twoStops);
        first.Service(Snapshot(grid, 3, 5), 1000);
        Assert.Equal([At(8, 5)], Assert.Single(first.Targets).Via);

        var second = new RoutePlanner(RoutePlanner.Immediate);
        second.Request(twoStops);
        second.Service(Snapshot(grid, 8, 5), 1000);
        Assert.Empty(Assert.Single(second.Targets).Via);
    }

    /// <summary>A stop walled off says which stop it is, rather than drawing a line that stops short.</summary>
    [Fact]
    public void ASTOPItCannotReachIsNamed()
    {
        TerrainGrid grid = Grid(0, 0,
            "..........",
            "##########",
            "..........");
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(new([new RouteTarget(Target, 9 * W, 0f) { Via = [At(5, 2)] }]));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        RouteView route = Assert.Single(planner.Routes);
        Assert.Empty(route.Cells);
        Assert.EndsWith("to stop 1", route.Status, StringComparison.Ordinal);
    }

    /// <summary>A stop goes on the route chosen last, up to the limit; with no route it is a route to the point itself.</summary>
    [Fact]
    public void ASTOPGoesOnTheNewestRouteOrStartsOne()
    {
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.AddVia(30f, 40f);
        RouteTarget point = Assert.Single(planner.Targets);
        Assert.Equal((30f, 40f), (point.WorldX, point.WorldY));
        Assert.Empty(point.Via);

        planner.Toggle(1, 100f, 100f);
        for (var one = 0; one <= RoutePlanner.MaxStops; one++)
        {
            planner.AddVia(one, one);
        }

        Assert.Empty(planner.Targets[0].Via);
        Assert.Equal(RoutePlanner.MaxStops, planner.Targets[1].Via.Count);
        Assert.Equal(Vector2.Zero, planner.Targets[1].Via[0]);
    }

    /// <summary>A room is arrived at on its first tile, far from where its line ends; next door it is not.</summary>
    [Fact]
    public void AROOMIsReachedOnItsFirstTile()
    {
        TerrainGrid grid = Open(46, 23, tilesX: 2, tilesY: 1);
        var room = new RouteTarget(Target, 40 * W, 10 * W) { Zone = new RouteZone(2, [1]) };

        var next = new RoutePlanner(RoutePlanner.Immediate);
        next.Toggle(room);
        next.Service(Snapshot(grid, 20, 5), 1000);
        Assert.Single(next.Targets);

        var inside = new RoutePlanner(RoutePlanner.Immediate);
        inside.Toggle(room);
        inside.Service(Snapshot(grid, 24, 5), 1000);
        Assert.Empty(inside.Targets);
    }

    /// <summary>A room's line ends on its own walkable ground - its middle tile blocked, the nearest walkable cell of the room - and its zone is its tiles.</summary>
    [Fact]
    public void AROOMSLineEndsOnItsOwnGround()
    {
        const int Side = 3 * TerrainGrid.CellsPerTile;
        string[] rows = [.. Enumerable.Range(0, Side).Select(y => new string([.. Enumerable.Range(0, Side)
            .Select(x => x / TerrainGrid.CellsPerTile == 1 && y / TerrainGrid.CellsPerTile == 1 ? '#' : '.')]))];
        TerrainGrid grid = Grid(3, 3, rows);
        var where = new RoomCandidate(0, 0, 0, 3, 3, 10, 10);

        RouteTarget target = Assert.IsType<RouteTarget>(RoomRoute.For(grid, "rooms/a.arm", null, where));
        int cellX = (int)(target.WorldX / W);
        int cellY = (int)(target.WorldY / W);
        Assert.True(grid.IsWalkable(cellX, cellY));
        Assert.Equal(9, target.Zone!.Count);
        Assert.True(target.Zone.Holds(target.WorldX, target.WorldY));
        Assert.False(target.Zone.Holds(Side * W + W, 5 * W));

        // The same room at the same place is the same route; it is not the map's own room-name route.
        Assert.Equal(target.Target, RoomRoute.IdFor("ROOMS/A.ARM", where));
        Assert.NotEqual(TerrainRooms.IdFor("rooms/a.arm", 0, 0), target.Target);
    }

    /// <summary>A room with no ground to stand on gets no route.</summary>
    [Fact]
    public void AROOMOfNoGroundGetsNoRoute()
        => Assert.Null(RoomRoute.For(
            Grid(1, 1, [.. Enumerable.Repeat(new string('#', TerrainGrid.CellsPerTile), TerrainGrid.CellsPerTile)]),
            "rooms/a.arm",
            null,
            new RoomCandidate(0, 0, 0, 1, 1, 10, 10)));
}
