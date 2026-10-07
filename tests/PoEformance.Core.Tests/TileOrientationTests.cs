using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// How a terrain tile was laid: the one decode the heights and the tile book share.
/// </summary>
/// <remarks>
/// THE TABLES ARE THE GAME'S OWN, as the recordings hold them: the 9-byte selector table and the
/// 32-byte helper table at "Terrain Rotation Selector" and "Terrain Rotator Helper", the same
/// bytes in every 2026-08 capture that read them (session-2026-08-areamarkers.rec among them).
/// They decode selectors 0-3 to the four turns and 4-7 to the four mirrored ones, which is the
/// structure a table that means what this reads it as has to have - and the odd selectors are
/// exactly the ones that swap x and y, which is GameHelper2's own rule for its tile keys.
/// </remarks>
public class TileOrientationTests
{
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>The selector table as the game holds it.</summary>
    internal static readonly byte[] GameSelectors = Convert.FromHexString("000302010405060708");

    /// <summary>The helper table as the game holds it.</summary>
    internal static readonly byte[] GameHelper = Convert.FromHexString(
        "0001010100010000000101000000010100000001000101010001010000000000");

    [Fact]
    public void TheGamesTablesLayTheEightWaysInOrder()
    {
        TileOrientation[] table = TileOrientation.Table(GameSelectors, GameHelper);

        string[] said = [.. Enumerable.Range(0, 8).Select(selector => table[selector].ToString())];
        Assert.Equal(
            [
                "as authored", "turned 90°", "turned 180°", "turned 270°",
                "mirrored", "mirrored, turned 90°", "mirrored, turned 180°", "mirrored, turned 270°",
            ],
            said);

        // Eight different placements - nothing is offered twice under two names.
        Assert.Equal(8, Enumerable.Range(0, 8).Select(selector => table[selector].Placement).Distinct().Count());
    }

    [Fact]
    public void ANDTheOddSelectorsAreTheOnesThatSwapXAndY()
    {
        // GameHelper2 keys a tile by "x:{tileIdY}-y:{tileIdX}" when RotationSelector % 2 is 1.
        TileOrientation[] table = TileOrientation.Table(GameSelectors, GameHelper);
        for (int selector = 0; selector < 8; selector++)
        {
            Assert.Equal(selector % 2 == 1, table[selector].FromX >= 2);
        }
    }

    [Fact]
    public void ASelectorPastTheTableReadsEntry24AsTheReferenceDoes()
    {
        // GetSubTerrainHeight's own bounds, kept so the heights read through the decode exactly as
        // they did before it: entry 24 of the game's helper is (0, 1, 1), which is as authored.
        TileOrientation[] table = TileOrientation.Table(GameSelectors, GameHelper);

        Assert.Equal("as authored", table[8].ToString());
        Assert.Equal("as authored", table[200].ToString());
        Assert.False(TileOrientation.Decode(0, GameSelectors, new byte[2]).IsKnown);
        Assert.False(default(TileOrientation).IsKnown);
        Assert.Equal("orientation unknown", default(TileOrientation).ToString());
    }

    [Fact]
    public void THETURNISTHEHEIGHTDECODEINVERTED()
    {
        // The decode is right against the game - it is how the map reads its ground heights - and
        // it maps a world cell to the template cell it reads. Turning the template's points by
        // Turn has to send every template cell back to the world cell that reads it, for all eight.
        const int middle = Cells / 2;
        for (int placement = 0; placement < 8; placement++)
        {
            TileOrientation laid = TileOrientation.OfPlacement(placement);
            (int xx, int xy, int yx, int yy) = laid.Turn;

            for (int y = 0; y < Cells; y++)
            {
                for (int x = 0; x < Cells; x++)
                {
                    int index = laid.TemplateIndex(x, y, Cells);
                    int tx = (index % Cells) - middle;
                    int ty = (index / Cells) - middle;

                    Assert.Equal(x - middle, (xx * tx) + (xy * ty));
                    Assert.Equal(y - middle, (yx * tx) + (yy * ty));
                }
            }
        }
    }

    [Fact]
    public void ThePlacementNumberRoundTrips()
    {
        for (int placement = 0; placement < 8; placement++)
        {
            Assert.Equal(placement, TileOrientation.OfPlacement(placement).Placement);
        }

        Assert.False(TileOrientation.OfPlacement(-1).IsKnown);
        Assert.False(TileOrientation.OfPlacement(8).IsKnown);
    }

    [Fact]
    public void AMirrorFlipsXBeforeTheTurn()
    {
        // "mirrored, turned 90°" is R(90) after X -> -X: (1, 0) -> (-1, 0) -> (0, -1).
        TileOrientation[] table = TileOrientation.Table(GameSelectors, GameHelper);
        TileOrientation laid = table[5];
        (int xx, _, int yx, _) = laid.Turn;

        Assert.True(laid.Mirrored);
        Assert.Equal(90, laid.Degrees);
        Assert.Equal((0, -1), (xx, yx));
    }

    [Fact]
    public void ATileKeyCarriesThePlacement()
    {
        var key = new TileKey("Metadata/Terrain/A.tdt", Walls: false, Tileset: "metadata/terrain/x/master.tsi", Laid: 5);
        string written = key.ToString();

        Assert.Equal("Metadata/Terrain/A.tdt|unwalled+laid=5+set=metadata/terrain/x/master.tsi", written);
        Assert.Equal(key, TileKey.Read(written));
        Assert.Equal("Metadata/Terrain/A.tdt|laid=0", new TileKey("Metadata/Terrain/A.tdt", Laid: 0).ToString());

        // A number that is not a placement is a word this does not know, and is ignored.
        Assert.Equal(-1, TileKey.Read("Metadata/Terrain/A.tdt|laid=9").Laid);
        Assert.Equal(-1, TileKey.Read("Metadata/Terrain/A.tdt|laid=-1").Laid);
    }

