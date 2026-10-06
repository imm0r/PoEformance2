using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The book's name door for fungible lines: what a recipe panel asks with, having no item.
/// </summary>
public class PriceSpellingTests
{
    private const string Answer = """
        {
          "core": { "rates": { "exalted": 500 } },
          "items": [
            { "id": "regal", "name": "Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
            { "id": "perfect-regal-orb", "name": "Perfect Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
            { "id": "nameless", "name": "Nameless Thing" },
            { "id": "thin", "name": "Thin Thing", "image": "/gen/image/x/Thin.png" }
          ],
          "lines": [
            { "id": "regal", "primaryValue": 0.001, "volumePrimaryValue": 50 },
            { "id": "perfect-regal-orb", "primaryValue": 1.0, "volumePrimaryValue": 50 },
            { "id": "nameless", "primaryValue": 0.5, "volumePrimaryValue": 50 },
            { "id": "thin", "primaryValue": 0.5, "volumePrimaryValue": 0.01 }
          ]
        }
        """;

    [Fact]
    public void ANameFindsItsOwnTier_WhereThePictureIsShared()
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, Answer) > 0);

        Assert.Equal(0.5, book.Spelt("Regal Orb")!.Value, 6);
        Assert.Equal(500.0, book.Spelt("Perfect Regal Orb")!.Value, 6);
        Assert.Equal(500.0, book.Spelt("perfect regal ORB")!.Value, 6);

        // A line with no picture in the table is still spelt.
        Assert.Equal(250.0, book.Spelt("Nameless Thing")!.Value, 6);

        // The gate applies here as everywhere: a thin line is not a price.
        Assert.Null(book.Spelt("Thin Thing"));
        Assert.Null(book.Spelt("Exalted Orb"));
        Assert.Null(book.Spelt(""));
        Assert.Null(book.Spelt(null));
    }

    [Fact]
    public void TheSpellingsSurviveTheFile()
    {
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Answer);

        PriceBook.Kept? kept = PriceBook.Reopen(book.Saved("Standard"), "Standard");

        Assert.NotNull(kept);
        Assert.Equal(500.0, kept.Value.Book.Spelt("Perfect Regal Orb")!.Value, 6);
        Assert.Equal(250.0, kept.Value.Book.Spelt("Nameless Thing")!.Value, 6);
    }

    [Fact]
    public void AFileWrittenBeforeTheSpellingsStillOpens()
    {
        // The older shape, with no "spelt" object: a book, with nothing to spell.
        const string older = """{"league":"Standard","when":1700000000,"rate":500,"art":{"currencyrerollrare":100},"name":{}}""";

        PriceBook.Kept? kept = PriceBook.Reopen(older, "Standard");

        Assert.NotNull(kept);
        Assert.Equal(100.0, kept.Value.Book.Worth("Art/2DItems/Currency/CurrencyRerollRare.dds")!.Value, 6);
        Assert.Null(kept.Value.Book.Spelt("Chaos Orb"));
    }

    [Fact]
    public void AUniquesNameIsNotAFungibleSpelling()
    {
        // The listed half keeps its own door: a base type spelled like a listed line must not
        // pick that line's price up through this one.
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Answer);
        book.Add(PriceKind.Listed, """
            { "core": { "rates": { "exalted": 500 } },
              "lines": [ { "name": "Astramentis", "primaryValue": 2, "listingCount": 500 } ] }
            """);

        Assert.Equal(1000.0, book.Worth(null, "Astramentis")!.Value, 6);
        Assert.Null(book.Spelt("Astramentis"));
    }
}
