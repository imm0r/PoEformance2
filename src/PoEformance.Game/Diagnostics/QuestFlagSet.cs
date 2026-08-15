using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Diagnostics;

/// <summary>One set flag: its row in QuestFlags.dat, and its name when the table is at hand.</summary>
public readonly record struct SetQuestFlag(int Row, string Id);

/// <summary>
/// The quest flags a character has actually set.
/// </summary>
/// <remarks>
/// THE ANSWER TO THE HUNT, and it was never going to be found by looking for references.
/// QuestFlags.dat is 5717 names and nothing else - no bit in a row says whether it is set - and
/// six sessions swept about 130 MB for pointers to rows, for their HASH32 columns, for their Id
/// strings and for index entries. The set holds NONE of those. It is a sparse bitset over ROW
/// NUMBERS: a sorted vector of nine-byte records, each a u8 chunk index followed by 64 bits.
/// A row number is all it stores, so every search that looked for a reference was looking for
/// a shape that is not there.
///
/// WHY WATCHING IT CHANGE FAILED TOO, which is the part worth keeping. The vector is
/// exact-sized - its end IS its end-of-allocation - so inserting a chunk reallocates the whole
/// buffer. Set a flag whose chunk is new and nothing flips in place anywhere: the entire set
/// appears at a fresh address and the old copy is left behind, byte for byte as it was. A
/// differential that watches pages for a bit turning on sees the old buffer unchanged and a
/// brand-new buffer somewhere else, which is exactly what the three-recording control run
/// reported and exactly why it read as "no isolated bit turning on".
///
/// Both mechanisms are in the fixture: one flag arrived as a new chunk, another was set in
/// place in a chunk that already existed.
/// </remarks>
public static class QuestFlagSet
{
    /// <summary>
    /// Reads the set, as row numbers. Empty when the chain does not resolve.
    /// </summary>
    /// <param name="serverData">
    /// The ServerData blob - the VALUE at AreaInstance+PlayerInfo, not a dereference of it.
    /// </param>
    public static IReadOnlyList<int> Rows(IMemoryReader reader, OffsetSchema schema, ulong serverData)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        if (serverData == 0
            || !schema.Structs.TryGetValue("ServerData", out StructDef? server)
            || !schema.Structs.TryGetValue("QuestFlagOwner", out StructDef? owner)
            || !schema.Structs.TryGetValue("QuestFlagSet", out StructDef? set))
        {
            return [];
        }

        ulong hop = reader.ReadPointer(serverData + (ulong)server.OffsetOf("QuestFlagOwnerPtr"));
        if (hop == 0)
        {
            return [];
        }

        ulong flags = reader.ReadPointer(hop + (ulong)owner.OffsetOf("QuestFlagSetPtr"));
        if (flags == 0)
        {
            return [];
        }

        ulong chunks = flags + (ulong)set.OffsetOf("Chunks");
        ulong begin = reader.ReadPointer(chunks);
        ulong end = reader.ReadPointer(chunks + sizeof(ulong));

        int recordSize = (int)set.Constants["RecordSize"];
        int bits = (int)set.Constants["BitsPerRecord"];
        long most = set.Constants["MaxRecords"];

        if (begin == 0 || end <= begin || (end - begin) % (ulong)recordSize != 0)
        {
            return [];
        }

        long count = (long)(end - begin) / recordSize;
        if (count > most)
        {
            return [];
        }

        var rows = new List<int>();
        int previous = -1;

        // Nine bytes, so the record straddles the eight-byte grid and every read is its own.
        // Cheap at a few dozen records, and correct without assuming alignment. Allocated
        // once outside the loop, which the analyzer insists on and is right to.
        Span<byte> record = stackalloc byte[9];

        for (long i = 0; i < count; i++)
        {
            if (!reader.TryRead(begin + (ulong)(i * recordSize), record))
            {
                break;
            }

            int chunk = record[0];

            // The vector is SORTED, and saying so is a real check: a garbage read, or an
            // offset that has drifted onto some other vector, produces indices that jump
            // about. Stopping keeps whatever was read before it went wrong.
            if (chunk <= previous)
            {
                break;
            }

            previous = chunk;
            ulong word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(record[1..]);
            for (int bit = 0; bit < bits; bit++)
            {
                if ((word >> bit & 1) != 0)
                {
                    rows.Add((chunk * bits) + bit);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Names the rows, through the table's by-Id index.
    /// </summary>
    /// <remarks>
    /// The INDEX rather than the rows array, because entry N holds row N and its first field
    /// is the Id's characters - so a name costs one read of the entry and one of the string,
    /// against following the row's own Id column. Rows that cannot be read keep their number
    /// and an empty name rather than being dropped: "row 644, unnamed" is a finding, and a
    /// silently shorter list is not.
    /// </remarks>
    public static IReadOnlyList<SetQuestFlag> Named(
        IMemoryReader reader, IReadOnlyList<int> rows, ulong byIdIndexBegin, int entrySize = 0x18)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(rows);

        var named = new List<SetQuestFlag>(rows.Count);
        foreach (int row in rows)
        {
            var id = string.Empty;
            if (byIdIndexBegin != 0)
            {
                ulong text = reader.ReadPointer(byIdIndexBegin + (ulong)((long)row * entrySize));
                if (text != 0)
                {
                    id = reader.ReadUnicodeString(text, 128);
                }
            }

            named.Add(new SetQuestFlag(row, id));
        }

        return named;
    }

    /// <summary>Where the by-Id index of a table starts, or 0 when it cannot be read.</summary>
    public static ulong ByIdIndex(IMemoryReader reader, ulong table, DatTableShape shape)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (table == 0)
        {
            return 0;
        }

        ulong store = reader.ReadPointer(table + (ulong)shape.RowStoreOffset);
        return store == 0 ? 0 : reader.ReadPointer(store + (ulong)shape.ByIdIndexOffset);
    }
}
