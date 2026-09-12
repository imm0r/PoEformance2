using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the belt's slots are on the HUD, found by the item each one holds.
/// </summary>
/// <remarks>
/// The tree below is shaped after the real one, which the tool's own interface browser printed
/// in game (2026-09, 0.5.5): HUD, then HUDLeft, then an element named flask_bar whose children
/// are two unnamed wrappers each holding a 58x115 slot and a build_planner_indicator, three
/// 58x58 charm slots with children of their own, and a few decorations. The slots have no
/// names; what they have is the item at ItemPtr.
/// </remarks>
public class FlaskBarReaderTests
{
    private const ulong Life = 0x598B3B7E280;
    private const ulong Mana = 0x598BC175A00;

    /// <summary>A 16:10 window, where both scale factors are 1 - so positions read directly.</summary>
    private static UiScale Window() => new(2560, 1600, 0);

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    private static (UiTree Tree, ulong Hud) Belt(OffsetSchema schema, bool barVisible = true, ulong mana = Mana)
    {
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: InterfaceReader.Id, children: [20, 2]);
        tree.Add(20, parent: 1, stringId: "life_orb", relative: new Vector2(60, 1240), size: new Vector2(300, 300));
        tree.Add(2, parent: 1, stringId: FlaskBarReader.ClusterId, children: [21, 3]);
        tree.Add(21, parent: 2, stringId: "menu_button", size: new Vector2(40, 40));
        tree.Add(
            3, parent: 2, stringId: FlaskBarReader.BarId, visible: barVisible,
            relative: new Vector2(354, 1444), children: [4, 7, 10, 13, 14]);

        // The two flasks: a wrapper each, the slot its first child.
        tree.Add(4, parent: 3, children: [5, 6]);
        tree.Add(5, parent: 4, size: new Vector2(58, 115), itemPtr: Life);
        tree.Add(6, parent: 4, stringId: "build_planner_indicator");
        tree.Add(7, parent: 3, relative: new Vector2(64, 0), children: [8, 9]);
        tree.Add(8, parent: 7, size: new Vector2(58, 115), itemPtr: mana);
        tree.Add(9, parent: 7, stringId: "build_planner_indicator");

        // A charm slot, which holds no item at either level, and two decorations.
        tree.Add(10, parent: 3, relative: new Vector2(156, 69), size: new Vector2(58, 58), children: [11, 12]);
        tree.Add(11, parent: 10, stringId: "build_planner_indicator");
        tree.Add(12, parent: 10, size: new Vector2(20, 20));
        tree.Add(13, parent: 3, size: new Vector2(10, 10));
        tree.Add(14, parent: 3, visible: false, size: new Vector2(10, 10));

