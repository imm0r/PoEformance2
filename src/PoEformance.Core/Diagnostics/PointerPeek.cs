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

    /// <summary>Looks at what a pointer leads to.</summary>
    public static PeekResult Peek(IMemoryReader reader, ulong address)
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
