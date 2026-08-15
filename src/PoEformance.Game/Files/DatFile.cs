using System.Buffers.Binary;

namespace PoEformance.Game.Files;

/// <summary>A foreign reference as the FILE stores it: two 64-bit words.</summary>
/// <remarks>
/// Which of the two is the row is not written down anywhere this project trusts. The memory
/// layout is known - table reference then row reference, so the row sits at +8 - and the file
/// is NOT the same thing: <see cref="DatFile"/> resolves it by asking which half is a
/// plausible row index of the table being pointed at, and says which one answered. An
/// observation beats a convention that might be the other way round.
/// </remarks>
public readonly record struct DatReference(ulong First, ulong Second)
{
    /// <summary>The value a null reference carries in both halves.</summary>
    public const ulong Nothing = ulong.MaxValue;

    /// <summary>True when neither half names anything.</summary>
    public bool IsNothing => First is Nothing or 0xFFFF_FFFF && Second is Nothing or 0xFFFF_FFFF;

    /// <summary>Whichever half is a row of a table with this many rows, or -1.</summary>
    public int RowIn(long rows)
    {
        if (First < (ulong)rows)
        {
            return (int)First;
        }

        return Second < (ulong)rows ? (int)Second : -1;
    }
}

/// <summary>
/// One of the game's .datc64 tables, read out of the install rather than out of memory.
/// </summary>
/// <remarks>
/// WHY THE FILE AND NOT THE PROCESS. The resident copy of a table is missing the half that
/// matters here: the schema's DatFileImage note records that the fixed-size rows are in memory
/// and the variable-length section is not, so a string or an ARRAY column - which is a count
/// and an offset INTO that section - cannot be followed there. Quest states are arrays of flag
/// references, so the file is the only place they can be read whole.
///
/// THE ROW SIZE IS DERIVED, NOT DECLARED, and that is the check this whole reader rests on.
/// A .dat is a row count, the rows, eight bytes of 0xBB, then the variable section - so the
/// size of a row is (where the 0xBB starts, minus four) divided by the count, with no schema
/// involved. Comparing that against the size the column list computes is a real test of the
/// column list: they agree or the layout is wrong, and a reader that finds out is worth far
/// more than one that quietly reads every field four bytes out.
/// </remarks>
public sealed class DatFile
{
    /// <summary>The eight bytes between the rows and the variable-length section.</summary>
    private const ulong Separator = 0xBBBB_BBBB_BBBB_BBBB;

    /// <summary>Longest string taken seriously, so a bad offset cannot run to the file end.</summary>
    private const int LongestString = 1024;

    private readonly byte[] _bytes;

    private DatFile(byte[] bytes, int rows, int rowSize, int variableAt)
    {
        _bytes = bytes;
        Rows = rows;
        RowSize = rowSize;
        VariableAt = variableAt;
    }

    /// <summary>How many rows the file declares.</summary>
    public int Rows { get; }

    /// <summary>Bytes per row, derived from where the separator sits.</summary>
    public int RowSize { get; }

    /// <summary>Where the variable-length section starts - offsets in rows are relative to it.</summary>
    public int VariableAt { get; }

    /// <summary>Reads a table, or null when the bytes are not one.</summary>
    public static DatFile? Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 12)
        {
            return null;
        }

        int rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (rows is <= 0 or > 4_000_000)
        {
            return null;
        }

        // The FIRST separator, scanned on the eight-byte grid the rows themselves sit on.
        // A row could hold these bytes as data, which is why the row size it implies is
        // checked for divisibility rather than taken on trust.
        for (int at = 4; at + 8 <= bytes.Length; at += 4)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) != Separator)
            {
                continue;
            }

            int fixedBytes = at - 4;
            if (fixedBytes <= 0 || fixedBytes % rows != 0)
            {
                continue;
            }

            return new DatFile(bytes, rows, fixedBytes / rows, at);
        }

        return null;
    }

    /// <summary>Where a row starts, or -1 when it is not in the file.</summary>
    private int At(int row, int offset, int width)
    {
        if (row < 0 || row >= Rows || offset < 0)
        {
            return -1;
        }

        int at = 4 + (row * RowSize) + offset;
        return offset + width <= RowSize && at + width <= _bytes.Length ? at : -1;
    }

    /// <summary>A 32-bit column.</summary>
    public int I32(int row, int offset)
    {
        int at = At(row, offset, 4);
        return at < 0 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(at));
    }

    /// <summary>A 64-bit column.</summary>
    public ulong U64(int row, int offset)
    {
        int at = At(row, offset, 8);
        return at < 0 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at));
    }

    /// <summary>A foreign reference column, as its two raw halves.</summary>
    public DatReference Reference(int row, int offset)
    {
        int at = At(row, offset, 16);
        return at < 0
            ? new DatReference(DatReference.Nothing, DatReference.Nothing)
            : new DatReference(
                BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at)),
                BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at + 8)));
    }

    /// <summary>
    /// A string column: an offset into the variable-length section, UTF-16 there.
    /// </summary>
    public string Text(int row, int offset)
    {
        int at = At(row, offset, 8);
        if (at < 0)
        {
            return string.Empty;
        }

        ulong into = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at));
        return TextAt(into);
    }

    /// <summary>The characters at an offset into the variable-length section.</summary>
    public string TextAt(ulong into)
    {
        long start = VariableAt + (long)into;
        if (into > (ulong)_bytes.Length || start < 0 || start + 2 > _bytes.Length)
        {
            return string.Empty;
        }

        int end = (int)start;
        while (end + 2 <= _bytes.Length
               && end - start < LongestString * 2
               && (_bytes[end] != 0 || _bytes[end + 1] != 0))
        {
            end += 2;
        }

        return System.Text.Encoding.Unicode.GetString(_bytes.AsSpan((int)start, end - (int)start));
    }

    /// <summary>
    /// An array column: a count and an offset into the variable-length section.
    /// </summary>
    /// <param name="elementWidth">
    /// How wide one element is - 16 for an array of foreign references, 4 for one of ints.
    /// </param>
    public IReadOnlyList<DatReference> References(int row, int offset, int elementWidth = 16)
    {
        int at = At(row, offset, 16);
        if (at < 0)
        {
            return [];
        }

        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at));
        ulong into = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at + 8));
        if (count == 0 || count > 4096)
        {
            return [];
        }

        long start = VariableAt + (long)into;
        if (start < 0 || start + ((long)count * elementWidth) > _bytes.Length)
        {
            return [];
        }

        var made = new List<DatReference>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            long entry = start + ((long)i * elementWidth);
            made.Add(elementWidth >= 16
                ? new DatReference(
                    BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan((int)entry)),
                    BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan((int)entry + 8)))
                : new DatReference(BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan((int)entry)), DatReference.Nothing));
        }

        return made;
    }
}
