using System.Globalization;
using System.Text.Json;

namespace PoEformance.Features;

/// <summary>
/// What one currency pair actually traded for, in one hour, on the game's own exchange.
/// </summary>
/// <remarks>
/// THREE NUMBERS, NOT ONE, and which of them is right depends on the question being asked. The
/// game shows both sides of the book in its Currency Exchange window, and this record is the
/// same two numbers plus what really cleared between them:
///
///   Divine for Exalted, Standard, one hour:
///     Bid    260 ex   - what the book pays for a Divine        ("260.02 : 1" in the game)
///     Ask    602 ex   - what the book charges for one          ("1 : 601.85")
///     Traded 473 ex   - volume-weighted over 16 Divine actually swapped
///
/// A HOARD IS WORTH THE BID. Valuing what somebody holds at the price they would have to PAY
/// for it is flattery, not accounting - and in Standard the difference is a factor of 2.3,
/// because a thin market has a wide spread. In an active league the same pair sits at 350 / 379
/// and the choice barely shows, which is exactly why it has to be made deliberately rather than
/// discovered later.
/// </remarks>
/// <param name="Bid">Units of the second currency received for one of the first.</param>
/// <param name="Ask">Units of the second currency paid for one of the first.</param>
/// <param name="Traded">Volume-weighted rate of what actually changed hands.</param>
/// <param name="Volume">
/// How many of the FIRST currency changed hands. That side rather than the other because it is
/// what <paramref name="Traded"/> is quoted per, so the two multiply back into the second side's
/// volume - which is what makes a blend across hours volume-weighted rather than a mean of
/// means, where a one-orb hour would count as heavily as a thousand-orb one.
/// </param>
public readonly record struct ExchangeRate(double Bid, double Ask, double Traded, double Volume)
{
    /// <summary>Whether anything is here at all.</summary>
    public bool Known => Bid > 0 || Ask > 0 || Traded > 0;
}

/// <summary>
/// The game's own Currency Exchange, as GGG publishes it.
/// </summary>
/// <remarks>
/// WHY THIS RATHER THAN A TRADE QUERY. Seven rounds went into asking pathofexile.com's trade
/// apis what a Divine costs, and every answer was wrong in a way that looked right: the search
/// endpoint sorts ascending, so its first result is whatever scam or typo is cheapest that
/// minute; the exchange endpoint returned one listing for the most traded pair in the game. This
/// feed is not listings at all. It is EXECUTED TRADES, summarised hourly by the game itself.
///
/// AND IT COSTS NOTHING TO ASK. No sign-in, no browser, no Cloudflare, no rate-limit budget to
/// nurse - a plain GET on a CDN, one request per hour for every market in every league. The
/// whole reason the trade side needed a hidden WebView2 window does not apply here.
///
/// MARKETS ARE KEYED BY METADATA PATH, which is the same string this tool already reads out of
/// the game's memory for every item it inspects. No id table, no naming to keep in step with
/// somebody else's spelling - the join is free.
///
/// THE CURRENT HOUR IS ALWAYS EMPTY and completed hours never change, so an hour once fetched
/// can be kept for as long as it is useful.
/// </remarks>
public static class ExchangeFeed
{
    /// <summary>Where the hourly digests live.</summary>
    public const string Host = "https://web.poecdn.com/api/currency-exchange/poe2/";

    /// <summary>The Divine Orb, as the game's own files spell it.</summary>
    /// <remarks>
    /// Named from what it DOES rather than what it is called, which is how the game's metadata
    /// works throughout: a Divine Orb rerolls the values of an item's modifiers.
    /// </remarks>
    public const string Divine = "Metadata/Items/Currency/CurrencyModValues";

    /// <summary>The Exalted Orb - the one that adds a modifier to a rare.</summary>
    public const string Exalted = "Metadata/Items/Currency/CurrencyAddModToRare";

    /// <summary>
    /// How much of the scarcer side has to have moved before an hour is trusted on its own.
    /// </summary>
    /// <remarks>
    /// Standard trades single-digit Divine in an hour - 16, 8, none, none, 1, 6 across one
    /// afternoon - so a single hour there swings between 260 and 473 on the strength of one
    /// large order. An active league moves two and a half thousand an hour and holds 363 to
    /// within a percent. Blending until this much has moved gives the liquid pair the freshest
    /// possible number and the thin one a believable one, rather than forcing both to the same
    /// compromise.
    /// </remarks>
    public const double Liquid = 20;

    /// <summary>The hour whose digest is complete, counting back from a moment.</summary>
    /// <remarks>
    /// The CURRENT hour is always empty - it is still being filled - so counting back one is the
    /// newest that can say anything. <paramref name="back"/> walks further for the blend.
    /// </remarks>
    public static long HourBefore(DateTimeOffset now, int back = 1)
        => (now.ToUnixTimeSeconds() / 3600 - back) * 3600;

