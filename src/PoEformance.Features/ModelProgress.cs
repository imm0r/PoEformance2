using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// How far a model being built has got: which step it is on and how much of that step is done - read by the pane while the build runs on the thread pool.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A laid room takes seconds - hundreds of tile pieces, dozens of doodads, every
/// material's graphs - and all the pane said while it ran was one line that did not change. Asked for
/// from the live client: whether it is still working or has quietly failed should not be a guess.
///
/// ONLY THE OUTERMOST STEP COUNTS. A room lays tile pieces, and each piece paints its own shapes; were
/// the inner walk to report as well, the bar would run back and forth inside every piece. So a step
/// begun while another is open is inert, and the bar follows the steps a person can name: the pieces,
/// the doodads, the graphs. A build that is handed none reports nothing and pays one null check a step.
///
/// THE PANE READS WHILE THE BUILD WRITES, from another thread, and three numbers read one after another
/// may be from two moments - a bar a step behind its label for one frame. That is a picture of
/// progress, not a ledger, and no lock is worth it.
/// </remarks>
public sealed class ModelProgress
{
    private readonly long _started = Environment.TickCount64;
    private int _depth;
    private int _done;
    private int _total;
    private string _stage = string.Empty;

    /// <summary>The step under way, in words - empty before the first.</summary>
    public string Stage => Volatile.Read(ref _stage);

    /// <summary>How much of the step is done.</summary>
    public int Done => Volatile.Read(ref _done);

    /// <summary>How much the step has to do - nought where it did not say.</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>How much of the step is done, nought to one.</summary>
    public float Fraction
    {
        get
        {
            int total = Total;
            return total > 0 ? Math.Clamp((float)Done / total, 0f, 1f) : 0f;
        }
    }

    /// <summary>How long the build has run, in whole seconds.</summary>
    public long Seconds => (Environment.TickCount64 - _started) / 1000;

    /// <summary>The line the pane shows: what is being built, the step and how far it is, and for how long.</summary>
    public string Said()
    {
        string stage = Stage;
        int total = Total;
        string step = stage.Length == 0
            ? string.Empty
            : total > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {stage} {Math.Min(Done, total)} of {total}") : " · " + stage;
        return string.Create(CultureInfo.InvariantCulture, $"building the model{step} · {Seconds} s");
    }

    /// <summary>
    /// Begins a step of <paramref name="total"/> things - inert where <paramref name="progress"/> is null or another step is open. Dispose it when the step is done.
    /// </summary>
    public static Step Begin(ModelProgress? progress, string stage, int total)
    {
        if (progress is null)
        {
            return default;
        }

        if (Interlocked.Increment(ref progress._depth) != 1)
        {
            return new Step(progress, counts: false);
        }

        Volatile.Write(ref progress._done, 0);
        Volatile.Write(ref progress._total, Math.Max(0, total));
        Volatile.Write(ref progress._stage, stage);
        return new Step(progress, counts: true);
    }

    /// <summary>One step of a build - see <see cref="Begin"/>.</summary>
    public readonly struct Step : IDisposable
    {
        private readonly ModelProgress? _progress;
        private readonly bool _counts;

        internal Step(ModelProgress progress, bool counts)
        {
            _progress = progress;
            _counts = counts;
        }

        /// <summary>One more thing done.</summary>
        public void Advance()
        {
            if (_counts)
            {
                Interlocked.Increment(ref _progress!._done);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_progress is not null)
            {
                Interlocked.Decrement(ref _progress._depth);
            }
        }
    }
}
