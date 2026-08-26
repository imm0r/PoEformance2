using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The one thing about the exchange probe that is not JavaScript: a probe that did not get an
/// answer must not look like one that did.
/// </summary>
/// <remarks>
/// The stash page quotes the rate only when it is above zero, and that gate is the whole
/// protection against printing "1 div costs 0 ex" - or worse, dividing poe.ninja's rate by it -
/// on a probe that never reached the site. So the zero has to be a promise rather than a
/// coincidence of how the record happens to be built today.
/// </remarks>
public sealed class TradeProbeTests
{
    [Fact]
    public void AProbeThatCouldNotRunQuotesNoRate()
    {
        TradeProbe never = TradeProbe.Not("the trade window was closed");

        Assert.False(never.Ok);
        Assert.Equal(0, never.Rate);
        Assert.Equal("the trade window was closed", never.Error);
    }

    [Fact]
    public void AProbeThatCouldNotRunClaimsNothingAboutTheSite()
    {
        TradeProbe never = TradeProbe.Not("the trade page has not finished loading");

        // Nothing came back, so nothing may read as having come back: no listings, no ids, and
        // no status that a reader could mistake for one the site chose.
        Assert.Equal(0, never.Status);
        Assert.Equal(0, never.Got);
        Assert.Empty(never.Tags);
        Assert.Empty(never.Raw);
    }
}
