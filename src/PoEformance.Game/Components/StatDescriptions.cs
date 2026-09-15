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
/// WHERE IT COMES FROM: tools/poe_tools.py build-stat-desc, over the <c>*.csd</c> stat-description
/// files unpacked from the install. That is also the route by which this file could stop being a
/// file at all - the tool already reads arbitrary install files through GameFiles - but parsing the
/// csd format is its own piece of work and this is not it.
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
    public static StatDescriptions Empty => new([], 0);

    private readonly Dictionary<string, string> _lines;

    private StatDescriptions(Dictionary<string, string> lines, int grouped)
    {
        _lines = lines;
        Grouped = grouped;
    }

    /// <summary>How many sentences are known.</summary>
    public int Count => _lines.Count;

    /// <summary>How many were dropped for belonging to a multi-stat group. See the remarks.</summary>
    public int Grouped { get; }

    /// <summary>
    /// The sentence for a stat id, with <c>{0}</c> where its value goes, or null.
    /// </summary>
    public string? Of(string? statId)
        => statId is { Length: > 0 } && _lines.TryGetValue(statId, out string? line) ? line : null;

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

        return new StatDescriptions(lines, grouped);
    }
}
