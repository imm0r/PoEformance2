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
    /// </remarks>
    [Fact]
    public void AKilledSessionKeepsEverythingUpToTheLastFlush()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        long durable = 0;

        using (var recorder = new RecordingMemoryReader(memory, file) { FlushEveryMs = 0 })
        {
            for (int frame = 0; frame < 10; frame++)
            {
                memory.Place(Where, (long)frame);
                recorder.MarkFrame();

                // What would survive a kill at exactly this instant.
                durable = recorder.FileBytes;

                recorder.Read<long>(Where);
            }
        }

        byte[] killed = file.ToArray()[..(int)durable];
        var replay = ReplayMemoryReader.Load(new MemoryStream(killed));

        Assert.Equal(10, replay.FrameCount);

        // Every read before the last flush is there, frame for frame.
        for (uint frame = 0; frame < 9; frame++)
        {
            replay.Seek(frame);
            Assert.Equal((long)frame, replay.Read<long>(Where));
        }

        // And the last one, written after the flush, is gone rather than wrong - the replay
        // falls back to the newest data at or before that frame.
        replay.Seek(9);
        Assert.Equal(8L, replay.Read<long>(Where));
    }
}
