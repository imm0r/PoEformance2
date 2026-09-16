using System.Text;

namespace PoEformance.Game.Files;

/// <summary>One block of a stat-description file: which stats it covers, and what it says.</summary>
/// <param name="Stats">The stat ids, in the order the block lists them.</param>
/// <param name="Template">The English wording, with <c>{0}</c> where each stat's value goes.</param>
public readonly record struct StatDescription(IReadOnlyList<string> Stats, string Template);

/// <summary>
/// The game's own stat descriptions, read out of the install's <c>.csd</c> files.
/// </summary>
/// <remarks>
/// THIS IS WHERE data/stat_desc_map.tsv COMES FROM, and reading it here is what makes that file
/// unnecessary. The project's own tools/poe_tools.py unpacks <c>Data/StatDescriptions/**/*.csd</c>
/// and parses exactly this format; the install has the same files, and GameFiles already reads
/// arbitrary paths out of it.
///
/// THE FORMAT, from that reference rather than guessed at. A file is UTF-16 with a byte-order mark
/// (some are UTF-8), and reads as a run of blocks:
///
/// <code>
///   description
///       1 map_num_extra_shrines
///           1 1 "Area contains an additional Shrine"
///           lang "German"
///           1 1 "..."
/// </code>
///
/// The line after <c>description</c> is a COUNT followed by that many stat ids. The lines under it
/// are conditions and a quoted template; the first quoted line is the English one, because English
/// is what a file leads with - a <c>lang "X"</c> line switches to another language and everything
/// after it is that language until the next block.
///
/// WHY THE FIRST QUOTED LINE AND NOT THE LAST. A block lists one line per value range - "an
/// additional Shrine" against "{0} additional Shrines" - and they are all the same sentence with
/// different grammar. Taking the first is what the reference does, and picking a different one
/// would change wording without changing meaning.
///
/// <c>[Code|Display]</c> MARKUP IS STRIPPED to its display half, the same as everywhere else in
/// this project - see KeywordGlossary.Plain, which does the same job for the strings that come out
/// of memory rather than out of a file.
/// </remarks>
public static class StatDescriptionFiles
{
    /// <summary>Where the game keeps them.</summary>
    public const string Folder = "data/statdescriptions/";

    /// <summary>What one of them is called.</summary>
    public const string Extension = ".csd";

    /// <summary>
    /// The file that leads, because its wordings are the general ones.
    /// </summary>
    /// <remarks>
    /// FIRST FOUND WINS when a stat appears in several files, so which file is read first decides
    /// the wording. The reference puts this one in front for that reason: the others are a skill's
    /// or an item class's own phrasing of a stat the general file already covers.
    /// </remarks>
    public const string General = "stat_descriptions.csd";

    /// <summary>
    /// Longest a template is taken seriously - a bound against a mis-read file, not a real limit.
    /// </summary>
    /// <remarks>
    /// A line over this is passed over rather than ending the block, so a block whose first wording
    /// is absurd still gets its second. The longest real one in the game is well under it.
    /// </remarks>
    private const int LongestTemplate = 1024;

    /// <summary>Most stats one block may cover. A guard on a count read out of a file.</summary>
    private const int MostStatsPerBlock = 16;

    /// <summary>
    /// Reads every stat description the install holds, general file first.
    /// </summary>
    /// <param name="files">The opened install, or null where there is none.</param>
    /// <param name="paths">
    /// Which files to read, when the caller has already walked for them - see
    /// <see cref="GameFiles.Names"/> for why a caller would have. Null walks for them here.
    /// </param>
    /// <returns>Stat id to wording, first wording found for each id. Empty where nothing read.</returns>
    public static Dictionary<string, StatDescription> Read(GameFiles? files, IEnumerable<string>? paths = null)
    {
        var found = new Dictionary<string, StatDescription>(StringComparer.Ordinal);
        if (files is null)
        {
            return found;
        }

        List<string> reading = [.. paths ?? files.Under(Folder, Extension)];
        if (reading.Count == 0)
        {
            return found;
        }

        // The general file first; see General for why that decides wording rather than just order.
        reading.Sort((left, right) =>
        {
            bool first = IsGeneral(left);
            bool second = IsGeneral(right);
            return first == second
                ? string.Compare(left, right, StringComparison.OrdinalIgnoreCase)
                : first ? -1 : 1;
        });

        // Collected as they are met rather than swept for afterwards: a sweep means copying the
        // whole table to iterate it while adding to it, and these are a few hundred of seventeen
        // thousand.
        var aliases = new List<string>();

        foreach (string path in reading)
        {
            byte[]? content = files.Read(path);
            if (content is null)
            {
                continue;
            }

            foreach (StatDescription block in Parse(Decode(content)))
            {
                foreach (string stat in block.Stats)
                {
                    // A STAT ALREADY FOUND IS NOT REPLACED, the same rule the index walk keeps: the
                    // answer must be the same on every run, and it is the general file's wording.
                    if (found.TryAdd(stat, block) && stat.StartsWith("base_", StringComparison.Ordinal))
                    {
                        aliases.Add(stat);
                    }
                }
            }
        }

        // The game writes some stats as base_X and refers to them as X. The reference adds the
        // short form as an alias, and a lookup that misses one of the two is a sentence lost for no
        // reason - so the same is done here, and after the walk so a real entry always wins.
        foreach (string stat in aliases)
        {
            found.TryAdd(stat[5..], found[stat]);
        }

        return found;
    }

