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
        using var reader = new BinaryReader(input);

        Span<byte> magic = stackalloc byte[RecordingFormat.Magic.Length];
        if (reader.Read(magic) != magic.Length || !magic.SequenceEqual(RecordingFormat.Magic))
        {
            throw new InvalidDataException("Not a PoEformance recording (bad magic).");
        }

        uint version = reader.ReadUInt32();
        if (version != RecordingFormat.Version)
        {
            throw new InvalidDataException($"Recording version {version} is not supported (expected {RecordingFormat.Version}).");
        }

        int processId = (int)reader.ReadUInt32();
        ulong moduleBase = reader.ReadUInt64();
        uint moduleSize = reader.ReadUInt32();
        var createdUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);

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

                    replay._blocks.Add(new Block(frame, address, bytes));
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

    /// <summary>Positions the replay at a frame. Reads then see the state as of that frame.</summary>
    public void Seek(uint frame) => _currentFrame = frame;

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return false;
        }

        // Walk newest-to-oldest so later frames win, and fill the destination from
        // possibly-overlapping blocks until every byte is covered.
        Span<bool> covered = destination.Length <= 4096 ? stackalloc bool[destination.Length] : new bool[destination.Length];
        int remaining = destination.Length;

        for (int i = _blocks.Count - 1; i >= 0 && remaining > 0; i--)
        {
            Block block = _blocks[i];
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
