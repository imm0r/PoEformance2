using System.Buffers.Binary;

namespace PoEformance.Game.Files;

/// <summary>Which way round an array column stores its two words.</summary>
/// <remarks>
/// NOT KNOWN FROM ANY SOURCE THIS PROJECT TRUSTS, which is why it is a measurement and not a
/// constant. The reader was written count-first; the AHK tool's layout table carries a comment
/// saying "8-byte offset + 8-byte count", the other way round - and that tool never decodes an
/// array, so its comment is a belief rather than a test. Two beliefs pointing opposite ways is
/// exactly the situation that produces a plausible wrong answer, so the file is asked.
/// </remarks>
public enum ArrayOrder
{
    /// <summary>Nothing decided yet - reads fall back to count first.</summary>
    Unknown,

    /// <summary>The count, then the offset.</summary>
    CountFirst,

    /// <summary>The offset, then the count.</summary>
    OffsetFirst,
}

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

    /// <summary>Most elements an array is believed to hold, as a plausibility bound.</summary>
    private const int MostElements = 4096;

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

    /// <summary>
    /// Which way round this file's array columns are, once something has measured it.
    /// </summary>
    public ArrayOrder Arrays { get; private set; }

    /// <summary>
    /// Works out the array word order by trying both against a column that holds arrays.
    /// </summary>
    /// <remarks>
    /// A COUNT and an OFFSET are told apart by what they have to satisfy, not by what they
    /// look like: the count must be small, the offset must land inside the variable-length
    /// section, and the elements must fit between the offset and the end of it. One reading
    /// of a row satisfies all three and the other usually satisfies none, so counting the
    /// rows where exactly one works decides it for the file.
    ///
    /// Rows where BOTH readings work say nothing - an empty array is two zeros either way -
    /// and are not counted for either side.
    /// </remarks>
    public ArrayOrder DetectArrays(int columnOffset, int elementWidth = 16)
    {
        var countFirst = 0;
        var offsetFirst = 0;
        int variableLength = _bytes.Length - VariableAt;

        for (var row = 0; row < Rows; row++)
        {
            int at = At(row, columnOffset, 16);
            if (at < 0)
            {
                break;
            }

            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at));
            ulong second = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at + 8));

            bool a = Fits(first, second, elementWidth, variableLength);
            bool b = Fits(second, first, elementWidth, variableLength);
            if (a && !b)
            {
                countFirst++;
            }
            else if (b && !a)
            {
                offsetFirst++;
            }
        }

        Arrays = countFirst == offsetFirst
            ? ArrayOrder.Unknown
            : countFirst > offsetFirst ? ArrayOrder.CountFirst : ArrayOrder.OffsetFirst;
        return Arrays;
    }

    /// <summary>Whether this pair reads as a non-empty array that lies inside the file.</summary>
    private static bool Fits(ulong count, ulong into, int elementWidth, int variableLength)
        => count is > 0 and <= MostElements
           && into <= (ulong)variableLength
           && into + (count * (ulong)elementWidth) <= (ulong)variableLength;

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

        // EVERY byte offset, not every fourth. Rows are PACKED - Quest.dat's is 103 bytes -
        // so the separator lands wherever the rows end and is under no obligation to be
        // aligned to anything. Scanning on the four-byte grid found QuestStates (208) and
        // QuestFlags (12) and walked straight past Quest, which reported "in the install but
        // did not parse" - a wrong answer that looked like a missing file.
        //
        // A row could hold these bytes as data, so the row size the position implies is
        // checked for divisibility rather than taken on trust, and the search carries on when
        // it does not divide.
        Span<byte> separator = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(separator, Separator);

        for (int from = 4; from + 8 <= bytes.Length;)
        {
            int found = bytes.AsSpan(from).IndexOf(separator);
            if (found < 0)
            {
                return null;
            }

            int at = from + found;
            int fixedBytes = at - 4;
            if (fixedBytes > 0 && fixedBytes % rows == 0)
            {
                return new DatFile(bytes, rows, fixedBytes / rows, at);
            }

            from = at + 1;
        }

        return null;
    }

    /// <summary>How many rows a probe looks at before deciding.</summary>
    private const int ProbeRows = 64;

    /// <summary>What fraction of a probe's reads must be text, in tenths.</summary>
    /// <remarks>
    /// Eight in ten rather than all of them. A real string column has empty rows in it - not
    /// every quest has an icon, not every state has a tracker line - and demanding perfection
    /// would reject the correct offset for being honest about a blank.
    /// </remarks>
    private const int ProbeTenths = 8;

    /// <summary>
    /// Whether these offsets read as text across the table, which is how a layout is checked
    /// against the game rather than against the schema that produced it.
    /// </summary>
    /// <remarks>
    /// THE CHECK THAT MATTERS when the row size disagrees. A column list can be one patch out
    /// of date in its TAIL and still be right about its front, and the front is usually all a
    /// reader wants - so "does the field I am about to read actually hold a name" is a better
    /// question than "does the whole row add up".
    /// </remarks>
    public bool ReadsAsText(IReadOnlyList<int> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);

        if (offsets.Count == 0)
        {
            return false;
        }

        int rows = Math.Min(Rows, ProbeRows);
        var good = 0;
        var total = 0;

        for (var row = 0; row < rows; row++)
        {
            foreach (int offset in offsets)
            {
                total++;
                if (Printable(Text(row, offset)))
                {
                    good++;
                }
            }
        }

        return total > 0 && good * 10 >= total * ProbeTenths;
    }

    /// <summary>
    /// Every offset in a row that reads as text, which finds a moved column when one has moved.
    /// </summary>
    /// <remarks>
    /// Not used to DECIDE anything - it is reported, so that a layout which stopped matching
    /// says where its strings actually went instead of only that it failed. Strings are the
    /// one column type that can be recognised from the bytes alone, which is what makes this
    /// possible at all.
    /// </remarks>
    public IReadOnlyList<int> TextOffsets()
    {
        var found = new List<int>();
        for (var offset = 0; offset + 8 <= RowSize; offset++)
        {
            if (ReadsAsText([offset]))
            {
                found.Add(offset);
            }
        }

        return found;
    }

    private static bool Printable(string text)
        => text.Length >= 2 && text.All(c => c is >= ' ' and <= '~');

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
    /// <remarks>
    /// OFFSET 0 IS HOW A ROW SAYS "no string". Offsets count from the separator, so the first
    /// eight bytes of the section are the 0xBB magic itself and nothing real can start there -
    /// an unset column simply holds zero. Reading it anyway returns a run of U+BBBB, which is
    /// not empty, and a caller choosing between two text columns then picks the garbage over
    /// the real one. Found exactly that way: a step with only a long form started reporting
    /// its empty short form as the objective.
    /// </remarks>
    public string TextAt(ulong into)
    {
        long start = VariableAt + (long)into;
        if (into < 8 || into > (ulong)_bytes.Length || start < 0 || start + 2 > _bytes.Length)
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

        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at));
        ulong second = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(at + 8));
        (ulong count, ulong into) = Arrays == ArrayOrder.OffsetFirst ? (second, first) : (first, second);

        if (count == 0 || count > MostElements)
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
