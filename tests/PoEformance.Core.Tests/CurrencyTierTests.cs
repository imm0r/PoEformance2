using System.Text;
using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// The three tiers of an orb, which share one picture and do not share a price.
/// </summary>
/// <remarks>
/// THE SHAPE THIS PINS IS REAL AND WAS MEASURED, not invented to make a point. In PoE2 five
/// currency families come in a plain, a Greater and a Perfect variant - Transmutation,
/// Augmentation, Regal, Exalted, Chaos - and all three of each draw ONE art file, with the game
/// painting the tier as an overlay. The metadata paths differ by a trailing digit
/// (CurrencyUpgradeToMagic, ...2, ...3) and the shipped name table resolves all fifteen
/// correctly, so the item side has always been able to tell them apart.
///
/// The PRICE side could not, and the failure needed the price source to be incomplete before it
/// showed. Checked against a live poe.ninja answer: it carries all three tiers of Exalted, Chaos
/// and Regal - so those pictures are visibly shared and the picture-only fallback is refused -
/// but of Augmentation and Transmutation it carries ONLY the Perfect variant. One item drawing a
/// picture made it look unshared, the fallback fired, and every plain Orb of Augmentation in a
/// stash was valued at the Perfect orb's rate. On a real stash of 3,357 plain Transmutation, 425
/// Greater and 114 Perfect, that is three thousand nine hundred orbs wearing the dearest price
/// of the three.
///
/// So the two halves of this file are one bug seen twice: a picture is not an identity, and
/// anything that treats it as one - the valuation or the breakdown that explains it - is wrong
/// in the same way.
/// </remarks>
public sealed class CurrencyTierTests
{
    private const string Picture = "CurrencyUpgradeToMagic";
    private const string Art = "Art/2DItems/Currency/" + Picture + ".dds";

    private const string Plain = "Orb of Transmutation";
    private const string Greater = "Greater Orb of Transmutation";
    private const string Perfect = "Perfect Orb of Transmutation";

    /// <summary>The real Standard rate at the hour the live answer above was read.</summary>
    private const double Rate = 326.2;

    /// <summary>An image url in the shape the site serves, so ArtOf reads the picture out of it.</summary>
    private static string Image()
        => "/gen/image/"
           + Convert.ToBase64String(Encoding.UTF8.GetBytes($"[25,14,{{\"f\":\"2DItems/Currency/{Picture}\"}}]"))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_')
           + "/ab/" + Picture + ".png";

    /// <summary>A book listing exactly the named tiers, priced in Divine as the site quotes.</summary>
    private static PriceBook Book(params (string Called, double Divine)[] tiers)
    {
        var items = new StringBuilder();
        var lines = new StringBuilder();
        for (var i = 0; i < tiers.Length; i++)
        {
            string id = $"t{i}";
            items.Append(i > 0 ? "," : string.Empty)
                .Append($"{{\"id\":\"{id}\",\"name\":\"{tiers[i].Called}\",\"image\":\"{Image()}\",\"category\":\"Currency\"}}");

            // The volume is over the gate on purpose: a thin line is a different refusal, and
            // mixing the two would let this pass for the wrong reason.
            lines.Append(i > 0 ? "," : string.Empty)
                .Append($"{{\"id\":\"{id}\",\"primaryValue\":{tiers[i].Divine.ToString(System.Globalization.CultureInfo.InvariantCulture)},")
                .Append("\"volumePrimaryValue\":9999,\"maxVolumeCurrency\":\"exalted\",\"maxVolumeRate\":1}");
        }

        var book = new PriceBook();
        Assert.True(
            book.Add(
                PriceKind.Exchange,
                $"{{\"core\":{{\"rates\":{{\"exalted\":{Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}},"
                + $"\"items\":[{items}],\"lines\":[{lines}]}}") > 0,
            "the book took nothing - the fixture is wrong, not the code");

        return book;
    }

    private static InspectedItem Item(string path, string called, int stack)
        => new(1, path, called, string.Empty, string.Empty, -1, null, stack, 0, Art, [], []);

    private static InspectedItem Nameless(int stack)
        => new(1, "Metadata/Items/Currency/CurrencyUpgradeToMagic", string.Empty, string.Empty,
            string.Empty, -1, null, stack, 0, Art, [], []);

    private static StashPage Page(params InspectedItem[] items)
        => new(1, InventoryKind.Stash, "page", 12, 12,
            [.. items.Select((item, i) => new StashSlot(new StashedItem((ulong)i + 1, i, 0, 1, 1), item))]);

