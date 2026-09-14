using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The WorldAreas probe, against a synthetic row built from the schema.
/// </summary>
/// <remarks>
/// AS EVER, NO ADDRESS IS VOUCHED FOR HERE - the fixture is built from the same schema the probe
/// reads, so it would pass with every computed offset wrong. What it covers is the three things
/// the probe has to get right for a capture to be worth taking: that it follows the two hops to
/// the row at all, that it measures the row size off the TABLE and says whether that agrees with
/// the arithmetic, and that a read which did not happen prints as a question mark instead of as
/// a zero. The last one is not a nicety: the first replay of this probe against an older
/// recording printed "not a map, not a hideout, not unique" about every map on the atlas, and
/// all three were simply bytes nobody had ever read.
/// </remarks>
public class WorldAreaRowProbeTests
{
    private const ulong Node = 0x30_0000;
    private const ulong Storage = 0x40_0000;
    private const ulong Data = 0x50_0000;
    private const ulong EndgameRow = 0x70_0000;
    private const ulong AreaRow = 0x80_0000;
    private const ulong Table = 0x90_0000;
    private const ulong Store = 0x91_0000;
    private const ulong ById = 0x92_0000;
    private const ulong TagEntries = 0x93_0000;
    private const ulong TagRow = 0x94_0000;
    private const ulong Strings = 0xA0_0000;

    /// <summary>Rows of the fake table, so the measured size is (span / rows).</summary>
    private const int Rows = 4;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    private static void Fill(FakeMemoryReader fake, ulong address, int length)
        => fake.Place(address, new byte[length]);

    private static ulong Text(FakeMemoryReader fake, ulong address, string text)
    {
        Fill(fake, address, 0x100);
        fake.PlaceUtf16(address, text);
        return address;
    }

    /// <summary>
    /// A node whose map leads to a WorldAreas row, with a table that states its own size.
    /// </summary>
    /// <param name="rowSize">
    /// What the table reports per row. The default is the computed size, so the probe says the
    /// two agree; a test passes something else to see it say they do not.
    /// </param>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Fixture(int rowSize = 0, bool withTags = true)
    {
        OffsetSchema schema = LoadSchema();
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef endgame = schema.Structs["EndgameMapsRow"];
        StructDef area = schema.Structs["WorldAreaDat"];
        StructDef tag = schema.Structs["TagsRow"];
        StructDef table = schema.Structs["DatTable"];
        StructDef store = schema.Structs["DatRowStore"];

        rowSize = rowSize > 0 ? rowSize : (int)area.Constants["ComputedRowSize"];
        var fake = new FakeMemoryReader();

        // node -> storage -> data -> EndgameMaps row -> WorldAreas row, the chain the reader walks
        // for a map id, one hop further than it goes.
        fake.Place(Node + (ulong)(int)node.Constants["DataStoragePtr"], Storage);
        fake.Place(Storage + (ulong)(int)node.Constants["DataPtr"], Data);
        fake.Place(Data + (ulong)data.OffsetOf("MapDataPtr"), EndgameRow);
        fake.Place(EndgameRow + (ulong)endgame.OffsetOf("WorldAreaRef"), AreaRow);
        fake.Place(EndgameRow + (ulong)endgame.OffsetOf("WorldAreaRef") + 8, Table);

        // The table object, which is where the row size is MEASURED rather than computed.
        fake.PlaceStdWString(Table + (ulong)table.OffsetOf("Path"), "Data/Balance/WorldAreas.dat", Strings + 0x8000);
        fake.Place(Table + (ulong)table.OffsetOf("RowStorePtr"), Store);
        fake.Place(Store + (ulong)store.OffsetOf("Rows"), AreaRow);
        fake.Place(Store + (ulong)store.OffsetOf("Rows") + 8, AreaRow + (ulong)(Rows * rowSize));
        fake.Place(Store + (ulong)store.OffsetOf("ByIdIndex"), ById);
        fake.Place(Store + (ulong)store.OffsetOf("ByIdIndex") + 8, ById + (ulong)(Rows * (int)store.Constants["ByIdEntrySize"]));
        fake.Place(ById + (ulong)(int)store.Constants["ByIdEntryRowAt"], AreaRow);

        // The row itself: the two columns this tool reads today, and the four it does not.
        Fill(fake, AreaRow, 0x200);
        fake.Place(AreaRow + (ulong)area.OffsetOf("IdPtr"), Text(fake, Strings, "MapLostTowers"));
        fake.Place(AreaRow + (ulong)area.OffsetOf("NamePtr"), Text(fake, Strings + 0x1000, "Lost Towers"));
        fake.Place(AreaRow + (ulong)area.OffsetOf("IsMapArea"), (byte)1);
        fake.Place(AreaRow + (ulong)area.OffsetOf("IsHideout"), (byte)0);
        fake.Place(AreaRow + (ulong)area.OffsetOf("IsUniqueMapArea"), (byte)1);

        if (withTags)
        {
            // As a begin/end pair of one entry - the reading the probe tries first.
            fake.Place(AreaRow + (ulong)area.OffsetOf("TagsArray"), TagEntries);
            fake.Place(AreaRow + (ulong)area.OffsetOf("TagsArray") + 8, TagEntries + (ulong)(int)tag.Constants["EntrySize"]);
            fake.Place(TagEntries, TagRow);
            Fill(fake, TagRow, 0x40);
            fake.Place(TagRow + (ulong)tag.OffsetOf("IdPtr"), Text(fake, Strings + 0x2000, "map_tower"));
            fake.Place(TagRow + (ulong)tag.OffsetOf("DisplayStringPtr"), Text(fake, Strings + 0x3000, "Tower"));
        }

        return (fake, schema);
    }

