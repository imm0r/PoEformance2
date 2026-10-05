using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// A tileset's ground files: the MaterialsList (.mtd), the override list (.tmo) and a ground type's name (.gt).
/// </summary>
/// <remarks>
/// THE TEXTS ARE THE GAME'S OWN, from a tile dump of <c>cliffcvm_stroma1</c>: the crimson merchant's
/// MaterialsList and the hive's, which between them hold every branch of poe_data_tools' grammar -
/// a single choice with no weight line, weighted choices with the trailing number, the extra pair
/// and its choices, comments, and backslashed paths.
/// </remarks>
public class TilesetFilesTests
{
    /// <summary>metadata/terrain/maps/uniquemerchant_02/crimsonmerchant/materials.mtd, as the dump printed it.</summary>
    private const string Merchant = """
        version 5
        3 0
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand02.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand03.mat"
        35 40 25 16

        "Stromatolite" 3 0
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand02.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand03.mat"
        35 40 25 16

        "StromatoliteTrim" 3 0
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand02.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand03.mat"
        35 40 25 16

        "Pool" 3 0
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand02.mat"
        "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand03.mat"
        35 40 25 16
        """;

    /// <summary>The front of metadata/terrain/maps/hive/materials.mtd, with a single-choice group and a doodad layer added.</summary>
    private const string Hive = """
        version 5
        // MAIN SAND AREAS
        5 2
            "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClay01.mat"
            "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClay02.mat"
            "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClay03.mat"
            "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClayTunnels01.mat"
            "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClayTunnels02.mat"
            15 15 15 35 35 32
                20 1
                "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClayHoles01.mat"
                "Art\Models\Terrain\Jungle\Tiles\AntHill\Textures\Ground\G_ANT_JungleClayHoles02.mat"
        /* OUT OF BOUNDS FILLS */
        "Stromatolite" 1 0
            "Art/Ground/Clay.mat""Art/Ground/Pebbles.dlp" "Art/Ground/Roots.dlp"
        "DesertDune" 2 0
            "Art/Ground/Dune01.mat"
            "Art/Ground/Dune02.mat"
            25 25 32
        """;

    [Fact]
    public void AMATERIALSLISTMapsEachGroundTypeNameToItsWeightedMaterials()
    {
        GroundMaterials list = GroundMaterials.Parse(Merchant);

        Assert.True(list.Ready, list.Why);
        Assert.Equal(5, list.Version);
        Assert.Equal(["", "Stromatolite", "StromatoliteTrim", "Pool"], list.Groups.Select(one => one.Name));

        GroundGroup? stromatolite = list.Named("Stromatolite");
        Assert.NotNull(stromatolite);
        Assert.Equal(
            ["Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat",
             "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand02.mat",
             "Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand03.mat"],
            stromatolite.Choices.Select(one => one.Material));
        Assert.Equal([35, 40, 25], stromatolite.Weights);
        Assert.Equal(16, stromatolite.Trailing);
        Assert.Empty(stromatolite.Extras);
        Assert.Equal(-1, stromatolite.ExtraNumber);

        // THE GROUP WITH NO NAME is the file's first, and is asked for by the empty name.
        Assert.Same(list.Groups[0], list.Named(string.Empty));
        Assert.Null(list.Named("DesertDune"));
    }

    [Fact]
    public void ANDEVERYOTHERBRANCHOfTheGrammarReadsTheExtrasTheLayersAndTheSingleChoiceWithoutWeights()
    {
        GroundMaterials list = GroundMaterials.Parse(Hive);

        Assert.True(list.Ready, list.Why);
        Assert.Equal(["", "Stromatolite", "DesertDune"], list.Groups.Select(one => one.Name));

        GroundGroup main = list.Groups[0];
        Assert.Equal(5, main.Choices.Count);
        Assert.Equal("Art/Models/Terrain/Jungle/Tiles/AntHill/Textures/Ground/G_ANT_JungleClay01.mat", main.Choices[0].Material);
        Assert.Equal([15, 15, 15, 35, 35], main.Weights);
        Assert.Equal(32, main.Trailing);
        Assert.Equal(20, main.ExtraNumber);
        Assert.True(main.ExtraFlag);
        Assert.Equal(
            ["Art/Models/Terrain/Jungle/Tiles/AntHill/Textures/Ground/G_ANT_JungleClayHoles01.mat",
             "Art/Models/Terrain/Jungle/Tiles/AntHill/Textures/Ground/G_ANT_JungleClayHoles02.mat"],
            main.Extras.Select(one => one.Material));

        // ONE CHOICE HAS NO WEIGHT LINE, and its layers end where the next group's quoted name begins -
        // the first of them with no space after the material, as poe_data_tools notes the files do.
        GroundGroup clay = list.Named("Stromatolite")!;
        Assert.Equal("Art/Ground/Clay.mat", Assert.Single(clay.Choices).Material);
        Assert.Equal(["Art/Ground/Pebbles.dlp", "Art/Ground/Roots.dlp"], clay.Choices[0].Layers);
        Assert.Empty(clay.Weights);
        Assert.Equal(-1, clay.Trailing);

        Assert.Equal([25, 25], list.Named("desertdune")!.Weights);
    }

