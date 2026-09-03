using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading what KIND of ground is under each tile, and refusing to when it cannot be believed.
/// </summary>
/// <remarks>
/// The route here was long and every earlier one was a dead end, so what these tests are really
/// about is the two checks rather than the reading: the room files named their ground types and
/// never their tiles, which killed the chain room-to-tile and left this one - a nibble per cell
/// indexing the area's own list of .gt files. A wrong list would produce a map of plausible
/// nonsense, and this project has paid for one of those before.
/// </remarks>
public class GroundTypeTests
{
    private const int Cells = TerrainGrid.CellsPerTile;

    private static readonly string[] Types =
    [
        "Metadata/Terrain/Desert/Badlands/bone_fill.gt",
        "Metadata/Terrain/Desert/Badlands/bone_abyss.gt",
    ];

    /// <summary>
    /// An area of tilesX by tilesY whose LEFT half is type 0 and right half type 1.
    /// </summary>
    /// <remarks>
    /// Packed as the walkable grid is - two cells per byte, even x in the low nibble - because
    /// that is the packing the reader establishes by requiring the two buffers to be the same
    /// length. A test that packed it differently would be testing a different game.
    /// </remarks>
    private static byte[] Halves(int tilesX, int tilesY, out int bytesPerRow)
    {
        int width = tilesX * Cells;
        bytesPerRow = (width + 1) / 2;
        var cells = new byte[bytesPerRow * tilesY * Cells];

        for (int y = 0; y < tilesY * Cells; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int type = x < width / 2 ? 0 : 1;
                int index = (y * bytesPerRow) + (x >> 1);
                cells[index] |= (byte)((x & 1) == 0 ? type : type << 4);
            }
        }

