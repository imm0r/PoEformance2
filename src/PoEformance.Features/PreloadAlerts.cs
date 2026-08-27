using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// One loaded file worth being told about, and how to say it.
/// </summary>
/// <remarks>
/// THE PATH IS EXACT, and that is the decision this whole file turns on. The list it replaced
/// matched FRAGMENTS - "/Breach/" caught every breach file in one line - which is fewer entries
/// and survives a renamed file. It also produced, on its first live run, eight league mechanics
/// in one map: a fragment matches anything that merely mentions the thing, and an area mentions
/// a great deal it does not contain.
///
/// The two defences that layer needed both disappear here rather than being reimplemented. A
/// file COUNT was needed to tell "Breach 40" from "Delirium 1", because a fragment could not say
/// which it had found; an exact path IS its own evidence, so there is nothing to count. And a
/// list of paths that cannot testify - the Atlas map pins that named a mechanic in every area,
/// forever - is not needed when nothing matches unless somebody put it in the list on purpose.
///
/// What it costs is honest: one entry per file rather than per mechanic, and a shipped list of
/// nothing, because the exact paths a PoE2 area loads are not something this project has
/// captured. They are added from the area list, one click per row, which is the same way the
/// reference tool tells people to build theirs.
/// </remarks>
/// <param name="Path">
/// The loaded file's full path, matched exactly. Empty means the entry says nothing - see
/// <see cref="SaysNothing"/>.
/// </param>
/// <param name="Called">What to show instead of the path. Empty falls back to the file's name.</param>
/// <param name="Colour">Packed ABGR, like every other colour here.</param>
/// <param name="Enabled">Off keeps the entry without acting on it. Deleting is the other way.</param>
/// <param name="Log">Also write a line to disk when the area turns out to have it.</param>
public sealed record PreloadAlertEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("called")] string Called = "",
    [property: JsonPropertyName("colour")] uint Colour = PreloadAlerts.Plain,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("log")] bool Log = false)
{
    /// <summary>Whether this entry says anything at all about what to look for.</summary>
    public bool SaysNothing => string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// What to put on screen for it.
    /// </summary>
    /// <remarks>
    /// The file's own name when nothing was typed, rather than the whole path. A path is eighty
    /// characters of folders and one word that means something, and the window it goes in is a
    /// corner of the screen read at a glance.
    /// </remarks>
    public string Shown
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Called))
            {
                return Called;
            }

            int cut = Path.LastIndexOf('/');
            return cut >= 0 && cut < Path.Length - 1 ? Path[(cut + 1)..] : Path;
        }
    }
}

/// <summary>
/// The curated list, and what an area turns out to hold of it.
/// </summary>
/// <remarks>
/// ORDER IS PRIORITY, with no field for it. The reference carries a priority number and then
/// spends a container keeping it in step with the list's order, decrementing indexes on every
/// removal; the two can disagree, and when they do the file says one thing and the window shows
/// another. A list that IS the order cannot drift from itself.
/// </remarks>
public static class PreloadAlerts
{
    /// <summary>The colour an entry gets when nobody chose one - packed ABGR, opaque white.</summary>
    public const uint Plain = 0xFFFFFFFF;

    /// <summary>
    /// Which entries the area turned out to hold, in the list's own order.
    /// </summary>
    /// <remarks>
    /// CASE-INSENSITIVE, deliberately, even though the game is consistent about its own casing.
    /// An entry that differs only in case would match nothing and say nothing about why - and a
    /// setting whose only symptom is silence is the exact failure this project has already paid
    /// for once, in a key binding that read a virtual-key code as two digits.
    ///
    /// Disabled entries are dropped here rather than by the caller, so that everything drawing
    /// this list is drawing the same list.
    /// </remarks>
    public static IReadOnlyList<PreloadAlertEntry> Found(
        IReadOnlyList<PreloadAlertEntry>? entries, IEnumerable<string>? loaded)
    {
        if (entries is null || entries.Count == 0 || loaded is null)
        {
            return [];
        }

        if (Lookup(loaded) is not HashSet<string> here)
        {
            return [];
        }

        var found = new List<PreloadAlertEntry>();
        foreach (PreloadAlertEntry entry in entries)
        {
            if (entry is { Enabled: true, SaysNothing: false } && here.Contains(entry.Path))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    /// <summary>
    /// The loaded paths in the form the editor asks about them, or null when there are none.
    /// </summary>
    /// <remarks>
    /// BUILT ONCE PER FRAME, not once per row. The editor asks "is this row in the area" for
    /// every row it draws, an area loads a few thousand paths, and a list can run to hundreds
    /// of rows - so a linear scan per row is a few hundred thousand string comparisons per
    /// frame, in a window that redraws at the refresh rate.
    /// </remarks>
    public static HashSet<string>? Lookup(IEnumerable<string>? loaded)
    {
        if (loaded is null)
        {
            return null;
        }

        var here = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);
        return here.Count > 0 ? here : null;
    }

    /// <summary>Whether one path is in a set of loaded ones, by the same rule as <see cref="Found"/>.</summary>
    /// <remarks>
    /// Exposed so an editor can show, per row, whether that row matches the area being stood in.
    /// A list of paths nobody can check is a list of guesses, and a typo in one is otherwise
    /// indistinguishable from an area that simply does not have the thing.
    /// </remarks>
    public static bool Here(string? path, IReadOnlySet<string>? loaded)
        => !string.IsNullOrWhiteSpace(path) && loaded is not null && loaded.Contains(path);

    /// <summary>The line written to disk for a found entry.</summary>
    /// <remarks>
    /// The area HASH rather than its name, because that is what identifies one instance of an
    /// area - two runs of the same map share a name and nothing else. Sortable timestamp first,
    /// so the file reads in order and can be grepped by day.
    /// </remarks>
    public static string LogLine(DateTimeOffset when, uint area, PreloadAlertEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{when:yyyy-MM-dd HH:mm:ss}  area {area}  {entry.Shown}  {entry.Path}";
    }
}

/// <summary>
/// Loads and saves the curated list, in its own file.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE SWITCHES on purpose, and it is the exact matching that earns the split: a
/// list of full paths is the one part of this configuration worth handing to somebody else, and
/// a file that also carried "hide when in town" would make importing one person's list overwrite
/// another person's window. The reference keeps the same two files apart for the same reason.
/// </remarks>
public static class PreloadAlertStore
{
    public static string DefaultPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "config", "preload-alerts.json");

