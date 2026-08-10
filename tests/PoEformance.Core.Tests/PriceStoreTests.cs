using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Keeping prices current for whichever league is being played.
/// </summary>
/// <remarks>
/// The asking is handed in, so none of this touches a network. What it covers is the part that
/// decides WHEN somebody else's server is asked and what is done with a bad answer - which is
/// where the damage is: a refresh that quietly replaces good prices with none looks like the
/// feature breaking, and prices carried over from another league look exactly as trustworthy as
/// the right ones.
/// </remarks>
public class PriceStoreTests
{
    private const string Exchange = """
        {"core":{"rates":{"exalted":500}},
         "items":[{"id":"exalted","image":"/gen/image/x/CurrencyAddModToRare.png"}],
         "lines":[{"id":"exalted","primaryValue":0.002,"volumePrimaryValue":50}]}
        """;

    private static string Somewhere() => Path.Combine(Path.GetTempPath(), $"prices-{Guid.NewGuid():N}.json");

    /// <summary>Waits for the store's background fetch to finish.</summary>
    /// <remarks>
    /// Generous ceiling and an assertion, for the reason the art store's twin learned the
    /// hard way: a three-second budget that passes on this machine is not a three-second
    /// budget on a shared CI runner, and a SILENT timeout makes the test fail later on
    /// whatever the unfinished work did not produce - which reads as a bug in the code under
    /// test rather than as "it was not done yet". The ceiling costs nothing while the work
    /// finishes, because this polls.
    /// </remarks>
    private static void Settle(PriceStore store)
    {
        for (int i = 0; i < 3_000 && store.Busy; i++)
        {
            Thread.Sleep(10);
        }

        Assert.False(store.Busy, "the store never settled - still busy");
    }

