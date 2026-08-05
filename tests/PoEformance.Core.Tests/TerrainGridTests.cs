using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The walkable grid: how it is packed, and the outline the maphack draws from it.
/// </summary>
public class TerrainGridTests
{
    /// <summary>Builds a grid from rows of '.' (walkable) and '#' (solid).</summary>
    /// <remarks>
    /// Two cells per byte, even x in the low nibble - so the fixture packs it the same way
    /// the game does rather than testing against a convenient shape the reader never sees.
    /// </remarks>
    private static TerrainGrid Grid(params string[] rows)
    {
        int width = rows[0].Length;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (rows[y][x] != '.')
                {
                    continue;
                }

                int index = (y * stride) + (x / 2);
                cells[index] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
            }
        }

        return new TerrainGrid(cells, stride, rows.Length);
    }

    [Fact]
    public void TwoCellsPerByte_EvenInTheLowNibble()
    {
        // Getting the packing backwards produces a grid that is half right, which looks
        // like a plausible map and is not one.
        TerrainGrid grid = Grid(
            ".#..",
            "##.#");

        Assert.True(grid.IsWalkable(0, 0));
        Assert.False(grid.IsWalkable(1, 0));
        Assert.True(grid.IsWalkable(2, 0));
        Assert.True(grid.IsWalkable(3, 0));
        Assert.False(grid.IsWalkable(0, 1));
        Assert.True(grid.IsWalkable(2, 1));
        Assert.Equal(4, grid.Width);
        Assert.Equal(2, grid.Height);
    }

    [Fact]
    public void OutsideTheGridIsSolid()
    {
        // Load-bearing for the outline: without it every edge cell of the grid would count
        // as open ground and the level would have no border at all.
        TerrainGrid grid = Grid("..", "..");

        Assert.False(grid.IsWalkable(-1, 0));
        Assert.False(grid.IsWalkable(0, -1));
        Assert.False(grid.IsWalkable(2, 0));
        Assert.False(grid.IsWalkable(0, 2));
    }

    [Fact]
    public void TheOutlineIsTheWalkableSideOfTheBoundary()
    {
        // A room: the ring of floor next to the wall is marked, the middle is not. Drawn on
        // the wall side instead, the shape would be the rock rather than the room.
        TerrainGrid grid = Grid(
            "#####",
            "#...#",
            "#...#",
            "#...#",
            "#####");

        byte[] outline = grid.BuildOutline();
        bool Marked(int x, int y) => outline[(y * grid.Width) + x] != 0;

        Assert.True(Marked(1, 1));    // corner of the floor
        Assert.True(Marked(2, 1));    // along the top wall
        Assert.True(Marked(1, 2));    // along the left wall
        Assert.False(Marked(2, 2));   // the middle is open floor, not a boundary
        Assert.False(Marked(0, 0));   // the wall itself is never marked
    }

    [Fact]
    public void OpenGroundProducesNoOutlineExceptAtTheEdge()
    {
        TerrainGrid grid = Grid(
            "....",
            "....",
            "....");

        byte[] outline = grid.BuildOutline();

        // Everything on the border is a boundary, because outside is solid.
        Assert.Equal(1, outline[0]);
        Assert.Equal(1, outline[(1 * 4) + 3]);

        // ...and the one interior cell that touches no edge is not.
        Assert.Equal(0, outline[(1 * 4) + 1]);
    }

    [Fact]
    public void ASolidAreaMarksNothing()
    {
        // The state right after a zone change, when terrain has loaded but is all zeros.
        byte[] outline = Grid("####", "####").BuildOutline();
        Assert.All(outline, cell => Assert.Equal(0, cell));
    }
}
