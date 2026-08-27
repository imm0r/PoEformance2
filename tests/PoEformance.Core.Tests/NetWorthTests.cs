using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// What a character is worth, tab by tab, priced from the game's own exchange.
/// </summary>
/// <remarks>
/// The pages are built by hand but the PRICES are not: they come from the same real Standard
/// hours everything else here reads, so a test that says an Essence tab is worth something is
/// saying the exchange really did trade essences that hour.
/// </remarks>
public sealed class NetWorthTests
{
    private const string Chaos = "Metadata/Items/Currency/CurrencyRerollRare";

    private static string Hour(int which)
    {
        string name = $"ggg-exchange-hour{which}.json";
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

        throw new FileNotFoundException($"captured hour {name} not found");
    }

    private static ExchangePairs Standard()
    {
        var pairs = new ExchangePairs();
        pairs.Add(Hour(1), "Standard");
        pairs.Add(Hour(2), "Standard");
        return pairs;
    }

    private static InspectedItem Item(string path, int stack = 1)
        => new(1, path, path[(path.LastIndexOf('/') + 1)..], string.Empty, string.Empty,
            -1, null, stack, 0, path, [], []);

    private static StashPage Tab(int id, InventoryKind kind, params InspectedItem[] items)
        => new(
            id, kind, kind == InventoryKind.Backpack ? "Backpack" : $"Stash tab {id}", 12, 12,
            [.. items.Select((one, at) => new StashSlot(new StashedItem((ulong)at + 1, at, 0, 1, 1), one))]);

    [Fact]
    public void EachTabIsCountedOnItsOwn()
    {
        ExchangePairs pairs = Standard();

        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [
                Tab(1, InventoryKind.Stash, Item(ExchangeFeed.Divine, 10)),
                Tab(2, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 500)),
            ],
            pairs);

        Assert.Equal(2, tabs.Count);

        TabWorth divines = tabs.Single(t => t.Id == 1);
        TabWorth exalts = tabs.Single(t => t.Id == 2);

        // Ten Divine at the SELLING rate the game's own window showed for this hour.
        Assert.Equal(2600, divines.Exalted, 0);
        Assert.Equal(500, exalts.Exalted, 0);
    }

    [Fact]
    public void TheRichestTabComesFirst()
    {
        // "Where is it" is answered by the top line, so the top line has to be the answer.
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [
                Tab(1, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 5)),
                Tab(2, InventoryKind.Stash, Item(ExchangeFeed.Divine, 50)),
                Tab(3, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 900)),
            ],
            Standard());

        Assert.Equal(2, tabs[0].Id);
        Assert.Equal(3, tabs[1].Id);
        Assert.Equal(1, tabs[2].Id);
    }

    [Fact]
    public void AnUntickedTabIsShownButNotAddedUp()
    {
        // It has to stay visible: a tab that vanished when unticked could never be found to tick.
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [
                Tab(1, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 100)),
                Tab(2, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 900)),
            ],
            Standard(),
            skipping: new HashSet<int> { 2 });

        Assert.Equal(2, tabs.Count);
        Assert.False(tabs.Single(t => t.Id == 2).Counted);

        // And its worth is still computed, so unticking it shows what is being left out.
        Assert.Equal(900, tabs.Single(t => t.Id == 2).Exalted, 0);
        Assert.Equal(100, NetWorth.Total(tabs).Exalted, 0);
    }

    [Fact]
    public void WornGearIsNotMoney()
    {
        // The scope decision this whole feature was built to: currency the character holds, not
        // what is bound up in the gear on their back.
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [Tab(1, InventoryKind.Equipped, Item(ExchangeFeed.Divine, 99))],
            Standard());

        Assert.Empty(tabs);
    }

    [Fact]
    public void SomethingTheExchangeNeverTradedIsCountedAsUnpricedRatherThanFree()
    {
        // The number that keeps a total honest. A tab half unpriced is not a tab worth what it
        // says, and swallowing that would report a fraction of a stash as the whole of it.
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [
                Tab(1, InventoryKind.Stash,
                    Item(ExchangeFeed.Divine, 1),
                    Item("Metadata/Items/Currency/CurrencyNobodyHasEverTraded", 1)),
            ],
            Standard());

        TabWorth only = Assert.Single(tabs);
        Assert.Equal(2, only.Stacks);
        Assert.Equal(1, only.Priced);
        Assert.Equal(1, only.Unpriced);
        Assert.Equal(260, only.Exalted, 0);
    }

    [Fact]
    public void TheExchangePricesWhatTheBookAloneWouldCallEmpty()
    {
        // THE MEASUREMENT THIS FEATURE RESTS ON. An empty book stands in for the aggregated
        // index having never heard of a currency; the exchange has, because it watched it trade.
        // If this stops being true the whole per-tab breakdown is built on nothing.
        ExchangePairs pairs = Standard();
        var book = new PriceBook();

        string obscure = pairs.Everything()
            .First(path => pairs.Worth(path).Known
                           && path != ExchangeFeed.Exalted
                           && path != ExchangeFeed.Divine);

        Assert.NotNull(NetWorth.Unit(Item(obscure), pairs, book));
        Assert.Null(NetWorth.Unit(Item(obscure), null, book));
    }

    [Fact]
    public void TheBookStillAnswersWhereTheExchangeHadAQuietHour()
    {
        // The exchange goes first, not alone. Anything it had no market for that hour still gets
        // whatever the book knows - dropping the book would trade one gap for another.
        var pairs = new ExchangePairs();

        Assert.Null(NetWorth.Unit(Item(ExchangeFeed.Divine), pairs, null));
        Assert.Null(NetWorth.Unit(null, pairs, new PriceBook()));
    }

    [Fact]
    public void ATotalOfNothingIsNotACrash()
    {
        Assert.Empty(NetWorth.ByTab(null, Standard()));
        Assert.Equal(0, NetWorth.Total([]).Exalted);
        Assert.Equal(0, NetWorth.Total(null).Exalted);
    }

    [Fact]
    public void TheTotalSaysHowManyTabsWentIntoIt()
    {
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(
            [
                Tab(1, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 1)),
                Tab(2, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 1)),
                Tab(3, InventoryKind.Stash, Item(ExchangeFeed.Exalted, 1)),
            ],
            Standard(),
            skipping: new HashSet<int> { 3 });

        Assert.Equal("2 of 3 tabs", NetWorth.Total(tabs).Called);
    }
}
