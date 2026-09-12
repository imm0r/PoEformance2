using System.Diagnostics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Reads world snapshots on its own thread and hands the newest one to whoever asks.
/// </summary>
/// <remarks>
/// The renderer used to call the reader directly, which meant every drawn frame walked the
/// entity map and read a component set for every entity - at 60 frames a second, with a
/// read that costs milliseconds and grows with the monster count. The frame rate was
/// therefore bounded by memory reads, and worst in exactly the moment it matters: a screen
/// full of monsters.
///
/// The split follows the shape the architecture doc set out. One reader thread produces
/// IMMUTABLE snapshots at its own pace; publishing one is a single reference assignment,
/// which is atomic - so a reader mid-update cannot hand out a half-built snapshot, and the
/// render thread never waits, never locks, and never sees a torn one. That property is a
/// consequence of the snapshot being a record rather than something the code has to
/// arrange, which is why the type matters more than the synchronisation here.
///
/// Reading and drawing also want different rates. Sixty reads a second buys nothing when
/// entities move at the game's tick rate, so the feed runs slower than the frame rate on
/// purpose: fewer reads, less disturbance of the game process, and a frame rate that no
/// longer depends on how many monsters are on screen.
///
/// The predecessor needed two extra PROCESSES, shared memory and a seqlock protocol to get
/// this same headroom out of a single-threaded language. Here it is a thread and a field.
/// </remarks>
public sealed class SnapshotFeed : IDisposable
{
    private readonly Func<UiScale, WorldSnapshot> _read;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _thread;

    // Written by the reader thread, read by the renderer. A reference assignment is atomic,
    // and WorldSnapshot is immutable, so no lock is needed on either side.
    private WorldSnapshot _latest = WorldSnapshot.Empty;

    // The viewport travels the other way: the renderer knows it, the reader needs it.
    // Boxed so the publish is a single reference write, and only written when it actually
    // changes - so the per-frame path allocates nothing.
    private object _viewport = default(UiScale);
    private UiScale _lastPublishedViewport;

    private long _readCount;
    private long _failureCount;
    private long _lastReadTicks;
    private string _lastFailure = string.Empty;

    /// <summary>Starts the reader thread.</summary>
    /// <param name="read">Reads one snapshot. Called only on the feed's thread.</param>
    /// <param name="interval">Target time between reads.</param>
    public SnapshotFeed(Func<UiScale, WorldSnapshot> read, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);

