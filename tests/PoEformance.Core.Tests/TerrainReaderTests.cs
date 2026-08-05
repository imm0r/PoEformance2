using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading the terrain out of a synthetic process: the grid, and the per-tile heights.
/// </summary>
/// <remarks>
/// Built against the SHIPPED schema rather than hand-picked constants, so a drifted offset
/// fails here instead of producing a grid with the right area and the wrong shape - which is
/// the failure mode that draws a convincing, completely wrong map.
/// </remarks>
public class TerrainReaderTests
{
    private const ulong AreaInstance = 0x0000_0200_0000_0000;
    private const ulong GridData = 0x0000_0200_0100_0000;
    private const ulong TileData = 0x0000_0200_0200_0000;

    /// <summary>GameHelper2's height constant, negated - the game counts height downward.</summary>
    private const float HeightScale = -7.8125f;

    private const int TilesX = 3;
    private const int TilesY = 2;

    /// <summary>
    /// Lays out an area: 3x2 tiles of open ground, with the tile heights supplied raw.
    /// </summary>
    private static (FakeMemoryReader Reader, OffsetSchema Schema, int Stride, int Rows) Area(
        short heightMultiplier = 2, short[]? rawHeights = null, bool withTileDetails = true)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef area = schema.Structs["AreaInstance"];
        StructDef terrain = schema.Structs["TerrainMetadata"];
        ulong terrainBase = AreaInstance + (ulong)area.OffsetOf("TerrainMetadata");

        int width = TilesX * TerrainGrid.CellsPerTile;
        int rows = TilesY * TerrainGrid.CellsPerTile;
        int stride = (width + 1) / 2;

        var cells = new byte[stride * rows];
        Array.Fill(cells, (byte)0x11);   // both nibbles set: every cell walkable

        var fake = new FakeMemoryReader()
            .Place(GridData, cells)
            .Place<uint>(AreaInstance + (ulong)area.OffsetOf("CurrentAreaHash"), 0xABCD1234)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData"), GridData)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData") + 8, GridData + (ulong)cells.Length)
            .Place<int>(terrainBase + (ulong)terrain.OffsetOf("BytesPerRow"), stride)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesX"), TilesX)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesY"), TilesY)
            .Place<short>(terrainBase + (ulong)terrain.OffsetOf("TileHeightMultiplier"), heightMultiplier);

        if (withTileDetails)
        {
            const int entrySize = 0x38;
            int tileHeight = schema.Structs["TileStruct"].OffsetOf("TileHeight");
            var tiles = new byte[TilesX * TilesY * entrySize];

            for (int i = 0; i < TilesX * TilesY; i++)
            {
                short raw = rawHeights is not null ? rawHeights[i] : (short)0;
                BitConverter.GetBytes(raw).CopyTo(tiles, (i * entrySize) + tileHeight);
            }

            fake.Place(TileData, tiles)
                .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("TileDetailsPtr"), TileData)
                .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("TileDetailsPtr") + 8, TileData + (ulong)tiles.Length);
        }

        return (fake, schema, stride, rows);
    }

    [Fact]
    public void ReadsTheGridAtTheSizeTheTileCountReports()
    {
        (FakeMemoryReader fake, OffsetSchema schema, _, int rows) = Area();
        TerrainGrid? grid = new TerrainReader(fake, schema).Read(AreaInstance, nowMs: 0);

        Assert.NotNull(grid);
        Assert.Equal(TilesX * TerrainGrid.CellsPerTile, grid!.Width);
        Assert.Equal(rows, grid.Height);
        Assert.True(grid.IsWalkable(0, 0));
    }

    [Fact]
    public void TileHeightsComeBackInTheUnitsEntitiesReport()
    {
        // raw * multiplier * -7.8125, GameHelper2's formula. The sign is part of it: the
        // game counts height downward and everything the map projects counts it up, so
        // dropping it inverts every slope on the map.
        short[] raw = [0, 4, -4, 16, 1, 0];
        (FakeMemoryReader fake, OffsetSchema schema, _, _) = Area(heightMultiplier: 2, raw);

        TerrainGrid? grid = new TerrainReader(fake, schema).Read(AreaInstance, nowMs: 0);
        Assert.NotNull(grid);
        Assert.True(grid!.HasHeights);

        int cells = TerrainGrid.CellsPerTile;
        Assert.Equal(0f, grid.HeightAt(0, 0));
        Assert.Equal(4 * 2 * HeightScale, grid.HeightAt(cells, 0));
        Assert.Equal(-4 * 2 * HeightScale, grid.HeightAt(2 * cells, 0));
        Assert.Equal(16 * 2 * HeightScale, grid.HeightAt(0, cells));      // row 1 starts at index 3
    }

    [Fact]
    public void MissingTileDetailsLeavesTheMapFlatRatherThanFailing()
    {
        // The heights are a correction, not the feature. A null vector - which is what the
        // ordinary "terrain is still populating" state looks like - has to leave a usable
        // grid behind, drawn the way it was before heights existed.
        (FakeMemoryReader fake, OffsetSchema schema, _, _) = Area(withTileDetails: false);

        TerrainGrid? grid = new TerrainReader(fake, schema).Read(AreaInstance, nowMs: 0);

        Assert.NotNull(grid);
        Assert.False(grid!.HasHeights);
        Assert.Equal(0f, grid.HeightAt(10, 10));
        Assert.True(grid.IsWalkable(5, 5));
    }

    [Fact]
    public void ATileVectorTooShortForTheAreaIsRefused()
    {
        // A drifted offset can land on a vector that reads perfectly well and describes
        // something else. Requiring one entry per tile is the cheap check that catches it,
        // and failing it costs the correction rather than the map.
        OffsetSchema schema = RealSessionTests.Schema();
        (FakeMemoryReader fake, _, _, _) = Area(rawHeights: [1, 2, 3, 4, 5, 6]);

        StructDef terrain = schema.Structs["TerrainMetadata"];
        ulong details = AreaInstance
            + (ulong)schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata")
            + (ulong)terrain.OffsetOf("TileDetailsPtr");

        // Two tiles' worth of bytes for a six-tile area.
        fake.Place<ulong>(details + 8, TileData + (2 * 0x38));

        TerrainGrid? grid = new TerrainReader(fake, schema).Read(AreaInstance, nowMs: 0);

        Assert.NotNull(grid);
        Assert.False(grid!.HasHeights);
    }
}
