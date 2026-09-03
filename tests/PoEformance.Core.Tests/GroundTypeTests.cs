using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading what KIND of ground is under each tile, and refusing to when it cannot be believed.
/// </summary>
/// <remarks>
/// The route here was long and every earlier one was a dead end, so what these tests are really
/// about is the two checks rather than the reading: the room files named their ground types and
/// never their tiles, which killed the chain room-to-tile; then a landscape nibble was taken for
/// an index into the area's .gt list, was not one, and shipped anyway. What is left is byte 0 of
/// each TILE CORNER, which is measured - see the class doc. A wrong list would still produce a
/// map of plausible nonsense, and this project has paid for two of those.
/// </remarks>
public class GroundTypeTests
{
    private const int Cells = TerrainGrid.CellsPerTile;
    private const int Corner = TerrainGroundTypes.BytesPerCorner;

    /// <summary>
    /// Twenty by ten tiles, which is twenty-one by eleven corners.
    /// </summary>
    /// <remarks>
    /// BIG ENOUGH FOR THE SPREAD CHECK TO RUN, and that is the whole reason for the size. The
    /// check ignores a type covering fewer than 64 corners, because a handful of corners says
    /// nothing either way - so an eight-by-four area, which was plenty when this counted CELLS,
    /// now has 45 corners in total and would silence the check rather than exercise it.
    /// </remarks>
    private const int TilesX = 20;
    private const int TilesY = 10;

    private static readonly string[] Types =
    [
        "Metadata/Terrain/Desert/Badlands/bone_fill.gt",
        "Metadata/Terrain/Desert/Badlands/bone_abyss.gt",
    ];

    /// <summary>
    /// A corner array whose LEFT half is type 0 and right half type 1.
    /// </summary>
    /// <remarks>
    /// Three bytes per corner with the type in byte 0, which is the shape the reader identifies
    /// the array by. Bytes 1 and 2 are filled with something OTHER than the type, so a reading
    /// that took the wrong lane would come back with the wrong answer rather than the same one.
    /// </remarks>
    private static byte[] Halves()
    {
        int across = TilesX + 1;
        var corners = new byte[across * (TilesY + 1) * Corner];

        for (int cornerY = 0; cornerY <= TilesY; cornerY++)
        {
            for (int cornerX = 0; cornerX <= TilesX; cornerX++)
            {
                int at = (((cornerY * across) + cornerX) * Corner);
                corners[at] = (byte)(cornerX < TilesX / 2 ? 0 : 1);
                corners[at + 1] = 0x7F;
                corners[at + 2] = 0x40;
            }
        }

        return corners;
    }

