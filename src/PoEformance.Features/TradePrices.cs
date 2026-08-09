using System.Text.Json;

namespace PoEformance.Features;

/// <summary>One asking price, as the trade site gives it.</summary>
/// <param name="Amount">How many.</param>
/// <param name="Currency">Of what, in the site's own spelling - <c>divine</c>.</param>
public readonly record struct TradeListing(double Amount, string Currency);

/// <summary>What one query came back with.</summary>
/// <param name="Ok">Whether the query ran. False for a wall, a timeout or no session.</param>
/// <param name="Status">The HTTP status behind a failure, or 0 when there was not one.</param>
/// <param name="Listings">The asking prices, cheapest first as the site sorts them.</param>
/// <param name="Error">What went wrong, in words, for a window to show.</param>
public readonly record struct TradeAnswer(
    bool Ok, int Status, IReadOnlyList<TradeListing> Listings, string Error)
{
    /// <summary>A query that could not be run at all.</summary>
    public static TradeAnswer Not(string why, int status = 0) => new(false, status, [], why);
}

/// <summary>
/// What uniques are actually being listed for, from the game's own trade site.
/// </summary>
/// <remarks>
/// WHY AT ALL, when poe.ninja is already here: poe.ninja has no unique prices on Standard, and
/// on every league the listing gate drops the uniques with few listings - which is most of the
/// interesting ones. This fills that gap and nothing else: it is asked only for uniques the
/// book could not price.
///
/// IT CANNOT TALK TO THE TRADE API ITSELF, and that is not a limitation to be worked around.
/// The endpoints are Cloudflare-gated: a session cookie alone gets a 403, and passing the check
/// wants the browser's cookies, its User-Agent AND its TLS fingerprint together. So the query
/// is handed in - what actually runs it is a same-origin fetch inside a logged-in browser, and
/// no secret ever reaches this side. Handing it in is also what makes all of this testable
/// without a network or a browser.
///
/// EVERY ANSWER IS WRITTEN DOWN, INCLUDING THE EMPTY ONES. A unique nobody is selling answers
/// with nothing, and without remembering that it is asked again every time the stash is read -
/// at three and a half seconds a query, for the rest of the session.
///
/// ONE QUERY IN FLIGHT, spaced out, and five minutes of silence after a wall. This is somebody
/// else's server and the only polite failure mode is to stop asking.
/// </remarks>
public sealed class TradePrices : IDisposable
{
    /// <summary>
    /// How many priceable listings make a market.
    /// </summary>
    /// <remarks>
    /// TWO IS NOT A MEDIAN. The cheapest-few median was accepting a SINGLE listing as the
    /// price, which is how a starter unique ends up quoted at four thousand Divine - the same
    /// illiquidity that the poe.ninja gates exist for, arriving by a different door. Below this
    /// the answer is "unpriced", which is cached like any other answer.
    /// </remarks>
    public const int Enough = 3;

    /// <summary>How many of the cheapest listings the price is taken from.</summary>
    public const int Cheapest = 8;

    /// <summary>Most names waiting at once, so a stash read cannot queue a thousand.</summary>
    public const int MostQueued = 40;

    /// <summary>How long a name is left alone between queries.</summary>
    public static readonly TimeSpan Apart = TimeSpan.FromSeconds(3.5);

    /// <summary>How long to stop asking after a sign-in wall.</summary>
    public static readonly TimeSpan AfterAWall = TimeSpan.FromMinutes(5);

    /// <summary>And after anything else that failed, which is usually a moment's trouble.</summary>
    public static readonly TimeSpan AfterATrip = TimeSpan.FromSeconds(15);

    /// <summary>How long a price is believed.</summary>
    public static readonly TimeSpan Keeps = TimeSpan.FromHours(24);

    /// <summary>And how long "nobody is selling one" is believed, which is less.</summary>
    public static readonly TimeSpan KeepsNothing = TimeSpan.FromHours(12);