    /// <summary>
    /// Whether a path IS the general file, rather than merely ending in its name.
    /// </summary>
    /// <remarks>
    /// THE WHOLE NAME, because a suffix is not enough and that cost the very thing this sorting is
    /// for: <c>skill_stat_descriptions.csd</c> ends with <c>stat_descriptions.csd</c>, so a suffix
    /// test calls it general too - and then a skill's own phrasing of a general stat wins by being
    /// alphabetically earlier. The reference compares the file name, and so does this.
    /// </remarks>
    private static bool IsGeneral(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        return path.AsSpan(slash + 1).Equals(General, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UTF-16 with a mark, UTF-8 without one - and UTF-16 without a mark, which also happens.
    /// </summary>
    /// <remarks>
    /// THE REFERENCE DECODES EVERY ONE OF THEM AS UTF-16LE: its utf-8 fallback sits behind an
    /// except that a replacing decode can never reach, so a file without a mark still reads as
    /// UTF-16 there. Rather than copy that by accident, the shape is tested - a second byte of
    /// nought is ASCII text in UTF-16LE and cannot occur in UTF-8, which has no NUL at all.
    /// </remarks>
    public static string Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        return content.Length >= 2 && content[1] == 0
            ? Encoding.Unicode.GetString(content)
            : Encoding.UTF8.GetString(content);
    }

    /// <summary>
    /// Turns one file's text into its blocks.
    /// </summary>
    /// <remarks>
    /// Written from tools/poe_tools.py's own parser, which this project ships and which produced
    /// the table this replaces. A block that does not parse is skipped rather than guessed at: a
    /// file of a shape nobody expected should cost the stats in it, not the ones around them.
    ///
    /// THREE LINE SHAPES INSIDE A BLOCK ARE NOT WORDINGS and each was worth an entry when read as
    /// one. <c>include "Metadata/StatDescriptions/stat_descriptions.csd"</c> is a quoted PATH, and
    /// taking the first quoted line without skipping it files that path as the stat's sentence.
    /// <c>no_description</c> ends a block that deliberately has none. And a <c>lang</c> line that is
    /// not English does not end the block: a file may list German first and English after it, so
    /// this waits for English rather than giving up at the first foreign line.
    /// </remarks>
    public static List<StatDescription> Parse(string? text)
    {
        var blocks = new List<StatDescription>();
        if (string.IsNullOrEmpty(text))
        {
            return blocks;
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "description")
            {
                continue;
            }

            // The stat line: a count, then that many ids.
            int at = i + 1;
            while (at < lines.Length && lines[at].Trim().Length == 0)
            {
                at++;
            }

            if (at >= lines.Length || Stats(lines[at]) is not { Count: > 0 } stats)
            {
                continue;
            }

            // The wordings under it, until the next block. A file with no lang lines at all is
            // English throughout, which is why "none seen yet" counts as English here.
            string? template = null;
            bool anyLanguage = false;
            bool english = true;
            for (at++; at < lines.Length; at++)
            {
                string line = lines[at].Trim();
                if (line == "description")
                {
                    break;
                }

                if (line == "no_description")
                {
                    break;
                }

                if (line.StartsWith("include ", StringComparison.Ordinal))
                {
                    continue;   // a quoted PATH, not a wording - see the remarks
                }

                if (Language(line) is { } spoken)
                {
                    anyLanguage = true;
                    english = spoken.Equals("English", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (template is not null || !english)
                {
                    continue;
                }

                template = Quoted(line);
                if (template is not null && anyLanguage)
                {
                    break;   // an English block said its piece; the rest is other languages
                }
            }

            if (template is { Length: > 0 })
            {
                blocks.Add(new StatDescription(stats, template));
                i = at - 1;
            }
        }

        return blocks;
    }

    /// <summary>The language a <c>lang "X"</c> line switches to, or null when it is not one.</summary>
    private static string? Language(string line)
    {
        if (!line.StartsWith("lang", StringComparison.Ordinal) || line.Length < 5 || line[4] is not (' ' or '\t'))
        {
            return null;
        }

        int first = line.IndexOf('"', 4);
        int last = first < 0 ? -1 : line.IndexOf('"', first + 1);
        return last > first ? line[(first + 1)..last] : null;
    }

    /// <summary>The stat ids of a "count id id ..." line, or null when it is not one.</summary>
    private static List<string>? Stats(string line)
    {
        string[] words = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !int.TryParse(words[0], out int count)
            || count < 1 || count > MostStatsPerBlock || count > words.Length - 1)
        {
            return null;
        }

        var stats = new List<string>(count);
        for (int i = 1; i <= count; i++)
        {
            stats.Add(words[i]);
        }

        return stats;
    }

    /// <summary>The quoted part of a wording line, with the game's markup stripped.</summary>
    private static string? Quoted(string line)
    {
        int first = line.IndexOf('"', StringComparison.Ordinal);
        int last = line.LastIndexOf('"');
        if (first < 0 || last <= first || last - first > LongestTemplate)
        {
            return null;
        }

        // Unwrapped before the brackets are stripped, because the wrapper is the outer one: a
        // tag's body may itself hold [Code|Display] markup.
        return Plain(Unwrap(line[(first + 1)..last]));
    }

    /// <summary>
    /// The sentence inside a <c>&lt;tag&gt;{{...}}</c> wrapper, or the text unchanged.
    /// </summary>
    /// <remarks>
    /// A COLOUR, NOT A WORDING. The game marks a few lines for a different ink by wrapping the
    /// whole sentence - <c>&lt;enchanted&gt;{{Monsters grant {0}% increased Experience}}</c> - and
    /// nothing here draws in that ink, so the wrapper is furniture on the way to a reader. Left in,
    /// it is printed: that stat is carried by seven monster modifiers, and the line they showed
    /// read as broken markup rather than as a sentence.
    ///
    /// MEASURED BEFORE IT WAS WRITTEN, because a rule this cheap is also cheap to get wrong: 79 of
    /// the export's 16802 templates carry the wrapper and ALL 79 are the whole template, one tag
    /// (<c>enchanted</c> on 78, <c>nemesismod</c> on one). The 33 other rows with angle brackets are
    /// a different markup - <c>&lt;&lt;ExpedRuneFire&gt;&gt; Fire Rune</c>, a sprite rather than a
    /// wrapper - and they have no braces, which is why this matches on the pair and not on the tag.
    /// </remarks>
    public static string Unwrap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length < 5 || text[0] != '<' || !text.EndsWith("}}", StringComparison.Ordinal))
        {
            return text;
        }

        int close = text.IndexOf('>', 1);
        if (close < 2 || !text.AsSpan(close + 1).StartsWith("{{", StringComparison.Ordinal))
        {
            return text;
        }

        return text[(close + 3)..^2];
    }

    /// <summary>
    /// The display half of the game's <c>[Code|Display]</c> markup.
    /// </summary>
    /// <remarks>
    /// The same job KeywordGlossary.Plain does for strings out of memory, and deliberately a
    /// separate copy: that one is about a keyword glossary and lives in a type this has no reason
    /// to depend on. Both are four lines.
    /// </remarks>
    public static string Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('[', StringComparison.Ordinal))
        {
            return text;
        }

        var built = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '[')
            {
                built.Append(text[i]);
                continue;
            }

            int end = text.IndexOf(']', i + 1);
            if (end < 0)
            {
                built.Append(text, i, text.Length - i);   // unbalanced: keep it verbatim
                break;
            }

            ReadOnlySpan<char> inside = text.AsSpan(i + 1, end - i - 1);
            int pipe = inside.IndexOf('|');
            built.Append(pipe >= 0 ? inside[(pipe + 1)..] : inside);
            i = end;
        }

        return built.ToString();
    }
}
