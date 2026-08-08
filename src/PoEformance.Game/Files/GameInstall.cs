using System.Diagnostics;

namespace PoEformance.Game.Files;

/// <summary>
/// Where Path of Exile 2 is installed.
/// </summary>
/// <remarks>
/// ASKED OF THE GAME ITSELF, not of the person using this. The tool is already attached to the
/// running game, and a running process knows exactly where its own executable is - so the folder
/// is a property of what is already open rather than a setting somebody has to find and type,
/// and it cannot be stale or point at a second copy.
///
/// The usual places are only a fallback, for reading an install with the game shut. They are
/// guesses and are treated as such: a folder counts only when the files this wants are in it.
/// </remarks>
public static class GameInstall
{
    /// <summary>
    /// What the game's process is called, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// One list, because finding the process to READ and finding the process to ask where the
    /// files are is the same question - and two copies of it drift the moment a launcher adds
    /// a name.
    /// </remarks>
    public static readonly string[] Names =
        ["PathOfExileSteam", "PathOfExile", "PathOfExile_x64", "PathOfExile_x64Steam"];

    /// <summary>
    /// Finds the install, or returns null when there is not one to be found.
    /// </summary>
    /// <param name="preferred">A folder somebody named, which wins when it holds an install.</param>
    public static string? Find(string? preferred = null)
    {
        if (Holds(preferred))
        {
            return preferred;
        }

        if (FromRunningGame() is { } running && Holds(running))
        {
            return running;
        }

        return Usual().FirstOrDefault(Holds);
    }

    /// <summary>Whether a folder is an install - by the files, not by the name.</summary>
    public static bool Holds(string? folder)
        => !string.IsNullOrWhiteSpace(folder)
            && Directory.Exists(folder)
            && (File.Exists(Path.Combine(folder, "Bundles2", "_.index.bin"))
                || File.Exists(Path.Combine(folder, "Content.ggpk")));

    /// <summary>The folder of the running game, or null when it is not running or will not say.</summary>
    public static string? FromRunningGame()
    {
        foreach (string name in Names)
        {
            foreach (Process running in Process.GetProcessesByName(name))
            {
                try
                {
                    if (running.MainModule?.FileName is { } exe)
                    {
                        return Path.GetDirectoryName(exe);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process that exited between the two calls, or one this cannot look
                    // inside - a bitness mismatch, or no rights. Neither is worth stopping for:
                    // the art simply comes from the fallback.
                }
                finally
                {
                    running.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>The places an install usually is, for reading one with the game shut.</summary>
    private static IEnumerable<string> Usual()
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "C:\\", "D:\\", "E:\\",
        })
        {
            if (root.Length == 0)
            {
                continue;
            }

            yield return Path.Combine(root, "Grinding Gear Games", "Path of Exile 2");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "Path of Exile 2");
            yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", "Path of Exile 2");
        }
    }
}
