using System.Buffers.Binary;

namespace PoEformance.Core.Memory;

/// <summary>
/// Wraps another reader and writes every successful read to a recording file.
/// </summary>
/// <remarks>
/// Drop-in: give any consumer a RecordingMemoryReader instead of the live one and the
/// session becomes reproducible. Failed reads are not recorded - a replay then fails
/// the same way, via "no data for this address".
///
/// The frame marker exists so a replay can be scrubbed in time: call
/// <see cref="MarkFrame"/> once per reader tick and the replay can answer "what did
/// this address contain around frame N".
/// </remarks>
public sealed class RecordingMemoryReader : IMemoryReader, IMemoryRegions
{
    private readonly IMemoryReader _inner;
    private readonly BinaryWriter _writer;
    private readonly BrotliWriter _body;
    private readonly CountingStream _file;
    private readonly long _startTimestamp;
    private long _lastFlush;

    // Reads themselves are thread-safe - ReadProcessMemory fills the CALLER's buffer - but
    // the file, the counters and the frame index are shared state. Two threads now read
    // through the same reader (the overlay's feed and the config window), so the writing
    // half is serialised. Only the write is inside the lock; the read is not, so the
    // expensive part still runs concurrently.
    private readonly Lock _writeLock = new();
    private uint _frameIndex;
    private bool _disposed;

    /// <summary>
    /// Reads bigger than this are served normally but left out of the file. Defaults to
    /// <see cref="RecordingFormat.DefaultMaxRecordedReadBytes"/>, which excludes the module
    /// image copy - the difference between a 77 MB recording and a few hundred KB.
    /// </summary>
    public int MaxRecordedReadBytes { get; set; } = RecordingFormat.DefaultMaxRecordedReadBytes;

    /// <summary>Bytes actually written to the recording, for reporting.</summary>
    public long RecordedBytes { get; private set; }

    /// <summary>Reads skipped because they exceeded <see cref="MaxRecordedReadBytes"/>.</summary>
    public int SkippedLargeReads { get; private set; }

    /// <summary>
    /// Stop recording once the file reaches this size. Reading continues unaffected.
    /// </summary>
    /// <remarks>
    /// A recording is only useful if it can be SHARED, and without a cap the size depends
    /// on how long the session ran: a one-shot diagnostic produces a couple of hundred
    /// kilobytes, while the same flag next to a live overlay grows without limit at thirty
    /// reads a second. Capping turns an unusable multi-hundred-megabyte file into a bounded
    /// one that still contains the beginning - which is the part that carries the startup
    /// chain and the diagnostics worth replaying.
    ///
    /// Measured against the BYTES ON DISK, which is not what it used to be measured against
    /// and is why a 16 MB cap once produced a 33 MB file: the old count was of payload only,
    /// and the thirteen bytes of tag, address and length in front of each eight-byte read
    /// more than doubled it. Compression widens that gap further, in the useful direction.
    /// </remarks>
    public long MaxTotalBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>True once the cap stopped further recording.</summary>
    public bool ReachedSizeLimit { get; private set; }

    /// <summary>Bytes written to the file so far, compression included.</summary>
    public long FileBytes => _file.Written;

    public RecordingMemoryReader(IMemoryReader inner, Stream output)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(output);

        _inner = inner;
        _file = new CountingStream(output);
        _startTimestamp = Environment.TickCount64;

        // The header goes down uncompressed, so the file is still recognisable by its first
        // eight bytes and the version can be read before deciding how to read the rest.
        var header = new BinaryWriter(_file, System.Text.Encoding.UTF8, leaveOpen: true);
        header.Write(RecordingFormat.Magic);
        header.Write(RecordingFormat.Version);
        header.Write((uint)inner.ProcessId);
        header.Write(inner.ModuleBase);
        header.Write(inner.ModuleSize);
        header.Write(DateTime.UtcNow.Ticks);
        header.Flush();

