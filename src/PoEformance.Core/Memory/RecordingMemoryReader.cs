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

        if (destination.Length <= RecordingFormat.MaxReadLength)
        {
            _writer.Write(RecordingFormat.TagRead);
            _writer.Write(address);
            _writer.Write((uint)destination.Length);
            _writer.Write(destination);
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
