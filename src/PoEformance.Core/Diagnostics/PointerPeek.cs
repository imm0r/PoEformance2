using System.Buffers.Binary;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>What a pointer turned out to lead to.</summary>
public enum TargetKind
{
    /// <summary>Nothing readable. A stale pointer, or not a pointer at all.</summary>
    Unreadable,

    /// <summary>The game's own code. A vtable, which identifies a TYPE even when nothing else does.</summary>
    Code,

    /// <summary>Wide text, which is how the game stores nearly every name and path.</summary>
    WideText,

    /// <summary>Plain text.</summary>
    Text,

    /// <summary>A begin/end pair bracketing an array - the shape of a std::vector.</summary>
    Vector,

    /// <summary>A row of one of the game's static content tables, identified by its Id string.</summary>
    DatRow,

    /// <summary>One of those tables itself, which knows its own path, row count and row size.</summary>
    DatTable,

    /// <summary>Readable, and none of the above. Another structure, waiting to be looked at.</summary>
    Structure,
}

/// <summary>What was found behind a pointer.</summary>
/// <param name="Summary">One line to put next to the row.</param>
/// <param name="Span">For a vector, the bytes between begin and end. Zero otherwise.</param>
/// <param name="ElementGuesses">Element sizes that divide the span exactly - the plausible ones.</param>
public sealed record PeekResult(
    TargetKind Kind,
    string Summary,
    ulong Address,
    long Span = 0,
    IReadOnlyList<int>? ElementGuesses = null)
{
    /// <summary>Nothing there.</summary>
    public static PeekResult Nothing(ulong address) => new(TargetKind.Unreadable, "unreadable", address);
}

/// <summary>What a dat table says about itself.</summary>
/// <param name="Path">Its file path, e.g. "Data/Balance/QuestFlags.dat". Empty when unreadable.</param>
/// <param name="Name">Just the table name - "QuestFlags" - which is what dat-schema calls it.</param>
/// <param name="Rows">How many rows it holds.</param>
/// <param name="RowSize">How many bytes one row takes.</param>
/// <param name="RowsBegin">The first row, so a row pointer turns into an index.</param>
public sealed record DatTableFacts(string Path, string Name, long Rows, long RowSize, ulong RowsBegin)
{
    /// <summary>The table's name if its path was readable, or "dat" so a sentence still reads.</summary>
    public string Label => Name.Length > 0 ? Name : "dat";

    /// <summary>Which row an address is, or -1 when it is not one of this table's.</summary>
    public long IndexOf(ulong row)
    {
        if (RowSize <= 0 || row < RowsBegin)
        {
            return -1;
        }

        ulong offset = row - RowsBegin;

        // Exactly on the grid or not at all. A pointer INTO a row - at its second column, say -
        // divides with a remainder, and calling that "row 41" would be worse than saying nothing.
        if (offset % (ulong)RowSize != 0)
        {
            return -1;
        }

        long index = (long)(offset / (ulong)RowSize);
        return index < Rows ? index : -1;
    }
}

/// <summary>
/// Follows one pointer and says what kind of thing is on the other end.
/// </summary>
/// <remarks>
/// The difference between a list of addresses and a map. A structure full of pointers tells
/// you nothing until you know that this one is a name, that one is a list of forty-two
/// somethings, and the third is another structure - and at that point the shape of the thing
/// is visible without having decoded a single field of it.
///
/// One pointer per call, on purpose. Following everything automatically walks the entire heap
/// and costs thousands of reads; this is meant to be spent where a person is actually looking.
/// </remarks>
public static class PointerPeek
{
    /// <summary>Element sizes worth suggesting for a vector, commonest first.</summary>
    /// <remarks>
    /// Not arbitrary - these are the sizes this game's arrays actually use. 8 is a vector of
    /// pointers, 0x10 a component lookup entry, 0x38 a terrain tile.
    /// </remarks>
    private static readonly int[] LikelyElementSizes = [8, 0x10, 0x18, 0x20, 0x28, 0x38, 0x40, 4];

