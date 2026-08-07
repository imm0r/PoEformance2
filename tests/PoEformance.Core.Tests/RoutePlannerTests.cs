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

    private static RouteRequest To(int cellX, int cellY, ulong target = Target)
        => new([new RouteTarget(target, cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid)]);

    [Fact]
    public void WithNoDestinationThereIsNoRoute()
    {
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Service(Snapshot(Grid("....", "...."), 0, 0), 1000);

        Assert.Empty(planner.Routes);
        Assert.Empty(planner.Targets);
    }

    [Fact]
    public void ARouteRunsFromThePlayerToTheChosenPlace()
    {
        TerrainGrid grid = Grid(
            "..........",
            ".########.",
            "..........");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        RouteView route = Assert.Single(planner.Routes);
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

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        RouteView route = Assert.Single(planner.Routes);
        Assert.Empty(route.Cells);
        Assert.Equal("no way there", route.Status);
    }

    [Fact]
    public void StandingStillDoesNotSearchAgain()
    {
        // A route is only wrong once its start has moved, so following the player by DISTANCE
        // keeps it correct while standing still costs nothing - which matters because this
        // shares a thread with the world read.
        TerrainGrid grid = Grid("..........", "..........", "..........");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(9, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);

        IReadOnlyList<RouteView> first = planner.Routes;
        planner.Service(Snapshot(grid, 0, 0), 2000);

        Assert.Same(first, planner.Routes);
    }

    [Fact]
    public void MovingFarEnoughSearchesAgain()
    {
        // Twenty wide so that the destination stays a destination: the player walks to cell
        // 8 here, and anything within four cells of the end would be reached rather than
        // re-planned - a different behaviour, tested next door.
        TerrainGrid grid = Grid(
            "....................",
            "....................",
            "....................");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(19, 2));
        planner.Service(Snapshot(grid, 0, 0), 1000);
        Assert.Equal((0, 0), planner.Routes[0].Cells[0]);

        planner.Service(Snapshot(grid, 8, 0), 2000);
        Assert.Equal((8, 0), planner.Routes[0].Cells[0]);
    }

    [Fact]
    public void ClearingForgetsTheRoute()
    {
        TerrainGrid grid = Grid("..........", "..........");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(9, 1));
        planner.Service(Snapshot(grid, 0, 0), 1000);
        Assert.NotEmpty(planner.Routes);

        planner.Clear();
        Assert.Empty(planner.Routes);
        Assert.Empty(planner.Targets);
    }

    [Fact]
    public void SeveralPlacesAreRoutedToAtOnce()
    {
        // The reason for having more than one: which exit is actually closer THROUGH the
        // walls is a question a straight line cannot answer, and two drawn routes answer at
        // a glance.
        TerrainGrid grid = Grid(
            "..........",
            ".########.",
            "..........");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Toggle(0xAAAA, 9 * MapView.WorldToGrid, 0);
        planner.Toggle(0xBBBB, 9 * MapView.WorldToGrid, 2 * MapView.WorldToGrid);
        planner.Service(Snapshot(grid, 0, 0), 1000);

        Assert.Equal(2, planner.Routes.Count);
        Assert.Equal((9, 0), planner.For(0xAAAA)!.Cells[^1]);
        Assert.Equal((9, 2), planner.For(0xBBBB)!.Cells[^1]);

        // The one that has to go around the wall is the longer walk, though the straight-line
        // distance is nearly the same - which is the whole point of drawing it.
        Assert.True(planner.For(0xBBBB)!.LengthCells > planner.For(0xAAAA)!.LengthCells);
    }

    [Fact]
    public void ChoosingAPlaceAgainDropsIt()
    {
        TerrainGrid grid = Grid("....", "....");
        var planner = new RoutePlanner(RoutePlanner.Immediate);

        planner.Toggle(0xAAAA, 3 * MapView.WorldToGrid, 0);
        Assert.True(planner.IsTarget(0xAAAA));

        planner.Toggle(0xAAAA, 3 * MapView.WorldToGrid, 0);
        Assert.False(planner.IsTarget(0xAAAA));
    }

    [Fact]
    public void PastTheLimitTheOldestGivesWay()
    {
        // Every route is its own search, re-run whenever the player moves, so the count is a
        // bound on WORK and not just on clutter. Dropping the oldest keeps a click doing
        // something rather than silently refusing.
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        for (int i = 0; i < RoutePlanner.MaxRoutes + 2; i++)
        {
            planner.Toggle((ulong)(0x1000 + i), i * MapView.WorldToGrid, 0);
        }

        Assert.Equal(RoutePlanner.MaxRoutes, planner.Targets.Count);
        Assert.False(planner.IsTarget(0x1000));                       // the first one went
        Assert.True(planner.IsTarget((ulong)(0x1000 + RoutePlanner.MaxRoutes + 1)));
    }

    [Fact]
    public void ChoosingAnotherPlaceLeavesTheOthersInOrder()
    {
        // Order is what the colours are assigned from, so a new route must not renumber the
        // existing ones - a line that changed colour when a neighbour was added would make
        // the map lie about which route leads where.
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Toggle(0xAAAA, 0, 0);
        planner.Toggle(0xBBBB, 0, 0);
        planner.Toggle(0xCCCC, 0, 0);

        planner.Toggle(0xBBBB, 0, 0);   // drop the middle one

        Assert.Equal([0xAAAA, 0xCCCC], planner.Targets.Select(t => t.Target));
    }

    [Fact]
    public void WithoutTerrainItWaitsInsteadOfGuessing()
    {
        // Terrain arrives well after an area does, and a route drawn before it lands would be
        // a straight line through whatever is in the way.
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(To(5, 5));

        var player = new WorldEntity(0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0, 0, 0);
        planner.Service(new WorldSnapshot(true, player, [player], new float[16]), 1000);

        Assert.Equal("no terrain yet", Assert.Single(planner.Routes).Status);
    }
}

