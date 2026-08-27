using System.Text.Json;

namespace PoEformance.Features;

/// <summary>What one currency came out worth, and how that was arrived at.</summary>
/// <param name="Exalted">Its worth in Exalted Orbs, on the selling side of the book.</param>
/// <param name="Through">
/// The currency the value was routed through, or empty when the pair traded against Exalted
/// directly. Kept because a two-leg valuation is a weaker claim than a one-leg one, and a
/// reader deserves to know which they are looking at.
/// </param>
/// <param name="Volume">The thinnest leg's traded volume - how much of a market said this.</param>
/// <param name="Stock">
/// The thinnest leg's BOOK depth - how much the price could actually move, as opposed to how
/// much moved already. Carried here rather than looked up again by whoever draws it: the legs
/// are known at the moment the route is chosen and not afterwards, and a caller asking the
/// direct Exalted market for the depth behind a ROUTED price gets a dash on nine rows in ten.
/// </param>
public readonly record struct Valuation(double Exalted, string Through, double Volume, double Stock = 0)
{
    /// <summary>Whether a price was found at all.</summary>
    public bool Known => Exalted > 0;

    /// <summary>Whether the pair traded against Exalted itself, with nothing in between.</summary>
    public bool Direct => Known && Through.Length == 0;
}

/// <summary>
/// Every currency in one league, valued in Exalted from the game's own exchange.
/// </summary>
/// <remarks>
/// WHY A GRAPH AND NOT A LOOKUP. In an active league almost everything trades against Exalted
/// directly - 512 of 571 currencies in one measured hour - and a plain table would do. In
/// STANDARD it is 29 of 163. The rest trade against Chaos, or Divine, or each other, and a
/// direct-only reading would call eighty percent of somebody's stash unpriceable. So a currency
/// with no Exalted market of its own is valued through one hop, and the hop is real: both legs
/// are trades that actually happened, in the same hours.
///
/// ONE HOP AND NO MORE. Two hops would price almost everything, and would also multiply two
/// thin markets into a number with no relationship to what anybody would pay. The tool says
/// "unpriced" instead, which the stash page already knows how to show.
///
/// THE HOP IS CHOSEN BY LIQUIDITY, not by which route flatters the total. That rule is taken
/// from poe2-currency-overlay, whose own remark on it is worth keeping: stale illiquid pairs
/// produce fantasy numbers that no real order will ever fill. Picking the richest route would
/// be picking the fantasy every time.
///
/// EVERYTHING IS THE SELLING SIDE. A hoard is worth what it can be sold for - see
/// <see cref="ExchangeRate"/> - so every leg uses the bid, and a two-leg value pays the spread
/// twice, exactly as its owner would.
/// </remarks>
public sealed class ExchangePairs
{
    /// <summary>
    /// The currencies a hop may go through.
    /// </summary>
    /// <remarks>
    /// The three the game itself treats as money. A hop through something nobody stocks is a
    /// route on paper and a dead end in the exchange window, and widening this list buys
    /// coverage of exactly the currencies whose prices would be worth least.
    /// </remarks>
    public static readonly string[] Majors =
    [
        ExchangeFeed.Exalted,
        "Metadata/Items/Currency/CurrencyRerollRare",     // Chaos Orb
        ExchangeFeed.Divine,
    ];

    /// <summary>How little traded volume still counts as a market for a leg.</summary>
    /// <remarks>
    /// One orb changing hands is an anecdote. This is deliberately low rather than the
    /// reference's three hundred, because that number guards an ARBITRAGE ROUTE - something
    /// somebody is about to trade on - and this one only guards a valuation, where being
    /// approximately right beats saying nothing.
    /// </remarks>
    public const double Enough = 2;

    private readonly Dictionary<string, Dictionary<string, ExchangeRate>> _pairs = new(StringComparer.Ordinal);

    /// <summary>How many currencies the feed had anything at all to say about.</summary>
    public int Count => _pairs.Count;

    /// <summary>The league these markets are from.</summary>
    public string League { get; private set; } = string.Empty;

    /// <summary>
    /// The currency this league actually treats as money.
    /// </summary>
    /// <remarks>
    /// READ FROM THE MARKETS, NOT DECIDED HERE, because the answer is different per league and
    /// picking either one as a constant is wrong in the other. Counted over six live hours:
    ///
    ///   Runes of Aldur   651 currencies   399 trade against Exalted   265 against Divine
    ///   Standard         367 currencies    29 trade against Exalted   117 against Divine
    ///
    /// So an active league is an Exalted economy and Standard is a Divine one, and a tool that
    /// hardcodes Exalted calls seven percent of Standard's currencies normal and the rest routed.
    ///
    /// THIS IS NOT THE UNIT. Everything is still valued IN Exalted - see <see cref="Worth"/> -
    /// because the index quotes in Exalted and the wealth record is written in it. This only says
    /// which currency a one-leg valuation is allowed to be against, and therefore which rows are
    /// an ordinary trade rather than a detour. Exalted wins a tie, being the unit.
    /// </remarks>
    public string Pivot { get; private set; } = ExchangeFeed.Exalted;

