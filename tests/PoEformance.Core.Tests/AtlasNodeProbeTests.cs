using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The layout probe, against a synthetic node built from the schema.
/// </summary>
/// <remarks>
/// NOTHING HERE CAN VOUCH FOR AN ADDRESS, and that is not what it is for: a fixture built from
/// the schema follows the schema anywhere, so this would pass with every candidate offset wrong.
/// What it covers is that the probe ASKS - that both badge strings are read rather than only the
/// one this tool uses, that the node's atlas row is followed through its Passives column into the
/// passive's own columns, and that a slot which is not a pointer is reported rather than followed.
/// A probe that quietly reads nothing would leave the next recording as empty as the last one,
/// which is the failure this whole diagnostic exists to prevent.
/// </remarks>
public class AtlasNodeProbeTests
{
    private const ulong Node = 0x30_0000;
    private const ulong Storage = 0x40_0000;
    private const ulong Data = 0x50_0000;
    private const ulong Child = 0x60_0000;
    private const ulong Holder = 0x62_0000;
    private const ulong Badge = 0x64_0000;
    private const ulong Row = 0x70_0000;
    private const ulong Inner = 0x71_0000;
    private const ulong Strings = 0x80_0000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>Zeroes a span, because the fake reader serves a block only when all of it is placed.</summary>
    private static void Fill(FakeMemoryReader fake, ulong address, int length)
        => fake.Place(address, new byte[length]);

    /// <summary>A string with room around it, so a read that asks for more than the text still lands.</summary>
    private static ulong Text(FakeMemoryReader fake, ulong address, string text)
    {
        Fill(fake, address, 0x100);
        fake.PlaceUtf16(address, text);
        return address;
    }

    /// <summary>A node with the whole probed neighbourhood in place.</summary>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Fixture(bool withRow = true)
    {
        OffsetSchema schema = LoadSchema();
        StructDef ui = schema.Structs["UiElementBase"];
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef atlasRow = schema.Structs["EndgameMapAtlasRow"];
        StructDef row = schema.Structs["PassiveSkillsRow"];

        int self = ui.OffsetOf("Self");
        int first = ui.OffsetOf("ChildrenFirst");
        int last = ui.OffsetOf("ChildrenLast");

        var fake = new FakeMemoryReader();

        // One child each, three levels down: the badges hang off node[0][0].
        void Element(ulong at, ulong array, ulong child)
        {
            fake.Place(at + (ulong)self, at);
            fake.Place(at + (ulong)first, array);
            fake.Place(at + (ulong)last, array + 8);
            fake.Place(array, child);
        }

        // The node's child array and its data pointer share the same block - the two-hop chain
        // reads +0x20 into the very array the children live in, which is why the element and its
        // data read as one object. In the game they sit 0x60 apart; nothing here depends on that.
        Element(Node, Storage, Child);
        fake.Place(Storage + (ulong)(int)node.Constants["DataPtr"], Data);
        Element(Child, Child + 0x800, Holder);
        Element(Holder, Holder + 0x800, Badge);

        Fill(fake, Data + (ulong)(data.OffsetOf("MapDataPtr") - 0x10), 0x58);
        fake.Place(Data + (ulong)data.OffsetOf("BiomeId"), (byte)4);
        fake.Place(Data + (ulong)data.OffsetOf("StatusBits"), (byte)0x13);

        Fill(fake, Node + (ulong)(node.OffsetOf("GridPosition") - 0x30), 0x60);
        if (withRow)
        {
            fake.Place(Data + (ulong)data.OffsetOf("AtlasRowPtr"), Row);
            Fill(fake, Row, 0x10);
            fake.Place(Row + (ulong)atlasRow.OffsetOf("PassivesRef"), Inner);
            Fill(fake, Inner, 0x50);
            fake.Place(Inner + (ulong)row.OffsetOf("IdPtr"), Text(fake, Strings, "AtlasNodeWhite"));
            fake.Place(Inner + (ulong)row.OffsetOf("IconPtr"), Text(fake, Strings + 0x1000, "Art/2DArt/node.dds"));
            fake.Place(Inner + (ulong)row.OffsetOf("GraphId"), (ushort)861);
            fake.Place(Inner + (ulong)row.OffsetOf("NamePtr"), Text(fake, Strings + 0x2000, "[DNT] District B"));
        }

        Fill(fake, Badge + (ulong)((int)node.Constants["BadgeContentNameGameHelper"] - 8), 0x90);
        fake.Place(Badge + (ulong)(int)node.Constants["BadgeContentId"], 0x0002_0064u);
        fake.Place(
            Badge + (ulong)(int)node.Constants["BadgeContentName"],
            Text(fake, Strings + 0x3000, "[DeadlyMapBoss|Deadly Map Boss]"));
        fake.Place(
            Badge + (ulong)(int)node.Constants["BadgeContentNameGameHelper"],
            Text(fake, Strings + 0x4000, "Powerful Map Boss"));

        return (fake, schema);
    }

    private static string Run(FakeMemoryReader fake, OffsetSchema schema)
    {
        var probe = new AtlasNodeProbe(fake, schema, new UiElementReader(fake, schema));
        return string.Join('\n', probe.Probe([new ProbedNode(0, Node, "MapRupture")]));
    }

    [Fact]
    public void ReadsBothBadgeStrings()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        string said = Run(fake, schema);

        // The whole point of the probe: the string this tool already uses AND the one the other
        // project reads, side by side, from the same badge in the same read.
        Assert.Contains("\"Powerful Map Boss\"", said, StringComparison.Ordinal);
        Assert.Contains("\"[DeadlyMapBoss|Deadly Map Boss]\"", said, StringComparison.Ordinal);
        Assert.Contains("id 0x00020064", said, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowsTheAtlasRowToThePassiveAndItsColumns()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        string said = Run(fake, schema);

        Assert.Contains("\"AtlasNodeWhite\"", said, StringComparison.Ordinal);
        Assert.Contains("\"Art/2DArt/node.dds\"", said, StringComparison.Ordinal);
        Assert.Contains("GraphId 861", said, StringComparison.Ordinal);
        Assert.Contains("\"[DNT] District B\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhatTheStatusByteHoldsThatNothingDecodes()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture();
        string said = Run(fake, schema);

        // 0x13 is completed AND accessible AND a third bit nobody has a name for. The unnamed
        // part is the one worth printing - it is how a third bit ever gets decoded.
        Assert.Contains("biome 4", said, StringComparison.Ordinal);
        Assert.Contains("status 0x13", said, StringComparison.Ordinal);
        Assert.Contains("0x10 nothing decodes", said, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheRowSlotIsNotAPointer()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Fixture(withRow: false);
        string said = Run(fake, schema);

        // The failure that matters: a candidate offset that is simply wrong must read as "not a
        // pointer" rather than send the probe walking into whatever the bytes happen to be.
        Assert.Contains("not a pointer", said, StringComparison.Ordinal);
        Assert.DoesNotContain("[DNT] District B", said, StringComparison.Ordinal);
    }
}
