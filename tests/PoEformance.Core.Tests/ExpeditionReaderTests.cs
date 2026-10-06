using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The expedition reads, each against fake memory laid out as the reference measured the
/// real thing: the map-modifier vector, the controller behind the ServerData, the counter
/// widget, the detonator's state, a blocker's flag, a relic's mods.
/// </summary>
public class ExpeditionReaderTests
{
    private const ulong Area = 0x0000_0500_0000_0000;
    private const ulong ModsVector = Area + 0x1000;
    private const ulong ServerData = 0x0000_0500_0010_0000;
    private const ulong Controller = 0x0000_0500_0020_0000;
    private const ulong Placement = 0x0000_0500_0021_0000;
    private const ulong PlacedVector = 0x0000_0500_0022_0000;
    private const ulong Detonator = 0x0000_0500_0030_0000;
    private const ulong Blocker = 0x0000_0500_0040_0000;
    private const ulong Relic = 0x0000_0500_0050_0000;
    private const ulong Vtable = 0x1400_0000_1000;

    private static OffsetSchema Schema() => MonolithFixture.ShippedSchema();

    private static ExpeditionReader Make(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    private static void PlaceMods(FakeMemoryReader fake, OffsetSchema schema, params (int Key, int Value)[] pairs)
    {
        int at = schema.Structs["AreaInstance"].OffsetOf("MapMods");
        fake.Place(Area, new byte[0x200]);
        fake.Place<ulong>(Area + (ulong)at, pairs.Length > 0 ? ModsVector : 0UL);
        fake.Place<ulong>(Area + (ulong)at + 8, pairs.Length > 0 ? ModsVector + (ulong)(pairs.Length * 8) : 0UL);
        for (int i = 0; i < pairs.Length; i++)
        {
            fake.Place<int>(ModsVector + (ulong)(i * 8), pairs[i].Key);
            fake.Place<int>(ModsVector + (ulong)(i * 8) + 4, pairs[i].Value);
        }
    }

    [Fact]
    public void TheMapModifiersAreSummedByStatKey_AndAnEmptyVectorIsNoModifier()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceMods(fake, schema, (13686, 30), (5, 1), (13471, 35), (13686, 5));

        ExpeditionReader reader = Make(fake, schema);
        Assert.Equal((35, 35), reader.MapMods(Area));

        PlaceMods(fake, schema);
        Assert.Equal((0, 0), Make(fake, schema).MapMods(Area));
    }

    [Fact]
    public void AnUnreadableOrMisshapenVectorReadsAsNothing_NotAsZero()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionReader reader = Make(fake, schema);

        // Unmapped memory: null, so the caller keeps what it had.
        Assert.Null(reader.MapMods(Area));
        Assert.Null(reader.MapMods(0));

