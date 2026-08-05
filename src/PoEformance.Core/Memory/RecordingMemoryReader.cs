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
public sealed class RecordingMemoryReader : IMemoryReader
{
    private readonly IMemoryReader _inner;
    private readonly BinaryWriter _writer;
    private readonly long _startTimestamp;
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
    /// </remarks>
    public long MaxTotalBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>True once the cap stopped further recording.</summary>
    public bool ReachedSizeLimit { get; private set; }

    public RecordingMemoryReader(IMemoryReader inner, Stream output)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(output);

        _inner = inner;
        _writer = new BinaryWriter(output);
        _startTimestamp = Environment.TickCount64;

        _writer.Write(RecordingFormat.Magic);
        _writer.Write(RecordingFormat.Version);
        _writer.Write((uint)inner.ProcessId);
        _writer.Write(inner.ModuleBase);
        _writer.Write(inner.ModuleSize);
        _writer.Write(DateTime.UtcNow.Ticks);
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

        _writer.Write(RecordingFormat.TagNote);
        _writer.Write((ushort)keyBytes.Length);
        _writer.Write(keyBytes);
        _writer.Write((ushort)valueBytes.Length);
        _writer.Write(valueBytes);
    }

    /// <summary>Stores a resolved static address as a note.</summary>
    public void NoteStatic(string name, ulong address)
        => Note(RecordingFormat.StaticNotePrefix + name, address.ToString("X", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Writes a frame boundary. Call once per reader tick.</summary>
    public void MarkFrame()
    {
        _writer.Write(RecordingFormat.TagFrame);
        _writer.Write(_frameIndex++);
        _writer.Write((uint)(Environment.TickCount64 - _startTimestamp));
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (!_inner.TryRead(address, destination))
        {
            return false;
        }

        if (destination.Length > MaxRecordedReadBytes)
        {
            SkippedLargeReads++;
            return true;
        }

        if (RecordedBytes >= MaxTotalBytes)
        {
            // Keep serving reads; just stop growing the file. The early part is the part
            // worth replaying.
            ReachedSizeLimit = true;
            return true;
        }

        if (destination.Length <= RecordingFormat.MaxReadLength)
        {
            _writer.Write(RecordingFormat.TagRead);
            _writer.Write(address);
            _writer.Write((uint)destination.Length);
            _writer.Write(destination);
            RecordedBytes += destination.Length;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Flush();
        _writer.Dispose();
        _inner.Dispose();
    }
}
