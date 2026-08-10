using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// A recording must replay as the process behaved, however little of it was written down.
/// </summary>
/// <remarks>
/// The recorder now skips a read whose bytes have not moved since the last one written for
/// that address, because a real session showed why it had to: one frame held 1.2 million
/// reads and 32 MB, almost all of it the same addresses carrying the same bytes, and an
/// ordinary archiver squeezed the file to under a two-hundredth of its size. The recording
/// filled and stopped 437 milliseconds in - before anything worth looking at had happened.
///
/// Skipping is only sound because the replay serves "the newest data at or before the current
/// frame", so an unchanged read that was never written resolves to identical bytes from the
/// frame that did write them. That is an argument, and these tests are the check on it: they
/// drive a recorder over changing memory and then demand that every frame of the replay
/// returns exactly what the live reader returned at that frame.
///
/// The overlapping case is the one worth the trouble. Reads of different widths at nearby
/// addresses cover each other, so a suppressed narrow read could in principle be answered by
/// a wider block captured at a different moment - which is the failure that would corrupt a
/// replay while leaving the file looking healthy.
/// </remarks>
public class RecordingRoundTripTests
{
    private const ulong Where = 0x2_0000;

    /// <summary>Records a session and hands back what the live reads returned, per frame.</summary>
    private static (byte[] File, List<byte[]> Live, long Skipped) Record(
        int frames,
        Action<FakeMemoryReader, int> mutate,
        int readLength = 8,
        ulong readAt = Where)
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        var live = new List<byte[]>();
        long skipped;

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            for (int frame = 0; frame < frames; frame++)
            {
                mutate(memory, frame);
                recorder.MarkFrame();

                var got = new byte[readLength];
                Assert.True(recorder.TryRead(readAt, got), $"the live read failed at frame {frame}");
                live.Add(got);
            }