        // A span that is not whole pairs: not a vector of them.
        int at = schema.Structs["AreaInstance"].OffsetOf("MapMods");
        fake.Place(Area, new byte[0x200]);
        fake.Place<ulong>(Area + (ulong)at, ModsVector);
        fake.Place<ulong>(Area + (ulong)at + 8, ModsVector + 12);
        fake.Place(ModsVector, new byte[16]);
        Assert.Null(Make(fake, schema).MapMods(Area));
    }

    /// <summary>A controller with its placement state, the way the plugin's fingerprint expects them.</summary>
    internal static void PlaceController(
        FakeMemoryReader fake, OffsetSchema schema, ulong at, ulong manager, int total, int placed, int typeId = 0x33,
        ulong placementAt = Placement, ulong placedVector = PlacedVector)
    {
        StructDef controller = schema.Structs["ExpeditionController"];
        StructDef placement = schema.Structs["ExpeditionPlacementState"];
        fake.Place(at, new byte[0x280]);
        fake.Place<ulong>(at, Vtable);
        fake.Place<int>(at + (ulong)controller.OffsetOf("TypeId"), typeId);
        fake.Place<ulong>(at + (ulong)controller.OffsetOf("ManagerPtr"), manager);
        fake.Place<ulong>(at + (ulong)controller.OffsetOf("PlacementStatePtr"), placementAt);

        fake.Place(placementAt, new byte[0x70]);
        fake.Place<ulong>(placementAt, Vtable + 0x100);
        fake.Place<byte>(placementAt + (ulong)placement.OffsetOf("TotalCharges"), (byte)total);
        fake.Place<ulong>(placementAt + (ulong)placement.OffsetOf("PlacedVec"), placed > 0 ? placedVector : 0UL);
        fake.Place<ulong>(placementAt + (ulong)placement.OffsetOf("PlacedVec") + 8, placed > 0 ? placedVector + (ulong)(placed * 8) : 0UL);
        fake.Place(placedVector, new byte[(placed * 8) + 8]);
    }

    [Fact]
    public void TheControllerIsFoundInTheServerDataSlot_AndItsCountsRead()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        int slot = schema.Structs["ServerData"].OffsetOf("ExpeditionControllerPtr");
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)slot, Controller);
        PlaceController(fake, schema, Controller, ServerData, total: 15, placed: 2);

        ExpeditionCounts? counts = Make(fake, schema).Counts(ServerData, 0);
        Assert.NotNull(counts);
        Assert.Equal(15, counts!.Value.Total);
        Assert.Equal(2, counts.Value.Placed);
        Assert.Equal("ServerData slot", counts.Value.Source);
    }

    [Fact]
    public void TheOldSlotAndTheScanStillFindIt_AndTheFingerprintRefusesAnImpostor()
    {
        OffsetSchema schema = Schema();
        StructDef server = schema.Structs["ServerData"];

        // The old slot.
        var fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)server.Constants["ExpeditionControllerPtrWas"], Controller);
        PlaceController(fake, schema, Controller, ServerData, total: 20, placed: 0);
        ExpeditionCounts? old = Make(fake, schema).Counts(ServerData, 0);
        Assert.Equal((20, 0, "ServerData old slot"), (old!.Value.Total, old.Value.Placed, old.Value.Source));

        // Somewhere in the window: the scan.
        fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + 0x2500, Controller);
        PlaceController(fake, schema, Controller, ServerData, total: 5, placed: 5);
        ExpeditionCounts? scanned = Make(fake, schema).Counts(ServerData, 0);
        Assert.Equal((5, 5, "ServerData scan +0x2500"), (scanned!.Value.Total, scanned.Value.Placed, scanned.Value.Source));

        // The right slot, the wrong type id: not believed. A back-pointer elsewhere: not believed.
        fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)server.OffsetOf("ExpeditionControllerPtr"), Controller);
        PlaceController(fake, schema, Controller, ServerData, total: 15, placed: 2, typeId: 0x34);
        Assert.Null(Make(fake, schema).Counts(ServerData, 0));

        fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)server.OffsetOf("ExpeditionControllerPtr"), Controller);
        PlaceController(fake, schema, Controller, ServerData + 0x10, total: 15, placed: 2);
        Assert.Null(Make(fake, schema).Counts(ServerData, 0));

        // A total outside 1..64 is unknown, not a budget.
        fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)server.OffsetOf("ExpeditionControllerPtr"), Controller);
        PlaceController(fake, schema, Controller, ServerData, total: 0, placed: 1);
        ExpeditionCounts? unknown = Make(fake, schema).Counts(ServerData, 0);
        Assert.Equal(0, unknown!.Value.Total);
        Assert.Equal(1, unknown.Value.Placed);
    }

    /// <summary>The counter widget at [97][9][17][1] under the root, its text leaf three [0] steps down.</summary>
    private static (UiTree Tree, int Widget) HudTree(OffsetSchema schema, string text)
    {
        var tree = new UiTree(schema);
        var next = 1;
        int[] rootChildren = new int[98];
        for (int i = 0; i < 98; i++)
        {
            rootChildren[i] = next++;
        }

        int level1 = rootChildren[97];
        int[] level1Children = new int[10];
        for (int i = 0; i < 10; i++)
        {
            level1Children[i] = next++;
        }

        int level2 = level1Children[9];
        int[] level2Children = new int[18];
        for (int i = 0; i < 18; i++)
        {
            level2Children[i] = next++;
        }

        int level3 = level2Children[17];
        int[] level3Children = [next++, next++];
        int widget = level3Children[1];
        int a = next++;
        int b = next++;
        int leaf = next++;

        tree.Add(0, children: rootChildren);
        foreach (int i in rootChildren)
        {
            if (i == level1)
            {
                tree.Add(i, parent: 0, children: level1Children);
            }
            else
            {
                tree.Add(i, parent: 0);
            }
        }

        foreach (int i in level1Children)
        {
            if (i == level2)
            {
                tree.Add(i, parent: level1, children: level2Children);
            }
            else
            {
                tree.Add(i, parent: level1);
            }
        }

        foreach (int i in level2Children)
        {
            if (i == level3)
            {
                tree.Add(i, parent: level2, children: level3Children);
            }
            else
            {
                tree.Add(i, parent: level2);
            }
        }

        tree.Add(level3Children[0], parent: level3);
        tree.Add(widget, parent: level3, children: [a]);
        tree.Add(a, parent: widget, children: [b]);
        tree.Add(b, parent: a, children: [leaf]);
        tree.Add(leaf, parent: b, text: text);
        return (tree, widget);
    }

    [Fact]
    public void TheCounterWidgetSaysHowManyRemain_AndCarriesTheControllerAsTheLastResort()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, int widget) = HudTree(schema, "13");
        ExpeditionReader reader = Make(tree.Reader, schema);

        Assert.Equal(13, reader.HudRemaining(UiTree.At(0)));
        Assert.Null(reader.HudRemaining(0));

        // The controller hung off the widget, with no ServerData to test the back-pointer against.
        tree.Reader.Place<ulong>(UiTree.At(widget) + (ulong)schema.Structs["ExpeditionHud"].OffsetOf("ControllerPtr"), Controller);
        PlaceController(tree.Reader, schema, Controller, 0, total: 18, placed: 3);
        ExpeditionCounts? counts = reader.Counts(0, UiTree.At(0));
        Assert.Equal((18, 3, "counter widget"), (counts!.Value.Total, counts.Value.Placed, counts.Value.Source));

        // Text that is not a number, or too big to be a count.
        (UiTree words, _) = HudTree(schema, "none");
        Assert.Null(Make(words.Reader, schema).HudRemaining(UiTree.At(0)));
        (UiTree big, _) = HudTree(schema, "1000");
        Assert.Null(Make(big.Reader, schema).HudRemaining(UiTree.At(0)));
    }

    [Fact]
    public void TheDetonatorIsActivatedByItsNamedState_AndOnlyThatOne()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ulong machine = Detonator + 0x10_000;
        MonolithFixture.PlaceEntity(fake, schema, Detonator, 77, ExpeditionReader.DetonatorPath, [("StateMachine", machine)]);
        MonolithFixture.PlaceStateMachine(
            fake, schema, machine, Detonator + 0x11_000, Detonator + 0x12_000, Detonator + 0x13_000,
            ["activated", "light_colour"], [0, 1]);
        ExpeditionReader reader = Make(fake, schema);

        // A charge placed lights the colour and does not start the dig.
        Assert.False(reader.DetonatorActivated(Detonator));

        MonolithFixture.PlaceStateMachine(
            fake, schema, machine, Detonator + 0x11_000, Detonator + 0x12_000, Detonator + 0x13_000,
            ["activated", "light_colour"], [1, 2]);
        Assert.True(reader.DetonatorActivated(Detonator));

        // No such entity: not activated rather than a throw.
        Assert.False(reader.DetonatorActivated(Detonator + 0x8_0000));
    }

    [Fact]
    public void ABlockerSaysWhetherItIsShut_AndNoComponentSaysNothing()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ulong blockage = Blocker + 0x10_000;
        MonolithFixture.PlaceEntity(fake, schema, Blocker, 78, "Metadata/Terrain/Gallows/Logbook_Gully/Objects/DevourerSegment", [("TriggerableBlockage", blockage)]);
        fake.Place(blockage, new byte[0x40]);
        fake.Place<byte>(blockage + (ulong)schema.Structs["TriggerableBlockage"].OffsetOf("IsBlocked"), 1);
        ExpeditionReader reader = Make(fake, schema);

        Assert.True(reader.IsBlocked(Blocker));

        fake.Place<byte>(blockage + (ulong)schema.Structs["TriggerableBlockage"].OffsetOf("IsBlocked"), 0);
        Assert.False(reader.IsBlocked(Blocker));

        MonolithFixture.PlaceEntity(fake, schema, Relic, 79, "Metadata/Terrain/Anything", [("Render", Relic + 0x10_000)]);
        Assert.Null(reader.IsBlocked(Relic));
    }

    [Fact]
    public void ARelicsModIdsComeOffItsMagicProperties()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ulong magic = Relic + 0x10_000;
        ulong entries = Relic + 0x11_000;
        ulong rows = Relic + 0x12_000;
        ulong strings = Relic + 0x13_000;
        MonolithFixture.PlaceEntity(fake, schema, Relic, 80, ExpeditionReader.RelicPath, [("ObjectMagicProperties", magic)]);

        int allMods = schema.Structs["ObjectMagicProperties"].OffsetOf("AllMods");
        int vectorSize = (int)schema.Structs["StdVector"].Constants["StructSize"];
        StructDef mod = schema.Structs["ModArray"];
        int entrySize = (int)mod.Constants["EntrySize"];
        fake.Place(magic, new byte[0x220]);

        // Two mods in the first list, one in the third, the others empty.
        string[] ids = ["ExpeditionRelicUpsideItemQuantityChest", "ExpeditionRelicDownsideAlwaysCrit", "ExpeditionRelicUpsidePackSize"];
        fake.Place(entries, new byte[entrySize * 3]);
        for (int i = 0; i < ids.Length; i++)
        {
            ulong row = rows + (ulong)(i * 0x100);
            ulong text = strings + (ulong)(i * 0x100);
            fake.Place<ulong>(entries + (ulong)(i * entrySize) + (ulong)mod.OffsetOf("ModsPtr"), row);
            fake.Place<ulong>(row, text);
            fake.Place(text, new byte[0x100]);
            fake.PlaceUtf16(text, ids[i]);
        }

        fake.Place<ulong>(magic + (ulong)allMods, entries);
        fake.Place<ulong>(magic + (ulong)allMods + 8, entries + (ulong)(entrySize * 2));
        fake.Place<ulong>(magic + (ulong)allMods + (ulong)(2 * vectorSize), entries + (ulong)(entrySize * 2));
        fake.Place<ulong>(magic + (ulong)allMods + (ulong)(2 * vectorSize) + 8, entries + (ulong)(entrySize * 3));

        Assert.Equal(ids, Make(fake, schema).ModIds(Relic));
        Assert.Empty(Make(fake, schema).ModIds(Relic + 0x8_0000));
    }
}