        return cells;
    }

    /// <summary>Walkable on the left half only - so type 0 stands and type 1 does not.</summary>
    private static byte[] WalkableCells(int tilesX, int tilesY, int bytesPerRow, bool everywhere = false)
    {
        int width = tilesX * Cells;
        var cells = new byte[bytesPerRow * tilesY * Cells];

        for (int y = 0; y < tilesY * Cells; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!everywhere && x >= width / 2)
                {
                    continue;
                }

                int index = (y * bytesPerRow) + (x >> 1);
                cells[index] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
            }
        }

        return cells;
    }

    private static TerrainGrid Walkable(int tilesX, int tilesY, int bytesPerRow, bool everywhere = false)
        => new(
            WalkableCells(tilesX, tilesY, bytesPerRow, everywhere),
            bytesPerRow, tilesY * Cells, tilesX, tilesY);

    [Fact]
    public void EachTileTakesTheTypeThatCoversMostOfIt()
    {
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4, Walkable(8, 4, stride)));

        // Four tiles of fill, four of abyss, on every row.
        Assert.Equal(0, ground.TileType[0]);
        Assert.Equal(0, ground.TileType[3]);
        Assert.Equal(1, ground.TileType[4]);
        Assert.Equal(1, ground.TileType[7]);
    }

    [Fact]
    public void ANibbleBeyondTheListIsCountedAndKillsTheReading()
    {
        // THE FIRST CHECK. If the vector at +0x68 is not the list these nibbles index, it is
        // almost certainly a shorter one - and then cells name types that do not exist. Zero is
        // the only passing answer, and no wrong list can fake it across a whole area.
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(
                Types[..1], landscape, stride, 8, 4, Walkable(8, 4, stride)));

        Assert.True(ground.OutOfRange > 0);
        Assert.False(ground.Trusted);
        Assert.Contains("beyond the 1", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TypesThatDoNotSeparateOnWalkabilityAreNotBelieved()
    {
        // THE SECOND CHECK, and the one a wrong reading cannot pass by luck. If a nibble really
        // names the ground, an abyss is walkable nowhere and a fill nearly everywhere. A grid
        // read at the wrong offset samples the same ground for every type and lands them all on
        // the area's average - which is exactly this: walkable everywhere, both types at 1.0.
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(
                Types, landscape, stride, 8, 4, Walkable(8, 4, stride, everywhere: true)));

        Assert.Equal(0, ground.OutOfRange);
        Assert.False(ground.Trusted);
        Assert.Contains("do not separate", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingThatPassesBothChecksIsBelieved()
    {
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4, Walkable(8, 4, stride)));

        Assert.Equal(0, ground.OutOfRange);
        Assert.True(ground.Trusted);
        Assert.Contains("2 ground types", ground.Note, StringComparison.Ordinal);

        // The spread itself, which is the evidence rather than the verdict.
        Assert.Equal(ground.TotalCells[0], ground.WalkableCells[0]);
        Assert.Equal(0, ground.WalkableCells[1]);
    }

    [Fact]
    public void WithoutAWalkableGridTheReadingIsReportedAsUncheckedRatherThanGood()
    {
        // A check that cannot run is not a check that passed, and the difference decides
        // whether anything gets drawn.
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4));

        Assert.False(ground.Trusted);
        Assert.Contains("unchecked", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrOversizedTypeListIsRefusedOutright()
    {
        byte[] landscape = Halves(8, 4, out int stride);

        Assert.Null(TerrainGroundTypes.From([], landscape, stride, 8, 4));
        Assert.Null(TerrainGroundTypes.From(
            [.. Enumerable.Range(0, 17).Select(i => $"x{i}.gt")], landscape, stride, 8, 4));
    }

    [Fact]
    public void TheRegionsAreTheHalvesAndTheyCarryTheirNames()
    {
        // The same flood fill the rooms use, on the same tile grid, because it is the same
        // question - so a type that covers half the area comes back as ONE labelled block.
        byte[] landscape = Halves(8, 4, out int stride);
        byte[] walkableCells = WalkableCells(8, 4, stride);
        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(
                Types, landscape, stride, 8, 4,
                new TerrainGrid(walkableCells, stride, 4 * Cells, 8, 4)));

        // The GRID is built over the walkable cells, which is what it is: the ground types ride
        // alongside it rather than replacing it.
        var grid = new TerrainGrid(
            walkableCells, stride, 4 * Cells, 8, 4, heights: null, ground: ground);

        Assert.Equal(2, grid.GroundRegions.Count);
        Assert.Contains(grid.GroundRegions, r => r.Path.EndsWith("bone_fill.gt", StringComparison.Ordinal));
        Assert.Contains(grid.GroundRegions, r => r.Path.EndsWith("bone_abyss.gt", StringComparison.Ordinal));
        Assert.All(grid.GroundRegions, r => Assert.Equal(16, r.Tiles));
    }

    [Fact]
    public void AnUntrustedReadingDrawsNothingAtAll()
    {
        // The refusal lives at the source rather than in a flag the layer could forget to test.
        byte[] landscape = Halves(8, 4, out int stride);
        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(
                Types, landscape, stride, 8, 4, Walkable(8, 4, stride, everywhere: true)));

        var grid = new TerrainGrid(
            WalkableCells(8, 4, stride, everywhere: true), stride, 4 * Cells, 8, 4,
            heights: null, ground: ground);

        Assert.False(ground.Trusted);
        Assert.Empty(grid.GroundRegions);
    }

    [Fact]
    public void EveryValueThatOccursIsCountedEvenBeyondTheList()
    {
        // WHAT THE VERDICT CANNOT SAY. A real area came back "9190252 cells name a type beyond
        // the 5 the area lists" - which reports that the pairing is wrong and nothing at all
        // about the values, and the values are the only thing that decides what to do next. Two
        // named types here, and the grid also holds a 9: that 9 has to be visible as a 9.
        byte[] landscape = Halves(8, 4, out int stride);
        landscape[0] = 0x99;   // two cells of a value the list cannot name

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4, Walkable(8, 4, stride)));

        Assert.Equal(2, ground.OutOfRange);
        Assert.False(ground.Trusted);

        string nine = Assert.Single(
            ground.Lines, line => line.TrimStart().StartsWith("9 ", StringComparison.Ordinal));
        Assert.Contains("2 cells", nine, StringComparison.Ordinal);
        Assert.Contains("(beyond the list)", nine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHistogramNamesWhatItCanAndSaysSoWhenItCannot()
    {
        // The walkable share is what gives an unnamed value meaning without any names at all:
        // whatever is never walkable is the void or the abyss.
        byte[] landscape = Halves(8, 4, out int stride);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4, Walkable(8, 4, stride)));

        Assert.Contains("2 named types", ground.Lines[0], StringComparison.Ordinal);

        string fill = Assert.Single(
            ground.Lines, line => line.Contains("bone_fill", StringComparison.Ordinal));
        Assert.Contains("100.0% walkable", fill, StringComparison.Ordinal);

        string abyss = Assert.Single(
            ground.Lines, line => line.Contains("bone_abyss", StringComparison.Ordinal));
        Assert.Contains("0.0% walkable", abyss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBiggestValueIsListedFirst()
    {
        // The values covering a hundred cells between them are noise beside the one covering
        // nine million, and a person reading this wants the shape of the grid at a glance.
        byte[] landscape = Halves(8, 4, out int stride);
        landscape[0] = 0x99;

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, landscape, stride, 8, 4, Walkable(8, 4, stride)));

        // Line 0 is the total; the 9 covers two cells and must not be above the halves.
        Assert.DoesNotContain("9 ", ground.Lines[1], StringComparison.Ordinal);
        Assert.Contains("9 ", ground.Lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Metadata/Terrain/Desert/Badlands/bone_fill.gt", "bone_fill")]
    [InlineData("waypoint_ground.gt", "waypoint_ground")]
    [InlineData("Metadata/Terrain/wildcard", "wildcard")]
    public void TheLabelIsTheFileStem(string path, string expected)
        => Assert.Equal(expected, TerrainGroundTypes.NameFor(path));
}