    /// <summary>How much of an unknown structure to show as a preview.</summary>
    private const int PreviewBytes = 32;

    /// <summary>Most rows a table is believed to have. Beyond this the containers are garbage.</summary>
    private const long MostRows = 2_000_000;

    /// <summary>And the biggest row. WorldAreas' 0x300 is the largest this game is known to have.</summary>
    private const int LargestRow = 0x1000;

    /// <summary>Longest table path taken seriously.</summary>
    private const int LongestTablePath = 128;

    /// <summary>Looks at what a pointer leads to.</summary>
    public static PeekResult Peek(IMemoryReader reader, ulong address)
        => Peek(reader, address, null, 0);

    /// <summary>
    /// Looks at what a pointer leads to, able to recognise dat tables and to number the rows
    /// of one.
    /// </summary>
    /// <param name="tables">
    /// Where a table keeps its path and rows. Without it this behaves exactly as before -
    /// tables read as anonymous structures, which is what they did for months.
    /// </param>
    /// <param name="following">
    /// The eight bytes AFTER the peeked ones, when the caller has them. A foreign reference is
    /// a row followed by its table, so this is what lets a row be named and numbered rather
    /// than just shown - and it is the only reason a row can say WHICH table it belongs to,
    /// which the layout alone has never been able to answer.
    /// </param>
    public static PeekResult Peek(IMemoryReader reader, ulong address, DatTableShape? tables, ulong following)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!MemoryReaderExtensions.IsPlausiblePointer(address))
        {
            return PeekResult.Nothing(address);
        }

        if (reader.ModuleSize > 0 && address >= reader.ModuleBase && address < reader.ModuleBase + reader.ModuleSize)
        {
            return new PeekResult(
                TargetKind.Code, $"code at module+0x{address - reader.ModuleBase:X}", address);
        }

        // As much as is there, rather than a fixed block. A read only succeeds when EVERY
        // requested byte is available, so demanding a full preview would report "unreadable"
        // for a short string, or for anything sitting near the end of its page - which is to
        // say, for real things.
        Span<byte> head = stackalloc byte[PreviewBytes];
        int got = ReadWhatIsThere(reader, address, head);
        if (got == 0)
        {
            return PeekResult.Nothing(address);
        }

        // Text before structure. A name is the single most useful thing to find, and its bytes
        // are unmistakable in a way a structure's are not.
        string wide = reader.ReadUnicodeString(address, 64);
        if (Readable(wide))
        {
            return new PeekResult(TargetKind.WideText, $"\"{Trim(wide)}\"", address);
        }

        string plain = reader.ReadUtf8(address, 64);
        if (Readable(plain))
        {
            return new PeekResult(TargetKind.Text, $"\"{Trim(plain)}\"", address);
        }

        // A TABLE before a row, because it is the thing that can name one. Its own first field
        // is a vtable, so nothing above matched it and nothing below would have: it used to
        // read as an anonymous structure, which is exactly how the QuestFlags table sat
        // unrecognised in a dissector window for as long as anyone looked at it.
        if (tables is { } shape && Describe(reader, address, shape) is { } self)
        {
            return new PeekResult(TargetKind.DatTable, Summarise(self), address);
        }

        // BEFORE the vector test, which it would otherwise be mistaken for: a dat row often
        // starts with two string columns whose characters live in the same blob, and Id then
        // Description is a readable begin/end pair with a small, neatly divisible span. That
        // is every requirement TryVector has.
        if (got >= 8 && TryDatRow(reader, head, out string id))
        {
            // The table half of a foreign reference, if the caller handed us the bytes it
            // sits in. WHICH table a row belongs to cannot be answered from the row.
            DatTableFacts? owner = tables is { } ownerShape && following != 0
                ? Describe(reader, following, ownerShape)
                : null;

            long index = owner?.IndexOf(address) ?? -1;
            string summary = owner is null ? $"dat row, Id \"{Trim(id)}\""
                : index >= 0 ? $"{owner.Label} row {index} of {owner.Rows}, Id \"{Trim(id)}\""
                : $"{owner.Label} row, Id \"{Trim(id)}\"";

            return new PeekResult(TargetKind.DatRow, summary, address);
        }

        if (got >= 16 && TryVector(head, out long span, out IReadOnlyList<int> sizes))
        {
            string counts = string.Join(", ", sizes.Select(size => $"{span / size} x 0x{size:X}"));
            return new PeekResult(
                TargetKind.Vector,
                counts.Length > 0 ? $"list of {counts}" : $"list spanning 0x{span:X} bytes",
                address,
                span,
                sizes);
        }

        return new PeekResult(TargetKind.Structure, Preview(head[..got]), address);
    }

    /// <summary>Reads the largest of a few sizes that succeeds, and says how much that was.</summary>
    private static int ReadWhatIsThere(IMemoryReader reader, ulong address, Span<byte> destination)
    {
        foreach (int size in (int[])[PreviewBytes, 16, 8])
        {
            if (size <= destination.Length && reader.TryRead(address, destination[..size]))
            {
                return size;
            }
        }

        return 0;
    }

    /// <summary>True when a structure starts the way a row of a .dat table starts.</summary>
    /// <remarks>
    /// Half of this game's PoE2 tables declare Id as their first column, and a string column
    /// is a pointer to wide characters once the row is in memory - so "the first eight bytes
    /// point at readable wide text" is what a dat row looks like from outside.
    ///
    /// What makes it worth saying rather than a coin flip is the alternative. An engine object
    /// starts with a vtable, which points into the module and is refused here, so the two
    /// commonest things behind a pointer are told apart by their first field. It is still a
    /// FINGERPRINT and not a proof: any structure whose first member happens to be a string
    /// pointer matches, and which table this is a row OF cannot be answered from the layout
    /// alone. The Id usually answers it for a human - "flask_effect_life" needs no lookup.
    /// </remarks>
    private static bool TryDatRow(IMemoryReader reader, ReadOnlySpan<byte> head, out string id)
    {
        id = string.Empty;

        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(head);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first))
        {
            return false;
        }

        if (reader.ModuleSize > 0 && first >= reader.ModuleBase && first < reader.ModuleBase + reader.ModuleSize)
        {
            return false;   // a vtable: an engine object, not a row
        }

        string text = reader.ReadUnicodeString(first, 64);
        if (!Readable(text))
        {
            return false;
        }

        id = text;
        return true;
    }

    /// <summary>
    /// Reads what a dat table says about itself, or null when the address is not one.
    /// </summary>
    /// <remarks>
    /// THE ROW SIZE IS THE POINT, and it comes out of the table rather than out of a schema.
    /// A by-Id index entry is a fixed 0x18 whatever the table is, and there is exactly one per
    /// row - so that index says how many rows there are, and the rows array divided by that
    /// says how big one is. Until this, a row size could only be COMPUTED from the community
    /// schema's column list and then checked by hand against a stride; now the game answers it
    /// for every table it has loaded, which is also what makes a row pointer into "row 2170 of
    /// 5717" instead of an address.
    ///
    /// WHAT MAKES IT A TABLE AND NOT A COINCIDENCE is the last check rather than the path: the
    /// first entry of the by-Id index must point at the first row. Two containers whose spans
    /// happen to divide is a shape several structures have; two containers that agree with
    /// each other about where the rows start is not. The path is only the LABEL, deliberately
    /// not a requirement - it lives one pointer further away, so a recording that never
    /// followed it, or a table caught mid-load, still gets recognised and counted rather than
    /// falling back to "another structure".
    /// </remarks>
    private static DatTableFacts? Describe(IMemoryReader reader, ulong address, DatTableShape shape)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(address))
        {
            return null;
        }

        ulong store = reader.ReadPointer(address + (ulong)shape.RowStoreOffset);
        if (!MemoryReaderExtensions.IsPlausiblePointer(store))
        {
            return null;
        }

        ulong rowsBegin = reader.ReadPointer(store + (ulong)shape.RowsOffset);
        ulong rowsEnd = reader.ReadPointer(store + (ulong)shape.RowsOffset + sizeof(ulong));
        ulong idBegin = reader.ReadPointer(store + (ulong)shape.ByIdIndexOffset);
        ulong idEnd = reader.ReadPointer(store + (ulong)shape.ByIdIndexOffset + sizeof(ulong));

        if (!MemoryReaderExtensions.IsPlausiblePointer(rowsBegin)
            || !MemoryReaderExtensions.IsPlausiblePointer(idBegin)
            || rowsEnd <= rowsBegin
            || idEnd <= idBegin)
        {
            return null;
        }

        long rows = (long)(idEnd - idBegin) / shape.ByIdEntrySize;
        long span = (long)(rowsEnd - rowsBegin);
        if (rows is <= 0 or > MostRows || span % rows != 0)
        {
            return null;
        }

        long rowSize = span / rows;
        if (rowSize is <= 0 or > LargestRow)
        {
            return null;
        }

        if (reader.ReadPointer(idBegin + (ulong)shape.ByIdEntryRowAt) != rowsBegin)
        {
            return null;
        }

        string path = reader.ReadStdWString(address + (ulong)shape.PathOffset, LongestTablePath);
        bool named = LooksLikeATablePath(path);
        return new DatTableFacts(
            named ? path : string.Empty,
            named ? NameIn(path) : string.Empty,
            rows,
            rowSize,
            rowsBegin);
    }

    /// <summary>One line for a table: what it is called and how big it is.</summary>
    private static string Summarise(DatTableFacts table) => table.Path.Length > 0
        ? $"dat table \"{table.Path}\", {table.Rows} rows of 0x{table.RowSize:X}"
        : $"dat table, {table.Rows} rows of 0x{table.RowSize:X}";

    /// <summary>True when a string reads as the path of one of the game's tables.</summary>
    private static bool LooksLikeATablePath(string text)
        => text.Length is > 4 and <= LongestTablePath
            && text.Contains('/', StringComparison.Ordinal)
            && text.Contains(".dat", StringComparison.OrdinalIgnoreCase)
            && !text.Any(char.IsControl);

    /// <summary>"Data/Balance/QuestFlags.dat" -> "QuestFlags", which is what dat-schema calls it.</summary>
    private static string NameIn(string path)
    {
        int start = path.LastIndexOf('/') + 1;
        int end = path.IndexOf('.', start);
        return end > start ? path[start..end] : path[start..];
    }

    /// <summary>True when a begin/end pair looks like a vector rather than two unrelated pointers.</summary>
    /// <remarks>
    /// Both must be readable addresses, the end must be at or after the start, and the span
    /// must be a size an array plausibly has. The last part matters most: any two neighbouring
    /// pointers into the same allocation satisfy the first two, and calling all of those
    /// vectors would make the label meaningless.
    /// </remarks>
    private static bool TryVector(ReadOnlySpan<byte> head, out long span, out IReadOnlyList<int> sizes)
    {
        span = 0;
        sizes = [];

        ulong begin = BinaryPrimitives.ReadUInt64LittleEndian(head);
        ulong end = BinaryPrimitives.ReadUInt64LittleEndian(head[8..]);

        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || !MemoryReaderExtensions.IsPlausiblePointer(end))
        {
            return false;
        }

        if (end < begin)
        {
            return false;
        }

        long length = (long)(end - begin);
        if (length == 0 || length > 0x0100_0000)
        {
            return false;
        }

        span = length;
        sizes = [.. LikelyElementSizes.Where(size => length % size == 0 && length / size <= 1_000_000)];
        return true;
    }

    private static bool Readable(string text)
    {
        if (text.Length < 3)
        {
            return false;
        }

        int printable = text.Count(c => c is >= ' ' and <= '~');
        return printable == text.Length;
    }

    private static string Trim(string text) => text.Length <= 60 ? text : text[..57] + "...";

    private static string Preview(ReadOnlySpan<byte> head)
    {
        var text = new System.Text.StringBuilder(PreviewBytes * 3);
        foreach (byte value in head)
        {
            text.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }

        return text.ToString().TrimEnd();
    }
}
