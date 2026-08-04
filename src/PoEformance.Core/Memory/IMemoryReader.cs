namespace PoEformance.Core.Memory;

/// <summary>
/// Reads bytes out of the attached game process.
/// </summary>
/// <remarks>
/// This is the single most important abstraction in the project. Everything that
/// touches game memory goes through it, which buys three things:
///
///   1. Record and replay. A session can be captured to a file and replayed later,
///      so features and struct decoders can be developed and tested without the game
///      running at all (see <see cref="RecordingMemoryReader"/> / <see cref="ReplayMemoryReader"/>).
///   2. Tests. A synthetic reader backed by a byte array makes decoders unit-testable.
///   3. One place to add batching, caching or instrumentation later.
///
/// Implementations must be safe to call from a single thread only. The reader thread
/// owns its instance; nothing else touches it.
/// </remarks>
public interface IMemoryReader : IDisposable
{
    /// <summary>True when the reader is connected to a live or replayed process.</summary>
    bool IsAttached { get; }

    /// <summary>Process id of the target, or 0 when not attached.</summary>
    int ProcessId { get; }

    /// <summary>Base address of the target's main module, or 0 when not attached.</summary>
    ulong ModuleBase { get; }

    /// <summary>Size in bytes of the target's main module, or 0 when not attached.</summary>
    uint ModuleSize { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with the bytes at <paramref name="address"/>.
    /// Returns false if the read failed for any reason - a bad pointer, a dead process,
    /// or a replay that has no data for this address. Callers must always check.
    /// </summary>
    /// <remarks>
    /// The caller owns the buffer, so a read never allocates. Read one large block and
    /// pick fields out of it locally rather than issuing many small reads: each call is
    /// a kernel transition and the syscall count, not the byte count, dominates the cost.
    /// </remarks>
    bool TryRead(ulong address, Span<byte> destination);
}
