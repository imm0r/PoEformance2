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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void TheRimReachesExactlySoFarOnEachSideOfTheLine(int thickness, int reach)
    {
        // The line is one colour and the ground under it is every colour, so on the ground
        // that matches it the line is not there at all. The rim is what fixes that, and it
        // has to reach the SAME distance on each side whatever the width: grown from the
        // thin line again, an even width would have it one pixel off-centre in its own rim.
        var rows = new string[21];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string('#', 10) + "." + new string('#', 10);
        }

        OutlineMask mask = TerrainOutline.Build(Grid(rows), maxEdge: 64, thickness);
        byte[] rim = TerrainOutline.Rim(mask, reach);

        // Across the column: the line plus the reach on each side, and nothing else.
        int lineWidth = 0;
        int rimWidth = 0;
        for (int x = 0; x < mask.Width; x++)
        {
            lineWidth += mask.IsSet(x, 10) ? 1 : 0;
            rimWidth += rim[(10 * mask.Width) + x] != 0 ? 1 : 0;
        }

        Assert.Equal(thickness, lineWidth);
        Assert.Equal(thickness + (2 * reach), rimWidth);

        // And pixel by pixel, against the definition: set exactly where a line pixel is
        // within the reach in any direction, including diagonally. The two-pass version has
        // to agree with the plain one everywhere, edges included.
        for (int y = 0; y < mask.Height; y++)
        {
            for (int x = 0; x < mask.Width; x++)
            {
                bool near = false;
                for (int dy = -reach; dy <= reach && !near; dy++)
                {
                    for (int dx = -reach; dx <= reach && !near; dx++)
                    {
                        near = mask.IsSet(x + dx, y + dy);
                    }
                }

                Assert.Equal(near, rim[(y * mask.Width) + x] != 0);
            }
        }
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

    [Fact]
    public void TheFloorIsEveryWalkableCell_AndKnowsWhereEachPixelCameFrom()
    {
        // The floor as a sheet under the line: the whole of the floor, the ring beside the wall
        // as much as the middle - the line is drawn over it, not instead of it - and none of the
        // rock. Drawn on the walls too it would be a sheet over the whole map, which is what the
        // outline exists to avoid. And on flat ground a pixel's ground is its own cell.
        TerrainGrid grid = Grid(
            "#####",
            "#...#",
            "#...#",
            "#...#",
            "#####");

        FloorPlan floor = TerrainOutline.Floor(grid, step: 1, isoHeightShift: false);
        int At(int x, int y) => (y * floor.Width) + x;

        Assert.Equal(grid.Width, floor.Width);
        Assert.Equal(255, floor.Coverage[At(2, 2)]);   // the middle
        Assert.Equal(255, floor.Coverage[At(1, 1)]);   // the ring, where the line also is
        Assert.Equal(0, floor.Coverage[At(0, 0)]);     // the wall
        Assert.Equal(0, floor.Coverage[At(4, 2)]);

        Assert.Equal(At(2, 2), floor.Source[At(2, 2)]);
        Assert.Equal(-1, floor.Source[At(0, 0)]);
    }

    [Fact]
    public void ACoarsePixelIsAsMuchFloorAsItHolds()
    {
        // At a step of two a pixel holds four cells, and the floor's edge runs through some of
        // them. Counted, those pixels get a proportionate alpha - the edge anti-aliased for
        // nothing. Flagged, they would get the full sheet and grow the floor by up to a pixel
        // on every side. Five cells wide, so the last column is a partial pixel that is still
        // drawn rather than dropped.
        TerrainGrid grid = Grid(
            "...#.",
            "..##.");

        FloorPlan floor = TerrainOutline.Floor(grid, step: 2, isoHeightShift: false);

        Assert.Equal(3, floor.Width);
        Assert.Equal(1, floor.Height);
        Assert.Equal(255, floor.Coverage[0]);   // four of four
        Assert.Equal(64, floor.Coverage[1]);    // one of four
        Assert.Equal(128, floor.Coverage[2]);   // two of four: the column, plus the padding cell
        Assert.Equal(2, floor.Source[2]);
    }

    [Fact]
    public void TheFloorMovesWithTheLine_AndRemembersWhereItStood()
    {
        // The sheet and its edge have to be displaced by the same heights, or on every slope
        // the sheet peels away from the line drawn around it. Same grid as the line's own
        // height test: one raised tile in a walkable field.
        //
        // And a displaced pixel has to know which cell it shows, because "has this been walked"
        // is recorded against the cell and asked of the pixel - on a hill the two are tens of
        // cells apart, and a hole looked up at the pixel's own position would open beside the
        // player rather than around them.
        int cells = TerrainGrid.CellsPerTile;
        int width = 2 * cells;
        int stride = (width + 1) / 2;

        var packed = new byte[stride * width];
        Array.Fill(packed, (byte)0x11);
        var heights = new float[] { -242f, 0f, 0f, 0f };
        var grid = new TerrainGrid(packed, stride, width, 2, 2, heights);

        int shift = grid.IsoHeightShift(0, 0);
        Assert.True(shift < 0);

        OutlineMask mask = TerrainOutline.Build(grid, maxEdge: 4096, thickness: 1, isoHeightShift: true);
        FloorPlan flat = TerrainOutline.Floor(grid, step: 1, isoHeightShift: false);
        FloorPlan shifted = TerrainOutline.Floor(grid, step: 1, isoHeightShift: true);
        int At(int x, int y) => (y * shifted.Width) + x;

        // Flat, the corner is floor like everything else. Shifted, the raised tile's corner
        // cell has moved its own height away - and nothing has moved INTO the corner, so the
        // picture is clear there, exactly where the line is clear too.
        Assert.Equal(255, flat.Coverage[At(0, 0)]);
        Assert.Equal(0, shifted.Coverage[At(0, 0)]);
        Assert.False(mask.IsSet(0, 0));

        // Where it landed, the pixel is floor - and names the corner cell as its ground.
        Assert.Equal(255, shifted.Coverage[At(-shift, -shift)]);
        Assert.Equal(At(0, 0), shifted.Source[At(-shift, -shift)]);

        // And the far corner is flat ground, which does not move and is its own ground.
        int far = width - 1;
        Assert.Equal(255, shifted.Coverage[At(far, far)]);
        Assert.Equal(At(far, far), shifted.Source[At(far, far)]);
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

    [Fact]
    public void AClearLineNeedsEveryCellBetweenToBeWalkable()
    {
        // Line of sight, as near as this game lets it be answered: the reference's own
        // technique (GameHelper2's Radar walks exactly this line over the same nibbles).
        TerrainGrid grid = Grid(
            "..........",
            "..........",
            "..........",
            "..........",
            "..........");

        Assert.True(grid.IsClearLine(0, 0, 9, 4));
        Assert.True(grid.IsClearLine(9, 4, 0, 0));
    }

    [Fact]
    public void AWallBetweenThemBreaksIt()
    {
        TerrainGrid grid = Grid(
            "....#.....",
            "....#.....",
            "....#.....",
            "....#.....",
            "....#.....");

        // Across the wall, both ways round - the walk is symmetric or it is wrong.
        Assert.False(grid.IsClearLine(0, 2, 9, 2));
        Assert.False(grid.IsClearLine(9, 2, 0, 2));

        // Along the same side of it, untouched.
        Assert.True(grid.IsClearLine(0, 0, 3, 4));
        Assert.True(grid.IsClearLine(5, 0, 9, 4));
    }

    [Fact]
    public void AGapInTheWallIsSeenThrough()
    {
        // The case that says the walk really follows the line rather than testing a box
        // around it: one open cell, and only the lines that pass through it are clear.
        TerrainGrid grid = Grid(
            "....#.....",
            "....#.....",
            ".........#",
            "....#.....",
            "....#.....");

        Assert.True(grid.IsClearLine(0, 2, 8, 2));
        Assert.False(grid.IsClearLine(0, 0, 9, 0));
    }

    [Fact]
    public void BothEndsAreTested()
    {
        // Like the reference. A monster standing on ground that reads solid - over a gap,
        // inside scenery - is not in sight, and neither is anything measured from a player
        // whose own cell is solid. Excluding the ends would be a tolerance invented for a
        // problem nothing has shown.
        TerrainGrid grid = Grid(
            "#....",
            ".....",
            "....#");

        Assert.False(grid.IsClearLine(0, 0, 4, 1));
        Assert.False(grid.IsClearLine(0, 1, 4, 2));
        Assert.True(grid.IsClearLine(0, 1, 4, 1));
    }

    [Fact]
    public void ALineThatLeavesTheGridIsBlocked()
    {
        // Same rule IsWalkable follows. A line that leaves the map is blocked rather than
        // running off the end of the buffer.
        TerrainGrid grid = Grid(
            ".....",
            ".....");

        Assert.False(grid.IsClearLine(0, 0, 40, 0));
        Assert.False(grid.IsClearLine(0, 0, 0, -5));
    }

    [Fact]
    public void OneCellIsAlwaysItsOwnAnswer()
    {
        TerrainGrid grid = Grid(
            ".#");

        Assert.True(grid.IsClearLine(0, 0, 0, 0));
        Assert.False(grid.IsClearLine(1, 0, 1, 0));
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
