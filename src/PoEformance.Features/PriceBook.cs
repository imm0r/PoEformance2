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
    private readonly Dictionary<string, double> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// The fungible lines under picture AND name, for the pictures more than one thing draws.
    /// </summary>
    /// <remarks>
    /// BECAUSE THE PICTURE IS NOT A KEY IN PATH OF EXILE 2. Its Greater and Perfect variants draw
    /// the SAME art as the orb they upgrade, and they are not worth the same - measured live on
    /// Standard, all five of these collide:
    ///
    ///   currencyupgrademagictorare   Regal Orb 0.28 ex  |  Perfect Regal Orb 454.20 ex
    ///   currencyupgradetomagic       Transmutation 0.05 |  Perfect Transmutation 13.76
    ///   currencyaddmodtorare         Exalted 1.00       |  Greater 50.33  |  Perfect 151.39
    ///   currencyaddmodtomagic        Augmentation 0.10  |  Greater 1.00   |  Perfect 26.06
    ///   currencyrerollrare           Chaos 116.23       |  Greater 247.77 |  Perfect 876.61
    ///
    /// Keyed by picture alone the last line read simply overwrote the others, so a stash of plain
    /// Transmutation Orbs priced at the PERFECT rate: 3,312 of them came to 45.6k Exalted instead
    /// of 172. That is how a purse of 97 Divine reported itself as 2,700.
    ///
    /// THE NAME IS STILL LANGUAGE-INDEPENDENT, which is the property art-keying was chosen for.
    /// It is not what the client painted: it is what the shipped table resolved from the item's
    /// metadata path, in English, exactly as poe.ninja spells it.
    /// </remarks>
    private readonly Dictionary<string, double> _byArtName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which currencies draw each picture. More than one means the picture is not a price.
    /// </summary>
    /// <remarks>
    /// COUNTED FROM THE ITEM TABLE, NOT FROM THE SURVIVING LINES, and that distinction is the
    /// whole of it. Measured live: the plain Orb of Transmutation trades 0.006 Divine a day and
    /// is thrown out by the volume gate, while the Perfect variant that draws the same picture
    /// survives it. Counting the lines that got through would therefore find ONE claimant, call
    /// the picture unambiguous, and hand every plain Transmutation the Perfect price - which is
    /// exactly the wrong answer the gate was supposed to make impossible.
    ///
    /// A gated-out line still proves the picture is shared. So the claim is registered when the
    /// item is seen, before anything decides whether its price is believable.
    ///
    /// A SET OF IDS rather than a count, so calling <see cref="Add"/> for several types cannot
    /// count one currency twice and invent an ambiguity that is not there.
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> _drawnBy = new(StringComparer.Ordinal);

    /// <summary>How many Exalted one Divine is worth.</summary>
    /// <remarks>
    /// TAKEN ONLY FROM AN ANSWER THAT HAD PRICES IN IT. Every response carries a rate, including
    /// the ones carrying nothing else - and those are stale. Measured live on Standard: the
    /// exchange answer said 581 and the unique-weapons answer, which had ZERO lines, said 932.9.
    /// Letting the last answer win is a sixty per cent error in every converted price, and it
    /// shows up only on the leagues where one family has no data.
    /// </remarks>
    public double Rate { get; private set; }

    /// <summary>
    /// How many prices are in here.
    /// </summary>
    /// <remarks>
    /// The currency ids are NOT counted: they are the same exchange lines under a second key,
    /// and counting them would report twice as many prices as were learned.
    /// </remarks>
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
        // matched against - and the id-to-NAME table beside it, which is what tells apart the
        // things that draw the same picture. See _byArtName.
        var art = new Dictionary<string, string>(StringComparer.Ordinal);
        var called = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement one in items.EnumerateArray())
            {
                if (Text(one, "id") is not { Length: > 0 } id)
                {
                    continue;
                }

                if (ArtOf(Text(one, "image")) is { Length: > 0 } named)
                {
                    art[id] = named;

                    // HERE, before any gate has had a say - see _drawnBy.
                    if (!_drawnBy.TryGetValue(named, out HashSet<string>? drawn))
                    {
                        _drawnBy[named] = drawn = new HashSet<string>(StringComparer.Ordinal);
                    }

                    drawn.Add(id);
                }

                if (Text(one, "name") is { Length: > 0 } spelt)
                {
                    called[id] = spelt;
                }
            }
        }

        var added = 0;
        foreach (JsonElement line in lines.EnumerateArray())
        {
            if (Text(line, "id") is not { Length: > 0 } id
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

            double worth = divine * Rate;

            // The line's own id as well as its picture. It is the same price under a second
            // key, for the one caller that has a currency's NAME in the site's spelling and no
            // item to look at - see Quoted.
            _byId[id] = worth;

            // The picture is what an item can be matched against, and a line without one in the
            // table is a price nothing here can ever ask for.
            if (art.TryGetValue(id, out string? picture))
            {
                _byArt[picture] = worth;

                if (called.TryGetValue(id, out string? spelt))
                {
                    _byArtName[Named(picture, spelt)] = worth;
                }

                added++;
            }
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
        // The ambiguity guard is here rather than only in Fungible, because this is the older
        // door into the same table: a picture that several things draw must not answer through
        // either of them. See _byArtName for what it costs to get this wrong.
        if (ArtOf(artPath) is { Length: > 0 } picture
            && !Shared(picture)
            && _byArt.TryGetValue(picture, out double byArt))
        {
            return byArt;
        }

        return name is { Length: > 0 } && _byName.TryGetValue(Tidy(name), out double byName) ? byName : null;
    }

    /// <summary>
    /// What one of a fungible item is worth, told apart by name where the picture is shared.
    /// </summary>
    /// <remarks>
    /// THE NAME IS TRIED FIRST and the picture only after, which is the way round that stays
    /// right as poe.ninja adds variants. A picture nothing else draws answers on its own exactly
    /// as it always did; a picture two things draw answers only to the one that also matches by
    /// name, and to NOTHING otherwise. Nothing is the correct answer there: an unpriced stack is
    /// visible in the unpriced count and understates a total, where guessing between two prices
    /// that differ by four hundred times overstates it silently.
    /// </remarks>
    /// <param name="artPath">The item's own art path, as it carries it.</param>
    /// <param name="name">
    /// The item's base name out of the shipped table - English, resolved from its metadata path,
    /// and therefore the same on a client running in any language.
    /// </param>
    public double? Fungible(string? artPath, string? name)
    {
        if (ArtOf(artPath) is not { Length: > 0 } picture)
        {
            return null;
        }

        if (name is { Length: > 0 }
            && _byArtName.TryGetValue(Named(picture, name), out double exact))
        {
            return exact;
        }

        return !Shared(picture) && _byArt.TryGetValue(picture, out double one) ? one : null;
    }

    /// <summary>Whether more than one thing draws this picture, so the picture alone is not a price.</summary>
    public bool Shared(string? picture)
        => picture is { Length: > 0 }
           && _drawnBy.TryGetValue(picture, out HashSet<string>? drawn)
           && drawn.Count > 1;

    /// <summary>A picture and a name as one key.</summary>
    /// <remarks>
    /// A NUL between them rather than any printable separator, so no name containing the
    /// separator can be made to read as a different picture's key.
    /// </remarks>
    private static string Named(string picture, string name) => $"{picture}\0{name.Trim()}";

    /// <summary>
    /// What one of the currency an asking price is quoted in is worth, in Exalted.
    /// </summary>
    /// <param name="currency">The trade site's own id for it - <c>divine</c>, <c>chaos</c>.</param>
    /// <remarks>
    /// FOR PRICES THAT ARRIVE AS TEXT rather than as an item: a trade listing says "3 divine",
    /// and there is no picture to join on.
    ///
    /// EXALTED IS ONE BY DEFINITION - it is the unit everything here is in - and Divine comes
    /// from the rate, so the two currencies that carry nearly every listing convert even when
    /// the exchange half of a refresh came back thin.
    ///
    /// Everything else has to BE in the book, under the id poe.ninja gave its exchange line.
    /// The two vocabularies look like the same short slugs - "divine", "chaos", "whetstone" -
    /// but that is a join rather than an assumption: an id with no line behind it answers null,
    /// and the caller drops that listing. The cost of being wrong is a listing ignored, never a
    /// price invented.
    /// </remarks>
    public double? Quoted(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        string id = currency.Trim().ToLowerInvariant();
        return id switch
        {
            "exalted" or "exalt" or "ex" => 1.0,
            "divine" or "div" => Rate > 0 ? Rate : null,
            _ => _byId.TryGetValue(id, out double worth) ? worth : null,
        };
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

    /// <summary>What was kept from a previous session, and when.</summary>
    public readonly record struct Kept(PriceBook Book, DateTimeOffset When);

    /// <summary>
    /// Writes the book down, so the next session opens with prices rather than without.
    /// </summary>
    /// <remarks>
    /// The PRICES rather than the answers they came out of. The answers are megabytes and this
    /// is what was actually learned from them - and a book that has been through the gates
    /// cannot quietly un-gate itself when the thresholds change, because what was dropped is
    /// simply not in here.
    /// </remarks>
    public string Saved(string league)
    {
        var writing = new System.Text.Json.Nodes.JsonObject
        {
            ["league"] = league,
            ["when"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["rate"] = Rate,
        };

        var art = new System.Text.Json.Nodes.JsonObject();
        foreach ((string key, double worth) in _byArt)
        {
            art[key] = worth;
        }

        var named = new System.Text.Json.Nodes.JsonObject();
        foreach ((string key, double worth) in _byName)
        {
            named[key] = worth;
        }

        var currency = new System.Text.Json.Nodes.JsonObject();
        foreach ((string key, double worth) in _byId)
        {
            currency[key] = worth;
        }

        writing["art"] = art;
        writing["name"] = named;
        writing["currency"] = currency;
        return writing.ToJsonString();
    }

    /// <summary>
    /// Reads one back, when it is for the league being played.
    /// </summary>
    /// <returns>Null when it is for somewhere else, or is not one.</returns>
    /// <remarks>
    /// The league is checked HERE rather than by the caller, because prices from another league
    /// are not old - they are a different economy, and shown next to the right ones they look
    /// exactly as trustworthy.
    /// </remarks>
    public static Kept? Reopen(string? json, string league)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(league))
        {
            return null;
        }

        try
        {
            using JsonDocument saved = JsonDocument.Parse(json);
            JsonElement root = saved.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(Text(root, "league"), league, StringComparison.OrdinalIgnoreCase)
                || Number(root, "rate") is not { } rate
                || rate <= 0)
            {
                return null;
            }

            var book = new PriceBook();
            book.Rate = rate;   // reachable here: same type, and this is where a saved book is rebuilt
            Pour(root, "art", book._byArt);
            Pour(root, "name", book._byName);

            // Absent in a file written before this key existed, which costs the trade layer a
            // few currencies it can convert and nothing else - Pour simply finds nothing.
            Pour(root, "currency", book._byId);

            long when = Number(root, "when") is { } stamp ? (long)stamp : 0;
            return book.Ready ? new Kept(book, DateTimeOffset.FromUnixTimeSeconds(when)) : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Pour(JsonElement root, string named, Dictionary<string, double> into)
    {
        if (!root.TryGetProperty(named, out JsonElement found) || found.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty one in found.EnumerateObject())
        {
            if (one.Value.ValueKind == JsonValueKind.Number && one.Value.TryGetDouble(out double worth) && worth > 0)
            {
                into[one.Name] = worth;
            }
        }
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