    /// <summary>The stacks from the stash this was reported against, tiers and all.</summary>
    private static InspectedItem[] AllThree() =>
    [
        Item("Metadata/Items/Currency/CurrencyUpgradeToMagic", Plain, 3357),
        Item("Metadata/Items/Currency/CurrencyUpgradeToMagic2", Greater, 425),
        Item("Metadata/Items/Currency/CurrencyUpgradeToMagic3", Perfect, 114),
    ];

    [Fact]
    public void AnOrbIsNotPricedAtItsPerfectVariantsRate()
    {
        // THE BUG, in the shape the live price source actually has: only the Perfect variant is
        // listed. Before the fix the picture looked unshared, so all 3,896 orbs were valued at
        // 0.02257 div - 7.36 Exalted each, on stacks worth a fiftieth of that.
        PriceBook book = Book((Perfect, 0.02257));

        Assert.Null(book.Unit(AllThree()[0]));
        Assert.Null(book.Unit(AllThree()[1]));
        Assert.Equal(0.02257 * Rate, book.Unit(AllThree()[2])!.Value, 6);

        Valued purse = book.Purse([Page(AllThree())]);

        // Only the Perfect stack counts, and the other two are declared missing rather than
        // guessed at - which is what lets the caller see the total is partial.
        Assert.Equal(114 * 0.02257 * Rate, purse.Exalted, 6);
        Assert.Equal(1, purse.Priced);
        Assert.Equal(2, purse.Unpriced);
    }

    [Fact]
    public void EachTierTheBookListsKeepsItsOwnPrice()
    {
        // The Exalted, Chaos and Regal case, where the source does carry all three. This has
        // always worked and must go on working: the fix must refuse a missing name, not a
        // present one.
        PriceBook book = Book((Plain, 0.001), (Greater, 0.01), (Perfect, 0.02257));

        Assert.Equal(0.001 * Rate, book.Unit(AllThree()[0])!.Value, 6);
        Assert.Equal(0.01 * Rate, book.Unit(AllThree()[1])!.Value, 6);
        Assert.Equal(0.02257 * Rate, book.Unit(AllThree()[2])!.Value, 6);

        Valued purse = book.Purse([Page(AllThree())]);

        Assert.Equal(
            ((3357 * 0.001) + (425 * 0.01) + (114 * 0.02257)) * Rate, purse.Exalted, 5);
        Assert.Equal(3, purse.Priced);
        Assert.Equal(0, purse.Unpriced);
    }

    [Fact]
    public void ThreeTiersAreThreeLinesRatherThanOneSum()
    {
        // The other half of the same bug. Grouped by picture alone, the three collapsed into one
        // row reading 3,896 held - the count of all three - beside whichever unit price happened
        // to be written last. The count was real and the line was a fiction.
        PriceBook book = Book((Plain, 0.001), (Greater, 0.01), (Perfect, 0.02257));

        IReadOnlyList<CurrencyPurse.PurseLine> lines = book.Breakdown([Page(AllThree())]);

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, line => line.Stack == 3357 + 425 + 114);

        Assert.Equal(3357, lines.Single(line => line.Called == Plain).Stack);
        Assert.Equal(425, lines.Single(line => line.Called == Greater).Stack);
        Assert.Equal(114, lines.Single(line => line.Called == Perfect).Stack);

        // And every row reads as it looks: held times each really is worth.
        foreach (CurrencyPurse.PurseLine line in lines)
        {
            Assert.Equal(line.Unit!.Value * line.Stack, line.Exalted, 5);
        }
    }

    [Fact]
    public void AnItemWhoseNameDidNotResolveStillUsesThePicture()
    {
        // What the picture-only fallback was written for, and the reason it is not simply
        // deleted: nothing else can price an item whose name the table has never heard of. It is
        // only refused when a name IS known and the book has no line under it.
        PriceBook book = Book((Plain, 0.001));

        Assert.Equal(0.001 * Rate, book.Unit(Nameless(10))!.Value, 6);
    }

    [Fact]
    public void APictureSharedByMoreThanOneIsStillRefusedWithoutAName()
    {
        // Unchanged, and the older guard: with several tiers listed the picture cannot say which
        // is meant, so a nameless item gets no price rather than one of the three.
        PriceBook book = Book((Plain, 0.001), (Perfect, 0.02257));

        Assert.True(book.Shared(PriceBook.ArtOf(Art)));
        Assert.Null(book.Unit(Nameless(10)));
    }
}