    /// <summary>Reads the list, falling back to an empty one on any problem.</summary>
    /// <remarks>
    /// Entries that say nothing are dropped on the way in, and duplicates are collapsed to the
    /// FIRST of them - both arrive from a hand-edited file, and a duplicate would otherwise draw
    /// the same line twice while only one of the two could be edited.
    /// </remarks>
    public static IReadOnlyList<PreloadAlertEntry> Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return [];
            }

            using FileStream stream = File.OpenRead(file);
            List<PreloadAlertEntry>? loaded =
                JsonSerializer.Deserialize(stream, PreloadJsonContext.Default.ListPreloadAlertEntry);

            return loaded is null ? [] : Tidy(loaded);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes the list, returning false when it could not.</summary>
    public static bool Save(IReadOnlyList<PreloadAlertEntry> entries, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(
                stream, Tidy(entries), PreloadJsonContext.Default.ListPreloadAlertEntry);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes an area's whole loaded-file list out, so it can be read away from the game.
    /// </summary>
    /// <returns>The file written, or null when it could not be.</returns>
    /// <remarks>
    /// THE LIST IS ONLY EVER ON SCREEN OTHERWISE, and that is a real gap rather than a
    /// convenience: deciding what belongs in a watch list means comparing paths against each
    /// other, and questions like "does this one carry a variant suffix" cannot be answered by
    /// scrolling a window on the machine running the game.
    ///
    /// A plain list of paths, one per line, under a few lines of comment naming the area. Plain
    /// because the next thing anybody does with it is grep it.
    /// </remarks>
    public static string? Dump(
        uint area, IReadOnlyList<string>? all, string note = "", string? path = null)
    {
        if (all is null)
        {
            return null;
        }

        try
        {
            string file = path
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "preloads", $"area-{area}.txt");

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

            var lines = new List<string>(all.Count + 4)
            {
                $"# area {area}",
                $"# {all.Count} files",
            };

            if (note.Length > 0)
            {
                lines.Add($"# {note}");
            }

            lines.Add("#");

            // SORTED, because the reason to read this is comparison - between two areas, or
            // between one area and a list somebody is building. An arbitrary order makes a diff
            // of two captures useless.
            var sorted = new List<string>(all);
            sorted.Sort(StringComparer.Ordinal);
            lines.AddRange(sorted);

            File.WriteAllLines(file, lines);
            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where the record of what turned up goes.</summary>
    public static string LogPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "preloads-found.log");

    /// <summary>
    /// Appends a line per found entry that asked to be logged.
    /// </summary>
    /// <returns>How many lines were written, so a caller can say nothing happened.</returns>
    /// <remarks>
    /// APPEND, never rewrite. The point of the file is what turned up over a league, so a run
    /// that opened it for writing would answer the question by destroying it.
    ///
    /// A failure is swallowed rather than thrown: this is called from the read loop on the way
    /// into an area, and a locked file is not a reason to stop reading the game.
    /// </remarks>
    public static int Log(
        uint area, IReadOnlyList<PreloadAlertEntry>? found, DateTimeOffset when, string? path = null)
    {
        if (found is null || found.Count == 0)
        {
            return 0;
        }

        var lines = new List<string>();
        foreach (PreloadAlertEntry entry in found)
        {
            if (entry.Log)
            {
                lines.Add(PreloadAlerts.LogLine(when, area, entry));
            }
        }

        if (lines.Count == 0)
        {
            return 0;
        }

        try
        {
            string file = path ?? LogPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.AppendAllLines(file, lines);
            return lines.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Drops the empty entries and the repeats, keeping the order.</summary>
    public static List<PreloadAlertEntry> Tidy(IReadOnlyList<PreloadAlertEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PreloadAlertEntry>(entries.Count);
        foreach (PreloadAlertEntry entry in entries)
        {
            if (entry is not null && !entry.SaysNothing && seen.Add(entry.Path))
            {
                kept.Add(entry);
            }
        }

        return kept;
    }
}
