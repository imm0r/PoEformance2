using System.Buffers;
using System.IO.Compression;

namespace PoEformance.Core.Memory;

/// <summary>
/// Replays a recorded session as if it were the live process.
/// </summary>
/// <remarks>
/// This is what lets decoders, features and tests run without the game. The replay
/// keeps, per address, the bytes each frame wrote there; <see cref="Seek"/> moves the
/// current position and reads then serve the newest data at or before that frame.
///
/// Reads may span multiple recorded blocks (the interval map handles overlap), but a
/// read into a region the recording never touched fails - exactly like a bad pointer
/// on the live reader. That symmetry is intentional: code that works against a replay
/// works against the game.
/// </remarks>
public sealed class ReplayMemoryReader : IMemoryReader
{
    /// <summary>One recorded read: which frame it happened in and the captured bytes.</summary>
    private readonly record struct Block(uint Frame, ulong Address, byte[] Bytes);

    /// <summary>All recorded blocks in file order (which is also frame order).</summary>
    private readonly List<Block> _blocks = [];

    /// <summary>Which blocks touch which page of memory, so a read need not scan them all.</summary>
    /// <remarks>
    /// Without this a read walks every block backwards looking for the newest coverage, which
    /// is fine for a recording of a few thousand reads and hopeless for one of a few hundred
    /// thousand: measured at 0.66 ms per read on a two-minute session, against the thousands
    /// of reads a single world scan performs. Recordings became long enough to be worth
    /// replaying and immediately became too long to replay.
    ///
    /// A block is registered under every page it covers, so scanning the pages a READ covers
    /// finds every block that could answer any byte of it - the newest-wins rule below then
    /// works exactly as it did over the whole list. Pages rather than addresses because reads
    /// overlap: a field read four bytes into a struct is answered by the struct's own read,
    /// which starts somewhere else entirely, and an index keyed on the start address would
    /// quietly serve the older of the two.
    /// </remarks>
    private readonly Dictionary<ulong, List<int>> _byPage = [];

    private const int PageShift = 12;

    /// <summary>Frame index -> elapsed ms since session start, for time scrubbing UIs.</summary>
    private readonly List<uint> _frameTimes = [];

    /// <summary>Derived facts stored alongside the reads (see RecordingFormat notes).</summary>
    private readonly Dictionary<string, string> _notes = [];

    private uint _currentFrame = uint.MaxValue;

    private ReplayMemoryReader(int processId, ulong moduleBase, uint moduleSize, DateTime createdUtc)
    {
        ProcessId = processId;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        CreatedUtc = createdUtc;
    }

    public bool IsAttached => true;

    public int ProcessId { get; }

    public ulong ModuleBase { get; }

    public uint ModuleSize { get; }

    /// <summary>When the recording was captured.</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>Number of frame markers in the recording.</summary>
    public int FrameCount => _frameTimes.Count;

    /// <summary>Derived key/value facts stored in the recording.</summary>
    public IReadOnlyDictionary<string, string> Notes => _notes;

