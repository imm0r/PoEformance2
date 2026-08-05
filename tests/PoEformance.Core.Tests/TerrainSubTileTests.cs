using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The slope inside a terrain tile: how it is packed, and how a tile's placement is undone.
/// </summary>
/// <remarks>
/// This is the term that made the outline sit wrong however the reference was chosen. The
/// tile height is not the ground - it is the level the ground is described relative to - so
/// leaving this out leaves a large common offset in every height, which shows on screen as
/// the whole map shifted.
/// </remarks>
public class TerrainSubTileTests
{
    private const int Cells = TerrainGrid.CellsPerTile;

    [Fact]
    public void TheThreePackedFormatsFallOutOf23x23()
    {
        // The check that this is the RIGHT reading of the format rather than a plausible one.
        // Nothing declares how a height array is packed - only its LENGTH says - so the sizes
        // have to be derivable, and they are: a tile is 23x23 = 529 cells, and each format is
        // exactly enough index bits for 529 cells plus its value table.
        const int cellsPerTile = Cells * Cells;
        Assert.Equal(529, cellsPerTile);

        Assert.Equal(0x45, ((cellsPerTile + 7) / 8) + 2);     // 1 bit each, 2 values
        Assert.Equal(0x89, ((cellsPerTile + 3) / 4) + 4);     // 2 bits each, 4 values
        Assert.Equal(0x119, ((cellsPerTile + 1) / 2) + 16);   // 4 bits each, 16 values
    }

    [Fact]
    public void OneByteIsAWholeTileAtASingleHeight()
    {
        Assert.Equal(40, TerrainHeightField.SubHeight([40], 0));
        Assert.Equal(40, TerrainHeightField.SubHeight([40], 528));

        // Signed: the game has ground below its tile's level as well as above, and reading
        // these unsigned would turn every dip into a 200-unit spike.
        Assert.Equal(-16, TerrainHeightField.SubHeight([unchecked((byte)-16)], 5));
    }

    [Fact]
    public void OneBitPerCellPicksBetweenTwoHeights()
    {
        var array = new byte[0x45];
        array[0] = 10;                       // value 0
        array[1] = unchecked((byte)-6);      // value 1

        // Cell 3 selects the second value, cell 4 the first.
        array[2 + (3 >> 3)] |= 1 << (3 & 7);

        Assert.Equal(-6, TerrainHeightField.SubHeight(array, 3));
        Assert.Equal(10, TerrainHeightField.SubHeight(array, 4));
    }

    [Fact]
    public void TwoBitsPerCellPickBetweenFour()
    {
        var array = new byte[0x89];
        array[0] = 1;
        array[1] = 2;
        array[2] = 3;
        array[3] = unchecked((byte)-4);

        // Cell 5 -> value 3, which is index 3 in the table.
        array[4 + (5 >> 2)] |= 3 << ((5 & 3) << 1);

        Assert.Equal(-4, TerrainHeightField.SubHeight(array, 5));
        Assert.Equal(1, TerrainHeightField.SubHeight(array, 6));
    }

    [Fact]
    public void FourBitsPerCellPickBetweenSixteen()
    {
        var array = new byte[0x119];
        for (int i = 0; i < 16; i++)
        {
            array[i] = (byte)(i * 2);
        }

        // Cell 9 -> value 12.
        array[16 + (9 >> 1)] |= 12 << ((9 & 1) << 2);

        Assert.Equal(24, TerrainHeightField.SubHeight(array, 9));
    }

    [Fact]
    public void AnUnrecognisedLengthIsOneSignedBytePerCell()
    {
        var array = new byte[600];
        array[42] = unchecked((byte)-9);

        Assert.Equal(-9, TerrainHeightField.SubHeight(array, 42));
        Assert.Equal(0, TerrainHeightField.SubHeight(array, 5000));   // past the end
    }

    [Fact]
    public void AShortArrayNeverReadsPastItsEnd()
    {
        // The arrays come from the game, mid-load and mid-mutation, and a truncated one must
        // read as no height rather than as whatever follows it in memory.
        var truncated = new byte[0x45 - 20];
        Assert.Equal(0, TerrainHeightField.SubHeight(truncated, 400));
        Assert.Equal(0, TerrainHeightField.SubHeight([], 0));
        Assert.Equal(0, TerrainHeightField.SubHeight([1, 2, 3], -1));
    }

    /// <summary>Tile levels and a per-cell slope, with the rotation tables supplied.</summary>
    private static TerrainHeightField Field(byte[] subHeights, byte rotationSelector = 0)
    {
        // The identity placement: selector 0 -> rotation 0 -> straight x, straight y.
        byte[] selector = [0, 3, 2, 1, 4, 5, 6, 7, 8];
        var helper = new byte[32];
        helper[0] = 0;   // rx0
        helper[1] = 1;   // rx1 -> ix = 1, the un-mirrored x
        helper[2] = 1;   // ry0; ry1 = 2 because rx0 == 0 -> iy = 3, the un-mirrored y

        return TerrainHeightField.WithSubTile(
            [100f], 1, 1, [rotationSelector], [0], [subHeights], selector, helper);
    }

    [Fact]
    public void TheHeightIsTheTilesLevelPlusTheCellsOwnSlope()
    {
        // Both terms, in the same units, with the game's downward sign on the slope.
        var array = new byte[600];
        array[(4 * Cells) + 7] = 8;   // cell (7, 4) inside the tile

        TerrainHeightField field = Field(array);

        Assert.Equal(100f + (8 * TerrainHeightField.HeightScale), field.HeightAt(7, 4), 3);
        Assert.Equal(100f, field.HeightAt(0, 0), 3);   // no slope recorded there
    }

    [Fact]
    public void WithoutTheRotationTablesTheFieldStaysTileLevel()
    {
        // A pattern scan can legitimately come up empty after a patch. Reading a template's
        // height array without knowing how its tile was placed returns a real height
        // belonging to the wrong corner, which is worse than not reading it - so it is not
        // read at all.
        TerrainHeightField tilesOnly = TerrainHeightField.Tiles([100f, 200f], 2, 1);

        Assert.False(tilesOnly.HasSubTile);
        Assert.Equal(100f, tilesOnly.HeightAt(0, 0));
        Assert.Equal(200f, tilesOnly.HeightAt(Cells, 0));
    }

    [Fact]
    public void CellsOutsideTheFieldClampToItsEdge()
    {
        TerrainHeightField field = Field(new byte[600]);

        Assert.Equal(100f, field.HeightAt(-5, -5), 3);
        Assert.Equal(100f, field.HeightAt(10_000, 10_000), 3);
    }
}
