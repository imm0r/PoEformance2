using PoEformance.Core.Schema;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The entity-map walk over a synthetic red-black tree. The tree the game gives us is
/// LIVE - it mutates while we read it - so these tests care as much about surviving a
/// corrupt tree as about traversing a healthy one.
/// </summary>
public class EntityMapReaderTests
{
    private const ulong MapStruct = 0x10_0000;
    private const ulong Head = 0x20_0000;
    private const ulong NodeBase = 0x30_0000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    private static ulong NodeAt(int index) => NodeBase + (ulong)(index * 0x100);

    /// <summary>Writes one tree node: children, id and entity pointer.</summary>
    private static void PlaceNode(FakeMemoryReader fake, OffsetSchema schema, int index,
        ulong left, ulong right, uint entityId, ulong entityPtr)
    {
        StructDef n = schema.Structs["StdMapNode"];
        ulong node = NodeAt(index);

        // The reader batch-reads the whole 0x30-byte node in ONE call (the same
        // optimisation the AHK reference makes), and a partially-mapped read fails
        // outright - exactly as ReadProcessMemory does. In the real game a node is
        // contiguous memory, so lay down a full block here before overlaying fields.
        fake.Place(node, new byte[0x30]);

        fake.Place(node + (ulong)n.OffsetOf("Left"), left);
        fake.Place(node + (ulong)n.OffsetOf("Right"), right);
        fake.Place<uint>(node + (ulong)n.OffsetOf("KeyId"), entityId);
        fake.Place(node + (ulong)n.OffsetOf("ValueEntityPtr"), entityPtr);
    }

    /// <summary>Sets up the map header pointing at a root node.</summary>
    private static FakeMemoryReader NewMap(OffsetSchema schema, long size, ulong root)
    {
        StructDef m = schema.Structs["StdMap"];
        StructDef n = schema.Structs["StdMapNode"];
        var fake = new FakeMemoryReader();
        fake.Place(MapStruct + (ulong)m.OffsetOf("Head"), Head);
        fake.Place<long>(MapStruct + (ulong)m.OffsetOf("Size"), size);
        fake.Place(Head + (ulong)n.OffsetOf("Parent"), root);
        return fake;
    }

