using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Asking the game's own trade site what the uniques poe.ninja missed are going for.
/// </summary>
/// <remarks>
/// The transport is handed in, so none of this touches a browser or a network. What is covered
/// is everything that decides WHETHER to ask and what to believe about the answer - which is
/// where the damage is. The trade API hands back individual asking prices, and a single asking
/// price is exactly the illiquidity the poe.ninja gates exist to keep out, arriving by a
/// different door.
/// </remarks>
public class TradePricesTests
{
    private const string Exchange = """
        {"core":{"rates":{"exalted":500}},
         "items":[{"id":"exalted","image":"/gen/image/x/CurrencyAddModToRare.png"},
                  {"id":"chaos","image":"/gen/image/x/CurrencyRerollRare.png"}],
         "lines":[{"id":"exalted","primaryValue":0.002,"volumePrimaryValue":50},
                  {"id":"chaos","primaryValue":0.02,"volumePrimaryValue":50}]}
        """;

    private static PriceBook Book()
    {
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Exchange);
        return book;
    }

    private static string Somewhere() => Path.Combine(Path.GetTempPath(), $"trade-{Guid.NewGuid():N}.json");

    /// <summary>
    /// Waits for the query that is out, if there is one.
    /// </summary>
    /// <remarks>
    /// The QUERY rather than a spin on "is it busy yet". A spin turns every check below into a
    /// race with the thread pool: it passed on a quiet machine and failed on a loaded CI runner,
    /// which is the worst kind of check - one that reports on the machine rather than on the
    /// code. Waiting on the task itself is the same question with a definite answer.
    /// </remarks>
    private static void Settle(TradePrices trade)
        => Assert.True(trade.Sending.Wait(TimeSpan.FromSeconds(30)), "the query never finished");

    private static TradeAnswer Answered(params (double Amount, string Currency)[] listings)
        => new(true, 200, [.. listings.Select(one => new TradeListing(one.Amount, one.Currency))], string.Empty);

    private static InspectedItem Unique(string name, ulong entity = 1)
        => new(entity, "Metadata/Items/Thing", "Stellar Amulet", name, "SomeIvi", 3, true, 0, 0, "", [], []);

    [Fact]
    public void ONELISTINGIsNotAMarketHoweverItIsAveraged()
    {
        // The one that mattered: "median of the cheapest few" over a single listing IS that
        // listing, so one troll ask became the price of the item. Below three it is unpriced.
        PriceBook book = Book();

        Assert.Null(TradePrices.Robust([new TradeListing(4225, "divine")], book));
        Assert.Null(TradePrices.Robust([new TradeListing(1, "exalted"), new TradeListing(4225, "divine")], book));
        Assert.NotNull(TradePrices.Robust(
            [new TradeListing(1, "exalted"), new TradeListing(2, "exalted"), new TradeListing(3, "exalted")],
            book));
    }

    [Fact]
    public void ANDTheCheapestListingIsNotThePriceEither()
    {
        // The cheapest is as often a mispriced item or somebody logged off as it is the market.
        // Five listings, so the median of the cheapest five is the third.
        PriceBook book = Book();

        double? worth = TradePrices.Robust(
            [
                new TradeListing(1, "exalted"),
                new TradeListing(40, "exalted"),
                new TradeListing(44, "exalted"),
                new TradeListing(46, "exalted"),
                new TradeListing(50, "exalted"),
            ],
            book);

        Assert.Equal(44.0, worth!.Value, 3);
    }

    [Fact]
    public void EXALTEDIsOneAndDivineIsTheRateWhateverElseTheBookHolds()
    {
        PriceBook book = Book();

        Assert.Equal(1.0, book.Quoted("exalted")!.Value, 3);
        Assert.Equal(500.0, book.Quoted("divine")!.Value, 3);

        // And a line that came out of the exchange half converts at what that line said: 0.02
        // Divine at 500 Exalted to the Divine.
        Assert.Equal(10.0, book.Quoted("chaos")!.Value, 3);
    }

    [Fact]
    public void ACURRENCYTheBookCannotConvertCostsThatListingAndNotThePrice()
    {
        // Dropped rather than guessed at: the two vocabularies LOOK alike, but a made-up rate
        // would be a price somebody believes. Enough of them dropped leaves it unpriced, which
        // is the intended failure.
        PriceBook book = Book();

        Assert.Null(book.Quoted("somethingnewthisleague"));

        double? mixed = TradePrices.Robust(
            [
                new TradeListing(3, "exalted"),
                new TradeListing(4, "exalted"),
                new TradeListing(5, "exalted"),
                new TradeListing(1, "somethingnewthisleague"),
            ],
            book);

        Assert.Equal(4.0, mixed!.Value, 3);

        Assert.Null(TradePrices.Robust(
            [
                new TradeListing(1, "somethingnewthisleague"),
                new TradeListing(2, "somethingnewthisleague"),
                new TradeListing(3, "somethingnewthisleague"),
            ],
            book));
    }

    [Fact]
    public void ONLYTheUniquesTheBookCouldNotPriceAreEverAsked()
    {
        // poe.ninja is one request for thousands of prices; this is one request per name at
        // three and a half seconds each. A stash holds hundreds of uniques.
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Exchange);
        book.Add(PriceKind.Listed, """
            {"core":{"rates":{"exalted":500}},
             "lines":[{"name":"Astramentis","primaryValue":2,"listingCount":900}]}
            """);

        using var trade = new TradePrices(Somewhere()) { Enabled = true };
        trade.Watching("Standard");

        var page = new StashPage(1, InventoryKind.Stash, "Stash tab 1", 12, 12,
        [
            new StashSlot(new StashedItem(1, 0, 0, 1, 1), Unique("Astramentis", 1)),
            new StashSlot(new StashedItem(2, 1, 0, 1, 1), Unique("Redbeak", 2)),
            new StashSlot(new StashedItem(3, 2, 0, 1, 1), Unique("Redbeak", 3)),
        ]);

        Assert.Equal(1, trade.Ask(new StashView([page], string.Empty), book));
        Assert.Equal(1, trade.Waiting);
    }

    [Fact]
    public void NOTHINGIsAskedUntilSomebodyTurnsItOn()
    {
        // It opens a browser and asks somebody to sign in to their own account. That is a
        // decision, not something to discover.
        var asked = 0;
        using var trade = new TradePrices(Somewhere(), (name, league, token) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(Answered((1, "exalted")));
        });

        trade.Watching("Standard");
        trade.Ask("Astramentis");
        trade.Service();
        Settle(trade);

        Assert.Equal(0, asked);
        Assert.Equal(0, trade.Waiting);
    }

    [Fact]
    public void ONEQueryGoesOutAtATimeAndTheyAreSpacedApart()
    {
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var asked = new List<string>();

        using var trade = new TradePrices(Somewhere(), (name, league, token) =>
        {
            lock (asked)
            {
                asked.Add(name);
            }

            return Task.FromResult(Answered((3, "exalted"), (4, "exalted"), (5, "exalted")));
        }, () => now)
        {
            Enabled = true,
            Book = Book(),
        };

        trade.Watching("Standard");
        trade.Ask("Astramentis");
        trade.Ask("Redbeak");

        trade.Service();
        Settle(trade);
        Assert.Single(asked);

        // Straight away again: this is somebody else's server, and a stash read queues forty
        // names at once.
        trade.Service();
        Settle(trade);
        Assert.Single(asked);

        now += TradePrices.Apart;
        trade.Service();
        Settle(trade);
        Assert.Equal(2, asked.Count);

        Assert.Equal(4.0, trade.Worth("Astramentis")!.Value, 3);
    }

    [Fact]
    public void ASIGNINWallStopsTheAskingForLongerThanAStumbleDoes()
    {
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var asked = 0;
        var wall = true;

        using var trade = new TradePrices(Somewhere(), (name, league, token) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(wall
                ? TradeAnswer.Not("signed out", 403)
                : Answered((3, "exalted"), (4, "exalted"), (5, "exalted")));
        }, () => now)
        {
            Enabled = true,
            Book = Book(),
        };

        trade.Watching("Standard");
        trade.Ask("Astramentis");

        trade.Service();
        Settle(trade);
        Assert.Equal(1, asked);

        // The name goes back in the queue - it is the one somebody is looking at - and asking
        // again in fifteen seconds only gets refused again, four times a minute, at somebody
        // else's front door.
        Assert.Equal(1, trade.Waiting);

        now += TradePrices.AfterATrip + TradePrices.Apart;
        trade.Service();
        Settle(trade);
        Assert.Equal(1, asked);

        wall = false;
        now += TradePrices.AfterAWall;
        trade.Service();
        Settle(trade);

        Assert.Equal(2, asked);
        Assert.Equal(4.0, trade.Worth("Astramentis")!.Value, 3);
    }

    [Fact]
    public void ANEmptyAnswerIsRememberedSoItIsNotAskedForAgain()
    {
        // A unique nobody is selling answers with nothing. Not written down, it is asked again
        // on every stash read, at three and a half seconds a name, for the rest of the session.
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var asked = 0;

        string file = Somewhere();

        try
        {
            using var trade = new TradePrices(file, (name, league, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult(Answered());
            }, () => now)
            {
                Enabled = true,
                Book = Book(),
            };

            trade.Watching("Standard");
            trade.Ask("Redbeak");
            trade.Service();
            Settle(trade);

            Assert.Equal(1, asked);
            Assert.Null(trade.Worth("Redbeak"));

            now += TradePrices.Apart;
            trade.Ask("Redbeak");
            trade.Service();
            Settle(trade);

            Assert.Equal(1, asked);
            Assert.Equal(0, trade.Waiting);

            // But not forever: a "nobody is selling one" ages out sooner than a price does.
            now += TradePrices.KeepsNothing + TimeSpan.FromMinutes(1);
            trade.Ask("Redbeak");
            trade.Service();
            Settle(trade);

            Assert.Equal(2, asked);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void THEAnswersAreKeptForNextTimeButOnlyForTheSameLeague()
    {
        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        string file = Somewhere();

        try
        {
            using (var first = new TradePrices(file, (name, league, token) =>
                       Task.FromResult(Answered((3, "exalted"), (4, "exalted"), (5, "exalted"))), () => now)
                   {
                       Enabled = true,
                       Book = Book(),
                   })
            {
                first.Watching("Standard");
                first.Ask("Astramentis");
                first.Service();
                Settle(first);
                Assert.Equal(4.0, first.Worth("Astramentis")!.Value, 3);
            }

            using var next = new TradePrices(file, null, () => now) { Enabled = true };
            next.Watching("Standard");

            Assert.Equal(1, next.Known);
            Assert.Equal(4.0, next.Worth("Astramentis")!.Value, 3);

            // Somewhere else, those are not old answers - they are a different market.
            using var elsewhere = new TradePrices(file, null, () => now) { Enabled = true };
            elsewhere.Watching("HC Runes of Aldur");

            Assert.Equal(0, elsewhere.Known);
            Assert.Null(elsewhere.Worth("Astramentis"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ATRADEPriceOnlyFillsTheGapTheBookLeft()
    {
        // The order matters: a book is a league's trade averaged and gated, and a trade answer
        // is ten listings at one moment. The second is a good answer to a question the first
        // could not answer at all, and a worse one to a question it could.
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Exchange);
        book.Add(PriceKind.Listed, """
            {"core":{"rates":{"exalted":500}},
             "lines":[{"name":"Astramentis","primaryValue":2,"listingCount":900}]}
            """);

        DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        using var trade = new TradePrices(Somewhere(), (name, league, token) =>
            Task.FromResult(Answered((7, "exalted"), (8, "exalted"), (9, "exalted"))), () => now)
        {
            Enabled = true,
            Book = book,
        };

        trade.Watching("Standard");
        trade.Ask("Astramentis");
        trade.Ask("Redbeak");
        trade.Service();
        Settle(trade);
        now += TradePrices.Apart;
        trade.Service();
        Settle(trade);

        // The book's Astramentis wins over the trade site's; Redbeak has only the trade site.
        Assert.Equal(1000.0, book.Of(Unique("Astramentis"), trade)!.Value, 3);
        Assert.Equal(8.0, book.Of(Unique("Redbeak"), trade)!.Value, 3);
    }
}