    /// <summary>Where the answers are kept between sessions.</summary>
    public static string DefaultFile { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "trade-prices.json");

    private readonly string _file;
    private readonly Func<string, string, CancellationToken, Task<TradeAnswer>>? _ask;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();

    // What is known: the tidied name, and what came back. A zero price is a real answer -
    // nobody is selling one - and is remembered so it is not asked for again.
    private readonly Dictionary<string, Answer> _known = new(StringComparer.Ordinal);
    private readonly List<string> _queue = [];
    private readonly Dictionary<string, string> _spelt = new(StringComparer.Ordinal);

    private string _league = string.Empty;
    private string _opened = string.Empty;
    private DateTimeOffset _sent;
    private DateTimeOffset _quietUntil;
    private bool _busy;

    /// <param name="ask">
    /// How to run one query - the unique's name and the league in, the listings out. Null means
    /// there is no way to ask, which is the state until a session has been wired up.
    /// </param>
    /// <param name="clock">Handed in so the rate limit and the ages are testable in a millisecond.</param>
    public TradePrices(
        string? file = null,
        Func<string, string, CancellationToken, Task<TradeAnswer>>? ask = null,
        Func<DateTimeOffset>? clock = null)
    {
        _file = file ?? DefaultFile;
        _ask = ask;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether to ask at all. Off until somebody says otherwise.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The book the listings are converted with. Set by whoever owns both.
    /// </summary>
    /// <remarks>
    /// A listing says "3 divine" and this is what turns that into Exalted. Held rather than
    /// passed in with the question, because the answer arrives long afterwards on a thread of
    /// its own - and by then the right book is whichever one is current.
    /// </remarks>
    public PriceBook Book { get; set; } = new();

    /// <summary>What the last query did, for a window to show.</summary>
    public string Status { get; private set; } = "not asked yet";

    /// <summary>How many names are waiting.</summary>
    public int Waiting
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    /// <summary>How many answers are remembered, the empty ones included.</summary>
    public int Known
    {
        get { lock (_gate) { return _known.Count; } }
    }

    /// <summary>Whether a query is out.</summary>
    public bool Busy
    {
        get { lock (_gate) { return _busy; } }
    }

    /// <summary>Which league the answers are for.</summary>
    public string League
    {
        get { lock (_gate) { return _league; } }
    }

    /// <summary>
    /// Says which league is being played, and picks up what the last session learned.
    /// </summary>
    /// <remarks>
    /// The same rule the price book follows: answers for another league are not old, they are a
    /// different market, so a change throws them away rather than ageing them. Read once per
    /// league and only once somebody has switched this on.
    /// </remarks>
    public void Watching(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return;
        }

        bool first;
        lock (_gate)
        {
            if (!string.Equals(_league, league, StringComparison.OrdinalIgnoreCase))
            {
                _league = league;
                _known.Clear();
                _queue.Clear();
                _spelt.Clear();
            }

            first = Enabled && !string.Equals(_opened, league, StringComparison.OrdinalIgnoreCase);
            if (first)
            {
                _opened = league;
            }
        }

        if (first)
        {
            Reopen(league);
        }
    }

    /// <summary>
    /// What a unique is being listed for, or null when nothing fresh is known.
    /// </summary>
    /// <remarks>
    /// NEVER WAITS and never asks. It is called while drawing, so it answers with what is
    /// already known; getting an answer is <see cref="Ask(string)"/>'s job.
    /// </remarks>
    public double? Worth(string? name)
    {
        if (!Enabled || Tidy(name) is not { Length: > 0 } key)
        {
            return null;
        }

        lock (_gate)
        {
            return _known.TryGetValue(key, out Answer answer) && Fresh(answer) && answer.Exalted > 0
                ? answer.Exalted
                : null;
        }
    }

