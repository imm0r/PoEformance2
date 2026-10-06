using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>The planner's pathfinder: bounded reach, full length, stepping along a path, doors, the memo.</summary>
public class ExpeditionPathsTests
{
    /// <summary>Builds a grid from rows of '.' (walkable) and '#' (solid).</summary>
    internal static TerrainGrid Grid(params string[] rows)
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

    /// <summary>An open field, width by height, every cell walkable.</summary>
    internal static TerrainGrid Field(int width, int height)
    {
        int stride = (width + 1) / 2;
        var cells = new byte[stride * height];
        Array.Fill(cells, (byte)0x11);
        return new TerrainGrid(cells, stride, height);
    }

    [Fact]
    public void ReachAnswersWithinTheHop_AndRefusesBeyondIt()
    {
        var paths = new ExpeditionPaths(Field(200, 20), null, hop: 50f);

        float near = paths.Reach(new Vector2(10, 10), new Vector2(40, 10));
        Assert.Equal(30f, near, 3);

        Assert.Equal(-1f, paths.Reach(new Vector2(10, 10), new Vector2(120, 10)));

        // The full length still answers for the far one.
        Assert.Equal(110f, paths.Length(new Vector2(10, 10), new Vector2(120, 10)), 3);

        // The memo: the same question again costs no search.
        long searches = paths.Searches;
        paths.Reach(new Vector2(10, 10), new Vector2(40, 10));
        Assert.Equal(searches, paths.Searches);
        Assert.True(paths.Hits > 0);
    }

    [Fact]
    public void TheLengthGoesRoundAWall_AndNoWayIsMinusOne()
    {
        TerrainGrid grid = Grid(
            "..........",
            "....#.....",
            "....#.....",
            "....#.....",
            "..........");
        var paths = new ExpeditionPaths(grid, null, hop: 100f);

        float length = paths.Length(new Vector2(2, 2), new Vector2(7, 2));
        Assert.True(length > 5f, $"a path through a wall: {length}");

        TerrainGrid walled = Grid(
            "....#.....",
            "....#.....",
            "....#.....",
            "....#.....",
            "....#.....");
        var apart = new ExpeditionPaths(walled, null, hop: 100f);
        Assert.Equal(-1f, apart.Length(new Vector2(1, 2), new Vector2(8, 2)));
        Assert.Equal(-1f, apart.Reach(new Vector2(1, 2), new Vector2(8, 2)));

        // A doorway opened through the wall joins the two halves.
        var door = new ExpeditionPaths(walled, [(4, 2)], hop: 100f);
        Assert.True(door.Length(new Vector2(1, 2), new Vector2(8, 2)) > 0f);
        Assert.True(door.IsWalkable(new Vector2(4, 2)));
        Assert.False(apart.IsWalkable(new Vector2(4, 2)));
    }

    [Fact]
    public void StepTowardStopsAtTheDistance_OnWalkableGround()
    {
        var paths = new ExpeditionPaths(Field(200, 20), null, hop: 50f);

        Assert.True(paths.StepToward(new Vector2(10, 10), new Vector2(150, 10), 40f, out Vector2 step));
        Assert.Equal(50f, step.X, 1);
        Assert.Equal(10f, step.Y, 1);

        // Already there: nothing to step.
        Assert.False(paths.StepToward(new Vector2(10, 10), new Vector2(10.5f, 10), 40f, out _));

        // Without a grid, the straight line.
        var free = new ExpeditionPaths(null, null, hop: 50f);
        Assert.True(free.StepToward(new Vector2(0, 0), new Vector2(100, 0), 30f, out Vector2 straight));
        Assert.Equal(30f, straight.X, 3);
        Assert.Equal(60f, free.Length(new Vector2(0, 0), new Vector2(60, 0)), 3);
        Assert.Equal(-1f, free.Reach(new Vector2(0, 0), new Vector2(60, 0)));
        Assert.True(free.HasGrid == false);
    }

    [Fact]
    public void EndpointsOffTheFloorAreSnappedToIt()
    {
        TerrainGrid grid = Grid(
            "##########",
            "#........#",
            "#........#",
            "##########");
        var paths = new ExpeditionPaths(grid, null, hop: 100f);

        // Both ends on the wall: snapped to the floor beside them.
        float length = paths.Length(new Vector2(0, 0), new Vector2(9, 3));
        Assert.True(length > 0f);
    }
}
