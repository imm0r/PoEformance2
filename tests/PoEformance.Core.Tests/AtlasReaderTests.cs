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
    private const ulong Edges = 0x60_0000;

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
    /// <param name="open">
    /// Whether the panel is SHOWING. Shut by default, because that is the state the fixture
    /// otherwise describes: everything present in the tree and nothing on the screen.
    /// </param>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Atlas(
        uint flags, byte status, int gridX = 7, int gridY = 9, bool open = false)
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

        if (open)
        {
            fake.Place(Panel + (ulong)ui.OffsetOf("Flags"), (uint)ui.Constants["FlagIsVisible"]);
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

    /// <summary>Puts a table of atlas lines on the panel, laid out the way the schema says.</summary>
    /// <remarks>
    /// One contiguous block rather than a field at a time, because that is how it is read: the
    /// whole table comes back in a single call, and a fixture of scattered writes would pass a
    /// reader that issued a call per edge.
    /// </remarks>
    private static void PlaceEdges(
        FakeMemoryReader fake, OffsetSchema schema, ((int X, int Y) From, (int X, int Y) To)[] edges)
    {
        StructDef panel = schema.Structs["AtlasPanel"];
        int stride = (int)panel.Constants["ConnectionEdgeStride"];
        int source = (int)panel.Constants["ConnectionEdgeSource"];
        int target = (int)panel.Constants["ConnectionEdgeTarget"];

        var table = new byte[edges.Length * stride];
        for (int i = 0; i < edges.Length; i++)
        {
            Span<byte> at = table.AsSpan(i * stride, stride);
            BitConverter.TryWriteBytes(at[source..], edges[i].From.X);
            BitConverter.TryWriteBytes(at[(source + 4)..], edges[i].From.Y);
            BitConverter.TryWriteBytes(at[target..], edges[i].To.X);
            BitConverter.TryWriteBytes(at[(target + 4)..], edges[i].To.Y);
        }

        fake.Place(Edges, table);
        fake.Place(Panel + (ulong)panel.OffsetOf("ConnectionsVector"), Edges);
        fake.Place(Panel + (ulong)panel.OffsetOf("ConnectionsVector") + 8, Edges + (ulong)table.Length);
    }

    private static AtlasReader ReaderFor(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    private static uint MapNodeFlags(OffsetSchema schema)
        => (uint)(int)schema.Structs["AtlasPanel"].Constants["MapNodeFingerprint"];


    /// <summary>
    /// A tree with a node grid somewhere OTHER than the schema's path, for the hunt.
    /// </summary>
    /// <remarks>
    /// Which is the situation the hunt exists for: the path is a position in a list, and a
    /// patch that inserts one panel above the atlas moves it without changing anything else.
    /// </remarks>
    private static (FakeMemoryReader Fake, OffsetSchema Schema) Misplaced(int nodes, uint flags)
    {
        OffsetSchema schema = LoadSchema();
        int children = schema.Structs["UiElement"].OffsetOf("Children");
        int self = schema.Structs["UiElement"].OffsetOf("Self");
        int flagsAt = schema.Structs["UiElementBase"].OffsetOf("Flags");
        var fake = new FakeMemoryReader();

        void Element(ulong address) => fake.Place(address + (ulong)self, address);
        void Give(ulong parent, ulong[] kids)
        {
            ulong array = parent + 0x800;
            fake.Place(parent + (ulong)children, array);
            fake.Place(parent + (ulong)children + 8, array + (8 * (ulong)kids.Length));
            for (int i = 0; i < kids.Length; i++)
            {
                fake.Place(array + (8 * (ulong)i), kids[i]);
            }
        }

        // Root -> [decoy, panel]. The decoy has a few children of assorted kinds; the panel
        // has the grid. Neither is at the schema's path, so Panel() finds nothing.
        const ulong root = 0x10_0000;
        const ulong decoy = 0x11_0000;
        const ulong panel = 0x12_0000;

        Element(root);
        Element(decoy);
        Element(panel);
        Give(root, [decoy, panel]);

        var assorted = new ulong[40];
        for (int i = 0; i < assorted.Length; i++)
        {
            assorted[i] = 0x20_0000 + ((ulong)i * 0x1000);
            Element(assorted[i]);
            fake.Place(assorted[i] + (ulong)flagsAt, (uint)(0x1000 + i));   // all different
        }

        Give(decoy, assorted);

        var grid = new ulong[nodes];
        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = 0x30_0000 + ((ulong)i * 0x1000);
            Element(grid[i]);
            fake.Place(grid[i] + (ulong)flagsAt, flags);
        }

        Give(panel, grid);
        return (fake, schema);
    }

    [Fact]
    public void WHENThePathIsWrongItSaysWhereTheNodesActuallyAre()
    {
        // The whole point: a child path is a position in somebody else's list, so a patch that
        // inserts one panel above the atlas breaks every atlas offset at once, and the only
        // symptom is an overlay that draws nothing. Hunting for the SHAPE turns that from a
        // trawl through the interface browser into a line to paste into the schema.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Misplaced(nodes: 120, MapNodeFlags(schema));

        IReadOnlyList<string> said = ReaderFor(fake, schema).Describe(UiRoot, new UiScale(2560, 1600, 0));
        string all = string.Join("\n", said);

        Assert.Contains("did not resolve", all);
        Assert.Contains("look like a grid of map nodes", all);
        Assert.Contains("child path 1", all);              // root -> child 1 is the panel
        Assert.Contains("120 children", all);
        Assert.Contains("the map-node fingerprint", all);  // and this one is even recognised
    }

    [Fact]
    public void ANDItPrefersTheGridToAListOfAssortedThings()
    {
        // The decoy has forty children, which is plenty - but they are forty different kinds,
        // and a grid of map nodes is not. Without that rule the hunt names whichever list is
        // biggest, which on a real interface is never the atlas.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Misplaced(nodes: 35, MapNodeFlags(schema));

        string all = string.Join("\n", ReaderFor(fake, schema).Hunt(UiRoot));

        Assert.Contains("child path 1", all);
        Assert.DoesNotContain("child path 0", all);
    }

    [Fact]
    public void ANDItSaysSoWhenThereIsNothingLikeAnAtlasAtAll()
    {
        // The answer when the atlas is simply shut, which is most of the time - and it must
        // not read like a failure, because it is not one.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Misplaced(nodes: 3, MapNodeFlags(schema));

        string all = string.Join("\n", ReaderFor(fake, schema).Hunt(UiRoot));
        Assert.Contains("nothing in the interface looks like a grid of map nodes", all);
    }

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
    public void ASHUTAtlasStillHasEveryOneOfItsNodesInTheTree()
    {
        // Which is why counting them says nothing about whether anybody is looking at it, and
        // why the overlay went on writing map names over the game after the player had left
        // the atlas: closing it clears the panel's visible bit and leaves everything else
        // exactly where it was. The nodes below still read - they are just not on the screen.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader shut, _) = Atlas(MapNodeFlags(schema), status: 0x01);

        Assert.Single(ReaderFor(shut, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
        Assert.False(ReaderFor(shut, schema).IsOpen(Panel));

        (FakeMemoryReader open, _) = Atlas(MapNodeFlags(schema), status: 0x01, open: true);
        Assert.True(ReaderFor(open, schema).IsOpen(Panel));
    }

    [Fact]
    public void ANDAPanelThatIsNotThereAtAllIsNotOpenEither()
    {
        OffsetSchema schema = LoadSchema();
        Assert.False(ReaderFor(new FakeMemoryReader(), schema).IsOpen(0));
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

        int at = schema.Structs["AtlasPanel"].OffsetOf("ConnectionsVector");
        fake.Place(Panel + (ulong)at, Edges);
        fake.Place(Panel + (ulong)at + 8, Edges + (0x14 * 1_000_000));

        AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
        Assert.Empty(node.Connections);
    }

    [Fact]
    public void THELinesAreOneTableOnThePanelRatherThanAListOnEachNode()
    {
        // The bug this is here for: read at the same offset on a NODE, the two words are
        // whatever that node's own bytes happen to be. Most nodes then have no connections at
        // all and the occasional one has invented ones - which draws a route to the right map
        // along a way nobody can walk, and looks like a pathfinder that picked a long way round.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status: 0x01, gridX: 3, gridY: 4);

        PlaceEdges(fake, schema, [((3, 4), (5, 6)), ((7, 8), (3, 4))]);

        AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));

        // BOTH of them, though the node is the source of one edge and the target of the other:
        // an atlas line is walkable either way, and following it only forwards would call half
        // the atlas unreachable.
        Assert.Equal([(5, 6), (7, 8)], node.Connections);
    }

    [Fact]
    public void ANDANEmptySlotIsNotAPlaceToGo()
    {
        // The table has room for lines it is not using, and an unused one reads as (0,0). Kept,
        // it becomes a neighbour every node on the atlas shares - which is a route through the
        // middle of nowhere to anywhere.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status: 0x01, gridX: 3, gridY: 4);

        PlaceEdges(fake, schema, [((3, 4), (0, 0)), ((3, 4), (3, 4))]);

        AtlasNode node = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
        Assert.Empty(node.Connections);
    }

    [Fact]
    public void ABADGEVectorIsBytesAndTheIdIsTheRowPlusAHundred()
    {
        // Read as a vector of pointers - which it is not - its length is divided by eight, so a
        // node with fewer than eight badges reports none and one with more reads the front of
        // the buffer as an address. Either way the atlas says a map contains nothing.
        OffsetSchema schema = LoadSchema();
        StructDef node = schema.Structs["AtlasNode"];
        (FakeMemoryReader fake, _) = Atlas(MapNodeFlags(schema), status: 0x01);

        const ulong rows = 0x80_0000;
        fake.Place(rows, [0x01, 0x08]);           // Breach is row 1, Abyss row 8
        fake.Place(Node + (ulong)node.OffsetOf("BadgeVectorBegin"), rows);
        fake.Place(Node + (ulong)node.OffsetOf("BadgeVectorEnd"), rows + 2);

        AtlasNode read = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0)));
        Assert.Equal([0x0065u, 0x006Cu], read.BadgeIds);
    }
}
