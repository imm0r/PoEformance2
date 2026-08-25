using System.Runtime.Versioning;
using System.Text.Json;
using DirectN;
using DirectN.Extensions.Utilities;

namespace PoEformance.Config;

/// <summary>
/// Runs the config window with its own message loop, and owns the message protocol.
/// </summary>
/// <remarks>
/// WebView2 wants an STA thread with a pump, and the rest of the app is neither - so the
/// window gets a dedicated thread and the caller blocks until it closes. Cross-thread
/// traffic is JSON strings in both directions, which keeps the seam as thin as the process
/// boundary the AHK tool had.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ConfigWindowHost
{
    /// <summary>
    /// Opens the window on its own thread and returns immediately.
    /// </summary>
    /// <param name="stateSource">Called whenever the page asks for the current state.</param>
    /// <param name="apply">
    /// Called for every other request. Returns null when it does not handle one, an empty
    /// string to have the host answer with a fresh state, or its own JSON reply.
    /// </param>
    /// <param name="alwaysOnTop">
    /// Keep the window above everything else. Needed when the game is running: it is
    /// normally fullscreen, and an ordinary window opened behind it is invisible and
    /// effectively unreachable - which reads as "the config window did not open".
    /// </param>
    /// <returns>The window's thread - join it to wait for the window to close.</returns>
    /// <remarks>
    /// Not joining is what lets the config window run WHILE the overlay does, which is the
    /// difference between a settings page and a settings file: a switch that only takes
    /// effect after a restart is not a switch anyone reaches for mid-fight.
    ///
    /// Both callbacks run on the window's thread, so whatever they touch is shared with
    /// the reader. The engine takes its configuration as one atomic reference swap for
    /// exactly this reason.
    /// </remarks>
    public static Thread Start(
        string title,
        Func<ConfigState> stateSource,
        Func<ConfigRequest, string?>? apply = null,
        bool alwaysOnTop = false)
    {
        ArgumentNullException.ThrowIfNull(stateSource);

        var thread = new Thread(() => RunOnThisThread(title, stateSource, apply, alwaysOnTop))
        {
            Name = "config-window",
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>Opens the window and blocks until it closes.</summary>
    public static void Run(
        string title,
        Func<ConfigState> stateSource,
        Func<ConfigRequest, string?>? apply = null,
        bool alwaysOnTop = false)
        => Start(title, stateSource, apply, alwaysOnTop).Join();

    /// <summary>
    /// Runs the window, and contains any failure to this thread.
    /// </summary>
    /// <remarks>
    /// An exception escaping a thread's entry point terminates the PROCESS - there is no
    /// enclosing catch on a thread start. That is how a teardown slip in the config window
    /// took the whole tool down with it, overlay session included. The config window is an
    /// auxiliary view; it has no business ending the process, so its failures stop here and
    /// are reported instead.
    /// </remarks>
    private static void RunOnThisThread(
        string title, Func<ConfigState> stateSource, Func<ConfigRequest, string?>? apply, bool alwaysOnTop)
    {
        try
        {
            using var application = WindowThread.Started();
            using var window = new ConfigWindow(title, json => Handle(json, stateSource, apply));
            window.ResizeClient(1100, 780);
            window.Center();
            window.Show();

            if (alwaysOnTop)
            {
                // SetForeground alone is not enough against a fullscreen game: Windows
                // refuses foreground changes from a process the user is not interacting
                // with, so the window ends up behind the game and looks like it never
                // opened. Topmost is the only placement the game cannot end up in front of.
                window.SetWindowPos(
                    HWND.TOPMOST, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                        | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            }

            window.SetForeground();
            application.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"config window failed: {exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
        }
    }

    /// <summary>
    /// The protocol: one request in, at most one reply out.
    /// </summary>
    /// <remarks>
    /// A malformed message answers with nothing rather than throwing - the page is editable
    /// on disk, so a typo during UI work must cost a dead button, never a crashed host.
    /// </remarks>
    private static string? Handle(string json, Func<ConfigState> stateSource, Func<ConfigRequest, string?>? apply)
    {
        ConfigRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ConfigRequest);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null)
        {
            return null;
        }

        // "hello" is the page's load signal; both greet with the full state, because the
        // page renders whole states rather than patching fields.
        if (request.Type is "hello" or "getState")
        {
            // Building a state READS GAME MEMORY on this thread, and a read can throw at any
            // zone change. One thrown poll must cost one missing answer - the page asks again
            // in a second - not the message channel it would have taken down by escaping.
            try
            {
                return State(stateSource);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"state build failed: {exception.Message}");
                return null;
            }
        }

        // Anything else is a command. A handled one answers with the WHOLE state rather
        // than an acknowledgement, so what the page displays is always what the host
        // actually holds - a rejected or clamped value shows up as itself, not as the
        // value that was sent.
        try
        {
            // A handler answers with its own JSON when it has one - the map layout is far
            // too big to ride along with every state - and with an empty string to mean
            // "handled, send the state". Null is "not mine".
            string? reply = apply?.Invoke(request);
            return reply is null ? null : reply.Length == 0 ? State(stateSource) : reply;
        }
        catch (JsonException)
        {
            // A malformed payload is a page-side typo, and the page is editable on disk.
            return null;
        }
    }

    private static string State(Func<ConfigState> stateSource)
        => JsonSerializer.Serialize(stateSource(), ConfigJsonContext.Default.ConfigState);
}