    /// <summary>
    /// Reads every market for one league out of one hour's digest.
    /// </summary>
    /// <remarks>
    /// HOURS ARE ADDED, NOT REPLACED. Calling this again with an older hour fills in pairs the
    /// newer one had nothing on and leaves the ones it did alone, which is what makes a thin
    /// league priceable at all: no single hour of Standard covers even a third of it.
    /// </remarks>
    /// <returns>How many markets this hour contributed that were not already known.</returns>
    public int Add(string? json, string league)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(league))
        {
            return 0;
        }

        League = league;

        JsonDocument feed;
        try
        {
            feed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return 0;
        }

        using (feed)
        {
            if (!feed.RootElement.TryGetProperty("markets", out JsonElement markets)
                || markets.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var added = 0;
            foreach (JsonElement market in markets.EnumerateArray())
            {
                if (Pair(market, league) is not { } both)
                {
                    continue;
                }

                (string first, string second) = both;
                ExchangeRate rate = ExchangeFeed.Of(market, first, second);
                if (!rate.Known || rate.Volume < Enough)
                {
                    continue;
                }

                if (Note(first, second, rate))
                {
                    added++;
                }

                // The same market read the other way round, so a lookup never has to know which
                // order the feed happened to list a pair in.
                Note(second, first, Flip(rate));
            }

            Settle();
            return added;
        }
    }

    /// <summary>Picks the currency the league trades against, now that the hours are in.</summary>
    /// <remarks>
    /// After every Add rather than once, because hours arrive one at a time and a single hour of
    /// a thin league can name the wrong winner - the blend is what the choice has to be made on.
    /// </remarks>
    private void Settle()
    {
        string best = ExchangeFeed.Exalted;
        int most = Against(ExchangeFeed.Exalted);

        foreach (string major in Majors)
        {
            // Strictly more, so Exalted keeps a tie: it is the unit everything is reported in,
            // and a coin toss that moves the unit would be a coin toss that moves every price.
            int has = Against(major);
            if (has > most)
            {
                best = major;
                most = has;
            }
        }

        Pivot = best;
    }

    /// <summary>How many currencies trade directly against one.</summary>
    private int Against(string currency)
        => _pairs.TryGetValue(currency, out Dictionary<string, ExchangeRate>? markets) ? markets.Count : 0;

    /// <summary>
    /// What one currency is worth IN EXALTED, directly or through one hop.
    /// </summary>
    /// <remarks>
    /// EXALTED WHATEVER THE PIVOT IS. The unit and the pivot are different questions: the pivot
    /// decides which markets a valuation may be built from, this always reports in Exalted,
    /// because the index quotes in Exalted and the wealth record on disk is written in it.
    /// Changing what the number MEANS would make every point ever recorded incomparable.
    ///
    /// THE DIRECT MARKET COMPETES, IT DOES NOT WIN BY DEFAULT. This used to return the Exalted
    /// market the moment one existed, which sounds right - one leg is a stronger claim than two -
    /// and quietly preferred whichever market happened to be against Exalted however thin it was.
    /// Measured over six live hours, of the currencies that had both a direct Exalted market and
    /// a usable route, the route's thinnest leg was DEEPER than the direct market in 9 of 11 in
    /// Standard and 87 of 187 in Runes of Aldur. So the short circuit was choosing the worse
    /// market most of the time in the league this tool is used in.
    ///
    /// Now every candidate is judged by the rule this file already applied to the hops - the
    /// thinnest leg carries the route - and the direct one is simply first in line, so it keeps
    /// a tie without needing a fudge factor to prefer it by.
    /// </remarks>
    public Valuation Worth(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        if (string.Equals(path, ExchangeFeed.Exalted, StringComparison.Ordinal))
        {
            // An Exalted Orb is one Exalted Orb. Said rather than looked up, because the feed
            // has no market of a thing against itself and the answer is not "unpriced".
            return new Valuation(1, string.Empty, double.PositiveInfinity, double.PositiveInfinity);
        }

        Valuation best = default;

        // First in line, so an equally deep direct market keeps the tie and stays a one-leg claim.
        // NOT gated on Enough, unlike the hops: a thin direct market that nothing can outbid is
        // still the only thing anybody knows about this currency, and this valuation would
        // otherwise go from approximately right to absent - see the remark on Enough itself.
        if (Rate(path, ExchangeFeed.Exalted) is { Known: true } straight)
        {
            best = new Valuation(straight.Bid, string.Empty, straight.Volume, straight.Stock);
        }

        foreach (string middle in Majors)
        {
            if (string.Equals(middle, path, StringComparison.Ordinal)
                || string.Equals(middle, ExchangeFeed.Exalted, StringComparison.Ordinal))
            {
                continue;
            }

            if (Rate(path, middle) is not { Known: true } first
                || Rate(middle, ExchangeFeed.Exalted) is not { Known: true } second)
            {
                continue;
            }

            // The THINNEST leg is what the route is worth trusting to, the way a chain is worth
            // its weakest link - a fat second leg cannot rescue a first one that two orbs set.
            double carries = Math.Min(first.Volume, second.Volume);
            if (carries < Enough || carries <= best.Volume)
            {
                continue;
            }

            // The thinner leg again, for the same reason it decides the volume: being able to
            // sell a thousand of something into Divine is worth nothing if the Divine side can
            // only absorb ten.
            best = new Valuation(
                first.Bid * second.Bid, middle, carries, Math.Min(first.Stock, second.Stock));
        }

        return best;
    }

    /// <summary>
    /// Whether a valuation is against the money this league uses, however many legs it took.
    /// </summary>
    /// <remarks>
    /// The question the table's colouring actually wants answered. <see cref="Valuation.Direct"/>
    /// asks whether a price came straight from an Exalted market, which in Standard is true of
    /// seven percent of currencies - so colouring by it paints ninety-three percent of a perfectly
    /// ordinary Divine economy as second-rate. A hop through the league's own money is a normal
    /// trade; a hop through something else is the one worth pointing at.
    /// </remarks>
    public bool Ordinary(Valuation worth)
        => worth.Known
           && (worth.Direct
               || string.Equals(worth.Through, Pivot, StringComparison.Ordinal));

    /// <summary>The rate between two currencies, or nothing when they never traded.</summary>
    public ExchangeRate Rate(string? from, string? to)
        => from is not null && to is not null
           && _pairs.TryGetValue(from, out Dictionary<string, ExchangeRate>? against)
           && against.TryGetValue(to, out ExchangeRate rate)
            ? rate
            : default;

    /// <summary>Every currency the feed named, priced or not.</summary>
    public IEnumerable<string> Everything() => _pairs.Keys;

    private bool Note(string from, string to, ExchangeRate rate)
    {
        if (!_pairs.TryGetValue(from, out Dictionary<string, ExchangeRate>? against))
        {
            against = new Dictionary<string, ExchangeRate>(StringComparer.Ordinal);
            _pairs[from] = against;
        }

        if (against.TryGetValue(to, out ExchangeRate already) && already.Volume >= rate.Volume)
        {
            // A newer hour already said this, and said it on more trades. Hours arrive newest
            // first, so the first answer is also the freshest - only a better-supported one wins.
            return false;
        }

        against[to] = rate;
        return true;
    }

    /// <summary>The same market seen from the other side.</summary>
    /// <remarks>
    /// BID AND ASK SWAP AND INVERT, which is easy to get subtly wrong: what you RECEIVE selling
    /// one Divine is the reciprocal of what you PAY buying one Exalted, not of what you receive
    /// for it. Volume converts too - it is counted in the first currency, and the first currency
    /// is now the other one.
    /// </remarks>
    private static ExchangeRate Flip(ExchangeRate rate) => new(
        Bid: rate.Ask > 0 ? 1 / rate.Ask : 0,
        Ask: rate.Bid > 0 ? 1 / rate.Bid : 0,
        Traded: rate.Traded > 0 ? 1 / rate.Traded : 0,
        Volume: rate.Volume * rate.Traded,

        // Depth is already the THINNER side of the book, so it reads the same from either
        // direction - the shallower end of a market does not deepen by being looked at.
        Stock: rate.Stock);

    private static (string, string)? Pair(JsonElement market, string league)
    {
        if (!market.TryGetProperty("league", out JsonElement named)
            || named.ValueKind != JsonValueKind.String
            || !string.Equals(named.GetString(), league, StringComparison.Ordinal)
            || !market.TryGetProperty("market_pair", out JsonElement pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() != 2)
        {
            return null;
        }

        string? first = null, second = null;
        foreach (JsonElement side in pair.EnumerateArray())
        {
            if (side.ValueKind != JsonValueKind.String || side.GetString() is not { Length: > 0 } path)
            {
                return null;
            }

            if (first is null)
            {
                first = path;
            }
            else
            {
                second = path;
            }
        }

        return first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal)
            ? (first, second)
            : null;
    }
}
