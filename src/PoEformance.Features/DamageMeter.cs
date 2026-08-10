using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>What one monster is currently taking, for the per-target list.</summary>
/// <param name="Name">What to call it on screen.</param>
/// <param name="Dps">Its own smoothed rate.</param>
/// <param name="Percent">How much of its pool is left, 0-100, or -1 when unreadable.</param>
/// <param name="Rarity">So the list can put the pack leader at the top.</param>
public readonly record struct DamageTarget(string Name, float Dps, int Percent, ItemRarity Rarity);

/// <summary>
/// How much damage is being done, measured by watching monster health fall.
/// </summary>
/// <remarks>
/// PORTED FROM AuraTracker's <c>controllers/DpsTracker.cs</c> (MordWraith's GameHelper2 port
/// of Skrip's plugin). The estimator is theirs and is kept: per monster, take the drop in
/// (life + shield) between two readings, divide by the time between them, and feed it into an
/// exponential moving average whose alpha is <c>1 - exp(-dt/tau)</c> - which is the form that
/// stays correct when the interval between samples varies, as it does here.
///
/// THREE THINGS HAD TO CHANGE, and each of them is a wrong number if it does not:
///
/// 1. IT IS DRIVEN BY THE READER, NOT THE RENDERER. The plugin calls its tracker from the
///    draw loop, which is the only thread it has. Here the renderer runs at VSync while
///    snapshots refresh at about 30Hz, so half of the frames would resample an UNCHANGED
///    snapshot: no health has moved, so the sample is zero, and the average gets dragged
///    down by readings that are not readings. Sampling where the snapshots are produced
///    gives exactly one sample per actual read.
///
/// 2. THE KILLING BLOW IS COUNTED. A dead monster does not linger at zero health for us to
///    watch - <see cref="CorpseFilter"/> drops it from the snapshot the moment its health
///    hits zero or it reads untargetable - so the last and largest chunk of every kill is
///    invisible to the estimator above, and a monster deleted from full health between two
///    reads is invisible in its entirety. The plugin therefore under-reports, and worst on
///    exactly the builds that kill fastest. So a tracked monster that leaves the snapshot
///    has its last known pool counted as damage. That is a JUDGEMENT, not a measurement -
///    "it vanished" and "we killed it" are not the same statement - so it is separable
///    (<see cref="Credited"/>), and it can be turned off (<see cref="CountKills"/>).
///
/// 3. THE OVERALL FIGURE IS ITS OWN AVERAGE, rather than the sum of the per-target ones.
///    Summing them cannot survive the point above: a kill would spike the total and then
///    take the spike away with the target it belonged to, one read later. One average over
///    the whole tick's damage has no such discontinuity, and it is the number being asked
///    for anyway - "how hard am I hitting", not "how hard am I hitting each of these".
///
/// Nothing here reads memory. It takes finished snapshots, which is what lets it be tested
/// against made-up ones rather than against a game.
/// </remarks>
public sealed class DamageMeter
{
    /// <summary>Longest a target stays in the list after its last damage.</summary>
    /// <remarks>
    /// Long enough to survive a pause in a boss fight, short enough that the list is about
    /// what is happening now. A target still being hit refreshes this on every read.
    /// </remarks>
    private const long KeepTargetMs = 6_000;

    private sealed class Tracked
    {
        public int Pool;
        public long StampMs;
        public float Ema;
        public long DamagedMs;
        public string Name = string.Empty;
        public int Percent = -1;
        public ItemRarity Rarity = ItemRarity.Unknown;
        public bool Hurt;
    }

    private readonly Dictionary<uint, Tracked> _tracked = [];
    private readonly List<uint> _gone = [];

    private uint _area;
    private float _overall;

    // When the last reading was taken, or null when there is no baseline to measure against -
    // a fresh area, or the first reading back after a loading screen. Nullable rather than a
    // sentinel of zero, because zero is a perfectly good timestamp and a test that starts
    // there would silently never leave the establishing branch.
    private long? _stampMs;

    /// <summary>
    /// How long the average looks back, in seconds. The plugin's default.
    /// </summary>
    /// <remarks>
    /// Under a second because this is read WHILE fighting: it has to answer "did that do
    /// anything" while the thing that did it is still on screen. Longer reads steadier and
    /// says less.
    /// </remarks>
    public float SmoothingSeconds { get; set; } = 0.7f;

