using System.Runtime.Versioning;
using System.Text.Json;
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
    /// Called for every other request. Returns true when it handled one, which makes the
    /// host answer with a fresh state.
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
    public static Thread Start(string title, Func<ConfigState> stateSource, Func<ConfigRequest, bool>? apply = null)
    {
        ArgumentNullException.ThrowIfNull(stateSource);

        var thread = new Thread(() => RunOnThisThread(title, stateSource, apply))
        {
            Name = "config-window",
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    /// <summary>Opens the window and blocks until it closes.</summary>
    public static void Run(string title, Func<ConfigState> stateSource, Func<ConfigRequest, bool>? apply = null)
        => Start(title, stateSource, apply).Join();

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
    private static void RunOnThisThread(string title, Func<ConfigState> stateSource, Func<ConfigRequest, bool>? apply)
    {
        try
        {
            using var application = new Application();
            using var window = new ConfigWindow(title, json => Handle(json, stateSource, apply));
            window.ResizeClient(1100, 780);
            window.Center();
            window.Show();
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
    private static string? Handle(string json, Func<ConfigState> stateSource, Func<ConfigRequest, bool>? apply)
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
            return State(stateSource);
        }

        // Anything else is a command. A handled one answers with the WHOLE state rather
        // than an acknowledgement, so what the page displays is always what the host
        // actually holds - a rejected or clamped value shows up as itself, not as the
        // value that was sent.
        try
        {
            return apply is not null && apply(request) ? State(stateSource) : null;
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
