using PoEformance.Game.Components;
using PoEformance.Game.Ui;
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

    /// <summary>
    /// World units across one screen.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="MapCoverage.SeeRadius"/>, which is 22 coarse cells of 4 grid
    /// cells at <see cref="MapView.WorldToGrid"/> units each - about 957 units, and its own
    /// comment calls that "roughly what fits on screen". Doubled here because that figure is
    /// a radius and this is a width.
    ///
    /// THIS IS APPROXIMATE, and the codebase does not agree with itself about it: the default
    /// "Rare monster" alert uses 120 units and calls that "within a screen", which is eight
    /// times smaller and would be about a character and a half. The coverage figure is the
    /// one trusted here because it is load-bearing - it sizes the sight disc behind a walked
    /// percentage that would be visibly wrong if the radius were eight times off, whereas the
    /// alert distance was tuned by feel and its comment is loose. Anything resting on this
    /// number should be adjustable and should show its effect, which is what the gate below
    /// does.
    /// </remarks>
    public const float ScreenWorldUnits = 2f * MapCoverage.SeeRadius * MapCoverage.CoarseStep * MapView.WorldToGrid;

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

        // How far from the player this was the last time it was SEEN, or -1 when there was no
        // player to measure against. At the last sighting rather than at the moment it went
        // missing, because the moment it went missing is the moment there is nothing left to
        // measure - and it is the right question anyway: a monster killed in front of you was
        // last seen close, one that dropped off the entity list because you left was last
        // seen far.
        public float Distance = -1f;
    }

    private readonly Dictionary<uint, Tracked> _tracked = [];
    private readonly List<uint> _gone = [];

    private uint _area;
    private float _overall;

    // What the three running totals stood at when the last sample was written down, and when
    // that was. Differences against these are what a sample IS - the meter's own totals only
    // ever grow, and a graph of a growing total is a ramp.
    //
    // NULLABLE for the same reason _stampMs above is, and this repeated that mistake before
    // the tests caught it: zero is a perfectly good timestamp, so a sentinel of zero means a
    // clock that starts there never leaves the establishing branch.
    private long? _sampledAtMs;
    private long _sampledObserved;
    private long _sampledHurt;
    private long _sampledUntouched;

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

    /// <summary>
    /// Only credit a vanished monster if it was last seen this close. 0 credits any distance.
    /// </summary>
    /// <remarks>
    /// The gate on the one way this can be badly wrong. Crediting a vanished monster assumes
    /// it died, and the assumption fails for a monster that left the entity list without
    /// dying - walked away from, or dropped because the player went far enough that the game
    /// stopped keeping it. Those are the ones that were last seen a long way off, so distance
    /// separates them from kills without needing to know why the game drops entities.
    ///
    /// It was two screens, chosen to be generous because the cost of the two mistakes is not
    /// symmetric. Then a whole map was recorded and the setting was judged against it rather
    /// than argued about, which is what <see cref="Withheld"/> exists for - and two screens
    /// turned out to refuse NOTHING while letting through four times more credit than the
    /// damage anybody watched fall.
    ///
    /// Sorted by how far away each vanishing was, on 321 seconds of one map:
    ///
    /// <code>
    ///   world units    grid       hurt then vanished     vanished untouched
    ///      0 -  250    0 -  23     7      58,318          6         78,898
    ///    250 -  500   23 -  46    17     215,933         10        205,434
    ///    500 - 1000   46 -  92    37     363,367         21        409,830
    ///   1000 - 1500   92 - 138     8      84,619          5         80,605
    ///   1500 - 2000  138 - 184     1       4,211         36      1,176,856
    ///   2000 - 2500  184 - 230     2      60,894         93      6,410,017
    ///   2500 - 3000  230 - 276     -           -         45      2,878,998
    ///   3000 - 4000  276 - 368     -           -          8        322,567
    /// </code>
    ///
    /// Two populations, and they barely overlap. Monsters that were HURT and then went are
    /// almost all inside 1500 units - 69 of 72 - which is what killing something looks like.
    /// Monsters that went having never lost a point cluster at 1500 to 3000, which is where
    /// the game stops keeping entities: 182 of 224 of them, carrying 10.8 million of pool.
    /// The most common one by name was VaalTrainingDummyIncursion, thirty-four times, which
    /// nobody killed at all.
    ///
    /// So the gate sits in the gap between them. At 0.8 screens it keeps 92% of the credit
    /// for monsters that were actually damaged and refuses 93% of the credit for monsters
    /// that never were.
    /// </remarks>
    public float CreditWithin { get; set; } = 0.8f * ScreenWorldUnits;

    /// <summary>Damage per second, smoothed - the headline figure.</summary>
    public float Dps => _overall;

    /// <summary>The highest <see cref="Dps"/> reached in this area.</summary>
    public float Peak { get; private set; }

    /// <summary>
    /// What the damage was doing over the map, kept so it can be looked at as a shape.
    /// </summary>
    /// <remarks>
    /// Owned here rather than serviced separately because this is the only place that knows
    /// both when a reading happened and which area it was in - a second service call would be
    /// a second clock to keep in step with this one.
    /// </remarks>
    public DamageHistory History { get; } = new();

    /// <summary>Total damage watched fall off monster health in this area.</summary>
    public long Observed { get; private set; }

    /// <summary>
    /// Credit from monsters that vanished AFTER being seen to take damage.
    /// </summary>
    /// <remarks>
    /// The confident half of the judgement: something was hitting it, and then it was gone.
    /// Kept apart from <see cref="CreditedUntouched"/> because the two are different claims
    /// wearing the same number, and only one of them is really in doubt.
    /// </remarks>
    public long CreditedHurt { get; private set; }

    /// <summary>
    /// Credit from monsters that vanished without ever being seen to take damage.
    /// </summary>
    /// <remarks>
    /// The half that rests entirely on the assumption. It is NOT noise to be discarded - a
    /// monster deleted from full health between two reads lands here, and on a build that
    /// one-shots packs that is most of the damage done. But it is also where a monster that
    /// simply left would land, which is why it is counted separately and gated by distance.
    /// </remarks>
    public long CreditedUntouched { get; private set; }

    /// <summary>Total damage credited from monsters that vanished. The judged part.</summary>
    public long Credited => CreditedHurt + CreditedUntouched;

    /// <summary>Pool the distance gate refused to credit, so the gate can be judged.</summary>
    public long Withheld { get; private set; }

    /// <summary>Monsters the distance gate refused.</summary>
    public int WithheldCount { get; private set; }

    /// <summary>Monsters whose pool has been seen to fall in this area.</summary>
    public int Hurt { get; private set; }

    /// <summary>Monsters that vanished after being tracked - kills, as far as this can tell.</summary>
    public int Vanished { get; private set; }

    /// <summary>
    /// The greatest distance at which anything has gone missing, in world units. -1 for none.
    /// </summary>
    /// <remarks>
    /// THE MEASUREMENT THAT SETTLES WHETHER THE GATE IS NEEDED AT ALL. Crediting a vanished
    /// monster assumes it died, and the one way that fails is a monster leaving the entity
    /// list without dying - which would show up here as something going missing a long way
    /// off. If this stays small, every disappearance happened in fighting range, and there
    /// is nothing for the gate to catch; if it runs to several screens, the game does drop
    /// distant monsters and the gate is load-bearing.
    ///
    /// Recorded for EVERY vanish, before the gate and regardless of whether kills are being
    /// counted, because it is evidence rather than part of the figure - a diagnostic that
    /// only reported what the current settings already accepted could never contradict them.
    /// </remarks>
    public float FurthestVanish { get; private set; } = -1f;

    /// <summary>
    /// The greatest distance at which a vanish was COUNTED as a kill. -1 for none.
    /// </summary>
    /// <remarks>
    /// The other half of the bracket, and the one that says whether the gate is set too
    /// tight. <see cref="FurthestVanish"/> shows how far the disappearances reach;
    /// this shows how far the ones being believed reach. Read together they place the
    /// threshold in the distribution rather than leaving it a guess:
    ///
    ///   - this figure well BELOW the gate, with the furthest well above it - the two
    ///     populations are cleanly separated and the gate sits in the gap between them.
    ///   - this figure pressed right UP against the gate - the gate is cutting into the
    ///     distribution rather than between two, and some of what it refuses is likely to
    ///     be kills.
    ///
    /// Which of those is true cannot be reasoned out from here. It is a fact about how far
    /// away this character kills things, and it is different for a bow than for a hammer.
    /// </remarks>
    public float FurthestCounted { get; private set; } = -1f;

    /// <summary>
    /// The greatest distance any monster has been SEEN at, in world units. -1 for none.
    /// </summary>
    /// <remarks>
    /// THE BUBBLE, MEASURED HERE RATHER THAN QUOTED. The game only replicates entities within
    /// some radius of the player - GameHelper2 calls it the network bubble and puts it near
    /// 200 grid units, measured by watching when entities leave, with a note that it differs
    /// by entity type. Everything that vanishes at that edge is alive and walking home; only
    /// what vanishes well inside it can have died.
    ///
    /// Which makes this the denominator the vanish distance was missing. On its own,
    /// "the furthest thing to go missing was 255 grid away" says nothing - it is far only in
    /// relation to how far the list reaches at all. Next to this, it becomes the one
    /// comparison that matters: vanishes crowding the edge are exits, vanishes well inside it
    /// are deaths.
    ///
    /// Taken from every sighting rather than from vanishes, so it fills in from ordinary play
    /// and does not need anything to die.
    /// </remarks>
    public float FurthestSeen { get; private set; } = -1f;

    /// <summary>
    /// How close to the edge of the list the furthest disappearance was, 0 to 1. -1 unknown.
    /// </summary>
    /// <remarks>
    /// Near 1 means things go missing where the list ends, which is what leaving the bubble
    /// looks like. Well under 1 means they go missing in the middle of it, where the only
    /// thing that removes a monster is dying.
    /// </remarks>
    public float VanishedAtEdge => FurthestSeen > 0f && FurthestVanish >= 0f
        ? FurthestVanish / FurthestSeen
        : -1f;

    /// <summary>
    /// Where the credited damage actually comes from: the pool-weighted mean distance of the
    /// vanishes it was credited for, in world units. -1 when nothing has been credited.
    /// </summary>
    /// <remarks>
    /// THE FIGURE THAT DECIDES, and the one the two "furthest" numbers above cannot give.
    /// Those compare two MAXIMA, and maxima coincide for free: the monster seen furthest away
    /// leaves the list eventually, and if the player walked away from it its last sighting is
    /// at that same distance. A ratio of 1 therefore only says something once left at the
    /// edge, which happens constantly while walking a map, and says nothing at all about
    /// where the damage was booked.
    ///
    /// Weighted by POOL rather than counted per vanish, because the question is about the
    /// figure and not about the population: one boss credited at arm's length outweighs a
    /// hundred trash monsters that drifted off the edge, and a plain average would report the
    /// opposite.
    ///
    /// Read against <see cref="FurthestSeen"/>: a mean well inside the reach means the credit
    /// is coming from monsters that died in front of the player, and a mean crowding the
    /// reach means it is coming from monsters that walked out of the list alive.
    /// </remarks>
    public float CreditedMeanDistance => _creditedPoolWithDistance > 0
        ? (float)(_creditedDistanceByPool / _creditedPoolWithDistance)
        : -1f;

    // Sum of (pool * distance) and of pool, over credited vanishes whose distance was known.
    private double _creditedDistanceByPool;
    private long _creditedPoolWithDistance;

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
        CreditedHurt = 0;
        CreditedUntouched = 0;
        Withheld = 0;
        WithheldCount = 0;
        Hurt = 0;
        Vanished = 0;
        FurthestVanish = -1f;
        FurthestCounted = -1f;
        FurthestSeen = -1f;
        _creditedDistanceByPool = 0;
        _creditedPoolWithDistance = 0;

        // The baselines, but NOT the history: the totals restart at nought in a new area, so
        // leaving them where they were would make the first sample of the map the whole of the
        // last one, negated. What was recorded stays - it is filed by area, and the map before
        // this one is a thing somebody wants to look back at.
        _sampledAtMs = null;
        _sampledObserved = 0;
        _sampledHurt = 0;
        _sampledUntouched = 0;
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

            // And the graph's stretch with it, for the same reason. Held, the next sample
            // would cover the whole loading screen - a bar several seconds wide reported as a
            // quarter second's worth of nothing.
            _sampledAtMs = null;
            return;
        }

        // The first snapshot in an area establishes where every pool started; there is no
        // earlier reading to take a difference against, so nothing is counted from it.
        if (_stampMs is not long since)
        {
            _stampMs = nowMs;
            Absorb(snapshot, nowMs);

            // The graph's first stretch starts HERE, at the area's first reading. Left until
            // the second one, the first sample would carry the damage of the interval before
            // it as well - measured against a baseline of nought that was never true.
            Record(nowMs, snapshot);
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
        Record(nowMs, snapshot);
    }

    /// <summary>
    /// How far a monster counts as "around" for the census. About a screen's radius.
    /// </summary>
    /// <remarks>
    /// HALF of <see cref="ScreenWorldUnits"/>, which is that figure's own see-radius before it
    /// was doubled into a width - so this is a radius measured as a radius, unlike the credit
    /// gate, which is a distance bound expressed in that same doubled figure. Said here because
    /// the two numbers look like they should agree and do not.
    ///
    /// Not adjustable, unlike the credit gate: nothing rests on it. A census that is a little
    /// wide or a little narrow is still a fair account of what a bar was fought against,
    /// whereas the gate decides whether damage is counted at all.
    /// </remarks>
    public const float NearbyWorldUnits = ScreenWorldUnits / 2f;

    /// <summary>
    /// Writes down what the last stretch did, once the stretch is long enough to be one.
    /// </summary>
    /// <remarks>
    /// The three parts are taken as DIFFERENCES of the running totals rather than accumulated
    /// alongside them, which keeps this honest by construction: whatever the counting above
    /// decides - a credit refused for distance, a monster that came back - the graph shows the
    /// same figure the readout does, because it is made of the same numbers.
    /// </remarks>
    private void Record(long nowMs, WorldSnapshot snapshot)
    {
        // No baseline yet - the first reading of an area. Take one, TOTALS AND ALL: a baseline
        // that remembers only the time measures the next stretch against nought, which reads
        // as every point of damage since the area loaded happening in one quarter second.
        if (_sampledAtMs is not long from)
        {
            Baseline(nowMs);
            return;
        }

        long span = nowMs - from;
        if (span < DamageHistory.IntervalMs)
        {
            return;
        }

        float seconds = span / 1000f;
        History.Add(new DamageSample(
            nowMs,
            _area,
            (Observed - _sampledObserved) / seconds,
            (CreditedHurt - _sampledHurt) / seconds,
            (CreditedUntouched - _sampledUntouched) / seconds,
            Census(snapshot),
            span));

        Baseline(nowMs);
    }

    /// <summary>What is around right now, by rarity.</summary>
    /// <remarks>
    /// Through the SAME filter the damage goes through, so the census is an account of the
    /// monsters this figure is about rather than of everything in the entity list - effects,
    /// friendly things and corpses are not what a bar was fought against.
    ///
    /// With no player there is no distance to measure, so everything the list holds is counted.
    /// That is the honest degradation: the list is already a bubble around the player, so it is
    /// the same question asked with a coarser radius rather than a different one.
    /// </remarks>
    private static MonsterCensus Census(WorldSnapshot snapshot)
    {
        int normal = 0;
        int magic = 0;
        int rare = 0;
        int unique = 0;

        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (!Worth(monster))
            {
                continue;
            }

            float away = Away(monster, snapshot.Player);
            if (away >= 0f && away > NearbyWorldUnits)
            {
                continue;
            }

            switch (monster.Rarity)
            {
                case ItemRarity.Magic:
                    magic++;
                    break;
                case ItemRarity.Rare:
                    rare++;
                    break;

                // Rarity 3 AND ABOVE, the same rule the read uses when it decides a monster is
                // a boss. A rarity nobody has seen before reads as the rarest thing rather
                // than as an ordinary monster, which is the safer way for it to be wrong.
                case >= ItemRarity.Unique:
                    unique++;
                    break;

                // Unknown lands here with Normal on purpose: a monster whose properties did
                // not read is still a monster in the way, and leaving it out would make the
                // count disagree with what is on the screen.
                default:
                    normal++;
                    break;
            }
        }

        return new MonsterCensus(normal, magic, rare, unique);
    }

    /// <summary>Marks where the next stretch starts, in time and in all three totals.</summary>
    private void Baseline(long nowMs)
    {
        _sampledAtMs = nowMs;
        _sampledObserved = Observed;
        _sampledHurt = CreditedHurt;
        _sampledUntouched = CreditedUntouched;
    }

    /// <summary>Notes every monster's pool without counting any damage.</summary>
    private void Absorb(WorldSnapshot snapshot, long nowMs)
    {
        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (Worth(monster))
            {
                Remember(monster, snapshot.Player, nowMs);
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
                Remember(monster, snapshot.Player, nowMs);
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
            target.Distance = Away(monster, snapshot.Player);

            // Every sighting, not just the ones that end in something going missing - this is
            // how far the entity list reaches, and it fills in from walking around.
            if (target.Distance > FurthestSeen)
            {
                FurthestSeen = target.Distance;
            }

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

        return dealt + Reap(nowMs);
    }

    /// <summary>Credits the pool of everything that was being tracked and is now gone.</summary>
    private long Reap(long nowMs)
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

            // Before every other decision below, so the evidence is not filtered by the
            // settings it exists to judge.
            if (target.Distance > FurthestVanish)
            {
                FurthestVanish = target.Distance;
            }

            if (!CountKills || target.Pool <= 0)
            {
                continue;
            }

            // A distance of -1 means there was no player to measure against, so the gate has
            // nothing to judge on. It lets the credit through rather than refusing it: the
            // gate exists to exclude a specific, identifiable mistake, and "unknown" is not
            // that mistake - refusing here would silently drop damage that was really dealt.
            if (CreditWithin > 0f && target.Distance >= 0f && target.Distance > CreditWithin)
            {
                Withheld += target.Pool;
                WithheldCount++;
                continue;
            }

            credited += target.Pool;
            if (target.Distance > FurthestCounted)
            {
                FurthestCounted = target.Distance;
            }

            // Only where the distance is known; an unmeasured one would otherwise pull the
            // mean toward zero and make the credit look closer than it was.
            if (target.Distance >= 0f)
            {
                _creditedDistanceByPool += (double)target.Pool * target.Distance;
                _creditedPoolWithDistance += target.Pool;
            }

            if (target.Hurt)
            {
                CreditedHurt += target.Pool;
            }
            else
            {
                CreditedUntouched += target.Pool;
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

    private void Remember(WorldEntity monster, WorldEntity? player, long nowMs)
    {
        _tracked[monster.Id] = new Tracked
        {
            Pool = Pool(monster),
            StampMs = nowMs,
            Name = monster.ShortName,
            Percent = monster.Life.Percent,
            Rarity = monster.Rarity,
            Distance = Away(monster, player),
        };
    }

    /// <summary>
    /// How far a monster is from the player on the ground, or -1 with no player to measure to.
    /// </summary>
    /// <remarks>
    /// Flat, ignoring height, because the question is "is this near me" and a monster on the
    /// gantry above counts as near. That is also what every other distance in this tool
    /// measures - the alert rules and the entity browser both take it in the XY plane - and a
    /// distance that meant something different here would be the more surprising choice.
    /// </remarks>
    private static float Away(WorldEntity monster, WorldEntity? player)
    {
        if (player is not WorldEntity at)
        {
            return -1f;
        }

        float dx = monster.WorldX - at.WorldX;
        float dy = monster.WorldY - at.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
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
