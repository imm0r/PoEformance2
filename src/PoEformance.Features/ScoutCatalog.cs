using System.Text.Json;

namespace PoEformance.Features;

/// <summary>One day in a currency's price history.</summary>
/// <param name="Day">The day it covers, as the site dated it.</param>
/// <param name="Exalted">What one was worth that day.</param>
/// <param name="Quantity">
/// How much traded. The reason a day can be told from HALF a day - see
/// <see cref="ScoutEntry.Settled"/>, which exists entirely because of it.
/// </param>
public readonly record struct ScoutDay(DateOnly Day, double Exalted, double Quantity);

/// <summary>One currency, as the index knows it.</summary>
/// <param name="Path">Its metadata path - the same key the exchange and the game both use.</param>
/// <param name="Called">What to show it as.</param>
/// <param name="Exalted">What it is worth now.</param>
/// <param name="Days">Its price by day, oldest first.</param>
public readonly record struct ScoutEntry(
    string Path, string Called, double Exalted, IReadOnlyList<ScoutDay> Days)
{
    /// <summary>Whether there is a price at all.</summary>
    public bool Known => Exalted > 0;

    /// <summary>
    /// The days that are whole ones, which is not all of them.
    /// </summary>
    /// <remarks>
    /// THE NEWEST POINT IS TODAY SO FAR, and reading it as a day is how a trend lies. Divine in
    /// Standard showed 194.7 on 31,903 traded where the six days before it ran 245 to 354 on
    /// about 1.2 million each - a forty percent crash that is really nine o'clock in the morning.
    /// A day whose volume is a small fraction of the days around it has not finished happening.
    /// </remarks>
    public IReadOnlyList<ScoutDay> Settled
    {
        get
        {
            if (Days.Count < 2)
            {
                return Days;
            }

            ScoutDay newest = Days[^1];
            double typical = Middle([.. Days.Take(Days.Count - 1).Select(day => day.Quantity)]);

            return typical > 0 && newest.Quantity < typical * PartOfADay
                ? [.. Days.Take(Days.Count - 1)]
                : Days;
        }
    }

    /// <summary>
    /// The price to compare anything against - the newest FINISHED day.
    /// </summary>
    /// <remarks>
    /// NOT CurrentPrice, and finding that out cost a test. The index's current price is today so
    /// far, and today so far is a partial day: it read 194.7 for a Divine in the captured hour
    /// where the finished days ran 245 to 354 and the game's own exchange was paying 260. Used as
    /// a second opinion it declared the exchange wrong by a third, which is the opposite of what
    /// a corroborating source is for.
    ///
    /// The NEWEST finished day rather than a median of them, because this is a cross-check
    /// against something an hour old and the closest real day is the fairest thing to hold it to.
    /// Falls back to the current price when there is no finished day at all - one imperfect
    /// number beats refusing to answer.
    /// </remarks>
    public double Steady
    {
        get
        {
            IReadOnlyList<ScoutDay> days = Settled;
            return days.Count > 0 && days[^1].Exalted > 0 ? days[^1].Exalted : Exalted;
        }
    }

    /// <summary>
    /// How the price has moved across the settled days, as a fraction.
    /// </summary>
    /// <remarks>
    /// Null rather than zero when there is nothing to compare, because "flat" and "unknown" are
    /// different answers and a sparkline drawn from the second one is a lie about the first.
    /// </remarks>
    public double? Trend
    {
        get
        {
            IReadOnlyList<ScoutDay> days = Settled;
            return days.Count >= 2 && days[0].Exalted > 0
                ? (days[^1].Exalted - days[0].Exalted) / days[0].Exalted
                : null;
        }
    }

    /// <summary>What fraction of a normal day's volume still counts as a finished day.</summary>
    /// <remarks>
    /// Half. Generous on purpose: a genuinely quiet day is a real day and dropping it would
    /// shorten every trend by one, while a partial day is usually a small fraction rather than
    /// a near miss - the case that prompted this was three percent of normal.
    /// </remarks>
    public const double PartOfADay = 0.5;

    private static double Middle(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        values.Sort();
        return values[values.Count / 2];
    }
}

/// <summary>
/// The aggregated index, for the two things the exchange cannot give.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR, now that prices come from the game's own exchange: history, and a second
/// opinion.
///
/// HISTORY, because the exchange feed is hourly digests of a moment - seven days of it is a
/// hundred and sixty-eight requests, and the trend a person actually wants is daily anyway. This
/// serves seven daily points per currency, with the volume behind each, in one request.
///
/// A SECOND OPINION, because an arbitrage route computed from one source alone cannot be
/// checked. A single stale fill in an hourly digest produces a route showing three hundred
/// thousand percent - measured, not imagined - and no amount of volume filtering removes it,
/// because the trade really did happen at that price. What removes it is asking somebody else
/// what the thing is worth and refusing to believe a rate that disagrees.
///
/// IT JOINS ON THE METADATA PATH, like everything else here: the index publishes BaseItemTypeId
/// and that is the same string the game hands this tool for every item it reads.
/// </remarks>
public static class ScoutCatalog
{
    /// <summary>Where the currency catalogue lives.</summary>
    public const string Host = "https://api.poe2scout.com/poe2/Leagues/";

    /// <summary>How many days of history to ask for.</summary>
    public const int Days = 7;

    /// <summary>The address of one league's currency catalogue.</summary>
    public static string Where(string league)
        => $"{Host}{Uri.EscapeDataString(league)}/Currencies/ByCategory"
           + $"?category=currency&perPage=250&dataPoints={Days}";

    /// <summary>Reads a catalogue, keyed by metadata path.</summary>
    public static IReadOnlyDictionary<string, ScoutEntry> Read(string? json)
    {
        var found = new Dictionary<string, ScoutEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return found;
        }

        try
        {
            using JsonDocument page = JsonDocument.Parse(json);
            if (!page.RootElement.TryGetProperty("Items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                if (Text(item, "BaseItemTypeId") is not { Length: > 0 } path)
                {
                    continue;
                }

                found[path] = new ScoutEntry(
                    path,
                    Text(item, "Text") ?? path,
                    Number(item, "CurrentPrice"),
                    History(item));
            }
        }
        catch (JsonException)
        {
            return found;
        }

        return found;
    }

    private static IReadOnlyList<ScoutDay> History(JsonElement item)
    {
        if (!item.TryGetProperty("PriceLogs", out JsonElement logs)
            || logs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var days = new List<ScoutDay>(logs.GetArrayLength());
        foreach (JsonElement log in logs.EnumerateArray())
        {
            // A null entry is a day with no trades, and the site pads with them. Dropped rather
            // than carried as a zero, which would draw a sparkline through the floor.
            if (log.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double price = Number(log, "Price");
            if (price <= 0 || Text(log, "Time") is not { Length: > 0 } when
                || !DateTimeOffset.TryParse(when, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset at))
            {
                continue;
            }

            days.Add(new ScoutDay(DateOnly.FromDateTime(at.UtcDateTime), price, Number(log, "Quantity")));
        }

        // OLDEST FIRST, whatever order they arrived in. A trend is a direction, and a direction
        // read off a list sorted the other way is exactly backwards - which would show every
        // rising currency as falling.
        days.Sort((a, b) => a.Day.CompareTo(b.Day));
        return days;
    }

    private static string? Text(JsonElement holder, string name)
        => holder.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static double Number(JsonElement holder, string name)
        => holder.TryGetProperty(name, out JsonElement found)
           && found.ValueKind == JsonValueKind.Number
           && found.TryGetDouble(out double how)
            ? how
            : 0;
}
