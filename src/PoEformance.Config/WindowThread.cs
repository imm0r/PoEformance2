using System.Runtime.Versioning;
using DirectN.Extensions.Utilities;

namespace PoEformance.Config;

/// <summary>
/// Starts the message pump for a window thread - and disarms the one that would otherwise
/// take the whole windowing subsystem with it.
/// </summary>
/// <remarks>
/// THE ONLY PLACE THAT MAY CONSTRUCT AN <see cref="Application"/>, and there is a sharp
/// reason for that rather than tidiness.
///
/// DirectN's window bookkeeping is process-wide, and by default it TURNS ITSELF OFF FOR GOOD
/// when the last non-background window in the process closes:
///
///     if (ExitAllOnLastWindowRemoved &amp;&amp; !AllExiting &amp;&amp; !_windows.Any(w => !w.IsBackground))
///     { AllExiting = true; ... AllExit(); }
///
/// and AllExit does <c>Interlocked.Exchange(ref _applications, null)</c>, which the Application
/// constructor reads:
///
///     if (apps == null) throw new DirectNException("0003: It's not possible to create
///     applications any more.");
///
/// A one-way latch, with no way back - AllExiting has a private setter and the dictionary is
/// gone. So closing ONE window makes every later one impossible, for the rest of the run.
///
/// That is not theoretical: somebody opened the config window, closed it, and then the trade
/// window could never be opened again - it failed with DXN0003 while the stash window went on
/// saying "sign in once" about a browser that was never going to appear.
///
/// The assumption behind the default is an app whose windows ARE its lifetime. Here they are
/// not: the tool is an overlay that reads memory, and both of these windows are things somebody
/// opens and closes while it runs. So the process-wide half is switched off and the per-thread
/// half is left alone - each Application still ends its own message loop when its own last
/// window closes, which is what lets these threads finish.
///
/// Set before the Application rather than once at start-up because it only has to be true
/// before the FIRST window exists, and this is the code path every window goes through.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowThread
{
    /// <summary>The message pump for the calling thread. One per thread, disposed with it.</summary>
    public static Application Started()
    {
        Application.ExitAllOnLastWindowRemoved = false;
        return new Application();
    }
}