    /// <summary>
    /// Count a vanished monster's remaining pool as damage. See the type remarks.
    /// </summary>
    /// <remarks>
    /// On by default, because off is not the conservative choice it looks like: without it
    /// the reading is not "damage we are sure about", it is a number that falls as the build
    /// gets better, which is worse than no number at all. Off is for checking how much of a
    /// figure rests on the judgement rather than on watching health fall.
    /// </remarks>
    public bool CountKills { get; set; } = true;

    /// <summary>Damage per second, smoothed - the headline figure.</summary>
    public float Dps => _overall;

    /// <summary>The highest <see cref="Dps"/> reached in this area.</summary>
    public float Peak { get; private set; }

    /// <summary>Total damage watched fall off monster health in this area.</summary>
    public long Observed { get; private set; }

    /// <summary>Total damage credited from monsters that vanished. The judged part.</summary>
    public long Credited { get; private set; }

    /// <summary>Monsters whose pool has been seen to fall in this area.</summary>
    public int Hurt { get; private set; }

    /// <summary>Monsters that vanished after being tracked - kills, as far as this can tell.</summary>
    public int Vanished { get; private set; }

    /// <summary>Everything counted in this area, however it was counted.</summary>
    public long Total => Observed + Credited;

    /// <summary>Whether anything has been measured in this area at all.</summary>
    public bool Measuring => Total > 0;

    /// <summary>
    /// What is being hit right now, hardest first.
    /// </summary>
    /// <remarks>
    /// Built on demand rather than kept, because it is read by whoever is drawing and written
    /// by the reader thread - and a list rebuilt in place is one the renderer can be walking
    /// while it changes. Only targets that have actually taken damage: a list of every
    /// monster on screen is the health-bar layer's job, and this one answers a narrower
    /// question.
    /// </remarks>
    public IReadOnlyList<DamageTarget> Targets(long nowMs)
    {
        var listed = new List<DamageTarget>();
        foreach (Tracked target in _tracked.Values)
        {
            if (target.Hurt && nowMs - target.DamagedMs <= KeepTargetMs)
            {
                listed.Add(new DamageTarget(target.Name, target.Ema, target.Percent, target.Rarity));
            }
        }

        listed.Sort((left, right) => right.Dps.CompareTo(left.Dps));
        return listed;
    }

    /// <summary>Puts everything back, as if the area had just been entered.</summary>
    public void Forget()
    {
        _tracked.Clear();
        _gone.Clear();
        _stampMs = null;
        _overall = 0f;
        Peak = 0f;
        Observed = 0;
        Credited = 0;
        Hurt = 0;
        Vanished = 0;
    }

    /// <summary>Takes one snapshot and moves the figures on.</summary>
    /// <param name="nowMs">
    /// A monotonic clock in milliseconds. Passed in rather than read here so a test can hand
    /// over a fight that takes four seconds without taking four seconds.
    /// </param>
    public void Look(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Before anything is counted. Every monster in the old area is about to be missing
        // from this snapshot, and crediting that as a few hundred kills is the one way this
        // could produce a number that is not merely wrong but absurd.
        if (snapshot.AreaHash != _area)
        {
            _area = snapshot.AreaHash;
            Forget();
        }

        if (!snapshot.InGame)
        {
            // A loading screen is not a lull in the fighting. Holding the state without
            // advancing the clock means the average resumes where it left off rather than
            // decaying to zero across a portal and back - and dropping the baseline means
            // the first reading on the way back is not read as ten seconds of nothing.
            _stampMs = null;
            return;
        }

        // The first snapshot in an area establishes where every pool started; there is no
        // earlier reading to take a difference against, so nothing is counted from it.
        if (_stampMs is not long since)
        {
            _stampMs = nowMs;
            Absorb(snapshot, nowMs);
            return;
        }

        float dt = (nowMs - since) / 1000f;
        if (dt <= 0f)
        {
            // Two reads inside the same millisecond. Taking the difference would divide by
            // zero; skipping keeps the pools as they were, so the damage lands on the next
            // reading instead of being lost.
            return;
        }

        _stampMs = nowMs;
        long dealt = Damage(snapshot, nowMs, dt);
        Advance(dealt, dt);
    }

