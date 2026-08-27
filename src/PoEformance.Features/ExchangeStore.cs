namespace PoEformance.Features;

/// <summary>
/// Keeps the game's exchange feed current, and the pair graph built from it.
/// </summary>
/// <remarks>
/// ONE REQUEST AN HOUR, AFTER THE FIRST. Completed hours never change, so a digest once fetched
/// is kept forever - a refresh asks only for hours it has not seen, which after the first load
/// is exactly one. That is the whole cost of pricing every currency in the league.
///
/// HOW MANY HOURS DEPENDS ON THE LEAGUE, and it has to. One hour of an active league covers
/// nearly everything; one hour of Standard covered ninety-five currencies where two covered a
/// hundred and thirty-four. So hours are added until one stops bringing anything new, or until
/// <see cref="MostHours"/> - rather than a fixed depth that would be wasteful in one league and
/// threadbare in the other.
///
/// NOTHING HERE BLOCKS. Like the price store beside it, a refresh is a task and the graph is
/// swapped whole when it finishes, so a reader never sees half a league.
/// </remarks>
public sealed class ExchangeStore : IDisposable
{
    /// <summary>How far back to walk when a league is thin.</summary>
    /// <remarks>
    /// Six hours of Standard is a working afternoon of trades and covers what a stash actually
    /// holds. Going further buys currencies whose last trade was long enough ago that the price
    /// would be a fossil, and each hour is another request on somebody's CDN.
    /// </remarks>
    public const int MostHours = 6;

    /// <summary>When a new hour is worth asking for.</summary>
    /// <remarks>
    /// The feed publishes hourly, so anything shorter asks for a digest that cannot have
    /// changed. A few minutes past the hour rather than on it, because the hour has to finish
    /// before it is written.
    /// </remarks>
    public static readonly TimeSpan GoesStale = TimeSpan.FromMinutes(65);

    /// <summary>How long one digest is given before it counts as lost.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private readonly Func<long, CancellationToken, Task<string?>> _ask;
    private readonly Func<DateTimeOffset> _clock;

    // Hours are IMMUTABLE once complete, which is what makes keeping them free of consequence -
    // and what turns a refresh into one request rather than six.
    private readonly Dictionary<long, string> _hours = [];

    private HttpClient? _http;
    private ExchangePairs _pairs = new();
    private string _league = string.Empty;
    private DateTimeOffset _built;
    private bool _busy;

    /// <param name="ask">Where a digest comes from. Handed in so tests need no network.</param>
    /// <param name="clock">What time it is, for the same reason.</param>
    public ExchangeStore(
        Func<long, CancellationToken, Task<string?>>? ask = null,
        Func<DateTimeOffset>? clock = null)
    {
        _ask = ask ?? Download;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether the feed is being read at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>What every currency in the league is worth, as far as the feed has said.</summary>
    public ExchangePairs Pairs
    {
        get { lock (_gate) { return _pairs; } }
    }

    /// <summary>The league being read.</summary>
    public string League
    {
        get { lock (_gate) { return _league; } }
    }

    /// <summary>Whether a read is on the way.</summary>
    public bool Busy
    {
        get { lock (_gate) { return _busy; } }
    }

    /// <summary>What happened last, in words, for a window to show.</summary>
    public string Status { get; private set; } = "not asked yet";

    /// <summary>Whether what is known has aged out, or was never known.</summary>
    public bool Old
    {
        get
        {
            lock (_gate)
            {
                return _pairs.Count == 0 || _clock() - _built > GoesStale;
            }
        }
    }

    /// <summary>Tells the store which league to read, and refreshes when that changes.</summary>
    /// <remarks>
    /// A GRAPH FOR ANOTHER LEAGUE IS NOT STALE, IT IS WRONG - a different economy with different
    /// numbers - so it goes rather than being shown until the new one lands. The HOURS stay:
    /// every league shares one digest, so what was fetched still answers for the new one.
    /// </remarks>
    public void Playing(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_league, league, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                _league = league;
                _pairs = new ExchangePairs();
                _built = default;
            }
        }

        if (changed || Old)
        {
            Refresh();
        }
    }

    /// <summary>Reads any hours not already held, and rebuilds the graph.</summary>
    public void Refresh()
    {
        string league;
        lock (_gate)
        {
            if (!Enabled || _busy || _league.Length == 0)
            {
                return;
            }

            _busy = true;
            league = _league;
        }

        _ = Task.Run(() => Fetch(league));
    }

    private async Task Fetch(string league)
    {
        try
        {
            var pairs = new ExchangePairs();
            var read = 0;
            var fetched = 0;

            for (var back = 1; back <= MostHours; back++)
            {
                if (_closing.IsCancellationRequested)
                {
                    return;
                }

                long hour = ExchangeFeed.HourBefore(_clock(), back);

                string? digest;
                lock (_gate)
                {
                    _hours.TryGetValue(hour, out digest);
                }

                if (digest is null)
                {
                    digest = await _ask(hour, _closing.Token).ConfigureAwait(false);
                    fetched++;

                    if (digest is not null)
                    {
                        lock (_gate)
                        {
                            _hours[hour] = digest;

                            // Bounded because a session left running for days would otherwise
                            // keep every hour it ever saw, and only the newest few are ever read.
                            Forget();
                        }
                    }
                }

                if (digest is null)
                {
                    continue;
                }

                read++;
                if (pairs.Add(digest, league) == 0 && read > 1)
                {
                    // AN HOUR THAT BRINGS NOTHING NEW ENDS THE WALK. In an active league that is
                    // the second hour; in Standard it may be the sixth. Never the first, which
                    // can legitimately be empty when the league is quiet.
                    break;
                }
            }

            lock (_gate)
            {
                _pairs = pairs;
                _built = _clock();
            }

            Status = pairs.Count > 0
                ? $"{pairs.Count} currencies from {read} hour{(read == 1 ? string.Empty : "s")} of "
                  + $"{league}{(fetched > 0 ? $", {fetched} fetched" : ", all cached")}"
                : $"the exchange said nothing about {league}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The feed being unreachable leaves the last graph standing, exactly like the price
            // store beside it: a market that cannot be asked about has not become worthless.
            Status = $"the exchange did not answer: {exception.Message}";
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    /// <summary>Drops hours older than the walk will ever reach again.</summary>
    private void Forget()
    {
        if (_hours.Count <= MostHours * 2)
        {
            return;
        }

        long oldest = _hours.Keys.Min();
        _hours.Remove(oldest);
    }

    /// <summary>The one place that talks to the outside world.</summary>
    private async Task<string?> Download(long hour, CancellationToken cancelling)
    {
        _http ??= new HttpClient { Timeout = Patience };

        using HttpResponseMessage answer = await _http
            .GetAsync(ExchangeFeed.Where(hour), cancelling)
            .ConfigureAwait(false);

        return answer.IsSuccessStatusCode
            ? await answer.Content.ReadAsStringAsync(cancelling).ConfigureAwait(false)
            : null;
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        _http?.Dispose();
    }
}
