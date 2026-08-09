using System.Text.RegularExpressions;

namespace PoEformance.Core.Tests;

/// <summary>
/// That opening a window never costs the tool every window after it.
/// </summary>
/// <remarks>
/// DirectN's window bookkeeping is process-wide and, by default, turns itself off FOR GOOD when
/// the last non-background window closes: <c>AllExit</c> swaps its application dictionary for
/// null, and the Application constructor throws <c>DXN0003: It's not possible to create
/// applications any more</c> from then on. There is no way back - the flag has a private setter
/// and the dictionary is gone.
///
/// What that looked like: somebody opened the config window, closed it, and the trade window
/// could never be opened again for the rest of the run.
///
/// The cure is one assignment, made in <c>WindowThread.Started</c> before the first window can
/// exist. What this checks is that it stays the only door - a <c>new Application()</c> written
/// anywhere else brings the latch straight back, and it cannot be caught by running the tests,
/// because the whole windowing half is Windows-only and none of it runs here.
/// </remarks>
public class WindowLifetimeTests
{
    [Fact]
    public void ONLYOnePlaceInTheToolMayStartAWindowThread()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        string[] elsewhere =
        [
            .. Directory
                .EnumerateFiles(Path.Combine(dir.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "WindowThread.cs")
                .Where(file => Regex.IsMatch(File.ReadAllText(file), @"new\s+Application\s*\("))
                .Select(file => Path.GetFileName(file) ?? file),
        ];

        Assert.Empty(elsewhere);

        // And that the one place still disarms it. A file that no longer says this is a file
        // that has quietly gone back to the default.
        string door = Path.Combine(dir.FullName, "src", "PoEformance.Config", "WindowThread.cs");
        Assert.Contains(
            "Application.ExitAllOnLastWindowRemoved = false;",
            File.ReadAllText(door),
            StringComparison.Ordinal);
    }
}
