using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The walkable route: that it exists, that it stays on the floor, and that it is readable.
/// </summary>
public class TerrainPathfinderTests
{
    /// <summary>Builds a grid from rows of '.' (walkable) and '#' (solid).</summary>
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

    private static void AssertOnFloor(TerrainGrid grid, List<(int X, int Y)> path)
    {
        Assert.NotEmpty(path);
        for (int i = 1; i < path.Count; i++)
        {
            Assert.True(
                TerrainPathfinder.IsLineClear(grid, path[i - 1], path[i]),
                $"segment {path[i - 1]} -> {path[i]} crosses something solid");
        }
    }

    [Fact]
    public void AnOpenRoomIsCrossedInAStraightLine()
    {
        // Smoothing's whole job: raw A* on a grid returns a step per cell, which is both
        // unreadable drawn and pointless to carry.
        TerrainGrid grid = Grid(
            "..........",
            "..........",
            "..........",
            "..........");

        List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, (0, 0), (9, 3));

        Assert.Equal((0, 0), path[0]);
        Assert.Equal((9, 3), path[^1]);
        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void AWallIsWalkedAround_NotThrough()
    {
        // A wall with one gap. Every segment of the answer has to stay on the floor, which is
        // checked directly rather than by counting corners - the route is allowed to look
        // however it likes as long as it is walkable.
        TerrainGrid grid = Grid(
            ".........",
            ".........",
            "####.####",
            ".........",
            ".........");

        List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, (0, 0), (8, 4));

        AssertOnFloor(grid, path);
        Assert.Equal((0, 0), path[0]);
        Assert.Equal((8, 4), path[^1]);

        // ...and it really did go through the gap.
        Assert.Contains(path, p => p.X is >= 3 and <= 5);
    }

    [Fact]
    public void ThereIsNoRouteThroughASealedWall()
    {
        // An unreachable target has to come back empty rather than with a route that cheats.
        TerrainGrid grid = Grid(
            ".........",
            ".........",
            "#########",
            ".........",
            ".........");

        Assert.Empty(TerrainPathfinder.FindPath(grid, (0, 0), (8, 4)));
    }

    [Fact]
    public void ADiagonalNeverSlipsThroughTheCornerOfAWall()
    {
        // The failure this prevents is subtle and confident: two walls meeting at a corner
        // leave a diagonal gap between their cells that A* will happily step through, and the
        // route then reads as walking through the join. Here the ONLY diagonal from (1,1) to
        // (2,2) is such a corner, and taking it would be the shortest answer.
        TerrainGrid grid = Grid(
            "....",
            "..#.",
            ".#..",
            "....");

        Assert.False(TerrainPathfinder.IsLineClear(grid, (1, 1), (2, 2)));

        List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, (1, 1), (2, 2));
        AssertOnFloor(grid, path);
    }

    [Fact]
    public void BothEndsSnapToWalkableGround()
    {
        // The player stands against walls constantly, and a point of interest is often placed
        // ON a terrain boundary rather than beside it. Refusing a blocked endpoint would
        // refuse most of the routes actually asked for.
        TerrainGrid grid = Grid(
            "#........",
            ".........",
            ".........",
            "........#");

        List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, (0, 0), (8, 3));

        AssertOnFloor(grid, path);
        Assert.True(grid.IsWalkable(path[0].X, path[0].Y));
        Assert.True(grid.IsWalkable(path[^1].X, path[^1].Y));
    }

    [Fact]
    public void TheNearestWalkableCellIsTheNEARESTOne()
    {
        // Searched ring by ring rather than as a filled square, or a corner two cells away is
        // returned before a neighbour directly ahead.
        TerrainGrid grid = Grid(
            "###",
            "#.#",
            "###");

        Assert.True(TerrainPathfinder.TryWalkableNear(grid, (1, 0), out (int X, int Y) found));
        Assert.Equal((1, 1), found);

        // Nothing walkable at all is an honest no rather than a guess.
        Assert.False(TerrainPathfinder.TryWalkableNear(Grid("###", "###"), (1, 1), out _));
    }

    [Fact]
    public void ATargetTooFarAwayIsNotSearchedFor()
    {
        // A cap on the QUESTION: a target across a big map is a search over most of it, for a
        // route nobody is looking at.
        var rows = new string[40];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string('.', 200);
        }

        TerrainGrid grid = Grid(rows);

        Assert.Empty(TerrainPathfinder.FindPath(grid, (0, 0), (199, 0), maxDistance: 50));
        Assert.NotEmpty(TerrainPathfinder.FindPath(grid, (0, 0), (40, 0), maxDistance: 50));
    }

    [Fact]
    public void TheLineTestAgreesWithTheSearchAboutCorners()
    {
        // These two have to apply the SAME rule, and the disagreement is one-directional and
        // therefore dangerous: the search refuses to cut a wall's corner, and then smoothing -
        // which uses the line test - would put the shortcut straight back in. Found by the
        // corner test above, which the first line test passed while being wrong: an exact
        // diagonal has no sample point between its ends, so sampling along it saw nothing.
        TerrainGrid corner = Grid(
            "....",
            "..#.",
            ".#..",
            "....");

        Assert.False(TerrainPathfinder.IsLineClear(corner, (1, 1), (2, 2)));
        Assert.False(TerrainPathfinder.IsLineClear(corner, (2, 2), (1, 1)));   // and both ways

        // An ordinary blocked line, and an ordinary clear one.
        TerrainGrid open = Grid(
            "..........",
            "..........",
            ".....#....",
            "..........");

        Assert.False(TerrainPathfinder.IsLineClear(open, (0, 3), (9, 1)));
        Assert.True(TerrainPathfinder.IsLineClear(open, (0, 0), (9, 0)));
    }

    [Fact]
    public void SmoothingKeepsTheRouteWalkable()
    {
        // Smoothing is allowed to drop corners, not to cut through anything.
        TerrainGrid grid = Grid(
            "..........",
            ".########.",
            "..........",
            ".########.",
            "..........");

        List<(int X, int Y)> path = TerrainPathfinder.FindPath(grid, (1, 0), (1, 4));

        AssertOnFloor(grid, path);
        Assert.True(path.Count < 12, $"still a staircase: {path.Count} points");
    }

    [Fact]
    public void StandingOnTheTargetIsASinglepoint()
    {
        TerrainGrid grid = Grid("....", "....");
        Assert.Equal([(2, 1)], TerrainPathfinder.FindPath(grid, (2, 1), (2, 1)));
    }
}

