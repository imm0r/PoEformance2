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

    [Fact]
    public void AProbeThatCouldNotRunNamesNoLeagueToCompareAgainst()
    {
        TradeProbe never = TradeProbe.Not("the trade window was closed");

        // The stash page decides whether to print poe.ninja's rate beside the probe's by
        // comparing the two leagues. An empty league must therefore never equal a real one -
        // if it did, a probe that reached nobody would be compared against the price book as
        // though it had.
        Assert.Empty(never.League);
        Assert.False(never.League.Equals("Standard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARateFromAnotherLeagueIsNotTheLeagueBeingPlayed()
    {
        // The shape the live probe actually returned: Standard had no listings at all, so the
        // rate came from the challenge league instead. Printing "240 ex (poe.ninja: 367 ex,
        // 0.65x)" off that pair divides two unrelated markets by each other, and the guard
        // against it is this comparison - so pin that it distinguishes them.
        var elsewhere = new TradeProbe(
            Ok: true, Status: 200, Limits: string.Empty, Tags: [], Asked: 1, Got: 3,
            Rate: 240, League: "Runes of Aldur", Listed: 0, ListedRate: 0, ListedStatus: 200,
            Raw: string.Empty, Error: string.Empty);

        Assert.False(elsewhere.League.Equals("Standard", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("runes of aldur", elsewhere.League, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyExchangeSaysNothingAboutTheSearch()
    {
        // The shape that reading the exchange alone got wrong: no exchange listings in the
        // played league, and plenty on the other api serving the same league. The page decides
        // which of the two may be compared against poe.ninja from exactly these fields, so pin
        // that they stay independent - an empty Got must not drag ListedRate down with it.
        var mixed = new TradeProbe(
            Ok: true, Status: 200, Limits: string.Empty, Tags: [], Asked: 1, Got: 0,
            Rate: 240, League: "Runes of Aldur", Listed: 118, ListedRate: 367, ListedStatus: 200,
            Raw: string.Empty, Error: string.Empty);

        Assert.Equal(0, mixed.Got);
        Assert.True(mixed.Listed > 0);
        Assert.Equal(367, mixed.ListedRate);
        Assert.NotEqual(mixed.Rate, mixed.ListedRate);
    }

    [Fact]
    public void ARefusedSearchIsTellableFromAnEmptyOne()
    {
        // Both of these count zero listings, and for three runs the page called both of them
        // "nothing listed in Standard". One of them was the site answering
        // 400 "Unknown item name" - a question it refused, which says nothing at all about the
        // market. The status is the only thing that separates them, so pin that it does.
        var refused = new TradeProbe(
            Ok: true, Status: 200, Limits: string.Empty, Tags: [], Asked: 1, Got: 0,
            Rate: 240, League: "Runes of Aldur", Listed: 0, ListedRate: 0, ListedStatus: 400,
            Raw: string.Empty, Error: "Unknown item name");

        var empty = refused with { ListedStatus = 200, Error = string.Empty };

        Assert.Equal(refused.Listed, empty.Listed);
        Assert.NotEqual(refused.ListedStatus, empty.ListedStatus);
        Assert.True(refused.ListedStatus is > 0 and not 200);
        Assert.False(empty.ListedStatus is > 0 and not 200);
    }
}
