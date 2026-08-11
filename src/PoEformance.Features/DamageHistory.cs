namespace PoEformance.Features;

/// <summary>
/// How much damage was done over one short stretch, split by how well it is known.
/// </summary>
/// <remarks>
/// RATES rather than running totals, because a total over a map is a ramp that only ever goes
/// up and the shape of a ramp says nothing. What anybody asks a damage graph is "when was I
/// doing well and when was I not", and that is the rate.
///
/// Split the same three ways the readout above it is, because the split is the part of this
/// figure that is a decision rather than a measurement - a spike made entirely of monsters
/// that vanished is a different event from one watched off their health, and a single line
/// cannot tell them apart.
/// </remarks>
/// <param name="Watched">Damage per second seen falling off monster health.</param>
/// <param name="Credited">Per second from monsters that were already being hurt when they went.</param>
/// <param name="Untouched">Per second from monsters that vanished without a scratch seen.</param>
public readonly record struct DamageSample(
    long AtMs, uint Area, float Watched, float Credited, float Untouched)
{
    /// <summary>All of it - the height of one bar on the graph.</summary>
    public float Total => Watched + Credited + Untouched;
}

/// <summary>
/// Keeps what the damage was doing over a whole map, so it can be looked at as a shape.
/// </summary>
/// <remarks>
/// The same arrangement as <see cref="CostHistory"/> and for the same reason: a live number
/// answers "how am I doing right now", which is the question the status line already answers.
/// The ones worth asking need time - was that pack better than this one, did the build stop
/// working when the rare spawned, what actually was the best moment of the map.
///
/// ON AN INTERVAL rather than per read. The meter is fed thirty times a second and most of
/// those readings carry no damage at all, so a sample per read is mostly zeroes with spikes
/// between them - a graph of the sampling rate rather than of the fighting. A quarter of a
/// second is short enough to keep a burst distinct and long enough that the line is a line.
///
/// Written from the reader thread and read from the renderer, so it locks - adds four times a
/// second against reads once a frame, where the contention is not measurable and the
/// alternative is a lock-free structure whose bugs would be.
/// </remarks>
public sealed class DamageHistory
{
    /// <summary>How long one sample covers. See the type remarks.</summary>
    public const long IntervalMs = 250;

    /// <summary>Samples kept before the oldest is dropped - a little over half an hour.</summary>
    public const int Capacity = 8192;

    private readonly Lock _gate = new();
    private readonly DamageSample[] _samples = new DamageSample[Capacity];
    private int _next;
    private int _count;

    /// <summary>How many samples are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Records one stretch. Called from the reader thread.</summary>
    public void Add(DamageSample sample)
    {
        lock (_gate)
        {
            _samples[_next] = sample;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    /// <summary>Forgets everything. For starting a fresh measurement.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>Every sample held, oldest first.</summary>
    public IReadOnlyList<DamageSample> Samples()
    {
        lock (_gate)
        {
            var all = new DamageSample[_count];
            int start = _count == Capacity ? _next : 0;
            for (int i = 0; i < _count; i++)
            {
                all[i] = _samples[(start + i) % Capacity];
            }

            return all;
        }
    }

    /// <summary>The samples for one area, oldest first, or everything held when 0.</summary>
    public IReadOnlyList<DamageSample> In(uint area)
    {
        IReadOnlyList<DamageSample> all = Samples();
        if (area == 0)
        {
            return all;
        }

        var mine = new List<DamageSample>(all.Count);
        foreach (DamageSample sample in all)
        {
            if (sample.Area == area)
            {
                mine.Add(sample);
            }
        }

        return mine;
    }

    /// <summary>The tallest bar in a scope, which is what a graph has to be scaled to.</summary>
    /// <remarks>
    /// Zero when nothing was done, and a caller must treat that as "no scale" rather than
    /// dividing by it - a whole map of standing still is a real thing to look at.
    /// </remarks>
    public float Highest(uint area = 0)
    {
        float highest = 0f;
        foreach (DamageSample sample in In(area))
        {
            highest = MathF.Max(highest, sample.Total);
        }

        return highest;
    }

    /// <summary>The areas held, newest first - one entry per map still in the buffer.</summary>
    public IReadOnlyList<uint> Areas()
    {
        IReadOnlyList<DamageSample> all = Samples();
        var seen = new List<uint>();

        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].Area != 0 && !seen.Contains(all[i].Area))
            {
                seen.Add(all[i].Area);
            }
        }

        return seen;
    }

    /// <summary>How long the samples for a scope span, in seconds.</summary>
    public double SecondsIn(uint area)
    {
        IReadOnlyList<DamageSample> mine = In(area);
        return mine.Count == 0 ? 0 : (mine[^1].AtMs - mine[0].AtMs) / 1000.0;
    }
}
