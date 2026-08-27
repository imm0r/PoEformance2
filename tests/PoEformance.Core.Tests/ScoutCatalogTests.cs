using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The aggregated index, read against a real capture of it.
/// </summary>
/// <remarks>
/// The fixture is one unedited answer for Standard, taken the morning these tests were written.
/// Which matters more than usual here: the newest day in it is a PARTIAL one, and telling a
/// partial day from a real one is most of what this class does.
/// </remarks>
public sealed class ScoutCatalogTests
{
    private static string Captured(string name = "scout-currency-standard.json")
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

    private static ScoutEntry Divine()
        => ScoutCatalog.Read(Captured())[ExchangeFeed.Divine];

    [Fact]
    public void ItJoinsOnTheSameKeyAsTheExchangeAndTheGame()
    {
        // THE REASON THIS COSTS NOTHING TO ADD. The index publishes BaseItemTypeId, the exchange
        // names its markets by metadata path, and the game hands this tool the same string for
        // every item it reads - so there is no id table anywhere to fall out of step.
        IReadOnlyDictionary<string, ScoutEntry> book = ScoutCatalog.Read(Captured());

        Assert.True(book.ContainsKey(ExchangeFeed.Divine));
        Assert.True(book.ContainsKey(ExchangeFeed.Exalted));
        Assert.Equal("Divine Orb", book[ExchangeFeed.Divine].Called);
    }

    [Fact]
    public void APriceAndSevenDaysComeBackTogether()
    {
        ScoutEntry divine = Divine();

        Assert.True(divine.Known);
        Assert.Equal(ScoutCatalog.Days, divine.Days.Count);
    }

    [Fact]
    public void DaysAreOldestFirstWhateverOrderTheyArrivedIn()
    {
        // A trend is a DIRECTION, and one read off a list sorted the other way is exactly
        // backwards - every rising currency would show as falling.
        ScoutEntry divine = Divine();

        for (var day = 1; day < divine.Days.Count; day++)
        {
            Assert.True(divine.Days[day].Day > divine.Days[day - 1].Day);
        }
    }

    [Fact]
    public void TodaySoFarIsNotADay()
    {
        // THE TRAP THIS CLASS EXISTS FOR. In the captured hour Divine read 194.7 on 31,903
        // traded, where the six days before it ran 245 to 354 on about 1.2 million each. Taken
        // as a day that is a forty percent crash; it is really nine o'clock in the morning.
        ScoutEntry divine = Divine();

        ScoutDay newest = divine.Days[^1];
        Assert.True(newest.Quantity < divine.Days[^2].Quantity / 10);

        Assert.Equal(divine.Days.Count - 1, divine.Settled.Count);
        Assert.DoesNotContain(newest, divine.Settled);
    }

    [Fact]
    public void TheTrendIsReadOffTheSettledDaysOnly()
    {
        ScoutEntry divine = Divine();

        double? trend = divine.Trend;
        Assert.NotNull(trend);

        IReadOnlyList<ScoutDay> settled = divine.Settled;
        Assert.Equal((settled[^1].Exalted - settled[0].Exalted) / settled[0].Exalted, trend.Value, 8);

        // And it is NOT the crash the partial day would have invented.
        double naive = (divine.Days[^1].Exalted - divine.Days[0].Exalted) / divine.Days[0].Exalted;
        Assert.NotEqual(naive, trend.Value, 3);
    }

    [Fact]
    public void AQuietDayIsStillADay()
    {
        // The threshold is deliberately generous. A genuinely slow day is a real day, and
        // dropping it would shorten every trend by one for no reason.
        var quiet = new ScoutEntry("x", "X", 10,
        [
            new(new DateOnly(2026, 8, 20), 10, 1000),
            new(new DateOnly(2026, 8, 21), 11, 1000),
            new(new DateOnly(2026, 8, 22), 12, 700),
        ]);

        Assert.Equal(3, quiet.Settled.Count);
    }

    [Fact]
    public void ASingleDayIsKeptRatherThanExplainedAway()
    {
        var one = new ScoutEntry("x", "X", 10, [new(new DateOnly(2026, 8, 22), 12, 5)]);

        Assert.Single(one.Settled);
        Assert.Null(one.Trend);
    }

    [Fact]
    public void NothingToCompareIsUnknownRatherThanFlat()
    {
        // "Flat" and "unknown" are different answers, and a sparkline drawn from the second one
        // is a lie about the first.
        var bare = new ScoutEntry("x", "X", 10, []);

        Assert.Null(bare.Trend);
        Assert.Empty(bare.Settled);
    }

