using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// World-map pins against the flags a character has set.
/// </summary>
/// <remarks>
/// WHAT THE COLUMNS MEAN IS NOT KNOWN, and these tests are written so that they do not pretend
/// otherwise. MapPins carries five flag columns - QuestFlags1/2/3 as arrays, QuestFlag1/2 as
/// single references - and unlike QuestStates, whose two are named FlagsPresent and
/// FlagsMissing, nothing says which way any of them points. They could all be required, they
/// could be alternatives, one could be an exclusion.
///
/// So nothing here asserts a RULE. What is asserted is that both readings are computed and kept
/// apart, that a condition naming no flags counts for neither, and that the flags a pin is
/// still waiting on come out by name - which is the half that is useful whichever way the rule
/// turns out to go.
/// </remarks>
public class MapPinProgressTests
{
    private static PinCondition Condition(string column, int[] rows, int[] set)
        => new(column, rows, [.. rows.Where(set.Contains)]);

    [Fact]
    public void AConditionIsMetOnlyWhenEveryFlagItNamesIsSet()
    {
        Assert.True(Condition("QuestFlags1", [1, 2], [1, 2, 9]).Met);
        Assert.False(Condition("QuestFlags1", [1, 2], [1]).Met);
    }

    [Fact]
    public void AConditionNamingNothingIsNotMetAndNotCountedEither()
    {
        // Four of a pin's five columns are usually empty. Treating an empty one as satisfied
        // would make every pin "shown"; treating it as unsatisfied would make every pin
        // locked. It has to count for neither, which is what Real filters on.
        PinCondition empty = Condition("QuestFlag2", [], []);

        Assert.False(empty.Met);
        Assert.Empty(new MapPin(0, "x", "X", 1, [empty]).Real);
    }

    [Fact]
    public void APinWithNoConditionsAtAllIsCalledUnconditional()
    {
        // Neither shown-by-flags nor waiting: it is simply always there, and a list about what
        // is waiting should leave it out rather than claim either.
        var pin = new MapPin(0, "town", "Clearfell Encampment", 1, [Condition("QuestFlags1", [], [])]);

        Assert.True(pin.Unconditional);
        Assert.False(pin.All);
        Assert.False(pin.Any);
    }

    [Fact]
    public void BothReadingsAreKeptApartBecauseTheRuleIsUnknown()
    {
        // The measurement this is built for: one condition met and one not. Under "every
        // condition" the pin is locked, under "any condition" it is shown, and which of those
        // the game does is what counting its own pins will say.
        var pin = new MapPin(0, "p", "Somewhere", 2,
        [
            Condition("QuestFlags1", [1], [1]),
            Condition("QuestFlags2", [2], []),
        ]);

        Assert.False(pin.All);
        Assert.True(pin.Any);
    }

    [Fact]
    public void ThePinSaysWhichFlagsItIsStillWaitingOn()
    {
        // The half that is useful whichever way the rule goes, and the half the game shows
        // nowhere: a locked pin is simply not drawn, so what it wants is invisible in play.
        var pin = new MapPin(0, "p", "The Red Vale", 1,
        [
            Condition("QuestFlags1", [4, 5, 6], [4]),
        ]);

        Assert.Equal([5, 6], pin.Real.Single().Wanting);
    }

    [Fact]
    public void EveryConditionMetMakesThePinShown()
    {
        var pin = new MapPin(0, "p", "Mud Burrow", 1,
        [
            Condition("QuestFlags1", [1], [1]),
            Condition("QuestFlag1", [7], [7]),
            Condition("QuestFlags3", [], []),
        ]);

        Assert.True(pin.All);
        Assert.True(pin.Any);
        Assert.Empty(pin.Real.SelectMany(c => c.Wanting));
    }

    [Fact]
    public void TheColumnsAreNamedRatherThanGuessedFromTheirNeighbours()
    {
        // The column list is vendored anyway, so a schema refresh that renames one should drop
        // it out with the rest of its layout rather than have it inferred from position.
        Assert.Equal(
            ["QuestFlags1", "QuestFlags2", "QuestFlags3", "QuestFlag1", "QuestFlag2"],
            MapPinProgress.FlagColumns);
    }
}