        // Brotli, at what CompressionLevel.Optimal means: measured on a real 25 MB session,
        // it compressed the entry stream to two thirds of what Fastest managed and took
        // seven milliseconds against four, for a whole session. The recorder writes from the
        // reader thread at thirty ticks a second, so that cost is not detectable.
        //
        // Through BrotliWriter rather than BrotliStream, and that is not a detail: see the
        // measurements there. BrotliStream's Flush leaves the data unreadable until an
        // eight-megabyte block fills, which is how two recordings came to end at exactly
        // 8,388,608 bytes.
        _body = new BrotliWriter(_file);
        _writer = new BinaryWriter(_body);
    }

    public bool IsAttached => _inner.IsAttached;

    public int ProcessId => _inner.ProcessId;

    public ulong ModuleBase => _inner.ModuleBase;

    public uint ModuleSize => _inner.ModuleSize;

    /// <summary>
    /// Stores a derived fact in the recording, so a replay can use it instead of
    /// recomputing it from data that was too large to record.
    /// </summary>
    public void Note(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (keyBytes.Length > ushort.MaxValue || valueBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Note key/value too long.", nameof(key));
        }

        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Write(RecordingFormat.TagNote);
            _writer.Write((ushort)keyBytes.Length);
            _writer.Write(keyBytes);
            _writer.Write((ushort)valueBytes.Length);
            _writer.Write(valueBytes);
        }
    }

    /// <summary>Stores a resolved static address as a note.</summary>
    public void NoteStatic(string name, ulong address)
        => Note(RecordingFormat.StaticNotePrefix + name, address.ToString("X", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Writes a frame boundary. Call once per reader tick.</summary>
    /// <remarks>
    /// Also where the compressor is closed off into a segment. A recording that ends because
    /// the game or the tool was KILLED - which is the interesting way for one to end - is
    /// readable up to the last segment boundary, so this is the difference between losing a
    /// few seconds and losing everything since the file was opened. On a frame boundary
    /// rather than mid-frame, so whole ticks survive.
    ///
    /// By SIZE first and time second. Size, because what a kill costs is content rather than
    /// wall-clock, and a segment boundary costs the compressor its history: measured on a
    /// real session, 128 KB segments cost 9% in size and put at most 11 seconds at risk,
    /// where 8 KB segments cost 46% for less than a second. Time as well, because a quiet
    /// session writes almost nothing and would otherwise hold its tail open indefinitely -
    /// and what it is holding is small, so closing it early costs little.
    /// </remarks>
    public void MarkFrame()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Write(RecordingFormat.TagFrame);
            _writer.Write(_frameIndex++);
            _writer.Write((uint)(Environment.TickCount64 - _startTimestamp));

            long now = Environment.TickCount64;
            if (_body.SinceFlush >= FlushEveryBytes || now - _lastFlush >= FlushEveryMs)
            {
                _lastFlush = now;
                _writer.Flush();
                _file.Flush();
            }
        }
    }

    /// <summary>How much of the entry stream may be at risk before a segment is closed.</summary>
    public long FlushEveryBytes { get; set; } = 128 * 1024;

    /// <summary>How long a segment may stay open when almost nothing is being written.</summary>
    public int FlushEveryMs { get; set; } = 5000;

    /// <summary>
    /// Whether this read says anything the recording does not already hold.
    /// </summary>
    /// <remarks>
    /// THE REASON A SESSION ONLY EVER LASTED HALF A SECOND. One frame of a real recording
    /// held 1.2 million reads and 32 MB - 657,000 of them eight bytes wide - and the file
    /// packed down to under a two-hundredth of that with an ordinary archiver. Almost all of
    /// it was the same addresses carrying the same bytes, written again and again, and the
    /// size limit was reached before the first monster died.
    ///
    /// A repeat is not merely wasteful, it is INERT: the replay serves "the newest data at
    /// or before the current frame", so an unchanged read that was never written resolves to
    /// the identical bytes from the frame that did write them. Dropping it cannot change what
    /// a replay sees, which is what makes this a saving rather than a trade.
    ///
    /// The comparison is against the last bytes WRITTEN for that exact address and length.
    /// Length is part of the key because reads of different widths at one address are
    /// different facts, and a wide read that matched the prefix of a narrow one would
    /// otherwise suppress the rest of itself.
    ///
    /// A first sighting always writes - "never recorded" and "unchanged" have to be told
    /// apart, or the recording would start with nothing in it.
    /// </remarks>
    private bool Changed(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (!_lastWritten.TryGetValue((address, bytes.Length), out byte[]? previous))
        {
            if (_lastWritten.Count >= MaxTrackedReads)
            {
                // Start over rather than grow without bound. Entity addresses churn as areas
                // change, so a long session keeps meeting addresses it will never see again,
                // and each one would be remembered forever. Forgetting costs one full rewrite
                // of whatever is still being read - the file gains a keyframe - and cannot
                // cost correctness, because a first sighting always writes.
                _lastWritten.Clear();
                ForgottenReadHistories++;
            }

            _lastWritten[(address, bytes.Length)] = bytes.ToArray();
            return true;
        }

        if (bytes.SequenceEqual(previous))
        {
            SkippedUnchangedReads++;
            return false;
        }

        bytes.CopyTo(previous);
        return true;
    }

    /// <summary>Reads that were served but not written, because nothing about them moved.</summary>
    public long SkippedUnchangedReads { get; private set; }

    /// <summary>
    /// How many distinct (address, length) pairs to remember before starting over. A real
    /// session touched ten thousand of them, so this is generous; it exists so that a long
    /// one, moving between areas, cannot grow this without bound.
    /// </summary>
    public int MaxTrackedReads { get; set; } = 250_000;

    /// <summary>Times the comparison history was cleared to stay within its bound.</summary>
    public int ForgottenReadHistories { get; private set; }

    // The last bytes written for each (address, length) - the memory this trades for the
    // disk it no longer spends.
    private readonly Dictionary<(ulong Address, int Length), byte[]> _lastWritten = [];

    /// <summary>
    /// The wrapped reader's regions, or none if it cannot enumerate them.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, --record SILENTLY DISABLES --heapscan. The scan asks whether its reader
    /// can enumerate regions, and a wrapper that does not forward the question answers no for
    /// a live reader that would have said yes - so the scan skipped itself, the report came
    /// back with every other section intact, and the missing one read as "found nothing".
    /// A recorded heap scan is exactly the combination worth having, since the hits are only
    /// worth anything if the session they came from can be replayed.
    ///
    /// Not recorded, and that is not a gap: region enumeration is a query about the process
    /// rather than a read of its memory, and the scan's own reads pass through TryRead like
    /// any other. They land above MaxRecordedReadBytes and are skipped there, which is what
    /// keeps a scanned session's recording small.
    /// </remarks>
    public IEnumerable<MemoryRegion> Regions() =>
        _inner is IMemoryRegions regions ? regions.Regions() : [];

    public bool TryRead(ulong address, Span<byte> destination)
    {
        // Outside the lock: this is the expensive part, and it touches nothing shared.
        if (!_inner.TryRead(address, destination))
        {
            return false;
        }

        lock (_writeLock)
        {
            if (_disposed)
            {
                // The file was closed while this read was in flight. Serving the read is
                // still correct; there is just nowhere left to record it.
                return true;
            }

            if (destination.Length > MaxRecordedReadBytes)
            {
                SkippedLargeReads++;
                return true;
            }

            if (_file.Written >= MaxTotalBytes)
            {
                // Keep serving reads; just stop growing the file. The early part is the part
                // worth replaying.
                ReachedSizeLimit = true;
                return true;
            }

            if (destination.Length <= RecordingFormat.MaxReadLength && Changed(address, destination))
            {
                _writer.Write(RecordingFormat.TagRead);
                _writer.Write(address);
                _writer.Write((uint)destination.Length);
                _writer.Write(destination);
                RecordedBytes += destination.Length;
            }
        }

        return true;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();

            // Cascades: writer -> compressor (which finishes its stream) -> counter -> file.
            _writer.Dispose();
        }

        _inner.Dispose();
    }

    /// <summary>Passes writes through and tallies them, so the cap can mean the FILE size.</summary>
    /// <remarks>
    /// Once the entries are compressed there is no arithmetic that turns what was written
    /// into what landed on disk, and asking the stream would only work for a seekable one.
    /// Counting on the way past works for any stream and costs an addition.
    /// </remarks>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            Written++;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
