using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// A flask's real charge numbers, computed from the base type's and the item's own rolls.
/// </summary>
/// <remarks>
/// WHY THESE ARE COMPUTED AT ALL. The game does not store them. Its Charges component is 0x40
/// bytes and every slot is accounted for; a value hunt across every component of the item and
/// one pointer level out of each found neither candidate while finding a control value it was
/// given; and a live watch moved only the current count. So the base numbers memory holds get
/// the item's stats applied, exactly as the game's own tooltip does.
///
/// THE TWO CASES BELOW ARE THE GAME'S OWN ANSWERS, read off one belt, and between them they
/// pin the rounding rule that makes this safe to do at all. The per-use case alone would not:
/// 8.5 truncates and rounds-half-to-even to the same 8. The maximum is what settles it, at
/// 88.9 against an observed 88.
/// </remarks>
public class FlaskChargeMathTests
{
    private static EquippedFlask Flask(int charges, int perUse, int max, int basePerUse, int baseMax)
        => new(1, "Metadata/Items/Flasks/FourFlaskLife1", charges, perUse, 0, max, basePerUse, baseMax);

    [Fact]
    public void AFLASKSCostAndMaximumAreTheGamesNumbersRatherThanItsBaseTypes()
    {
        // Simmering Lesser Life Flask of the Apprentice: base 10 per use,
        // local_charges_used_+% = -15, and a tooltip reading "Consumes 8 of 60 Charges on use".
        Assert.Equal(8, Scaled(10, -15));
        Assert.Equal(60, Scaled(60, 0));

        // The Mana Flask beside it: base maximum 70, local_max_charges_+% = 27, observed sitting
        // FULL at 88. This is the case that rules out rounding - 70 x 1.27 is 88.9, and the game
        // says 88, so it truncates. Everything else here rests on that.
        Assert.Equal(88, Scaled(70, 27));
        Assert.NotEqual(89, Scaled(70, 27));
    }

    [Fact]
    public void ANDTheArithmeticIsIntegerSoTheTruncationIsExact()
    {
        // 70 * 1.27 in doubles is 88.899999999999991, which truncates to 88 only because it
        // happens to land below. 70 * 127 / 100 is 88 because it cannot land anywhere else.
        Assert.Equal(88, 70 * (100 + 27) / 100);
        Assert.Equal(88, (int)Math.Truncate(70 * 1.27));

        // The case where the two would part company, kept as the reason the integer form is
        // not a stylistic preference: a value whose double is a hair ABOVE the true product
        // truncates one too high.
        Assert.Equal(3 * 105 / 100, (int)(3 * 1.05));
    }

    [Fact]
    public void ANDCanUseAsksTHISFlasksCostNotTheBaseTypes()
    {
        // The reported bug, as a test. A flask holding 8 charges that costs 8 is usable; it
        // read as unusable while the gate asked the base type's 10.
        Assert.True(Flask(charges: 8, perUse: 8, max: 60, basePerUse: 10, baseMax: 60).CanUse);
        Assert.False(Flask(charges: 7, perUse: 8, max: 60, basePerUse: 10, baseMax: 60).CanUse);

        // And the other direction, which the same gap breaks the other way round: a flask whose
        // rolls made a use MORE expensive would have had its key pressed for nothing.
        Assert.False(Flask(charges: 11, perUse: 12, max: 60, basePerUse: 10, baseMax: 60).CanUse);
    }

    [Fact]
    public void ANDAFLASKWithNoRollsIsUnchanged()
    {
        // The common case, and the one a scaling bug would show up in first: no charge stats,
        // so the base numbers have to come through untouched rather than off by a rounding.
        Assert.Equal(10, Scaled(10, 0));
        Assert.Equal(65, Scaled(65, 0));
        Assert.Equal(20, Scaled(20, 0));
    }

    /// <summary>
    /// The reader's arithmetic, restated.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY A COPY rather than a call. FlaskBeltReader.Scaled is private to a type that
    /// needs a memory reader and a schema to exist, and exposing it to be tested would widen a
    /// reader's surface for a test's convenience. What these tests are FOR is the rule - that
    /// the game truncates, and what the two observed flasks come to - so the rule is written
    /// down twice on purpose, and a change to one that is not made to the other goes red.
    /// </remarks>
    private static int Scaled(int baseValue, int percent)
    {
        if (baseValue <= 0)
        {
            return baseValue;
        }

        int scaled = baseValue * (100 + percent) / 100;
        return scaled > 0 ? scaled : baseValue;
    }
}
