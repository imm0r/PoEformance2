using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The two rules the tool's palette holds itself to, and the arithmetic they are stated in.
/// </summary>
/// <remarks>
/// WHY THESE ARE TESTS AND NOT A SCREENSHOT. A colour that is slightly too dark on the panel is
/// not a thing anybody notices in a screenshot taken in a hideout at noon - it is a thing
/// somebody notices six weeks later, in a dark map, when a row they needed to read was not
/// readable. The failure mode of a palette is that it is FINE UNTIL IT IS NOT, per reader, per
/// monitor, per area. So the readable-here floor is a number, and the number is checked.
///
/// The floors themselves are the part worth arguing with, and they are two:
///
/// - THE TOOL'S OWN INKS clear 7:1 on the panel and 4.5:1 on the band under a picked row. Both
///   are above the usual 4.5 for body text, on purpose: this is body text over a game that is
///   painting foliage and firelight through whatever solidity the reader chose, read across a
///   room, at a size somebody may have set to twelve pixels.
/// - THE GAME'S OWN LADDER clears 4:1 and 3:1, which is lower and has to be, because those
///   colours are not ours. A player has read #AF6025 as "unique" since their first drop, and a
///   tool that prints its own more legible orange is a tool that has to be learned. So the
///   BACKGROUND is what gets tuned until the game's colours clear a floor on it - see
///   <see cref="ThePickedRowBandIsLowEnoughForTheGamesOwnColours"/>, which is the test that
///   caught the 2.2:1 this whole rework started from.
/// </remarks>
public class OverlayInkTests
{
    /// <summary>Every colour the tool writes its own meaning in.</summary>
    /// <remarks>
    /// Named pairs rather than a bare list, because a failure that says "an ink at 6.2:1" sends
    /// somebody looking through eleven colours, and one that says "Bad at 6.2:1" does not.
    ///
    /// <see cref="OverlayInk.Accent"/> IS ABSENT and that is the file's own rule rather than an
    /// oversight: it is chrome - a checkmark, a tab's overline, a slider's grab - and is never a
    /// word standing in a column of words. The exemption is written down where the colour is.
    /// </remarks>
    public static readonly (string Name, Vector4 Ink)[] Inks =
    [
        ("Ink", OverlayInk.Ink),
        ("Quiet", OverlayInk.Quiet),
        ("Measured", OverlayInk.Measured),
        ("Name", OverlayInk.Name),
        ("Reference", OverlayInk.Reference),
        ("Money", OverlayInk.Money),
        ("Good", OverlayInk.Good),
        ("Warn", OverlayInk.Warn),
        ("Bad", OverlayInk.Bad),
    ];

    private static (string Name, Vector4 Ink)[] Ladder()
        => Enumerable.Range(0, OverlayInk.RarityCount)
            .Select(rarity => ($"rarity {rarity}", OverlayInk.Rarity(rarity)))
            .ToArray();

    [Fact]
    public void TheArithmeticIsTheStandardArithmetic()
    {
        // PINNED AGAINST KNOWN ANSWERS, because everything below is stated in these two
        // functions - and a contrast helper with a transposed coefficient would make every floor
        // in this file pass while measuring nothing. These three are the published values for
        // WCAG relative luminance, so a wrong transcription cannot survive them.
        var black = new Vector4(0f, 0f, 0f, 1f);
        var white = new Vector4(1f, 1f, 1f, 1f);

        Assert.Equal(0f, OverlayInk.Luminance(black), 4);
        Assert.Equal(1f, OverlayInk.Luminance(white), 4);
        Assert.Equal(21f, OverlayInk.Contrast(black, white), 2);
        Assert.Equal(1f, OverlayInk.Contrast(white, white), 4);

        // And the perceptual distance, which has no famous constant to check against but does
        // have the two properties any distance must have.
        Assert.Equal(0f, OverlayInk.Distance(OverlayInk.Good, OverlayInk.Good), 5);
        Assert.Equal(
            OverlayInk.Distance(OverlayInk.Good, OverlayInk.Bad),
            OverlayInk.Distance(OverlayInk.Bad, OverlayInk.Good),
            5);
    }

    [Fact]
    public void EveryInkIsReadableOnAPanel()
    {
        foreach ((string name, Vector4 ink) in Inks)
        {
            float contrast = OverlayInk.Contrast(ink, OverlayInk.Panel);
            Assert.True(contrast >= 7f, $"{name} is only {contrast:F2}:1 against the panel it is printed on");
        }
    }

