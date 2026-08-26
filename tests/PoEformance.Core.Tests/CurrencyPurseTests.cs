using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// What counts as currency, and what a purse of it comes to.
/// </summary>
/// <remarks>
/// The predicate is tested against paths this project has actually seen - the ones written down
/// in its own name tables and preload tests - rather than against paths invented to match it.
/// The valuation is tested against a REAL poe.ninja answer, for the same reason the price book's
/// own tests are: a total computed from prices written to match the totaliser proves only that
/// it matches itself.
/// </remarks>
public class CurrencyPurseTests
{
    private static string Captured(string name)
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "fixtures", name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                at = at.Parent;
            }
        }

        throw new FileNotFoundException($"captured answer {name} not found");
    }

    private static PriceBook Real()
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, Captured("ninja-exchange.json")) > 0);
        return book;
    }

    private static InspectedItem Item(string path, string art, int stack = 1)
        => new(1, path, "base", string.Empty, string.Empty, -1, null, stack, 0, art, [], []);

    private static StashPage Page(InventoryKind kind, params InspectedItem[] items)
        => new(
            1,
            kind,
            "page",
            12,
            12,
            [.. items.Select((item, i) => new StashSlot(new StashedItem((ulong)i + 1, i, 0, 1, 1), item))]);

    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyAddModToRare")]
    [InlineData("Metadata/Items/Currency/CurrencyWeaponQuality")]
    [InlineData("Metadata/Items/Currency/Ritual/RitualPinnacleKey")]
    public void CurrencyIsFoundByItsPath(string path)
        => Assert.True(CurrencyPaths.IsCurrency(path));

    [Theory]
    [InlineData("Metadata/Items/Armours/BodyArmours/BodyStr1")]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/Daggers/Dagger1")]
    [InlineData("Metadata/Items/Rings/Ring1")]
    [InlineData("")]
    [InlineData(null)]
    public void AndNothingElseIs(string? path)
        => Assert.False(CurrencyPaths.IsCurrency(path));

    [Fact]
    public void TheSegmentIsMatchedRatherThanThePrefix()
    {
        // Ritual keys sit a level deeper - Metadata/Items/Currency/Ritual/... - and a prefix
        // test would still have caught those. What it would NOT catch is the family GGG nests
        // somewhere else next, which is the whole reason this looks for the segment.
        Assert.True(CurrencyPaths.IsCurrency("Metadata/Items/Some/Other/Currency/Thing"));

        // And a path that merely CONTAINS the word is not the same as one containing the
        // segment: the slashes are doing work here.
        Assert.False(CurrencyPaths.IsCurrency("Metadata/Items/CurrencyLikeThing/Nope"));
    }

    [Fact]
    public void RarityIsNotAskedBecauseCurrencyHasNone()
    {
        // The correction this whole rule exists for, pinned so it cannot be undone: currency
        // carries no rarity component, so a reader that trusts rarity files every Exalted Orb
        // under Normal. These items are built with rarity -1 - what an item with no mods block
        // actually reads as - and they still have to be found.
        InspectedItem exalted = Item(
            "Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 20);

        Assert.Equal(-1, exalted.Rarity);
        Assert.True(CurrencyPaths.IsCurrency(exalted));
    }

    [Fact]
    public void APurseIsWorthItsStacksAtTheBookPrice()
    {
        // Exalted is 1 by construction and Divine is 581 in the captured answer, so twenty
        // Exalted and two Divine is 20 + 1162. Both numbers come from the real file.
        PriceBook book = Real();

        StashPage backpack = Page(
            InventoryKind.Backpack,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 20),
            Item("Metadata/Items/Currency/CurrencyModValues", "Art/2DItems/Currency/CurrencyModValues.dds", 2));

        Valued purse = book.Purse([backpack]);

        Assert.Equal(20 + (2 * 581), purse.Exalted, 0);
        Assert.Equal(2, purse.Priced);
        Assert.Equal(0, purse.Unpriced);
    }

    [Fact]
    public void GearIsNotInIt()
    {
        // The narrowing that makes the graph readable. A body armour sitting in the same tab is
        // not part of what could be spent, and pricing it is where the book is least sure of
        // itself - see the type remarks.
        PriceBook book = Real();

        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 5),
            Item("Metadata/Items/Armours/BodyArmours/BodyStr1", "Art/2DItems/Armours/BodyArmours/BodyStr1.dds"));

        Valued purse = book.Purse([tab]);

        Assert.Equal(5, purse.Exalted, 0);
        Assert.Equal(1, purse.Items);
        Assert.Equal(1, CurrencyPurse.Stacks([tab]));
    }

    [Fact]
    public void WornThingsAreNotCountedEvenIfTheyMatch()
    {
        // Belt and braces. Currency cannot be equipped today, so this is guarding the day the
        // currency rule is widened: whatever it grows to mean, an equipped item is not money.
        PriceBook book = Real();

        StashPage worn = Page(
            InventoryKind.Equipped,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 99));

        Assert.Equal(0, book.Purse([worn]).Exalted);
        Assert.Equal(0, CurrencyPurse.Stacks([worn]));
    }

    [Fact]
    public void WhatTheBookCouldNotPriceIsCountedRatherThanDropped()
    {
        // The number that stops a total being believed as a whole picture. Blacksmith's
        // Whetstone is thin in the captured answer and is deliberately not priced - so a purse
        // holding one is worth what the rest of it is worth, and says one item is missing.
        PriceBook book = Real();

        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 3),
            Item("Metadata/Items/Currency/CurrencyWeaponQuality", "Art/2DItems/Currency/CurrencyWeaponQuality.dds", 40));

        Valued purse = book.Purse([tab]);

        Assert.Equal(3, purse.Exalted, 0);
        Assert.Equal(1, purse.Priced);
        Assert.Equal(1, purse.Unpriced);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void TheStashHalfIsOnlyReplacedWhereAStashCanExist(bool canSee, bool sawTab, bool replaces)
    {
        // THE REGRESSION, off a real recorded purse. The first version replaced the remembered
        // stash contents whenever the game listed any stash inventory, on the assumption that in
        // a map the tabs are absent from the list. They are not - they are listed and empty. The
        // record shows what that cost, in the player's own file:
        //
        //   09:06:11   74 stacks   1,280,968 ex     (in the hideout)
        //   09:11:12    3 stacks          15 ex     (walked into a map)
        //   09:18:38   59 stacks   1,272,975 ex     (came back)
        //   09:25:34    2 stacks          55 ex     (left again)
        //
        // Every one of those transitions was written into a record that never resets, as a gain
        // or loss of about 2,700 Divine that never happened.
        Assert.Equal(replaces, StashInspector.ReplacesTheStash(canSee, sawTab));
    }

    [Fact]
    public void DivineIsTheSameTotalRatherThanASecondOne()
    {
        // Why nothing here knows which picture the Divine Orb draws. The book is in Exalted and
        // carries the rate, so the second reading is a division - which means the two can never
        // disagree about what the purse holds.
        PriceBook book = Real();

        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyModValues", "Art/2DItems/Currency/CurrencyModValues.dds", 3));

        double exalted = book.Purse([tab]).Exalted;

        Assert.Equal(3, exalted / book.Rate, 3);
    }
}
