using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Keeping a route to a chosen place as the player moves.
/// </summary>
public class RoutePlannerTests
{
    private const ulong Target = 0xABCD;

    private static TerrainGrid Grid(params string[] rows)
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

        return new TerrainGrid(cells, stride, rows.Length);
    }

    /// <summary>A snapshot holding just what a route needs: the terrain and the player.</summary>
    private static WorldSnapshot Snapshot(TerrainGrid grid, int playerCellX, int playerCellY, uint area = 1)
    {
        var player = new WorldEntity(
            0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player,
            playerCellX * MapView.WorldToGrid, playerCellY * MapView.WorldToGrid, 0f);

        return new WorldSnapshot(true, player, [player], new float[16], Terrain: grid, AreaHash: area);
    }

    private static RouteRequest To(int cellX, int cellY)
        => new(Target, cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid);

    [Fact]
    public void WithNoDestinationThereIsNoRoute()
    {
        var planner = new RoutePlanner();
        planner.Service(Snapshot(Grid("....", "...."), 0, 0), 1000);

        Assert.Empty(planner.View.Cells);
        Assert.Equal(0UL, planner.Target);
    }

    [Fact]
    public void ARouteRunsFromThePlayerToTheChosenPlace()
    {
        TerrainGrid grid = Grid(
            "..........",
            ".########.",
            "..........");

        var planner = new RoutePlanner();
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        RouteView route = planner.View;
        Assert.Equal(Target, route.Target);
        Assert.Equal((0, 0), route.Cells[0]);
        Assert.Equal((9, 2), route.Cells[^1]);

        // The length is what the picker shows, so it has to be a real distance rather than a
        // count of corners.
        Assert.True(route.LengthCells > 9f, $"length {route.LengthCells}");
    }

    [Fact]
    public void AnUnreachablePlaceSaysSoRatherThanDrawingNothing()
    {
        // "No route" and "still working it out" look identical on a map, and only one of them
        // is worth waiting through.
        TerrainGrid grid = Grid(
            "..........",
            "##########",
            "..........");

        var planner = new RoutePlanner();
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        Assert.Empty(planner.View.Cells);
        Assert.Equal("no way there", planner.View.Status);
    }

    [Fact]
    public void StandingStillDoesNotSearchAgain()
    {
        // A route is only wrong once its start has moved, so following the player by DISTANCE
        // keeps it correct while standing still costs nothing - which matters because this
        // shares a thread with the world read.
        TerrainGrid grid = Grid("..........", "..........", "..........");

        var planner = new RoutePlanner();
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        RouteView first = planner.View;
        planner.Service(Snapshot(grid, 0, 0), 2000);

        Assert.Same(first, planner.View);
    }

    [Fact]
    public void MovingFarEnoughSearchesAgain()
    {
        TerrainGrid grid = Grid(
            "..........",
            "..........",
            "..........");

        var planner = new RoutePlanner();
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);
        Assert.Equal((0, 0), planner.View.Cells[0]);

        planner.Service(Snapshot(grid, 8, 0), 2000);
        Assert.Equal((8, 0), planner.View.Cells[0]);
    }

    [Fact]
    public void ClearingForgetsTheRoute()
    {
        TerrainGrid grid = Grid("....", "....");

        var planner = new RoutePlanner();
        planner.Request(To(3, 1));
        planner.Service(Snapshot(grid, 0, 0), 1000);
        Assert.NotEmpty(planner.View.Cells);

        planner.Clear();
        Assert.Empty(planner.View.Cells);
        Assert.Equal(0UL, planner.Target);
    }

    [Fact]
    public void WithoutTerrainItWaitsInsteadOfGuessing()
    {
        // Terrain arrives well after an area does, and a route drawn before it lands would be
        // a straight line through whatever is in the way.
        var planner = new RoutePlanner();
        planner.Request(To(5, 5));

        var player = new WorldEntity(0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0, 0, 0);
        planner.Service(new WorldSnapshot(true, player, [player], new float[16]), 1000);

        Assert.Empty(planner.View.Cells);
        Assert.Equal("no terrain yet", planner.View.Status);
    }
}
