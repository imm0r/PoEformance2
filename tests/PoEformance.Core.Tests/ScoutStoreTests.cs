using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The thing that keeps the index current, with the network and the clock handed in.
/// </summary>
/// <remarks>
/// The parsing is <see cref="ScoutCatalogTests"/>. What is pinned here is the ASKING, and one
/// difference from the exchange store beside it that is easy to get wrong in the other direction:
/// a catalogue belongs to ONE league, so a league change has to throw it away rather than keep it.
/// </remarks>
public sealed class ScoutStoreTests
{
    private static string Catalogue()
    {
        const string Name = "scout-currency-standard.json";
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "fixtures", Name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                at = at.Parent;
            }
        }

        throw new FileNotFoundException($"captured catalogue {Name} not found");
    }

    /// <summary>An index that answers with a real Standard capture, and counts the asking.</summary>
    private sealed class Feed
    {
        public List<string> Asked { get; } = [];

        public bool Silent { get; set; }

        public Task<string?> Ask(string league, CancellationToken cancelling)
        {
            Asked.Add(league);
            return Task.FromResult(Silent ? null : Catalogue());
        }
    }

    /// <summary>Waits for the background read, without a sleep that would be a race either way.</summary>
    private static async Task Settled(ScoutStore store)
    {
        for (var spin = 0; spin < 200 && store.Busy; spin++)
        {
            await Task.Delay(10);
        }

        Assert.False(store.Busy, "the store never finished reading");
    }

    private static async Task<(ScoutStore Store, Feed Feed)> Ready(DateTimeOffset now)
    {
        var feed = new Feed();
        var store = new ScoutStore(feed.Ask, () => now) { Enabled = true };
        store.Playing("Standard");
        await Settled(store);
        return (store, feed);
    }

    private static DateTimeOffset Noon => DateTimeOffset.UnixEpoch.AddSeconds(1787810000);

    [Fact]
    public async Task ReadingALeagueFillsTheIndex()
    {
        (ScoutStore store, Feed feed) = await Ready(Noon);

        Assert.Equal(["Standard"], feed.Asked);
        Assert.True(store.Index.Count > 20);
        Assert.True(store.Index[ExchangeFeed.Divine].Known);
        Assert.Contains("Standard", store.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsAskedWhileTheIndexIsSwitchedOff()
    {
        var feed = new Feed();
        var store = new ScoutStore(feed.Ask, () => Noon);

        store.Playing("Standard");
        await Settled(store);

        Assert.Empty(feed.Asked);
        Assert.Empty(store.Index);
    }

    [Fact]
    public async Task AFreshCatalogueIsNotAskedForAgain()
    {
        (ScoutStore store, Feed feed) = await Ready(Noon);

        feed.Asked.Clear();
        store.Playing("Standard");
        await Settled(store);

        // Same league, same half hour: the daily points cannot have moved, so asking again is a
        // request that can only return what is already held.
        Assert.Empty(feed.Asked);
        Assert.False(store.Old);
    }

    [Fact]
    public async Task AnotherLeagueThrowsTheCatalogueAway()
    {
        // THE DIFFERENCE FROM THE EXCHANGE STORE. Its hourly digests carry every league at once,
        // so a league change keeps them. A catalogue is one league's answer and says nothing at
        // all about another's, so keeping it would price a fresh league off Standard's economy.
        (ScoutStore store, Feed feed) = await Ready(Noon);

        Assert.NotEmpty(store.Index);
        feed.Silent = true;
        feed.Asked.Clear();

        store.Playing("Runes of Aldur");
        await Settled(store);

        Assert.Equal("Runes of Aldur", store.League);
        Assert.Equal(["Runes of Aldur"], feed.Asked);
        Assert.Empty(store.Index);
    }

    [Fact]
    public async Task ASilentFeedLeavesTheLastAnswerStanding()
    {
        // Same league, so what is held is still the right league's - a source that cannot be
        // asked has not become wrong. The league change above is the case where it has.
        (ScoutStore store, Feed feed) = await Ready(Noon);

        int had = store.Index.Count;
        Assert.True(had > 0);

        feed.Silent = true;
        store.Refresh();
        await Settled(store);

        Assert.Equal(had, store.Index.Count);
        Assert.Contains("said nothing", store.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingReadAtAllCountsAsStale()
    {
        // Which is what makes the first Playing call actually fetch.
        var fresh = new ScoutStore((_, _) => Task.FromResult<string?>(null), () => Noon);

        Assert.True(fresh.Old);
    }

    [Fact]
    public async Task ACatalogueAgesOut()
    {
        var now = Noon;
        var feed = new Feed();
        var store = new ScoutStore(feed.Ask, () => now) { Enabled = true };

        store.Playing("Standard");
        await Settled(store);
        Assert.False(store.Old);

        now += ScoutStore.GoesStale + TimeSpan.FromMinutes(1);
        Assert.True(store.Old);

        feed.Asked.Clear();
        store.Playing("Standard");
        await Settled(store);

        Assert.Equal(["Standard"], feed.Asked);
    }

    [Fact]
    public async Task TwoRefreshesAtOnceOnlyRunOne()
    {
        var feed = new Feed();
        var store = new ScoutStore(feed.Ask, () => Noon) { Enabled = true };

        store.Playing("Standard");
        store.Refresh();
        store.Refresh();
        await Settled(store);

        Assert.Single(feed.Asked);
    }
}
