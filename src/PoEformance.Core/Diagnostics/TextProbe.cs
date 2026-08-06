using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>How a piece of text is stored, when a row turns out to hold one.</summary>
public enum TextShape
{
    /// <summary>Not text.</summary>
    None,

    /// <summary>Short enough to live in the row itself - no read needed to show it.</summary>
    Inline,

    /// <summary>A header: the characters are elsewhere, and it takes one read to fetch them.</summary>
    Heap,
}

/// <summary>What was found at a row that looks like text.</summary>
/// <param name="Text">The characters, for <see cref="TextShape.Inline"/>. Empty otherwise.</param>
/// <param name="Address">Where the characters live, for <see cref="TextShape.Heap"/>.</param>
/// <param name="Length">How many characters there are, for <see cref="TextShape.Heap"/>.</param>
public readonly record struct TextCandidate(TextShape Shape, string Text, ulong Address, int Length)
{
    public static TextCandidate None { get; } = new(TextShape.None, string.Empty, 0, 0);
}

/// <summary>
/// Finds the text in a window of memory.
/// </summary>
/// <remarks>
/// Worth its own file because of how much of this game is strings: every metadata path, every
/// tile file, every area and item name is a std::wstring, and in a dissector without this they
/// appear as a pointer followed by two apparently meaningless numbers spread over four rows.
/// Reading those four rows as "Metadata/Monsters/MudGolem/SandGolem" is the difference between
/// browsing and guessing.
///
/// The header is ALREADY in the window - it is the same bytes the rows are drawn from - so
/// spotting one costs nothing, and a short string costs nothing to show either, since it is
/// stored in the row. Only the characters of a long one need fetching.
///
/// Deliberately stricter than <see cref="MemoryReaderExtensions.ReadStdWString"/>, which is
/// generous because it is called where a string is already expected. Here nothing is expected,
/// every row is offered to it, and a "string" label on random numbers would send someone
/// looking for a name that does not exist.
/// </remarks>
public static class TextProbe
{
    /// <summary>Bytes a std::wstring header occupies: characters or pointer, size, capacity.</summary>
    public const int HeaderSize = 32;

    /// <summary>Characters that fit inside the header before the game moves them out.</summary>
    private const int InlineCapacity = 8;

    /// <summary>
    /// Longest string treated as real.
    /// </summary>
    /// <remarks>
    /// The reader's own limit is 100,000, which is right for a field known to be a string.
    /// Offered every row of a structure it is far too generous: a length that large is
    /// overwhelmingly likely to be some other number that happens to sit where a length would.
    /// Paths are the longest strings this game holds and they do not approach this.
    /// </remarks>
    private const long LongestSensible = 4096;

    /// <summary>Reads the row at an offset as a string header, if it can be one.</summary>
    public static TextCandidate At(ReadOnlySpan<byte> window, int offset)
    {
        if (offset < 0 || offset + HeaderSize > window.Length)
        {
            return TextCandidate.None;
        }

        ReadOnlySpan<byte> header = window.Slice(offset, HeaderSize);
        long size = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        long capacity = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);

        // A string is at least this consistent with itself. Most rows fail here, which is the
        // point - the two numbers following arbitrary bytes are almost never a size and a
        // capacity that agree.
        if (size <= 0 || capacity < size || capacity > LongestSensible)
        {
            return TextCandidate.None;
        }

        if (capacity < InlineCapacity)
        {
            ReadOnlySpan<char> inline = MemoryMarshal.Cast<byte, char>(header[..16]);
            string text = new(inline[..(int)Math.Min(size, 7)]);
            return LooksLikeText(text) ? new TextCandidate(TextShape.Inline, text, 0, text.Length) : TextCandidate.None;
        }

        ulong data = BinaryPrimitives.ReadUInt64LittleEndian(header);
        return MemoryReaderExtensions.IsPlausiblePointer(data)
            ? new TextCandidate(TextShape.Heap, string.Empty, data, (int)size)
            : TextCandidate.None;
    }

    /// <summary>
    /// True when a string is worth showing as one.
    /// </summary>
    /// <remarks>
    /// Almost any bytes can be squinted at as text, so this is the guard that keeps the label
    /// meaningful. Control characters disqualify outright; beyond that it asks for a majority
    /// of ordinary printable characters, since the game's own strings - paths, metadata ids,
    /// names - are overwhelmingly plain ASCII. A rare genuine string in another script is
    /// missed, which is a much cheaper mistake than labelling numbers.
    /// </remarks>
    public static bool LooksLikeText(string text)
    {
        if (text.Length < 3)
        {
            return false;
        }

        int printable = 0;
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return false;
            }

            if (c is >= ' ' and <= '~')
            {
                printable++;
            }
        }

        return printable * 5 >= text.Length * 4;
    }
}