    [Fact]
    public void ARoomCollectsEveryWayItsTilesWereLaid()
    {
        // Two touching placements of one file come out as one room - the fill cannot tell them
        // apart - so the room keeps both ways rather than whichever tile it met first.
        string[] paths = ["Metadata/Terrain/A.tdt", "Metadata/Terrain/B.tdt"];
        int[] tilePath = [0, 0, 1];
        byte[] turns = [1 << 3, 1 << 6, 1 << 0];

        List<TerrainRoom> rooms = TerrainRooms.Find(paths, tilePath, 3, 1, tileTurns: turns);

        Assert.Equal((byte)((1 << 3) | (1 << 6)), rooms.Single(room => room.Path == paths[0]).Turns);
        Assert.Equal((byte)1, rooms.Single(room => room.Path == paths[1]).Turns);
        Assert.All(TerrainRooms.Find(paths, tilePath, 3, 1), room => Assert.Equal(0, room.Turns));
    }

    private const ulong AreaInstance = 0x0000_0600_0000_0000;
    private const ulong GridData = 0x0000_0600_0100_0000;
    private const ulong TileData = 0x0000_0600_0200_0000;
    private const ulong TgtFile = 0x0000_0600_0300_0000;
    private const ulong PathData = 0x0000_0600_0400_0000;
    private const ulong SelectorTable = 0x0000_0600_0500_0000;
    private const ulong HelperTable = 0x0000_0600_0500_1000;

    /// <summary>Two tiles of one file side by side, laid by the two selectors given, out of a synthetic process.</summary>
    private static TerrainGrid? Read(byte left, byte right, bool tables = true)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef area = schema.Structs["AreaInstance"];
        StructDef terrain = schema.Structs["TerrainMetadata"];
        StructDef tile = schema.Structs["TileStruct"];
        ulong terrainBase = AreaInstance + (ulong)area.OffsetOf("TerrainMetadata");

        const int tilesX = 2;
        const int tilesY = 1;
        int width = tilesX * Cells;
        int rows = tilesY * Cells;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * rows];
        Array.Fill(cells, (byte)0x11);

        const int entrySize = 0x38;
        var tiles = new byte[tilesX * tilesY * entrySize];
        for (int i = 0; i < tilesX * tilesY; i++)
        {
            BitConverter.GetBytes(TgtFile).CopyTo(tiles, (i * entrySize) + tile.OffsetOf("TgtFilePtr"));
            tiles[(i * entrySize) + tile.OffsetOf("RotationSelector")] = i == 0 ? left : right;
        }

        var fake = new FakeMemoryReader()
            .Place(GridData, cells)
            .Place<uint>(AreaInstance + (ulong)area.OffsetOf("CurrentAreaHash"), 0xABCD1234)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData"), GridData)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData") + 8, GridData + (ulong)cells.Length)
            .Place<int>(terrainBase + (ulong)terrain.OffsetOf("BytesPerRow"), stride)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesX"), tilesX)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesY"), tilesY)
            .Place<short>(terrainBase + (ulong)terrain.OffsetOf("TileHeightMultiplier"), 1)
            .Place(TileData, tiles)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("TileDetailsPtr"), TileData)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("TileDetailsPtr") + 8, TileData + (ulong)tiles.Length)
            .Place(SelectorTable, GameSelectors)
            .Place(HelperTable, GameHelper);
        fake.PlaceStdWString(TgtFile + (ulong)schema.Structs["TgtFile"].OffsetOf("TgtPath"), "Metadata/Terrain/Test/Arena.tdt", PathData);

        var rotation = tables ? new TerrainRotationTables(SelectorTable, HelperTable) : default;
        return new TerrainReader(fake, schema, rotation).Read(AreaInstance, nowMs: 0);
    }

    [Fact]
    public void THEREADERMARKSEACHROOMWITHTHEWAYSITSTILESWERELAID()
    {
        TerrainGrid? grid = Read(left: 1, right: 6);

        Assert.NotNull(grid);
        TerrainRoom room = Assert.Single(grid!.Rooms);
        TileOrientation[] table = TileOrientation.Table(GameSelectors, GameHelper);
        Assert.Equal((byte)((1 << table[1].Placement) | (1 << table[6].Placement)), room.Turns);

        // AND EVERY TILE IS KEPT FOR THE ROOM SEARCH: its file and the way it was laid.
        TerrainTiles laid = Assert.IsType<TerrainTiles>(grid.Tiles);
        Assert.Equal((2, 1), (laid.Width, laid.Height));
        Assert.Equal("Metadata/Terrain/Test/Arena.tdt", laid.PathAt(1, 0));
        Assert.Equal(table[1].Placement, laid.PlacementAt(0, 0));
        Assert.Equal(table[6].Placement, laid.PlacementAt(1, 0));
        Assert.Equal(string.Empty, laid.PathAt(2, 0));
    }

    [Fact]
    public void ANDWithoutTheTablesClaimsNoWayAtAll()
    {
        // Zero, not "as authored": a tile whose selector was not decoded is not known to be unturned.
        TerrainGrid? grid = Read(left: 1, right: 6, tables: false);

        Assert.NotNull(grid);
        Assert.Equal(0, Assert.Single(grid!.Rooms).Turns);
    }
}
