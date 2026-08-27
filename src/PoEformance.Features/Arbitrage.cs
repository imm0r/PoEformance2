namespace PoEformance.Features;

/// <summary>A three-trade loop that comes back with more than it started with.</summary>
/// <param name="Path">The currency the loop is around.</param>
/// <param name="Called">What to show it as.</param>
/// <param name="Through">The currency it routes through.</param>
/// <param name="Direct">What one sells for in Exalted, straight.</param>
/// <param name="Routed">And what it comes to through the middle.</param>
/// <param name="Carries">The thinnest leg's book depth - how much the route could actually move.</param>
public readonly record struct Route(
    string Path, string Called, string Through, double Direct, double Routed, double Carries)
{
    /// <summary>What the loop gains, as a fraction.</summary>
    public double Gain => Direct > 0 ? (Routed - Direct) / Direct : 0;
}

/// <summary>
/// Loops through the exchange that end up ahead, and the reasons to disbelieve most of them.
/// </summary>
/// <remarks>
/// WHAT THIS IS GUARDING AGAINST, measured rather than imagined. Run over one real hour of a
/// live league with no guard at all, this finds 247 routes and the best of them reads
/// +314,900 PERCENT. Filtering on traded volume does not remove it: the trade genuinely happened
/// at that price, and one stale fill in an hourly digest is enough to produce it. Filtering on
/// book DEPTH removes most, and still leaves +145% on Chaos Orb - the most traded item in the
/// game, where a real inefficiency of that size cannot exist.
///
/// SO THE DECIDING GUARD IS A SECOND OPINION. Every leg's rate has to agree with what an
/// independent index says the two currencies are worth. A rate that disagrees is not an
/// opportunity, it is a stale fill wearing one - and no amount of arithmetic on a single source
/// can tell those apart, which is the whole reason the reference this borrows from carries two.
///
/// THE MIDDLE IS CHOSEN BY LIQUIDITY, not by which route pays best. Sorting candidates by profit
/// selects for exactly the broken legs the guards exist to catch; sorting by depth selects for
/// the ones somebody could actually trade.
///
/// NOTHING HERE PLACES AN ORDER or tells anybody to. It is a reading of two public price
/// sources, of the same kind as the rest of this tool.
/// </remarks>
public static class Arbitrage
{
    /// <summary>How far a leg's rate may sit from the index before it is disbelieved.</summary>
    /// <remarks>
    /// A quarter, from the reference. Wide, because the feed is an hour of executed trades and
    /// the index is a daily aggregate - they are measuring slightly different things and a tight
    /// band would reject honest disagreement. Narrow enough that the six-figure fantasies fail it
    /// by three orders of magnitude.
    /// </remarks>
    public const double Tolerance = 0.25;

    /// <summary>How deep a leg's book has to be to count as tradeable.</summary>
    public const double Deep = 100;

    /// <summary>How much a loop has to gain before it is worth the name.</summary>
    /// <remarks>
    /// Two percent, from the reference. Below that the exchange's own rounding and the gap
    /// between one hour and the next explain it, and a list of one-percent routes is a list of
    /// noise with a heading.
    /// </remarks>
    public const double Worthwhile = 0.02;