    /// <summary>Walkable on the left half only - so type 0 stands and type 1 does not.</summary>
    private static byte[] WalkableCells(out int bytesPerRow, bool everywhere = false)
    {
        int width = TilesX * Cells;
        bytesPerRow = (width + 1) / 2;
        var cells = new byte[bytesPerRow * TilesY * Cells];

        for (int y = 0; y < TilesY * Cells; y++)
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

    private static TerrainGrid Walkable(bool everywhere = false)
    {
        byte[] cells = WalkableCells(out int stride, everywhere);
        return new TerrainGrid(cells, stride, TilesY * Cells, TilesX, TilesY);
    }

    private static TerrainGroundTypes Read(
        IReadOnlyList<string>? types = null, byte[]? corners = null, TerrainGrid? walkable = null)
        => Assert.IsType<TerrainGroundTypes>(TerrainGroundTypes.From(
            types ?? Types, corners ?? Halves(), TilesX, TilesY, walkable));

    [Fact]
    public void EachTileTakesTheTypeItsCornersAgreeOn()
    {
        TerrainGroundTypes ground = Read(walkable: Walkable());

        // Ten tiles of fill, ten of abyss, on every row. Tile 9 is the boundary - its west
        // corners are type 0 and its east ones type 1 - and a two-two tie goes to the first.
        Assert.Equal(0, ground.TileType[0]);
        Assert.Equal(0, ground.TileType[9]);
        Assert.Equal(1, ground.TileType[10]);
        Assert.Equal(1, ground.TileType[TilesX - 1]);
    }

    [Fact]
    public void TheTypeIsReadFromByteZeroAndNotFromTheOtherTwo()
    {
        // The array holds three bytes per corner and only the first is the type. The fixture
        // puts 0x7F and 0x40 in the other two, and neither the list nor the map may show them.
        TerrainGroundTypes ground = Read(walkable: Walkable());

        Assert.Equal(0, ground.OutOfRange);
        Assert.Equal(0, ground.TotalCorners[0x7F]);
        Assert.Equal(0, ground.TotalCorners[0x40]);
    }

    [Fact]
    public void TheTypeAtOneCornerIsReachableAndBoundsChecked()
    {
        // The accessor the room stamp will read through: a room file carries a ground type per
        // corner of every slot, so matching one against the area means asking corner by corner.
        TerrainGroundTypes ground = Read(walkable: Walkable());

        Assert.Equal(0, ground.At(0, 0));
        Assert.Equal(0, ground.At((TilesX / 2) - 1, TilesY));
        Assert.Equal(1, ground.At(TilesX / 2, 0));
        Assert.Equal(1, ground.At(TilesX, TilesY));

        // One PAST the last corner, which is one past the last tile - the array has tilesX+1
        // columns, so TilesX is the last valid index and TilesX+1 is off the end.
        Assert.Equal(-1, ground.At(TilesX + 1, 0));
        Assert.Equal(-1, ground.At(0, TilesY + 1));
        Assert.Equal(-1, ground.At(-1, 0));
    }

    [Fact]
    public void AValueBeyondTheListIsCountedAndKillsTheReading()
    {
        // THE FIRST CHECK. If the vector at +0x68 is not the list this array indexes, it is
        // almost certainly a shorter one - and then corners name types that do not exist. Zero
        // is the only passing answer, and no wrong list can fake it across a whole area.
        TerrainGroundTypes ground = Read(Types[..1], walkable: Walkable());

        Assert.True(ground.OutOfRange > 0);
        Assert.False(ground.Trusted);
        Assert.Contains("beyond the 1", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TypesThatDoNotSeparateOnWalkabilityAreNotBelieved()
    {
        // THE SECOND CHECK, and the one a wrong reading cannot pass by luck. If byte 0 really
        // names the ground, an abyss is walkable nowhere and a fill nearly everywhere. An array
        // read at the wrong offset samples the same ground for every type and lands them all on
        // the area's average - which is exactly this: walkable everywhere, both types at 1.0.
        TerrainGroundTypes ground = Read(walkable: Walkable(everywhere: true));

        Assert.Equal(0, ground.OutOfRange);
        Assert.False(ground.Trusted);
        Assert.Contains("do not separate", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingThatPassesBothChecksIsBelieved()
    {
        TerrainGroundTypes ground = Read(walkable: Walkable());

        Assert.Equal(0, ground.OutOfRange);
        Assert.True(ground.Trusted);
        Assert.Contains("2 ground types", ground.Note, StringComparison.Ordinal);

        // The spread itself, which is the evidence rather than the verdict.
        Assert.Equal(ground.TotalCorners[0], ground.WalkableCorners[0]);
        Assert.Equal(0, ground.WalkableCorners[1]);
    }

    [Fact]
    public void TheLastRowAndColumnOfCornersTakeTheGroundOfTheTileBeforeThem()
    {
        // A corner sits where four tiles meet, so its cell is the tile boundary - except on the
        // far edges, where there is no tile beyond and the cell would fall outside the walkable
        // grid. Stepping back one cell is what keeps two whole edges of the area from counting
        // as unwalkable and dragging every edge type's share down for no reason.
        TerrainGroundTypes ground = Read(walkable: Walkable(everywhere: true));

        Assert.Equal(ground.TotalCorners[0], ground.WalkableCorners[0]);
        Assert.Equal(ground.TotalCorners[1], ground.WalkableCorners[1]);
    }

    [Fact]
    public void WithoutAWalkableGridTheReadingIsReportedAsUncheckedRatherThanGood()
    {
        // A check that cannot run is not a check that passed, and the difference decides
        // whether anything gets drawn.
        TerrainGroundTypes ground = Read();

        Assert.False(ground.Trusted);
        Assert.Contains("unchecked", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrayOfAnotherSizeIsNotThisArray()
    {
        // THE IDENTIFICATION, and the gate that licenses everything after it. Three bytes per
        // tile corner is what tied this array to a room file's per-corner ground types in the
        // first place; anything else is a different thing being read.
        byte[] corners = Halves();

        Assert.Null(TerrainGroundTypes.From(Types, corners[..^Corner], TilesX, TilesY));
        Assert.Null(TerrainGroundTypes.From(Types, corners, TilesX + 1, TilesY));
        Assert.Null(TerrainGroundTypes.From(Types, corners, TilesX, 0));
    }

    [Fact]
    public void AnEmptyOrOversizedTypeListIsRefusedOutright()
    {
        byte[] corners = Halves();

        Assert.Null(TerrainGroundTypes.From([], corners, TilesX, TilesY));
        Assert.Null(TerrainGroundTypes.From(
            [.. Enumerable.Range(0, TerrainGroundTypes.MostTypes + 1).Select(i => $"x{i}.gt")],
            corners, TilesX, TilesY));
    }

    [Fact]
    public void TheRegionsAreTheHalvesAndTheyCarryTheirNames()
    {
        // The same flood fill the rooms use, on the same tile grid, because it is the same
        // question - so a type that covers half the area comes back as ONE labelled block.
        byte[] walkableCells = WalkableCells(out int stride);
        TerrainGroundTypes ground = Read(
            walkable: new TerrainGrid(walkableCells, stride, TilesY * Cells, TilesX, TilesY));

        // The GRID is built over the walkable cells, which is what it is: the ground types ride
        // alongside it rather than replacing it.
        var grid = new TerrainGrid(
            walkableCells, stride, TilesY * Cells, TilesX, TilesY, heights: null, ground: ground);

        Assert.Equal(2, grid.GroundRegions.Count);
        Assert.Contains(grid.GroundRegions, r => r.Path.EndsWith("bone_fill.gt", StringComparison.Ordinal));
        Assert.Contains(grid.GroundRegions, r => r.Path.EndsWith("bone_abyss.gt", StringComparison.Ordinal));
        Assert.All(grid.GroundRegions, r => Assert.Equal(TilesX / 2 * TilesY, r.Tiles));
    }

    [Fact]
    public void AnUntrustedReadingDrawsNothingAtAll()
    {
        // The refusal lives at the source rather than in a flag the layer could forget to test.
        byte[] walkableCells = WalkableCells(out int stride, everywhere: true);
        TerrainGroundTypes ground = Read(walkable: Walkable(everywhere: true));

        var grid = new TerrainGrid(
            walkableCells, stride, TilesY * Cells, TilesX, TilesY, heights: null, ground: ground);

        Assert.False(ground.Trusted);
        Assert.Empty(grid.GroundRegions);
    }

    [Fact]
    public void EveryValueThatOccursIsCountedEvenBeyondTheList()
    {
        // WHAT THE VERDICT CANNOT SAY. A real area came back "9190252 cells name a type beyond
        // the 5 the area lists" - which reports that the pairing is wrong and nothing at all
        // about the values, and the values are the only thing that decides what to do next. Two
        // named types here, and the array also holds a 9: that 9 has to be visible as a 9.
        byte[] corners = Halves();
        corners[0] = 9;

        TerrainGroundTypes ground = Read(corners: corners, walkable: Walkable());

        Assert.Equal(1, ground.OutOfRange);
        Assert.False(ground.Trusted);

        string nine = Assert.Single(
            ground.Lines, line => line.TrimStart().StartsWith("9 ", StringComparison.Ordinal));
        Assert.Contains("1 corners", nine, StringComparison.Ordinal);
        Assert.Contains("(beyond the list)", nine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHistogramLeadsWithTheAreaAndNamesWhatItCan()
    {
        TerrainGroundTypes ground = Read(walkable: Walkable());

        // THE AREA'S OWN WALKABLE COUNT FIRST, without which no row means anything: a value
        // walkable nearly everywhere is the ordinary ground in an area that is, and a finding
        // in one that is not.
        Assert.Contains("231 corners over 20x10 tiles", ground.Lines[0], StringComparison.Ordinal);
        Assert.Contains("110 of them walkable", ground.Lines[0], StringComparison.Ordinal);
        Assert.Contains("2 slots (2 named)", ground.Lines[0], StringComparison.Ordinal);

        string fill = Assert.Single(
            ground.Lines, line => line.Contains("bone_fill", StringComparison.Ordinal));
        Assert.Contains("110 corners", fill, StringComparison.Ordinal);
        Assert.Contains("110 walkable", fill, StringComparison.Ordinal);

        string abyss = Assert.Single(
            ground.Lines, line => line.Contains("bone_abyss", StringComparison.Ordinal));
        Assert.Contains("0 walkable", abyss, StringComparison.Ordinal);
    }

    [Fact]
    public void NoHistogramLineCarriesAPerCentSign()
    {
        // THE REGRESSION FOR THE WORST BUG THIS FEATURE HAD. ImGui's text is printf underneath,
        // the first histogram went to it raw with two per-cent signs per row, and printf ate one
        // as a conversion - rendering a hex float where a share belonged and leaving the OTHER
        // number standing under its label. Every walkable figure that version displayed was a
        // different number wearing that name, and the verdict survived only because the gates
        // compute on the arrays rather than on the strings.
        //
        // The draw call is careful now as well (ImGuiText.Mono is TextUnformatted), so this is
        // the second of two locks. Counts say the same thing and cannot be misread by a
        // formatter, which is why they replaced the shares rather than being escaped.
        TerrainGroundTypes ground = Read(walkable: Walkable());

        Assert.All(ground.Lines, line => Assert.DoesNotContain("%", line, StringComparison.Ordinal));
        Assert.DoesNotContain("%", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBiggestValueIsListedFirst()
    {
        // The values covering a corner or two are noise beside the ones covering half the area,
        // and a person reading this wants the shape of the array at a glance.
        byte[] corners = Halves();
        corners[0] = 9;

        TerrainGroundTypes ground = Read(corners: corners, walkable: Walkable());

        // Line 0 is the total; the 9 covers one corner and must not be above the halves.
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
