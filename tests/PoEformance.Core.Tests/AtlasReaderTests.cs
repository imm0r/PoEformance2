using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading the endgame atlas out of the interface, against a synthetic panel.
/// </summary>
/// <remarks>
/// The offsets themselves are UNCONFIRMED - ported from GameHelper2 while the game was not
/// available - and a fixture built from the schema follows the schema anywhere, so nothing
/// here can vouch for a single address. What it does cover is the walk: that a child which is
/// not a map is refused rather than parsed, that the status byte is read as the two bits it
/// is, and that a length out of game memory cannot become a loop of any size it likes. Those
/// are the parts that turn a wrong offset into a hang instead of an empty list.
/// </remarks>
public class AtlasReaderTests
{
    private const ulong UiRoot = 0x10_0000;
    private const ulong Panel = 0x20_0000;
    private const ulong Node = 0x30_0000;
    private const ulong Storage = 0x40_0000;
    private const ulong Data = 0x50_0000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>A panel with one child, laid out the way the schema describes a map node.</summary>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Atlas(
        uint flags, byte status, int gridX = 7, int gridY = 9)
    {
        OffsetSchema schema = LoadSchema();
        StructDef ui = schema.Structs["UiElementBase"];
        StructDef panel = schema.Structs["AtlasPanel"];
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];

        int children = schema.Structs["UiElement"].OffsetOf("Children");
        int self = schema.Structs["UiElement"].OffsetOf("Self");
        var fake = new FakeMemoryReader();

        // A UiElement points at ITSELF, and the reader checks it before touching anything
        // else - which is how it refuses to walk something that is not one. A fixture that
        // skips it is testing nothing, because every element would be rejected.
        void Element(ulong address) => fake.Place(address + (ulong)self, address);

        // The path from the root to the panel, one element per hop.
        ulong at = UiRoot;
        Element(UiRoot);
        foreach (string key in new[] { "PathFromUiRoot0", "PathFromUiRoot1", "PathFromUiRoot2" })
        {
            ulong next = key == "PathFromUiRoot2" ? Panel : at + 0x1000;
            Element(next);
            ulong array = at + 0x800;
            fake.Place(at + (ulong)children, array);
            fake.Place(at + (ulong)children + 8, array + (8 * ((ulong)(int)panel.Constants[key] + 1)));
            fake.Place(array + (8 * (ulong)(int)panel.Constants[key]), next);
            at = next;
        }

        // The panel's one child.
        Element(Node);
        fake.Place(Panel + (ulong)children, Panel + 0x800);
        fake.Place(Panel + (ulong)children + 8, Panel + 0x808);
        fake.Place(Panel + 0x800, Node);

        fake.Place(Node + (ulong)ui.OffsetOf("Flags"), flags);
        fake.Place(Node + (ulong)node.OffsetOf("GridPosition"), gridX);
        fake.Place(Node + (ulong)node.OffsetOf("GridPosition") + 4, gridY);
        fake.Place(Node + (ulong)(int)node.Constants["DataStoragePtr"], Storage);
        fake.Place(Storage + (ulong)(int)node.Constants["DataPtr"], Data);
        fake.Place(Data + (ulong)data.OffsetOf("StatusBits"), status);
        fake.Place(Data + (ulong)data.OffsetOf("BiomeId"), (byte)3);

        return (fake, schema);
    }

    private static AtlasReader ReaderFor(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    private static uint MapNodeFlags(OffsetSchema schema)
        => (uint)(int)schema.Structs["AtlasPanel"].Constants["MapNodeFingerprint"];

    [Fact]
    public void AMapNodeIsReadWithItsPlaceAndItsProgress()
    {
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status: 0x02);

        AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));

        Assert.Equal((7, 9), node.Grid);
        Assert.Equal(AtlasNodeState.Completed, node.State);
        Assert.Equal((byte)3, node.Biome);
    }

    [Fact]
    public void THEStatusByteIsTwoBitsRatherThanANumber()
    {
        OffsetSchema schema = LoadSchema();

        foreach ((byte status, AtlasNodeState expected) in new (byte, AtlasNodeState)[]
        {
            (0x00, AtlasNodeState.Locked),
            (0x01, AtlasNodeState.Open),
            (0x02, AtlasNodeState.Completed),
            (0x03, AtlasNodeState.Completed),   // completed wins: it is also still accessible
        })
        {
            (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status);
            AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
            Assert.Equal(expected, node.State);
        }
    }

    [Fact]
    public void BEINGOnScreenDoesNotChangeWhatANodeIs()
    {
        // The visible bit rides in the same word as the fingerprint, so a node that scrolled
        // into view would stop being a node if the bit were not masked off first.
        OffsetSchema schema = LoadSchema();
        uint visible = (uint)(int)schema.Structs["AtlasPanel"].Constants["VisibleMask"];

        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema) | visible, status: 0x01);
        Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
    }

    [Fact]
    public void ANDACHILDThatIsNotAMapIsLeftAlone()
    {
        // The marker and the region buttons share this list with entirely different layouts.
        // Parsing one as a node is what the reference records as freezing its overlay.
        OffsetSchema schema = LoadSchema();
        uint marker = (uint)(int)schema.Structs["AtlasPanel"].Constants["CurrentNodeMarkerFingerprint"];

        (FakeMemoryReader fake, _) = Atlas(marker, status: 0x02);
        Assert.Empty(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
    }

    [Fact]
    public void ACLOSEDAtlasIsAnEmptyListRatherThanAFailure()
    {
        OffsetSchema schema = LoadSchema();
        var fake = new FakeMemoryReader();

        Assert.Equal(0ul, ReaderFor(fake, schema).Panel(UiRoot));
        Assert.Empty(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
    }

    [Fact]
    public void ANDAConnectionCountOutOfMemoryCannotRunAway()
    {
        // begin and end come from the game; a wrong offset makes their difference arbitrary,
        // and the difference between "no connections" and a loop of a hundred million is this.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status: 0x01);

        int at = schema.Structs["AtlasNode"].OffsetOf("ConnectionsVector");
        fake.Place(Node + (ulong)at, 0x70_0000UL);
        fake.Place(Node + (ulong)at + 8, 0x70_0000UL + (8 * 1_000_000));

        AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
        Assert.Empty(node.Connections);
    }
}
