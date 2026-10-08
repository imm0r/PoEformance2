using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// A recording switched on mid-session: what the capture key keeps of memory.
/// </summary>
public class TapMemoryReaderTests
{
    private static readonly KeyValuePair<string, string>[] Statics =
        [new(RecordingFormat.StaticNotePrefix + "GameStates", "1234")];

    /// <summary>Only the reads made while it ran are in it, it ends on its own after its frames, and it replays with its statics.</summary>
    [Fact]
    public async Task ARECORDINGHoldsTheReadsOfItsFramesAndReplays()
    {
        var live = new FakeMemoryReader().Place(0x1000, 7UL).Place(0x2000, 8UL).Place(0x3000, 9UL);
        using var tap = new TapMemoryReader(live);
        Assert.Equal(7UL, tap.Read<ulong>(0x1000));

        var file = new MemoryStream();
        Task<long> done = Assert.IsType<Task<long>>(tap.Start(file, Statics, frames: 2));
        Assert.True(tap.Recording);
        Assert.Null(tap.Start(new MemoryStream(), Statics, frames: 1));

        tap.MarkFrame();
        Assert.Equal(8UL, tap.Read<ulong>(0x2000));
        tap.MarkFrame();
        tap.MarkFrame();
        Assert.False(tap.Recording);
        Assert.Equal(9UL, tap.Read<ulong>(0x3000));

        long bytes = await done;
        byte[] recorded = file.ToArray();
        Assert.Equal(recorded.Length, bytes);

        using var replay = ReplayMemoryReader.Load(new MemoryStream(recorded));
        Assert.Equal(0x1234UL, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(8UL, replay.Read<ulong>(0x2000));
        Assert.False(replay.TryRead(0x1000, out ulong _));
        Assert.False(replay.TryRead(0x3000, out ulong _));
    }

    /// <summary>A recording ending closes its file, never the reader it records - the session goes on reading.</summary>
    [Fact]
    public void ARECORDINGEndingLeavesTheReaderOpen()
    {
        var live = new CountedReader();
        using var tap = new TapMemoryReader(live);
        _ = tap.Start(new MemoryStream(), Statics, frames: 1);
        tap.Stop();
        Assert.False(tap.Recording);
        Assert.Equal(0, live.Disposed);
        Assert.NotNull(tap.Start(new MemoryStream(), Statics, frames: 1));

        tap.Dispose();
        Assert.Equal(1, live.Disposed);
    }

    private sealed class CountedReader : IMemoryReader
    {
        public int Disposed { get; private set; }

        public bool IsAttached => true;

        public int ProcessId => 1;

        public ulong ModuleBase => 0;

        public uint ModuleSize => 0;

        public bool TryRead(ulong address, Span<byte> destination) => false;

        public void Dispose() => Disposed++;
    }
}
