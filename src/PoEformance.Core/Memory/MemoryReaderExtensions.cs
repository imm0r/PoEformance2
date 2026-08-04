using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PoEformance.Core.Memory;

/// <summary>
/// Typed convenience reads on top of <see cref="IMemoryReader.TryRead"/>.
/// </summary>
/// <remarks>
/// These are extension methods rather than interface members on purpose: every
/// implementation gets them for free and can never get them subtly wrong.
/// </remarks>
public static class MemoryReaderExtensions
{
    /// <summary>Lowest address we ever treat as a real user-space pointer.</summary>
    private const ulong MinPlausiblePointer = 0x10000;

    /// <summary>
    /// Highest address we treat as a real user-space pointer. Windows x64 user space
    /// ends at 0x7FFFFFFFFFFF, so anything above is kernel space or garbage.
    /// </summary>
    private const ulong MaxPlausiblePointer = 0x7FFFFFFFFFFF;

    /// <summary>Reads a blittable value. Returns false and default(T) on a failed read.</summary>
    public static bool TryRead<T>(this IMemoryReader reader, ulong address, out T value)
        where T : unmanaged
    {
        Unsafe.SkipInit(out value);
        Span<byte> buffer = MemoryMarshal.AsBytes(new Span<T>(ref value));
        if (reader.TryRead(address, buffer))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a blittable value, returning default(T) when the read fails.</summary>
    public static T Read<T>(this IMemoryReader reader, ulong address)
        where T : unmanaged
        => reader.TryRead(address, out T value) ? value : default;

    /// <summary>
    /// Reads a pointer and returns 0 unless it looks like a real user-space address.
    /// Filtering here rather than at every call site removes a whole class of
    /// "followed a garbage pointer" bugs.
    /// </summary>
    public static ulong ReadPointer(this IMemoryReader reader, ulong address)
    {
        ulong pointer = reader.Read<ulong>(address);
        return IsPlausiblePointer(pointer) ? pointer : 0;
    }

    /// <summary>True when the value is in the range a user-space heap pointer can occupy.</summary>
    public static bool IsPlausiblePointer(ulong value)
        => value >= MinPlausiblePointer && value <= MaxPlausiblePointer;

    /// <summary>
    /// Follows a chain of offsets: reads a pointer at <paramref name="baseAddress"/> +
    /// offsets[0], then at that result + offsets[1], and so on. Returns 0 as soon as any
    /// hop yields an implausible pointer, so a broken chain fails quietly instead of
    /// reading nonsense.
    /// </summary>
    public static ulong ReadChain(this IMemoryReader reader, ulong baseAddress, params int[] offsets)
    {
        ulong current = baseAddress;
        foreach (int offset in offsets)
        {
            if (!IsPlausiblePointer(current))
            {
                return 0;
            }

            current = reader.ReadPointer(current + (ulong)offset);
        }

        return current;
    }

    /// <summary>
    /// Reads a UTF-16 string of at most <paramref name="maxChars"/> characters, stopping
    /// at the first NUL. Used for the game's wide strings once the pointer is already known.
    /// </summary>
    /// <remarks>
    /// Page-aware: a single big read fails entirely when the string sits near the end of
    /// a mapped page, so this reads in chunks that never cross a 4 KB boundary and stops
    /// at the first NUL or the first unreadable chunk. A string cut short by an unmapped
    /// page returns the part that was readable - the useful behaviour when exploring.
    /// </remarks>
    public static string ReadUtf16(this IMemoryReader reader, ulong address, int maxChars = 256)
    {
        if (maxChars <= 0 || address == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        ulong position = address;
        int remainingBytes = maxChars * 2;
        Span<byte> chunk = stackalloc byte[512];

        while (remainingBytes > 0)
        {
            int untilPageEnd = (int)(PageSize - (position & (PageSize - 1)));
            int take = Math.Min(chunk.Length, Math.Min(remainingBytes, untilPageEnd));
            take &= ~1; // keep char alignment

            // Adaptive shrink: a chunk that fails (region edge, or a replay that only
            // captured a shorter read) is retried at half size down to one char. This
            // keeps live and replayed sessions behaviourally identical.
            while (take >= 2 && !reader.TryRead(position, chunk[..take]))
            {
                take = (take / 2) & ~1;
            }

            if (take < 2)
            {
                break;
            }

            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(chunk[..take]);
            int end = chars.IndexOf('\0');
            if (end >= 0)
            {
                builder.Append(chars[..end]);
                return builder.ToString();
            }

            builder.Append(chars);
            position += (ulong)take;
            remainingBytes -= take;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a NUL-terminated UTF-8 / ASCII string of at most <paramref name="maxBytes"/> bytes.
    /// Page-aware like <see cref="ReadUtf16"/>.
    /// </summary>
    public static string ReadUtf8(this IMemoryReader reader, ulong address, int maxBytes = 256)
    {
        if (maxBytes <= 0 || address == 0)
        {
            return string.Empty;
        }

        var bytes = new List<byte>(Math.Min(maxBytes, 64));
        ulong position = address;
        int remaining = maxBytes;
        Span<byte> chunk = stackalloc byte[512];

        while (remaining > 0)
        {
            int untilPageEnd = (int)(PageSize - (position & (PageSize - 1)));
            int take = Math.Min(chunk.Length, Math.Min(remaining, untilPageEnd));
            while (take >= 1 && !reader.TryRead(position, chunk[..take]))
            {
                take /= 2;
            }

            if (take < 1)
            {
                break;
            }

            Span<byte> slice = chunk[..take];

            int end = slice.IndexOf((byte)0);
            if (end >= 0)
            {
                for (int i = 0; i < end; i++)
                {
                    bytes.Add(slice[i]);
                }

                break;
            }

            for (int i = 0; i < slice.Length; i++)
            {
                bytes.Add(slice[i]);
            }

            position += (ulong)take;
            remaining -= take;
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>
    /// Reads an MSVC <c>std::wstring</c> at <paramref name="address"/>, handling the
    /// small-string optimisation: capacity &lt; 8 means the characters live inline in
    /// the first 16 bytes, otherwise the first 8 bytes are a pointer to them.
    /// </summary>
    /// <remarks>
    /// This is how the game stores every path and name (entity metadata paths, tile
    /// paths, league name, item art paths). Garbage headers are rejected via the
    /// size/capacity sanity checks instead of producing a nonsense string.
    /// </remarks>
    public static string ReadStdWString(this IMemoryReader reader, ulong address, int maxChars = 512)
    {
        Span<byte> header = stackalloc byte[32];
        if (!reader.TryRead(address, header))
        {
            return string.Empty;
        }

        long size = MemoryMarshal.Read<long>(header[16..]);
        long capacity = MemoryMarshal.Read<long>(header[24..]);
        if (size < 0 || capacity < size || capacity > 100_000)
        {
            return string.Empty;
        }

        int chars = (int)Math.Min(size, maxChars);
        if (chars == 0)
        {
            return string.Empty;
        }

        if (capacity < 8)
        {
            // Inline (SSO): characters occupy the first 16 header bytes.
            ReadOnlySpan<char> inline = MemoryMarshal.Cast<byte, char>(header[..16]);
            return new string(inline[..Math.Min(chars, 7)]);
        }

        ulong dataPtr = MemoryMarshal.Read<ulong>(header);
        if (!IsPlausiblePointer(dataPtr))
        {
            return string.Empty;
        }

        int want = chars * 2;
        var buffer = new byte[want];
        while (want >= 2 && !reader.TryRead(dataPtr, buffer.AsSpan(0, want)))
        {
            want = (want / 2) & ~1;
        }

        if (want < 2)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> data = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, want));
        int nul = data.IndexOf('\0');
        return new string(nul < 0 ? data : data[..nul]);
    }

    /// <summary>x64 page size, for chunked string reads.</summary>
    private const ulong PageSize = 4096;
}