    [Fact]
    public void AndOnTheBandUnderAPickedRow()
    {
        // The case that is easy to forget, because nothing looks wrong until somebody CLICKS a
        // row: a colour tuned against the panel is being read against the selection band, which
        // is a good deal lighter than the panel is.
        foreach ((string name, Vector4 ink) in Inks)
        {
            float contrast = OverlayInk.Contrast(ink, OverlayInk.Selected);
            Assert.True(contrast >= 4.5f, $"{name} is only {contrast:F2}:1 on a picked row");
        }
    }

    [Fact]
    public void TheGamesOwnLadderClearsTheLowerFloorOnBoth()
    {
        foreach ((string name, Vector4 ink) in Ladder())
        {
            float onPanel = OverlayInk.Contrast(ink, OverlayInk.Panel);
            float onRow = OverlayInk.Contrast(ink, OverlayInk.Selected);

            Assert.True(onPanel >= 4f, $"{name} is only {onPanel:F2}:1 against the panel");
            Assert.True(onRow >= 3f, $"{name} is only {onRow:F2}:1 on a picked row");
        }
    }

    [Fact]
    public void ThePickedRowBandIsLowEnoughForTheGamesOwnColours()
    {
        // THE TEST THIS WHOLE FILE EXISTS FOR. The band under a picked row used to be the same
        // warm as the tab in front - Warm(0.30) - and on it the game's unique orange sat at
        // 2.2:1: not a dim name, an unreadable one, on the row somebody had just clicked. The
        // fix was to pull the band down the ray rather than to invent a more legible orange.
        //
        // Both halves are asserted, because only the pair says anything. That the band clears
        // the floor is the fix; that the TAB colour does not is why the two had to stop being
        // one value - a later tidy-up that merged them back would put the bug straight back.
        Vector4 unique = OverlayInk.Rarity(ItemRarity.Unique);

        Assert.True(
            OverlayInk.Contrast(unique, OverlayInk.Selected) >= 3f,
            $"the game's unique orange is {OverlayInk.Contrast(unique, OverlayInk.Selected):F2}:1"
            + " on a picked row - the band has drifted back up the ramp");

        Assert.True(
            OverlayInk.Contrast(unique, OverlayInk.Chrome) < 3f,
            "the tab colour now clears the floor too, so the split between it and the selection"
            + " band no longer costs anything - which means one of them has moved");
    }

    [Fact]
    public void TheStatusInksAreFurtherApartThanTheGamesOwnClosestPair()
    {
        // HOW FAR APART IS FAR ENOUGH is not a number this project gets to invent, and this is
        // where it comes from instead: the closest pair in the ladder the game itself ships.
        // Whatever the game expects a player to tell apart at a glance, the tool's own three
        // status colours must beat - they sit in one column and are read the same way.
        (string Name, Vector4 Ink)[] ladder = Ladder();
        float floor = Closest(ladder).Apart;

        (string, string, float) worst = Closest(
        [
            ("Good", OverlayInk.Good),
            ("Warn", OverlayInk.Warn),
            ("Bad", OverlayInk.Bad),
        ]);

        Assert.True(
            worst.Item3 >= floor,
            $"{worst.Item1} and {worst.Item2} are {worst.Item3:F4} apart, under the {floor:F4}"
            + " between the game's own closest two rarities");
    }

    [Fact]
    public void AndNoneOfThemShoutsOverTheOthersByBrightnessAlone()
    {
        // HUE CARRIES THE MEANING; BRIGHTNESS MUST NOT ADD A SECOND ONE. The eye reads a
        // brighter row as a more important row whether or not anybody meant it to, so three
        // colours at three different brightnesses rank themselves - and the old set ranked
        // itself BACKWARDS. Its greens sat at .64 and its reds at .29, so in a column of
        // statuses "this worked" was over twice as loud as "this failed" and the failure was
        // the last thing the eye arrived at.
        //
        // A red cannot be made as bright as a green without turning pink - green is three times
        // the luminance of red at the same intensity, which is a fact about eyes rather than a
        // choice - so the rule is a BAND rather than equality. Within a factor of two the three
        // read as three colours; beyond it they read as a ranking.
        float brightest = Math.Max(
            OverlayInk.Luminance(OverlayInk.Good),
            Math.Max(OverlayInk.Luminance(OverlayInk.Warn), OverlayInk.Luminance(OverlayInk.Bad)));

        float dimmest = Math.Min(
            OverlayInk.Luminance(OverlayInk.Good),
            Math.Min(OverlayInk.Luminance(OverlayInk.Warn), OverlayInk.Luminance(OverlayInk.Bad)));

        Assert.True(
            brightest / dimmest <= 2f,
            $"the loudest status ink is {brightest / dimmest:F2} times the quietest, so the three"
            + " rank themselves by brightness on top of what they were meant to say");
    }

