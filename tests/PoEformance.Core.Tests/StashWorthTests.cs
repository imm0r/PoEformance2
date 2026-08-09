using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the things in a stash are worth, given a book of prices.
/// </summary>
/// <remarks>
/// The two keys are the whole subject. Anything fungible is found by its ART - a handle both
/// sides carry, in any client language - and a unique by the NAME resolved from its
/// ItemVisualIdentity id. What is checked here is that each is asked the right question, and
/// that the totals say how much of the pile they could not answer for.
/// </remarks>
public class StashWorthTests
{
    private const string Exchange = """
        {"core":{"rates":{"exalted":500}},
         "items":[{"id":"exalted","image":"/gen/image/x/CurrencyAddModToRare.png"}],
         "lines":[{"id":"exalted","primaryValue":0.002,"volumePrimaryValue":50}]}
        """;

    private const string Uniques = """
        {"core":{"rates":{"exalted":500}},
         "lines":[{"name":"Astramentis","primaryValue":2,"listingCount":900},
                  {"name":"Stellar Amulet","primaryValue":40,"listingCount":900}]}
        """;

    private static PriceBook Book()
    {
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Exchange);
        book.Add(PriceKind.Listed, Uniques);
        return book;
    }

    private static InspectedItem Item(
        string art = "", string unique = "", string @base = "", int stack = 0, ulong entity = 1)
        => new(entity, "Metadata/Items/Thing", @base, unique, unique.Length > 0 ? "SomeIvi" : "", 0, null,
            stack, 0, art, [], []);

    [Fact]
    public void CURRENCYIsPricedByItsPictureAndByHowManyAreInTheStack()
    {
        // The join is the file name of the picture, which the item carries as a path and the
        // price server as a URL. No name table, and it works on a client in any language.
        PriceBook book = Book();
        InspectedItem stack = Item(art: "Art/2DItems/Currency/CurrencyAddModToRare.dds", stack: 17);

        Assert.Equal(1.0, book.Unit(stack)!.Value, 3);
        Assert.Equal(17.0, book.Of(stack)!.Value, 3);
    }

    [Fact]
    public void ANDSomethingThatDoesNotStackIsWorthOneOfItself()
    {
        // Stack is 0 for most things - the component is simply absent - and multiplying by it
        // would price every piece of gear in the game at nothing.
        PriceBook book = Book();

        Assert.Equal(1.0, book.Of(Item(art: "Art/2DItems/Currency/CurrencyAddModToRare.dds"))!.Value, 3);
    }

    [Fact]
    public void AUNIQUEIsPricedByTheNameItsIdResolvedTo()
    {
        // 2 Divine at 500 Exalted to the Divine. Uniques have no art handle - every Astramentis
        // draws the same picture as its base - so the name is all there is.
        PriceBook book = Book();

        Assert.Equal(1000.0, book.Of(Item(unique: "Astramentis", @base: "Stellar Amulet"))!.Value, 3);
    }

    [Fact]
    public void BUTABaseTypeIsNeverPricedAsTheListedLineThatShareItsName()
    {
        // "Stellar Amulet" is in the book as a listed line here. An ordinary amulet asked for
        // by its base name would come back carrying that line's price - which is the one
        // failure worth designing against, because an item with a wrong price is believed.
        PriceBook book = Book();

        Assert.Null(book.Unit(Item(@base: "Stellar Amulet")));
        Assert.Null(book.Of(Item(@base: "Stellar Amulet")));
    }

    [Fact]
    public void ATOTALSaysHowMuchOfThePileItCouldNotAnswerFor()
    {
        PriceBook book = Book();

        StashSlot[] items =
        [
            new(new StashedItem(1, 0, 0, 1, 1), Item(art: "Art/2DItems/Currency/CurrencyAddModToRare.dds", stack: 3, entity: 1)),
            new(new StashedItem(2, 1, 0, 1, 1), Item(unique: "Astramentis", entity: 2)),
            new(new StashedItem(3, 2, 0, 1, 1), Item(@base: "Iron Ring", entity: 3)),
            new(new StashedItem(4, 3, 0, 1, 1), Item(@base: "Iron Ring", entity: 4)),
        ];

        Valued valued = book.Across(items);

        Assert.Equal(1003.0, valued.Exalted, 3);
        Assert.Equal(2, valued.Priced);
        Assert.Equal(2, valued.Unpriced);
        Assert.Equal(4, valued.Items);

        // And the same across pages, which is what the tab list adds up.
        StashPage page = new(1, InventoryKind.Stash, "Stash tab 1", 12, 12, items);
        Valued pages = book.Across([page, page]);

        Assert.Equal(2006.0, pages.Exalted, 3);
        Assert.Equal(4, pages.Priced);
        Assert.Equal(4, pages.Unpriced);
    }

    [Fact]
    public void ANEMPTYBookPricesNothingRatherThanEverythingAtZero()
    {
        var nothing = new PriceBook();

        Assert.Null(nothing.Of(Item(art: "Art/2DItems/Currency/CurrencyAddModToRare.dds", stack: 3)));
        Assert.Equal(0, nothing.Across([new StashSlot(new StashedItem(1, 0, 0, 1, 1), Item())]).Priced);
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.07, "0.07")]
    [InlineData(3.42, "3.4")]
    [InlineData(41.6, "42")]
    [InlineData(3_841.0, "3.8k")]
    [InlineData(4_300_000.0, "4.3M")]
    public void ANAmountIsShortEnoughToSitInAStashCell(double exalted, string expected)
    {
        // Fewer digits the larger it gets: nobody acts on the difference between 3841 and 3862
        // Exalted, and below one Exalted the digits ARE the answer.
        Assert.Equal(expected, StashWorth.Money(exalted));
    }
}