    /// <summary>
    /// Static addresses that were resolved when the session was recorded. A replay uses
    /// these instead of pattern-scanning, because the module image they came from is
    /// deliberately not recorded (it would dwarf the useful data).
    /// </summary>
    public IReadOnlyDictionary<string, ulong> ResolvedStatics
    {
        get
        {
            var result = new Dictionary<string, ulong>();
            foreach ((string key, string value) in _notes)
            {
                if (key.StartsWith(RecordingFormat.StaticNotePrefix, StringComparison.Ordinal)
                    && ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong address))
                {
                    result[key[RecordingFormat.StaticNotePrefix.Length..]] = address;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Frame the replay is positioned at. Reads serve the newest block at or before
    /// this frame. Defaults to the last frame.
    /// </summary>
    public uint CurrentFrame => _currentFrame;

    /// <summary>Loads a recording produced by <see cref="RecordingMemoryReader"/>.</summary>
    public static ReplayMemoryReader Load(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var header = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);

        Span<byte> magic = stackalloc byte[RecordingFormat.Magic.Length];
        if (header.Read(magic) != magic.Length || !magic.SequenceEqual(RecordingFormat.Magic))
        {
            throw new InvalidDataException("Not a PoEformance recording (bad magic).");
        }

        uint version = header.ReadUInt32();
        if (version is not (RecordingFormat.Version
            or RecordingFormat.SingleStreamVersion
            or RecordingFormat.UncompressedVersion))
        {
            throw new InvalidDataException($"Recording version {version} is not supported (expected {RecordingFormat.Version}).");
        }

        int processId = (int)header.ReadUInt32();
        ulong moduleBase = header.ReadUInt64();
        uint moduleSize = header.ReadUInt32();
        var createdUtc = new DateTime(header.ReadInt64(), DateTimeKind.Utc);

        using var reader = new BinaryReader(
            version == RecordingFormat.UncompressedVersion ? input : Decompress(input));

        var replay = new ReplayMemoryReader(processId, moduleBase, moduleSize, createdUtc);
        uint frame = 0;

        // Read entries until the stream ends. A truncated final entry (crash mid-write)
        // is silently dropped - everything before it is still usable.
        try
        {
            while (true)
            {
                int tag = reader.BaseStream.ReadByte();
                if (tag < 0)
                {
                    break;
                }

                if (tag == RecordingFormat.TagFrame)
                {
                    frame = reader.ReadUInt32();
                    replay._frameTimes.Add(reader.ReadUInt32());
                }
                else if (tag == RecordingFormat.TagRead)
                {
                    ulong address = reader.ReadUInt64();
                    uint length = reader.ReadUInt32();
                    if (length > RecordingFormat.MaxReadLength)
                    {
                        throw new InvalidDataException($"Corrupt recording: read of {length} bytes at 0x{address:X}.");
                    }

                    byte[] bytes = reader.ReadBytes((int)length);
                    if (bytes.Length != length)
                    {
                        break; // truncated tail
                    }

                    replay.Add(new Block(frame, address, bytes));
                }
                else if (tag == RecordingFormat.TagNote)
                {
                    int keyLength = reader.ReadUInt16();
                    byte[] keyBytes = reader.ReadBytes(keyLength);
                    int valueLength = reader.ReadUInt16();
                    byte[] valueBytes = reader.ReadBytes(valueLength);
                    if (keyBytes.Length != keyLength || valueBytes.Length != valueLength)
                    {
                        break; // truncated tail
                    }

                    replay._notes[System.Text.Encoding.UTF8.GetString(keyBytes)] =
                        System.Text.Encoding.UTF8.GetString(valueBytes);
                }
                else
                {
                    throw new InvalidDataException($"Corrupt recording: unknown entry tag {tag}.");
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated tail - keep what we parsed.
        }

        replay._currentFrame = frame;
        return replay;
    }

    /// <summary>
    /// Unpacks the entry stream of a version-3 recording, keeping whatever a killed session
    /// managed to write.
    /// </summary>
    /// <remarks>
    /// Uses the raw <see cref="BrotliDecoder"/> rather than a <see cref="BrotliStream"/>
    /// because a truncated tail has to be a RESULT here, not an exception. The stream
    /// wrapper throws when the data runs out mid-block, and it throws from inside the same
    /// call that produced the good bytes, so the healthy part of a recording would be lost
    /// along with the torn end of it. The decoder reports <c>NeedMoreData</c> instead and
    /// leaves everything decoded so far in hand.
    ///
    /// The body is a CHAIN of finished Brotli streams rather than one long one - see
    /// BrotliWriter for why nothing else made a half-written recording readable. So the
    /// decoding runs one stream at a time and starts a fresh decoder wherever the last one
    /// said it was done. A version-3 recording, written as a single stream, is the case
    /// where that loop happens to run once.
    /// </remarks>
    private static MemoryStream Decompress(Stream input)
    {
        byte[] compressed;
        using (input)
        {
            var raw = new MemoryStream();
            input.CopyTo(raw);
            compressed = raw.ToArray();
        }

        var body = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            ReadOnlySpan<byte> source = compressed;
            while (!source.IsEmpty)
            {
                using var decoder = new BrotliDecoder();
                bool moved = false;

                while (true)
                {
                    OperationStatus status = decoder.Decompress(source, chunk, out int consumed, out int written);
                    body.Write(chunk, 0, written);
                    source = source[consumed..];
                    moved |= consumed > 0 || written > 0;

                    if (status == OperationStatus.DestinationTooSmall)
                    {
                        continue; // more of this segment to come
                    }

                    // Done: the segment ended, and another may follow. NeedMoreData: the
                    // recording was cut mid-segment, so this is as far as it goes.
                    // InvalidData: the same, said less politely.
                    break;
                }

                // A round that took nothing and produced nothing would spin forever on a
                // tail the decoder cannot make sense of.
                if (!moved)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        body.Position = 0;
        return body;
    }

    /// <summary>Keeps a block and files it under every page it covers.</summary>
    private void Add(Block block)
    {
        int index = _blocks.Count;
        _blocks.Add(block);

        ulong first = block.Address >> PageShift;
        ulong last = (block.Address + (ulong)block.Bytes.Length - 1) >> PageShift;
        for (ulong page = first; page <= last; page++)
        {
            if (!_byPage.TryGetValue(page, out List<int>? bucket))
            {
                _byPage[page] = bucket = [];
            }

            bucket.Add(index);
        }
    }

    /// <summary>Positions the replay at a frame. Reads then see the state as of that frame.</summary>
    public void Seek(uint frame) => _currentFrame = frame;

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return false;
        }

        ulong firstPage = address >> PageShift;
        ulong lastPage = (address + (ulong)(destination.Length - 1)) >> PageShift;

        List<int>? candidates;
        if (firstPage == lastPage)
        {
            _byPage.TryGetValue(firstPage, out candidates);
        }
        else
        {
            // A read across a page boundary is rare enough not to be worth a merge: gather
            // the buckets and sort, which restores the file order the walk below relies on.
            // A block covering both pages lands in the list twice; the second visit finds
            // every byte already covered and does nothing.
            var spanning = new List<int>();
            for (ulong page = firstPage; page <= lastPage; page++)
            {
                if (_byPage.TryGetValue(page, out List<int>? bucket))
                {
                    spanning.AddRange(bucket);
                }
            }

            spanning.Sort();
            candidates = spanning;
        }

        if (candidates is null)
        {
            return false;
        }

        // Walk newest-to-oldest so later frames win, and fill the destination from
        // possibly-overlapping blocks until every byte is covered.
        Span<bool> covered = destination.Length <= 4096 ? stackalloc bool[destination.Length] : new bool[destination.Length];
        int remaining = destination.Length;

        for (int i = candidates.Count - 1; i >= 0 && remaining > 0; i--)
        {
            Block block = _blocks[candidates[i]];
            if (block.Frame > _currentFrame)
            {
                continue;
            }

            ulong blockEnd = block.Address + (ulong)block.Bytes.Length;
            ulong readEnd = address + (ulong)destination.Length;
            if (block.Address >= readEnd || blockEnd <= address)
            {
                continue; // no overlap
            }

            ulong overlapStart = Math.Max(block.Address, address);
            ulong overlapEnd = Math.Min(blockEnd, readEnd);
            int sourceOffset = (int)(overlapStart - block.Address);
            int destOffset = (int)(overlapStart - address);
            int overlapLength = (int)(overlapEnd - overlapStart);

            for (int j = 0; j < overlapLength; j++)
            {
                if (!covered[destOffset + j])
                {
                    destination[destOffset + j] = block.Bytes[sourceOffset + j];
                    covered[destOffset + j] = true;
                    remaining--;
                }
            }
        }

        return remaining == 0;
    }

    public void Dispose()
    {
        // Nothing to release - the recording is fully loaded into memory.
    }
}
