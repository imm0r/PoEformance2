using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game's own exchange feed, read against two real hours of it.
/// </summary>
/// <remarks>
/// The fixtures are unedited digests from the live CDN, trimmed to a handful of markets. They
/// carry the hour whose Divine/Exalted numbers were checked against what the game's own Currency
/// Exchange window showed at the time - 260.02 : 1 one way, 1 : 601.85 the other - which is the
/// only reason the bid and ask can be named with any confidence at all.
/// </remarks>
public sealed class ExchangeFeedTests
{
    /// <summary>Walks up for the fixtures folder, the way every other capture here is found.</summary>
    private static string Hour(int which)
    {
        string name = $"ggg-exchange-hour{which}.json";
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

        throw new FileNotFoundException($"captured hour {name} not found");
    }

    private static ExchangeRate Standard(int hour)
        => ExchangeFeed.Read(Hour(hour), "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted);

    [Fact]
    public void TheTwoSidesOfTheBookAreTheTwoSidesTheGameShows()
    {
        ExchangeRate now = Standard(1);

        // Exactly the two numbers in the game's Currency Exchange window for this hour.
        // If these ever drift, the naming below them is wrong and everything reading it is too.
        Assert.Equal(260, now.Bid, 0);
        Assert.Equal(602, now.Ask, 0);
    }

    [Fact]
    public void SellingIsWorthLessThanBuyingCosts()
    {
        // The whole reason the distinction is carried at all. A hoard valued at the ask is
        // valued at a price its owner would have to PAY - in this league and hour, 2.3 times
        // what they would actually get.
        ExchangeRate now = Standard(1);

        Assert.True(now.Bid < now.Ask);
        Assert.InRange(now.Ask / now.Bid, 2.0, 2.6);
    }

    [Fact]
    public void WhatTradedSitsBetweenTheTwo()
    {
        // Not a rule of the feed, a property of a market: trades clear between the two sides.
        // A Traded outside the spread would mean the sides were read the wrong way round.
        ExchangeRate now = Standard(1);

        Assert.InRange(now.Traded, now.Bid, now.Ask);
        Assert.Equal(473, now.Traded, 0);
    }

    [Fact]
    public void VolumeIsCountedInTheCurrencyTheRateIsQuotedPer()
    {
        // 16 Divine changed hands, not 7571 - and the blend multiplies this back by Traded to
        // recover the other side, so counting the wrong one silently unweights every average.
        ExchangeRate now = Standard(1);

        Assert.Equal(16, now.Volume, 0);
        Assert.Equal(7571, now.Volume * now.Traded, 0);
    }

    [Fact]
    public void AThinHourAloneIsNotEnoughToPriceOn()
    {
        // Standard traded sixteen Divine in the newest hour and eight in the one before, and
        // sixteen is under the threshold - so the blend must reach for the second hour rather
        // than quote a rate one large order could have set.
        ExchangeRate one = Standard(1);
        Assert.True(one.Volume < ExchangeFeed.Liquid);

        ExchangeRate blended = ExchangeFeed.Blend(
            [Hour(1), Hour(2)], "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted);

        Assert.True(blended.Volume >= ExchangeFeed.Liquid);
        Assert.Equal(24, blended.Volume, 0);
    }

    [Fact]
    public void ABlendWeighsByVolumeRatherThanAveragingRates()
    {
        // 16 at 473.19 and 8 at 260.0. The mean of the two RATES is 366.6; the volume-weighted
        // answer is (7571 + 2080) / 24 = 402.1. The difference is the whole point: an hour that
        // moved one orb must not count as heavily as one that moved a thousand.
        ExchangeRate blended = ExchangeFeed.Blend(
            [Hour(1), Hour(2)], "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted);

        Assert.Equal(402.1, blended.Traded, 1);
        Assert.NotEqual(366.6, blended.Traded, 1);
    }

    [Fact]
    public void ABlendKeepsTheNewestBookRatherThanMixingSpreads()
    {
        // Bid and ask are what the exchange was offering at a moment. Averaging them across
        // hours would quote a spread that never existed, so the newest hour that had a book
        // keeps it and only what traded is blended.
        ExchangeRate newest = Standard(1);
        ExchangeRate blended = ExchangeFeed.Blend(
            [Hour(1), Hour(2)], "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted);

        Assert.Equal(newest.Bid, blended.Bid, 2);
        Assert.Equal(newest.Ask, blended.Ask, 2);
    }

    [Fact]
    public void AnotherLeagueIsAnotherMarket()
    {
        // Every league shares one file, and reading the wrong one is the mistake that already
        // cost this project a day - see TradeProbe. The two leagues genuinely differ here.
        ExchangeRate standard = Standard(1);
        ExchangeRate live = ExchangeFeed.Read(
            Hour(1), "Runes of Aldur", ExchangeFeed.Divine, ExchangeFeed.Exalted);

        Assert.True(live.Known);
        Assert.NotEqual(standard.Traded, live.Traded, 0);

        // And the active league is liquid enough to price on a single hour, where Standard is not.
        Assert.True(live.Volume >= ExchangeFeed.Liquid);
        Assert.True(standard.Volume < ExchangeFeed.Liquid);
    }

    [Fact]
    public void ALeagueNobodyPlaysAnswersNothingRatherThanGuessing()
    {
        ExchangeRate none = ExchangeFeed.Read(
            Hour(1), "No Such League", ExchangeFeed.Divine, ExchangeFeed.Exalted);

        Assert.False(none.Known);
        Assert.Equal(0, none.Traded);
    }

    [Fact]
    public void ThePairIsTheSameMarketWhicheverWayRoundItIsAsked()
    {
        // The feed's own pair order is arbitrary, so asking Divine-in-Exalted and
        // Exalted-in-Divine must both find the market - and give reciprocal rates.
        ExchangeRate divInEx = Standard(1);
        ExchangeRate exInDiv = ExchangeFeed.Read(
            Hour(1), "Standard", ExchangeFeed.Exalted, ExchangeFeed.Divine);

        Assert.True(exInDiv.Known);
        Assert.Equal(1.0 / divInEx.Traded, exInDiv.Traded, 6);
    }

    [Fact]
    public void RubbishIsAMissingHourRatherThanACrash()
    {
        Assert.False(ExchangeFeed.Read("{not json", "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted).Known);
        Assert.False(ExchangeFeed.Read(null, "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted).Known);
        Assert.False(ExchangeFeed.Read("{}", "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted).Known);

        // And a blend of nothing but holes is simply unknown, not zero-and-confident.
        Assert.False(ExchangeFeed.Blend(
            [null, "{not json", "{}"], "Standard", ExchangeFeed.Divine, ExchangeFeed.Exalted).Known);
    }

    [Fact]
    public void TheHourAskedForIsAlwaysOneThatHasFinished()
    {
        // The current hour is still being filled and always answers empty, so counting back one
        // is the newest that can say anything at all.
        const long AnHour = 3600;
        const long Striking = 1787806800;   // an exact hour boundary, from a real digest
        var partway = DateTimeOffset.FromUnixTimeSeconds(Striking + 1234);

        long newest = ExchangeFeed.HourBefore(partway);

        // The hour CONTAINING that moment is still being filled, so the newest complete one is
        // the hour before it - not the one just struck.
        Assert.Equal(Striking - AnHour, newest);
        Assert.NotEqual(Striking, newest);
        Assert.Equal(0, newest % AnHour);

        // And walking further back for the blend steps a whole hour at a time.
        Assert.Equal(newest - AnHour, ExchangeFeed.HourBefore(partway, 2));
        Assert.EndsWith(newest.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExchangeFeed.Where(newest), StringComparison.Ordinal);
    }
}