        return (tree, UiTree.At(1));
    }

    private static FlaskBarReader Reader(UiTree tree, OffsetSchema schema)
        => new(tree.Reader, schema, new UiElementReader(tree.Reader, schema));

    [Fact]
    public void TheSlotsAreFoundByTheItemTheyHold_AndPlaced()
    {
        // Nothing under the bar is named, so a slot is whatever points at an item. Two flasks,
        // two slots, each where the browser measured it - and the charm slot and the decorations
        // are not among them.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud) = Belt(schema);

        IReadOnlyList<FlaskSlotOnScreen> slots = Reader(tree, schema).Read(hud, Window(), 0);

        Assert.Equal(2, slots.Count);
        Assert.Equal(Life, slots[0].Item);
        Assert.Equal(new ScreenRect(354f, 1444f, 412f, 1559f), slots[0].Where);
        Assert.Equal(Mana, slots[1].Item);
        Assert.Equal(new ScreenRect(418f, 1444f, 476f, 1559f), slots[1].Where);
    }

    [Fact]
    public void AnEmptiedSlotIsStillASlot_AndSaysItIsEmpty()
    {
        // The slot is the same element whatever goes in and out of it, so it stays remembered
        // once found; what is read afresh is the item. Unequipping a flask empties the slot's
        // report rather than losing the slot.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud) = Belt(schema);
        FlaskBarReader reader = Reader(tree, schema);
        Assert.Equal(2, reader.Read(hud, Window(), 0).Count);

        int itemPtr = schema.Structs["UiElementBase"].OffsetOf("ItemPtr");
        tree.Reader.Place<ulong>(UiTree.At(5) + (ulong)itemPtr, 0UL);

        IReadOnlyList<FlaskSlotOnScreen> slots = reader.Read(hud, Window(), 100);

        Assert.Equal(2, slots.Count);
        Assert.Equal(0UL, slots[0].Item);
        Assert.Equal(Mana, slots[1].Item);
    }

    [Fact]
    public void AFlaskEquippedIntoAnEmptySlotIsPickedUpOnTheNextSearch()
    {
        // A slot that was empty when the bar was searched points at nothing and so was never
        // found - which is why the search is repeated on a clock rather than trusted forever.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud) = Belt(schema, mana: 0);
        FlaskBarReader reader = Reader(tree, schema);
        Assert.Single(reader.Read(hud, Window(), 0));

        int itemPtr = schema.Structs["UiElementBase"].OffsetOf("ItemPtr");
        tree.Reader.Place<ulong>(UiTree.At(8) + (ulong)itemPtr, Mana);

        // Within the pause the remembered slots stand; past it the bar is searched again.
        Assert.Single(reader.Read(hud, Window(), FlaskBarReader.SearchAgainMs - 1));
        Assert.Equal(2, reader.Read(hud, Window(), FlaskBarReader.SearchAgainMs).Count);
    }

    [Fact]
    public void ASlotThatStopsBeingAnElementIsDropped_AndSearchedForOnTheClockNotOnTheSpot()
    {
        // The bar has children that come and go - the browser caught one under a charm slot at
        // a different address on each look - and were the loss of any remembered element to
        // send the whole search round, such a child carrying an item would have it run every
        // tick. So a dead slot is dropped, and the clock brings it back.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud) = Belt(schema);
        FlaskBarReader reader = Reader(tree, schema);
        Assert.Equal(2, reader.Read(hud, Window(), 0).Count);

        // The first slot's memory stops saying it is an element.
        ulong self = UiTree.At(5) + (ulong)schema.Structs["UiElementBase"].OffsetOf("Self");
        tree.Reader.Place<ulong>(self, 0UL);

        long before = tree.Reader.Reads;
        IReadOnlyList<FlaskSlotOnScreen> slots = reader.Read(hud, Window(), 100);
        long dyingTick = tree.Reader.Reads - before;

        before = tree.Reader.Reads;
        reader.Read(hud, Window(), 200);
        long settledTick = tree.Reader.Reads - before;

        Assert.Equal(Mana, Assert.Single(slots).Item);

        // One read more than the settled tick after it - the read that found the slot dead -
        // and not a walk of the bar, which would be dozens.
        Assert.Equal(settledTick + 1, dyingTick);

        // An element again, it is found when the clock says so and not before.
        tree.Reader.Place<ulong>(self, UiTree.At(5));
        Assert.Single(reader.Read(hud, Window(), FlaskBarReader.SearchAgainMs - 1));
        Assert.Equal(2, reader.Read(hud, Window(), FlaskBarReader.SearchAgainMs).Count);
    }

    [Fact]
    public void NothingWhileTheBarIsHidden_OrThereIsNoInterface()
    {
        // A loading screen, a hidden interface, a state without a HUD: no slot, and no search
        // for one either.
        OffsetSchema schema = Schema();
        (UiTree hidden, ulong hud) = Belt(schema, barVisible: false);

        Assert.Empty(Reader(hidden, schema).Read(hud, Window(), 0));
        Assert.Empty(Reader(hidden, schema).Read(0, Window(), 0));
    }

    [Fact]
    public void TheBarIsLookedForAgainWhenItStopsAnsweringToItsName()
    {
        // The cached bar is trusted only while it still says flask_bar under the same HUD - the
        // same rule the HUD itself is held to - so a rebuilt interface costs a search, not a
        // number drawn on whatever now sits at the old address.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud) = Belt(schema);
        FlaskBarReader reader = Reader(tree, schema);
        Assert.Equal(2, reader.Read(hud, Window(), 0).Count);

        int stringId = schema.Structs["UiElementBase"].OffsetOf("StringIdPtr");
        tree.Reader.PlaceStdWString(UiTree.At(3) + (ulong)stringId, "something_else", 0x0000_0300_3000_0000);

        Assert.Empty(reader.Read(hud, Window(), 0));
        Assert.Equal(0UL, reader.Element);
    }
}
