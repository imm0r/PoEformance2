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
    private readonly HashSet<string> _shared;

    private StatDescriptions(
        Dictionary<string, string> lines, int grouped, string source, HashSet<string>? shared = null)
    {
        _lines = lines;
        Grouped = grouped;
        Source = source;
        _shared = shared ?? [];
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
        var shared = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string stat, StatDescription block) in blocks)
        {
            if (block.Stats.Count == 1)
            {
                lines[stat] = block.Template;
            }
            else
            {
                // KEPT BY NAME THOUGH THE SENTENCE IS DROPPED, so that a caller can tell the two
                // ways of having no wording apart: a stat the game words as part of a group, and a
                // stat the game does not word at all. Those are different findings - the first is
                // reachable by a caller that holds every value in the group, the second is
                // engine-internal and never had a sentence to find.
                shared.Add(stat);
            }
        }

        return new StatDescriptions(
            lines,
            shared.Count,
            $"the game's own .csd files ({lines.Count} sentences, {shared.Count} dropped as multi-stat)",
            shared)
        {
            FromGame = true,
        };
    }

    /// <summary>
    /// Whether the game words this stat only as part of a group, so no sentence was kept.
    /// </summary>
    /// <remarks>
    /// ANSWERED BY BOTH ROUTES, which was worth checking rather than assuming: the TSV's fourth
    /// column is the group, written as the comma-separated ids that share the template, so the
    /// export carries the same fact the install does. <see cref="Load"/> was already reading it to
    /// decide what to drop and simply was not keeping the names.
    ///
    /// SO A "NO" MEANS NO on either route, and the distinction it buys is the useful one: a stat
    /// with no sentence anywhere is engine bookkeeping that never had one, while a stat that is
    /// only worded as part of a group is reachable by a caller holding every value in that group.
    /// </remarks>
    public bool Shared(string? statId) => statId is { Length: > 0 } && _shared.Contains(statId);

    /// <summary>
    /// The sentence for a stat id, with <c>{0}</c> where its value goes, or null.
    /// </summary>
    public string? Of(string? statId)
        => statId is { Length: > 0 } && _lines.TryGetValue(statId, out string? line) ? line : null;

    /// <summary>
    /// One argument's hole in a template filled in, in every spelling the game writes it.
    /// </summary>
    /// <remarks>
    /// THE PLACEHOLDERS CARRY A FORMAT, and that is the part that is easy to miss. <c>{0:+d}</c>
    /// means show the sign - it is how "+79 to maximum Life" gets its plus - and replacing the
    /// bare <c>{0}</c> alone leaves the line reading as though the table had no entry for it.
    /// Measured over the 16802-row export: 11501 holes are written <c>{0}</c>, 1038 <c>{0:+d}</c>,
    /// 37 <c>{0:d}</c>, and 148 leave the index out altogether as <c>{}</c> or <c>{:+d}</c> - so
    /// four of those five spellings are ones a reader would otherwise see raw.
    ///
    /// ONLY THIS ARGUMENT'S HOLE IS FILLED. A line built from two stats keeps the other's marker,
    /// because a caller holds one value, and guessing at the second would put this number in the
    /// wrong half of somebody else's sentence.
    ///
    /// A TEMPLATE WITH NO HOLE COMES BACK UNCHANGED, which is right rather than a shortfall:
    /// "Maim on Hit" is the whole sentence for a flag, and the game shows no number for it either.
    /// </remarks>
    /// <param name="template">The wording, as <see cref="Of"/> hands it out.</param>
    /// <param name="argument">Which hole this value fills. Zero for a single-stat line.</param>
    /// <param name="min">The value, or the low end of one that rolls.</param>
    /// <param name="max">The same number again where it does not roll.</param>
    public static string Fill(string template, int argument, int min, int max)
    {
        ArgumentNullException.ThrowIfNull(template);

        string plain = min == max
            ? min.ToString(System.Globalization.CultureInfo.InvariantCulture)

            // A DASH BETWEEN A NEGATIVE MINIMUM AND ITS MAXIMUM READS AS A SUBTRACTION: "(-20-20)"
            // is not a range anybody can parse back. Those few are spelled out instead.
            : min < 0
                ? $"({min} to {max})"
                : $"({min}-{max})";

        string signed = min >= 0 ? "+" + plain : plain;

        string filled = Put(
            template, argument.ToString(System.Globalization.CultureInfo.InvariantCulture), plain, signed);

        // The game also writes the FIRST hole without its index, and that hole is argument zero.
        return argument == 0 ? Put(filled, string.Empty, plain, signed) : filled;
    }

    /// <summary>Every spelling of one hole, replaced. The bare form first; none contains another.</summary>
    private static string Put(string template, string index, string plain, string signed)
        => template
            .Replace($"{{{index}}}", plain, StringComparison.Ordinal)
            .Replace($"{{{index}:+d}}", signed, StringComparison.Ordinal)
            .Replace($"{{{index}:d}}", plain, StringComparison.Ordinal)
            .Replace($"{{{index}:-d}}", plain, StringComparison.Ordinal);

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
        var shared = new HashSet<string>(StringComparer.Ordinal);
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

                // THE TWO REASONS COINCIDE EXACTLY on the 0.5.5 export - 1257 rows belong to a
                // multi-stat group, and the 661 of them that fill a hole other than the first are
                // a subset of those, with no row dropped for the index alone. So the name is kept
                // under one heading rather than two: the game words this stat only in company.
                if (parts[2] != "0" || parts[3].Contains(',', StringComparison.Ordinal))
                {
                    shared.Add(parts[0]);
                    continue;
                }

                // Unwrapped HERE TOO and not only on the install's side, because the two are
                // compared against each other - see Against. A wrapper stripped on one route and
                // left on the other would turn 79 rows the two agree about into 79 disagreements,
                // and the check that exists to find drift would be reporting its own tidy-up.
                lines[parts[0]] = StatDescriptionFiles.Unwrap(parts[1]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }

        return new StatDescriptions(
            lines,
            shared.Count,
            $"data/{Path.GetFileName(path)} ({lines.Count} sentences)"
            + " - right only while somebody re-exports it",
            shared);
    }
}
