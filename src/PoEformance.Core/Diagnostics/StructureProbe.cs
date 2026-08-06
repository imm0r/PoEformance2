using System.Buffers.Binary;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>What a slot of memory most likely holds.</summary>
/// <remarks>
/// A GUESS, and named as one everywhere it is used. Bytes carry no type, so this is the eye's
/// shortcut and nothing more - the wrong guess costs a glance, and being told "this is an
/// integer" in a tone that sounds certain costs an afternoon.
/// </remarks>
public enum SlotGuess
{
    /// <summary>Eight zero bytes. Empty, padding, or something not filled in yet.</summary>
    Empty,

    /// <summary>An address that could be read. The thing worth following.</summary>
    Pointer,

    /// <summary>An address inside the game's own code - a vtable or a function.</summary>
    Code,

    /// <summary>A number with a decimal point, in a range a real quantity sits in.</summary>
    Float,

    /// <summary>A whole number small enough to be a count, an id, or a flag.</summary>
    Integer,

    /// <summary>Readable characters stored in place rather than behind a pointer.</summary>
    Text,

    /// <summary>None of the above. Usually several small fields sharing the row.</summary>
    Bytes,
}

/// <summary>One row of a structure, as every plausible reading of it at once.</summary>
/// <param name="Offset">Distance from the start of the window.</param>
/// <param name="Address">Where this row actually lives, for following and for writing down.</param>
public readonly record struct StructureSlot(
    int Offset,
    ulong Address,
    ulong Raw,
    long Int64,
    int Low,
    int High,
    float FloatLow,
    float FloatHigh,
    double Double,
    SlotGuess Guess)
{
    /// <summary>True when this row is worth clicking to see where it leads.</summary>
    public bool Followable => Guess == SlotGuess.Pointer;
}

/// <summary>
/// Turns a window of raw memory into something a person can read.
/// </summary>
/// <remarks>
/// The tool for the question this cannot answer any other way: "there is a structure here and
/// nobody knows what is in it". Every field of every struct in the schema started as a row in
/// a view like this one, and the way it stopped being unknown was somebody looking at it while
/// something happened in the game.
///
/// Pure on purpose - it takes bytes and returns readings. That keeps the guessing testable
/// without a game attached, and it means the same code describes a live process, a replayed
/// recording, and a synthetic buffer in a test.
/// </remarks>
public static class StructureProbe
{
    /// <summary>
    /// Floats below this are almost certainly a small integer being read the wrong way.
    /// </summary>
    /// <remarks>
    /// This is what separates the two most common readings, and it works because the mistaken
    /// versions are absurd rather than merely wrong. A count of 4366 read as a float is
    /// 6.1e-42; a coordinate of 4366.0 read as an integer is 1166864384. Neither is a quantity
    /// anything in a game would hold, so whichever reading is SANE is nearly always the
    /// intended one.
    /// </remarks>
    private const float SmallestSensibleFloat = 1e-3f;

    /// <summary>And above this a float is a bit pattern, not a measurement.</summary>
    private const float LargestSensibleFloat = 1e9f;

    /// <summary>
    /// Below four gigabytes, a pointer-shaped value is nearly always two other fields.
    /// </summary>
    /// <remarks>
    /// This is a stricter bar than <see cref="MemoryReaderExtensions.IsPlausiblePointer"/>,
    /// and deliberately so - the two answer different questions. That one asks "is this safe
    /// to try to follow", which wants to be generous. This one asks "is this a pointer", which
    /// must not be: a float of 4366.0 next to an empty field reads as 0x45887800, comfortably
    /// inside the safe-to-follow range, and calling THAT a pointer would put the label on a
    /// large share of the rows in any structure and make the whole view worthless.
    ///
    /// Safe because of how Windows lays out a 64-bit process: real allocations sit far above
    /// four gigabytes, and the game's own module higher still.
    /// </remarks>
    private const ulong LowestLikelyPointer = 0x1_0000_0000;

    /// <summary>Describes every row of a window of memory.</summary>
    /// <param name="bytes">The window, already read.</param>
    /// <param name="baseAddress">Where the window starts, so each row knows its own address.</param>
    /// <param name="stride">4 or 8. Fields do not all sit on eight-byte boundaries.</param>
    public static List<StructureSlot> Describe(
        ReadOnlySpan<byte> bytes, ulong baseAddress, int stride = 8, ulong moduleBase = 0, uint moduleSize = 0)
    {
        if (stride is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "a row is four or eight bytes");
        }

