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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    public void ThicknessIsTheLineWidthInPixels(int thickness, int expectedWidth)
    {
        // The setting says 1 to 6, so it had better mean 1 to 6. Grown as a RADIUS around
        // each pixel it can only make odd widths - 1, 3, 5 - and the scale then skips the
        // value most people actually want, which is exactly how it was reported: one too
        // thin and two already too thick, with nothing in between because there was nothing.
        //
        // A single walkable column in a solid field: its outline is that one column, so the
        // widened line's width is measurable directly.
        var rows = new string[21];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string('#', 10) + "." + new string('#', 10);
        }

        OutlineMask mask = TerrainOutline.Build(Grid(rows), maxEdge: 64, thickness);

        int width = 0;
        for (int x = 0; x < mask.Width; x++)
        {
            if (mask.IsSet(x, 10))
            {
                width++;
            }
        }

        Assert.Equal(expectedWidth, width);
    }

    [Fact]
    public void TheHeightShiftMovesACellInThePicture_AndOnlyWhenAskedFor()
    {
        // The overlay draws the outline as one FLAT quad and gets the heights from the
        // picture, where each cell has been moved diagonally by half its height. The config
        // page draws the same outline from directly above, where a height means nothing and
        // the same shift would only skew the map - so it is a choice, not a property.
        int cells = TerrainGrid.CellsPerTile;
        int width = 2 * cells;
        int stride = (width + 1) / 2;

        var packed = new byte[stride * width];
        Array.Fill(packed, (byte)0x11);   // all walkable, so the border is the outline

        // One tile raised, the rest at ground level.
        var heights = new float[] { -242f, 0f, 0f, 0f };
        var grid = new TerrainGrid(packed, stride, width, 2, 2, heights);

        int shift = grid.IsoHeightShift(0, 0);
        Assert.True(shift < 0);

        OutlineMask flatDrawn = TerrainOutline.Build(grid, maxEdge: 4096, thickness: 1);
        OutlineMask shifted = TerrainOutline.Build(grid, maxEdge: 4096, thickness: 1, isoHeightShift: true);

        // The corner cell is on the boundary. Without the shift it is drawn where it is;
        // with it, exactly its own height away, on both axes.
        Assert.True(flatDrawn.IsSet(0, 0));
        Assert.False(shifted.IsSet(0, 0));
        Assert.True(shifted.IsSet(-shift, -shift));

        // ...and a cell on flat ground does not move either way.
        int far = width - 1;
        Assert.True(flatDrawn.IsSet(far, far));
        Assert.True(shifted.IsSet(far, far));
    }

    [Theory]
    [InlineData(1, 0f)]     // odd widths sit ON the boundary
    [InlineData(3, 0f)]
    [InlineData(5, 0f)]
    [InlineData(2, 0.5f)]   // even ones cannot, and lean by half a pixel
    [InlineData(4, 0.5f)]
    public void AnEvenWidthLeansOffTheBoundaryAndSaysBySoMuch(int thickness, float expectedLeanPixels)
    {
        // A line of even width has to take one more pixel on one side than the other, so its
        // centre lands half a pixel off the boundary it describes - in a FIXED direction,
        // everywhere on the map, whatever the distance from the player. That is a real
        // displacement of the drawn line rather than a rounding detail: at a thinning step of
        // two it is a whole grid cell, and it reads as the outline sitting beside the game's
        // own line rather than on it.
        //
        // Measured as the marked band's centre of mass, which is what an eye comparing it to
        // a thin line actually sees, against the single column the outline occupies.
        var rows = new string[21];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string('#', 10) + "." + new string('#', 10);
        }

        OutlineMask mask = TerrainOutline.Build(Grid(rows), maxEdge: 64, thickness);

        double sum = 0;
        int count = 0;
        for (int x = 0; x < mask.Width; x++)
        {
            if (mask.IsSet(x, 10))
            {
                sum += x;
                count++;
            }
        }

        Assert.True(count > 0);
        Assert.Equal(10 + expectedLeanPixels, sum / count, 3);

        // ...and it is reported in GRID CELLS, because that is what the drawing works in.
        Assert.Equal(expectedLeanPixels * mask.Step, mask.LeanCells, 3);
    }

    /// <summary>A grid of whole tiles with exactly one walkable cell, at the given coordinates.</summary>
    private static TerrainGrid OneWalkableCell(int tilesX, int tilesY, int cellX, int cellY)
    {
        int width = tilesX * TerrainGrid.CellsPerTile;
        int height = tilesY * TerrainGrid.CellsPerTile;
        var rows = new string[height];
        for (int y = 0; y < height; y++)
        {
            rows[y] = y == cellY
                ? new string('#', cellX) + "." + new string('#', width - cellX - 1)
                : new string('#', width);
        }

        return Grid(rows);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(22, 22, 0)]     // the tile's last cell, at an odd x
    [InlineData(23, 23, 3)]     // the NEXT tile's first cell - and the SAME byte as the one above
    [InlineData(45, 45, 3)]
    public void OneWalkableCellMarksItsOwnTileAndNoOther(int cellX, int cellY, int expected)
    {
        // The boundary cases are the point. A tile is 23 cells across and a byte holds two, so
        // every odd tile boundary lands MID-BYTE: cells 22 and 23 share one byte and belong to
        // different tiles. Marking "the tile this byte is in" would let a neighbour's edge cell
        // answer for this one, which is why the sweep maps each NIBBLE to its own tile.
        bool[] mask = OneWalkableCell(2, 2, cellX, cellY).WalkableTileMask();

        Assert.Equal(4, mask.Length);
        for (int tile = 0; tile < mask.Length; tile++)
        {
            Assert.Equal(tile == expected, mask[tile]);
        }
    }

    [Fact]
    public void AnAreaWithNothingWalkableMarksNothing()
    {
        // Which is a real state, not a broken read: an area that has not finished loading and
        // a tile block of solid scenery both look exactly like this.
        bool[] mask = Grid([.. Enumerable.Repeat(new string('#', 46), 46)]).WalkableTileMask();

        Assert.Equal(4, mask.Length);
        Assert.All(mask, walkable => Assert.False(walkable));
    }

    [Fact]
    public void RowPaddingIsNotWalkableGround()
    {
        // The row stride is a byte count the game is free to round up, and the cells past the
        // area's own width are whatever was in memory. Counting them would mark the last tile
        // of every row as walkable on a map where it is not.
        var cells = new byte[2 * TerrainGrid.CellsPerTile * 24];   // 24 bytes a row for 46 cells
        for (int y = 0; y < 2 * TerrainGrid.CellsPerTile; y++)
        {
            cells[(y * 24) + 23] = 0xFF;   // cells 46 and 47: past the width, pure padding
        }

        bool[] mask = new TerrainGrid(cells, 24, 46, 2, 2, heights: null).WalkableTileMask();

        Assert.All(mask, walkable => Assert.False(walkable));
    }
}