        _thread = new Thread(Loop)
        {
            Name = "world-reader",
            IsBackground = true, // a stuck read must never keep the process alive
        };
        _thread.Start();
    }

    /// <summary>
    /// The newest snapshot. Never blocks, never null, never partially built.
    /// </summary>
    public WorldSnapshot Latest => Volatile.Read(ref _latest);

    /// <summary>How long the last read took - the number that justifies this class.</summary>
    public double LastReadMilliseconds => TimeSpan.FromTicks(Volatile.Read(ref _lastReadTicks)).TotalMilliseconds;

    /// <summary>Completed reads since start.</summary>
    public long ReadCount => Interlocked.Read(ref _readCount);

    /// <summary>
    /// Reads that threw. Expected to be non-zero: pointers go stale during a zone change.
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>What the last swallowed exception was, or empty when nothing has thrown.</summary>
    /// <remarks>
    /// A COUNT WITHOUT A MESSAGE IS NOT A DIAGNOSTIC. Catching here is right - a stale pointer
    /// during a zone change must not end the feed - but the catch used to record only that
    /// something had happened, and everything the callback does after the throw simply stopped
    /// happening. From the outside that is indistinguishable from a feature that was never
    /// wired up: the rules said "not started", the buff list stayed empty, and the overlay went
    /// on drawing the last good snapshot, so nothing anywhere said a word about an exception.
    /// </remarks>
    public string LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>Tells the reader which viewport the renderer is drawing into.</summary>
    /// <remarks>
    /// Called every frame, so it publishes only on a change. The comparison is against a
    /// field only the calling thread touches, so it needs no synchronisation of its own.
    /// </remarks>
    public void SetViewport(UiScale viewport)
    {
        if (!viewport.Equals(_lastPublishedViewport))
        {
            _lastPublishedViewport = viewport;
            Volatile.Write(ref _viewport, viewport);
        }
    }

    private void Loop()
    {
        var clock = new Stopwatch();
        while (!_cancellation.IsCancellationRequested)
        {
            clock.Restart();
            try
            {
                WorldSnapshot snapshot = _read((UiScale)Volatile.Read(ref _viewport));

                // The publish. One assignment, and the renderer's next Latest sees it whole.
                Volatile.Write(ref _latest, snapshot);
                Interlocked.Increment(ref _readCount);
            }
            catch (Exception exception)
            {
                // CAUGHT UNCONDITIONALLY, and the filter that used to be here was a crash. It
                // read "when (!_cancellation.IsCancellationRequested)", so a read that threw at
                // the instant shutdown was requested did not match, left Loop entirely, and went
                // unhandled on a background thread - which takes the process down. Disposing the
                // feed while a read is in flight is not an edge case; it is what shutdown IS, and
                // a reader whose whole purpose is surviving stale pointers must not die of one
                // arriving a millisecond late.
                //
                // The filter's intent survives below: nothing is recorded once cancellation is
                // requested, because a failure caused by teardown is noise. The difference is
                // that it is now a decision made INSIDE the handler rather than a condition on
                // whether to handle at all.
                if (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                // A read that throws must not end the feed. Pointers go stale on every zone
                // change, and the correct response is to keep the last good snapshot and
                // try again - a dead reader thread would be a far worse outcome than a
                // frame of slightly old data.
                //
                // But it is WRITTEN DOWN. Everything the callback does after the throw - the
                // rules, the buff list, the damage meter - simply stops happening, and with
                // only a counter to go on that is indistinguishable from a feature nobody
                // wired up. The throwing frame is kept because the message alone rarely says
                // which of a dozen services on this thread it came out of.
                // The MESSAGE first, then the count. Anything watching notices the count and
                // then goes looking for the reason, so incrementing first leaves a window in
                // which a failure is visible and unexplained - which is the state this whole
                // field exists to abolish.
                Volatile.Write(ref _lastFailure, Describe(exception));
                Interlocked.Increment(ref _failureCount);
            }

            clock.Stop();
            Volatile.Write(ref _lastReadTicks, clock.Elapsed.Ticks);

            // Pace by the time ALREADY spent, so a slow read shortens the wait instead of
            // adding to it - the cadence stays the target rather than drifting with cost.
            TimeSpan remaining = _interval - clock.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                _cancellation.Token.WaitHandle.WaitOne(remaining);
            }
        }
    }

    /// <summary>One line naming the exception and the frame it came out of.</summary>
    /// <remarks>
    /// Public because it is the format the config window shows, and a format nobody can test
    /// is a format that quietly stops naming the frame.
    /// </remarks>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The DEEPEST frame, which is where it actually went wrong - the top of the stack
        // string is the read callback every time and says nothing.
        string where = string.Empty;
        string? stack = exception.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            string[] lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            where = lines[0].Trim();
            if (where.StartsWith("at ", StringComparison.Ordinal))
            {
                where = where[3..];
            }

            int file = where.IndexOf(" in ", StringComparison.Ordinal);
            if (file >= 0)
            {
                where = where[..file];
            }
        }

        string what = $"{exception.GetType().Name}: {exception.Message}";
        return where.Length > 0 ? $"{what} - in {where}" : what;
    }

    private bool _disposed;

    public void Dispose()
    {
        // Idempotent, because a feed handed out early and replaced later is disposed at the
        // hand-off AND by whatever scope held it - and Cancel on a disposed token source
        // throws, which would turn a tidy shutdown into a crash.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        // Bounded: the thread is a background one, so a read wedged inside the game process
        // cannot hold up shutdown past this.
        _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }
}
