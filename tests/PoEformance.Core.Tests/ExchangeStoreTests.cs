using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The thing that keeps the exchange current, with the network and the clock handed in.
/// </summary>
/// <remarks>
/// What is worth pinning here is not the parsing - that is <see cref="ExchangePairsTests"/> - but
/// the ASKING: how many hours get fetched, how many get fetched AGAIN, and what happens to the
/// last good answer when the feed stops replying. All three are the difference between a polite
/// tool and one that hammers somebody's CDN or blanks a stash on a dropped packet.
/// </remarks>
public sealed class ExchangeStoreTests
{
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

    /// <summary>A feed that answers every hour with real Standard data, and counts the asking.</summary>
    private sealed class Feed
    {
        public List<long> Asked { get; } = [];

        public bool Silent { get; set; }

        public Task<string?> Ask(long hour, CancellationToken cancelling)
        {
            Asked.Add(hour);

            // Two real hours, alternating, so a walk of six hours keeps finding markets rather
            // than stopping on the first repeat.
            return Task.FromResult<string?>(Silent ? null : Hour((Asked.Count % 2) + 1));
        }
    }

    private static async Task<(ExchangeStore Store, Feed Feed)> Ready(DateTimeOffset now)
    {
        var feed = new Feed();
        var store = new ExchangeStore(feed.Ask, () => now) { Enabled = true };
        store.Playing("Standard");
        await Settled(store);
        return (store, feed);
    }

    /// <summary>Waits for the background read, without a sleep that would be a race either way.</summary>
    private static async Task Settled(ExchangeStore store)
    {
        for (var spin = 0; spin < 200 && store.Busy; spin++)
        {
            await Task.Delay(10);
        }

        Assert.False(store.Busy, "the store never finished reading");
    }

    [Fact]
    public async Task ReadingALeagueBuildsAGraphOfIt()
    {
        (ExchangeStore store, _) = await Ready(DateTimeOffset.UnixEpoch.AddSeconds(1787810000));

        Assert.True(store.Pairs.Count > 50);
        Assert.Equal("Standard", store.Pairs.League);
        Assert.True(store.Pairs.Worth(ExchangeFeed.Divine).Known);
        Assert.Contains("Standard", store.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyCompletedHoursAreEverAskedFor()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (_, Feed feed) = await Ready(now);

        Assert.NotEmpty(feed.Asked);
        foreach (long hour in feed.Asked)
        {
            // The hour still being filled always answers empty, so asking for it is a wasted
            // request every single time.
            Assert.True(hour < now.ToUnixTimeSeconds());
            Assert.Equal(0, hour % 3600);
        }
    }

    [Fact]
    public async Task TheWalkStopsRatherThanAlwaysTakingSixHours()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (_, Feed feed) = await Ready(now);

        Assert.InRange(feed.Asked.Count, 1, ExchangeStore.MostHours);
    }

    [Fact]
    public async Task AnHourAlreadyHeldIsNeverFetchedTwice()
    {
        // THE POINT OF THE WHOLE CACHE. Completed hours never change, so a refresh should cost
        // one request - the newest hour - and not six. A tool that re-downloads a megabyte of
        // immutable data every hour is one somebody eventually blocks.
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (ExchangeStore store, Feed feed) = await Ready(now);

        int first = feed.Asked.Count;
        feed.Asked.Clear();

        store.Refresh();
        await Settled(store);

        Assert.True(first > 0);
        Assert.Empty(feed.Asked);
        Assert.Contains("cached", store.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASilentFeedLeavesTheLastAnswerStanding()
    {
        // A market that cannot be asked about has not become worthless, and a dropped packet
        // must not blank somebody's stash.
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (ExchangeStore store, Feed feed) = await Ready(now);

        int had = store.Pairs.Count;
        Assert.True(had > 0);

        feed.Silent = true;
        store.Refresh();
        await Settled(store);

        Assert.Equal(had, store.Pairs.Count);
    }

    [Fact]
    public async Task AnotherLeagueThrowsTheGraphAwayButKeepsTheHours()
    {
        // A graph for another league is WRONG rather than stale - different economy, different
        // numbers. The digests are not: every league shares one file, so what was fetched still
        // answers, and switching leagues must not re-download it.
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (ExchangeStore store, Feed feed) = await Ready(now);

        feed.Asked.Clear();
        store.Playing("Runes of Aldur");
        await Settled(store);

        Assert.Equal("Runes of Aldur", store.Pairs.League);
        Assert.Empty(feed.Asked);
    }

    [Fact]
    public async Task NothingIsAskedWhileTheFeedIsSwitchedOff()
    {
        var feed = new Feed();
        var store = new ExchangeStore(feed.Ask, () => DateTimeOffset.UnixEpoch.AddSeconds(1787810000));

        store.Playing("Standard");
        await Settled(store);

        Assert.Empty(feed.Asked);
        Assert.Equal(0, store.Pairs.Count);
    }

    [Fact]
    public async Task AGraphGoesStaleOnceTheHourHasTurned()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1787810000);
        (ExchangeStore store, _) = await Ready(now);

        Assert.False(store.Old);

        var later = new ExchangeStore(
            (_, _) => Task.FromResult<string?>(null),
            () => now + ExchangeStore.GoesStale + TimeSpan.FromMinutes(1));

        // Nothing read at all is the other way to be stale, and the more important one: it is
        // what makes the first Playing call actually fetch.
        Assert.True(later.Old);
    }

    [Fact]
    public async Task TwoRefreshesAtOnceOnlyRunOne()
    {
        var feed = new Feed();
        var store = new ExchangeStore(feed.Ask, () => DateTimeOffset.UnixEpoch.AddSeconds(1787810000))
        {
            Enabled = true,
        };

        store.Playing("Standard");
        store.Refresh();
        store.Refresh();
        await Settled(store);

        // The busy flag is the guard; without it a league change during a read would have two
        // walks writing one graph.
        Assert.True(feed.Asked.Count <= ExchangeStore.MostHours);
    }
}