    [Fact]
    public void Walk_FindsEveryEntityInTheTree()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 3, root: NodeAt(0));

        //        node0 (id 100)
        //        /          \
        //   node1 (101)   node2 (102)
        PlaceNode(fake, schema, 0, NodeAt(1), NodeAt(2), 100, 0x50_0000);
        PlaceNode(fake, schema, 1, Head, Head, 101, 0x51_0000);
        PlaceNode(fake, schema, 2, Head, Head, 102, 0x52_0000);

        Dictionary<uint, ulong> found = new EntityMapReader(fake, schema).ReadEntityPointers(MapStruct);

        Assert.Equal(3, found.Count);
        Assert.Equal(0x50_0000UL, found[100]);
        Assert.Equal(0x51_0000UL, found[101]);
        Assert.Equal(0x52_0000UL, found[102]);
    }

    [Fact]
    public void Walk_SkipsVisualAndDecorationIds()
    {
        // The game keeps decorations in the same map with ids >= 0x40000000; they are not
        // gameplay entities and would swamp the result.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 2, root: NodeAt(0));
        PlaceNode(fake, schema, 0, NodeAt(1), Head, 100, 0x50_0000);
        PlaceNode(fake, schema, 1, Head, Head, EntityMapReader.MaxRealEntityId + 5, 0x51_0000);

        Dictionary<uint, ulong> found = new EntityMapReader(fake, schema).ReadEntityPointers(MapStruct);

        Assert.Single(found);
        Assert.True(found.ContainsKey(100));
    }

    [Fact]
    public void Walk_KeepsTheVisualsWhenAskedTo()
    {
        // Because a projectile is one of them. Everything a skill puts on the screen is filed
        // above the threshold, so with the filter on no projectile can reach a snapshot at all,
        // however it is classified afterwards.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 2, root: NodeAt(0));
        PlaceNode(fake, schema, 0, NodeAt(1), Head, 100, 0x50_0000);
        PlaceNode(fake, schema, 1, Head, Head, EntityMapReader.MaxRealEntityId + 5, 0x51_0000);

        Dictionary<uint, ulong> found = new EntityMapReader(fake, schema)
            .ReadEntityPointers(MapStruct, includeVisuals: true);

        Assert.Equal(2, found.Count);
        Assert.Equal(0x51_0000UL, found[EntityMapReader.MaxRealEntityId + 5]);
    }

    [Fact]
    public void Walk_NeverLetsTheVisualsCrowdOutAGameplayEntity()
    {
        // The cap counts gameplay entities only. Counted together, a screen of sparks would
        // spend a cap of two on itself and the monsters behind them would silently stop being
        // read - and the walk is breadth-first, so WHICH ones survive would be a fact about the
        // tree's shape rather than about what matters.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 5, root: NodeAt(0));

        // Three visuals in front of two real entities, in the order the walk reaches them.
        PlaceNode(fake, schema, 0, NodeAt(1), NodeAt(2), EntityMapReader.MaxRealEntityId + 1, 0x50_0000);
        PlaceNode(fake, schema, 1, NodeAt(3), Head, EntityMapReader.MaxRealEntityId + 2, 0x51_0000);
        PlaceNode(fake, schema, 2, NodeAt(4), Head, EntityMapReader.MaxRealEntityId + 3, 0x52_0000);
        PlaceNode(fake, schema, 3, Head, Head, 100, 0x53_0000);
        PlaceNode(fake, schema, 4, Head, Head, 101, 0x54_0000);

        Dictionary<uint, ulong> found = new EntityMapReader(fake, schema)
            .ReadEntityPointers(MapStruct, maxEntities: 2, includeVisuals: true);

        Assert.True(found.ContainsKey(100));
        Assert.True(found.ContainsKey(101));
    }

    [Fact]
    public void Walk_SurvivesACyclicTree_WithoutHanging()
    {
        // The decisive robustness test: the tree is live and can be read mid-mutation, so a
        // node may point back at an ancestor. A naive recursive walk would spin forever;
        // the visited set must make this terminate with whatever was reachable.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 2, root: NodeAt(0));
        PlaceNode(fake, schema, 0, NodeAt(1), Head, 100, 0x50_0000);
        PlaceNode(fake, schema, 1, NodeAt(0), NodeAt(1), 101, 0x51_0000); // points back + at itself

        Dictionary<uint, ulong> found = new EntityMapReader(fake, schema).ReadEntityPointers(MapStruct);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Walk_RejectsAnImplausibleHeader()
    {
        OffsetSchema schema = LoadSchema();
        StructDef m = schema.Structs["StdMap"];

        // Absurd size: a corrupt or mid-resize header must yield nothing, not a huge walk.
        FakeMemoryReader huge = NewMap(schema, size: 5_000_000, root: NodeAt(0));
        Assert.Empty(new EntityMapReader(huge, schema).ReadEntityPointers(MapStruct));

        // Empty map.
        FakeMemoryReader empty = NewMap(schema, size: 0, root: NodeAt(0));
        Assert.Empty(new EntityMapReader(empty, schema).ReadEntityPointers(MapStruct));

        // Root == head means an empty tree.
        FakeMemoryReader noRoot = NewMap(schema, size: 3, root: Head);
        Assert.Empty(new EntityMapReader(noRoot, schema).ReadEntityPointers(MapStruct));

        // Nothing mapped at all.
        Assert.Empty(new EntityMapReader(new FakeMemoryReader(), schema).ReadEntityPointers(MapStruct));
    }

    [Fact]
    public void Walk_HonoursTheEntityCap()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = NewMap(schema, size: 10, root: NodeAt(0));

        // A right-leaning chain of 10 nodes.
        for (int i = 0; i < 10; i++)
        {
            PlaceNode(fake, schema, i, Head, i < 9 ? NodeAt(i + 1) : Head, (uint)(200 + i), 0x50_0000 + (ulong)(i * 0x1000));
        }

        Dictionary<uint, ulong> capped = new EntityMapReader(fake, schema).ReadEntityPointers(MapStruct, maxEntities: 4);
        Assert.True(capped.Count <= 4);
    }

    [Theory]
    [InlineData("Metadata/Characters/Int/IntFour", EntityKind.Player)]
    [InlineData("Metadata/Monsters/Skeletons/SkeletonMelee", EntityKind.Monster)]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist", EntityKind.Chest)]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem", EntityKind.WorldItem)]
    [InlineData("Metadata/Effects/Spells/Fireball", EntityKind.Effect)]
    [InlineData("Metadata/Terrain/Doodads/Rock", EntityKind.Terrain)]
    [InlineData("Metadata/Something/Else", EntityKind.Unknown)]
    public void ClassifyPath_UsesTheMetadataPrefix(string path, EntityKind expected)
        => Assert.Equal(expected, WorldReader.ClassifyPath(path));

    /// <summary>
    /// The paths a hideout is actually made of, which used to classify as nothing.
    /// </summary>
    /// <remarks>
    /// Every one of these was read out of the running game rather than supposed - a hideout
    /// listed page after page of "Unknown" while the path plainly said what each thing was.
    /// </remarks>
    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Hideout/TransmutationBench", EntityKind.Object)]
    [InlineData("Metadata/MiscellaneousObjects/Hideout/KaruiHealingWell", EntityKind.Object)]
    [InlineData("Metadata/MiscellaneousObjects/Hideout/ReforgingBench", EntityKind.Object)]
    [InlineData("Metadata/MiscellaneousObjects/Sanctum/SanctumLocker_Hideout", EntityKind.Object)]
    [InlineData("Metadata/MiscellaneousObjects/Stash", EntityKind.Object)]
    [InlineData("Metadata/MiscellaneousObjects/Portals/DemonicApparitionPortal", EntityKind.Portal)]
    public void TheFurnitureOfAnAreaIsNotUnknown(string path, EntityKind expected)
        => Assert.Equal(expected, WorldReader.ClassifyPath(path));

    [Fact]
    public void APetIsItsOwnKindRatherThanAMonster()
    {
        // The reference's reading as well as ours: GameHelper2 lists Metadata/Pet among the
        // paths its monster classification refuses. Both of these are out of this project's
        // own delirium recording, where they classified as nothing at all.
        Assert.Equal(EntityKind.Pet, WorldReader.ClassifyPath("Metadata/Pet/BetaKiwis/FaridunKiwi"));
        Assert.Equal(EntityKind.Pet, WorldReader.ClassifyPath("Metadata/Pet/BetaKiwis/VaalKiwi"));
    }

    /// <summary>
    /// Two things that are both "Object", told apart by the folder the game files them under.
    /// </summary>
    /// <remarks>
    /// Read off the running game: a Reforging Bench is hideout furniture and a Relic Locker is
    /// Sanctum's, and the list said "Object" to both.
    /// </remarks>
    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Hideout/ReforgingBench", "Hideout Object")]
    [InlineData("Metadata/MiscellaneousObjects/Sanctum/SanctumLocker_Hideout", "Sanctum Object")]
    [InlineData("Metadata/MiscellaneousObjects/Hideout/KaruiHealingWell", "Hideout Object")]
    public void AnObjectIsNamedByItsFamily(string path, string expected)
        => Assert.Equal(expected, WorldReader.DescribeKind(WorldReader.ClassifyPath(path), path));

    [Theory]
    // No sub-folder, so no family - and "Stash Object" would say one thing twice anyway.
    [InlineData("Metadata/MiscellaneousObjects/Stash", "Object")]

    // The game files portals under a Portals folder, and "Portals Portal" is noise where
    // "Portal" is an answer.
    [InlineData("Metadata/MiscellaneousObjects/Portals/DemonicApparitionPortal", "Portal")]

    // Everything outside that bucket keeps the plain kind: the families of monsters and
    // chests exist, but nobody asked to read the list that way.
    [InlineData("Metadata/Monsters/LeagueDelirium/DeliriumMinion1", "Monster")]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist", "Chest")]
    [InlineData("Metadata/Something/Else", "Unknown")]
    public void AndKeepsThePlainKindWhereAFamilyWouldAddNothing(string path, string expected)
        => Assert.Equal(expected, WorldReader.DescribeKind(WorldReader.ClassifyPath(path), path));

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem", EntityKind.WorldItem)]
    [InlineData("Metadata/MiscellaneousObjects/Delirium/DeliriumShardSeethingChymeDialogueController", EntityKind.Object)]
    [InlineData("Metadata/Monsters/MarakethSanctumTrial/Hazards/ProjectilePortal", EntityKind.Monster)]
    [InlineData("Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition", EntityKind.Terrain)]
    public void TheNewRulesOnlyEverTurnUnknownIntoSomething(string path, EntityKind expected)
    {
        // The whole safety of the change. These rules run LAST, so everything already
        // classified keeps its kind: a drop under MiscellaneousObjects is still a drop, a
        // monster whose name happens to contain "Portal" is still a monster - the reference
        // lists that exact one - and an exit built into terrain is still terrain, which is
        // what the noise filter, the health bars and the drawn-kind filter all read.
        Assert.Equal(expected, WorldReader.ClassifyPath(path));
    }
}