    [Fact]
    public void AGROUPThatStopsShortIsNamedAndTheGroupsBeforeItAreKept()
    {
        GroundMaterials list = GroundMaterials.Parse(
            "version 5\n1 0\n\"Art/Dirt.mat\"\n\"Sand\" 2 0\n\"Art/Sand01.mat\"\n\"Art/Sand02.mat\"\n60 40\n");

        Assert.False(list.Ready);
        Assert.Equal("group 2: no number after the weights", list.Why);
        Assert.Equal("Art/Dirt.mat", Assert.Single(list.Groups).Choices[0].Material);

        Assert.Equal("no version line", GroundMaterials.Parse("\"Sand\" 1 0\n\"Art/Sand.mat\"").Why);
        Assert.Equal("group 1: fewer choices than its count", GroundMaterials.Parse("version 5\n2 0\n\"Art/Sand.mat\"").Why);
        Assert.False(GroundMaterials.Read(null).Ready);
    }

    [Fact]
    public void ANOVERRIDELISTPairsTwoMaterialsPerLineAndSkipsComments()
    {
        IReadOnlyList<MaterialOverride> swaps = MaterialOverrides.Parse(
            """
            version 1

            //PLEASE DELETE OR REPLACE WITH JUNGLE OUTSKIRTS or SAVANNA ASSETS
            "Art/Textures/Environment/desert/Shore/RockyLedgec.mat" "Art/Textures/Environment/prismac.mat"
            "Art\Textures\Rocks.mat""Art/Textures/Environment/prismac.mat" trailing words
            "Art/Textures/OnlyOne.mat"
            """);

        Assert.Equal(
            [new MaterialOverride("Art/Textures/Environment/desert/Shore/RockyLedgec.mat", "Art/Textures/Environment/prismac.mat"),
             new MaterialOverride("Art/Textures/Rocks.mat", "Art/Textures/Environment/prismac.mat")],
            swaps);
        Assert.Empty(MaterialOverrides.Read(null));
    }

    [Fact]
    public void AGROUNDTYPEIsNamedByItsFirstLine()
    {
        Assert.Equal("Stromatolite", GroundType.NameOf("Stromatolite\r\n1 1 0 0\r\n"));
        Assert.Equal("DesertDune", GroundType.NameOf("\r\n  DesertDune  \r\n0 1 0 0"));
        Assert.Equal(string.Empty, GroundType.NameOf(null));
        Assert.Equal(string.Empty, GroundType.Name(null));
    }

    [Fact]
    public void ATILELISTNamesItsIncludesAndItsTilesWhateverIsAroundThem()
    {
        TileList list = TileList.Parse(
            """
            // DESERT
            include "shared.tst"
            include "Metadata\Terrain\Common\common.tst"
            10 "Metadata/Terrain/Desert/A.tdt" r90 r180
            "Metadata\Terrain\Desert\B.tdt"
            5"Metadata/Terrain/Desert/C.tdt"
            "Metadata/Terrain/Desert/Room.arm"
            """);

        Assert.Equal(["shared.tst", "Metadata/Terrain/Common/common.tst"], list.Includes);
        Assert.Equal(["Metadata/Terrain/Desert/A.tdt", "Metadata/Terrain/Desert/B.tdt", "Metadata/Terrain/Desert/C.tdt"], list.Tiles);
        Assert.Empty(TileList.Read(null).Tiles);
    }

    [Fact]
    public void ATILESETNamesItsFilesByKeyAndABareNameLivesBesideIt()
    {
        const string Text = "version 3\r\nTileSet\t\t\"tiles.tst\"\r\nTileSetExtra \"no\"\r\nMaterialsList Metadata\\Terrain\\x.mtd\r\n";

        Assert.Equal("tiles.tst", TilesetFile.Value(Text, "TileSet"));
        Assert.Equal("Metadata/Terrain/x.mtd", TilesetFile.Value(Text, "MaterialsList"));
        Assert.Null(TilesetFile.Value(Text, "TileMaterialOverrides"));
        Assert.Equal("metadata/terrain/maps/swarm/tiles.tst", TilesetFile.Beside("metadata/terrain/maps/swarm/master.tsi", "tiles.tst"));
        Assert.Equal("Metadata/Terrain/x.mtd", TilesetFile.Beside("metadata/terrain/maps/swarm/master.tsi", "Metadata/Terrain/x.mtd"));
    }