        var slots = new List<StructureSlot>(bytes.Length / stride);

        for (int offset = 0; offset + stride <= bytes.Length; offset += stride)
        {
            // The eight-byte readings need eight bytes. At the end of an odd-sized window, or
            // on the last four-byte row, they simply are not available.
            bool wide = offset + 8 <= bytes.Length;

            ulong raw = wide ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]) : 0;
            int low = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
            int high = wide ? BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]) : 0;

            slots.Add(new StructureSlot(
                offset,
                baseAddress + (ulong)offset,
                raw,
                wide ? BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]) : low,
                low,
                high,
                BitConverter.Int32BitsToSingle(low),
                BitConverter.Int32BitsToSingle(high),
                wide ? BitConverter.Int64BitsToDouble((long)raw) : 0,
                Guess(bytes[offset..], raw, low, wide, stride, moduleBase, moduleSize)));
        }

        return slots;
    }

    /// <summary>
    /// The offsets whose bytes differ between two readings of the same window.
    /// </summary>
    /// <remarks>
    /// The single most productive thing in this file, and the reason the rest of it exists.
    /// Reading an unknown structure tells you almost nothing; reading it twice, with something
    /// done in the game in between, tells you WHICH PART of it is that thing. Health is the
    /// field that moved when you were hit. The animation is the field that moved when you
    /// cast. Nothing else finds those.
    ///
    /// Reported per row rather than per byte, because a row is the unit a person reads.
    /// </remarks>
    public static List<int> Changes(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int stride = 8)
    {
        if (stride is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "a row is four or eight bytes");
        }

        var changed = new List<int>();
        int length = Math.Min(before.Length, after.Length);

        for (int offset = 0; offset + stride <= length; offset += stride)
        {
            if (!before.Slice(offset, stride).SequenceEqual(after.Slice(offset, stride)))
            {
                changed.Add(offset);
            }
        }

        return changed;
    }

    /// <summary>True when a value is worth showing as an address rather than as numbers.</summary>
    /// <remarks>See <see cref="LowestLikelyPointer"/> for why this is stricter than following one.</remarks>
    public static bool LooksLikePointer(ulong value)
        => value >= LowestLikelyPointer && MemoryReaderExtensions.IsPlausiblePointer(value);

    /// <summary>True when a float is in the range a real quantity occupies.</summary>
    public static bool SensibleFloat(float value)
    {
        if (!float.IsFinite(value))
        {
            return false;
        }

        float size = Math.Abs(value);
        return size >= SmallestSensibleFloat && size <= LargestSensibleFloat;
    }

    private static SlotGuess Guess(
        ReadOnlySpan<byte> row, ulong raw, int low, bool wide, int stride, ulong moduleBase, uint moduleSize)
    {
        if (wide && raw == 0)
        {
            return SlotGuess.Empty;
        }

        if (!wide && low == 0)
        {
            return SlotGuess.Empty;
        }

        // Pointers first, and only on an eight-byte row: a pointer split across two
        // four-byte rows is not a pointer in either of them.
        if (wide && stride == 8 && LooksLikePointer(raw))
        {
            return moduleSize > 0 && raw >= moduleBase && raw < moduleBase + moduleSize
                ? SlotGuess.Code
                : SlotGuess.Pointer;
        }

        if (LooksLikeText(row[..Math.Min(stride, row.Length)]))
        {
            return SlotGuess.Text;
        }

        float asFloat = BitConverter.Int32BitsToSingle(low);
        if (SensibleFloat(asFloat))
        {
            return SlotGuess.Float;
        }

        // Deliberately generous: an id, a count, a handle and a flag are all "some whole
        // number", and separating them is the user's job, not a heuristic's.
        return Math.Abs((long)low) < 0x0100_0000 ? SlotGuess.Integer : SlotGuess.Bytes;
    }

    /// <summary>True when a row is printable characters rather than a number.</summary>
    /// <remarks>
    /// Deliberately strict. Almost any bytes can be squinted at as text, and a view that
    /// labels arbitrary numbers "text" is worse than one that never mentions it - so every
    /// byte has to be ordinary printable ASCII, and one stray value disqualifies the row.
    /// </remarks>
    private static bool LooksLikeText(ReadOnlySpan<byte> row)
    {
        int letters = 0;

        foreach (byte value in row)
        {
            if (value == 0)
            {
                continue;   // a short string padded out with nulls
            }

            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }

            letters++;
        }

        return letters >= 4;
    }
}
