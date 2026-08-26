using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// The fingerprint that decides whether a tick has to look at the game at all.
/// </summary>
/// <remarks>
/// A purse count used to pay one identity read per item across every inventory, five seconds
/// apart, to rediscover each time that a helmet is still not money. It now asks that once per
/// ARRANGEMENT, and this fingerprint is what "arrangement" means. Everything it fails to notice
/// is a stale purse; everything it notices needlessly is the old cost back again.
/// </remarks>
public sealed class PurseMemoTests
{
    private static StashInventory Holding(params StashedItem[] items)
        => new(7, InventoryKind.Stash, 0xDEAD, 12, 12, items);

    private static StashedItem At(ulong entity, int left, int top)
        => new(entity, left, top, 1, 1);

    [Fact]
    public void NothingMovedIsTheSameArrangement()
    {
        StashInventory before = Holding(At(0x100, 0, 0), At(0x200, 1, 0));
        StashInventory after = Holding(At(0x100, 0, 0), At(0x200, 1, 0));

        Assert.Equal(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void AStackGrowingIsNOTAChangeOfArrangement()
    {
        // THE CASE THE WHOLE DESIGN TURNS ON. Dropping ten Chaos onto a stack of five changes
        // neither the entity nor the slot, so this fingerprint cannot see it - and must not be
        // asked to. It answers "which slots are money", and that really has not changed. What
        // the slots HOLD is read in full on every tick precisely because of this test.
        StashInventory before = Holding(At(0x100, 3, 2));
        StashInventory after = Holding(At(0x100, 3, 2));

        Assert.Equal(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void AnItemPickedUpChangesIt()
    {
        StashInventory before = Holding(At(0x100, 0, 0));
        StashInventory after = Holding(At(0x100, 0, 0), At(0x200, 1, 0));

        Assert.NotEqual(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void AnItemMovedWithinTheTabChangesIt()
    {
        StashInventory before = Holding(At(0x100, 0, 0));
        StashInventory after = Holding(At(0x100, 4, 6));

        Assert.NotEqual(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void AnItemSwappedForAnotherAtTheSameSlotChangesIt()
    {
        StashInventory before = Holding(At(0x100, 2, 2));
        StashInventory after = Holding(At(0x300, 2, 2));

        Assert.NotEqual(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void ADifferentlyShapedItemAtTheSameSlotChangesIt()
    {
        // Size is in the fingerprint because it is free, and because a two-by-four that became a
        // one-by-one is the clearest possible sign that the slot is not holding what it held.
        StashInventory before = Holding(new StashedItem(0x100, 2, 2, 2, 4));
        StashInventory after = Holding(new StashedItem(0x100, 2, 2, 1, 1));

        Assert.NotEqual(StashInspector.Print(before), StashInspector.Print(after));
    }

    [Fact]
    public void EmptyingATabIsNotTheSameAsATabThatWasNeverFilled()
    {
        // Both are the empty product, which is why the count is folded in separately: without it
        // an emptied currency tab would keep answering with what it used to hold.
        StashInventory emptied = Holding();
        StashInventory held = Holding(At(0x100, 0, 0));

        Assert.NotEqual(StashInspector.Print(held), StashInspector.Print(emptied));
    }

    [Fact]
    public void TwoItemsSwappingPlacesChangesIt()
    {
        // An order-insensitive fingerprint - a sum, an xor - would call this unchanged. It is not:
        // the items really did move, and the cheapest correct answer is to re-read.
        StashInventory before = Holding(At(0x100, 0, 0), At(0x200, 1, 0));
        StashInventory after = Holding(At(0x100, 1, 0), At(0x200, 0, 0));

        Assert.NotEqual(StashInspector.Print(before), StashInspector.Print(after));
    }
}
