using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Why there is no route, when there is no route.
/// </summary>
/// <remarks>
/// This exists because of one screenshot: a boss arena correctly found at the far side of a
/// map, and under it the words "no way there" - which were not true. The route had never been
/// searched for at all; the destination was simply further than the search would attempt. One
/// message covering every kind of failure had turned "I did not look" into "it cannot be
/// reached", and there was no way to tell from the screen which had happened.
/// </remarks>
public class RouteOutcomeTests
{
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

    /// <summary>Open ground of a given size, with one sealed room in the bottom-right corner.</summary>
    private static TerrainGrid WithSealedCorner(int size, int room)
    {
        var rows = new string[size];
        for (int y = 0; y < size; y++)
        {
            var line = new char[size];
            for (int x = 0; x < size; x++)
            {
                bool inRoom = x >= size - room && y >= size - room;
                bool inWall = x >= size - room - 2 && y >= size - room - 2 && !inRoom;
                line[x] = inWall ? '#' : '.';
            }

            rows[y] = new string(line);
        }

        return Grid(rows);
    }

    [Fact]
    public void AFoundRouteSaysSo()
    {
        TerrainGrid grid = Grid("....", "....", "....");

        Assert.NotEmpty(TerrainPathfinder.FindPath(grid, (0, 0), (3, 2), out RouteOutcome outcome));
        Assert.Equal(RouteOutcome.Found, outcome);
    }

    [Fact]
    public void SomewhereTrulySealedOffIsUNREACHABLE()
    {
        // The only outcome that means the place cannot be walked to - and the only one where
        // the search actually looked at everything before saying so.
        TerrainGrid grid = WithSealedCorner(size: 40, room: 6);

        Assert.Empty(TerrainPathfinder.FindPath(grid, (2, 2), (37, 37), out RouteOutcome outcome));
        Assert.Equal(RouteOutcome.Unreachable, outcome);
    }

    [Fact]
    public void SomewhereBeyondTheSearchsReachIsTOOFAR()
    {
        TerrainGrid grid = Grid("....", "....", "....");

        Assert.Empty(TerrainPathfinder.FindPath(grid, (0, 0), (3, 2), out RouteOutcome outcome, maxDistance: 1));
        Assert.Equal(RouteOutcome.TooFar, outcome);
    }

    [Fact]
    public void GivingUpEarlyIsNEVERReportedAsUnreachable()
    {
        // The distinction the whole file is about. A search that stopped early knows nothing
        // about whether a way exists, and must not be allowed to claim it does.
        // Big enough that exhausting it takes more steps than the clock is checked after -
        // otherwise the search finishes first and the test proves nothing about the budget.
        TerrainGrid grid = WithSealedCorner(size: 150, room: 8);

        Assert.Empty(TerrainPathfinder.FindPath(grid, (2, 2), (146, 146), out RouteOutcome stopped, maxIterations: 20));
        Assert.Equal(RouteOutcome.GaveUp, stopped);

        Assert.Empty(TerrainPathfinder.FindPath(
            grid, (2, 2), (146, 146), out RouteOutcome timedOut, maxMilliseconds: 0));
        Assert.Equal(RouteOutcome.GaveUp, timedOut);
    }

    [Fact]
    public void AMarkerWithNoFloorAnywhereNearSaysThereIsNothingToStandOn()
    {
        TerrainGrid grid = Grid(
            ".....",
            ".....",
            ".....");

        Assert.Empty(TerrainPathfinder.FindPath(grid, (0, 0), (400, 400), out RouteOutcome outcome));
        Assert.Equal(RouteOutcome.NoGroundNearEnd, outcome);
    }

    [Fact]
    public void GroundNEARTheMarkerIsGoodEnough()
    {
        // A landmark is anchored at the middle of a terrain tile, and the middle of a tile can
        // easily be solid - a wall, a pillar, the structure the arena is built from. Without
        // the snap most markers would refuse a route for standing one cell inside one.
        TerrainGrid grid = Grid(
            ".....",
            ".....",
            "....#");

        Assert.NotEmpty(TerrainPathfinder.FindPath(grid, (0, 0), (4, 2), out RouteOutcome outcome));
        Assert.Equal(RouteOutcome.Found, outcome);
    }

    [Fact]
    public void ACrossMapDestinationIsSearchedForRatherThanRefused()
    {
        // The screenshot's actual fault. The old limit of 2500 cells was under the diagonal of
        // a real map, so the far end of one - which is exactly where its boss arena sits - was
        // refused before any search happened.
        Assert.True(TerrainPathfinder.DefaultMaxDistance > 3800, "a map's diagonal must be searchable");
    }

    [Fact]
    public void ThePlannerSaysWHICHFailureItWas()
    {
        TerrainGrid grid = Grid(
            ".....",
            ".....",
            ".....");

        var planner = new RoutePlanner(RoutePlanner.Immediate);
        planner.Request(new RouteRequest([new RouteTarget(1, 900f * MapView.WorldToGrid, 0f)]));

        var player = new WorldEntity(0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0f, 0f, 0f);
        planner.Service(
            new WorldSnapshot(true, player, [player], new float[16], Terrain: grid, AreaHash: 1), 1000);

        RouteView? route = planner.For(1);
        Assert.NotNull(route);

        // Off the edge of a five-cell grid there is no ground at all - and the message has to
        // be about THAT, not a flat claim that the place cannot be reached.
        Assert.Equal("nothing to stand on there", route.Status);
    }

    [Fact]
    public void TheSearchDoesNotHappenOnTheThreadThatAsksForIt()
    {
        // Not a detail. Service is called from the loop that reads the game AND drives
        // auto-flask, and a search across a real map was measured at about 1.8 seconds - so
        // doing it there would stop the flasks being watched for that whole time.
        int scheduled = 0;
        var planner = new RoutePlanner(work =>
        {
            scheduled++;
            work();
        });

        TerrainGrid grid = Grid("..........", "..........");
        planner.Request(new RouteRequest([new RouteTarget(1, 9f * MapView.WorldToGrid, MapView.WorldToGrid)]));

        var player = new WorldEntity(0, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0f, 0f, 0f);
        planner.Service(
            new WorldSnapshot(true, player, [player], new float[16], Terrain: grid, AreaHash: 1), 1000);

        Assert.Equal(1, scheduled);
    }
}
