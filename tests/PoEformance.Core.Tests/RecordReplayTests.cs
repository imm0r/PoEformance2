using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

public class RecordReplayTests
{
    [Fact]
    public void RecordedSession_ReplaysIdentically()
    {
        var live = new FakeMemoryReader()
            .Place(0x1000, 0x1111111111111111UL)
            .PlaceUtf16(0x2000, "Metadata/Monsters/Bear");

        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file))
        {
            recorder.MarkFrame();
            Assert.Equal(0x1111111111111111UL, recorder.Read<ulong>(0x1000));
            Assert.Equal("Metadata/Monsters/Bear", recorder.ReadUtf16(0x2000));
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        Assert.Equal(4242, replay.ProcessId);
        Assert.Equal(0x1111111111111111UL, replay.Read<ulong>(0x1000));
        Assert.Equal("Metadata/Monsters/Bear", replay.ReadUtf16(0x2000));
    }

    [Fact]
    public void Replay_FailsReadsTheRecordingNeverMade()
    {
        var live = new FakeMemoryReader().Place(0x1000, 42UL);
        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file))
        {
            recorder.MarkFrame();
            recorder.Read<ulong>(0x1000);
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        Assert.False(replay.TryRead(0x9999, out ulong _)); // never recorded
    }

    [Fact]
    public void Seek_ServesTheValueAsOfThatFrame()
    {
        // Same address recorded with different values in frame 0 and frame 1 - the
        // basis for time scrubbing ("what did this hold before I died?").
        var live = new FakeMemoryReader().Place(0x1000, 100);
        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file))
        {
            recorder.MarkFrame();
            recorder.Read<int>(0x1000);

            live.Place(0x1000, 200); // game state changed
            recorder.MarkFrame();
            recorder.Read<int>(0x1000);
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        Assert.Equal(2, replay.FrameCount);

        Assert.Equal(200, replay.Read<int>(0x1000)); // positioned at the end
        replay.Seek(0);
        Assert.Equal(100, replay.Read<int>(0x1000)); // scrubbed back
    }

    [Fact]
    public void TruncatedRecording_LoadsUpToTheLastCompleteEntry()
    {
        var live = new FakeMemoryReader().Place(0x1000, 0xABCDUL);
        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file))
        {
            recorder.MarkFrame();
            recorder.Read<ulong>(0x1000);
        }

        // Chop bytes off the tail, as a crash mid-write would.
        byte[] full = file.ToArray();
        var truncated = new MemoryStream(full, 0, full.Length - 3);

        var replay = ReplayMemoryReader.Load(truncated);
        Assert.Equal(1, replay.FrameCount); // frame marker survived; torn read dropped
    }
}