    /// <summary>Notes every monster's pool without counting any damage.</summary>
    private void Absorb(WorldSnapshot snapshot, long nowMs)
    {
        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (Worth(monster))
            {
                Remember(monster, nowMs);
            }
        }
    }

    /// <summary>
    /// Counts what fell off every monster's pool since the last reading.
    /// </summary>
    /// <remarks>
    /// One pass over the snapshot to take the differences, then one over what is being
    /// tracked to find what is no longer there. The second pass is what catches kills, and
    /// it is why this cannot be done from the entity list alone: the interesting monster is
    /// the one that is missing from it.
    /// </remarks>
    private long Damage(WorldSnapshot snapshot, long nowMs, float dt)
    {
        long dealt = 0;
        float tau = MathF.Max(0.1f, SmoothingSeconds);
        float alpha = 1f - MathF.Exp(-dt / tau);

        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (!Worth(monster))
            {
                continue;
            }

            int pool = Pool(monster);

            if (!_tracked.TryGetValue(monster.Id, out Tracked? target))
            {
                Remember(monster, nowMs);
                continue;
            }

            // Negative means it healed or gained a shield, which is not damage taken away -
            // the pool moves to the new value either way, so the next drop is measured from
            // where the monster actually is rather than from a high-water mark.
            int fell = target.Pool - pool;
            target.Pool = pool;
            target.Percent = monster.Life.Percent;

            // The stamp IS the liveness test: anything still carrying an older one was not
            // in this snapshot, which is how a kill is noticed. A timestamp rather than a
            // bool, so nothing has to be cleared in a pass of its own.
            target.StampMs = nowMs;

            if (fell > 0)
            {
                dealt += fell;
                Observed += fell;
                target.DamagedMs = nowMs;

                if (!target.Hurt)
                {
                    target.Hurt = true;
                    Hurt++;
                }
            }

            target.Ema += alpha * ((fell > 0 ? fell / dt : 0f) - target.Ema);
        }

        return dealt + Reap(snapshot, nowMs);
    }

    /// <summary>Credits the pool of everything that was being tracked and is now gone.</summary>
    private long Reap(WorldSnapshot snapshot, long nowMs)
    {
        _gone.Clear();
        foreach ((uint id, Tracked target) in _tracked)
        {
            if (target.StampMs != nowMs)
            {
                _gone.Add(id);
            }
        }

        long credited = 0;
        foreach (uint id in _gone)
        {
            Tracked target = _tracked[id];
            _tracked.Remove(id);
            Vanished++;

            if (CountKills && target.Pool > 0)
            {
                credited += target.Pool;
                Credited += target.Pool;
            }
        }

        return credited;
    }

    /// <summary>Moves the overall average on by one reading.</summary>
    private void Advance(long dealt, float dt)
    {
        float tau = MathF.Max(0.1f, SmoothingSeconds);
        float alpha = 1f - MathF.Exp(-dt / tau);

        _overall += alpha * ((dealt / dt) - _overall);

        if (_overall > Peak)
        {
            Peak = _overall;
        }
    }

    private void Remember(WorldEntity monster, long nowMs)
    {
        _tracked[monster.Id] = new Tracked
        {
            Pool = Pool(monster),
            StampMs = nowMs,
            Name = monster.ShortName,
            Percent = monster.Life.Percent,
            Rarity = monster.Rarity,
        };
    }

    /// <summary>
    /// Whether a monster's health is worth watching.
    /// </summary>
    /// <remarks>
    /// The health-bar layer's filter, for the same reasons: friendly things are your own
    /// minions and totems, whose health falling is damage being done TO you, and an effect
    /// wearing a monster's components is not something anybody is fighting. An unreadable
    /// pool is excluded rather than treated as zero - a component that did not resolve would
    /// otherwise read as a monster deleted from full health.
    /// </remarks>
    private static bool Worth(WorldEntity monster)
        => monster.Kind == EntityKind.Monster
           && !monster.IsFriendly
           && !monster.IsEffect
           && monster.Life.IsValid;

    /// <summary>Everything that has to be taken off before a monster dies.</summary>
    private static int Pool(WorldEntity monster)
        => Math.Max(0, monster.Life.Current) + Math.Max(0, monster.EnergyShield.Current);
}
