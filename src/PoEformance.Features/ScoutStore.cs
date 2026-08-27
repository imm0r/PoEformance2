namespace PoEformance.Features;

/// <summary>
/// Keeps the aggregated index current: history, and the second opinion routes are checked against.
/// </summary>
/// <remarks>
/// ONE REQUEST PER CATEGORY, plus one that asks which categories the league has. Seventeen where
/// there used to be one, because one bought thirty-five trend lines out of three hundred and
/// sixty-eight rows - see <see cref="ScoutCatalog"/> for the measurement.
///
/// SO THE INTERVAL PAYS FOR IT. The points this carries are DAILY, and the current price it also
/// carries is deliberately not what anything here reads - see <see cref="ScoutEntry.Steady"/>.
/// Nothing it serves can move in half an hour, so the old half-hour window was already asking
/// far more often than the data changes; widening it to two hours costs a new day's point being
/// noticed up to two hours late, on a seven-day line, and brings the average back to well under
/// ten requests an hour. Seventeen every thirty minutes would have been thirty-four.
/// </remarks>
public sealed class ScoutStore : IDisposable
{
    /// <summary>When the catalogue is worth asking for again.</summary>
    public static readonly TimeSpan GoesStale = TimeSpan.FromHours(2);

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

    /// <param name="ask">
    /// Fetches one ADDRESS, or null when it could not. Addresses rather than leagues because
    /// there are now two shapes of request - the category list and a category's page - and one
    /// transport that knows neither is a smaller seam than two delegates that know both.
    /// </param>
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
            string? listed = await _ask(ScoutCatalog.Categories(league), _closing.Token).ConfigureAwait(false);
            IReadOnlyList<string> categories = ScoutCatalog.ReadCategories(listed);

            if (categories.Count == 0)
            {
                // A silent category list is not a reason to read nothing. The one category whose
                // name is certain still carries Exalted, Divine and Chaos, which is everything
                // the arbitrage check needs - so this degrades to the old behaviour rather than
                // to no index at all.
                categories = [ScoutCatalog.Fallback];
            }

            var read = new Dictionary<string, ScoutEntry>(StringComparer.Ordinal);
            var answered = 0;

            // ONE AT A TIME rather than all at once. Seventeen requests fired together at a
            // volunteer-run index to save four seconds on a background task is a bad trade.
            foreach (string category in categories)
            {
                string? page = await _ask(ScoutCatalog.Where(league, category), _closing.Token)
                    .ConfigureAwait(false);

                if (page is null)
                {
                    // Told apart from an empty category on purpose: the list only names
                    // categories that HAVE prices, so an empty answer is a failure wearing a
                    // success's clothes, and counting it as read would make the status line lie.
                    continue;
                }

                answered++;
                foreach ((string path, ScoutEntry entry) in ScoutCatalog.Read(page))
                {
                    read[path] = entry;
                }
            }

            if (read.Count == 0)
            {
                // Nothing readable leaves the last catalogue standing, like the stores beside
                // this one: a source that cannot be asked has not become wrong.
                Status = $"the index said nothing about {league}";
                return;
            }

            lock (_gate)
            {
                // REPLACED, NOT MERGED WITH WHAT WAS HELD, even when only some categories
                // answered. Merging would keep a category alive in the index long after the
                // league stopped pricing it, and there would be no way to tell the difference.
                // A short read costs some trend lines until the next refresh, and says so.
                _index = read;
                _read = _clock();
            }

            Status = $"{read.Count} currencies for {league}, {ScoutCatalog.Days} days each"
                     + (answered == categories.Count
                         ? $", {answered} categories"
                         : $", only {answered} of {categories.Count} categories answered");
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
    private async Task<string?> Download(string address, CancellationToken cancelling)
    {
        _http ??= new HttpClient { Timeout = Patience };

        using HttpResponseMessage answer = await _http
            .GetAsync(address, cancelling)
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
