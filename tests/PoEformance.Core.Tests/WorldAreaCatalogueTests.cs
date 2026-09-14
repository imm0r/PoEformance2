using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The catalogue, against a synthetic WorldAreas table built from the schema.
/// </summary>
/// <remarks>
/// What a fixture CAN settle here, unlike the offsets it reads: that the walk decodes the rows
/// it is given, that the tags come back as the game's own ids, that the difference report says
/// which of the file's claims the table does not carry - and that a table whose row size is not
/// the one the columns were computed against is REFUSED rather than read. That last one is the
/// test worth having: reading half a megabyte of rows at the wrong stride produces a catalogue
/// full of confident nonsense, and the only guard against it is the size the table states.
/// </remarks>
public class WorldAreaCatalogueTests
{
    private const ulong Node = 0x30_0000;
    private const ulong Storage = 0x40_0000;
    private const ulong Data = 0x50_0000;
    private const ulong EndgameRow = 0x70_0000;
    private const ulong Table = 0x90_0000;
    private const ulong Store = 0x91_0000;
    private const ulong ById = 0x92_0000;
    private const ulong Rows = 0xB0_0000;
    private const ulong Tags = 0xC0_0000;
    private const ulong Strings = 0xD0_0000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    private static ulong Text(FakeMemoryReader fake, ulong address, string text)
    {
        fake.Place(address, new byte[0x100]);
        fake.PlaceUtf16(address, text);
        return address;
    }

    /// <summary>Three areas: a tower with three tags, a unique, and a hideout.</summary>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Fixture(int rowSizeSaid = 0)
    {
        OffsetSchema schema = LoadSchema();
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef endgame = schema.Structs["EndgameMapsRow"];
        StructDef area = schema.Structs["WorldAreaDat"];
        StructDef tagRow = schema.Structs["TagsRow"];
        StructDef table = schema.Structs["DatTable"];
        StructDef store = schema.Structs["DatRowStore"];

        int size = (int)area.Constants["ComputedRowSize"];
        int entry = (int)tagRow.Constants["EntrySize"];
        int said = rowSizeSaid > 0 ? rowSizeSaid : size;
        const int Count = 3;

        var fake = new FakeMemoryReader();

        // The route: a node names its EndgameMaps row, and that row names the table.
        fake.Place(Node + (ulong)(int)node.Constants["DataStoragePtr"], Storage);
        fake.Place(Storage + (ulong)(int)node.Constants["DataPtr"], Data);
        fake.Place(Data + (ulong)data.OffsetOf("MapDataPtr"), EndgameRow);
        fake.Place(EndgameRow + (ulong)endgame.OffsetOf("WorldAreaRef") + 8, Table);

        fake.PlaceStdWString(Table + (ulong)table.OffsetOf("Path"), "Data/Balance/WorldAreas.dat", Strings + 0x9000);
        fake.Place(Table + (ulong)table.OffsetOf("RowStorePtr"), Store);
        fake.Place(Store + (ulong)store.OffsetOf("Rows"), Rows);
        fake.Place(Store + (ulong)store.OffsetOf("Rows") + 8, Rows + (ulong)(Count * said));
        fake.Place(Store + (ulong)store.OffsetOf("ByIdIndex"), ById);
        fake.Place(Store + (ulong)store.OffsetOf("ByIdIndex") + 8, ById + (ulong)(Count * (int)store.Constants["ByIdEntrySize"]));
        fake.Place(ById + (ulong)(int)store.Constants["ByIdEntryRowAt"], Rows);

        // The rows themselves, one contiguous block because that is how they are read.
        fake.Place(Rows, new byte[Count * size]);

        void Area(int index, string id, string name, byte map, byte hideout, byte unique, string[] tags)
        {
            ulong at = Rows + (ulong)(index * size);
            fake.Place(at + (ulong)area.OffsetOf("IdPtr"), Text(fake, Strings + (ulong)(0x1000 * index), id));
            fake.Place(at + (ulong)area.OffsetOf("NamePtr"), Text(fake, Strings + (ulong)(0x1000 * index) + 0x400, name));
            fake.Place(at + (ulong)area.OffsetOf("IsMapArea"), map);
            fake.Place(at + (ulong)area.OffsetOf("IsHideout"), hideout);
            fake.Place(at + (ulong)area.OffsetOf("IsUniqueMapArea"), unique);

            ulong entries = Tags + (ulong)(0x1000 * index);
            fake.Place(at + (ulong)area.OffsetOf("TagsArray"), (ulong)tags.Length);
            fake.Place(at + (ulong)area.OffsetOf("TagsArray") + 8, entries);
            for (int i = 0; i < tags.Length; i++)
            {
                ulong row = entries + 0x200 + (ulong)(i * 0x80);
                fake.Place(entries + (ulong)(i * entry), row);
                fake.Place(row + (ulong)tagRow.OffsetOf("IdPtr"), Text(fake, row + 0x400, tags[i]));
            }
        }

        Area(0, "MapLostTowers", "Lost Towers", 1, 0, 0, ["map", "map_tower", "swamp_biome"]);
        Area(1, "MapUniqueLake", "The Fractured Lake", 1, 0, 1, ["map"]);
        Area(2, "HideoutCanal", "Canal Hideout", 0, 1, 0, []);

        return (fake, schema);
    }

