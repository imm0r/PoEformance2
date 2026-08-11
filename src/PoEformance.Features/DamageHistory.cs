namespace PoEformance.Features;

/// <summary>
/// How many monsters were around, by what kind of monster they are.
/// </summary>
/// <remarks>
/// WHAT THE BAR WAS AGAINST. A damage rate on its own does not say whether it was a good
/// moment: five thousand into a rare is a build working and five thousand into forty white
/// monsters is a build that cannot single-target. The census is what turns the graph from a
/// record of what happened into something that can be read.
///
/// The game's own four rarities rather than invented tiers. There IS a boss flag on the
/// monster component, and it is marked in the schema as an unverified hypothesis - so it is
/// not what a readout gets built on. Unique covers the bosses, which is how the rest of this
/// codebase already treats rarity 3 and above.
/// </remarks>
public readonly record struct MonsterCensus(int Normal, int Magic, int Rare, int Unique)
{
    /// <summary>Every monster counted.</summary>
    public int All => Normal + Magic + Rare + Unique;

    /// <summary>Whether anything was counted at all.</summary>
    public bool Any => All > 0;
}

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
/// <param name="Nearby">
/// What was around at that moment, by rarity. Taken at the instant the sample was written
/// rather than averaged over its stretch: "how many were there" is a thing you point at, and
/// an average of a quarter second's monsters is a number that was never true.
/// </param>
/// <param name="SpanMs">
/// How long this sample covers. Carried rather than worked out from the gap to the one before
/// it, because that gap is not the span whenever there is a hole - a loading screen, a change
/// of area, a stretch the buffer has dropped - and a window summed across such a hole would
/// report a burst that never happened.
/// </param>
public readonly record struct DamageSample(
    long AtMs,
    uint Area,
    float Watched,
    float Credited,
    float Untouched,
    MonsterCensus Nearby = default,
    long SpanMs = DamageHistory.IntervalMs)
{
    /// <summary>All of it - the height of one bar on the graph.</summary>
    public float Total => Watched + Credited + Untouched;

    /// <summary>When this sample's stretch began.</summary>
    public long FromMs => AtMs - SpanMs;

    /// <summary>The damage itself, rather than the rate - what a window has to add up.</summary>
    public float Dealt => Total * SpanMs / 1000f;
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

    /// <summary>
    /// The most damage per second sustained over any stretch this long.
    /// </summary>
    /// <remarks>
    /// THE NUMBER BUILDS ARE COMPARED BY, and the one the meter did not have. <c>Peak</c> is the
    /// high-water mark of an exponential average with a sub-second half-life: it is a smoothed
    /// figure, so it under-reports a real burst by however much the smoothing flattened it, and
    /// it MOVES WHEN THE SMOOTHING SLIDER DOES. Two builds measured at different slider
    /// positions cannot be compared by it at all, which is most of what anybody wants a peak
    /// for.
    ///
    /// This asks the question the other way round: over any window of this length, what is the
    /// most damage that actually landed? No smoothing, nothing to configure, and the answer
    /// means the same thing on every machine.
    ///
    /// WINDOWS NEVER SPAN A HOLE. Samples carry their own span, so a stretch the buffer dropped,
    /// a loading screen or a change of area leaves a gap - and adding across one would report a
    /// burst made of two separate fights with the pause between them deleted.
    /// </remarks>
    /// <param name="seconds">How long the window is. One, five and ten are the useful ones.</param>
    public float Best(double seconds, uint area = 0)
    {
        if (seconds <= 0)
        {
            return 0f;
        }

        IReadOnlyList<DamageSample> mine = In(area);
        long want = (long)(seconds * 1000);
        float best = 0f;

        // A window is [left, right]. It grows on the right, and gives up ground on the left
        // only while what is left is still long enough to be the window that was asked for.
        int left = 0;
        float dealt = 0f;
        long span = 0;

        for (int right = 0; right < mine.Count; right++)
        {
            // A gap means the two sides are not one stretch of fighting. Start again from here
            // rather than adding across it.
            if (right > left && mine[right].FromMs - mine[right - 1].AtMs > IntervalMs)
            {
                left = right;
                dealt = 0f;
                span = 0;
            }

            dealt += mine[right].Dealt;
            span += mine[right].SpanMs;

            while (left < right && span - mine[left].SpanMs >= want)
            {
                dealt -= mine[left].Dealt;
                span -= mine[left].SpanMs;
                left++;
            }

            // Only once the window is actually as long as asked. A shorter one would divide a
            // burst by less time than it was measured over and report a rate nobody sustained.
            if (span >= want)
            {
                best = MathF.Max(best, dealt / (span / 1000f));
            }
        }

        return best;
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