/// <summary>
/// Destinations belong to the map they were chosen in.
/// </summary>
/// <remarks>
/// Written from a live report: points of interest turning up from a map already left, and only
/// on the ones that had been given an arrow. Nothing but the "clear all" button ever emptied
/// the list, so a destination survived every zone - as an address the game had since handed
/// out to something else, and a position somewhere in a map no longer loaded.
/// </remarks>
public class RouteAreaTests
{
    private static TerrainGrid Ground()
    {
        var cells = new byte[8 * 8];
        Array.Fill(cells, (byte)0x11);
        return new TerrainGrid(cells, 8, 8);
    }

    private static WorldSnapshot In(uint area)
    {
        var player = new WorldEntity(
            0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0f, 0f, 0f);

        return new WorldSnapshot(true, player, [player], new float[16], Terrain: Ground(), AreaHash: area);
    }

    private static RoutePlanner Routed(uint area)
    {
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Service(In(area), 1000);
        planner.Request(new RouteRequest([new RouteTarget(0xABCD, 32f, 32f)]));
        planner.Service(In(area), 1100);

        Assert.Single(planner.Targets);
        return planner;
    }

    [Fact]
    public void LEAVINGTheAreaForgetsWhatWasChosenInIt()
    {
        RoutePlanner planner = Routed(1);

        planner.Service(In(2), 2000);

        Assert.Empty(planner.Targets);
        Assert.Empty(planner.Routes);
    }

    [Fact]
    public void STAYINGKeepsThem()
    {
        // The case that must not regress: this runs on every read, and clearing on a tick
        // would make choosing a place impossible rather than merely wrong.
        RoutePlanner planner = Routed(1);

        planner.Service(In(1), 2000);
        planner.Service(In(1), 3000);

        Assert.Single(planner.Targets);
    }

    [Fact]
    public void ALoadingScreenIsNotANewArea()
    {
        // The hash reads 0 between zones. Treating that as an area would wipe the destinations
        // of the map being loaded into, on the frame before it finished loading.
        RoutePlanner planner = Routed(1);

        planner.Service(In(0), 2000);

        Assert.Single(planner.Targets);
    }

    [Fact]
    public void ANDTheChangeIsSeenEvenAcrossOne()
    {
        // Two real areas are rarely next to each other in the readings - there is a loading
        // screen between them - so the comparison has to skip the zeroes rather than use the
        // last value seen.
        RoutePlanner planner = Routed(1);

        planner.Service(In(0), 2000);
        planner.Service(In(2), 3000);

        Assert.Empty(planner.Targets);
    }

    [Fact]
    public void COMINGBackToTheSameInstanceForgetsThemToo()
    {
        // Its hash is unchanged, but leaving reloaded the area: the addresses that were chosen
        // no longer point at what was chosen. Keeping the arrows would be the same bug wearing
        // the same hash.
        RoutePlanner planner = Routed(1);

        planner.Service(In(2), 2000);   // town
        planner.Service(In(1), 3000);   // back through the portal

        Assert.Empty(planner.Targets);
    }
}

