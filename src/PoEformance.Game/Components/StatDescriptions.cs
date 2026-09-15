using PoEformance.Game.Files;

namespace PoEformance.Game.Components;

/// <summary>
/// What the game says a stat MEANS - the sentence, with a hole where its value goes.
/// </summary>
/// <remarks>
/// KEYED BY NAME, WHICH IS THE POINT. Every other table this project ships is keyed by a dat ROW
/// INDEX, and an index is a position: a league that inserts a row moves every entry after it, and
/// the file is wrong from that patch with nothing to say so. This one is keyed by the stat's own
/// id - <c>map_num_extra_shrines</c> - which the game does not renumber. It goes out of date only
/// when GGG rewords something, and a reworded sentence is a cosmetic difference rather than a
/// confidently wrong answer about a different stat.
///
/// SO THE JOIN IS THE THING. The row index comes out of memory (StatTable), the index becomes a
/// name, and the name becomes a sentence here. No part of that chain carries an index across a
/// patch boundary, which is what makes it hold.
///
/// AND THE INSTALL SAYS IT ITSELF, which is what <see cref="FromInstall"/> is. The sentences live
/// in the game's own <c>*.csd</c> files; tools/poe_tools.py build-stat-desc is what unpacked them
/// into the TSV, and <see cref="StatDescriptionFiles"/> now reads the same files straight out of
/// the install. The TSV stays as the fallback for a session with no install to read - and as the
/// thing to measure the reader against, which is the only check available without one.
///
/// ONLY SINGLE-STAT LINES ARE KEPT. The game groups stats that share one sentence - "Damage Gained
/// as Fire on {0} Heat Consumption@{1}%" is written once for three stats, each filling a different
/// hole - and a caller here holds ONE stat's value, so it cannot fill the others. Rendering such a
/// line from one value would print a sentence about numbers it does not have. They are dropped and
/// counted rather than half-filled; on the 0.5.5 export that is 1257 of 16802, and every one of the
/// 61 stats the atlas actually shows is in the 15545 that remain.
/// </remarks>
public sealed class StatDescriptions
{
    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static StatDescriptions Empty => new([], 0, "nothing - no stat descriptions were read");

    private readonly Dictionary<string, string> _lines;

    private StatDescriptions(Dictionary<string, string> lines, int grouped, string source)
    {
        _lines = lines;
        Grouped = grouped;
        Source = source;
    }

    /// <summary>How many sentences are known.</summary>
    public int Count => _lines.Count;

    /// <summary>How many were dropped for belonging to a multi-stat group. See the remarks.</summary>
    public int Grouped { get; }

    /// <summary>
    /// Where these sentences came from, for the line on screen that says so.
    /// </summary>
    /// <remarks>
    /// THE SAME REASON StatNames HAS ONE. A sentence read out of the install and a sentence read
    /// out of a six-month-old export look identical on the atlas - the difference only shows on the
    /// handful the game has since reworded, which is exactly the case nobody would notice. This
    /// says which is in force.
    /// </remarks>
    public string Source { get; }

    /// <summary>Whether these came out of the install rather than out of the shipped table.</summary>
    public bool FromGame { get; private init; }