    [Theory]
    [InlineData(0.13f, 0.20f)]
    [InlineData(0.20f, 0.30f)]
    [InlineData(0.30f, 0.44f)]
    [InlineData(0.44f, 0.54f)]
    public void TheWarmRampOnlyEverGetsBrighter(float dim, float lit)
    {
        // WHAT THE RAMP IS FOR: a control that is pointed at gets lighter. The buttons used to
        // break this outright - they rested at a neutral grey and lit to a warm one, so being
        // pointed at swapped the button for a different-coloured button.
        Assert.True(
            OverlayInk.Luminance(OverlayInk.Warm(lit)) > OverlayInk.Luminance(OverlayInk.Warm(dim)),
            $"Warm({lit}) is not brighter than Warm({dim})");
    }

    [Fact]
    public void AndIsTheSameMaterialAtEveryStop()
    {
        // The other half: brighter and NOTHING ELSE. Two stops on the ray have to hold the same
        // proportions between their channels, or "lit harder" is quietly also "a bit more
        // orange" - which is how a ramp becomes four colours that merely look related.
        Vector4 dim = OverlayInk.Warm(0.2f);
        Vector4 lit = OverlayInk.Warm(0.5f);

        Assert.Equal(dim.Y / dim.X, lit.Y / lit.X, 5);
        Assert.Equal(dim.Z / dim.X, lit.Z / lit.X, 5);

        // And the stops the theme actually uses are on it rather than beside it.
        Assert.Equal(OverlayInk.Warm(0.20f), OverlayInk.Selected);
        Assert.Equal(OverlayInk.Warm(0.30f), OverlayInk.Chrome);
        Assert.Equal(OverlayInk.Warm(0.44f), OverlayInk.Lit);
        Assert.Equal(OverlayInk.Warm(0.54f), OverlayInk.Held);
    }

    [Fact]
    public void TheLadderAnswersForEveryNumberTheGameCanHandIt()
    {
        // The rarity arrives from memory, so it can be anything at all: a wrong offset, a value
        // a patch added, or the -1 the reader uses for "could not tell". None of those may take
        // the stash grid down over a colour.
        Assert.Equal(OverlayInk.Rarity(0), OverlayInk.Rarity(-1));
        Assert.Equal(OverlayInk.Rarity(0), OverlayInk.Rarity(99));
        Assert.Equal(OverlayInk.Rarity(0), OverlayInk.Rarity(ItemRarity.Unknown));

        // Including the number the enum does not name. The game numbers currency 5 and leaves 4
        // for quest items; a five-entry table indexed by the raw value would quietly print
        // currency in quest's green.
        Assert.Equal(6, OverlayInk.RarityCount);
        Assert.Equal(OverlayInk.Rarity(5), OverlayInk.Rarity(ItemRarity.Currency));
        Assert.NotEqual(OverlayInk.Rarity(4), OverlayInk.Rarity(5));
    }

    [Fact]
    public void AndEveryRungIsItsOwnColour()
    {
        (string Name, Vector4 Ink)[] ladder = Ladder();
        (string first, string second, float apart) = Closest(ladder);

        // Loose, because this is not the separation rule - it is the guard against a copy-paste
        // that leaves two rungs identical, which is exactly how the two ladders this replaced
        // came to disagree in the first place.
        Assert.True(apart > 0.05f, $"{first} and {second} are all but the same colour ({apart:F4} apart)");
    }

    [Fact]
    public void MeasuredIsTheInkCooled_NotASecondColour()
    {
        // The same rule the tab bar's tint holds to, and for the same reason: a figure the tool
        // worked out is still the tool's ordinary text with something said about it, not a new
        // voice. Far enough to be seen, near enough to still be the ink.
        Assert.True(
            (OverlayInk.Measured - OverlayInk.Ink).Length()
            < (OverlayInk.Measured - OverlayInk.Reference).Length(),
            "the derived-figure ink has drifted closer to the pointer blue than to the ink");

        Assert.True(
            (OverlayInk.Measured - OverlayInk.Ink).Length() > 0.05f,
            "the derived-figure ink is too small a step from the ordinary one to be seen");
    }

    /// <summary>The two of these that look most alike, and how far apart they are.</summary>
    private static (string First, string Second, float Apart) Closest((string Name, Vector4 Ink)[] set)
    {
        (string, string, float) worst = (string.Empty, string.Empty, float.MaxValue);

        for (int i = 0; i < set.Length; i++)
        {
            for (int j = i + 1; j < set.Length; j++)
            {
                float apart = OverlayInk.Distance(set[i].Ink, set[j].Ink);
                if (apart < worst.Item3)
                {
                    worst = (set[i].Name, set[j].Name, apart);
                }
            }
        }

        return worst;
    }
}
