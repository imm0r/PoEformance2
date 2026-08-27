namespace PoEformance.Features;

/// <summary>
/// Keeps the aggregated index current: history, and the second opinion routes are checked against.
/// </summary>
/// <remarks>
/// ONE REQUEST PER LEAGUE, refreshed on the same sort of schedule as the price book beside it.
/// The daily points it carries do not move within a day, and the current price it also carries is
/// deliberately not what anything here reads - see <see cref="ScoutEntry.Steady"/> - so there is
/// nothing to gain from asking often.
/// </remarks>
public sealed class ScoutStore : IDisposable
{
    /// <summary>When the catalogue is worth asking for again.</summary>
    public static readonly TimeSpan GoesStale = TimeSpan.FromMinutes(30);

    /// <summary>How long one request is given before it counts as lost.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private readonly Func<string, CancellationToken, Task<string?>> _ask;
    private readonly Func<DateTimeOffset> _clock;

    private HttpClient? _http;
    private IReadOnlyDictionary<string, ScoutEntry> _index =
        new Dictionary<string, ScoutEntry>(StringComparer.Ordinal);

    private string _league = string.Empty;
    private DateTimeOffset _read;
    private bool _busy;

    /// <param name="ask">Where the catalogue comes from. Handed in so tests need no network.</param>
    /// <param name="clock">What time it is, for the same reason.</param>
    public ScoutStore(
        Func<string, CancellationToken, Task<string?>>? ask = null,
        Func<DateTimeOffset>? clock = null)
    {
        _ask = ask ?? Download;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether the index is being read at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>What the index knows, by metadata path.</summary>
    public IReadOnlyDictionary<string, ScoutEntry> Index
    {
        get { lock (_gate) { return _index; } }
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

    /// <summary>What happened last, in words.</summary>
    public string Status { get; private set; } = "not asked yet";

    /// <summary>Whether what is known has aged out, or was never known.</summary>
    public bool Old
    {
        get
        {
            lock (_gate)
            {
                return _index.Count == 0 || _clock() - _read > GoesStale;
            }
        }
    }

    /// <summary>Tells the store which league to read, and refreshes when that changes.</summary>
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

                // A catalogue for another league is WRONG rather than stale - and unlike the
                // exchange's hourly digests, one league's answer says nothing about another's,
                // so there is nothing to keep.
                _index = new Dictionary<string, ScoutEntry>(StringComparer.Ordinal);
                _read = default;
            }
        }

        if (changed || Old)
        {
            Refresh();
        }
    }

    /// <summary>Reads the catalogue again, unless one is already on the way.</summary>
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
            string? page = await _ask(league, _closing.Token).ConfigureAwait(false);
            IReadOnlyDictionary<string, ScoutEntry> read = ScoutCatalog.Read(page);

            if (read.Count == 0)
            {
                // Nothing readable leaves the last catalogue standing, like the stores beside
                // this one: a source that cannot be asked has not become wrong.
                Status = $"the index said nothing about {league}";
                return;
            }

            lock (_gate)
            {
                _index = read;
                _read = _clock();
            }

            Status = $"{read.Count} currencies for {league}, {ScoutCatalog.Days} days each";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Status = $"the index did not answer: {exception.Message}";
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    /// <summary>The one place that talks to the outside world.</summary>
    private async Task<string?> Download(string league, CancellationToken cancelling)
    {
        _http ??= new HttpClient { Timeout = Patience };

        using HttpResponseMessage answer = await _http
            .GetAsync(ScoutCatalog.Where(league), cancelling)
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
