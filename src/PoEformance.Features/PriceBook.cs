using System.Text.Json;

namespace PoEformance.Features;

/// <summary>Which shape of poe.ninja answer this is - they are keyed differently.</summary>
public enum PriceKind
{
    /// <summary>The fungible economy: currency, fragments, essences, runes. Keyed by art.</summary>
    Exchange,

    /// <summary>Individually listed gear: uniques, tablets. Keyed by name.</summary>
    Listed,
}

/// <summary>
/// What things are worth, in Exalted Orbs.
/// </summary>
/// <remarks>
/// PARSING ONLY - nothing here goes near a network, so all of it is checked against real
/// captured answers rather than against what a fetcher was told to return.
///
/// KEYED BY ART FOR THE FUNGIBLE HALF, and that is the good trick: every exchange line carries
/// the file name of its picture, and an item in memory carries the path of the same picture.
/// So currency joins up without knowing what anything is CALLED - which means it works on a
/// client running in any language, and needs no name table to ship or keep current. Uniques
/// have no such handle and are keyed by name.
///
/// EVERYTHING IS IN EXALTED. The API quotes in Divine, so a rate has to be applied, and that
/// rate is the single most dangerous number here - see <see cref="Rate"/>.
/// </remarks>
public sealed class PriceBook
{
    /// <summary>
    /// How many listings a gear line needs before its price is believed.
    /// </summary>
    /// <remarks>
    /// NOT A ROUND NUMBER PULLED FROM THE AIR. poe.ninja's raw API carries price-fixed lines its
    /// own website hides, and measured live on "Runes of Aldur" the two populations barely
    /// overlap: the real lines run to hundreds and thousands of listings (median 813 to 5190
    /// across the three unique types), while the absurd ones sit at 9 to 53. Redbeak - a
    /// starter-tier sword - was quoted at 4225 Divine off NINE listings.
    ///
    /// At a gate of 5, all of them survive. At 100, the most expensive surviving unique weapon
    /// drops from 4225 Divine to 3. So a hundred is where the cliff is, on real data, across
    /// three item types.
    ///
    /// WHAT IT COSTS: a genuinely rare chase item legitimately has few listings and is dropped
    /// with them. That is the trade being made on purpose - an item with no price shows no
    /// price, and an item with a wrong price is believed.
    /// </remarks>
    public const int EnoughListings = 100;

    /// <summary>How much Divine has to have changed hands before an exchange line is believed.</summary>
    /// <remarks>
    /// The same idea for the fungible half, where the API gives traded volume instead of a
    /// listing count. Live example: Blacksmith's Whetstone quotes a price off 0.07 Divine of
    /// trade, which is an asking price rather than a market.
    /// </remarks>
    public const double EnoughVolume = 1.0;

    /// <summary>A sanity ceiling on the rate, past which the answer is not a rate.</summary>
    public const double SilliestRate = 100_000;

    private readonly Dictionary<string, double> _byArt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _byName = new(StringComparer.Ordinal);

    /// <summary>How many Exalted one Divine is worth.</summary>
    /// <remarks>
    /// TAKEN ONLY FROM AN ANSWER THAT HAD PRICES IN IT. Every response carries a rate, including
    /// the ones carrying nothing else - and those are stale. Measured live on Standard: the
    /// exchange answer said 581 and the unique-weapons answer, which had ZERO lines, said 932.9.
    /// Letting the last answer win is a sixty per cent error in every converted price, and it
    /// shows up only on the leagues where one family has no data.
    /// </remarks>
    public double Rate { get; private set; }

    /// <summary>How many prices are in here.</summary>
    public int Count => _byArt.Count + _byName.Count;

    /// <summary>How many lines were dropped for being too thin to believe.</summary>
    public int Thin { get; private set; }

    /// <summary>Whether anything can be priced at all.</summary>
    public bool Ready => Rate > 0 && Count > 0;

