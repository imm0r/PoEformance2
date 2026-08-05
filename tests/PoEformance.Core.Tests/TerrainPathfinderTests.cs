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
