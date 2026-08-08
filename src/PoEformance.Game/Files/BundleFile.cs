namespace PoEformance.Game.Files;

/// <summary>
/// One of the game's <c>.bundle.bin</c> files: a compressed blob cut into fixed-size chunks.
/// </summary>
/// <remarks>
/// CHUNKS ARE THE POINT. A bundle runs to a couple of hundred megabytes and holds thousands of
/// files, but it is compressed in independent 256 KB pieces with a table of their compressed
/// sizes at the front. So reading one icon out of a 200 MB bundle costs ONE chunk - which is the
/// difference between an icon appearing and the overlay stopping to unpack a bundle. Nothing
/// here ever holds a whole one: it keeps the chunk table, which is a few thousand numbers, and
/// asks for the bytes of a chunk when it needs them.
///
/// The layout is a 60-byte header, then <c>chunk_count</c> four-byte compressed sizes, then the
/// chunks back to back. The header repeats both sizes as 64-bit values, which is where a
/// from-memory version puts the chunk count in the wrong place: it is at 36, AFTER those longs,
/// and the chunk size at 40.
///
/// Format from LibBundle3, which is the reference for it.
/// </remarks>
public sealed class BundleFile
{
    /// <summary>The header, before the table of chunk sizes.</summary>
    public const int HeaderBytes = 60;

    /// <summary>A bound on the chunk table, so a wrong offset is refused rather than allocated from.</summary>
    public const int MostChunks = 100_000;

    /// <summary>And on what one bundle may claim to decompress to.</summary>
    public const int Biggest = 512 * 1024 * 1024;

    private readonly Func<int, int, byte[]?> _bytes;
    private readonly int[] _sizes;
    private readonly long[] _starts;
    private readonly int _dataAt;

    private BundleFile(Func<int, int, byte[]?> bytes, int uncompressed, int chunkSize, int[] sizes, int dataAt)
    {
        _bytes = bytes;
        _sizes = sizes;
        _dataAt = dataAt;
        Uncompressed = uncompressed;
        ChunkSize = chunkSize;

        // Where each chunk starts, added up once. Otherwise every read walks the table from the
        // beginning, which turns a late chunk in a large bundle into thousands of additions.
        _starts = new long[sizes.Length + 1];
        _starts[0] = dataAt;
        for (int i = 0; i < sizes.Length; i++)
        {
            _starts[i + 1] = _starts[i] + sizes[i];
        }
    }

    /// <summary>How big the whole thing is once decompressed.</summary>
    public int Uncompressed { get; }

    /// <summary>How much each chunk decompresses to - the last one being whatever is left.</summary>
    public int ChunkSize { get; }

    /// <summary>How many chunks it is cut into.</summary>
    public int Chunks => _sizes.Length;

    /// <summary>How many bytes the compressed chunks take up.</summary>
    public long Compressed => _starts[^1] - _dataAt;

    /// <summary>
    /// Reads a bundle's header and chunk table, or null when the bytes are not a bundle.
    /// </summary>
    /// <param name="bytes">
    /// How to get a range of the file - offset and length in, bytes out. Kept, because reading a
    /// chunk later goes back through it.
    /// </param>
    public static BundleFile? Open(Func<int, int, byte[]?> bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        byte[]? head = bytes(0, HeaderBytes);
        if (head is not { Length: >= HeaderBytes })
        {
            return null;
        }

        int uncompressed = BitConverter.ToInt32(head, 0);
        int chunks = BitConverter.ToInt32(head, 36);
        int chunkSize = BitConverter.ToInt32(head, 40);

        if (uncompressed is <= 0 or > Biggest || chunkSize <= 0 || chunks is <= 0 or > MostChunks)
        {
            return null;
        }

        // The count and the size have to agree with the total, which is the cheapest way to
        // notice this is not a bundle before allocating anything from its numbers.
        if ((long)chunks * chunkSize < uncompressed || (long)(chunks - 1) * chunkSize >= uncompressed)
        {
            return null;
        }

        byte[]? table = bytes(HeaderBytes, chunks * 4);
        if (table is not { } sizesRaw || sizesRaw.Length < chunks * 4)
        {
            return null;
        }

        var sizes = new int[chunks];
        for (int i = 0; i < chunks; i++)
        {
            sizes[i] = BitConverter.ToInt32(sizesRaw, i * 4);
            if (sizes[i] <= 0)
            {
                return null;
            }
        }

        return new BundleFile(bytes, uncompressed, chunkSize, sizes, HeaderBytes + (chunks * 4));
    }

    /// <summary>Opens one that is already in memory - the index, which is small and read whole.</summary>
    public static BundleFile? Open(byte[]? raw)
        => raw is null ? null : Open((at, length) =>
            at >= 0 && length >= 0 && at + (long)length <= raw.Length ? raw[at..(at + length)] : null);

    /// <summary>What the chunk at an index decompresses to - the last one is short.</summary>
    public int SizeOfChunk(int chunk)
        => chunk == _sizes.Length - 1 ? Uncompressed - (ChunkSize * (_sizes.Length - 1)) : ChunkSize;

    /// <summary>The whole thing, decompressed.</summary>
    public byte[]? Read(Func<ReadOnlyMemory<byte>, int, byte[]?> decompress)
        => Read(0, Uncompressed, decompress);

    /// <summary>
    /// A range of it, decompressing only the chunks that range falls in.
    /// </summary>
    /// <param name="at">Where the wanted bytes start, in the decompressed content.</param>
    /// <param name="length">How many of them there are.</param>
    /// <param name="decompress">
    /// How to undo Oodle. Handed in so the chunk arithmetic - the part that is easy to get wrong,
    /// and the part that cannot be checked against a real bundle without the game - is testable
    /// on its own.
    /// </param>
    /// <returns>Null when the range is outside the bundle, or a chunk will not decompress.</returns>
    public byte[]? Read(int at, int length, Func<ReadOnlyMemory<byte>, int, byte[]?> decompress)
    {
        ArgumentNullException.ThrowIfNull(decompress);

        if (at < 0 || length < 0 || (long)at + length > Uncompressed)
        {
            return null;
        }

        if (length == 0)
        {
            return [];
        }

        var wanted = new byte[length];
        int written = 0;

        int last = (at + length - 1) / ChunkSize;
        for (int chunk = at / ChunkSize; chunk <= last; chunk++)
        {
            if (_starts[chunk] > int.MaxValue)
            {
                return null;
            }

            byte[]? packed = _bytes((int)_starts[chunk], _sizes[chunk]);
            if (packed is null || packed.Length < _sizes[chunk])
            {
                return null;
            }

            int plainSize = SizeOfChunk(chunk);
            byte[]? plain = decompress(packed, plainSize);
            if (plain is null || plain.Length < plainSize)
            {
                return null;
            }

            // Which part of this chunk was asked for: partly the ones at either end of the
            // range, all of the ones in between.
            int startsAt = chunk * ChunkSize;
            int from = Math.Max(at - startsAt, 0);
            int take = Math.Min(startsAt + plainSize, at + length) - Math.Max(startsAt, at);

            Array.Copy(plain, from, wanted, written, take);
            written += take;
        }

        return written == length ? wanted : null;
    }
}