    [Fact]
    public void PRICESAreFetchedForTheLeagueBeingPlayed()
    {
        string file = Somewhere();
        var asked = new List<string>();

        try
        {
            using var store = new PriceStore(file, (kind, query, token) =>
            {
                asked.Add($"{kind}|{query}");
                return Task.FromResult<string?>(kind == "exchange" ? Exchange : null);
            })
            {
                Enabled = true,
            };

            store.Playing("HC Runes of Aldur");
            Settle(store);

            Assert.True(store.Book.Ready);
            Assert.Equal(500.0, store.Book.Rate);
            Assert.Equal(1.0, store.Book.Worth("Art/2DItems/Currency/CurrencyAddModToRare.dds")!.Value, 2);

            // Spaces as plus, the way the site spells them - and the hardcore prefix kept,
            // because that is a different economy and not a decoration.
            Assert.All(asked, one => Assert.Contains("HC+Runes+of+Aldur", one, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void NOTHINGIsAskedUntilSomebodyTurnsItOn()
    {
        // Prices only exist on somebody's server, so this is the one part of the feature that
        // has to talk to the outside world - which makes it a decision rather than a surprise.
        var asked = 0;
        using var store = new PriceStore(Somewhere(), (kind, query, token) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<string?>(Exchange);
        });

        store.Playing("Standard");
        store.Refresh();
        Settle(store);

        Assert.Equal(0, asked);
        Assert.False(store.Book.Ready);
    }

    [Fact]
    public void ABADAnswerLeavesTheGoodPricesAlone()
    {
        // "The numbers vanished" is a worse failure than "the numbers are twenty minutes old".
        string file = Somewhere();
        var giveNothing = false;

        try
        {
            using var store = new PriceStore(file, (kind, query, token) =>
                Task.FromResult<string?>(giveNothing ? null : kind == "exchange" ? Exchange : null))
            {
                Enabled = true,
            };

            store.Playing("Standard");
            Settle(store);
            Assert.True(store.Book.Ready);

            giveNothing = true;
            store.Refresh();
            Settle(store);

            Assert.True(store.Book.Ready);
            Assert.Equal(500.0, store.Book.Rate);
            Assert.Contains("previous prices", store.Status, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ANDONEKindOfThingFailingIsNotTheWholeRefresh()
    {
        // A slug this does not know, or one request that times out, must not cost the other
        // twenty - which is what makes it safe to ask for kinds that may not exist.
        string file = Somewhere();

        try
        {
            using var store = new PriceStore(file, (kind, query, token) =>
                query.Contains("Currency", StringComparison.Ordinal)
                    ? Task.FromResult<string?>(Exchange)
                    : throw new HttpRequestException("no such thing"))
            {
                Enabled = true,
            };

            store.Playing("Standard");
            Settle(store);

            Assert.True(store.Book.Ready);
            Assert.Contains("unanswered", store.Status, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CHANGINGLeagueThrowsTheOldPricesAwayRatherThanAgeingThem()
    {
        // Prices from another league are not old, they are a different economy - and shown next
        // to the right ones they look exactly as trustworthy.
        string file = Somewhere();
        var answer = true;

        try
        {
            using var store = new PriceStore(file, (kind, query, token) =>
                Task.FromResult<string?>(answer && kind == "exchange" ? Exchange : null))
            {
                Enabled = true,
            };

            store.Playing("Standard");
            Settle(store);
            Assert.True(store.Book.Ready);

            answer = false;
            store.Playing("HC Runes of Aldur");
            Settle(store);

            Assert.False(store.Book.Ready);
            Assert.Equal("HC Runes of Aldur", store.League);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void THEBookIsPickedUpAgainNextTimeButOnlyForTheSameLeague()
    {
        string file = Somewhere();

        try
        {
            using (var first = new PriceStore(file, (kind, query, token) =>
                       Task.FromResult<string?>(kind == "exchange" ? Exchange : null)) { Enabled = true })
            {
                first.Playing("Standard");
                Settle(first);
                Assert.True(first.Book.Ready);
            }

            using var next = new PriceStore(file, (kind, query, token) => Task.FromResult<string?>(null));

            Assert.True(next.Reopen("Standard"));
            Assert.Equal(500.0, next.Book.Rate);
            Assert.Equal(1.0, next.Book.Worth("Art/2DItems/Currency/CurrencyAddModToRare.dds")!.Value, 2);

            // And a book for somewhere else is not picked up at all.
            using var elsewhere = new PriceStore(file, (kind, query, token) => Task.FromResult<string?>(null));
            Assert.False(elsewhere.Reopen("HC Runes of Aldur"));
            Assert.False(elsewhere.Book.Ready);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SWITCHINGPricesOnOpensTheBookTheLastSessionLeftRatherThanAskingAgain()
    {
        string file = Somewhere();

        try
        {
            using (var first = new PriceStore(file, (kind, query, token) =>
                       Task.FromResult<string?>(kind == "exchange" ? Exchange : null)) { Enabled = true })
            {
                first.Playing("Standard");
                Settle(first);
                Assert.True(first.Book.Ready);
            }

            var asked = 0;
            using var next = new PriceStore(file, (kind, query, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<string?>(kind == "exchange" ? Exchange : null);
            });

            // Switched off, every tick: the league is noted so a window can name it, and
            // nothing else happens at all.
            next.Watching("Standard");
            next.Watching("Standard");
            Settle(next);

            Assert.False(next.Book.Ready);
            Assert.Equal(0, asked);
            Assert.Equal("Standard", next.League);

            next.Enabled = true;
            next.Watching("Standard");
            Settle(next);

            // The saved book was still fresh, so it is used and NOTHING is asked for. Opened
            // after the refresh decision instead, every start of the tool would refetch a book
            // it already had - twenty requests to be told what is on disk.
            Assert.True(next.Book.Ready);
            Assert.Equal(0, asked);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ANDAWatchedLeagueChangeDoesNotOpenTheOtherLeaguesBook()
    {
        string file = Somewhere();

        try
        {
            using (var first = new PriceStore(file, (kind, query, token) =>
                       Task.FromResult<string?>(kind == "exchange" ? Exchange : null)) { Enabled = true })
            {
                first.Playing("Standard");
                Settle(first);
            }

            var asked = 0;
            using var next = new PriceStore(file, (kind, query, token) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<string?>(null);
            })
            {
                Enabled = true,
            };

            next.Watching("HC Runes of Aldur");
            Settle(next);

            // The file on disk is for Standard, and Standard's prices are not old here - they
            // are a different economy. So nothing is picked up and the answer is asked for.
            Assert.False(next.Book.Ready);
            Assert.True(asked > 0);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("Standard", "Standard")]
    [InlineData("HC Runes of Aldur", "HC+Runes+of+Aldur")]
    [InlineData("  Rise of the Abyssal  ", "Rise+of+the+Abyssal")]
    public void ALEAGUEIsSpeltInAQueryTheWayTheSiteSpellsIt(string league, string expected)
        => Assert.Equal(expected, PriceStore.Escaped(league));

    [Fact]
    public void ANDNothingIsAskedForALeagueNobodyIsIn()
    {
        var asked = 0;
        using var store = new PriceStore(Somewhere(), (kind, query, token) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<string?>(Exchange);
        })
        {
            Enabled = true,
        };

        store.Playing(null);
        store.Playing("");
        store.Playing("   ");
        store.Refresh();
        Settle(store);

        Assert.Equal(0, asked);
    }
}