    /// <summary>Queues one unique, unless it is already known or already waiting.</summary>
    public void Ask(string? name)
    {
        if (!Enabled || Tidy(name) is not { Length: > 0 } key)
        {
            return;
        }

        lock (_gate)
        {
            if (_queue.Count >= MostQueued
                || _queue.Contains(key)
                || (_known.TryGetValue(key, out Answer answer) && Fresh(answer)))
            {
                return;
            }

            _queue.Add(key);
            _spelt[key] = name!.Trim();
        }
    }

    /// <summary>
    /// Queues every unique in a stash read that the price book could not price.
    /// </summary>
    /// <returns>How many were queued.</returns>
    /// <remarks>
    /// ONLY WHAT THE BOOK MISSED. poe.ninja's answer is one request for thousands of prices and
    /// this is one request per name, so anything the book already knows must not come here -
    /// and a stash holds hundreds of uniques.
    /// </remarks>
    public int Ask(StashView view, PriceBook book)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(book);

        int before = Waiting;
        foreach (StashPage page in view.Pages)
        {
            foreach (StashSlot slot in page.Items)
            {
                if (slot.Item.Unique.Length > 0 && book.Unit(slot.Item) is null)
                {
                    Ask(slot.Item.Unique);
                }
            }
        }

        return Waiting - before;
    }

    /// <summary>
    /// Sends the next queued name, when it is time. Called from wherever the game is read.
    /// </summary>
    /// <remarks>
    /// NEVER WAITS: it starts one query at most and returns. Everything that decides whether to
    /// send is here rather than in the transport, so being polite to somebody else's server
    /// does not depend on which transport is wired up.
    /// </remarks>
    public void Service()
    {
        string name;
        string league;

        lock (_gate)
        {
            DateTimeOffset now = _clock();
            if (!Enabled || _busy || _queue.Count == 0 || _league.Length == 0
                || now < _quietUntil || now - _sent < Apart)
            {
                return;
            }

            if (_ask is null)
            {
                Status = "nowhere to ask - the trade session is not wired up";
                return;
            }

            string key = _queue[0];
            _queue.RemoveAt(0);
            name = _spelt.TryGetValue(key, out string? spelt) ? spelt : key;
            league = _league;
            _sent = now;
            _busy = true;
        }

        _ = Task.Run(() => Send(name, league));
    }

    private async Task Send(string name, string league)
    {
        TradeAnswer answer;
        try
        {
            answer = await _ask!(name, league, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever the transport did wrong, it is not worth a session over - and the name
            // goes back in the queue below like any other failure.
            answer = TradeAnswer.Not(exception.Message);
        }

        Keep(name, answer);
    }

    /// <summary>Takes one answer: a price, a "nobody is selling one", or a reason to wait.</summary>
    private void Keep(string name, TradeAnswer answer)
    {
        string key = Tidy(name)!;
        var write = false;

        lock (_gate)
        {
            _busy = false;

            if (!answer.Ok)
            {
                // The name goes back, at the FRONT: it is the one somebody is looking at.
                if (!_queue.Contains(key) && _queue.Count < MostQueued)
                {
                    _queue.Insert(0, key);
                }

                // A wall is different in kind from a hiccup. 401 and 403 mean the browser is no
                // longer signed in, and asking again in fifteen seconds only gets refused
                // again, forty times a minute, at somebody else's front door.
                bool wall = answer.Status is 401 or 403;
                _quietUntil = _clock() + (wall ? AfterAWall : AfterATrip);
                Status = wall
                    ? $"signed out - sign in again in the trade window ({answer.Status})"
                    : answer.Error.Length > 0 ? answer.Error : $"the query failed ({answer.Status})";
                return;
            }

            double worth = Robust(answer.Listings, Book) ?? 0;
            _known[key] = new Answer(worth, _clock());
            _spelt.Remove(key);
            write = true;

            Status = worth > 0
                ? $"{name}: {StashWorth.Money(worth)} ex from {answer.Listings.Count} listings"
                : $"{name}: no market - {answer.Listings.Count} listings, which is not enough to believe";
        }

        if (write)
        {
            Write();
        }
    }

    /// <summary>
    /// One price out of a pile of asking prices.
    /// </summary>
    /// <remarks>
    /// THE MEDIAN OF THE CHEAPEST FEW, not the cheapest. The cheapest listing is as often a
    /// mispriced item or a seller who has logged off as it is the market; the middle of the
    /// cheap end is what somebody would actually pay.
    ///
    /// A listing in a currency the book cannot convert is DROPPED rather than guessed at, and
    /// dropping enough of them takes the answer below <see cref="Enough"/> and leaves the unique
    /// unpriced. That is the intended failure: a unique with no price shows none.
    /// </remarks>
    public static double? Robust(IReadOnlyList<TradeListing> listings, PriceBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (listings is null || listings.Count == 0)
        {
            return null;
        }

        var exalted = new List<double>(listings.Count);
        foreach (TradeListing listing in listings)
        {
            if (listing.Amount > 0 && book.Quoted(listing.Currency) is { } rate and > 0)
            {
                exalted.Add(listing.Amount * rate);
            }
        }

        if (exalted.Count < Enough)
        {
            return null;
        }

        exalted.Sort();
        int take = Math.Min(Cheapest, exalted.Count);
        return exalted[take / 2];
    }

    /// <summary>What was learned about one name.</summary>
    /// <param name="Exalted">What it is worth, or zero for "nobody is selling one".</param>
    /// <param name="When">When that was found out.</param>
    private readonly record struct Answer(double Exalted, DateTimeOffset When);

    private bool Fresh(Answer answer)
        => _clock() - answer.When < (answer.Exalted > 0 ? Keeps : KeepsNothing);

    /// <summary>A name in the one spelling both sides can agree on.</summary>
    /// <remarks>The same tidying the price book does, so the two halves key alike.</remarks>
    public static string? Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        Span<char> kept = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;

        foreach (char one in name)
        {
            if (char.IsAsciiLetterOrDigit(one))
            {
                kept[length++] = char.ToLowerInvariant(one);
            }
        }

        return length > 0 ? new string(kept[..length]) : null;
    }

    /// <summary>Remembers the answers, so a new session does not re-ask a hundred questions.</summary>
    private void Write()
    {
        var writing = new System.Text.Json.Nodes.JsonObject();
        var lines = new System.Text.Json.Nodes.JsonObject();

        lock (_gate)
        {
            writing["league"] = _league;
            foreach ((string key, Answer answer) in _known)
            {
                lines[key] = new System.Text.Json.Nodes.JsonObject
                {
                    ["ex"] = answer.Exalted,
                    ["when"] = answer.When.ToUnixTimeSeconds(),
                };
            }
        }

        writing["lines"] = lines;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, writing.ToJsonString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Answers that are not written down are answers that get asked again. Not worth
            // anything more than not doing it.
        }
    }

    /// <summary>
    /// Picks up what the last session learned, when it is for this league.
    /// </summary>
    /// <returns>How many answers were picked up.</returns>
    public int Reopen(string league)
    {
        if (string.IsNullOrWhiteSpace(league) || !File.Exists(_file))
        {
            return 0;
        }

        try
        {
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(_file));
            JsonElement root = saved.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("league", out JsonElement was)
                || was.ValueKind != JsonValueKind.String
                || !string.Equals(was.GetString(), league, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("lines", out JsonElement lines)
                || lines.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var taken = 0;
            lock (_gate)
            {
                foreach (JsonProperty one in lines.EnumerateObject())
                {
                    if (one.Value.ValueKind != JsonValueKind.Object
                        || !one.Value.TryGetProperty("ex", out JsonElement ex)
                        || !ex.TryGetDouble(out double exalted)
                        || exalted < 0
                        || !one.Value.TryGetProperty("when", out JsonElement when)
                        || !when.TryGetInt64(out long stamp))
                    {
                        continue;
                    }

                    _known[one.Name] = new Answer(exalted, DateTimeOffset.FromUnixTimeSeconds(stamp));
                    taken++;
                }
            }

            Status = $"{taken} answers remembered for {league}";
            return taken;
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
