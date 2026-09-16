using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;

namespace PoEformance.Game.Items;

/// <summary>
/// The game's own Mods.dat: what an affix is called, and whether it is a prefix or a suffix.
/// </summary>
/// <remarks>
/// WHAT THIS RETIRES. data/item-names.json carries 6567 mods as "id, name, kind", extracted from
/// this table by the AHK tool's scripts. The live client has 16784 rows of which 6470 carry a
/// name, so the shipped list is close to complete TODAY and goes short the moment a league adds
/// an affix - the same shape of staleness as the base types, and not the misnumbering the stat
/// tables had: a mod is keyed by its own id, which the game does not renumber.
///
/// THE LAYOUT IS THE ONE THE INTERVAL RULE UNLOCKED. Priced without it, Mods computed 0x295
/// against the 0x2B5 the client reports, and this table was one commit from being written down as
/// unreadable. Its eight Stat*Value columns are INTERVALS - a range stored as two values - which
/// is the thirty-two bytes exactly. With them priced, Id lands at +0x00, Name at +0x62 and
/// GenerationType at +0x6A, and the row sums to what the client says. See DatColumn.Interval.
///
/// KIND COMES FROM GenerationType, 1 prefix, 2 suffix, 3 unique, and nothing else is given a kind
/// - which is what the shipped file does, checked against it: of the 16784 rows, gen types 1, 2
/// and 3 account for every one of the 6567 the file kept, and the ten thousand rows of type 3
/// without a name are the ones it dropped.
///
/// READ IN BLOCKS. Five reads a row over 16784 rows is eighty thousand round trips into another
/// process; the rows are contiguous, so DatRows carries ninety of them per read and the pointers
/// are picked out in this process. A row with no name is skipped before its strings are touched,
/// which is ten thousand of them.
/// </remarks>
public sealed class ModTable
{
    /// <summary>What the table is called in the loader's file list.</summary>
    public const string TableName = "Mods";

    /// <summary>Longest id or affix taken seriously. The longest in the game is well under this.</summary>
    private const int MostChars = 128;

    private readonly Dictionary<string, (string Name, string Kind)> _mods;

    private ModTable(DatTableFacts facts, Dictionary<string, (string Name, string Kind)> mods)
    {
        Facts = facts;
        _mods = mods;
    }

    /// <summary>What the table said about itself.</summary>
    public DatTableFacts Facts { get; }

    /// <summary>How many mods were read and named.</summary>
    public int Named => _mods.Count;

    /// <summary>Finds Mods.dat in a file-table walk that has already run, and reads it whole.</summary>
    public static ModTable? From(LoadedDatTables tables, IMemoryReader reader, OffsetSchema schema)
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

    /// <summary>The same reader over facts from somewhere other than the file-table walk.</summary>
    /// <returns>The table, or null when the facts do not describe one this layout fits.</returns>
    public static ModTable? Over(IMemoryReader reader, DatTableFacts facts, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(schema);

        StructDef row = schema.Structs["ModsRow"];
        int idAt = row.OffsetOf("IdPtr");
        int nameAt = row.OffsetOf("NamePtr");
        int kindAt = row.OffsetOf("GenerationType");

        // THE ROW SIZE IS THE FINGERPRINT, and on this table it is what the whole reader rests
        // on: the columns in front of Name include eight intervals, so a row that is not this
        // width is a row whose Name is not at +0x62 and whose every string read is a lottery.
        if (facts.RowSize != (int)row.Constants["ComputedRowSize"])
        {
            return null;
        }

        var mods = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        DatRows.Walk(reader, facts, (_, bytes) =>
        {
            // THE NAME POINTER FIRST AND THE REST ONLY IF IT IS THERE. Ten thousand of these rows
            // have no name - unique-domain mods the shipped file drops as well - and a row with
            // nothing to call itself is not worth four string reads to find that out.
            ulong namePtr = BinaryPrimitives.ReadUInt64LittleEndian(bytes[nameAt..]);
            if (!MemoryReaderExtensions.IsPlausiblePointer(namePtr))
            {
                return;
            }

            string name = reader.ReadUnicodeString(namePtr, MostChars);
            if (name.Length == 0)
            {
                return;
            }

            ulong idPtr = BinaryPrimitives.ReadUInt64LittleEndian(bytes[idAt..]);
            if (!MemoryReaderExtensions.IsPlausiblePointer(idPtr))
            {
                return;
            }

            string id = reader.ReadUnicodeString(idPtr, MostChars);
            if (id.Length > 0)
            {
                mods[id] = (name, KindOf(BinaryPrimitives.ReadInt32LittleEndian(bytes[kindAt..])));
            }
        });

        // Nothing read is a table that could not be read rather than an empty one - see
        // BaseItemTable, where the same distinction keeps a replay from blanking the screen.
        return mods.Count > 0 ? new ModTable(facts, mods) : null;
    }

    /// <summary>What the game calls a mod id, or null when it has no name.</summary>
    public (string Name, string Kind)? Of(string? id)
        => id is { Length: > 0 } && _mods.TryGetValue(id, out (string Name, string Kind) found) ? found : null;

    /// <summary>
    /// The three generation types the shipped table gives a kind, worded as it words them.
    /// </summary>
    /// <remarks>
    /// CHECKED AGAINST THE EXPORT rather than taken from its header: every one of the file's 6567
    /// mods has generation type 1, 2 or 3, and the words it uses for them are these. Anything else
    /// gets no kind, which is also what the file does - and what a caller shows when the kind is
    /// empty is already its business, because a mod the table has never heard of arrives that way.
    /// </remarks>
    private static string KindOf(int generationType) => generationType switch
    {
        1 => "prefix",
        2 => "suffix",
        3 => "unique",
        _ => string.Empty,
    };
}
