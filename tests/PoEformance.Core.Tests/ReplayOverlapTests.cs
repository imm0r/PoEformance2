using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which recorded block answers a read, once the replay stopped scanning all of them.
/// </summary>
/// <remarks>
/// A replay used to walk every block backwards until the read was covered. That is exactly
/// right and, on a two-minute session of two hundred thousand blocks, took 0.66 ms per read
/// against the thousands of reads one world scan makes - so recordings became long enough to
/// be worth replaying and, in the same change, too long to replay. The blocks are now filed
/// by the pages they cover.
///
/// These are the cases that decide whether the index kept the meaning. The rule is
/// newest-wins PER BYTE, and the trap is that the block holding the newest bytes need not
/// start where the read starts: a struct read covers the fields inside it, so an index keyed
/// on the start address would answer a field read from its own older block and never look at
/// the newer struct read sitting on top of it.
/// </remarks>
public class ReplayOverlapTests
{
    private const ulong Struct = 0x4_1000;

    /// <summary>A newer read that merely COVERS the address still wins.</summary>
    [Fact]
    public void AWiderNewerReadAnswersAFieldItCovers()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            // Frame 0: the field is read on its own, and read narrowly.
            memory.Place(Struct + 16, 1111L);
            recorder.MarkFrame();
            Assert.Equal(1111L, recorder.Read<long>(Struct + 16));

            // Frame 1: the field has moved, and this time the whole struct is read - one
            // block, starting sixteen bytes earlier, carrying the newer value.
            var whole = new byte[48];
            BitConverter.TryWriteBytes(whole.AsSpan(16, 8), 2222L);
            memory.Place(Struct, whole);
            recorder.MarkFrame();
            Assert.True(recorder.TryRead(Struct, new byte[48]));
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        replay.Seek(1);
        Assert.Equal(2222L, replay.Read<long>(Struct + 16));

        // And at frame 0 the struct read has not happened yet, so the narrow one still holds.
        replay.Seek(0);
        Assert.Equal(1111L, replay.Read<long>(Struct + 16));
    }

    /// <summary>Blocks and reads that straddle a page boundary are still found.</summary>
    /// <remarks>
    /// The index files a block under every page it touches and a read looks at every page it
    /// touches, so the two meet whichever side of a boundary either one starts on. Worth its
    /// own test because the failure would be invisible until a struct happened to land on a
    /// page edge - which is a property of the game's allocator, not of anything here.
    /// </remarks>
    [Fact]
    public void ReadsAcrossAPageBoundaryStillFindTheirBlocks()
    {
        const ulong Page = 4096;
        const ulong Straddling = (16 * Page) - 24; // 24 bytes before the boundary

        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        var written = new byte[64];
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = (byte)(i + 1);
        }

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            memory.Place(Straddling, written);
            recorder.MarkFrame();
            Assert.True(recorder.TryRead(Straddling, new byte[64]));
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        // The whole straddling block, read back as one.
        var all = new byte[64];
        Assert.True(replay.TryRead(Straddling, all));
        Assert.Equal(written, all);

        // A narrow read on the far side of the boundary, answered by a block that starts on
        // the near side - the case an index keyed on the read's own page alone would miss.
        var beyond = new byte[8];
        Assert.True(replay.TryRead(16 * Page, beyond));
        Assert.Equal(written[24..32], beyond);

        // And one that straddles the boundary itself.
        var across = new byte[16];
        Assert.True(replay.TryRead((16 * Page) - 8, across));
        Assert.Equal(written[16..32], across);
    }

    /// <summary>A read into memory the recording never touched still fails.</summary>
    /// <remarks>
    /// The symmetry with the live reader is the point: code that works against a replay works
    /// against the game, and a bad pointer has to fail on both. An index that answered from
    /// the nearest page would turn that failure into plausible nonsense.
    /// </remarks>
    [Fact]
    public void MemoryThatWasNeverRecordedIsStillUnreadable()
    {
        var memory = new FakeMemoryReader().Place(Struct, 1UL);
        var file = new MemoryStream();

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            recorder.MarkFrame();
            recorder.Read<ulong>(Struct);
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        Assert.False(replay.TryRead(Struct + 4096, new byte[8]));   // a page that has nothing
        Assert.False(replay.TryRead(Struct + 8, new byte[8]));      // the same page, past the block
        Assert.False(replay.TryRead(Struct, new byte[16]));         // half covered is not covered
    }
}
