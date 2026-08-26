using System.Runtime.Versioning;
using System.Text.Json;
using DirectN.Extensions.Utilities;
using PoEformance.Features;

namespace PoEformance.Config;

/// <summary>
/// Runs the trade window on a thread of its own, and turns it into one query at a time.
/// </summary>
/// <remarks>
/// The seam between the price layer and a browser: everything above this hands in a unique's
/// name and gets asking prices back, and everything below it is a signed-in page running its
/// own fetch. WebView2 wants an STA thread with a message pump and the rest of the tool is
/// neither, so the window gets one - the same arrangement the config window has.
///
/// OPENED ON DEMAND, NEVER AT START-UP. Nothing here happens until somebody switches trade
/// pricing on AND something needs a price; the first query is what opens the window, and the
/// sign-in is done once.
///
/// A QUERY NEVER BLOCKS ITS CALLER. Every answer arrives as a message from the page, so the
/// question is a task that completes when the page replies, times out, or the window closes
/// underneath it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TradeSession : IDisposable
{
    /// <summary>Where the sign-in lives - this window's own browser profile, and nowhere else.</summary>
    /// <remarks>
    /// Next to the executable, like the config window's, because the tool is a portable folder
    /// and its state should travel with it. A folder of its own so that signing in to the trade
    /// site is not the same act as opening the settings page.
    /// </remarks>
    public static string DefaultProfile { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "webview2-trade");

    /// <summary>How long one query is given before it counts as lost.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);

    private readonly string _profile;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<TradeAnswer>> _waiting = [];
    private readonly Dictionary<int, TaskCompletionSource<TradeProbe>> _probing = [];

    private TradeSessionWindow? _window;
    private bool _starting;
    private string _failed = string.Empty;
    private int _next;

    public TradeSession(string? profile = null) => _profile = profile ?? DefaultProfile;

    /// <summary>Whether the window is up.</summary>
    public bool Open
    {
        get { lock (_gate) { return _window is not null; } }
    }

    /// <summary>Whether the page's helper has come up, so a query can actually be sent.</summary>
    public bool Ready
    {
        get { lock (_gate) { return _window is { Ready: true }; } }
    }

    /// <summary>
    /// Whether the window takes itself off screen once the site has actually answered.
    /// </summary>
    /// <remarks>
    /// WHAT THE SIGN-IN WINDOW IS FOR is the sign-in. After that it is a browser sitting on the
    /// taskbar doing nothing visible, and the AHK tool this replaces hid it for exactly that
    /// reason. Hidden rather than closed: closing it would mean creating a whole browser again
    /// on the next query, and the sign-in has to survive anyway.
    ///
    /// IT COMES BACK BY ITSELF on 401 or 403. Those are the two answers that mean a person has
    /// to do something, and a hidden window that silently stops working would be the worst of
    /// both arrangements. Anything else - a timeout, a 429, a wall - is not a sign-in problem
    /// and does not need a window.
    /// </remarks>
    public bool HideWhenSignedIn { get; set; } = true;

    /// <summary>
    /// Opens the window, or brings it forward if it is already there.
    /// </summary>
    /// <remarks>
    /// The game is normally fullscreen, so a window opened behind it is invisible and reads as
    /// "nothing happened" - which is why this asks for the foreground rather than assuming it.
    /// </remarks>
    public void Show(string league)
    {
        lock (_gate)
        {
            if (_window is { } already)
            {
                Forward(already);
                return;
            }

            // Creating the browser takes seconds, and the window only becomes visible to this
            // check at the end of them. Without this flag, the queue asking again while one is
            // coming up opens a SECOND browser - and then a third.
            if (_starting)
            {
                return;
            }

            _starting = true;

            var thread = new Thread(() => Run(league))
            {
                Name = "trade-session",

                // A browser window is not a reason for the tool to stay alive: closing the
                // overlay has to close this too, or the process outlives its own UI.
                IsBackground = true,
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    /// <summary>
    /// Asks the page what a unique is going for.
    /// </summary>
    /// <remarks>
    /// The first call OPENS the window and answers "not yet" rather than waiting on a sign-in
    /// that may take a minute or an hour; the caller queues the name again and asks a few
    /// seconds later, which is how the queue gets moving the moment somebody signs in.
    /// </remarks>
    public Task<TradeAnswer> Query(string name, string league, CancellationToken cancelling)
    {
        ArgumentNullException.ThrowIfNull(name);

        TradeSessionWindow? window;
        string failed;
        lock (_gate)
        {
            window = _window;
            failed = _failed;
        }

        if (window is null)
        {
            Show(league);

            // WHY THE LAST FAILURE IS CARRIED BACK: a window that cannot be created at all
            // leaves this answering "sign in once" about a browser that is never going to
            // appear, forever, while the real reason sits in a console nobody is looking at.
            // Still tries again, because the next reason might not be the last one.
            return Task.FromResult(TradeAnswer.Not(failed.Length > 0
                ? failed
                : "opening the trade site - sign in to pathofexile.com once in that window"));
        }

        if (!window.Ready)
        {
            return Task.FromResult(TradeAnswer.Not("the trade page has not finished loading"));
        }

        int id;
        var answered = new TaskCompletionSource<TradeAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            id = ++_next;
            _waiting[id] = answered;
        }

        // Built as JSON rather than concatenated: a unique's name is somebody else's text and a
        // hand-rolled quote is one apostrophe away from a message the page cannot read.
        string json = new System.Text.Json.Nodes.JsonObject
        {
            ["cmd"] = "tradeQuery",
            ["id"] = id,
            ["name"] = name,
            ["league"] = league,
        }.ToJsonString();

        try
        {
            // Posting has to happen on the window's own thread - it is a COM object with an
            // apartment, and calling it from the reader thread is undefined rather than slow.
            window.RunTaskOnUIThread(() => window.Post(json));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            Finish(id, TradeAnswer.Not("the trade window went away"));
        }

        return Waited(id, answered, cancelling);
    }

    /// <summary>
    /// Asks the currency exchange about several currencies at once, and reports what came back.
    /// </summary>
    /// <remarks>
    /// A DIAGNOSTIC. Nothing prices anything from this - see <see cref="TradeProbe"/> for the
    /// three questions it exists to settle. It is run from a button, once, by somebody who is
    /// looking at the answer.
    /// </remarks>
    /// <param name="most">
    /// How many currencies the one request asks about. Capped by the caller rather than here so
    /// that a first look cannot be the thing that trips a rate limit.
    /// </param>
    public Task<TradeProbe> Probe(string league, int most, CancellationToken cancelling)
    {
        TradeSessionWindow? window;
        lock (_gate)
        {
            window = _window;
        }

        if (window is null)
        {
            Show(league);
            return Task.FromResult(TradeProbe.Not(
                "opening the trade site - sign in to pathofexile.com once, then ask again"));
        }

        if (!window.Ready)
        {
            return Task.FromResult(TradeProbe.Not("the trade page has not finished loading"));
        }

        int id;
        var answered = new TaskCompletionSource<TradeProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            id = ++_next;
            _probing[id] = answered;
        }

        string json = new System.Text.Json.Nodes.JsonObject
        {
            ["cmd"] = "exchangeProbe",
            ["id"] = id,
            ["league"] = league,
            ["most"] = most,
        }.ToJsonString();

        try
        {
            window.RunTaskOnUIThread(() => window.Post(json));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            FinishProbe(id, TradeProbe.Not("the trade window went away"));
        }

        return WaitedProbe(id, answered, cancelling);
    }

    private async Task<TradeProbe> WaitedProbe(
        int id, TaskCompletionSource<TradeProbe> answered, CancellationToken cancelling)
    {
        using var giveUp = new CancellationTokenSource(Patience);
        using CancellationTokenSource both =
            CancellationTokenSource.CreateLinkedTokenSource(giveUp.Token, cancelling);

        Task finished = await Task
            .WhenAny(answered.Task, Task.Delay(Timeout.InfiniteTimeSpan, both.Token))
            .ConfigureAwait(false);

        if (ReferenceEquals(finished, answered.Task))
        {
            return await answered.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            _probing.Remove(id);
        }

        return TradeProbe.Not("the trade site did not answer in time");
    }

    /// <summary>The answer, or what happened instead of one.</summary>
    private async Task<TradeAnswer> Waited(
        int id, TaskCompletionSource<TradeAnswer> answered, CancellationToken cancelling)
    {
        using var giveUp = new CancellationTokenSource(Patience);
        using CancellationTokenSource both =
            CancellationTokenSource.CreateLinkedTokenSource(giveUp.Token, cancelling);

        Task finished = await Task
            .WhenAny(answered.Task, Task.Delay(Timeout.InfiniteTimeSpan, both.Token))
            .ConfigureAwait(false);

        if (ReferenceEquals(finished, answered.Task))
        {
            return await answered.Task.ConfigureAwait(false);
        }

        // Nothing came back. Forgotten here rather than left in the map, or a page that answers
        // late completes a question nobody is waiting on any more.
        lock (_gate)
        {
            _waiting.Remove(id);
        }

        return TradeAnswer.Not("the trade site did not answer in time");
    }

    /// <summary>Closes the window. The sign-in stays in the profile folder.</summary>
    public void Close()
    {
        TradeSessionWindow? window;
        lock (_gate)
        {
            window = _window;
        }

        try
        {
            window?.RunTaskOnUIThread(window.Close);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Already gone, which is the outcome being asked for.
        }
    }

    /// <summary>The window's whole life, on its own thread.</summary>
    /// <remarks>
    /// Failures stop HERE. An exception escaping a thread's entry point terminates the process -
    /// there is no enclosing catch on a thread start - and a browser window has no business
    /// ending a session that is reading the game.
    /// </remarks>
    private void Run(string league)
    {
        try
        {
            using var application = WindowThread.Started();
            using var window = new TradeSessionWindow(
                "PoEformance - PoE trade (sign in once)",
                $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(league)}",
                _profile,
                Heard);

            window.ResizeClient(1000, 820);
            window.Center();
            window.Show();
            window.SetForeground();

            lock (_gate)
            {
                _window = window;
                _starting = false;
                _failed = string.Empty;
            }

            application.Run();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failed = $"the trade window could not be opened: {exception.Message}";
            }

            Console.Error.WriteLine($"trade window failed: {exception.Message}");
        }
        finally
        {
            List<TaskCompletionSource<TradeAnswer>> stranded;
            List<TaskCompletionSource<TradeProbe>> strandedProbes;
            lock (_gate)
            {
                _window = null;

                // Cleared here as well as on success: a window that failed to come up at all
                // must not leave the door locked against ever trying again.
                _starting = false;
                stranded = [.. _waiting.Values];
                _waiting.Clear();
                strandedProbes = [.. _probing.Values];
                _probing.Clear();
            }

            // Everything still waiting is answered rather than left hanging: a question that
            // never completes is a queue that never moves again.
            foreach (TaskCompletionSource<TradeAnswer> one in stranded)
            {
                one.TrySetResult(TradeAnswer.Not("the trade window was closed"));
            }

            foreach (TaskCompletionSource<TradeProbe> one in strandedProbes)
            {
                one.TrySetResult(TradeProbe.Not("the trade window was closed"));
            }
        }
    }

    /// <summary>
    /// One message from the page: either the helper saying hello, or an answer.
    /// </summary>
    /// <remarks>
    /// Read with JsonDocument rather than a deserialiser because it is the only shape of JSON
    /// coming this way, and because the page is a file this tool does not control - a message
    /// that is not what was expected has to cost nothing.
    /// </remarks>
    private void Heard(string json)
    {
        try
        {
            using JsonDocument said = JsonDocument.Parse(json);
            JsonElement root = said.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("cmd", out JsonElement command)
                && command.ValueKind == JsonValueKind.String
                && command.GetString() == "tradeHelperReady")
            {
                lock (_gate)
                {
                    if (_window is { } window)
                    {
                        window.Ready = true;
                    }
                }

                return;
            }

            if (!root.TryGetProperty("id", out JsonElement which) || !which.TryGetInt32(out int id))
            {
                return;
            }

            bool ok = root.TryGetProperty("ok", out JsonElement fine) && fine.ValueKind == JsonValueKind.True;
            int status = root.TryGetProperty("status", out JsonElement code) && code.TryGetInt32(out int number)
                ? number
                : 0;

            string error = root.TryGetProperty("error", out JsonElement why) && why.ValueKind == JsonValueKind.String
                ? why.GetString() ?? string.Empty
                : string.Empty;

            // Whether this window still needs to be looked at, decided on every answer rather
            // than only on the first: a session that expires has to bring the window back.
            Settled(ok, status);

            bool probing;
            lock (_gate)
            {
                probing = _probing.ContainsKey(id);
            }

            if (probing)
            {
                FinishProbe(id, new TradeProbe(
                    ok,
                    status,
                    Text(root, "limits"),
                    Words(root, "tags"),
                    Count(root, "asked"),
                    Count(root, "got"),
                    Measure(root, "rate"),
                    Text(root, "raw"),
                    error));

                return;
            }

            Finish(id, new TradeAnswer(ok, status, Listings(root), error));
        }
        catch (JsonException)
        {
            // Not a message from the helper. The page is somebody else's and may post its own.
        }
    }

    private static IReadOnlyList<TradeListing> Listings(JsonElement root)
    {
        if (!root.TryGetProperty("listings", out JsonElement listings)
            || listings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<TradeListing>(listings.GetArrayLength());
        foreach (JsonElement one in listings.EnumerateArray())
        {
            if (one.ValueKind == JsonValueKind.Object
                && one.TryGetProperty("amount", out JsonElement amount)
                && amount.TryGetDouble(out double how)
                && one.TryGetProperty("currency", out JsonElement currency)
                && currency.ValueKind == JsonValueKind.String)
            {
                found.Add(new TradeListing(how, currency.GetString() ?? string.Empty));
            }
        }

        return found;
    }

    /// <summary>
    /// Takes the window off screen once the site is answering, and brings it back when it is not.
    /// </summary>
    /// <remarks>
    /// RUNS ON THE WINDOW'S OWN THREAD, because it is reached from the browser's message handler
    /// - which is why it touches the window directly instead of posting the work to it. Posting
    /// from the thread that would run the post is the shape that deadlocks.
    ///
    /// 401 AND 403 ARE THE ONLY REASONS TO COME BACK. They mean the sign-in needs a person.
    /// Every other failure - a timeout, a 429, a wall - is not something a visible window helps
    /// with, and showing it for those would make it flicker onto the screen mid-fight.
    /// </remarks>
    private void Settled(bool ok, int status)
    {
        TradeSessionWindow? window;
        lock (_gate)
        {
            window = _window;
        }

        if (window is null)
        {
            return;
        }

        try
        {
            if (status is 401 or 403)
            {
                window.Show();
                window.SetForeground();
                return;
            }

            if (ok && HideWhenSignedIn && window.IsVisible)
            {
                window.Hide();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The window is on its way out, which settles the question a different way.
        }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? string.Empty
            : string.Empty;

    private static int Count(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement found) && found.TryGetInt32(out int how) ? how : 0;

    private static double Measure(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement found) && found.TryGetDouble(out double how) ? how : 0;

    private static IReadOnlyList<string> Words(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement found) || found.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var said = new List<string>(found.GetArrayLength());
        foreach (JsonElement one in found.EnumerateArray())
        {
            if (one.ValueKind == JsonValueKind.String && one.GetString() is { Length: > 0 } word)
            {
                said.Add(word);
            }
        }

        return said;
    }

    private void FinishProbe(int id, TradeProbe answer)
    {
        TaskCompletionSource<TradeProbe>? waiting;
        lock (_gate)
        {
            _probing.Remove(id, out waiting);
        }

        waiting?.TrySetResult(answer);
    }

    private void Finish(int id, TradeAnswer answer)
    {
        TaskCompletionSource<TradeAnswer>? waiting;
        lock (_gate)
        {
            _waiting.Remove(id, out waiting);
        }

        waiting?.TrySetResult(answer);
    }

    private static void Forward(TradeSessionWindow window)
    {
        try
        {
            window.RunTaskOnUIThread(() =>
            {
                window.Show();
                window.SetForeground();
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The window is on its way out. The next query opens a new one.
        }
    }

    public void Dispose() => Close();
}
