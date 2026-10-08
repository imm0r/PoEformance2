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
        // The wait is paced by time ALREADY spent, so a read costing most of the interval still
        // starts the next one on target instead of drifting to read + interval, and a read
        // costing more than the interval is followed at once.
        //
        // ON A CLOCK THE TEST OWNS. This used to sleep 20ms per read and count the reads in 250ms
        // of wall time, which the parallel suite on four cores failed one run in three with 4 reads:
        // it measured how promptly a loaded machine woke two threads, not what the feed asked for.
        // Here a read costs what it says on the stepped clock and a wait moves that clock by exactly
        // what the feed asked for, so the gap between two reads is the pacing decision and nothing
        // else - and waiting the full interval after every read fails it on every gap.
        TimeSpan interval = TimeSpan.FromMilliseconds(25);
        int[] costMilliseconds = [20, 20, 40, 20, 40, 5];

        using var holding = new ManualResetEventSlim(false);
        var clock = new SteppedClock(holding);
        var starts = new List<TimeSpan>();

        using var feed = new SnapshotFeed(
            _ =>
            {
                // One read past the costed ones, so the last costed read's gap is measured too.
                starts.Add(clock.Now);
                if (starts.Count > costMilliseconds.Length)
                {
                    clock.Hold = true;
                }
                else
                {
                    clock.Spend(TimeSpan.FromMilliseconds(costMilliseconds[starts.Count - 1]));
                }

                return SnapshotWith(1);
            },
            interval,
            clock);

        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)), "the feed never got through its reads");

        for (int i = 0; i < costMilliseconds.Length; i++)
        {
            TimeSpan cost = TimeSpan.FromMilliseconds(costMilliseconds[i]);
            TimeSpan expected = cost > interval ? cost : interval;
            TimeSpan gap = starts[i + 1] - starts[i];
            Assert.True(
                gap == expected,
                $"the read after one costing {cost.TotalMilliseconds}ms began {gap.TotalMilliseconds}ms after it, "
                + $"not {expected.TotalMilliseconds}ms - the cadence drifted with cost");
        }
    }

    /// <summary>
    /// The feed's clock, stepped by the test: it moves by what a read says it cost and by exactly
    /// the wait the feed asked for, and by nothing else.
    /// </summary>
    private sealed class SteppedClock(ManualResetEventSlim holding) : IFeedClock
    {
        private long _now;

        public TimeSpan Now => TimeSpan.FromTicks(_now);

        /// <summary>Parks the feed at its next wait until it is disposed.</summary>
        public bool Hold { get; set; }

        public void Spend(TimeSpan cost) => _now += cost.Ticks;

        public long GetTimestamp() => _now;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromTicks(_now - startingTimestamp);

        public void Wait(WaitHandle cancelled, TimeSpan duration)
        {
            _now += duration.Ticks;
            if (Hold)
            {
                // Parked rather than returned: a clock that never makes the feed wait would leave
                // it reading flat out on a machine the rest of the suite is sharing. Dispose sets
                // the handle, which is what lets the loop end.
                holding.Set();
                cancelled.WaitOne();
            }
        }
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
    public void DisposingWhileEveryReadThrowsDoesNotTakeTheProcessDown()
    {
        // THE CRASH THIS PINS was live for as long as the catch carried a filter reading
        // "when (!_cancellation.IsCancellationRequested)". A read that threw at the instant
        // shutdown was requested did not match it, so it was never caught at all: it left Loop,
        // reached the top of a background thread, and killed the process. CI found it as a test
        // host crash after 898 unrelated tests had passed, which is what a race looks like from
        // the outside - and it is not confined to tests, since disposing the feed while a read is
        // in flight is simply what shutdown is.
        //
        // Throw on EVERY read and dispose while they are in flight, repeatedly, so the window the
        // filter left open is actually aimed at rather than waited for.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var feed = new SnapshotFeed(
                _ => throw new InvalidOperationException("the entity map moved"),
                TimeSpan.FromMilliseconds(1));

            WaitFor(() => feed.FailureCount >= 1, "nothing failed");
            feed.Dispose();
        }

        // Reaching here at all is the assertion - the old code took the test host with it - but
        // an explicit one says so to anyone reading a green run.
        Assert.True(true);
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