    /// <summary>A stand-in for data/atlas-maps.json, written the way the real loader reads it.</summary>
    private static AtlasMapNames Curated(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"atlas-maps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return AtlasMapNames.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsEveryRowTheTableReports()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        var catalogue = new WorldAreaCatalogue(fake, schema);

        Assert.True(catalogue.ReadFromNode(Node), catalogue.LastError);
        Assert.Equal(3, catalogue.All.Count);

        WorldArea tower = Assert.IsType<WorldArea>(catalogue.Of("MapLostTowers"));
        Assert.Equal("Lost Towers", tower.Name);
        Assert.True(tower.IsMapArea);
        Assert.False(tower.IsUnique);
        Assert.Equal(["map", "map_tower", "swamp_biome"], tower.Tags);

        Assert.True(Assert.IsType<WorldArea>(catalogue.Of("MapUniqueLake")).IsUnique);
        Assert.True(Assert.IsType<WorldArea>(catalogue.Of("HideoutCanal")).IsHideout);
    }

    [Fact]
    public void RefusesATableWhoseRowSizeIsNotTheOneTheColumnsAssume()
    {
        // The guard that matters. At the wrong stride every row after the first lands in the
        // middle of another one, and the ids still decode to something - a catalogue of
        // confident nonsense. The table states its size, so this is checkable rather than
        // hopeful.
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture(rowSizeSaid: 0x2C0);
        var catalogue = new WorldAreaCatalogue(fake, schema);

        Assert.False(catalogue.ReadFromNode(Node));
        Assert.Empty(catalogue.All);
        Assert.Contains("0x2C0", catalogue.LastError, StringComparison.Ordinal);
        Assert.Contains("0x2E0", catalogue.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhatTheTableHoldsAndWhatOnlyTheFileDoes()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        var catalogue = new WorldAreaCatalogue(fake, schema);
        Assert.True(catalogue.ReadFromNode(Node), catalogue.LastError);

        // A file that agrees about the tower's name and unique flag, calls it a tower the way
        // the game does, and carries one word the game has never heard of.
        string said = string.Join('\n', catalogue.Describe(Curated(
            """
            {"maps":{
              "MapLostTowers":{"name":"Lost Towers","tags":["map_tower"]},
              "MapUniqueLake":{"name":"The Fractured Lake","type":"unique","tags":["arbiter"]}
            }}
            """)));

        Assert.Contains("\"Data/Balance/WorldAreas.dat\", 3 rows of 0x2E0", said, StringComparison.Ordinal);
        Assert.Contains("IsMapArea 2   IsHideout 1   IsUniqueMapArea 1", said, StringComparison.Ordinal);
        Assert.Contains("map_tower", said, StringComparison.Ordinal);
        Assert.Contains("0 of its ids are not in the table", said, StringComparison.Ordinal);
        Assert.Contains("0 names differ", said, StringComparison.Ordinal);
        Assert.Contains("the unique flag agrees everywhere", said, StringComparison.Ordinal);
        Assert.Contains("tags the file carries that the table does not", said, StringComparison.Ordinal);
        Assert.Contains("arbiter", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AndSaysSoWhenTheFileAndTheTableDisagreeAboutUnique()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        var catalogue = new WorldAreaCatalogue(fake, schema);
        Assert.True(catalogue.ReadFromNode(Node), catalogue.LastError);

        // The reading that would quietly break a group: the file calls the tower unique and the
        // table does not. Counting it is what turns "IsUniqueMapArea is the type column" from a
        // hope into something the next capture can refute.
        string said = string.Join('\n', catalogue.Describe(Curated(
            """{"maps":{"MapLostTowers":{"name":"Lost Towers","type":"unique"}}}""")));

        Assert.Contains("THE UNIQUE FLAG DISAGREES on 1", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodeThatNamesNoTableIsSaidRatherThanThrown()
    {
        OffsetSchema schema = LoadSchema();
        var catalogue = new WorldAreaCatalogue(new FakeMemoryReader(), schema);

        Assert.False(catalogue.ReadFromNode(Node));
        Assert.Contains("EndgameMaps row", catalogue.LastError, StringComparison.Ordinal);
        Assert.Contains("not read", string.Join('\n', catalogue.Describe(AtlasMapNames.Empty)), StringComparison.Ordinal);
    }
}