    /// <summary>Every loop that survives all three guards, best first.</summary>
    /// <param name="pairs">The exchange.</param>
    /// <param name="index">A second opinion, by metadata path. Without it, nothing is returned.</param>
    public static IReadOnlyList<Route> Routes(
        ExchangePairs? pairs, IReadOnlyDictionary<string, ScoutEntry>? index)
    {
        // NO SECOND SOURCE, NO ROUTES. Not a degraded mode: a route computed from one source is
        // the thing this class exists to refuse, so offering it when the index is missing would
        // be the failure dressed as a fallback.
        if (pairs is null || index is null || index.Count == 0)
        {
            return [];
        }

        var found = new List<Route>();
        foreach (string path in pairs.Everything())
        {
            if (string.Equals(path, ExchangeFeed.Exalted, StringComparison.Ordinal))
            {
                continue;
            }

            ExchangeRate straight = pairs.Rate(path, ExchangeFeed.Exalted);
            if (!straight.Known || straight.Bid <= 0 || straight.Stock < Deep)
            {
                continue;
            }

            if (!Agrees(pairs, index, path, ExchangeFeed.Exalted))
            {
                continue;
            }

            Route best = default;
            foreach (string middle in ExchangePairs.Majors)
            {
                if (string.Equals(middle, path, StringComparison.Ordinal)
                    || string.Equals(middle, ExchangeFeed.Exalted, StringComparison.Ordinal))
                {
                    continue;
                }

                ExchangeRate first = pairs.Rate(path, middle);
                ExchangeRate second = pairs.Rate(middle, ExchangeFeed.Exalted);
                if (!first.Known || !second.Known || first.Bid <= 0 || second.Bid <= 0)
                {
                    continue;
                }

                double carries = Math.Min(first.Stock, second.Stock);
                if (carries < Deep)
                {
                    continue;
                }

                if (!Agrees(pairs, index, path, middle) || !Agrees(pairs, index, middle, ExchangeFeed.Exalted))
                {
                    continue;
                }

                double routed = first.Bid * second.Bid;
                if ((routed - straight.Bid) / straight.Bid < Worthwhile)
                {
                    continue;
                }

                // BY DEPTH, NOT BY PROFIT. Sorting candidates by what they pay selects for the
                // broken legs; sorting by what they could move selects for the tradeable ones.
                if (carries <= best.Carries)
                {
                    continue;
                }

                best = new Route(
                    path,
                    index.TryGetValue(path, out ScoutEntry named) ? named.Called : Short(path),
                    middle,
                    straight.Bid,
                    routed,
                    carries);
            }

            if (best.Carries > 0)
            {
                found.Add(best);
            }
        }

        return [.. found.OrderByDescending(route => route.Gain)];
    }

    /// <summary>
    /// Whether the exchange and the index tell the same story about one pair.
    /// </summary>
    /// <remarks>
    /// The index quotes everything in Exalted, so what it implies for a pair is one side's price
    /// divided by the other's. A leg the two sources disagree about is not usable evidence of
    /// anything, whichever of them turns out to be right.
    ///
    /// UNKNOWN IS NOT AGREEMENT. A pair the index has never heard of fails this, because the
    /// whole point is corroboration and silence corroborates nothing.
    /// </remarks>
    public static bool Agrees(
        ExchangePairs? pairs,
        IReadOnlyDictionary<string, ScoutEntry>? index,
        string from,
        string to)
    {
        if (pairs is null || index is null)
        {
            return false;
        }

        ExchangeRate rate = pairs.Rate(from, to);
        if (!rate.Known || rate.Bid <= 0)
        {
            return false;
        }

        if (!index.TryGetValue(from, out ScoutEntry one) || !index.TryGetValue(to, out ScoutEntry other)
            || one.Steady <= 0 || other.Steady <= 0)
        {
            return false;
        }

        // STEADY, NOT CURRENT. The index's current price is today so far, and today so far is a
        // partial day - see ScoutEntry.Steady, which exists because comparing against it once
        // declared the game's own exchange wrong by a third.
        double implied = one.Steady / other.Steady;
        if (implied <= 0)
        {
            return false;
        }

        // Compared as a RATIO rather than a difference, because these span six orders of
        // magnitude: a quarter of a Mirror and a quarter of a Wisdom Scroll are not comparable
        // quantities, but "within a quarter of each other" is the same test for both.
        double apart = Math.Max(rate.Bid, implied) / Math.Min(rate.Bid, implied);
        return apart - 1 <= Tolerance;
    }

    private static string Short(string path)
        => path[(path.LastIndexOf('/') + 1)..];
}
