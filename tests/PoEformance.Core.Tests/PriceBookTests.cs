using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// What things are worth, read out of poe.ninja's answers.
/// </summary>
/// <remarks>
/// Against REAL captured answers, not invented ones. A price parser tested on JSON written to
/// match it proves only that it matches itself, and the two things most worth pinning here -
/// which lines are believable and which rate converts them - are both things the live API gets
/// subtly wrong in ways invented fixtures would never show.
/// </remarks>
public class PriceBookTests
{
    private static string Captured(string name)
    {
        // Beside the test assembly (copied at build) or in the source tree, so this works from
        // both a normal run and a bare `dotnet test`.
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

        throw new FileNotFoundException($"captured answer {name} not found");
    }

    [Fact]
    public void CURRENCYIsPricedByItsPictureRatherThanItsName()
    {
        // The join that makes this work in any language: the answer names each line's picture,
        // and an item in memory carries the path of the same picture. No name table, nothing to
        // keep current, and it does not care what the client is running in.
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, Captured("ninja-exchange.json")) > 0);

        Assert.True(book.Ready);
        Assert.Equal(581.0, book.Rate);

        // Exalted quotes 0.001721 Divine, and the rate says a Divine is 581 of them - so an
        // Exalted has to come back as one. If this is not 1, the whole book is scaled wrong.
        double? exalted = book.Worth("Art/2DItems/Currency/CurrencyAddModToRare.dds");
        Assert.NotNull(exalted);
        Assert.Equal(1.0, exalted!.Value, 2);

        Assert.Equal(581.0, book.Worth("Art/2DItems/Currency/CurrencyModValues.dds")!.Value, 1);
    }

    [Fact]
    public void ANDALineNobodyTradesIsNotAPrice()
    {
        // Blacksmith's Whetstone quotes off 0.07 Divine of trade - an asking price with no
        // market behind it. Live, in the captured answer.
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Captured("ninja-exchange.json"));

        Assert.Null(book.Worth("Art/2DItems/Currency/CurrencyWeaponQuality.dds"));
        Assert.True(book.Thin > 0);
    }

    [Fact]
    public void APRICEFIXEDUniqueIsDroppedAndARealOneIsKept()
    {
        // THE one that matters. poe.ninja's raw answer carries lines its own website hides, and
        // the captured one holds both kinds: Redbeak - a starter sword - quoted at 4225 Divine
        // off nine listings, next to real lines with thousands.
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Listed, Captured("ninja-item.json")) > 0);

        Assert.Null(book.Worth(null, "Redbeak"));
        Assert.Null(book.Worth(null, "Winter's Bite"));
        Assert.Null(book.Worth(null, "The Dancing Dervish"));

        // 3.0 Divine off 5504 listings is a market. At 371.7 a Divine, that is ~1115 Exalted.
        double? ordained = book.Worth(null, "The Ordained");
        Assert.NotNull(ordained);
        Assert.Equal(3.0 * 371.7, ordained!.Value, 1);

        Assert.Equal(3, book.Thin);
    }

    [Fact]
    public void ANDATHINLineIsOnlyThinWhenTheAnswerSaysSo()
    {
        // Both gates fail OPEN. A schema change that renames the field should cost the gate,
        // not every price in the book - which is the difference between "prices look generous
        // today" and "the feature is empty".
        var book = new PriceBook();
        book.Add(PriceKind.Listed, """
            {"core":{"rates":{"exalted":100}},
             "lines":[{"name":"No Count Given","primaryValue":2}]}
            """);

        Assert.Equal(200.0, book.Worth(null, "No Count Given"));
        Assert.Equal(0, book.Thin);
    }

    [Fact]
    public void THERATEIsNotTakenFromAnAnswerThatHadNoPricesInIt()
    {
        // Live on Standard: the exchange answer says 581 and the unique-weapons answer, which
        // has ZERO lines, says 932.9. Whichever spoke last winning is a sixty per cent error in
        // every converted price, on exactly the leagues where one family has no data.
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Captured("ninja-exchange.json"));
        Assert.Equal(581.0, book.Rate);

        Assert.Equal(0, book.Add(PriceKind.Listed, """
            {"core":{"rates":{"exalted":932.9}},"lines":[]}
            """));

        Assert.Equal(581.0, book.Rate);
        Assert.Equal(1.0, book.Worth("Art/2DItems/Currency/CurrencyAddModToRare.dds")!.Value, 2);
    }

    [Fact]
    public void ANDNoRateMeansNoPricesRatherThanUnconvertedOnes()
    {
        // A Divine figure handed out as though it were Exalted is wrong by a factor of some
        // hundreds, and looks entirely plausible.
        var book = new PriceBook();

        Assert.Equal(0, book.Add(PriceKind.Listed, """
            {"lines":[{"name":"Thing","primaryValue":5,"listingCount":9999}]}
            """));

        Assert.False(book.Ready);
        Assert.Null(book.Worth(null, "Thing"));
    }

    [Fact]
    public void THECheapestRollOfAUniqueIsTheOneItIsWorth()
    {
        // The same unique appears once per roll. What somebody is actually offered is the
        // cheapest of them, so quoting the dearest overstates a stash by however wide the
        // spread happens to be.
        var book = new PriceBook();
        book.Add(PriceKind.Listed, """
            {"core":{"rates":{"exalted":100}},
             "lines":[{"name":"Two Rolls","primaryValue":9,"listingCount":900},
                      {"name":"Two Rolls","primaryValue":2,"listingCount":900}]}
            """);

        Assert.Equal(200.0, book.Worth(null, "Two Rolls"));
    }

    [Theory]
    [InlineData("Art/2DItems/Currency/CurrencyAddModToRare.dds", "currencyaddmodtorare")]
    [InlineData(@"Art\2DItems\Currency\CurrencyAddModToRare.dds", "currencyaddmodtorare")]
    [InlineData("/gen/image/WzI1LDE0/ad7c366789/CurrencyAddModToRare.png", "currencyaddmodtorare")]
    [InlineData("https://web.poecdn.com/gen/image/x/Redbeak.png?v=2", "redbeak")]
    [InlineData("CurrencyAddModToRare", "currencyaddmodtorare")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void APICTUREIsNamedTheSameWhicheverSideItCameFrom(string? path, string expected)
        => Assert.Equal(expected, PriceBook.ArtOf(path));

    [Fact]
    public void ANANSWERThatIsNotOneIsRefusedRatherThanThrown()
    {
        var book = new PriceBook();

        Assert.Equal(0, book.Add(PriceKind.Exchange, null));
        Assert.Equal(0, book.Add(PriceKind.Exchange, ""));
        Assert.Equal(0, book.Add(PriceKind.Exchange, "<html>not json</html>"));
        Assert.Equal(0, book.Add(PriceKind.Exchange, "[1,2,3]"));
        Assert.Equal(0, book.Add(PriceKind.Listed, """{"lines":"not an array"}"""));
        Assert.False(book.Ready);
        Assert.Null(book.Worth("Art/Thing.dds", "Thing"));
    }
}
