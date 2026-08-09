using System.Net.Http;
using System.Text.Json;

namespace PoEformance.Features;

/// <summary>
/// Keeps prices current for whichever league is being played.
/// </summary>
/// <remarks>
/// IT GOES OUT TO THE NETWORK, and unlike the item art there is no local copy to prefer -
/// prices only exist on somebody's server. So it is OFF until somebody turns it on. What is
/// sent is a league name and nothing else: no account, no character, no stash.
///
/// NEVER ON THE THREAD THAT DRAWS. A refresh is a dozen requests and some megabytes of JSON,
/// so it happens in the background and the drawn frame reads whatever book is finished. Asking
/// for a price never waits for anything.
///
/// A BAD ANSWER NEVER REPLACES A GOOD BOOK. A refresh that comes back empty - the network
/// dropped, the league name was wrong, poe.ninja moved - leaves the previous prices in place,
/// because "the numbers vanished" is a worse failure than "the numbers are twenty minutes old".
/// The one exception is a change of league, where the old book is wrong by definition.
/// </remarks>
public sealed class PriceStore : IDisposable
{
    /// <summary>The fungible economy, keyed by picture.</summary>
    public static readonly string[] ExchangeTypes =
    [
        "Currency", "Fragments", "Abyss", "UncutGems", "LineageSupportGems", "Essences",
        "SoulCores", "Idols", "Runes", "Ritual", "Expedition", "Delirium", "Breach", "Verisium",
    ];

    /// <summary>Individually listed gear, keyed by name.</summary>
    /// <remarks>
    /// A slug this does not know lands in the per-type catch and is skipped, so listing a
    /// candidate costs one failed request rather than the whole refresh.
    /// </remarks>
    public static readonly string[] ListedTypes =
    [
        "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks",
        "UniqueJewels", "UniqueCharms", "UniqueRelics", "UniqueTablets", "PrecursorTablets",
    ];

    /// <summary>How long a book is used before it is worth asking again.</summary>
    /// <remarks>
    /// poe.ninja recomputes on its own schedule and a stash is not repriced by the minute, so
    /// this is about not hammering somebody else's server for numbers that have not moved.
    /// </remarks>
    public static readonly TimeSpan GoesStale = TimeSpan.FromMinutes(30);

    /// <summary>How long to wait before a refresh is a lost cause.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Where the last book is kept, so a session starts with prices rather than without.</summary>
    public static string DefaultFile { get; } = Path.Combine(AppContext.BaseDirectory, "config", "prices.json");

    private readonly string _file;
    private readonly Func<string, string, CancellationToken, Task<string?>> _ask;
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();

    private HttpClient? _http;
    private PriceBook _book = new();
    private string _league = string.Empty;
    private string _asked = string.Empty;
    private DateTimeOffset _fetched;
    private bool _busy;

    /// <param name="ask">
    /// How to get one answer - the kind of overview and the type in, the JSON out. Handed in so
    /// all of this is testable without a network, and so the one place that talks to the outside
    /// world is visible from the constructor.
    /// </param>
    public PriceStore(string? file = null, Func<string, string, CancellationToken, Task<string?>>? ask = null)
    {
        _file = file ?? DefaultFile;
        _ask = ask ?? Download;
    }

    /// <summary>Whether to ask for prices at all. Off until somebody says otherwise.</summary>
    public bool Enabled { get; set; }

    /// <summary>What is known right now. Never null, and never replaced by something worse.</summary>
    public PriceBook Book
    {
        get { lock (_gate) { return _book; } }
    }

    /// <summary>Which league the prices are for.</summary>
    public string League
    {
        get { lock (_gate) { return _league; } }
    }

    /// <summary>Whether a refresh is running.</summary>
    public bool Busy
    {
        get { lock (_gate) { return _busy; } }
    }

    /// <summary>What the last refresh did, for a window to show.</summary>
    public string Status { get; private set; } = "not asked yet";

    /// <summary>
    /// Says which league is being played, and refreshes when that is news.
    /// </summary>
    /// <remarks>
    /// Called from wherever the game is read. NEVER WAITS: it starts a refresh at most, and the
    /// caller carries on with whatever book is finished.
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