    private static string Run(FakeMemoryReader fake, OffsetSchema schema)
    {
        var probe = new WorldAreaRowProbe(fake, schema);
        return string.Join('\n', probe.Probe([new ProbedNode(0, Node, "MapLostTowers")]));
    }

    [Fact]
    public void ReadsTheRowTheMapIdChainStopsShortOf()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        string said = Run(fake, schema);

        Assert.Contains("\"MapLostTowers\"", said, StringComparison.Ordinal);
        Assert.Contains("\"Lost Towers\"", said, StringComparison.Ordinal);
        Assert.Contains("IsMapArea 1", said, StringComparison.Ordinal);
        Assert.Contains("IsHideout 0", said, StringComparison.Ordinal);
        Assert.Contains("IsUniqueMapArea 1", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesTheTagsToTheirOwnIds()
    {
        // The question the whole probe was built for: whether the game's tags carry what the
        // curated file calls tower, lineage, arbiter. This only shows that they are READ.
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        string said = Run(fake, schema);

        Assert.Contains("as (begin, end): 1 entries", said, StringComparison.Ordinal);
        Assert.Contains("\"map_tower\"", said, StringComparison.Ordinal);
        Assert.Contains("\"Tower\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhetherTheMeasuredRowSizeAgreesWithTheArithmetic()
    {
        OffsetSchema live = LoadSchema();
        long computed = live.Structs["WorldAreaDat"].Constants["ComputedRowSize"];

        (FakeMemoryReader agreeing, OffsetSchema schema) = Fixture();
        Assert.Contains($"rows of 0x{computed:X}, computed 0x{computed:X} - AGREE", Run(agreeing, schema), StringComparison.Ordinal);

        // And the case that matters more: a table that says something else must be reported as
        // a disagreement, because then every column offset past it is arithmetic on a wrong size.
        (FakeMemoryReader differing, OffsetSchema other) = Fixture(rowSize: (int)computed + 0x20);
        Assert.Contains("- DISAGREE", Run(differing, other), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadByteIsAQuestionMarkRatherThanAZero()
    {
        // Nothing placed at all: every flag must print as unknown, not as "false". A replay of
        // an older recording does exactly this, and a zero here would invent an answer.
        OffsetSchema schema = LoadSchema();
        var bare = new FakeMemoryReader();
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef endgame = schema.Structs["EndgameMapsRow"];

        bare.Place(Node + (ulong)(int)node.Constants["DataStoragePtr"], Storage);
        bare.Place(Storage + (ulong)(int)node.Constants["DataPtr"], Data);
        bare.Place(Data + (ulong)data.OffsetOf("MapDataPtr"), EndgameRow);
        bare.Place(EndgameRow + (ulong)endgame.OffsetOf("WorldAreaRef"), AreaRow);

        string said = Run(bare, schema);

        Assert.Contains("IsMapArea ?", said, StringComparison.Ordinal);
        Assert.Contains("IsHideout ?", said, StringComparison.Ordinal);
        Assert.Contains("IsUniqueMapArea ?", said, StringComparison.Ordinal);
        Assert.Contains("Tags unread", said, StringComparison.Ordinal);
    }
}
