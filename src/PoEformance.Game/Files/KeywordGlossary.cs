using System.Text;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Files;

/// <summary>One entry of the game's glossary.</summary>
/// <param name="Id">The engine key, English on every client - "CriticalDamageBonus".</param>
/// <param name="Term">What the player reads - "Critical Damage Bonus". Localised.</param>
/// <param name="Definition">The popup text, which may be empty and carries markup of its own.</param>
public readonly record struct KeywordPopup(string Id, string Term, string Definition);

/// <summary>
/// The game's glossary: what its own texts mean by the keywords they highlight.
/// </summary>
/// <remarks>
/// WHAT THIS IS AND IS NOT FOR. The game writes its skill, mod and item texts with a markup
/// that carries both halves - "[Critical|Critical Hits]" is a key and the words to draw - so
/// rendering one needs no table at all, and <see cref="Plain"/> is static for that reason.
/// What needs the table is the other half of the feature: the POPUP. Hovering a highlighted
/// word is supposed to explain it, and the explanation lives nowhere else.
///
/// Reached through <see cref="LoadedDatTables"/>, which is the point: nothing on screen points
/// at KeywordPopups, so before a table could be found by name this one could not be read at
/// all. See the schema's KeywordPopupsRow for how it was identified.
///
/// THE ROW SIZE IS CHECKED, NOT ASSUMED. A dat table reports its own row size (DatRowStore
/// divides its rows by its by-Id index), so a table whose rows are not 0x48 is not the table
/// this code knows how to read - a patch that adds a column would otherwise be read as
/// gibberish rather than refused. That check is the whole reason to prefer the game's own
/// arithmetic over the constant in the schema.
/// </remarks>
public sealed class KeywordGlossary
{
    /// <summary>What dat-schema calls the table, which is what its path's last segment reads.</summary>
    public const string TableName = "KeywordPopups";

    /// <summary>
    /// Most rows read out of it.
    /// </summary>
    /// <remarks>
    /// A guard on a count that came from game memory, not a view about how many keywords the
    /// game has. The table reports its own row count; a drifted row store reports a nonsense one.
    /// </remarks>
    public const int MostRows = 8192;

    /// <summary>Longest Id or Term taken seriously. The longest Term in 1026 rows is 40.</summary>
    private const int LongestTerm = 128;

    /// <summary>
    /// Longest popup text.
    /// </summary>
    /// <remarks>
    /// WAS 512 AND SILENTLY CUT 56 ROWS. A raw UTF-16 pointer carries no length, so this cap is
    /// the only thing that ends the read - and a definition longer than it comes back truncated
    /// rather than refused. The live table has 56 definitions of exactly 512 characters, which
    /// is what a cap looks like from the inside; Critical's own text ends mid-word at
    /// "would result in a final Criti". The real maximum is still unmeasured, because the run
    /// that recorded the fixture was the one doing the truncating.
    /// </remarks>
    private const int LongestDefinition = 2048;

    private readonly Dictionary<string, KeywordPopup> _byId;

    private KeywordGlossary(Dictionary<string, KeywordPopup> byId, string error)
    {
        _byId = byId;
        LastError = error;
    }

    /// <summary>Nothing read, and why.</summary>
    public static KeywordGlossary Nothing(string why) => new([], why);

    /// <summary>Every entry, keyed on the engine Id - never on the localised term.</summary>
    public IReadOnlyDictionary<string, KeywordPopup> ById => _byId;

    /// <summary>What went wrong, when nothing came back.</summary>
    public string LastError { get; }

    /// <summary>The table this was read from, for a diagnostic that wants to name it.</summary>
    public LoadedDatTable? Table { get; private init; }

    /// <summary>
    /// Reads the glossary out of the first loaded KeywordPopups table that decodes.
    /// </summary>
    /// <param name="tables">A walk that has already run - see <see cref="LoadedDatTables.Read"/>.</param>
    /// <param name="reader">The process.</param>
    /// <param name="schema">Where the row's columns are.</param>
    public static KeywordGlossary Read(LoadedDatTables tables, IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        // Both halves checked, and a message rather than a throw either way: the schema
        // hot-reloads, so an edit that renames a field should cost the glossary, not the tool.
        if (!schema.Structs.TryGetValue("KeywordPopupsRow", out StructDef? row)
            || !row.Constants.TryGetValue("RowSize", out long expected))
        {
            return Nothing("the schema does not describe a KeywordPopups row");
        }

        IReadOnlyList<LoadedDatTable> candidates = tables.FindAll(TableName);
        if (candidates.Count == 0)
        {
            return Nothing($"{TableName} is not among the {tables.Tables.Count} tables the game has loaded");
        }

        string why = string.Empty;

        // The first that decodes rather than the first that matches: the game loads a localised
        // copy of some tables under a language-prefixed path, and both answer to the same name.
        foreach (LoadedDatTable table in candidates)
        {
            if (table.Facts.RowSize != expected)
            {
                why = $"{table.Facts.Path} reports rows of 0x{table.Facts.RowSize:X}, not 0x{expected:X}"
                    + " - the table has gained or lost a column and the row layout wants re-deriving";
                continue;
            }

            Dictionary<string, KeywordPopup> read = ReadRows(reader, table, row);
            if (read.Count > 0)
            {
                return new KeywordGlossary(read, string.Empty) { Table = table };
            }

            why = $"{table.Facts.Path} has {table.Facts.Rows} rows and none of them read as a keyword";
        }

        return Nothing(why);
    }

