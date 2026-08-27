using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Pricing a whole league off the exchange, against two real hours of Standard.
/// </summary>
/// <remarks>
/// The fixtures carry EVERY Standard market from the hours they were captured in, which is the
/// only reason these tests can say anything about routing: Standard is where routing matters,
/// because barely a fifth of its currencies trade against Exalted in any given hour.
/// </remarks>
public sealed class ExchangePairsTests
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

    private static ExchangePairs Standard(params int[] hours)
    {
        var pairs = new ExchangePairs();
        foreach (int hour in hours)
        {
            pairs.Add(Hour(hour), "Standard");
        }

        return pairs;
    }

    /// <summary>One market, in the feed's own shape, for the cases no capture happens to hold.</summary>
    /// <remarks>
    /// Both ratio sides are set to the same number, so bid equals ask: these tests are about
    /// WHICH market gets chosen, and a spread would only make the arithmetic harder to read.
    /// </remarks>
    private static string Market(string a, string b, double bid, double volume, double stock)
    {
        // Written by hand rather than with a raw string literal: the shape ends in four closing
        // braces, and a $$"""...""" cannot tell those apart from an interpolation hole.
        string Both(double first, double second)
            => Where(a, first) + "," + Where(b, second);

        return "{\"league\":\"Test\",\"market_pair\":[" + Quoted(a) + "," + Quoted(b) + "],"
               + "\"volume_traded\":{" + Both(volume, volume * bid) + "},"
               + "\"lowest_stock\":{" + Both(stock, stock) + "},"
               + "\"lowest_ratio\":{" + Both(1, bid) + "},"
               + "\"highest_ratio\":{" + Both(1, bid) + "}}";
    }

    private static string Quoted(string what) => "\"" + what + "\"";

    private static string Where(string path, double how)
        => Quoted(path) + ":" + how.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ExchangePairs Made(params string[] markets)
    {
        var pairs = new ExchangePairs();
        pairs.Add("{\"markets\":[" + string.Join(",", markets) + "]}", "Test");
        return pairs;
    }

    [Fact]
    public void TheLeaguesOwnMoneyIsReadOffTheMarketsRatherThanAssumed()
    {
        // WHAT REPLACED A HARDCODED EXALTED. Counted over six live hours, Runes of Aldur has 399
        // currencies trading against Exalted and 265 against Divine; Standard has 29 against
        // Exalted and 117 against Divine. Neither currency is the right constant, because the
        // right answer is different per league - so it is counted instead.
        Assert.Equal(ExchangeFeed.Divine, Standard(1).Pivot);
        Assert.Equal(ExchangeFeed.Divine, Standard(1, 2).Pivot);

        // And the other way round, so this is a measurement and not a rename of the constant.
        ExchangePairs active = Made(
            Market("Metadata/Items/A", ExchangeFeed.Exalted, 3, 100, 100),
            Market("Metadata/Items/B", ExchangeFeed.Exalted, 4, 100, 100),
            Market("Metadata/Items/C", ExchangeFeed.Divine, 5, 100, 100));

        Assert.Equal(ExchangeFeed.Exalted, active.Pivot);
    }

    [Fact]
    public void NothingReadAtAllStillNamesAMoney()
    {
        // Exalted until the markets say otherwise. A pivot of "" would make every comparison
        // against it quietly false, which is a worse failure than an assumption that is right
        // in one league out of two.
        Assert.Equal(ExchangeFeed.Exalted, new ExchangePairs().Pivot);
    }

    [Fact]
    public void ADeeperRouteBeatsAThinnerDirectMarket()
    {
        // THE BUG THE PIVOT WORK FOUND. Worth() used to return the Exalted market the instant
        // one existed, which sounds like the stronger claim - one leg, one spread. Measured over
        // six live hours, of the currencies that had BOTH a direct Exalted market and a usable
        // route, the route's thinnest leg was deeper than the direct market in 9 of 11 in
        // Standard and 87 of 187 in Runes of Aldur. So "direct wins" was choosing the worse
        // market most of the time in the league this tool is actually used in.
        const string Thing = "Metadata/Items/Thing";

        ExchangePairs pairs = Made(
            Market(Thing, ExchangeFeed.Exalted, 10, 3, 3),        // direct, and almost nobody traded it
            Market(Thing, ExchangeFeed.Divine, 0.05, 900, 900),   // deep first leg
            Market(ExchangeFeed.Divine, ExchangeFeed.Exalted, 260, 900, 900));

        Valuation worth = pairs.Worth(Thing);

        Assert.Equal(ExchangeFeed.Divine, worth.Through);
        Assert.Equal(0.05 * 260, worth.Exalted, 6);
        Assert.Equal(900, worth.Volume, 6);
    }

    [Fact]
    public void AnEquallyDeepDirectMarketKeepsTheTie()
    {
        // Which is what makes "one leg is the stronger claim" survive without a fudge factor to
        // prefer it by: the direct candidate is simply weighed first, and the hop has to be
        // strictly deeper to displace it.
        const string Thing = "Metadata/Items/Thing";

        ExchangePairs pairs = Made(
            Market(Thing, ExchangeFeed.Exalted, 10, 900, 900),
            Market(Thing, ExchangeFeed.Divine, 0.05, 900, 900),
            Market(ExchangeFeed.Divine, ExchangeFeed.Exalted, 260, 900, 900));

        Valuation worth = pairs.Worth(Thing);

        Assert.True(worth.Direct);
        Assert.Equal(10, worth.Exalted, 6);
    }

    [Fact]
    public void AHopThroughTheLeaguesOwnMoneyIsAnOrdinaryTrade()
    {
        // The distinction the table's colouring needs. Direct means "came from an Exalted
        // market", which in Standard is true of a handful of currencies - colouring by it paints
        // a perfectly ordinary Divine economy as second-rate on nine rows in ten.
        ExchangePairs pairs = Standard(1);
        Assert.Equal(ExchangeFeed.Divine, pairs.Pivot);

        Valuation exalted = pairs.Worth(ExchangeFeed.Divine);
        Assert.True(exalted.Direct);
        Assert.True(pairs.Ordinary(exalted));

        string viaMoney = pairs.Everything()
            .First(p => pairs.Worth(p) is { Known: true, Direct: false } w
                        && string.Equals(w.Through, pairs.Pivot, StringComparison.Ordinal));

        Assert.True(pairs.Ordinary(pairs.Worth(viaMoney)));

        // And something nobody has a price for is not "ordinary", it is nothing.
        Assert.False(pairs.Ordinary(pairs.Worth("Metadata/Items/Currency/CurrencyNobodyHasEverSeen")));
    }

    [Fact]
    public void MostOfStandardIsAnOrdinaryTradeEvenThoughAlmostNoneOfItIsDirect()
    {
        // THE MEASUREMENT THE PIVOT EXISTS FOR, on the two committed hours. Colouring the table
        // by Direct calls almost all of Standard second-rate; colouring it by Ordinary calls
        // almost all of it what it is - a currency with a working Divine market. Both counts are
        // asserted, so this fails if either the pivot stops being read or Direct starts lying.
        ExchangePairs pairs = Standard(1, 2);

        var priced = 0;
        var direct = 0;
        var ordinary = 0;
        foreach (string path in pairs.Everything())
        {
            Valuation worth = pairs.Worth(path);
            if (!worth.Known)
            {
                continue;
            }

            priced++;
            if (worth.Direct)
            {
                direct++;
            }

            if (pairs.Ordinary(worth))
            {
                ordinary++;
            }
        }

        Assert.True(priced > 100, $"only {priced} priced - the fixture is not Standard-like");
        Assert.True(
            direct * 4 < priced,
            $"expected direct Exalted markets to be rare in Standard; {direct} of {priced}");
        Assert.True(
            ordinary > priced / 2,
            $"expected most rows to be against the league's own money; {ordinary} of {priced}");
    }

    [Fact]
    public void AnExaltedOrbIsWorthAnExaltedOrb()
    {
        // The feed has no market of a thing against itself, and "unpriced" would be the wrong
        // answer for the very unit everything else is quoted in.
        Valuation one = Standard(1).Worth(ExchangeFeed.Exalted);

        Assert.Equal(1, one.Exalted);
        Assert.True(one.Direct);
    }

    [Fact]
    public void ADirectMarketIsPricedWithoutAHop()
    {
        Valuation divine = Standard(1).Worth(ExchangeFeed.Divine);

        Assert.True(divine.Known);
        Assert.True(divine.Direct);
        Assert.Empty(divine.Through);

        // The selling side, which is the 260 the game's own window showed - not the 602 it
        // would charge, and not the 473 that changed hands between them.
        Assert.Equal(260, divine.Exalted, 0);
    }

    [Fact]
    public void MostOfStandardWouldBeUnpriceableWithoutRouting()
    {
        // THE MEASUREMENT THAT MADE THIS CLASS EXIST. If this ever stops being true the graph is
        // dead weight, and if it stays true a direct-only reading is throwing most of a stash
        // away. Either outcome is worth knowing about.
        ExchangePairs pairs = Standard(1);

        var direct = 0;
        var routed = 0;
        foreach (string path in pairs.Everything())
        {
            Valuation worth = pairs.Worth(path);
            if (!worth.Known)
            {
                continue;
            }

            if (worth.Direct)
            {
                direct++;
            }
            else
            {
                routed++;
            }
        }

        Assert.True(routed > 0, "no currency needed a hop - the fixture is not Standard-like");
        Assert.True(
            routed > direct,
            $"expected routing to carry most of Standard; direct {direct}, routed {routed}");
    }

    [Fact]
    public void AHoppedValueIsMarkedAsOne()
    {
        ExchangePairs pairs = Standard(1);

        string hopped = pairs.Everything().First(p => pairs.Worth(p) is { Known: true, Direct: false });
        Valuation worth = pairs.Worth(hopped);

        // A two-leg claim says so, because it is a weaker one than a direct market and whoever
        // reads it deserves to know which they have.
        Assert.NotEmpty(worth.Through);
        Assert.Contains(worth.Through, ExchangePairs.Majors);
        Assert.NotEqual(ExchangeFeed.Exalted, worth.Through);
    }

    [Fact]
    public void AHopMultipliesTheTwoLegsItNames()
    {
        // Not a tautology: it pins that the value really is the two bids multiplied, so a
        // future change that quietly used the ask, or the traded rate, or one leg only, fails.
        ExchangePairs pairs = Standard(1);

        string hopped = pairs.Everything().First(p => pairs.Worth(p) is { Known: true, Direct: false });
        Valuation worth = pairs.Worth(hopped);

        ExchangeRate first = pairs.Rate(hopped, worth.Through);
        ExchangeRate second = pairs.Rate(worth.Through, ExchangeFeed.Exalted);

        Assert.Equal(first.Bid * second.Bid, worth.Exalted, 8);
    }

    [Fact]
    public void ARouteIsWorthItsThinnestLeg()
    {
        ExchangePairs pairs = Standard(1);

        string hopped = pairs.Everything().First(p => pairs.Worth(p) is { Known: true, Direct: false });
        Valuation worth = pairs.Worth(hopped);

        ExchangeRate first = pairs.Rate(hopped, worth.Through);
        ExchangeRate second = pairs.Rate(worth.Through, ExchangeFeed.Exalted);

        // A fat second leg cannot rescue a first one that two orbs set, so the smaller carries.
        Assert.Equal(Math.Min(first.Volume, second.Volume), worth.Volume, 6);
    }

    [Fact]
    public void APairIsFoundWhicheverWayTheFeedHappenedToListIt()
    {
        ExchangePairs pairs = Standard(1);

        ExchangeRate divInEx = pairs.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted);
        ExchangeRate exInDiv = pairs.Rate(ExchangeFeed.Exalted, ExchangeFeed.Divine);

        Assert.True(divInEx.Known);
        Assert.True(exInDiv.Known);
        Assert.Equal(1.0 / divInEx.Traded, exInDiv.Traded, 8);
    }

    [Fact]
    public void FlippingAMarketSwapsTheSidesRatherThanJustInvertingThem()
    {
        // The subtle one. What you RECEIVE selling a Divine is the reciprocal of what you PAY
        // buying an Exalted - not of what you receive for one. Getting this wrong would price
        // every hopped currency at the wrong side of every spread, invisibly.
        ExchangePairs pairs = Standard(1);

        ExchangeRate forward = pairs.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted);
        ExchangeRate back = pairs.Rate(ExchangeFeed.Exalted, ExchangeFeed.Divine);

        Assert.Equal(1.0 / forward.Ask, back.Bid, 8);
        Assert.Equal(1.0 / forward.Bid, back.Ask, 8);
        Assert.NotEqual(1.0 / forward.Bid, back.Bid, 8);
    }

    [Fact]
    public void AnOlderHourFillsGapsRatherThanOverwriting()
    {
        ExchangePairs one = Standard(1);
        ExchangePairs both = Standard(1, 2);

        // Standard is thin enough that a second hour brings genuinely new markets.
        Assert.True(both.Count > one.Count, $"one hour {one.Count}, two hours {both.Count}");

        // And the newer hour's answer for a market both had is kept - hours arrive newest first,
        // so the first answer is also the freshest.
        Assert.Equal(
            one.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted).Bid,
            both.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted).Bid,
            6);
    }

    [Fact]
    public void AnotherLeagueContributesNothing()
    {
        var pairs = new ExchangePairs();
        pairs.Add(Hour(1), "Runes of Aldur");

        // The fixture carries a handful of that league's markets alongside Standard's, and
        // mixing two leagues' prices into one book is the mistake that already cost a day here.
        Assert.True(pairs.Count > 0);
        Assert.True(
            pairs.Count < Standard(1).Count,
            "reading one league picked up another league's markets");
    }

    [Fact]
    public void SomethingNobodyTradedIsUnpricedRatherThanFree()
    {
        ExchangePairs pairs = Standard(1);

        Assert.False(pairs.Worth("Metadata/Items/Currency/CurrencyNobodyHasEverSeen").Known);
        Assert.False(pairs.Worth(null).Known);
        Assert.False(pairs.Worth(string.Empty).Known);
    }

    [Fact]
    public void RubbishAddsNothingAndThrowsNothing()
    {
        var pairs = new ExchangePairs();

        Assert.Equal(0, pairs.Add("{not json", "Standard"));
        Assert.Equal(0, pairs.Add("{}", "Standard"));
        Assert.Equal(0, pairs.Add(null, "Standard"));
        Assert.Equal(0, pairs.Add(Hour(1), string.Empty));
        Assert.Equal(0, pairs.Count);
    }

    [Fact]
    public void THEBOOKBehindAPriceIsTheMarketThatSetIt()
    {
        // MEASURED, and the reason this exists. The rates page asked the direct Exalted market
        // for the depth behind every row - but on these hours only 13 of the 115 priced
        // currencies HAVE a direct Exalted market, so the column read as an empty one while 88
        // more had a book on the leg they were actually priced through.
        ExchangePairs pairs = Standard(1, 2);

        int priced = 0, routed = 0, routedWithBook = 0, directWithBook = 0;
        foreach (string path in pairs.Everything())
        {
            if (string.Equals(path, ExchangeFeed.Exalted, StringComparison.Ordinal))
            {
                continue;
            }

            Valuation worth = pairs.Worth(path);
            if (!worth.Known)
            {
                continue;
            }

            priced++;
            if (pairs.Rate(path, ExchangeFeed.Exalted).Stock > 0)
            {
                directWithBook++;
            }

            if (worth.Direct)
            {
                continue;
            }

            routed++;
            if (worth.Stock > 0)
            {
                routedWithBook++;
            }
        }

        Assert.True(priced > 100, $"only {priced} priced");
        Assert.True(routed > directWithBook * 4,
            $"{routed} routed against {directWithBook} with a direct book - the gap is the point");
        Assert.True(routedWithBook > directWithBook * 4,
            $"only {routedWithBook} routed rows carry a book, against {directWithBook} direct");
    }

    [Fact]
    public void ARoutedBookIsTheTHINNERLegRatherThanEither()
    {
        // A chain is worth its weakest link: being able to sell a thousand of something into
        // Divine is worth nothing if the Divine side can only absorb ten.
        ExchangePairs pairs = Standard();

        foreach (string path in pairs.Everything())
        {
            Valuation worth = pairs.Worth(path);
            if (!worth.Known || worth.Direct || worth.Through.Length == 0)
            {
                continue;
            }

            double first = pairs.Rate(path, worth.Through).Stock;
            double second = pairs.Rate(worth.Through, ExchangeFeed.Exalted).Stock;

            Assert.Equal(Math.Min(first, second), worth.Stock);
        }
    }

    [Fact]
    public void ADirectPriceCarriesItsOwnBook()
    {
        ExchangePairs pairs = Standard(1, 2);
        Valuation divine = pairs.Worth(ExchangeFeed.Divine);

        Assert.True(divine.Direct);
        Assert.Equal(pairs.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted).Stock, divine.Stock);
        Assert.True(divine.Stock > 0);
    }
}
