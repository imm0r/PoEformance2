namespace PoEformance.Core.Scanning;

using PoEformance.Core.Memory;

/// <summary>
/// Sweeps the target's main module for byte patterns and resolves RIP-relative
/// addresses out of the matched instructions.
/// </summary>
/// <remarks>
/// The module image is copied out of the target in chunks and scanned locally - one
/// large read beats thousands of small ones by orders of magnitude. The copy is kept
/// for the scanner's lifetime so several patterns scan the same snapshot; call sites
/// create, scan everything, and drop the scanner to release the buffer.
/// </remarks>
public sealed class PatternScanner
{
    /// <summary>Chunk size for copying the module image out of the target.</summary>
    private const int CopyChunkSize = 4 * 1024 * 1024;

    private readonly IMemoryReader _reader;
    private byte[]? _image;

    public PatternScanner(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Finds the first match of <paramref name="pattern"/> and returns its absolute
    /// address in the target, or 0 when the pattern does not occur.
    /// </summary>
    public ulong Find(BytePattern pattern)
    {
        List<ulong> all = FindAll(pattern, maxMatches: 1);
        return all.Count > 0 ? all[0] : 0;
    }

    /// <summary>Finds up to <paramref name="maxMatches"/> matches as absolute addresses.</summary>
    public List<ulong> FindAll(BytePattern pattern, int maxMatches = 16)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        byte[]? image = EnsureImage();
        if (image is null)
        {
            return [];
        }

        List<int> offsets = pattern.FindAll(image, maxMatches);
        var results = new List<ulong>(offsets.Count);
        foreach (int offset in offsets)
        {
            results.Add(_reader.ModuleBase + (ulong)offset);
        }

        return results;
    }

    /// <summary>
    /// Resolves the RIP-relative operand of an x64 instruction: reads the signed 32-bit
    /// displacement at <paramref name="instructionAddress"/> + <paramref name="displacementOffset"/>
    /// and returns <c>instruction + instructionLength + displacement</c> - the absolute
    /// address the instruction refers to.
    /// </summary>
    /// <remarks>
    /// For the typical <c>48 8B 05 xx xx xx xx</c> (mov rax, [rip+disp32]) the
    /// displacement sits at offset 3 and the instruction is 7 bytes long. The schema
    /// stores both numbers per pattern so this method never guesses.
    /// </remarks>
    public ulong ResolveRip(ulong instructionAddress, int displacementOffset, int instructionLength)
    {
        if (instructionAddress == 0)
        {
            return 0;
        }

        if (!_reader.TryRead(instructionAddress + (ulong)displacementOffset, out int displacement))
        {
            return 0;
        }

        ulong target = instructionAddress + (ulong)instructionLength + (ulong)(long)displacement;
        return MemoryReaderExtensions.IsPlausiblePointer(target) ? target : 0;
    }

    /// <summary>Copies the module image out of the target once. Null when unattached.</summary>
    private byte[]? EnsureImage()
    {
        if (_image is not null)
        {
            return _image;
        }

        if (!_reader.IsAttached || _reader.ModuleBase == 0 || _reader.ModuleSize == 0)
        {
            return null;
        }

        var image = new byte[_reader.ModuleSize];
        int position = 0;
        while (position < image.Length)
        {
            int chunk = Math.Min(CopyChunkSize, image.Length - position);

            // Individual pages of the image can be unreadable (guard pages); a failed
            // chunk is left zeroed rather than failing the whole scan.
            _reader.TryRead(_reader.ModuleBase + (ulong)position, image.AsSpan(position, chunk));
            position += chunk;
        }

        _image = image;
        return image;
    }
}