    /// <summary>
    /// The index files each tile against the tilesets that place it - by its whole path, includes followed.
    /// </summary>
    /// <remarks>
    /// The desert's list takes in a shared one, which takes the desert's back in - a loop that must
    /// end. The hive places a tile of the same FILE NAME in another folder, which is another tile,
    /// and names the desert's tile twice, which is still one tileset. A tileset with no tile list is
    /// not counted as searched.
    /// </remarks>
    [Fact]
    public void THEINDEXFilesEachTileAgainstTheTilesetsThatPlaceItByItsWholePath()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["metadata/terrain/maps/desert/master.tsi"] = Encoding.Unicode.GetBytes(
                "version 3\r\nTileSet \"tiles.tst\"\r\nTileMaterialOverrides \"swaps.tmo\"\r\n"),
            ["metadata/terrain/maps/desert/tiles.tst"] = Encoding.Unicode.GetBytes(
                "include \"shared.tst\"\r\n\"Metadata/Terrain/Desert/Cliff.tdt\"\r\n"),
            ["metadata/terrain/maps/desert/shared.tst"] = Encoding.Unicode.GetBytes(
                "include \"tiles.tst\"\r\n\"Metadata/Terrain/Desert/Shared.tdt\"\r\n"),
            ["metadata/terrain/maps/desert/swaps.tmo"] = Encoding.Unicode.GetBytes(
                "version 1\r\n\"Art/Rock.mat\" \"Art/Vastiri/Rock.mat\"\r\n\"Art/Rock.mat\" \"Art/Later.mat\"\r\n"),
            ["metadata/terrain/maps/hive/master.tsi"] = Encoding.Unicode.GetBytes(
                "version 3\r\nTileSet \"Metadata/Terrain/Maps/Hive/tiles.tst\"\r\n"),
            ["Metadata/Terrain/Maps/Hive/tiles.tst"] = Encoding.Unicode.GetBytes(
                "\"Metadata/Terrain/AntNest/Cliff.tdt\"\r\n\"Metadata/Terrain/Desert/Cliff.tdt\"\r\n10 \"Metadata/Terrain/Desert/Cliff.tdt\"\r\n"),
            ["metadata/terrain/maps/empty/master.tsi"] = Encoding.Unicode.GetBytes("version 3\r\n"),
        };
        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);

        TilesetIndex index = TilesetIndex.Build(
            read, ["metadata/terrain/maps/desert/master.tsi", "metadata/terrain/maps/hive/master.tsi", "metadata/terrain/maps/empty/master.tsi"]);

        Assert.Equal(2, index.Searched);
        Assert.Equal(
            ["metadata/terrain/maps/desert/master.tsi", "metadata/terrain/maps/hive/master.tsi"],
            index.Of("metadata/terrain/desert/cliff.tdt"));
        Assert.Equal(["metadata/terrain/maps/desert/master.tsi"], index.Of("Metadata\\Terrain\\Desert\\Shared.tdt"));
        Assert.Empty(index.Of("Metadata/Terrain/Desert/Nowhere.tdt"));

        (string tile, IReadOnlyList<string> sets) = Assert.Single(index.Alike("Metadata/Terrain/Desert/Cliff.tdt"));
        Assert.Equal("metadata/terrain/antnest/cliff.tdt", tile);
        Assert.Equal(["metadata/terrain/maps/hive/master.tsi"], sets);

        // THE FIRST LINE FOR A MATERIAL STANDS, and a tileset without a list swaps nothing.
        IReadOnlyDictionary<string, string> swaps = TilesetIndex.Overrides(read, "metadata/terrain/maps/desert/master.tsi");
        Assert.Equal("Art/Vastiri/Rock.mat", Assert.Single(swaps).Value);
        Assert.Equal("Art/Vastiri/Rock.mat", swaps["art/rock.mat"]);
        Assert.Empty(TilesetIndex.Overrides(read, "metadata/terrain/maps/hive/master.tsi"));

        Assert.Equal("maps/desert", TilesetIndex.Short("metadata/terrain/maps/desert/master.tsi"));
        Assert.Same(TilesetIndex.Empty, TilesetIndex.Build(read, []));
    }

    [Fact]
    public void ATILEKEYCarriesEveryChoiceAndReadsBackTheSame()
    {
        Assert.Equal("Metadata/Terrain/A.tdt", new TileKey("Metadata/Terrain/A.tdt").ToString());
        Assert.Equal("Metadata/Terrain/A.tdt|bare", new TileKey("Metadata/Terrain/A.tdt", Ground: false).ToString());

        var every = new TileKey("Metadata/Terrain/A.tdt", Ground: false, Walls: false, Tileset: "metadata/terrain/maps/crimsonshores/master.tsi");
        string key = every.ToString();
        Assert.Equal("Metadata/Terrain/A.tdt|bare+unwalled+set=metadata/terrain/maps/crimsonshores/master.tsi", key);
        Assert.Equal(every, TileKey.Read(key));

        var only = new TileKey("Metadata/Terrain/A.tdt", Tileset: "metadata/terrain/maps/aridplains/master.tsi");
        Assert.Equal(only, TileKey.Read(only.ToString()));
        Assert.Equal(new TileKey("Metadata/Terrain/A.tdt"), TileKey.Read("Metadata/Terrain/A.tdt"));
    }
}
