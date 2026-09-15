using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;

namespace PoEformance.Game.Items;

/// <summary>
/// The game's own BaseItemTypes.dat: what an item's metadata path is called.
/// </summary>
/// <remarks>
/// WHAT THIS RETIRES. data/item-names.json carries 5055 pairs of "metadata path, display name",
/// extracted from this very table by the AHK tool's scripts. The live client has 5496 rows, so
/// the shipped list has never heard of about four hundred and forty base types - every one of
/// which comes out as the tail of its own path today, which is close to the name and is not it.
///
/// KEYED BY THE PATH, WHICH IS WHY THE WHOLE TABLE IS READ AT ONCE and StatTable's row-at-a-time
/// cache would not do. A stat arrives as a row NUMBER, so that reader can answer one row and
/// forget the rest; an item arrives as a PATH, and finding the row a path is on means having
/// looked at all of them. So this is a single pass, once, and never again - about eleven thousand
/// string reads for five and a half thousand rows, on the thread that asked for it.
///
/// AND THAT IS WHY IT IS NOT A DRIFT FIX. The path does not renumber the way a stat row does:
/// item-names.json is keyed by the path itself, so it goes stale by OMISSION rather than by
/// pointing at the wrong thing. The stat tables had to be corrected; this one only has to be
/// completed, which is a smaller claim and worth stating as the smaller one.
///
/// THE LAYOUT IS CONFIRMED RATHER THAN COMPUTED. The columns come from poe-tool-dev/dat-schema
/// and sum to 0x168 under the width table this project already verified - and the loader's own
/// file table reports exactly 360 bytes a row on a live client. Every column in front of Name has
/// to be right for that total to land, which is what makes the agreement worth more than the
/// individual offsets. See BaseItemTypesRow in the schema, including what the same measurement
/// refused to confirm about Mods.
/// </remarks>
public sealed class BaseItemTable
{
    /// <summary>What the table is called in the loader's file list.</summary>
    public const string TableName = "BaseItemTypes";

    /// <summary>Longest path or name taken seriously. The longest in the game is well under this.</summary>
    private const int MostChars = 128;

    /// <summary>
    /// A bound on how many rows are walked, so a table that is not this one cannot cost the world.
    /// </summary>
    /// <remarks>
    /// The live client has 5496. Twice that leaves room for a league to add to it and still stops
    /// a wrong RowsBegin from reading until something falls over.
    /// </remarks>
    private const int MostRows = 20_000;

    private readonly Dictionary<string, string> _names;

    private BaseItemTable(DatTableFacts facts, Dictionary<string, string> names)
    {
        Facts = facts;
        _names = names;
    }

    /// <summary>What the table said about itself.</summary>
    public DatTableFacts Facts { get; }

    /// <summary>How many paths were read and named.</summary>
    public int Named => _names.Count;

    /// <summary>
    /// Finds BaseItemTypes.dat in a file-table walk that has already run, and reads it whole.
    /// </summary>
    /// <returns>The table, or null when the walk did not find it or it does not look like this one.</returns>
    public static BaseItemTable? From(LoadedDatTables tables, IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tables);

        foreach (LoadedDatTable found in tables.FindAll(TableName))
        {
            if (Over(reader, found.Facts, schema) is { } table)
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// The same reader over facts that came from somewhere other than the file-table walk.
    /// </summary>
    /// <remarks>
    /// Split off the walk for the same reason StatTable's is: which route found the table does
    /// not change what a row says, and a reader that can only be reached through eight thousand
    /// records is a reader nothing can check against a controlled image.
    /// </remarks>
    /// <returns>The table, or null when the facts do not describe one this layout fits.</returns>
    public static BaseItemTable? Over(IMemoryReader reader, DatTableFacts facts, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(schema);

        StructDef row = schema.Structs["BaseItemTypesRow"];
        int idAt = row.OffsetOf("IdPtr");
        int nameAt = row.OffsetOf("NamePtr");
        int size = (int)row.Constants["ComputedRowSize"];

        // THE ROW SIZE IS THE FINGERPRINT, and it is refused rather than worked around. The
        // schema's columns sum to this; the client reports it independently. If they disagree the
        // layout about to be used is not the layout of the table in front of it, and reading Name
        // at +0x20 of the wrong stride is how a project ends up with plausible nonsense rather
        // than an error. It is the check that kept Mods out - see BaseItemTypesRow.
        if (facts.RowSize != size || facts.Rows <= 0 || facts.Rows > MostRows)
        {
            return null;
        }

        var names = new Dictionary<string, string>((int)facts.Rows, StringComparer.OrdinalIgnoreCase);
        for (long index = 0; index < facts.Rows; index++)
        {
            ulong at = facts.RowsBegin + (ulong)(index * size);

            ulong idPtr = reader.ReadPointer(at + (ulong)idAt);
            if (!MemoryReaderExtensions.IsPlausiblePointer(idPtr))
            {
                continue;
            }

            string path = reader.ReadUnicodeString(idPtr, MostChars);
            if (path.Length == 0)
            {
                continue;
            }

            ulong namePtr = reader.ReadPointer(at + (ulong)nameAt);
            string name = MemoryReaderExtensions.IsPlausiblePointer(namePtr)
                ? reader.ReadUnicodeString(namePtr, MostChars)
                : string.Empty;

            // A row with a path and no name is kept OUT rather than stored empty: the caller's
            // fallback turns a path into something readable, and an empty string here would beat
            // that fallback to the answer and show nothing at all.
            if (name.Length > 0)
            {
                names[path] = name;
            }
        }

        // NOTHING READ IS NOT AN EMPTY TABLE, it is a table that could not be read - a replay
        // whose build never touched these rows looks exactly like this - and handing back an
        // empty one would put it in front of the file and blank every base type on the screen.
        return names.Count > 0 ? new BaseItemTable(facts, names) : null;
    }

    /// <summary>The game's name for a metadata path, or null when it has none.</summary>
    public string? Of(string? path)
        => path is { Length: > 0 } && _names.TryGetValue(path, out string? name) ? name : null;
}
