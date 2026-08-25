using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The reader thread and its handoff to the renderer. Tested against a fake read function,
/// so the concurrency properties are checked without a game or memory involved.
/// </summary>
public class SnapshotFeedTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    private static WorldSnapshot SnapshotWith(int entities)
    {
        var list = new List<WorldEntity>();
        for (int i = 0; i < entities; i++)
        {
            list.Add(new WorldEntity((uint)i, 0x1000UL + (ulong)i, "Metadata/Monsters/X", EntityKind.Monster, i, i, 0));
        }

        return new WorldSnapshot(true, null, list, new float[16]);
    }

    /// <summary>Spins until <paramref name="condition"/> holds, or fails the test.</summary>
    private static void WaitFor(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail(because);
    }

    [Fact]
    public void Latest_IsUsableBeforeTheFirstReadCompletes()
    {
        // A renderer starting before the reader has produced anything must get a usable
        // snapshot, not a null - the very first frame draws before any read can finish.
        // Gated rather than timed: the first read starts IMMEDIATELY (the loop reads, then
        // waits), so a long interval would not hold it back.
        using var blocked = new ManualResetEventSlim(false);
        using var reading = new ManualResetEventSlim(false);
        using var feed = new SnapshotFeed(
            _ =>
            {
                reading.Set();
                blocked.Wait(TimeSpan.FromSeconds(5));
                return SnapshotWith(1);
            },
            Fast);

        Assert.True(reading.Wait(TimeSpan.FromSeconds(5)), "the reader never started");

        WorldSnapshot beforeAnyRead = feed.Latest;
        Assert.NotNull(beforeAnyRead);
        Assert.False(beforeAnyRead.InGame);
        Assert.Empty(beforeAnyRead.Entities);

        blocked.Set();
    }

    [Fact]
    public void Latest_PublishesWhatTheReaderProduced()
    {
        using var feed = new SnapshotFeed(_ => SnapshotWith(7), Fast);

        WaitFor(() => feed.Latest.InGame, "the feed never published a snapshot");
        Assert.Equal(7, feed.Latest.Entities.Count);
        Assert.True(feed.ReadCount > 0);
    }

    [Fact]
    public void Latest_NeverBlocks_WhileAReadIsInFlight()
    {
        // THE property the split exists for: the renderer must never wait on a read. With a
        // read deliberately wedged, Latest still returns immediately - the previous
        // snapshot - instead of blocking the frame behind it.
        using var blocked = new ManualResetEventSlim(false);
        int reads = 0;

        using var feed = new SnapshotFeed(
            _ =>
            {
                if (Interlocked.Increment(ref reads) > 1)
                {
                    blocked.Wait(TimeSpan.FromSeconds(5)); // second read hangs
                }

                return SnapshotWith(3);
            },
            Fast);

        WaitFor(() => feed.Latest.InGame, "the first snapshot never arrived");
        WaitFor(() => Volatile.Read(ref reads) > 1, "the reader never started the wedged read");

        // While that read hangs, the renderer keeps getting complete snapshots at once.
        for (int i = 0; i < 50; i++)
        {
            WorldSnapshot snapshot = feed.Latest;
            Assert.Equal(3, snapshot.Entities.Count);
        }

        blocked.Set();
    }

    [Fact]
    public void ReadFailures_DoNotEndTheFeed()
    {
        // Pointers go stale on every zone change, so reads throw as a matter of course. The
        // feed must keep the last good snapshot and carry on - a dead reader thread would
        // silently freeze the overlay for the rest of the session.
        int calls = 0;
        using var feed = new SnapshotFeed(
            _ =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n is >= 2 and <= 4)
                {
                    throw new InvalidOperationException("stale pointer");
                }

                return SnapshotWith(n);
            },
            Fast);

        WaitFor(() => feed.FailureCount >= 3, "the failures were never recorded");
        WaitFor(() => feed.ReadCount >= 2, "the feed did not recover after failing");

        // And the snapshot held during the failures was the last good one, never null.
        Assert.NotNull(feed.Latest);
        Assert.True(feed.Latest.InGame);
    }

    [Fact]
    public void Viewport_ReachesTheReader()
    {
        UiScale seen = default;
        using var feed = new SnapshotFeed(
            scale =>
            {
                seen = scale;
                return SnapshotWith(1);
            },
            Fast);

        feed.SetViewport(new UiScale(3440, 1440, 128));
        WaitFor(() => seen.WindowWidth == 3440, "the viewport never reached the reader");
        Assert.Equal(1440, seen.WindowHeight);
        Assert.Equal(128, seen.Cull);
    }

    [Fact]
    public void Dispose_StopsTheReader()
    {
        int calls = 0;
        var feed = new SnapshotFeed(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return SnapshotWith(1);
            },
            Fast);

        WaitFor(() => Volatile.Read(ref calls) > 0, "the feed never ran");
        feed.Dispose();

        int afterDispose = Volatile.Read(ref calls);
        Thread.Sleep(60); // several intervals
        Assert.Equal(afterDispose, Volatile.Read(ref calls));
    }

    [Fact]
    public void SlowReads_DoNotAccumulateDelay()
    {
        // The wait is paced by time ALREADY spent, so a read costing most of the interval
        // still leaves the cadence near target instead of drifting to read + interval.
        using var feed = new SnapshotFeed(
            _ =>
            {
                Thread.Sleep(20);
                return SnapshotWith(1);
            },
            TimeSpan.FromMilliseconds(25));

        WaitFor(() => feed.ReadCount >= 1, "no read completed");
        long start = feed.ReadCount;
        Thread.Sleep(250);
        long done = feed.ReadCount - start;

        // At 25ms per cycle, 250ms allows ~10. Drifting to 45ms per cycle would give ~5.
        Assert.True(done >= 7, $"only {done} reads in 250ms - the cadence drifted with cost");
    }

    [Fact]
    public void ASwallowedExceptionIsWrittenDown()
    {
        // Catching here is right - a stale pointer during a zone change must not end the feed -
        // but the catch used to record only that something had happened. Everything the read
        // callback does after the throw then simply stops: the rules, the buff list, the damage
        // meter. From the config window that is indistinguishable from a feature nobody wired
        // up, which is exactly how it presented.
        using var feed = new SnapshotFeed(
            _ => throw new InvalidOperationException("the entity map moved"),
            TimeSpan.FromMilliseconds(5));

        WaitFor(() => feed.FailureCount >= 1, "nothing failed");

        Assert.Contains("InvalidOperationException", feed.LastFailure, StringComparison.Ordinal);
        Assert.Contains("the entity map moved", feed.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingHasThrownReadsAsNothingRatherThanAsAnEmptyMessage()
    {
        using var feed = new SnapshotFeed(_ => SnapshotWith(1), TimeSpan.FromMilliseconds(5));
        WaitFor(() => feed.ReadCount >= 1, "no read completed");

        Assert.Equal(string.Empty, feed.LastFailure);
    }

    [Fact]
    public void TheDescriptionNamesTheFrameItCameOutOf()
    {
        // The message alone rarely says which of a dozen services on the reader thread threw,
        // and the TOP of the stack is the read callback every time - so the deepest frame is
        // the one worth keeping.
        InvalidOperationException caught;
        try
        {
            ThrowFromHere();
            return;
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        string described = SnapshotFeed.Describe(caught);
        Assert.Contains("InvalidOperationException: deep", described, StringComparison.Ordinal);
        Assert.Contains("ThrowFromHere", described, StringComparison.Ordinal);
    }

    private static void ThrowFromHere() => throw new InvalidOperationException("deep");

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        // A feed handed out early and replaced later is disposed at the hand-off AND by the
        // scope that held it. Cancel on a disposed token source throws, so without the guard
        // a tidy shutdown became a crash - in the one path that exists to make start-up safer.
        var feed = new SnapshotFeed(_ => SnapshotWith(1), TimeSpan.FromMilliseconds(5));
        feed.Dispose();
        feed.Dispose();
    }
}