    /// <summary>
    /// The sentences the install itself holds, read from its <c>.csd</c> files.
    /// </summary>
    /// <remarks>
    /// WHAT THIS IS FOR: the TSV beside it is right only while somebody remembers to re-export it,
    /// and the install cannot go stale - it IS the game. The parse follows the same rules as the
    /// export, so the two can be compared, and that comparison is the check: see
    /// <c>AtlasWatch.Check</c>, which reports how many of the two agree on a machine that has both.
    ///
    /// SAME SINGLE-STAT RULE as the TSV, applied here where the blocks still say which stats share
    /// a sentence: a block covering more than one stat is dropped and counted, because a caller
    /// holds one stat's value and cannot fill the other holes. See the remarks on the class.
    /// </remarks>
    /// <param name="files">The opened install, or null where there is none.</param>
    /// <param name="paths">
    /// The <c>.csd</c> paths, when the caller has already walked for them - see
    /// <see cref="Files.GameFiles.Names"/> for why it would have. Null walks for them here.
    /// </param>
    public static StatDescriptions FromInstall(GameFiles? files, IEnumerable<string>? paths = null)
    {
        Dictionary<string, StatDescription> blocks = StatDescriptionFiles.Read(files, paths);
        if (blocks.Count == 0)
        {
            return Empty;
        }

        var lines = new Dictionary<string, string>(blocks.Count, StringComparer.Ordinal);
        int grouped = 0;
        foreach ((string stat, StatDescription block) in blocks)
        {
            if (block.Stats.Count == 1)
            {
                lines[stat] = block.Template;
            }
            else
            {
                grouped++;
            }
        }

        return new StatDescriptions(
            lines,
            grouped,
            $"the game's own .csd files ({lines.Count} sentences, {grouped} dropped as multi-stat)")
        {
            FromGame = true,
        };
    }

    /// <summary>
    /// The sentence for a stat id, with <c>{0}</c> where its value goes, or null.
    /// </summary>
    public string? Of(string? statId)
        => statId is { Length: > 0 } && _lines.TryGetValue(statId, out string? line) ? line : null;

    /// <summary>
    /// How this table compares with another, which is the only check there is without the game.
    /// </summary>
    /// <remarks>
    /// THE READER CANNOT BE TESTED AGAINST THE GAME FROM HERE - nothing in this project has an
    /// install to parse, and a recording holds memory reads rather than files. What it CAN be
    /// tested against is the export that the same .csd files produced by a completely different
    /// route: a Python tool over files unpacked by a third one. Two independent paths landing on
    /// the same sentence for the same stat is the strongest thing available, and where they differ
    /// the difference is printed rather than counted, because a count says nothing about which of
    /// them is wrong.
    /// </remarks>
    /// <param name="other">What to compare against, usually the shipped export.</param>
    /// <param name="examples">How many disagreements to spell out.</param>
    public IReadOnlyList<string> Against(StatDescriptions? other, int examples = 8)
    {
        if (other is null || other.Count == 0)
        {
            return [$"  nothing to compare {Count} sentences against"];
        }

        int agree = 0;
        int disagree = 0;
        int missing = 0;
        var shown = new List<string>();
        foreach ((string stat, string line) in _lines)
        {
            if (other.Of(stat) is not { } theirs)
            {
                missing++;
                continue;
            }

            if (string.Equals(theirs, line, StringComparison.Ordinal))
            {
                agree++;
                continue;
            }

            disagree++;
            if (disagree <= examples)
            {
                shown.Add($"    {stat}");
                shown.Add($"      read: {line}");
                shown.Add($"      file: {theirs}");
            }
        }

        var said = new List<string>
        {
            $"  {agree} of {Count} agree with the {other.Count} compared against;"
                + $" {disagree} differ, {missing} are not in it",
        };

        said.AddRange(shown);
        return said;
    }

    /// <summary>
    /// Loads the TSV. A missing file is not an error - the stats still read, just as ids.
    /// </summary>
    /// <remarks>
    /// Columns: stat id, template, which hole this stat fills, and the group it belongs to. A line
    /// filling anything but the first hole, or sharing its group, is skipped - see the remarks on
    /// the class for why half-filling one would be worse than leaving it as a number.
    /// </remarks>
    public static StatDescriptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        int grouped = 0;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                if (parts.Length < 4 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    continue;
                }

                if (parts[2] != "0" || parts[3].Contains(',', StringComparison.Ordinal))
                {
                    grouped++;
                    continue;
                }

                lines[parts[0]] = parts[1];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }

        return new StatDescriptions(
            lines,
            grouped,
            $"data/{Path.GetFileName(path)} ({lines.Count} sentences)"
            + " - right only while somebody re-exports it");
    }
}
