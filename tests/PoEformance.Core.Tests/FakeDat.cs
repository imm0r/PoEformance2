using System.Buffers.Binary;
using System.Text;

namespace PoEformance.Core.Tests;

/// <summary>
/// A <c>.datc64</c> table built by hand, in the shape the game writes them.
/// </summary>
/// <remarks>
/// WHY SYNTHETIC AND NOT A CAPTURED FILE, the same two reasons QuestProgressTests and
/// UniqueNamesTests give: the game's own tables are hundreds of megabytes nobody has in CI, and a
/// made-up one can be made to hold the awkward case on purpose - an empty array, a null reference,
/// a filler row - which is the half a real file will not reliably contain.
///
/// THE FORMAT, from DatFile's own reader rather than from a description of it: a row count, the
/// fixed-size rows, eight bytes of 0xBB, then the variable-length section. Offsets into that
/// section COUNT FROM THE SEPARATOR, so byte zero of the section is offset eight - a fixture that
/// prepends the eight bytes AND counts them points every string at the one before it, which is how
/// the unique-name fixture first went wrong.
///
/// ARRAYS ARE WRITTEN COUNT FIRST, which is DatFile's own default when nothing has measured the
/// file - see its DetectArrays. A fixture that wrote them the other way round would be testing the
/// detector rather than the reader.
/// </remarks>
internal sealed class FakeDat(int rows, int rowSize)
{
    /// <summary>What a foreign reference's second word holds when the first is the row.</summary>
    private const ulong TableMarker = 0;

    /// <summary>
    /// What a reference's first word holds when it names nothing.
    /// </summary>
    /// <remarks>
    /// 0xFEFEFEFE, from poe-dat-viewer's own reader rather than from the shape of the bytes -
    /// see DatReference.Null. Written in the FIRST word with a plain zero left in the second,
    /// which is the adversarial half of this fixture: if a reader falls through to the second
    /// word it finds a perfectly valid row nought.
    /// </remarks>
    public const ulong Nothing = 0xFEFE_FEFE_FEFE_FEFE;

    private readonly byte[] _rows = new byte[rows * rowSize];
    private readonly List<byte> _variable = [];

    /// <summary>How many rows it has, which is what a reference into it has to stay under.</summary>
    public int Rows => rows;

    /// <summary>A four-byte column.</summary>
    public FakeDat I32(int row, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(At(row, offset), value);
        return this;
    }

    /// <summary>A four-byte FLOAT column, whose bits are not its value.</summary>
    public FakeDat F32(int row, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(At(row, offset), value);
        return this;
    }

    /// <summary>A one-byte column.</summary>
    public FakeDat Bool(int row, int offset, bool value)
    {
        _rows[(row * rowSize) + offset] = value ? (byte)1 : (byte)0;
        return this;
    }

    /// <summary>A string column: an eight-byte offset into the variable section.</summary>
    public FakeDat Text(int row, int offset, string text)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset), Put(text));
        return this;
    }

    /// <summary>A foreign reference: the row in the first word, the table's marker in the second.</summary>
    public FakeDat Reference(int row, int offset, int target)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset), (ulong)target);
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset + 8), TableMarker);
        return this;
    }

    /// <summary>
    /// A reference that names nothing, which is NOT the same as one naming row zero.
    /// </summary>
    /// <remarks>
    /// THE HALF THAT MATTERS IS THE SECOND. The null marker goes in the first word and the second
    /// is left at zero, and DatReference.RowIn tries whichever word is a valid row - so unless the
    /// null is recognised first, an unset column reads as ROW ZERO. Row zero of BloodTypes is
    /// "Blood", which 1092 monsters really do carry, so the wrong answer is not merely silent: it
    /// is the commonest right one, on a monster that has no blood type at all.
    /// </remarks>
    public FakeDat Null(int row, int offset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset), Nothing);
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset + 8), TableMarker);
        return this;
    }

    /// <summary>An array of foreign references: a count and an offset, then sixteen bytes each.</summary>
    public FakeDat References(int row, int offset, params int[] targets)
    {
        ulong into = (ulong)(8 + _variable.Count);
        foreach (int target in targets)
        {
            Add((ulong)target);
            Add(TableMarker);
        }

        return Array(row, offset, targets.Length, into);
    }

    /// <summary>An array of strings: a count and an offset, then eight bytes each.</summary>
    public FakeDat Texts(int row, int offset, params string[] texts)
    {
        // THE STRINGS GO IN FIRST and the block of pointers after them, so that writing the block
        // does not move the text it points at. Interleaving them is how a fixture ends up with an
        // array whose last entry points past the end of the section.
        ulong[] at = [.. texts.Select(Put)];
        ulong into = (ulong)(8 + _variable.Count);
        foreach (ulong one in at)
        {
            Add(one);
        }

        return Array(row, offset, texts.Length, into);
    }

    /// <summary>The whole file: the count, the rows, the separator, the variable section.</summary>
    public byte[] Bytes()
    {
        var bytes = new byte[4 + _rows.Length + 8 + _variable.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows);
        _rows.CopyTo(bytes, 4);

        for (var i = 0; i < 8; i++)
        {
            bytes[4 + _rows.Length + i] = 0xBB;
        }

        _variable.CopyTo(bytes, 4 + _rows.Length + 8);
        return bytes;
    }

    private FakeDat Array(int row, int offset, int count, ulong into)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset), (ulong)count);
        BinaryPrimitives.WriteUInt64LittleEndian(At(row, offset + 8), into);
        return this;
    }

    /// <summary>Puts a string in the variable section and says where it went.</summary>
    private ulong Put(string text)
    {
        ulong into = (ulong)(8 + _variable.Count);
        _variable.AddRange(Encoding.Unicode.GetBytes(text));
        _variable.AddRange([0, 0]);
        return into;
    }

    private void Add(ulong value)
    {
        Span<byte> eight = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(eight, value);
        _variable.AddRange(eight);
    }

    private Span<byte> At(int row, int offset) => _rows.AsSpan((row * rowSize) + offset);
}