    /// <summary>
    /// Reads one poe.ninja answer into the book.
    /// </summary>
    /// <returns>How many prices it added.</returns>
    public int Add(PriceKind kind, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using JsonDocument answer = JsonDocument.Parse(json);
            JsonElement root = answer.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            bool any = root.TryGetProperty("lines", out JsonElement lines)
                       && lines.ValueKind == JsonValueKind.Array
                       && lines.GetArrayLength() > 0;

            // Before the lines, because converting them needs it - and only from an answer that
            // actually had some.
            if (any && Rated(root) is { } rate)
            {
                Rate = rate;
            }

            if (!any || Rate <= 0)
            {
                return 0;
            }

            return kind == PriceKind.Exchange ? Fungible(root, lines) : Gear(lines);
        }
        catch (JsonException)
        {
            // A truncated answer, or an error page where JSON was expected. No prices from it,
            // and nothing that should end a session.
            return 0;
        }
    }

    /// <summary>The Divine-to-Exalted rate an answer carries, if it has a believable one.</summary>
    private static double? Rated(JsonElement root)
        => root.TryGetProperty("core", out JsonElement core)
           && core.ValueKind == JsonValueKind.Object
           && core.TryGetProperty("rates", out JsonElement rates)
           && rates.ValueKind == JsonValueKind.Object
           && rates.TryGetProperty("exalted", out JsonElement exalted)
           && exalted.TryGetDouble(out double rate)
           && rate is > 0 and < SilliestRate
            ? rate
            : null;

    /// <summary>The fungible half: lines keyed by id, and the pictures that name those ids.</summary>
    private int Fungible(JsonElement root, JsonElement lines)
    {
        // The id-to-picture table, which is what turns a line into something an item can be
        // matched against.
        var art = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement one in items.EnumerateArray())
            {
                if (Text(one, "id") is { Length: > 0 } id && ArtOf(Text(one, "image")) is { Length: > 0 } named)
                {
                    art[id] = named;
                }
            }
        }

        var added = 0;
        foreach (JsonElement line in lines.EnumerateArray())
        {
            if (Text(line, "id") is not { Length: > 0 } id
                || !art.TryGetValue(id, out string? picture)
                || Number(line, "primaryValue") is not { } divine
                || divine <= 0)
            {
                continue;
            }

            // Fails OPEN when the field is missing: a schema change should cost the gate, not
            // every price in the book.
            if (Number(line, "volumePrimaryValue") is { } volume && volume < EnoughVolume)
            {
                Thin++;
                continue;
            }

            _byArt[picture] = divine * Rate;
            added++;
        }

        return added;
    }

    /// <summary>The listed half: uniques and tablets, keyed by name.</summary>
    private int Gear(JsonElement lines)
    {
        var added = 0;
        foreach (JsonElement line in lines.EnumerateArray())
        {
            if (Text(line, "name") is not { Length: > 0 } name
                || Number(line, "primaryValue") is not { } divine
                || divine <= 0)
            {
                continue;
            }

            if (Number(line, "listingCount") is { } listings && listings < EnoughListings)
            {
                Thin++;
                continue;
            }

            // A name can appear more than once - the same unique at different rolls. The
            // CHEAPEST is kept, because that is the one somebody is actually offered.
            string key = Tidy(name);
            double worth = divine * Rate;
            if (!_byName.TryGetValue(key, out double already) || worth < already)
            {
                _byName[key] = worth;
            }

            added++;
        }

        return added;
    }

    /// <summary>What one of a thing is worth in Exalted, or null when nothing is known about it.</summary>
    /// <param name="artPath">The item's own art path, as it carries it.</param>
    /// <param name="name">What it is called, for the things that have no art handle.</param>
    public double? Worth(string? artPath, string? name = null)
    {
        if (ArtOf(artPath) is { Length: > 0 } picture && _byArt.TryGetValue(picture, out double byArt))
        {
            return byArt;
        }

        return name is { Length: > 0 } && _byName.TryGetValue(Tidy(name), out double byName) ? byName : null;
    }

    /// <summary>
    /// The picture's own name, out of whatever kind of path it came in.
    /// </summary>
    /// <remarks>
    /// The join between the two sides. An item says
    /// <c>Art/2DItems/Currency/CurrencyAddModToRare.dds</c> and poe.ninja says
    /// <c>/gen/image/…/CurrencyAddModToRare.png</c>; what they agree on is the last part with
    /// the extension off. Lower-cased, because the two do not agree on case.
    /// </remarks>
    public static string ArtOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string name = path.Replace('\\', '/');

        int query = name.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            name = name[..query];
        }

        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        int dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }

    /// <summary>A name in the one spelling both sides can agree on.</summary>
    private static string Tidy(string name)
    {
        Span<char> kept = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;

        foreach (char one in name)
        {
            if (char.IsAsciiLetterOrDigit(one))
            {
                kept[length++] = char.ToLowerInvariant(one);
            }
        }

        return new string(kept[..length]);
    }

    private static string Text(JsonElement of, string named)
        => of.TryGetProperty(named, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? string.Empty
            : string.Empty;

    private static double? Number(JsonElement of, string named)
        => of.TryGetProperty(named, out JsonElement found)
           && found.ValueKind == JsonValueKind.Number
           && found.TryGetDouble(out double value)
            ? value
            : null;
}
