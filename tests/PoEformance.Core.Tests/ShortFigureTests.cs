using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>A count written short enough for an icon.</summary>
public class ShortFigureTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(538, "538")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(5400, "5.4k")]
    [InlineData(5449, "5.4k")]
    [InlineData(5460, "5.5k")]
    [InlineData(9949, "9.9k")]
    [InlineData(9950, "10k")]        // what would round to "10.0" is a whole number's worth
    [InlineData(53139, "53k")]
    [InlineData(53838, "54k")]       // the nearer number, not the cut one
    [InlineData(538000, "538k")]
    [InlineData(999499, "999k")]
    [InlineData(999500, "1.0M")]     // rounding up to the next unit is written in that unit
    [InlineData(1200000, "1.2M")]
    [InlineData(12000000, "12M")]
    [InlineData(538000000, "538M")]
    [InlineData(1200000000, "1.2B")]
    public void TwoFiguresUnderTenOfAUnit_WholeNumbersAbove(long value, string expected)
    {
        Assert.Equal(expected, ShortFigure.Format(value));
    }
}
