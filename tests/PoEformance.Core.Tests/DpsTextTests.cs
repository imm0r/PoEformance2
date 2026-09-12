using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>The figure in a DPS text, whichever way the player's locale writes it.</summary>
public class DpsTextTests
{
    [Theory]
    [InlineData("DPS: 53.838", 53838)]      // a German client groups with a point
    [InlineData("DPS: 53,838", 53838)]      // an English one with a comma - the same number
    [InlineData("DPS: 1.234.567", 1234567)]
    [InlineData("DPS: 538", 538)]
    [InlineData("DPS:  7", 7)]
    [InlineData("DPS: 12.5", 13)]           // one or two digits after the separator is a fraction, rounded
    [InlineData("DPS: 12,4", 12)]
    [InlineData("DPS: 1,234.5", 1235)]      // grouped and fractional at once
    [InlineData("DPS: 24 827", 24827)]      // a space as the group separator
    public void TheNumberIsReadAsTheGameWroteIt(string text, int expected)
    {
        Assert.Equal(expected, DpsText.Parse(text));
    }

    [Theory]
    [InlineData("DPS: —")]                  // the panel's dash for a skill that deals none
    [InlineData("DPS: -")]
    [InlineData("DPS:")]
    [InlineData("Level:")]
    [InlineData("")]
    [InlineData("DPS: 1.23.456")]           // groups are three digits or nothing
    [InlineData("DPS: 12.")]
    public void NoFigureIsNull_NotZero(string text)
    {
        Assert.Null(DpsText.Parse(text));
    }

    [Fact]
    public void ALabelIsKnownByItsStem()
    {
        Assert.True(DpsText.IsDps("DPS: 53.838"));
        Assert.True(DpsText.IsDps("dps 12"));
        Assert.False(DpsText.IsDps("Level:"));
        Assert.False(DpsText.IsDps("Spark"));
    }
}
