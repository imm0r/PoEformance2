using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding places in the SHAPE of the ground - boss arenas above all.
/// </summary>
/// <remarks>
/// The owner's observation, and the whole reason this can work: an endgame map is generated at
/// random, but a boss room is not. The arena is fixed layout, so the tiles it is built from
/// keep their names whatever the map around them looks like - and nothing in the entity list
/// says where the boss will be before it exists.
/// </remarks>
public class TerrainLandmarkTests
{
    private const string Arena = "Metadata/Terrain/Woods/GrimTangle/feature/BossArena_01.tdt";
    private const string Wall = "Metadata/Terrain/Woods/Slash/Wall_01.tdt";

    private static TerrainTile Tile(string path, int column, int row, int subX = 0, int subY = 0, byte rotation = 0)
        => new(path, column, row, subX, subY, rotation);

    [Fact]
    public void ATileNamedForAnArenaBecomesAPlace()
    {
        // No curation needed, which is the only way this can work in the endgame: the five
        // maps somebody has written down are nothing against the hundreds that exist.
        List<TerrainLandmark> found = TerrainLandmarks.Find([Tile(Arena, 10, 20)]);

        TerrainLandmark landmark = Assert.Single(found);
        Assert.Equal(PoiKind.BossArena, landmark.Kind);
        Assert.Equal("Boss Arena", landmark.Name);
    }

    [Fact]
    public void OrdinarySceneryIsNotAPlace()
        => Assert.Empty(TerrainLandmarks.Find([Tile(Wall, 1, 1), Tile(Wall, 2, 1)]));

    [Fact]
    public void AnArenaOfManyTilesIsONEPlace()
    {
        // The fault the AHK tool hit and fixed the same way: an arena is a block of tiles, so
        // one landmark per tile stacks a dozen markers on one room.
        var block = new List<TerrainTile>();
        for (int x = 10; x < 14; x++)
        {
            for (int y = 20; y < 24; y++)
            {
                block.Add(Tile(Arena, x, y));
            }
        }

        TerrainLandmark landmark = Assert.Single(TerrainLandmarks.Find(block));
        Assert.Equal(16, landmark.Tiles);

        // ...and it sits in the MIDDLE of the block, not on whichever tile came first.
        int centre = (int)((11.5f + 0.5f) * TerrainGrid.CellsPerTile);
        Assert.InRange(landmark.GridX, centre - TerrainGrid.CellsPerTile, centre + TerrainGrid.CellsPerTile);
    }

    [Fact]
    public void TwoArenasFarApartAreTwoPlaces()
    {
        List<TerrainLandmark> found = TerrainLandmarks.Find(
            [Tile(Arena, 10, 10), Tile(Arena, 11, 10), Tile(Arena, 60, 70), Tile(Arena, 61, 70)]);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void ATileNamedForAnArenaAndUsedEVERYWHEREIsScenery()
    {
        // The other half of the lesson. A wall or a floor that happens to carry the word turns
        // up all over an area, and marking every instance buries the real thing - which is
        // exactly what happened when a reused wall put "Boss" across a whole map.
        var scattered = new List<TerrainTile>();
        for (int i = 0; i < 8; i++)
        {
            scattered.Add(Tile(Arena, i * 20, i * 20));
        }

        Assert.Empty(TerrainLandmarks.Find(scattered));
    }

    [Fact]
    public void ACuratedTileGetsItsRealName()
    {
        // What curation is FOR: the shape of the ground can say "an arena is here", and only a
        // written-down name can say which boss and what it drops.
        var curated = new Dictionary<string, string>
        {
            ["Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdtx:5-y:0"] = "Beira of the Rotten (10% Cold Res)",
        };

        List<TerrainLandmark> found = TerrainLandmarks.Find(
            [Tile("Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdt", 4, 7, subX: 5, subY: 0)],
            curated);

        Assert.Equal("Beira of the Rotten (10% Cold Res)", Assert.Single(found).Name);
    }

    [Fact]
    public void TheCuratedKeySwapsItsIdsOnAnOddRotation()
    {
        // How the tile was PLACED changes the key, which is how GameHelper2 writes it and how
        // the curated data is therefore written. Reading it any other way misses every rotated
        // instance - and rotated instances are most of them.
        Assert.Equal(
            "tile.tdtx:5-y:0",
            TerrainLandmarks.KeyFor(Tile("tile.tdt", 0, 0, subX: 5, subY: 0, rotation: 2)));

        Assert.Equal(
            "tile.tdtx:0-y:5",
            TerrainLandmarks.KeyFor(Tile("tile.tdt", 0, 0, subX: 5, subY: 0, rotation: 3)));
    }

    [Fact]
    public void ACuratedNameWinsOverTheGenericOne()
    {
        var curated = new Dictionary<string, string> { [$"{Arena}x:0-y:0"] = "Den of the Druid" };

        Assert.Equal("Den of the Druid", Assert.Single(TerrainLandmarks.Find([Tile(Arena, 5, 5)], curated)).Name);
    }

    [Fact]
    public void EveryPlaceHasAStableIdentityThatCannotBeAnEntity()
    {
        // Routes are keyed by ONE number, and terrain landmarks have no address to use - so
        // the identity is derived from the place itself, which keeps a route to it a route to
        // it after a re-scan, and carries the top bit, which no heap pointer ever does.
        List<TerrainLandmark> first = TerrainLandmarks.Find([Tile(Arena, 10, 20)]);
        List<TerrainLandmark> again = TerrainLandmarks.Find([Tile(Arena, 10, 20)]);

        Assert.Equal(first[0].Id, again[0].Id);
        Assert.True(first[0].Id > 0x8000_0000_0000_0000UL, "a landmark id must not look like a pointer");

        // A different place is a different identity.
        Assert.NotEqual(first[0].Id, TerrainLandmarks.Find([Tile(Arena, 40, 20)])[0].Id);
    }

    [Fact]
    public void ThePlaceSitsAtTheTilesCENTRE()
    {
        // A tile is 23 cells across, so anchoring at its corner puts every landmark half a
        // tile - about 125 world units - toward the map's origin. The AHK tool shipped that
        // offset and then had to correct it.
        TerrainLandmark landmark = Assert.Single(TerrainLandmarks.Find([Tile(Arena, 4, 6)]));

        Assert.Equal((int)(4.5f * TerrainGrid.CellsPerTile), landmark.GridX);
        Assert.Equal((int)(6.5f * TerrainGrid.CellsPerTile), landmark.GridY);
    }

    [Theory]
    [InlineData("Metadata/Terrain/Woods/GrimTangle/feature/BossArena_01.tdt", "Boss Arena")]
    [InlineData("Metadata/Terrain/Woods/Slash/HagWitchArena_07.tdt", "Hag Witch Arena")]
    [InlineData("Metadata/Terrain/Maps/LostTowers/Tiles/PillarArena01.tdt", "Pillar Arena")]
    public void TheNameComesFromTheTilesOwnFile(string path, string expected)
        => Assert.Equal(expected, TerrainLandmarks.NameFor(path));
}