/// <summary>
/// Routes must not step between storeys.
/// </summary>
/// <remarks>
/// THE WALKABLE GRID IS FLAT. A bridge and the ground beneath it occupy the same cells and
/// both read as walkable, so a two-dimensional search steps between them and draws a route
/// that walks through a floor - which is the one kind of wrong route that looks entirely
/// plausible on screen. Height is the only thing that separates the two.
///
/// The allowance is thirty units a cell, taken from the AutoHotkey tool where it is in daily
/// use. What is checked here is what that number has to buy: ramps and uneven ground pass, a
/// storey seam does not, and an area with no heights behaves exactly as it did before.
/// </remarks>
public class RoutesAcrossStoreysTests
{
    /// <summary>Open ground, whole tiles, with a height per tile.</summary>
    private static TerrainGrid Ground(int tilesX, int tilesY, float[]? heights = null)
    {
        int width = tilesX * TerrainGrid.CellsPerTile;
        int height = tilesY * TerrainGrid.CellsPerTile;
        int stride = (width + 1) / 2;

        var cells = new byte[stride * height];
        Array.Fill(cells, (byte)0x11);

        return new TerrainGrid(cells, stride, height, tilesX, tilesY, heights);
    }

    /// <summary>Five tiles in a row, the middle one lifted by the given amount.</summary>
    private static TerrainGrid WithMiddleAt(float lifted)
        => Ground(5, 1, [0f, 0f, lifted, 0f, 0f]);

    private const int Left = 5;
    private static readonly int Right = (5 * TerrainGrid.CellsPerTile) - 6;

    [Fact]
    public void ARampIsWalkedUp()
    {
        // Ordinary uneven ground, a staircase, a slope. All of it has to stay passable, and
        // being too strict here is the worse failure of the two: a route that does exist and
        // is refused sends somebody looking for a way that is already under their feet.
        List<(int X, int Y)> path = TerrainPathfinder.FindPath(
            WithMiddleAt(20f), (Left, 11), (Right, 11), out RouteOutcome outcome);

        Assert.Equal(RouteOutcome.Found, outcome);
        Assert.NotEmpty(path);
    }

    [Fact]
    public void ASTOREYSeamIsNot()
    {
        // The same walkable cells, four hundred units apart. Nothing about the walkable data
        // distinguishes this from the ramp above - only the height does.
        TerrainPathfinder.FindPath(WithMiddleAt(400f), (Left, 11), (Right, 11), out RouteOutcome outcome);

        Assert.Equal(RouteOutcome.Unreachable, outcome);
    }

    [Fact]
    public void AnAreaWithNoHeightsBehavesExactlyAsBefore()
    {
        // The read is allowed to fail - a missing pointer, a drifted offset - and when it does
        // every cell reads as zero. The rule would pass every step anyway; what matters is
        // that routing does not quietly change character depending on whether a read worked.
        TerrainGrid flat = Ground(5, 1);

        Assert.False(flat.HasHeights);
        TerrainPathfinder.FindPath(flat, (Left, 11), (Right, 11), out RouteOutcome outcome);

        Assert.Equal(RouteOutcome.Found, outcome);
    }

    [Fact]
    public void TheAllowanceIsTheONEThatSeparatesThoseTwo()
    {
        // Pinning the number rather than trusting the two cases above to have straddled it.
        // Just under passes, just over does not - so a change to it is a decision somebody
        // makes rather than something that drifts.
        TerrainPathfinder.FindPath(
            WithMiddleAt(TerrainPathfinder.MaxClimbPerCell - 1f), (Left, 11), (Right, 11), out RouteOutcome under);
        TerrainPathfinder.FindPath(
            WithMiddleAt(TerrainPathfinder.MaxClimbPerCell + 1f), (Left, 11), (Right, 11), out RouteOutcome over);

        Assert.Equal(RouteOutcome.Found, under);
        Assert.Equal(RouteOutcome.Unreachable, over);
    }

    [Fact]
    public void AndAStepDOWNIsRefusedToo()
    {
        // Symmetric on purpose, and it has to be shown by a route that ONLY falls. A shape
        // that drops and then climbs back is caught by a climb-only rule as well, so it
        // proves nothing - and a route allowed to step OFF a storey but not onto one is
        // still drawn through a floor, just in one direction.
        TerrainGrid grid = Ground(5, 1, [400f, 400f, 400f, 0f, 0f]);

        TerrainPathfinder.FindPath(grid, (Left, 11), (Right, 11), out RouteOutcome outcome);

        Assert.Equal(RouteOutcome.Unreachable, outcome);
    }
}
