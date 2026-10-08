namespace PoEformance.Core.Memory;

/// <summary>
/// Passes every read through to another reader, and records a stretch of them when asked - a recording started mid-session rather than at launch.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. <c>--record</c> decides at launch, and a session is only recorded if somebody
/// knew beforehand that this one would matter. The capture key asks the other way round: the
/// player is standing somewhere worth keeping NOW, and a few seconds of what the overlay reads
/// there is the memory half of the capture. So the live reader is wrapped once, at start-up, and
/// a recording can be switched on for a count of frames at any moment after.
///
/// WHAT IT COSTS WHILE OFF: one volatile read per read. The overlay makes thousands a frame, and
/// each is a kernel transition beside which a field load does not register.
///
/// A RECORDING OF THE OVERLAY'S OWN READS, which is the rule every recording follows: what was
/// read while it ran is in it, and nothing else. Whatever the tool reads once per area - the
/// terrain, the loaded-file list - was read before the key was pressed and is not in it.
///
/// THE STATICS GO IN FIRST, as notes, for the reason <c>--record</c> writes them first: a replay
/// cannot find them without the module image, which is far too large to record, so a recording
/// without them cannot be replayed at all.
///
/// The wrapped reader is never disposed by a recording ending - the recorder disposes what it
/// wraps, so it is handed a view of the reader that ignores that - and is disposed with this.
/// </remarks>
public sealed class TapMemoryReader : IMemoryReader, IMemoryRegions
{
    private readonly IMemoryReader _inner;
    private readonly Kept _kept;
    private Tap? _tap;

    /// <param name="inner">The reader every read goes to.</param>
    public TapMemoryReader(IMemoryReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _kept = new Kept(inner);
    }

    /// <summary>Whether a recording is running.</summary>
    public bool Recording => Volatile.Read(ref _tap) is not null;

    public bool IsAttached => _inner.IsAttached;

    public int ProcessId => _inner.ProcessId;

    public ulong ModuleBase => _inner.ModuleBase;

    public uint ModuleSize => _inner.ModuleSize;

    /// <summary>
    /// Records every read from now until the recording's end, then closes the file. Null while another recording runs.
    /// </summary>
    /// <param name="output">Where the recording goes - closed when it ends.</param>
    /// <param name="notes">Written first: the resolved statics, and whatever else a replay needs to know.</param>
    /// <param name="how">How long it runs, and how large a read and a file it keeps.</param>
    /// <returns>Completes with the file's size once it is closed.</returns>
    public Task<long>? Start(Stream output, IEnumerable<KeyValuePair<string, string>> notes, TapRecording how)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(how);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(how.Frames);
        if (Recording)
        {
            output.Dispose();
            return null;
        }

        var recorder = new RecordingMemoryReader(_kept, output)
        {
            MaxRecordedReadBytes = how.MaxReadBytes,
            MaxTotalBytes = how.MaxTotalBytes,
        };
        foreach ((string key, string value) in notes)
        {
            recorder.Note(key, value);
        }

        var tap = new Tap(recorder, how);
        if (Interlocked.CompareExchange(ref _tap, tap, null) is not null)
        {
            recorder.Dispose();
            return null;
        }

        return tap.Done.Task;
    }

    /// <summary>
    /// One tick of the reader's loop: a frame boundary in the recording, and its end once its frames are spent and what it waits for is done. Call once per tick, from one thread.
    /// </summary>
    public void MarkFrame()
    {
        Tap? tap = Volatile.Read(ref _tap);
        if (tap is null)
        {
            return;
        }

        int ticks = ++tap.Ticks;
        bool spent = ticks > tap.How.Frames && (tap.How.Until is null || tap.How.Until.IsCompleted);
        if (!spent && ticks <= Math.Max(tap.How.Frames, tap.How.MostFrames))
        {
            tap.Recorder.MarkFrame();
            return;
        }

        Stop(tap);
    }

    /// <summary>Ends a running recording early, keeping what it has.</summary>
    public void Stop()
    {
        if (Volatile.Read(ref _tap) is { } tap)
        {
            Stop(tap);
        }
    }

    private void Stop(Tap tap)
    {
        if (Interlocked.CompareExchange(ref _tap, null, tap) != tap)
        {
            return;
        }

        // A read still in flight on another thread finishes against the recorder; it serves the
        // read and writes nothing once the file is closed, which is the recorder's own rule.
        try
        {
            tap.Recorder.Dispose();
            tap.Done.TrySetResult(tap.Recorder.FileBytes);
        }
        catch (IOException failed)
        {
            tap.Done.TrySetException(failed);
        }
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        Tap? tap = Volatile.Read(ref _tap);
        return tap is null ? _inner.TryRead(address, destination) : tap.Recorder.TryRead(address, destination);
    }

    /// <summary>The wrapped reader's regions - see RecordingMemoryReader.Regions for why a wrapper must pass the question on.</summary>
    public IEnumerable<MemoryRegion> Regions() => _inner is IMemoryRegions regions ? regions.Regions() : [];

    public void Dispose()
    {
        Stop();
        _inner.Dispose();
    }

    private sealed class Tap(RecordingMemoryReader recorder, TapRecording how)
    {
        public RecordingMemoryReader Recorder { get; } = recorder;

        public TapRecording How { get; } = how;

        public int Ticks { get; set; }

        public TaskCompletionSource<long> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The reader, with its Dispose taken away - what a recording that ends is allowed to close.</summary>
    private sealed class Kept(IMemoryReader inner) : IMemoryReader, IMemoryRegions
    {
        public bool IsAttached => inner.IsAttached;

        public int ProcessId => inner.ProcessId;

        public ulong ModuleBase => inner.ModuleBase;

        public uint ModuleSize => inner.ModuleSize;

        public bool TryRead(ulong address, Span<byte> destination) => inner.TryRead(address, destination);

        public IEnumerable<MemoryRegion> Regions() => inner is IMemoryRegions regions ? regions.Regions() : [];

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// How long a <see cref="TapMemoryReader"/> recording runs, and how much of what it sees it keeps.
/// </summary>
/// <param name="Frames">Ticks of the reader it runs for at least.</param>
/// <remarks>
/// THE LIMITS ARE WIDER THAN --record'S on purpose. A session recording leaves out any read over
/// 64 KB, which is what keeps the module image out - and also the terrain, whose walkable grid and
/// tile array are each one read of several hundred kilobytes. A capture is a few seconds long and
/// exists to hold exactly those, so its reads are kept up to the format's own largest.
/// </remarks>
public sealed record TapRecording(int Frames)
{
    /// <summary>Something else that must finish inside the recording - it runs on until this is done, up to <see cref="MostFrames"/>.</summary>
    public Task? Until { get; init; }

    /// <summary>The longest it runs whatever it waits for: a minute at the reader's thirty ticks a second.</summary>
    public int MostFrames { get; init; } = 30 * 60;

    /// <summary>The largest single read kept.</summary>
    public int MaxReadBytes { get; init; } = RecordingFormat.DefaultMaxRecordedReadBytes;

    /// <summary>The size the file stops growing at.</summary>
    public long MaxTotalBytes { get; init; } = 16 * 1024 * 1024;
}
