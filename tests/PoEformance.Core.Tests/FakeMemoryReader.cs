using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// An in-memory "process" for tests: byte regions placed at chosen addresses.
/// Reads succeed only where a region provides every requested byte, which mirrors
/// how ReadProcessMemory fails on unmapped pages.
/// </summary>
public sealed class FakeMemoryReader : IMemoryReader
{
    private readonly List<(ulong Address, byte[] Bytes)> _regions = [];

    public bool IsAttached => true;

    public int ProcessId => 4242;

    public ulong ModuleBase { get; set; } = 0x1400000000;

    public uint ModuleSize { get; set; }

    /// <summary>Places bytes at an absolute address. Later regions win on overlap.</summary>
    public FakeMemoryReader Place(ulong address, params byte[] bytes)
    {
        _regions.Add((address, bytes));
        return this;
    }

    /// <summary>Places an unmanaged value at an absolute address.</summary>
    public FakeMemoryReader Place<T>(ulong address, T value)
        where T : unmanaged
    {
        var bytes = new byte[System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in value);
        return Place(address, bytes);
    }

    /// <summary>Places a NUL-terminated UTF-16 string at an absolute address.</summary>
    public FakeMemoryReader PlaceUtf16(ulong address, string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        return Place(address, bytes);
    }

    /// <summary>Places a NUL-terminated UTF-8/ASCII string at an absolute address.</summary>
    public FakeMemoryReader PlaceUtf8(ulong address, string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        var bytes = new byte[utf8.Length + 1];
        utf8.CopyTo(bytes, 0);
        return Place(address, bytes);
    }

    /// <summary>
    /// Places an MSVC std::wstring (header + out-of-line character data) so schema
    /// string invariants can be exercised. <paramref name="dataAddress"/> is where the
    /// characters go when the string is too long for the SSO buffer.
    /// </summary>
    public FakeMemoryReader PlaceStdWString(ulong headerAddress, string text, ulong dataAddress)
    {
        var header = new byte[32];
        if (text.Length < 8)
        {
            System.Text.Encoding.Unicode.GetBytes(text, header.AsSpan(0, 16));
            System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(16), (long)text.Length);
            System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(24), 7L);
            return Place(headerAddress, header);
        }

        System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(0), dataAddress);
        System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(16), (long)text.Length);
        System.Runtime.InteropServices.MemoryMarshal.Write(header.AsSpan(24), (long)Math.Max(text.Length, 8));
        Place(headerAddress, header);
        return PlaceUtf16(dataAddress, text);
    }

    /// <summary>
    /// How many reads have been served, so a test can assert on the COST of one.
    /// </summary>
    /// <remarks>
    /// Some of what this project does is only correct because it is cheap - a cache that is
    /// re-checked rather than rebuilt, a parent chain walked once for a list of siblings - and
    /// the difference between the fast arrangement and the slow one is invisible in the answer
    /// both produce. Counting reads is the only way such a decision can be tested at all.
    /// </remarks>
    public long Reads { get; private set; }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        Reads++;

        if (destination.IsEmpty)
        {
            return false;
        }

        Span<bool> covered = destination.Length <= 4096 ? stackalloc bool[destination.Length] : new bool[destination.Length];
        int remaining = destination.Length;

        for (int i = _regions.Count - 1; i >= 0 && remaining > 0; i--)
        {
            (ulong regionAddress, byte[] bytes) = _regions[i];
            ulong regionEnd = regionAddress + (ulong)bytes.Length;
            ulong readEnd = address + (ulong)destination.Length;
            if (regionAddress >= readEnd || regionEnd <= address)
            {
                continue;
            }

            ulong overlapStart = Math.Max(regionAddress, address);
            ulong overlapEnd = Math.Min(regionEnd, readEnd);
            int sourceOffset = (int)(overlapStart - regionAddress);
            int destOffset = (int)(overlapStart - address);
            int length = (int)(overlapEnd - overlapStart);

            for (int j = 0; j < length; j++)
            {
                if (!covered[destOffset + j])
                {
                    destination[destOffset + j] = bytes[sourceOffset + j];
                    covered[destOffset + j] = true;
                    remaining--;
                }
            }
        }

        return remaining == 0;
    }

    public void Dispose()
    {
    }
}
