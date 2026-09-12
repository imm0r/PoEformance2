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

    /// <summary>
    /// The Maelstrom area, by its measured counts: the BLANK slot is the floor.
    /// </summary>
    /// <remarks>
    /// A corner array whose values reproduce the histogram that broke the first version of the
    /// spread check - 746 blank corners with 635 walkable, 2273 of a wall with none, 3561 of an
    /// abyss with 44. Laid out in bands so the walkable region is contiguous, which is what a
    /// walkable grid can actually represent; only the per-value totals and shares matter here.
    /// </remarks>
    private const int MaelstromTiles = 20;

    private static (byte[] Corners, byte[] Cells, int Stride) Maelstrom()
    {
        // Square, and its own size rather than the shared fixture's: all THREE bands have to
        // clear the 64-corner floor the spread check ignores below, or the test would be
        // measuring two of them and passing for the wrong reason.
        const int Tiles = MaelstromTiles;
        const int Band = (Tiles + 1) / 3;            // 7 corner rows each

        int across = Tiles + 1;
        var corners = new byte[across * (Tiles + 1) * Corner];
        for (int cornerY = 0; cornerY <= Tiles; cornerY++)
        {
            // The blank on top, then the wall, then the abyss.
            byte type = cornerY < Band ? (byte)0 : cornerY < 2 * Band ? (byte)1 : (byte)2;
            for (int cornerX = 0; cornerX <= Tiles; cornerX++)
            {
                corners[((cornerY * across) + cornerX) * Corner] = type;
            }
        }

        // Walkable exactly as far down as the blank band reaches. Corner row Band samples cell
        // Band*Cells, which is the first unwalkable one, so the boundary lands where it should.
        int width = Tiles * Cells;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * Tiles * Cells];
        for (int y = 0; y < Band * Cells; y++)
        {
            for (int x = 0; x < width; x++)
            {
                cells[(y * stride) + (x >> 1)] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
            }
        }

        return (corners, cells, stride);
    }

    [Fact]
    public void TheBlankSlotCanBeTheFloorAndTheReadingIsStillBelieved()
    {
        // THE BUG THIS IS THE REGRESSION FOR, found in a live Maelstrom area. The first version
        // of the spread check EXCLUDED the blank slot, on the theory that it covers the void
        // outside the playable area and is walkable nowhere - so counting it would satisfy the
        // "mostly not walkable" half for free. The game says otherwise: there the blank IS the
        // floor, 635 of the area's 679 walkable corners, and the two NAMED types are
        // black_inside_wall (0 of 2273 walkable) and maelstrom_abyss (44 of 3561). Demanding a
        // named type that is mostly walkable made a plainly correct reading fail.
        //
        // Correct because the walkable ground is PARTITIONED rather than shared out: noise would
        // have spread those 679 corners across the three values by coverage, and the names agree
        // with the physics - what the game calls a wall is walkable nowhere.
        (byte[] corners, byte[] cells, int stride) = Maelstrom();
        var walkable = new TerrainGrid(
            cells, stride, MaelstromTiles * Cells, MaelstromTiles, MaelstromTiles);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(TerrainGroundTypes.From(
            ["", "black_inside_wall.gt", "maelstrom_abyss.gt"],
            corners, MaelstromTiles, MaelstromTiles, walkable));

        Assert.Equal(0, ground.OutOfRange);

        // Every band big enough to be weighed, so all three take part in the spread.
        Assert.All([0, 1, 2], type => Assert.True(ground.TotalCorners[type] >= 64));

        // The partition: the walkable ground is the blank's, and the two NAMED types have none.
        Assert.Equal(ground.TotalCorners[0], ground.WalkableCorners[0]);
        Assert.Equal(0, ground.WalkableCorners[1]);
        Assert.Equal(0, ground.WalkableCorners[2]);

        Assert.True(ground.Trusted);
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
    public void AWallIsNotStandableJustBecauseSomeoneCanStandBesideIt()
    {
        // THE FILTER THE OBVIOUS TEST CANNOT DO. TerrainRoom.IsWalkable asks whether a region
        // holds ONE walkable tile, and a ground-type region hugs the floor for hundreds of tiles
        // with walkable geometry that does not follow tile edges - so an abyss touching the floor
        // anywhere passes it. The Maelstrom measures that leak: maelstrom_abyss has 44 walkable
        // corners of 3561, one tile in eighty. Standable() asks for a quarter, over the whole
        // area, so a wall stays a wall.
        (byte[] corners, byte[] cells, int stride) = Maelstrom();
        var walkable = new TerrainGrid(
            cells, stride, MaelstromTiles * Cells, MaelstromTiles, MaelstromTiles);

        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(TerrainGroundTypes.From(
            ["", "black_inside_wall.gt", "maelstrom_abyss.gt"],
            corners, MaelstromTiles, MaelstromTiles, walkable));

        Assert.True(ground.Standable(0));     // the blank slot IS the floor here
        Assert.False(ground.Standable(1));
        Assert.False(ground.Standable(2));

        // And the sentence agrees with the test, which is the point of there being one bar:
        // "0 of them" is what tells a person why the map is naming walls.
        Assert.Contains("0 of them ground you can stand on", ground.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void WallsAreNamedOnlyWhenNothingStandableIs()
    {
        // THE ANSWER TO "name walls and ceilings, if it is necessary". Necessary means: naming
        // them is what is left. In the Maelstrom the floor carries the game's UNNAMED slot, so
        // filtering to standable ground empties the map - and knowing which unenterable region
        // is a fall and which is a wall is then the whole of what this layer can say.
        (byte[] corners, byte[] cells, int stride) = Maelstrom();
        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(TerrainGroundTypes.From(
            ["", "black_inside_wall.gt", "maelstrom_abyss.gt"], corners,
            MaelstromTiles, MaelstromTiles,
            new TerrainGrid(cells, stride, MaelstromTiles * Cells, MaelstromTiles, MaelstromTiles)));

        var grid = new TerrainGrid(
            cells, stride, MaelstromTiles * Cells, MaelstromTiles, MaelstromTiles,
            heights: null, ground: ground);

        Assert.True(grid.NamingUnstandableGround);
        Assert.Equal(2, grid.GroundRegions.Count);
        Assert.Contains(grid.GroundRegions, r => r.Path.Contains("wall", StringComparison.Ordinal));
        Assert.Contains(grid.GroundRegions, r => r.Path.Contains("abyss", StringComparison.Ordinal));
    }

    [Fact]
    public void WhereSomethingStandableIsNamedTheWallsAreDropped()
    {
        // The other half of the same rule, and the one that thins a crowded map: an area is
        // mostly scenery you cannot enter, so naming every wall patch buries the labels worth
        // reading. Here the walkable half carries a NAMED type, so the unwalkable one goes.
        byte[] walkableCells = WalkableCells(out int stride);
        TerrainGroundTypes ground = Read(
            walkable: new TerrainGrid(walkableCells, stride, TilesY * Cells, TilesX, TilesY));

        var grid = new TerrainGrid(
            walkableCells, stride, TilesY * Cells, TilesX, TilesY, heights: null, ground: ground);

        Assert.False(grid.NamingUnstandableGround);

        // One block, carrying its own name and its own half of the area - the flood fill is the
        // same one the room names use, so a type covering half the map comes back as ONE region.
        TerrainRoom only = Assert.Single(grid.GroundRegions);
        Assert.EndsWith("bone_fill.gt", only.Path, StringComparison.Ordinal);
        Assert.Equal(TilesX / 2 * TilesY, only.Tiles);
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