    private static Dictionary<string, KeywordPopup> ReadRows(
        IMemoryReader reader, LoadedDatTable table, StructDef row)
    {
        ulong id = (ulong)row.OffsetOf("Id");
        ulong term = (ulong)row.OffsetOf("Term");
        ulong definition = (ulong)row.OffsetOf("Definition");
        long rows = Math.Min(table.Facts.Rows, MostRows);

        var found = new Dictionary<string, KeywordPopup>(StringComparer.OrdinalIgnoreCase);
        for (long i = 0; i < rows; i++)
        {
            ulong at = table.Facts.RowsBegin + (ulong)(i * table.Facts.RowSize);
            string key = reader.ReadUnicodeString(reader.ReadPointer(at + id), LongestTerm);
            if (key.Length == 0)
            {
                continue;   // a row whose Id did not read is a bad read, not an entry
            }

            found[key] = new KeywordPopup(
                key,
                reader.ReadUnicodeString(reader.ReadPointer(at + term), LongestTerm),
                reader.ReadUnicodeString(reader.ReadPointer(at + definition), LongestDefinition));
        }

        return found;
    }

    /// <summary>The entry for a keyword, or null when the game does not have one.</summary>
    public KeywordPopup? Lookup(string? id)
        => id is not null && _byId.TryGetValue(id, out KeywordPopup found) ? found : null;

    /// <summary>
    /// The game's own text as a player sees it, with the keyword markup resolved.
    /// </summary>
    /// <remarks>
    /// ONE RULE, and it is measured against the whole table rather than a handful of rows:
    /// "[Key|Text]" draws as Text, and "[Key]" draws as Key. Both forms are real - 595 of the
    /// 1026 definitions carry markup, and 859 of the brackets in them have no pipe at all
    /// ("[Armour]", "[Bleeding]") - and both name a ROW of this same table: of the 317 distinct
    /// keys referenced, 313 are Ids in it. The pipe form is the same row said twice, the key and
    /// the words for it, which is why rendering needs no lookup.
    ///
    /// THERE IS NO PERCENT RULE, and there was one here until the live table said otherwise.
    /// The dissector showed "+100%%" and "by 20%%", so this collapsed a doubled percent - but
    /// the raw column holds "+100%" and "by 20%", and not one of the 236 definitions containing
    /// a percent has it doubled. The doubling was OUR OWN: ImGui takes a label as a format
    /// string, so ImGuiText.Escape replaces "%" with "%%" before drawing. A rule was derived
    /// from reading the tool's rendering as if it were the game's data.
    ///
    /// A SECOND MARKUP EXISTS AND IS LEFT ALONE. The 33 Expedition rune rows are written in the
    /// game's rich-text form instead - "&lt;rgb(219,217,206)&gt;{Fire Rune}", "&lt;italic&gt;{...}",
    /// "&lt;font:'fontin'&gt;{...}", opening with a "&lt;&lt;ExpedRuneFire&gt;&gt;" style reference - and braces
    /// appear in those 33 rows and nowhere else, so the two markups do not overlap. Stripping it
    /// would throw away the colours and emphasis it exists to carry, and this method is not the
    /// place to decide what a caller wants done with them: it is passed through verbatim until
    /// something actually draws one.
    ///
    /// Static, and takes no glossary, because rendering needs none: the markup carries the
    /// words. What needs the table is the popup behind them.
    /// </remarks>
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.Contains('[', StringComparison.Ordinal))
        {
            return text;   // 431 of the 1026 definitions, and they should not be copied
        }

        var built = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
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
                continue;
            }

            built.Append(text[i]);
        }

        return built.ToString();
    }

    /// <summary>
    /// The keywords a text refers to, in the order they appear, without duplicates.
    /// </summary>
    /// <remarks>
    /// The left halves, which is what a lookup needs: the right half is localised and the
    /// glossary is not keyed on it. This is what turns a rendered line into the set of popups
    /// it can offer.
    ///
    /// EXPECT A KEY THAT IS NOT THERE. Four of the 317 keys the live table refers to name no
    /// row in it - Breach, DNT, DNT-UNUSED and passive_keystone_mind_over_matter, two of which
    /// are plainly the game's own do-not-translate placeholders. A caller offering popups has to
    /// cope with a miss; it is the game's data, not a failed read.
    /// </remarks>
    public static IReadOnlyList<string> KeysIn(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('[', StringComparison.Ordinal))
        {
            return [];
        }

        var keys = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '[')
            {
                continue;
            }

            int end = text.IndexOf(']', i + 1);
            if (end < 0)
            {
                break;
            }

            ReadOnlySpan<char> inside = text.AsSpan(i + 1, end - i - 1);
            int pipe = inside.IndexOf('|');
            string key = new(pipe >= 0 ? inside[..pipe] : inside);
            if (key.Length > 0 && !keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
            }

            i = end;
        }

        return keys;
    }
}
