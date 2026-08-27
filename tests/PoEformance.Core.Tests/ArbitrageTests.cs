using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Loops through the exchange, and the reasons to disbelieve most of them.
/// </summary>
/// <remarks>
/// Every number here comes from the same real Standard hours and the same real catalogue capture
/// the rest of these tests read. That matters more than usual: the whole class exists because
/// SYNTHETIC data would never have shown the problem. Run over one live hour with no guard, the
/// same arithmetic finds a route reading +314,900 percent.
/// </remarks>
public sealed class ArbitrageTests
{
    private static string Fixture(string name)
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

        throw new FileNotFoundException($"fixture {name} not found");
    }

    private static ExchangePairs Standard()
    {
        var pairs = new ExchangePairs();
        pairs.Add(Fixture("ggg-exchange-hour1.json"), "Standard");
        pairs.Add(Fixture("ggg-exchange-hour2.json"), "Standard");
        return pairs;
    }

    /// <summary>The same entry, saying a different price - settled day and all.</summary>
    /// <remarks>
    /// The settled day has to move too, not just CurrentPrice: what a cross-check reads is
    /// ScoutEntry.Steady, and a fixture that changed only the current price would be testing
    /// nothing at all.
    /// </remarks>
    private static ScoutEntry Worth(ScoutEntry entry, double exalted)
        => entry with
        {
            Exalted = exalted,
            Days = [new ScoutDay(new DateOnly(2026, 8, 26), exalted, 1_000_000)],
        };

    private static IReadOnlyDictionary<string, ScoutEntry> Index()
        => ScoutCatalog.Read(Fixture("scout-currency-standard.json"));

    [Fact]
    public void WithoutASecondSourceNothingIsOffered()
    {
        // NOT A DEGRADED MODE. A route computed from one source is the exact thing this refuses,
        // so offering one when the index is missing would be the failure dressed as a fallback.
        Assert.Empty(Arbitrage.Routes(Standard(), null));
        Assert.Empty(Arbitrage.Routes(Standard(), new Dictionary<string, ScoutEntry>()));
        Assert.Empty(Arbitrage.Routes(null, Index()));
    }

    [Fact]
    public void NoSurvivingRouteIsAbsurd()
    {
        // THE TEST THIS CLASS WAS WRITTEN FOR. Ungated, the same data yields six-figure
        // percentages. Anything that gets past the guards has to be a number somebody could say
        // out loud - and if this ever fails, the guards have stopped working and the page is
        // lying again.
        IReadOnlyList<Route> routes = Arbitrage.Routes(Standard(), Index());

        foreach (Route route in routes)
        {
            Assert.InRange(route.Gain, Arbitrage.Worthwhile, 1.0);
            Assert.True(route.Carries >= Arbitrage.Deep);
            Assert.NotEqual(ExchangeFeed.Exalted, route.Through);
            Assert.NotEqual(route.Path, route.Through);
        }
    }

    [Fact]
    public void RoutesComeBestFirst()
    {
        IReadOnlyList<Route> routes = Arbitrage.Routes(Standard(), Index());

        for (var at = 1; at < routes.Count; at++)
        {
            Assert.True(routes[at - 1].Gain >= routes[at].Gain);
        }
    }

    [Fact]
    public void ARateTheIndexDisagreesWithIsNotEvidence()
    {
        ExchangePairs pairs = Standard();
        IReadOnlyDictionary<string, ScoutEntry> index = Index();

        // The pair the game's own window was checked against, so both sources really do know it.
        Assert.True(Arbitrage.Agrees(pairs, index, ExchangeFeed.Divine, ExchangeFeed.Exalted));

        // Now tell the index a Divine is worth one Exalted. The feed says 260, so they are two
        // orders of magnitude apart and the leg must stop being usable evidence of anything.
        var lying = new Dictionary<string, ScoutEntry>(index, StringComparer.Ordinal)
        {
            [ExchangeFeed.Divine] = Worth(index[ExchangeFeed.Divine], 1),
        };

        Assert.False(Arbitrage.Agrees(pairs, lying, ExchangeFeed.Divine, ExchangeFeed.Exalted));
    }

    [Fact]
    public void SilenceIsNotAgreement()
    {
        // The point is corroboration, and a source that has never heard of the pair corroborates
        // nothing. Reading "not mentioned" as "no objection" is how the fantasies got through.
        ExchangePairs pairs = Standard();

        Assert.False(Arbitrage.Agrees(pairs, new Dictionary<string, ScoutEntry>(),
            ExchangeFeed.Divine, ExchangeFeed.Exalted));

        Assert.False(Arbitrage.Agrees(pairs, Index(),
            "Metadata/Items/Currency/CurrencyNobodyHasEverTraded", ExchangeFeed.Exalted));
    }

    [Fact]
    public void APairNeitherSourceTradedAgreesWithNothing()
    {
        Assert.False(Arbitrage.Agrees(Standard(), Index(),
            "Metadata/Items/Currency/A", "Metadata/Items/Currency/B"));

        Assert.False(Arbitrage.Agrees(null, Index(), ExchangeFeed.Divine, ExchangeFeed.Exalted));
        Assert.False(Arbitrage.Agrees(Standard(), null, ExchangeFeed.Divine, ExchangeFeed.Exalted));
    }

    [Fact]
    public void AgreementIsMeasuredAsARatioRatherThanADifference()
    {
        // These span six orders of magnitude: a quarter of a Mirror and a quarter of a Wisdom
        // Scroll are not comparable quantities, but "within a quarter of each other" is the same
        // test for both. A difference-based check would wave through everything cheap and reject
        // everything dear.
        ExchangePairs pairs = Standard();
        IReadOnlyDictionary<string, ScoutEntry> index = Index();

        ExchangeRate divine = pairs.Rate(ExchangeFeed.Divine, ExchangeFeed.Exalted);
        Assert.True(divine.Bid > 100, "the fixture's Divine rate should be in the hundreds");

        // Just inside the band, expressed as a ratio of the feed's own number.
        var close = new Dictionary<string, ScoutEntry>(index, StringComparer.Ordinal)
        {
            [ExchangeFeed.Divine] = Worth(index[ExchangeFeed.Divine], divine.Bid * (1 + (Arbitrage.Tolerance / 2))),
        };
        Assert.True(Arbitrage.Agrees(pairs, close, ExchangeFeed.Divine, ExchangeFeed.Exalted));

        var far = new Dictionary<string, ScoutEntry>(index, StringComparer.Ordinal)
        {
            [ExchangeFeed.Divine] = Worth(index[ExchangeFeed.Divine], divine.Bid * (1 + (Arbitrage.Tolerance * 3))),
        };
        Assert.False(Arbitrage.Agrees(pairs, far, ExchangeFeed.Divine, ExchangeFeed.Exalted));
    }

    [Fact]
    public void AGainIsMeasuredAgainstSellingItStraight()
    {
        var route = new Route("x", "X", "m", Direct: 100, Routed: 125, Carries: 500);

        Assert.Equal(0.25, route.Gain, 8);
        Assert.Equal(0, new Route("x", "X", "m", 0, 125, 500).Gain);
    }
}
