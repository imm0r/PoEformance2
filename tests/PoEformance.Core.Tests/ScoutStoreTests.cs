using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The thing that keeps the index current, with the network and the clock handed in.
/// </summary>
/// <remarks>
/// The parsing is <see cref="ScoutCatalogTests"/>. What is pinned here is the ASKING - which is
/// now seventeen requests rather than one, so what a partial answer does matters - and one
/// difference from the exchange store beside it that is easy to get wrong in the other direction:
/// a catalogue belongs to ONE league, so a league change has to throw it away rather than keep it.
/// </remarks>
public sealed class ScoutStoreTests
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

        throw new FileNotFoundException($"capture {name} not found");
    }

    private static string Catalogue() => Captured("scout-currency-standard.json");

    private static string Categories() => Captured("scout-categories-standard.json");

    /// <summary>A page in the index's shape, for the categories no capture was taken of.</summary>
    private static string Page(params string[] paths) =>
        $$"""
          {"CurrentPage":1,"Pages":1,"Total":{{paths.Length}},"Items":[
          {{string.Join(
              ",",
              paths.Select(path =>
                  $$"""
                    {"BaseItemTypeId":"{{path}}","Text":"{{path}}","CurrentPrice":3,"PriceLogs":[
                    {"Price":2,"Time":"2026-08-24T00:00:00Z","Quantity":900},
                    {"Price":3,"Time":"2026-08-25T00:00:00Z","Quantity":900}]}
                    """))}}
          ]}
          """;

    /// <summary>Which category an address is asking for.</summary>
    private static string Category(string address)
    {
        int at = address.IndexOf("category=", StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        string rest = address[(at + "category=".Length)..];
        int ends = rest.IndexOf('&');
        return ends < 0 ? rest : rest[..ends];
    }

    /// <summary>An index that answers with real captures, and counts the asking.</summary>
    private sealed class Feed
    {
        public List<string> Asked { get; } = [];

        /// <summary>The categories asked for, in order, without the addresses around them.</summary>
        public List<string> Categories { get; } = [];

        public bool Silent { get; set; }

        /// <summary>Whether the category LIST answers. The pages still can when it does not.</summary>
        public bool ListsCategories { get; set; } = true;

        /// <summary>Categories that fail, to stand for a request that did not come back.</summary>
        public HashSet<string> Mute { get; } = new(StringComparer.Ordinal);

        public Task<string?> Ask(string address, CancellationToken cancelling)
        {
            Asked.Add(address);

            if (Silent)
            {
                return Task.FromResult<string?>(null);
            }

            if (address.Contains("/Items/Categories", StringComparison.Ordinal))
            {
                return Task.FromResult(ListsCategories ? ScoutStoreTests.Categories() : null);
            }

            string category = Category(address);
            Categories.Add(category);

            if (Mute.Contains(category))
            {
                return Task.FromResult<string?>(null);
            }

            // Only the "currency" category was captured. The rest answer with a page in the same
            // shape carrying one invented path each, which is what makes a merge visible: an
            // index holding both means two categories really were read into one answer.
            return Task.FromResult<string?>(category switch
            {
                "currency" => Catalogue(),
                "runes" => Page("Metadata/Items/Rune/Test1"),
                "ritual" => Page("Metadata/Items/Omen/Test2"),
                _ => Page(),
            });
        }
    }

    /// <summary>Waits for the background read, without a sleep that would be a race either way.</summary>
    private static async Task Settled(ScoutStore store)
    {
        for (var spin = 0; spin < 400 && store.Busy; spin++)
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

        Assert.True(store.Index.Count > 20);
        Assert.True(store.Index[ExchangeFeed.Divine].Known);
        Assert.Contains("Standard", store.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCategoryTheIndexNamesIsAskedFor()
    {
        // THE POINT OF THE WHOLE ARRANGEMENT. Asking only for the category called "currency" is
        // asking for thirty-eight of the five hundred it knows, and the rows a page sorted by
        // worth actually shows - omens, runes, keys, soul cores - are all in the others.
        (ScoutStore store, Feed feed) = await Ready(Noon);

        // The list first, then one request per category it named.
        Assert.Contains("/Items/Categories", feed.Asked[0], StringComparison.Ordinal);
        Assert.Equal(16, feed.Categories.Count);
        Assert.Contains("currency", feed.Categories);
        Assert.Contains("ritual", feed.Categories);      // "Ritual Omens"
        Assert.Contains("ultimatum", feed.Categories);   // "Soul Cores", which nobody would guess
        Assert.Equal(feed.Categories.Count + 1, feed.Asked.Count);

        // And the answers are MERGED, not last-one-wins: the real capture's Divine and the two
        // invented paths from other categories are all in one index.
        Assert.True(store.Index.ContainsKey(ExchangeFeed.Divine));
        Assert.True(store.Index.ContainsKey("Metadata/Items/Rune/Test1"));
        Assert.True(store.Index.ContainsKey("Metadata/Items/Omen/Test2"));
    }

    [Fact]
    public async Task ASilentCategoryListStillReadsTheOneCategoryThatMatters()
    {
        // A list that does not answer must not cost the arbitrage check. Exalted, Divine and
        // Chaos are all in the one category whose name is certain, so this degrades to the old
        // behaviour rather than to no index at all.
        var feed = new Feed { ListsCategories = false };
        var store = new ScoutStore(feed.Ask, () => Noon) { Enabled = true };

        store.Playing("Standard");
        await Settled(store);

        Assert.Equal([ScoutCatalog.Fallback], feed.Categories);
        Assert.True(store.Index[ExchangeFeed.Divine].Known);
        Assert.True(store.Index[ExchangeFeed.Exalted].Known);
    }

    [Fact]
    public async Task ACategoryThatDidNotAnswerIsSaidRatherThanHidden()
    {
        // A short read is not a wrong read, but it IS fewer trend lines than the page had a
        // minute ago, and somebody looking at a row that lost its line deserves to see why.
        var feed = new Feed();
        feed.Mute.Add("runes");
        feed.Mute.Add("ritual");

        var store = new ScoutStore(feed.Ask, () => Noon) { Enabled = true };
        store.Playing("Standard");
        await Settled(store);

        Assert.Contains("only 14 of 16", store.Status, StringComparison.Ordinal);
        Assert.False(store.Index.ContainsKey("Metadata/Items/Rune/Test1"));
        Assert.True(store.Index.ContainsKey(ExchangeFeed.Divine));
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

        // Same league, same moment: the daily points cannot have moved, so asking again is
        // seventeen requests that can only return what is already held.
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
        Assert.NotEmpty(feed.Asked);
        Assert.All(feed.Asked, address => Assert.Contains("Runes%20of%20Aldur", address, StringComparison.Ordinal));
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

        Assert.NotEmpty(feed.Asked);
    }

    [Fact]
    public async Task TwoRefreshesAtOnceOnlyRunOne()
    {
        // AT ONCE has to mean at once, and that needs a read that is still in flight when the
        // second call arrives. An earlier version of this test just called Refresh twice and
        // asserted one fetch - which passed by luck: the fake answers synchronously, so the
        // first read could finish before the second call, and a refresh after one has finished
        // SHOULD fetch again. It failed the day the machine was a little slower.
        var holding = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var store = new ScoutStore(
            (address, _) =>
            {
                // The category list is the first thing a read asks for, so counting it counts
                // READS rather than requests - which is what "only run one" is about now that
                // one read is seventeen requests.
                if (address.Contains("/Items/Categories", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref started);
                    return holding.Task;
                }

                return Task.FromResult<string?>(Catalogue());
            },
            () => Noon)
        {
            Enabled = true,
        };

        store.Playing("Standard");

        // Wait for the ASK, not for the busy flag. Refresh raises the flag and only then starts
        // the task that does the asking, so a spin on Busy can leave here before the first read
        // has actually reached the feed - which is what made the first attempt at this fix fail.
        for (var spin = 0; spin < 200 && Volatile.Read(ref started) == 0; spin++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Volatile.Read(ref started));
        Assert.True(store.Busy, "the read that has not answered yet should still count as busy");

        store.Refresh();
        store.Refresh();
        Assert.Equal(1, Volatile.Read(ref started));

        holding.SetResult(Categories());
        await Settled(store);

        Assert.Equal(1, Volatile.Read(ref started));
        Assert.NotEmpty(store.Index);
    }
}