    /// <summary>The address of one hour's digest.</summary>
    public static string Where(long hour)
        => Host + hour.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads one hour's digest and returns what one pair did in it.
    /// </summary>
    /// <remarks>
    /// LOWEST AND HIGHEST ARE THE TWO SIDES OF THE BOOK, not a range of what cleared. That is
    /// worth stating because a published reading of this feed says otherwise, and the game
    /// settles it: its Currency Exchange window showed "1 : 601.85" for buying a Divine and
    /// "260.02 : 1" for selling one, in the same league and hour this feed reported
    /// lowest_ratio 1:602 and highest_ratio 1:260. The names read backwards because the ratio is
    /// counted in the OTHER direction - fewest Divine per Exalted is the most Exalted per Divine.
    /// </remarks>
    /// <param name="json">One hour's digest, as the CDN sent it.</param>
    /// <param name="league">Which league to read. Every league shares the file.</param>
    /// <param name="first">The metadata path of the currency being priced.</param>
    /// <param name="second">The metadata path it is being priced in.</param>
    public static ExchangeRate Read(string? json, string league, string first, string second)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(league))
        {
            return default;
        }

        try
        {
            using JsonDocument feed = JsonDocument.Parse(json);
            if (!feed.RootElement.TryGetProperty("markets", out JsonElement markets)
                || markets.ValueKind != JsonValueKind.Array)
            {
                return default;
            }

            foreach (JsonElement market in markets.EnumerateArray())
            {
                if (!Is(market, league, first, second))
                {
                    continue;
                }

                double bid = Side(market, "highest_ratio", first, second);
                double ask = Side(market, "lowest_ratio", first, second);
                double traded = Side(market, "volume_traded", first, second);
                double moved = Moved(market, first, second);

                return new ExchangeRate(bid, ask, traded, moved);
            }
        }
        catch (JsonException)
        {
            // A digest that will not parse is one hour missing, not a broken tool. The blend
            // walks further back and the caller never learns the difference.
            return default;
        }

        return default;
    }

    /// <summary>
    /// Blends hours, newest first, until enough has moved to believe the answer.
    /// </summary>
    /// <remarks>
    /// STOPS AS SOON AS IT CAN. A liquid pair is priced off the newest hour alone rather than
    /// smeared across six, and a thin one keeps accumulating so that a single large order does
    /// not become the rate. The <see cref="ExchangeRate.Bid"/> and <see cref="ExchangeRate.Ask"/>
    /// of a blend are the WIDEST seen across the hours used - a spread narrower than the hours
    /// it covers would be a claim nobody made.
    /// </remarks>
    /// <param name="hours">Each hour's digest, newest first. Nulls are skipped.</param>
    public static ExchangeRate Blend(
        IReadOnlyList<string?> hours, string league, string first, string second)
    {
        ArgumentNullException.ThrowIfNull(hours);

        double firstSide = 0, secondSide = 0, bid = 0, ask = 0, moved = 0;

        foreach (string? hour in hours)
        {
            ExchangeRate one = Read(hour, league, first, second);
            if (!one.Known)
            {
                continue;
            }

            // THE BOOK IS A STATE, NOT AN AVERAGE. Bid and ask are what the exchange is offering
            // at a moment; blending them across hours would quote a spread nobody ever showed.
            // So the newest hour that had a book keeps it, and only what TRADED is blended.
            if (bid == 0 && ask == 0)
            {
                bid = one.Bid;
                ask = one.Ask;
            }

            // Traded is a RATE, and rates do not average - the volumes behind them do. Carrying
            // the two sides and dividing at the end is what makes this volume-weighted.
            firstSide += one.Volume;
            secondSide += one.Volume * one.Traded;
            moved += one.Volume;

            if (moved >= Liquid)
            {
                break;
            }
        }

        double traded = firstSide > 0 ? secondSide / firstSide : 0;
        return new ExchangeRate(bid, ask, traded, moved);
    }

    private static bool Is(JsonElement market, string league, string first, string second)
    {
        if (!market.TryGetProperty("league", out JsonElement named)
            || named.ValueKind != JsonValueKind.String
            || !string.Equals(named.GetString(), league, StringComparison.Ordinal))
        {
            return false;
        }

        if (!market.TryGetProperty("market_pair", out JsonElement pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() != 2)
        {
            return false;
        }

        var both = new List<string>(2);
        foreach (JsonElement side in pair.EnumerateArray())
        {
            if (side.ValueKind == JsonValueKind.String && side.GetString() is { } path)
            {
                both.Add(path);
            }
        }

        // The pair's own order is arbitrary, so both arrangements are the same market.
        return both.Count == 2
            && ((both[0] == first && both[1] == second) || (both[0] == second && both[1] == first));
    }

    /// <summary>Units of <paramref name="second"/> per one <paramref name="first"/>.</summary>
    private static double Side(JsonElement market, string field, string first, string second)
    {
        if (!market.TryGetProperty(field, out JsonElement pair)
            || pair.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        double a = Number(pair, first);
        double b = Number(pair, second);
        return a > 0 && b > 0 ? b / a : 0;
    }

    /// <summary>How many of the first currency moved, when both sides really did.</summary>
    /// <remarks>
    /// BOTH SIDES OR NEITHER. A market reporting volume on one side only did not trade - it is
    /// half a record - and letting it through would divide by a zero that is not an error.
    /// </remarks>
    private static double Moved(JsonElement market, string first, string second)
    {
        if (!market.TryGetProperty("volume_traded", out JsonElement pair)
            || pair.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        double a = Number(pair, first);
        double b = Number(pair, second);
        return a > 0 && b > 0 ? a : 0;
    }

    private static double Number(JsonElement holder, string name)
        => holder.TryGetProperty(name, out JsonElement found)
           && found.ValueKind == JsonValueKind.Number
           && found.TryGetDouble(out double how)
            ? how
            : 0;
}
