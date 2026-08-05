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
    /// Opens the window and runs it to close. <paramref name="stateSource"/> is called
    /// whenever the page asks for the current state.
    /// </summary>
    public static void Run(string title, Func<ConfigState> stateSource)
    {
        ArgumentNullException.ThrowIfNull(stateSource);

        var thread = new Thread(() => RunOnThisThread(title, stateSource))
        {
            Name = "config-window",
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

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
    private static void RunOnThisThread(string title, Func<ConfigState> stateSource)
    {
        try
        {
            using var application = new Application();
            using var window = new ConfigWindow(title, json => Handle(json, stateSource));
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
    private static string? Handle(string json, Func<ConfigState> stateSource)
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

        return request?.Type switch
        {
            // "hello" is the page's load signal; both greet with the full state, because
            // the page renders whole states rather than patching fields.
            "hello" or "getState" =>
                JsonSerializer.Serialize(stateSource(), ConfigJsonContext.Default.ConfigState),
            _ => null,
        };
    }
}
