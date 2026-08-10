using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// A recording is compressed on the way to disk, and it has to stay a recording.
/// </summary>
/// <remarks>
/// The measurement that prompted this, on a real 25.7 MB session: the entry stream packs
/// down to 137 KB, and an ordinary archiver had already shown that was possible - which is
/// the whole reason the file was worth compressing rather than merely trimming. The header
/// stays in the clear so the file is still identifiable and, more importantly, so the
/// version that decides how to read the rest can be read without guessing.
/// </remarks>
public class RecordingCompressionTests
{
    private const ulong Where = 0x3_0000;

    /// <summary>The first bytes of the file still say what the file is.</summary>
    [Fact]
    public void TheHeaderIsNotCompressed()
    {
        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(new FakeMemoryReader().Place(Where, 1UL), file))
        {
            recorder.MarkFrame();
            recorder.Read<ulong>(Where);
        }

        byte[] bytes = file.ToArray();
        Assert.Equal(RecordingFormat.Magic.ToArray(), bytes[..8]);
        Assert.Equal(RecordingFormat.Version, BitConverter.ToUInt32(bytes, 8));
    }

    /// <summary>Recordings written before compression still replay.</summary>
    /// <remarks>
    /// The fixtures under tests/fixtures are version 2 and they are the regression tests
    /// against real memory, so the loader keeps reading that layout. This builds one by
    /// hand rather than depending on a fixture, so the compatibility is stated rather than
    /// merely inherited.
    /// </remarks>
    [Fact]
    public void AnUncompressedRecordingStillLoads()
    {
        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        writer.Write(RecordingFormat.Magic);
        writer.Write(RecordingFormat.UncompressedVersion);
        writer.Write(4242u);
        writer.Write(0x7FF6_0000_0000UL);
        writer.Write(1024u);
        writer.Write(DateTime.UtcNow.Ticks);

        writer.Write(RecordingFormat.TagFrame);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(RecordingFormat.TagRead);
        writer.Write(Where);
        writer.Write(8u);
        writer.Write(0xDEADBEEFUL);
        writer.Flush();

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        Assert.Equal(4242, replay.ProcessId);
        Assert.Equal(1, replay.FrameCount);
        Assert.Equal(0xDEADBEEFUL, replay.Read<ulong>(Where));
    }

    /// <summary>The file is a fraction of the entries it carries, and replays intact.</summary>
    [Fact]
    public void TheFileIsMuchSmallerThanWhatItRecords()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();

        // Two thousand addresses, each read once with a different value, so nothing is
        // dropped as unchanged and compression is the only thing left doing any work.
        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            recorder.MarkFrame();
            for (int i = 0; i < 2000; i++)
            {
                memory.Place(Where + ((ulong)i * 8), (long)i);
                recorder.Read<long>(Where + ((ulong)i * 8));
            }
        }

        // Written verbatim, each read costs its eight bytes plus thirteen of tag, address
        // and length - the repetition that makes the entry stream compress so well.
        const int Verbatim = 2000 * (8 + 13);
        int size = file.ToArray().Length;
        Assert.True(size < Verbatim / 4, $"recording was {size} bytes of a possible {Verbatim}");

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        for (int i = 0; i < 2000; i++)
        {
            Assert.Equal((long)i, replay.Read<long>(Where + ((ulong)i * 8)));
        }
    }

    /// <summary>
    /// A session that was killed rather than closed replays up to its last flush.
    /// </summary>
    /// <remarks>
    /// This is the cost of compressing, and the reason the writer flushes at all: an
    /// uncompressed recording was readable up to the last complete ENTRY, because entries
    /// are self-delimiting and a torn one can simply be dropped. A compressed one is
    /// readable up to the last complete BLOCK. The flush is what bounds the difference, so
    /// the test kills the session at a known flush and demands everything before it.
    ///
    /// Deliberately far more than a block's worth of frames. An earlier version of this test
    /// wrote ten of them and passed against a flush that did not flush at all - the whole
    /// recording fit inside the first block the encoder emitted anyway, so the assertion was
    /// true for a reason that had nothing to do with what it claimed to check. Two real
    /// recordings, ending at exactly 8,388,608 decoded bytes each, are what it missed.
    /// </remarks>
    [Fact]
    public void AKilledSessionKeepsEverythingUpToTheLastFlush()
    {
        // Enough entries to pass the eight megabytes the encoder will otherwise sit on.
        const int Frames = 2_200;
        const int BlockSize = 4096;

        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        long durable = 0;

        // Placed once and mutated in place: the fake keeps the array, and adding a region
        // per frame would turn every read into a walk of thousands of them.
        var moving = new byte[8];
        var block = new byte[BlockSize];
        memory.Place(Where, moving);
        memory.Place(Where + 64, block);

        using (var recorder = new RecordingMemoryReader(memory, file) { FlushEveryMs = 0 })
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                BitConverter.TryWriteBytes(moving, (long)frame);
                BitConverter.TryWriteBytes(block.AsSpan(0, 8), (long)frame * 7);

                recorder.MarkFrame();

                // What would survive a kill at exactly this instant.
                durable = recorder.FileBytes;

                recorder.Read<long>(Where);
                recorder.TryRead(Where + 64, new byte[BlockSize]);
            }
        }

        byte[] killed = file.ToArray()[..(int)durable];
        var replay = ReplayMemoryReader.Load(new MemoryStream(killed));

        Assert.Equal(Frames, replay.FrameCount);

        // Every read before the last flush is there, frame for frame.
        for (uint frame = 0; frame < Frames - 1; frame++)
        {
            replay.Seek(frame);
            Assert.Equal((long)frame, replay.Read<long>(Where));
        }

        // And the last one, written after the flush, is gone rather than wrong - the replay
        // falls back to the newest data at or before that frame.
        replay.Seek(Frames - 1);
        Assert.Equal((long)(Frames - 2), replay.Read<long>(Where));
    }

    /// <summary>With the settings it actually ships with, a kill costs a few seconds.</summary>
    /// <remarks>
    /// The test above forces a segment per frame to exercise the mechanism; this one leaves
    /// the defaults alone, because the number that matters is what a real recording loses.
    /// A frame here is deliberately fat - twelve times what the game's reader writes per
    /// tick - so a few thousand of them stand in for a long session without taking one.
    /// </remarks>
    [Fact]
    public void TheDefaultSettingsLoseSecondsRatherThanMinutes()
    {
        const int Frames = 3_000;
        const int BlockSize = 4096;

        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        var block = new byte[BlockSize];
        memory.Place(Where, block);

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                BitConverter.TryWriteBytes(block.AsSpan(0, 8), (long)frame);
                recorder.MarkFrame();
                recorder.TryRead(Where, new byte[BlockSize]);
            }
        }

        // Killed: no Dispose, so the tail of the file is whatever the segments had closed.
        // The whole file is 12 MB of entries; the default boundary is every 128 KB.
        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        Assert.True(replay.FrameCount > Frames * 0.95,
            $"only {replay.FrameCount} of {Frames} frames survived");
    }
}