            skipped = recorder.SkippedUnchangedReads;
        }

        return (file.ToArray(), live, skipped);
    }

    /// <summary>Memory that never moves is written once and replays the same every frame.</summary>
    [Fact]
    public void UnchangingMemoryIsWrittenOnceAndStillReplays()
    {
        (byte[] file, List<byte[]> live, long skipped) = Record(
            frames: 10,
            mutate: (memory, frame) =>
            {
                if (frame == 0)
                {
                    memory.Place(Where, 1, 2, 3, 4, 5, 6, 7, 8);
                }
            });

        Assert.Equal(9, skipped);   // one written, nine recognised as saying nothing new

        var replay = ReplayMemoryReader.Load(new MemoryStream(file));
        for (uint frame = 0; frame < live.Count; frame++)
        {
            replay.Seek(frame);
            var got = new byte[8];
            Assert.True(replay.TryRead(Where, got), $"frame {frame} lost its value");
            Assert.Equal(live[(int)frame], got);
        }
    }

    /// <summary>
    /// Memory that moves replays move for move, and only the moves are written.
    /// </summary>
    [Fact]
    public void EveryChangeSurvivesAndOnlyChangesAreWritten()
    {
        // Changes on a third of the frames; the rest repeat the previous value.
        (byte[] file, List<byte[]> live, long skipped) = Record(
            frames: 12,
            mutate: (memory, frame) =>
            {
                if (frame % 3 == 0)
                {
                    memory.Place(Where, (long)(frame + 1));
                }
            });

        Assert.Equal(8, skipped);   // 12 frames, 4 of them carrying something new

        var replay = ReplayMemoryReader.Load(new MemoryStream(file));
        for (uint frame = 0; frame < live.Count; frame++)
        {
            replay.Seek(frame);
            var got = new byte[8];
            Assert.True(replay.TryRead(Where, got));
            Assert.Equal(live[(int)frame], got);
        }
    }

    /// <summary>
    /// A value that goes away and comes back is not mistaken for one that never left.
    /// </summary>
    /// <remarks>
    /// The comparison is against the last bytes WRITTEN, not against the last bytes read, so
    /// a value that returns to an earlier one after moving in between must still be written -
    /// otherwise the frames in the middle would be the ones the replay serves.
    /// </remarks>
    [Fact]
    public void AValueThatReturnsToWhatItWasIsStillWritten()
    {
        (byte[] file, List<byte[]> live, _) = Record(
            frames: 3,
            mutate: (memory, frame) => memory.Place(Where, (long)(frame == 1 ? 99 : 7)));

        var replay = ReplayMemoryReader.Load(new MemoryStream(file));
        for (uint frame = 0; frame < live.Count; frame++)
        {
            replay.Seek(frame);
            var got = new byte[8];
            Assert.True(replay.TryRead(Where, got));
            Assert.Equal(live[(int)frame], got);
        }

        // And the middle frame really is the odd one out, rather than smeared over its
        // neighbours by the replay's newest-at-or-before rule.
        replay.Seek(1);
        var middle = new byte[8];
        replay.TryRead(Where, middle);
        Assert.Equal(99L, BitConverter.ToInt64(middle));
    }

    /// <summary>
    /// Reads of different widths over the same bytes do not answer for each other.
    /// </summary>
    /// <remarks>
    /// The failure this guards against would corrupt a replay while leaving the file looking
    /// healthy: a narrow read suppressed as unchanged, then served from a wider block that
    /// was captured at a different moment. Width is part of the key for exactly this reason.
    /// </remarks>
    [Fact]
    public void OverlappingReadsOfDifferentWidthsStayHonest()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        var narrow = new List<byte[]>();
        var wide = new List<byte[]>();

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            for (int frame = 0; frame < 8; frame++)
            {
                // Sixteen bytes, of which only the second half moves. A narrow read of the
                // first half never changes; a wide read across both does, every frame.
                var block = new byte[16];
                BitConverter.TryWriteBytes(block.AsSpan(0, 8), 0xAAAA_BBBB_CCCC_DDDDul);
                BitConverter.TryWriteBytes(block.AsSpan(8, 8), (ulong)frame);
                memory.Place(Where, block);

                recorder.MarkFrame();

                var eight = new byte[8];
                Assert.True(recorder.TryRead(Where, eight));
                narrow.Add(eight);

                var sixteen = new byte[16];
                Assert.True(recorder.TryRead(Where, sixteen));
                wide.Add(sixteen);
            }
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        for (uint frame = 0; frame < narrow.Count; frame++)
        {
            replay.Seek(frame);

            var eight = new byte[8];
            Assert.True(replay.TryRead(Where, eight));
            Assert.Equal(narrow[(int)frame], eight);

            var sixteen = new byte[16];
            Assert.True(replay.TryRead(Where, sixteen));
            Assert.Equal(wide[(int)frame], sixteen);
        }
    }

    /// <summary>
    /// Forgetting what was written, to stay within a memory bound, cannot change a replay.
    /// </summary>
    /// <remarks>
    /// The comparison history is cleared once it grows past its bound, which turns every
    /// address back into a first sighting and so writes them all again. That is a size
    /// argument, not a correctness one - and this is the check that it stays that way.
    /// </remarks>
    [Fact]
    public void ForgettingWhatWasWrittenCostsSizeAndNotAccuracy()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();
        var live = new List<byte[]>();

        // Room for four addresses, five in play, so the history is cleared repeatedly.
        using (var recorder = new RecordingMemoryReader(memory, file) { MaxTrackedReads = 4 })
        {
            for (int frame = 0; frame < 6; frame++)
            {
                recorder.MarkFrame();
                for (int address = 0; address < 5; address++)
                {
                    memory.Place(Where + ((ulong)address * 8), (long)((frame * 10) + address));
                    var got = new byte[8];
                    Assert.True(recorder.TryRead(Where + ((ulong)address * 8), got));
                    live.Add(got);
                }
            }

            Assert.True(recorder.ForgottenReadHistories > 0, "the bound was never reached");
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        for (int frame = 0; frame < 6; frame++)
        {
            replay.Seek((uint)frame);
            for (int address = 0; address < 5; address++)
            {
                var got = new byte[8];
                Assert.True(replay.TryRead(Where + ((ulong)address * 8), got));
                Assert.Equal(live[(frame * 5) + address], got);
            }
        }
    }

    /// <summary>The saving is the point, so it is worth asserting it is a large one.</summary>
    /// <remarks>
    /// Shaped like the session that prompted this: the same handful of addresses read over
    /// and over, with only a little of it moving. A recorder that wrote all of it would grow
    /// with the reads; this one grows with the changes.
    /// </remarks>
    [Fact]
    public void ARepetitiveSessionNoLongerGrowsWithItsReads()
    {
        var memory = new FakeMemoryReader();
        var file = new MemoryStream();

        using (var recorder = new RecordingMemoryReader(memory, file))
        {
            for (int address = 0; address < 200; address++)
            {
                memory.Place(Where + ((ulong)address * 8), (long)address);
            }

            for (int frame = 0; frame < 100; frame++)
            {
                recorder.MarkFrame();
                for (int address = 0; address < 200; address++)
                {
                    var got = new byte[8];
                    recorder.TryRead(Where + ((ulong)address * 8), got);
                }
            }

            // 20,000 reads, 200 of which said anything.
            Assert.Equal(19_800, recorder.SkippedUnchangedReads);
            Assert.Equal(200 * 8, recorder.RecordedBytes);
        }

        // The file is a header, a hundred frame markers and two hundred reads - kilobytes,
        // where writing every read would have been megabytes.
        int written = file.ToArray().Length;
        Assert.True(written < 8_000, $"the recording is {written} bytes");
    }
}
