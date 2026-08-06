using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One read's cost, and when it happened.</summary>
/// <param name="Area">
/// Which area it was read in. Carried so a graph can be scoped to the map being run rather
/// than to whatever happened to still be in the buffer.
/// </param>
public readonly record struct CostSample(long AtMs, uint Area, ReadCost Cost);

/// <summary>Which measured phase a series is about.</summary>
public enum CostPhase
{
    Total,
    Entities,
    Player,
    Terrain,
    Maps,
    Other,
}

/// <summary>How a phase behaved over a stretch of time.</summary>
/// <param name="Worst">
/// The highest single reading. The one that matters most for a stutter: an average hides the
/// spike that was actually felt, and the spike is the thing worth chasing.
/// </param>
public readonly record struct CostSummary(double Best, double Typical, double Worst, double NinetyFifth, int Samples);

/// <summary>
/// Keeps what every read cost, so a whole map can be looked at afterwards.
/// </summary>
/// <remarks>
/// A live number answers "is it slow right now", which is the question you can already answer
/// by looking at the screen. The useful questions are the other ones - did it get worse as the
/// map filled up, was that stutter the terrain or the entities, does this league mechanic cost
/// something - and none of them can be answered from an instant.
///
/// Kept whole rather than aggregated, because the arithmetic to make a long run fit is not
/// worth its own bugs: a read every thirty milliseconds for twenty minutes is forty thousand
/// samples, and at six numbers each that is under a megabyte. Bucketing would save memory
/// nobody needs and lose the spikes, which are the samples worth keeping.
///
/// Written from the reader thread and read from the renderer, so it locks. Adds happen thirty
/// times a second and reads once a frame; the contention is not measurable and the alternative
/// is a lock-free structure whose bugs would be.
/// </remarks>
public sealed class CostHistory
{
    /// <summary>Samples kept before the oldest is dropped - about twenty minutes of reading.</summary>
    public const int Capacity = 40_000;

    private readonly Lock _gate = new();
    private readonly CostSample[] _samples = new CostSample[Capacity];
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

    /// <summary>Records one read. Called from the reader thread.</summary>
    public void Add(ReadCost cost, uint area, long atMs)
    {
        // A read that cost nothing was not measured - outside the game there is no work to
        // record, and a run of zeroes would flatten every graph it is drawn beside.
        if (cost.TotalMs <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _samples[_next] = new CostSample(atMs, area, cost);
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
    public IReadOnlyList<CostSample> Samples()
    {
        lock (_gate)
        {
            var all = new CostSample[_count];
            int start = _count == Capacity ? _next : 0;
            for (int i = 0; i < _count; i++)
            {
                all[i] = _samples[(start + i) % Capacity];
            }

            return all;
        }
    }

    /// <summary>
    /// One phase as a plain array, for plotting.
    /// </summary>
    /// <param name="area">Only this area's samples, or 0 for everything held.</param>
    public float[] Series(CostPhase phase, uint area = 0)
    {
        IReadOnlyList<CostSample> all = Samples();
        var series = new List<float>(all.Count);

        foreach (CostSample sample in all)
        {
            if (area == 0 || sample.Area == area)
            {
                series.Add((float)Value(sample.Cost, phase));
            }
        }

        return [.. series];
    }

    /// <summary>How a phase behaved, over everything held or over one area.</summary>
    public CostSummary Summarise(CostPhase phase, uint area = 0)
    {
        float[] series = Series(phase, area);
        if (series.Length == 0)
        {
            return default;
        }

        float[] sorted = [.. series.Order()];
        double total = 0;
        foreach (float value in series)
        {
            total += value;
        }

        // The ninety-fifth rather than the mean for the "is it bad" question: a read that is
        // fine nineteen times out of twenty and awful on the twentieth is felt as awful, and
        // an average says it is fine.
        return new CostSummary(
            sorted[0],
            total / series.Length,
            sorted[^1],
            sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))],
            series.Length);
    }

    /// <summary>The areas held, newest first - one entry per map that is still in the buffer.</summary>
    /// <remarks>
    /// The list a person picks from, so it is areas rather than samples: "the map I am in" and
    /// "the one before it" are the two scopes anybody asks for.
    /// </remarks>
    public IReadOnlyList<uint> Areas()
    {
        IReadOnlyList<CostSample> all = Samples();
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

    /// <summary>How long the samples for an area span, in seconds.</summary>
    public double SecondsIn(uint area)
    {
        IReadOnlyList<CostSample> all = Samples();
        long first = 0;
        long last = 0;

        foreach (CostSample sample in all)
        {
            if (area != 0 && sample.Area != area)
            {
                continue;
            }

            first = first == 0 ? sample.AtMs : first;
            last = sample.AtMs;
        }

        return first == 0 ? 0 : (last - first) / 1000.0;
    }

    private static double Value(ReadCost cost, CostPhase phase) => phase switch
    {
        CostPhase.Entities => cost.EntitiesMs,
        CostPhase.Player => cost.PlayerMs,
        CostPhase.Terrain => cost.TerrainMs,
        CostPhase.Maps => cost.MapsMs,
        CostPhase.Other => cost.OtherMs,
        _ => cost.TotalMs,
    };
}
