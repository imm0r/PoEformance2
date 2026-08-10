using System.Buffers;
using System.IO.Compression;

namespace PoEformance.Core.Memory;

/// <summary>
/// Writes Brotli-compressed data as a chain of finished streams, so what has been written
/// can actually be read back before the file is closed.
/// </summary>
/// <remarks>
/// THE MEASUREMENT THIS IS BUILT ON, because every cheaper answer was tried and failed.
/// Writing 12 MiB and decoding what was on disk WITHOUT finishing the stream:
///
/// <code>
///                          on disk    readable
///   BrotliStream.Flush       2,741      8 MiB   (66.7%)
///   BrotliEncoder.Flush      2,741      8 MiB   (66.7%)
///   DeflateStream.Flush     92,243     12 MiB   (99.5%)
///   Brotli, segmented       50,043     12 MiB   (99.5%)
/// </code>
///
/// Neither Brotli flush makes the data readable: the encoder emits nothing until its input
/// block fills, and that block is eight megabytes. Two real recordings ended at exactly
/// 8,388,608 decoded bytes each - the same number twice, from sessions of different lengths,
/// is a block size and not a coincidence. One of them was a twelve-minute map clear that had
/// been killed rather than closed, and everything past the first block was gone.
///
/// Deflate flushes honestly but costs nearly twice the size. Finishing a Brotli stream and
/// starting the next one does the same job at half of Deflate's size, so that is what this
/// does: <see cref="Flush"/> ends the current stream and opens a new one, and each segment
/// stands on its own.
///
/// What a segment boundary costs is the compressor's history, so segment size is the trade.
/// On the entry stream of that real session (8.4 MB, 11.2 KB per second):
///
/// <code>
///   segment      size     vs one stream    at risk
///     8 KB    30.48%          1.46x          0.7 s
///    32 KB    25.37%          1.21x          2.9 s
///   128 KB    22.82%          1.09x         11.5 s
///   512 KB    21.84%          1.05x         45.9 s
///     2 MB    21.04%          1.01x        183.6 s
/// </code>
/// </remarks>
internal sealed class BrotliWriter : Stream
{
    // What .NET's CompressionLevel.Optimal maps to, kept explicit because the encoder is
    // constructed directly here. The window is the default 4 MB one; a recording's
    // repetitions are separated by far more than Deflate's 32 KB, which is why Brotli won
    // that comparison on size.
    private const int Quality = 4;
    private const int Window = 22;

    private readonly Stream _output;
    private readonly byte[] _buffer;
    private BrotliEncoder _encoder = new(Quality, Window);
    private bool _disposed;

    public BrotliWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    }

    /// <summary>Bytes written since the last segment boundary - what a kill would cost.</summary>
    public long SinceFlush { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SinceFlush += buffer.Length;

        while (!buffer.IsEmpty)
        {
            OperationStatus status = _encoder.Compress(
                buffer, _buffer, out int consumed, out int written, isFinalBlock: false);

            if (written > 0)
            {
                _output.Write(_buffer, 0, written);
            }

            buffer = buffer[consumed..];

            if (status == OperationStatus.InvalidData)
            {
                throw new IOException("The Brotli encoder rejected the data.");
            }

            if (consumed == 0 && written == 0)
            {
                // Neither taking input nor producing output: nothing further will happen,
                // and looping would spin. Cannot be reached with a 64 KB destination.
                break;
            }
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];
        Write(one);
    }

    /// <summary>Ends the current segment, so everything written so far can be read back.</summary>
    public override void Flush()
    {
        if (_disposed || SinceFlush == 0)
        {
            return;
        }

        Finish();
        _encoder = new BrotliEncoder(Quality, Window);
        SinceFlush = 0;
        _output.Flush();
    }

    /// <summary>Closes the current stream, emitting whatever the encoder still holds.</summary>
    private void Finish()
    {
        while (true)
        {
            OperationStatus status = _encoder.Compress(
                ReadOnlySpan<byte>.Empty, _buffer, out _, out int written, isFinalBlock: true);

            if (written > 0)
            {
                _output.Write(_buffer, 0, written);
            }

            if (status != OperationStatus.DestinationTooSmall)
            {
                break;
            }
        }

        _encoder.Dispose();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Finish();
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            _output.Dispose();
        }

        base.Dispose(disposing);
    }
}
