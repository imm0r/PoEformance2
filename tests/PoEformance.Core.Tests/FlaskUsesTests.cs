using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>How many times a flask can still be used - the figure written on it.</summary>
public class FlaskUsesTests
{
    [Theory]
    [InlineData(42, 9, 4)]     // four whole uses, and six charges that are not a fifth
    [InlineData(9, 9, 1)]
    [InlineData(8, 9, 0)]      // too few to trigger: none, not "almost one"
    [InlineData(60, 0, 0)]     // a cost that could not be read is no uses, not a division by nothing
    [InlineData(0, 10, 0)]
    public void UsesAreWholeChargesOverTheCostOfOne(int charges, int perUse, int expected)
    {
        var flask = new EquippedFlask(1, "Metadata/Items/Flasks/FlaskLife1", charges, perUse);

        Assert.Equal(expected, flask.Uses);
        Assert.Equal(expected > 0, flask.CanUse);
    }
}
