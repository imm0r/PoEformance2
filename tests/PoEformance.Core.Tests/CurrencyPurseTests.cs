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

    // The base NAME is derived from the path rather than a constant, because the breakdown
    // groups by art and labels by name - a fixture where every item is called the same thing
    // hides a grouping bug instead of catching one.
    private static InspectedItem Item(string path, string art, int stack = 1)
        => new(1, path, path[(path.LastIndexOf('/') + 1)..], string.Empty, string.Empty,
            -1, null, stack, 0, art, [], []);

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
    public void TheBreakdownSaysWhichStackProducedTheTotal()
    {
        // A single total is unfalsifiable: it is believed or distrusted, and neither can be
        // acted on. These lines are what turn "that looks far too high" into a specific claim
        // about a specific stack - which is the only form of it anybody can check.
        PriceBook book = Real();

        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyModValues", "Art/2DItems/Currency/CurrencyModValues.dds", 3),
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 20));

        IReadOnlyList<CurrencyPurse.PurseLine> lines = book.Breakdown([tab]);

        Assert.Equal(2, lines.Count);

        // Biggest contributor first: 3 Divine at 581 beats 20 Exalted at 1.
        Assert.Equal(3, lines[0].Stack);
        Assert.Equal(581, lines[0].Unit!.Value, 0);
        Assert.Equal(1743, lines[0].Exalted, 0);

        Assert.Equal(20, lines[1].Stack);
        Assert.Equal(1, lines[1].Unit!.Value, 2);
    }

    [Fact]
    public void AndAddsUpToExactlyWhatTheTotalSays()
    {
        // The breakdown has to be the SAME arithmetic as the total, not a second opinion about
        // it. A table that does not sum to the figure above it is worse than no table.
        PriceBook book = Real();

        StashPage backpack = Page(
            InventoryKind.Backpack,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 7));
        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyModValues", "Art/2DItems/Currency/CurrencyModValues.dds", 2),
            Item("Metadata/Items/Currency/CurrencyWeaponQuality", "Art/2DItems/Currency/CurrencyWeaponQuality.dds", 40));

        Valued purse = book.Purse([backpack, tab]);
        IReadOnlyList<CurrencyPurse.PurseLine> lines = book.Breakdown([backpack, tab]);

        Assert.Equal(purse.Exalted, lines.Sum(line => line.Exalted), 3);
    }

    [Fact]
    public void TheSameCurrencyInTwoPlacesIsOneLine()
    {
        // So the count can be compared against what the game's own tab shows, which is the check
        // somebody actually performs.
        PriceBook book = Real();

        StashPage backpack = Page(
            InventoryKind.Backpack,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 7));
        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 13));

        IReadOnlyList<CurrencyPurse.PurseLine> lines = book.Breakdown([backpack, tab]);

        Assert.Single(lines);
        Assert.Equal(20, lines[0].Stack);
        Assert.Equal(20, lines[0].Exalted, 0);
    }

    [Fact]
    public void WhatCouldNotBePricedIsListedRatherThanHidden()
    {
        // The other half of "is this right": a purse whose biggest holding is unpriced is
        // understated, and that is as wrong as an overstatement. It goes last and contributes
        // nothing, but it is on the page.
        PriceBook book = Real();

        StashPage tab = Page(
            InventoryKind.Stash,
            Item("Metadata/Items/Currency/CurrencyWeaponQuality", "Art/2DItems/Currency/CurrencyWeaponQuality.dds", 40),
            Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", 5));

        IReadOnlyList<CurrencyPurse.PurseLine> lines = book.Breakdown([tab]);

        Assert.Equal(2, lines.Count);
        Assert.NotNull(lines[0].Unit);
        Assert.Null(lines[^1].Unit);
        Assert.Equal(40, lines[^1].Stack);
        Assert.Equal(0, lines[^1].Exalted);
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

    private static StashPage Tab(int id, params InspectedItem[] items)
        => new(
            id,
            InventoryKind.Stash,
            $"Stash tab {id}",
            12,
            12,
            [.. items.Select((item, i) => new StashSlot(new StashedItem((ulong)i + 1, i, 0, 1, 1), item))]);

    [Fact]
    public void ATabTHATWASNOTREADDoesNotEraseWhatItHeld()
    {
        // THE SYMPTOM, in the player's own words: "I added items and never took one out, and I
        // am eight Divine down." A tab the game has not filled in reads as holding nothing, and
        // replacing the whole remembered stash with one instant's reading let that nothing
        // stand for the tab's contents.
        InspectedItem money = Item(
            "Metadata/Items/Currency/CurrencyAddModToRare",
            "Art/2DItems/Currency/CurrencyAddModToRare.dds",
            10);

        IReadOnlyList<StashPage> had = [Tab(7, money), Tab(8, money)];

        // Only tab 8 came back this read - tab 7 was not loaded, so it is not in the list at all.
        IReadOnlyList<StashPage> now = StashInspector.Remembered(had, [Tab(8, money)]);

        Assert.Equal(2, now.Count);
        Assert.Single(now[0].Items);
        Assert.Equal(7, now[0].Id);
    }

    [Fact]
    public void ATabTHATWASReadAndCameBackEmptyIsARealZero()
    {
        // The other half, and it was right before: somebody who has just emptied a tab is
        // looking at it, so it IS loaded, and its zero has to replace what it held.
        InspectedItem money = Item(
            "Metadata/Items/Currency/CurrencyAddModToRare",
            "Art/2DItems/Currency/CurrencyAddModToRare.dds",
            10);

        IReadOnlyList<StashPage> now = StashInspector.Remembered([Tab(7, money)], [Tab(7)]);

        Assert.Single(now);
        Assert.Empty(now[0].Items);
    }

    [Fact]
    public void AReadThatSawNOTabsAtAllChangesNothing()
    {
        IReadOnlyList<StashPage> had =
        [
            Tab(7, Item(
                "Metadata/Items/Currency/CurrencyAddModToRare",
                "Art/2DItems/Currency/CurrencyAddModToRare.dds",
                10)),
        ];

        Assert.Same(had, StashInspector.Remembered(had, []));
    }

    [Fact]
    public void TabsKeepTheGamesOwnOrderRatherThanTheOrderTheyWereSeenIn()
    {
        // Otherwise the breakdown reshuffles itself every time a different tab is the one that
        // happened to be readable.
        IReadOnlyList<StashPage> now = StashInspector.Remembered(
            [Tab(9), Tab(3)], [Tab(5), Tab(1)]);

        Assert.Equal([1, 3, 5, 9], now.Select(page => page.Id));
    }
}