    [Fact]
    public void ADayNOBODYTradedOnLeavesAHoleRatherThanAZero()
    {
        // WHAT THE DRAWN WEEK IS SPACED BY, and why it is not spaced by index. The site pads a
        // day with no trades with a null; the parser drops it rather than carrying it as a zero,
        // which would draw the line through the floor. So a currency's days are NOT seven
        // consecutive dates: in this capture 37 of the day slots are null and three of the
        // thirty-six currencies are short one. Chance Shard is the clearest - four points across
        // five calendar days - and spread evenly its two-day step draws as an ordinary one.
        IReadOnlyDictionary<string, ScoutEntry> book = ScoutCatalog.Read(Captured());

        ScoutEntry shard = book["Metadata/Items/Currency/CurrencyUpgradeRandomlyShard"];
        Assert.Equal(4, shard.Days.Count);
        Assert.Equal(5, shard.Days[^1].Day.DayNumber - shard.Days[0].Day.DayNumber + 1);

        // Stated as "it happens" rather than "it happens three times", so regenerating the
        // capture does not fail a test about the shape of the feed.
        Assert.Contains(
            book.Values,
            entry => entry.Days.Count >= 2
                     && entry.Days[^1].Day.DayNumber - entry.Days[0].Day.DayNumber > entry.Days.Count - 1);
    }

    [Fact]
    public void RubbishIsAnEmptyCatalogueRatherThanACrash()
    {
        Assert.Empty(ScoutCatalog.Read("{not json"));
        Assert.Empty(ScoutCatalog.Read("{}"));
        Assert.Empty(ScoutCatalog.Read(null));
    }

    [Fact]
    public void TheAddressNamesTheLeagueAndTheDepth()
    {
        string where = ScoutCatalog.Where("Runes of Aldur");

        Assert.Contains("Runes%20of%20Aldur", where, StringComparison.Ordinal);
        Assert.Contains($"dataPoints={ScoutCatalog.Days}", where, StringComparison.Ordinal);
        Assert.Contains($"category={ScoutCatalog.Fallback}", where, StringComparison.Ordinal);

        // A category with a space or a slash in it would otherwise walk out of the query string.
        Assert.Contains("category=a%2Fb", ScoutCatalog.Where("Standard", "a/b"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCategoryNamesAreReadRatherThanGuessed()
    {
        // WHY THIS IS FETCHED AND NOT A CONST ARRAY. The api ids are not derivable from the
        // labels the site shows: "Ritual Omens" is `ritual`, "Soul Cores" is `ultimatum`,
        // "Reliquary Keys" is `vaultkeys`, "Idols" is `idol` singular. Guessing them while
        // writing this produced `omens`, `soulcores`, `keys` and `idols` - and the index answers
        // an unknown category with 200 and an empty page, so all four looked like categories
        // that simply had nothing in them. A wrong name here is silent, which is the worst kind.
        IReadOnlyList<string> categories = ScoutCatalog.ReadCategories(Captured("scout-categories-standard.json"));

        Assert.Equal(16, categories.Count);
        Assert.Contains("currency", categories);
        Assert.Contains("ritual", categories);
        Assert.Contains("ultimatum", categories);
        Assert.Contains("vaultkeys", categories);
        Assert.Contains("idol", categories);

        // The names that reading the labels would have produced. None of them is real.
        Assert.DoesNotContain("omens", categories);
        Assert.DoesNotContain("soulcores", categories);
        Assert.DoesNotContain("keys", categories);
        Assert.DoesNotContain("idols", categories);

        // The one category whose name is relied on when the list cannot be read has to be in it.
        Assert.Contains(ScoutCatalog.Fallback, categories);
    }

    [Fact]
    public void UniqueCategoriesAreNotCurrencyCategories()
    {
        // The same answer carries both, and the unique half is served by a different endpoint
        // that Where() does not build. Asking Currencies/ByCategory for one would be sixteen
        // more requests returning empty pages.
        const string Both =
            """
            {"UniqueCategories":[{"ItemCategoryId":1,"ApiId":"weapon","Label":"Weapons","Icon":""}],
             "CurrencyCategories":[{"CurrencyCategoryId":21,"ApiId":"currency","Label":"Currency","Icon":""}]}
            """;

        Assert.Equal(["currency"], ScoutCatalog.ReadCategories(Both));
    }

    [Fact]
    public void RubbishCategoriesIsAnEmptyListRatherThanACrash()
    {
        // Which is what makes the store fall back to the one category it can name itself.
        Assert.Empty(ScoutCatalog.ReadCategories("{not json"));
        Assert.Empty(ScoutCatalog.ReadCategories("{}"));
        Assert.Empty(ScoutCatalog.ReadCategories("""{"CurrencyCategories":"nope"}"""));
        Assert.Empty(ScoutCatalog.ReadCategories(null));
    }
}