/// <summary>
/// Getting there is what finishes a destination.
/// </summary>
/// <remarks>
/// Asked for after the area-change fix, and it closes the same hole from the other side:
/// without it the arrows only ever accumulate until somebody clears them by hand, and by then
/// they point at places already visited.
/// </remarks>
public class RouteArrivalTests
{
    private static TerrainGrid Ground(int size = 40)
    {
        int stride = (size + 1) / 2;
        var cells = new byte[stride * size];
        Array.Fill(cells, (byte)0x11);
        return new TerrainGrid(cells, stride, size);
    }

    private static WorldSnapshot At(int cellX, int cellY)
    {
        var player = new WorldEntity(
            0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player,
            cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid, 0f);

        return new WorldSnapshot(true, player, [player], new float[16], Terrain: Ground(), AreaHash: 1);
    }

    private static RouteTarget To(ulong id, int cellX, int cellY)
        => new(id, cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid);

    private static RoutePlanner Walking(params RouteTarget[] targets)
    {
        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Service(At(0, 0), 1000);
        planner.Request(new RouteRequest([.. targets]));
        planner.Service(At(0, 0), 1100);
        return planner;
    }

    [Fact]
    public void STANDINGAtAPlaceTakesItOffTheList()
    {
        RoutePlanner planner = Walking(To(0xAA, 30, 0));
        Assert.Single(planner.Targets);

        planner.Service(At(30, 0), 2000);

        Assert.Empty(planner.Targets);
        Assert.Empty(planner.Routes);
    }

    [Fact]
    public void NEARENOUGHCounts()
    {
        // A destination is a marker's position, and chests and portals are reached from beside
        // them rather than from on top of them. An arrow that will not go away until you stand
        // on an exact point is worse than one that goes slightly early.
        RoutePlanner planner = Walking(To(0xAA, 30, 0));

        planner.Service(At(30 - (int)RoutePlanner.ArrivedWithinCells + 1, 0), 2000);

        Assert.Empty(planner.Targets);
    }

    [Fact]
    public void BUTWalkingPastAtADistanceDoesNot()
    {
        RoutePlanner planner = Walking(To(0xAA, 30, 0));

        planner.Service(At(30 - (int)RoutePlanner.ArrivedWithinCells - 2, 0), 2000);

        Assert.Single(planner.Targets);
    }

    [Fact]
    public void ONLYTheOneReachedIsDropped()
    {
        // The radius has to be small enough that arriving at one place does not tick off its
        // neighbour, which is the failure that would be hardest to notice: the arrow to the
        // thing you have NOT been to disappears.
        RoutePlanner planner = Walking(To(0xAA, 30, 0), To(0xBB, 0, 30), To(0xCC, 30, 30));
        Assert.Equal(3, planner.Targets.Count);

        planner.Service(At(30, 0), 2000);

        Assert.Equal(2, planner.Targets.Count);
        Assert.DoesNotContain(planner.Targets, target => target.Target == 0xAA);
        Assert.Contains(planner.Targets, target => target.Target == 0xBB);
        Assert.Contains(planner.Targets, target => target.Target == 0xCC);
    }

    [Fact]
    public void ANDTheRouteToItGoesWithIt()
    {
        // Not just the list. A route left behind would keep drawing a line to somewhere the
        // player is standing until the next plan happened to overwrite it.
        RoutePlanner planner = Walking(To(0xAA, 30, 0), To(0xBB, 0, 30));
        Assert.NotNull(planner.For(0xAA));

        planner.Service(At(30, 0), 2000);

        Assert.Null(planner.For(0xAA));
        Assert.NotNull(planner.For(0xBB));
    }

    [Fact]
    public void EVENBeforeTheNextSearchHasFinished()
    {
        // The version of the test above that can actually fail. Everywhere else here runs the
        // search inline, so the re-plan overwrites the routes in the same call and hides
        // whether arriving removed anything; in the game the search is on another thread and
        // takes up to seconds, and the line would be drawn across the screen for all of it.
        //
        // A scheduler that never runs the work is what that looks like from the outside.
        var searches = new List<Action>();
        var planner = new RoutePlanner(searches.Add);

        planner.Service(At(0, 0), 1000);
        planner.Request(new RouteRequest([To(0xAA, 30, 0), To(0xBB, 0, 30)]));
        planner.Service(At(0, 0), 1100);

        Assert.Single(searches);
        searches[0]();          // the one search that did finish, before the player set off
        searches.Clear();

        Assert.NotNull(planner.For(0xAA));

        planner.Service(At(30, 0), 2000);

        Assert.Null(planner.For(0xAA));
        Assert.NotNull(planner.For(0xBB));
    }
}