                // A book for another league is not stale, it is WRONG - different economy,
                // different numbers - so it goes rather than being shown until the new one lands.
                _book = new PriceBook();
                _fetched = default;
            }
        }

        if (changed || Old)
        {
            Refresh();
        }
    }

    /// <summary>Whether what is known has aged out.</summary>
    public bool Old
    {
        get
        {
            lock (_gate)
            {
                return !_book.Ready || DateTimeOffset.UtcNow - _fetched > GoesStale;
            }
        }
    }

    /// <summary>Fetches a new book, unless one is already on the way.</summary>
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
            _asked = league;
        }

        _ = Task.Run(() => Fetch(league));
    }

    private async Task Fetch(string league)
    {
        try
        {
            var built = new PriceBook();
            var failed = 0;

            // The fungible half FIRST, and this is not just ordering. Its answers are the ones
            // with a real market behind them, so the rate comes from there - see PriceBook.Rate.
            foreach ((string kind, string[] types) in new[] { ("exchange", ExchangeTypes), ("item", ListedTypes) })
            {
                foreach (string type in types)
                {
                    if (_closing.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        string? answer = await _ask(kind, $"{Escaped(league)}&type={type}", _closing.Token)
                            .ConfigureAwait(false);

                        built.Add(kind == "exchange" ? PriceKind.Exchange : PriceKind.Listed, answer);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException
                                                          or TaskCanceledException or OperationCanceledException)
                    {
                        // One type is not the refresh. A slug this does not know, or one request
                        // that timed out, must not cost the other twenty.
                        failed++;
                    }
                }
            }

            Keep(built, league, failed);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              or OperationCanceledException or JsonException)
        {
            Status = "could not be asked - the previous prices are still being used";
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    /// <summary>Takes a finished book, if it is worth having.</summary>
    private void Keep(PriceBook built, string league, int failed)
    {
        lock (_gate)
        {
            // Only if it is better than nothing, and only if it is still the league being
            // played - a refresh that finishes after somebody has changed characters is for the
            // wrong economy.
            if (!built.Ready || !string.Equals(_asked, league, StringComparison.OrdinalIgnoreCase))
            {
                Status = built.Ready
                    ? "answered for a league that is no longer being played"
                    : "nothing came back - the previous prices are still being used";
                return;
            }

            _book = built;
            _fetched = DateTimeOffset.UtcNow;
        }

        Status = $"{built.Count} prices for {league}, {built.Thin} too thin to believe"
                 + (failed > 0 ? $", {failed} kinds unanswered" : string.Empty);

        Write(built, league);
    }

    /// <summary>The one place that talks to the outside world.</summary>
    private async Task<string?> Download(string kind, string query, CancellationToken cancelling)
    {
        _http ??= new HttpClient { Timeout = Patience };

        string where = kind == "exchange"
            ? "exchange/current/overview"
            : "stash/current/item/overview";

        using HttpResponseMessage answer = await _http
            .GetAsync($"https://poe.ninja/poe2/api/economy/{where}?league={query}", cancelling)
            .ConfigureAwait(false);

        return answer.IsSuccessStatusCode
            ? await answer.Content.ReadAsStringAsync(cancelling).ConfigureAwait(false)
            : null;
    }

    /// <summary>A league name as it goes in a query - spaces as plus, the way the site spells them.</summary>
    public static string Escaped(string league)
        => Uri.EscapeDataString(league.Trim()).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>Remembers the book, so the next session opens with prices rather than without.</summary>
    private void Write(PriceBook book, string league)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, book.Saved(league));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Prices that are not written down are prices that get fetched again. Not worth
            // anything more than not doing it.
        }
    }

    /// <summary>
    /// Picks up the book the last session left, when it is for this league and not too old.
    /// </summary>
    /// <returns>Whether anything was picked up.</returns>
    public bool Reopen(string league)
    {
        if (string.IsNullOrWhiteSpace(league) || !File.Exists(_file))
        {
            return false;
        }

        try
        {
            if (PriceBook.Reopen(File.ReadAllText(_file), league) is not { } kept)
            {
                return false;
            }

            lock (_gate)
            {
                _book = kept.Book;
                _league = league;
                _fetched = kept.When;
            }

            Status = $"{kept.Book.Count} prices for {league}, from {kept.When.ToLocalTime():t}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _http?.Dispose();
        _closing.Dispose();
    }
}
