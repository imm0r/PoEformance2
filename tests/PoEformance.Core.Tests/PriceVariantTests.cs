using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// The Greater and Perfect variants, which draw the same picture as the orb they upgrade.
/// </summary>
/// <remarks>
/// AGAINST A LIVE ANSWER CAPTURED THE DAY THIS WAS FOUND. Every figure asserted here was read
/// out of poe.ninja's own reply for Standard, not chosen to make the code pass - which matters
/// more than usual, because the bug this covers was a wrong price that looked entirely
/// reasonable until somebody counted their own stash.
///
/// WHAT IT COST, from the report: a tab of 3,312 plain Orbs of Transmutation valued at the
/// PERFECT rate came to 45.6k Exalted instead of 172, and a purse holding 97 Divine reported
/// itself as roughly 2,700.
/// </remarks>
public class PriceVariantTests
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

        throw new FileNotFoundException($"captured answer {name} not found");
    }

    private static PriceBook Live()
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, Captured("ninja-exchange-variants.json")) > 0);
        Assert.True(book.Ready);
        return book;
    }

    private const string Transmute = "Art/2DItems/Currency/CurrencyUpgradeToMagic.dds";
    private const string Exalt = "Art/2DItems/Currency/CurrencyAddModToRare.dds";
    private const string Regal = "Art/2DItems/Currency/CurrencyUpgradeMagicToRare.dds";

    [Fact]
    public void APictureSeveralThingsDrawIsNotAPrice()
    {
        // The rule, stated on its own. Five pictures in the live answer are claimed by two or
        // three currencies each; asking any of them by picture alone has to come back with
        // nothing rather than with whichever line happened to be read last.
        PriceBook book = Live();

        Assert.Null(book.Worth(Transmute));
        Assert.Null(book.Worth(Exalt));
        Assert.Null(book.Worth(Regal));
    }

    [Fact]
    public void AndTheNameIsWhatTellsThemApart()
    {
        // Every figure is poe.ninja's, converted at the rate in the same answer. All three
        // Exalted variants trade enough to be believed, so this is the clean case: one picture,
        // three prices, a hundred and fifty times apart.
        PriceBook book = Live();

        Assert.Equal(1.0, book.Fungible(Exalt, "Exalted Orb")!.Value, 2);
        Assert.Equal(50.33, book.Fungible(Exalt, "Greater Exalted Orb")!.Value, 1);
        Assert.Equal(151.38, book.Fungible(Exalt, "Perfect Exalted Orb")!.Value, 1);
    }

    [Fact]
    public void TheWorstOfThemIsTheRegalOrb()
    {
        // Regal against Perfect Regal is 0.28 Exalted against 454.2 - a factor of sixteen
        // hundred on one picture. A tab of Regals priced at the Perfect rate is not a wrong
        // number, it is a different order of magnitude.
        PriceBook book = Live();

        Assert.Equal(0.28, book.Fungible(Regal, "Regal Orb")!.Value, 2);
        Assert.Equal(454.2, book.Fungible(Regal, "Perfect Regal Orb")!.Value, 1);
    }

    [Fact]
    public void AVariantTHROWNOUTByTheVolumeGateStillProvesThePictureIsShared()
    {
        // THE CASE THAT ALMOST GOT AWAY, and the reason the claim is registered from the item
        // table rather than from the lines that survived. In this answer:
        //
        //   Orb of Transmutation          0.006 Divine traded  ->  gated out as too thin
        //   Perfect Orb of Transmutation  1.33  Divine traded  ->  kept, at 13.76 Exalted
        //
        // Counting only what got through the gate finds ONE claimant, calls the picture
        // unambiguous, and hands every plain Transmutation the Perfect price - which is exactly
        // the 14 ex each that was reported. The plain orb must come back unpriced instead.
        PriceBook book = Live();

        Assert.True(book.Shared("currencyupgradetomagic"));
        Assert.Equal(13.76, book.Fungible(Transmute, "Perfect Orb of Transmutation")!.Value, 2);
        Assert.Null(book.Fungible(Transmute, "Orb of Transmutation"));

        // And the same shape on Augmentation, where TWO of the three were gated out.
        const string augment = "Art/2DItems/Currency/CurrencyAddModToMagic.dds";
        Assert.Equal(26.06, book.Fungible(augment, "Perfect Orb of Augmentation")!.Value, 2);
        Assert.Null(book.Fungible(augment, "Orb of Augmentation"));
        Assert.Null(book.Fungible(augment, "Greater Orb of Augmentation"));
    }

    [Fact]
    public void ANameNobodyRecognisesGetsNoPriceRatherThanTheWrongOne()
    {
        // What happens when the shipped name table has not heard of a variant yet. Nothing is
        // the right answer: an unpriced stack shows up in the unpriced count and understates a
        // total, where guessing between two prices four hundred times apart overstates it and
        // says nothing.
        PriceBook book = Live();

        Assert.Null(book.Fungible(Transmute, "Sublime Orb of Transmutation"));
        Assert.Null(book.Fungible(Transmute, null));
        Assert.Null(book.Fungible(Transmute, string.Empty));
    }

    [Fact]
    public void APictureOnlyOneThingDrawsStillAnswersOnItsOwn()
    {
        // The fix must not cost the ordinary case. Divine draws a picture nothing else draws, so
        // it prices with or without a name - which is what keeps every currency that has no
        // variants working exactly as it did.
        PriceBook book = Live();
        const string divine = "Art/2DItems/Currency/CurrencyModValues.dds";

        Assert.False(book.Shared("currencymodvalues"));
        Assert.Equal(book.Rate, book.Worth(divine)!.Value, 1);
        Assert.Equal(book.Rate, book.Fungible(divine, "Divine Orb")!.Value, 1);
        Assert.Equal(book.Rate, book.Fungible(divine, null)!.Value, 1);
    }

    [Fact]
    public void AnItemPricesThroughItsBaseNameEndToEnd()
    {
        // Through the path the stash actually uses, since that is where the wrong figure came
        // out: an InspectedItem carries its base name from the shipped table, and Unit has to
        // reach for that before it reaches for the picture.
        PriceBook book = Live();

        InspectedItem plain = Currency("CurrencyUpgradeToMagic", "Orb of Transmutation", 3312);
        InspectedItem perfect = Currency("CurrencyUpgradeToMagic", "Perfect Orb of Transmutation", 3312);

        // 3,312 of them came to 45.6k Exalted in the report. They are now unpriced, because the
        // only believable line on that picture belongs to a different orb - which understates a
        // purse by about 170 Exalted and says so in the unpriced count.
        Assert.Null(book.Of(plain));
        Assert.InRange(book.Of(perfect)!.Value, 45_000, 46_000);

        // The one the whole report turned on: 97 Divine is 97 Divine.
        InspectedItem divine = Currency("CurrencyModValues", "Divine Orb", 97);
        Assert.Equal(97 * book.Rate, book.Of(divine)!.Value, 0);
    }

    private static InspectedItem Currency(string file, string called, int stack)
        => new(
            1,
            $"Metadata/Items/Currency/{file}",
            called,
            string.Empty,
            string.Empty,
            -1,
            null,
            stack,
            0,
            $"Art/2DItems/Currency/{file}.dds",
            [],
            []);
}
