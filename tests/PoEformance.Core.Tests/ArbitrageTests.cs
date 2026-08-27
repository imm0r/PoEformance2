using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Loops through the exchange, and the reasons to disbelieve most of them.
/// </summary>
/// <remarks>
/// Every number here comes from the same real Standard hours and the same real catalogue capture
/// the rest of these tests read. That matters more than usual: the whole class exists because
/// SYNTHETIC data would never have shown the problem.
///
/// WHAT THE COMMITTED HOURS ACTUALLY SHOW, kept separate from what prompted the work. Ungated,
/// they yield six loops and the best reads +1,525 percent. Apply the depth rule and NOTHING is
/// left - all six are thin on the straight leg. So on this data the second source is never
/// reached, and its own tests are what hold it up.
///
/// The live hour that prompted the design was worse than these two - a route reading six figures
/// that survived depth - but that hour is not committed and nothing here should be read as
/// reproducing it.
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

    /// <summary>
    /// The same arithmetic with a guard switched off, so the fixtures can be shown to be ill.
    /// </summary>
    /// <remarks>
    /// A test that only checks the gated result is a test that passes when Routes() is changed to
    /// return nothing at all. This is the other half: it establishes that the captured hours
    /// really do contain the thing being caught.
    ///
    /// <paramref name="deep"/> applies the depth rule EXACTLY as Routes does - all three legs,
    /// the straight one included. Checking only the two routed legs made this report survivors
    /// the real walk had already thrown out, and a measurement that flatters the guard it is
    /// meant to test is worse than not measuring.
    /// </remarks>
    private static IReadOnlyList<(string Path, string Middle, double Gain)> Ungated(
        ExchangePairs pairs, bool deep)
    {
        var found = new List<(string, string, double)>();

        foreach (string path in pairs.Everything())
        {
            if (string.Equals(path, ExchangeFeed.Exalted, StringComparison.Ordinal))
            {
                continue;
            }

            ExchangeRate straight = pairs.Rate(path, ExchangeFeed.Exalted);
            if (!straight.Known || straight.Bid <= 0)
            {
                continue;
            }

            if (deep && straight.Stock < Arbitrage.Deep)
            {
                continue;
            }

            foreach (string middle in ExchangePairs.Majors)
            {
                if (string.Equals(middle, path, StringComparison.Ordinal)
                    || string.Equals(middle, ExchangeFeed.Exalted, StringComparison.Ordinal))
                {
                    continue;
                }

                ExchangeRate first = pairs.Rate(path, middle);
                ExchangeRate second = pairs.Rate(middle, ExchangeFeed.Exalted);
                if (!first.Known || !second.Known || first.Bid <= 0 || second.Bid <= 0)
                {
                    continue;
                }

                if (deep && Math.Min(first.Stock, second.Stock) < Arbitrage.Deep)
                {
                    continue;
                }

                double gain = ((first.Bid * second.Bid) - straight.Bid) / straight.Bid;
                if (gain < Arbitrage.Worthwhile)
                {
                    continue;
                }

                found.Add((path, middle, gain));
            }
        }

        return found;
    }

    [Fact]
    public void TheCapturedHoursReallyDoContainFantasies()
    {
        // THE HALF THAT MAKES THE OTHER HALF MEAN SOMETHING. On these two real Standard hours the
        // ungated arithmetic finds six loops and the best reads over a thousand percent. Without
        // this, "nothing survives" would also be true of a Routes() that returned nothing at all.
        IReadOnlyList<(string Path, string Middle, double Gain)> found = Ungated(Standard(), deep: false);
        double worst = found.Max(loop => loop.Gain);

        Assert.True(found.Count >= 5, $"only {found.Count} ungated loops - the fixture has gone tame");
        Assert.True(worst > 10, $"the worst ungated loop is only +{worst * 100:N0}%");
    }

    [Fact]
    public void NothingSurvivesOnTheseHours()
    {
        // Which is the RIGHT answer rather than a disappointing one: Standard is efficient and
        // every apparent loop in it is a stale fill.
        //
        // WHAT THIS ALONE DOES NOT PROVE, said out loud because the obvious reading is wrong. On
        // this data the two guards are INDEPENDENTLY sufficient: depth clears all six by itself,
        // and so does the second source. Removing either from the walk leaves this test still
        // passing - checked, by removing each in turn - so it cannot show which one is load
        // bearing, and neither guard is proven NECESSARY by these hours.
        //
        // That is a property of the fixture, not a weakness in the guards: the live hour that
        // prompted the design had a route survive depth. What pins each rule separately is the
        // test below and the Agrees tests under it.
        Assert.Empty(Arbitrage.Routes(Standard(), Index()));
        Assert.Empty(Ungated(Standard(), deep: true));
    }

    [Fact]
    public void TheSecondSourceRejectsEveryLoopInTheseHours()
    {
        // THE SECOND SOURCE, PINNED WITHOUT LEANING ON DEPTH. Each loop is rejected for one of
        // two reasons, and the test names which applies to which rather than averaging them.
        //
        // BEYOND ARGUMENT. A loop passes Agrees on all three legs only if the index corroborates
        // each, and the index is ONE number per currency - so the three demands compose:
        // i_p/i_e ~ straight, i_p/i_m ~ first, i_m/i_e ~ second, and the last two multiply into
        // the first. A gain above (1+T)^3 - 1 cannot satisfy all three at once whatever the
        // index says. Five of these six are an order of magnitude past that.
        //
        // AND THE ONE THAT IS NOT. The sixth gains +33%, which is inside the band - arithmetic
        // does not touch it, and the REAL index rejecting it is the only thing that does. That
        // row is the one place these fixtures exercise disagreement end to end, so it is
        // asserted rather than waved through with the others.
        ExchangePairs pairs = Standard();
        IReadOnlyDictionary<string, ScoutEntry> index = Index();
        double most = Math.Pow(1 + Arbitrage.Tolerance, 3) - 1;

        var argued = 0;
        foreach ((string path, string middle, double gain) in Ungated(pairs, deep: false))
        {
            if (gain > most)
            {
                continue;
            }

            argued++;
            Assert.False(
                Arbitrage.Agrees(pairs, index, path, ExchangeFeed.Exalted)
                && Arbitrage.Agrees(pairs, index, path, middle)
                && Arbitrage.Agrees(pairs, index, middle, ExchangeFeed.Exalted),
                $"the index corroborated all three legs of a +{gain * 100:N0}% loop");
        }

        // If this ever reaches zero the assertion above has stopped running, and the test would
        // go on passing while proving only the arithmetic half.
        Assert.True(argued > 0, "no loop was inside the band, so disagreement was never exercised");
    }

    [Fact]
    public void ASurvivingRouteCannotBeAbsurd()
    {
        // NOT A NUMBER SOMEBODY PICKED. Every leg has to sit within Tolerance of what the index
        // implies, and the index is self-consistent, so the most a loop can gain is what three
        // legs of slack multiply to: (1+T) on each of the two routed legs and (1+T) again on the
        // straight leg being understated. At a quarter that is 1.25 * 1.25 * 1.25 - 1, a little
        // over 95 percent, and the four-figure fantasies miss it by an order of magnitude.
        //
        // Asserted as arithmetic rather than over a result list, because the result list is
        // empty on this data and a foreach over nothing proves nothing.
        double most = Math.Pow(1 + Arbitrage.Tolerance, 3) - 1;

        Assert.InRange(most, Arbitrage.Worthwhile, 1.0);

        foreach (Route route in Arbitrage.Routes(Standard(), Index()))
        {
            Assert.InRange(route.Gain, Arbitrage.Worthwhile, most);
            Assert.True(route.Carries >= Arbitrage.Deep);
            Assert.NotEqual(ExchangeFeed.Exalted, route.Through);
            Assert.NotEqual(route.Path, route.Through);
        }
    }

    [Fact]
    public void RoutesComeBestFirst()
    {
        // Vacuous on today's fixtures, on purpose: it is the ordering invariant, and the day a
        // league does carry a real loop it is what stops the page burying it under a smaller one.
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
        var route = new Route("x", "X", "m", ExchangeFeed.Exalted, Direct: 100, Routed: 125, Carries: 500);

        Assert.Equal(0.25, route.Gain, 8);
        Assert.Equal(0, new Route("x", "X", "m", ExchangeFeed.Exalted, 0, 125, 500).Gain);
    }
}
