using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// How much damage is being done, measured by watching monster health fall.
/// </summary>
/// <remarks>
/// The estimator comes from AuraTracker's DpsTracker. What these tests are mostly about is
/// the part that could not be ported as it stood: a dead monster is dropped from the snapshot
/// rather than lingering at zero health, so the last and largest chunk of every kill is
/// invisible to the estimator, and a monster deleted from full health between two reads is
/// invisible entirely. A port that missed that would report a figure that falls as the build
/// gets better, which is the failure that looks most like success.
/// </remarks>
public class DamageMeterTests
{
    /// <summary>A monster with a life pool, and optionally a shield.</summary>
    private static WorldEntity Monster(
        uint id, int life, int max = 1000, int shield = 0, int shieldMax = 0, float away = 0f)
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: $"Metadata/Monsters/Test{id}",
            Kind: EntityKind.Monster,
            WorldX: away,
            WorldY: 0f,
            WorldZ: 0f,
            Life: new Vital(life, max, 0, 0),
            EnergyShield: new Vital(shield, shieldMax, 0, 0),
            Name: $"Test {id}");

    /// <summary>The player, at the origin - so a monster's WorldX is its distance.</summary>
    private static WorldEntity Player()
        => new(
            Id: 999,
            Address: 0x999,
            Path: "Metadata/Player",
            Kind: EntityKind.Player,
            WorldX: 0f,
            WorldY: 0f,
            WorldZ: 0f,
            Life: new Vital(100, 100, 0, 0));

    private static WorldSnapshot Area(uint hash, params WorldEntity[] entities)
        => new(true, null, entities, new float[16], AreaHash: hash);

    /// <summary>An area with a player in it, which the distance gate needs to measure from.</summary>
    private static WorldSnapshot Seen(uint hash, params WorldEntity[] entities)
        => new(true, Player(), entities, new float[16], AreaHash: hash);

    /// <summary>
    /// A pool that falls by a known amount over a known time reads as that rate.
    /// </summary>
    /// <remarks>
    /// The average needs several readings to climb, so this drives a steady 1000-per-second
    /// for long enough to converge rather than asserting on the first sample - an exponential
    /// average is defined by where it settles, not by where it starts.
    /// </remarks>
    [Fact]
    public void SteadyDamageConvergesOnTheRate()
    {
        var meter = new DamageMeter();
        int life = 100_000;

        meter.Look(Area(1, Monster(1, life, max: 100_000)), 0);

        for (long now = 100; now <= 5_000; now += 100)
        {
            life -= 100; // 100 per 100ms = 1000 per second
            meter.Look(Area(1, Monster(1, life, max: 100_000)), now);
        }

        Assert.InRange(meter.Dps, 950f, 1050f);
        Assert.Equal(5_000, meter.Observed);
    }

    /// <summary>Nothing is counted from the first snapshot - there is nothing to compare it to.</summary>
    [Fact]
    public void TheFirstReadingOnlyEstablishesThePools()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 500)), 0);

        Assert.Equal(0, meter.Total);
        Assert.Equal(0f, meter.Dps);
        Assert.False(meter.Measuring);
    }

    /// <summary>A monster that vanishes has its remaining pool counted as the killing blow.</summary>
    /// <remarks>
    /// The case the reference cannot see at all: a monster deleted from full health between
    /// two reads never shows a single falling reading, so an estimator watching only for
    /// falls reports zero for it.
    /// </remarks>
    [Fact]
    public void AMonsterDeletedFromFullHealthIsStillCounted()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 4_000, max: 4_000)), 0);
        meter.Look(Area(1), 100);

        Assert.Equal(4_000, meter.Credited);
        Assert.Equal(0, meter.Observed);
        Assert.Equal(4_000, meter.Total);
        Assert.Equal(1, meter.Vanished);
        Assert.True(meter.Dps > 0f);
    }

    /// <summary>Both halves of a kill are counted: what was watched, and what was left.</summary>
    [Fact]
    public void TheWatchedFallAndTheRemainderAreBothCounted()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 1_000)), 0);
        meter.Look(Area(1, Monster(1, 600)), 100);   // 400 watched off
        meter.Look(Area(1), 200);                     // 600 left when it vanished

        Assert.Equal(400, meter.Observed);
        Assert.Equal(600, meter.Credited);
        Assert.Equal(1_000, meter.Total);
    }

    /// <summary>Turning the judgement off leaves only what was actually watched.</summary>
    [Fact]
    public void KillCreditCanBeTurnedOff()
    {
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Monster(1, 1_000)), 0);
        meter.Look(Area(1, Monster(1, 600)), 100);
        meter.Look(Area(1), 200);

        Assert.Equal(400, meter.Observed);
        Assert.Equal(0, meter.Credited);
        Assert.Equal(1, meter.Vanished); // still noticed, just not counted
    }

    /// <summary>Shield and life are one pool - damage through a shield counts.</summary>
    [Fact]
    public void ShieldCountsTowardsThePool()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 500, shield: 300, shieldMax: 300)), 0);
        meter.Look(Area(1, Monster(1, 500, shield: 100, shieldMax: 300)), 100);

        Assert.Equal(200, meter.Observed);
    }

    /// <summary>A monster that heals is not negative damage.</summary>
    /// <remarks>
    /// And the pool follows it up, so the next real hit is measured from where the monster
    /// actually is - measuring from a high-water mark would count the healed amount twice.
    /// </remarks>
    [Fact]
    public void HealingCountsAsNothingAndMovesTheBaseline()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 500)), 0);
        meter.Look(Area(1, Monster(1, 900)), 100);
        meter.Look(Area(1, Monster(1, 800)), 200);

        Assert.Equal(100, meter.Observed);
    }

    /// <summary>
    /// Changing area forgets everything, rather than crediting the whole zone as kills.
    /// </summary>
    /// <remarks>
    /// The one way this could produce a number that is not merely wrong but absurd: every
    /// monster in the old area is missing from the first snapshot of the new one.
    /// </remarks>
    [Fact]
    public void ChangingAreaCountsNoKills()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 5_000), Monster(2, 5_000), Monster(3, 5_000)), 0);
        meter.Look(Area(2), 100);

        Assert.Equal(0, meter.Total);
        Assert.Equal(0, meter.Vanished);
        Assert.Equal(0f, meter.Dps);
    }

    /// <summary>A loading screen is not a lull - the average resumes rather than decaying.</summary>
    [Fact]
    public void TimeOutOfTheAreaDoesNotDecayTheAverage()
    {
        var meter = new DamageMeter();
        int life = 100_000;

        meter.Look(Area(1, Monster(1, life, max: 100_000)), 0);
        for (long now = 100; now <= 3_000; now += 100)
        {
            life -= 100;
            meter.Look(Area(1, Monster(1, life, max: 100_000)), now);
        }

        float before = meter.Dps;

        // Ten seconds of not being in an area, same area hash on the way back.
        for (long now = 3_100; now <= 13_000; now += 100)
        {
            meter.Look(new WorldSnapshot(false, null, [], new float[16], AreaHash: 1), now);
        }

        Assert.Equal(before, meter.Dps);
    }

    /// <summary>Friendly things and effects are not targets - their health falling is not our damage.</summary>
    [Fact]
    public void FriendlyThingsAndEffectsAreIgnored()
    {
        var meter = new DamageMeter();

        WorldEntity minion = Monster(1, 1_000) with { IsFriendly = true };
        WorldEntity flameWall = Monster(2, 1_000) with { IsEffect = true };

        meter.Look(Area(1, minion, flameWall), 0);
        meter.Look(Area(1, minion with { Life = new Vital(200, 1_000, 0, 0) }, flameWall), 100);
        meter.Look(Area(1), 200);

        Assert.Equal(0, meter.Total);
    }

    /// <summary>A pool that could not be read is not a monster deleted from full health.</summary>
    [Fact]
    public void AnUnreadablePoolIsNotAKill()
    {
        var meter = new DamageMeter();

        WorldEntity unreadable = Monster(1, 0, max: 0);

        meter.Look(Area(1, unreadable), 0);
        meter.Look(Area(1), 100);

        Assert.Equal(0, meter.Total);
        Assert.Equal(0, meter.Vanished);
    }

    /// <summary>The per-target list holds what has been hit, hardest first.</summary>
    [Fact]
    public void TargetsAreListedHardestFirst()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 10_000, max: 10_000), Monster(2, 10_000, max: 10_000)), 0);
        meter.Look(
            Area(1, Monster(1, 9_900, max: 10_000), Monster(2, 9_000, max: 10_000)),
            100);

        IReadOnlyList<DamageTarget> targets = meter.Targets(100);

        Assert.Equal(2, targets.Count);
        Assert.Equal("Test 2", targets[0].Name);   // took 1000, not 100
        Assert.True(targets[0].Dps > targets[1].Dps);
    }

    /// <summary>A monster nobody has hit is not a target, however long it stands there.</summary>
    [Fact]
    public void UnhurtMonstersAreNotTargets()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 1_000), Monster(2, 1_000)), 0);
        meter.Look(Area(1, Monster(1, 900), Monster(2, 1_000)), 100);

        Assert.Single(meter.Targets(100));
    }

    /// <summary>The peak is the highest the average reached, and it survives the fight ending.</summary>
    [Fact]
    public void ThePeakOutlivesTheFight()
    {
        var meter = new DamageMeter();
        int life = 100_000;

        meter.Look(Area(1, Monster(1, life, max: 100_000)), 0);
        for (long now = 100; now <= 3_000; now += 100)
        {
            life -= 100;
            meter.Look(Area(1, Monster(1, life, max: 100_000)), now);
        }

        float peak = meter.Peak;
        Assert.True(peak > 0f);

        // Nothing happening for a while: the rate decays, the peak does not.
        for (long now = 3_100; now <= 8_000; now += 100)
        {
            meter.Look(Area(1, Monster(1, life, max: 100_000)), now);
        }

        Assert.True(meter.Dps < peak / 10f);
        Assert.Equal(peak, meter.Peak);
    }

    /// <summary>
    /// Credit is split by whether the monster was ever seen taking damage.
    /// </summary>
    /// <remarks>
    /// The two are different claims wearing the same number. "It was being hurt and then it
    /// was gone" is nearly certain; "it was there and then it was gone" is the assumption
    /// under scrutiny, and it is also where a monster that merely left would land.
    /// </remarks>
    [Fact]
    public void CreditIsSplitByWhetherItWasSeenTakingDamage()
    {
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 1_000), Monster(2, 1_000)), 0);
        meter.Look(Seen(1, Monster(1, 400), Monster(2, 1_000)), 100);  // only #1 is hurt
        meter.Look(Seen(1), 200);                                       // both vanish

        Assert.Equal(600, meter.Observed);          // watched off #1
        Assert.Equal(400, meter.CreditedHurt);      // #1's remainder
        Assert.Equal(1_000, meter.CreditedUntouched); // #2, never seen to take a scratch
        Assert.Equal(1_400, meter.Credited);
        Assert.Equal(2_000, meter.Total);
    }

    /// <summary>A monster that vanishes far away is not credited as a kill.</summary>
    /// <remarks>
    /// The gate on the way this can be badly wrong: a monster that left the entity list
    /// without dying. Nothing is killed two screens away, so distance separates those from
    /// kills without needing to know why the game drops entities.
    /// </remarks>
    [Fact]
    public void AMonsterThatVanishesFarAwayIsNotCredited()
    {
        var meter = new DamageMeter();
        float far = (3f * DamageMeter.ScreenWorldUnits) + 1f;

        meter.Look(Seen(1, Monster(1, 5_000, max: 5_000, away: far)), 0);
        meter.Look(Seen(1), 100);

        Assert.Equal(0, meter.Credited);
        Assert.Equal(5_000, meter.Withheld);
        Assert.Equal(1, meter.WithheldCount);
        Assert.Equal(1, meter.Vanished); // still noticed, just not counted
    }

    /// <summary>A monster that vanishes close by is credited.</summary>
    [Fact]
    public void AMonsterThatVanishesNearbyIsCredited()
    {
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 5_000, max: 5_000, away: 200f)), 0);
        meter.Look(Seen(1), 100);

        Assert.Equal(5_000, meter.CreditedUntouched);
        Assert.Equal(0, meter.Withheld);
    }

    /// <summary>The gate can be turned off, and then distance stops mattering.</summary>
    [Fact]
    public void TheDistanceGateCanBeTurnedOff()
    {
        var meter = new DamageMeter { CreditWithin = 0f };
        float far = 100f * DamageMeter.ScreenWorldUnits;

        meter.Look(Seen(1, Monster(1, 5_000, max: 5_000, away: far)), 0);
        meter.Look(Seen(1), 100);

        Assert.Equal(5_000, meter.Credited);
        Assert.Equal(0, meter.Withheld);
    }

    /// <summary>
    /// With no player to measure from, the gate lets credit through rather than refusing it.
    /// </summary>
    /// <remarks>
    /// The gate exists to exclude one identifiable mistake, and "we could not tell" is not
    /// that mistake. Refusing here would silently drop damage that was really dealt.
    /// </remarks>
    [Fact]
    public void WithNoPlayerTheGateDoesNotRefuse()
    {
        var meter = new DamageMeter();

        // Area() builds a snapshot with no player in it.
        meter.Look(Area(1, Monster(1, 5_000, max: 5_000, away: 999_999f)), 0);
        meter.Look(Area(1), 100);

        Assert.Equal(5_000, meter.Credited);
        Assert.Equal(0, meter.Withheld);
    }

    /// <summary>
    /// Distance is judged where it was last SEEN, not from wherever the player ended up.
    /// </summary>
    /// <remarks>
    /// A monster killed in front of you was last seen close even if you then ran off; the
    /// snapshot it last appeared in is the only place its distance can be read at all.
    /// </remarks>
    [Fact]
    public void DistanceIsTakenAtTheLastSighting()
    {
        var meter = new DamageMeter();

        // Seen close, killed close - then the next snapshot has no monster at all.
        meter.Look(Seen(1, Monster(1, 3_000, max: 3_000, away: 100f)), 0);
        meter.Look(Seen(1, Monster(1, 3_000, max: 3_000, away: 150f)), 100);
        meter.Look(Seen(1), 200);

        Assert.Equal(3_000, meter.Credited);
        Assert.Equal(0, meter.Withheld);
    }

    /// <summary>The furthest disappearance is the largest one, not the latest.</summary>
    [Fact]
    public void TheFurthestDisappearanceKeepsTheLargest()
    {
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 500, away: 900f), Monster(2, 500, away: 300f)), 0);
        meter.Look(Seen(1, Monster(2, 500, away: 300f)), 100); // the far one goes
        meter.Look(Seen(1), 200);                              // then the near one

        Assert.Equal(900f, meter.FurthestVanish);
    }

    /// <summary>
    /// It is recorded even for a vanish the gate refused, and with kills not counted at all.
    /// </summary>
    /// <remarks>
    /// It is evidence about whether the settings are right, so it cannot be filtered by
    /// them - a diagnostic that only reported what the current settings already accepted
    /// could never contradict them.
    /// </remarks>
    [Fact]
    public void TheFurthestDisappearanceIgnoresTheGateAndTheSwitch()
    {
        float far = (4f * DamageMeter.ScreenWorldUnits) + 1f;

        var gated = new DamageMeter();
        gated.Look(Seen(1, Monster(1, 500, away: far)), 0);
        gated.Look(Seen(1), 100);

        Assert.Equal(0, gated.Credited);          // refused, as it should be
        Assert.Equal(far, gated.FurthestVanish);  // but still measured

        var off = new DamageMeter { CountKills = false };
        off.Look(Seen(1, Monster(1, 500, away: far)), 0);
        off.Look(Seen(1), 100);

        Assert.Equal(far, off.FurthestVanish);
    }

    /// <summary>
    /// The two furthest figures bracket the gate: one counts everything, one only the
    /// vanishes that were believed.
    /// </summary>
    [Fact]
    public void TheFurthestCountedExcludesWhatTheGateRefused()
    {
        var meter = new DamageMeter();
        float near = 300f;
        float far = (4f * DamageMeter.ScreenWorldUnits) + 1f;

        meter.Look(Seen(1, Monster(1, 500, away: near), Monster(2, 500, away: far)), 0);
        meter.Look(Seen(1), 100);

        Assert.Equal(far, meter.FurthestVanish);    // everything that went missing
        Assert.Equal(near, meter.FurthestCounted);  // only what was believed
        Assert.Equal(500, meter.Credited);
        Assert.Equal(500, meter.Withheld);
    }

    /// <summary>With nothing counted there is no counted figure, rather than a zero.</summary>
    [Fact]
    public void TheFurthestCountedIsAbsentWhenNothingWasCounted()
    {
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Seen(1, Monster(1, 500, away: 300f)), 0);
        meter.Look(Seen(1), 100);

        Assert.Equal(300f, meter.FurthestVanish); // still measured
        Assert.True(meter.FurthestCounted < 0f);  // but nothing was believed
    }

    /// <summary>With nothing gone missing yet there is no figure, rather than a zero.</summary>
    /// <remarks>
    /// Zero would read as "everything vanished right on top of you", which is the strongest
    /// possible evidence for the assumption - and it would be showing before any evidence
    /// existed at all.
    /// </remarks>
    [Fact]
    public void TheFurthestDisappearanceIsAbsentUntilSomethingVanishes()
    {
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 500, away: 400f)), 0);
        meter.Look(Seen(1, Monster(1, 400, away: 400f)), 100);

        Assert.Equal(0, meter.Vanished);
        Assert.True(meter.FurthestVanish < 0f);
    }

    /// <summary>
    /// How far the list reaches is measured from sightings, not from things going missing.
    /// </summary>
    /// <remarks>
    /// It is the denominator the vanish distance was missing: "the furthest thing to go
    /// missing was 255 grid away" says nothing until it is set against how far anything can
    /// be seen at all.
    /// </remarks>
    [Fact]
    public void TheReachOfTheListIsMeasuredFromEverySighting()
    {
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 500, away: 400f), Monster(2, 500, away: 1_800f)), 0);
        meter.Look(Seen(1, Monster(1, 500, away: 400f), Monster(2, 500, away: 1_800f)), 100);

        Assert.Equal(1_800f, meter.FurthestSeen);
        Assert.Equal(0, meter.Vanished);  // nothing had to die for this
    }

    /// <summary>
    /// A vanish at the edge of the list reads as one, and a vanish well inside does not.
    /// </summary>
    /// <remarks>
    /// The comparison that settles what the disappearances ARE. The game only replicates
    /// entities within a radius of the player, so anything leaving at that radius is alive and
    /// walking home; well inside it, the only thing that removes a monster is dying.
    /// </remarks>
    [Fact]
    public void AVanishAtTheEdgeIsToldApartFromOneInTheMiddle()
    {
        var atEdge = new DamageMeter { CreditWithin = 0f };
        atEdge.Look(Seen(1, Monster(1, 500, away: 2_000f), Monster(2, 500, away: 1_990f)), 0);
        atEdge.Look(Seen(1, Monster(1, 500, away: 2_000f)), 100);   // the far one goes

        Assert.True(atEdge.VanishedAtEdge > 0.9f, $"got {atEdge.VanishedAtEdge}");

        var inside = new DamageMeter { CreditWithin = 0f };
        inside.Look(Seen(1, Monster(1, 500, away: 2_000f), Monster(2, 500, away: 200f)), 0);
        inside.Look(Seen(1, Monster(1, 500, away: 2_000f)), 100);   // the NEAR one goes

        Assert.True(inside.VanishedAtEdge < 0.2f, $"got {inside.VanishedAtEdge}");
    }

    /// <summary>With nothing seen there is no reach, rather than a zero.</summary>
    [Fact]
    public void TheReachIsAbsentUntilSomethingIsSeen()
    {
        var meter = new DamageMeter();

        Assert.True(meter.FurthestSeen < 0f);
        Assert.True(meter.VanishedAtEdge < 0f);
    }

    /// <summary>Two readings inside the same millisecond lose no damage.</summary>
    /// <remarks>
    /// The reader is paced, not guaranteed - and a difference divided by a zero interval is
    /// the one arithmetic here that has no answer. The damage has to land on the next
    /// reading rather than be dropped.
    /// </remarks>
    [Fact]
    public void TwoReadingsInOneMillisecondLoseNothing()
    {
        var meter = new DamageMeter();

        meter.Look(Area(1, Monster(1, 1_000)), 0);
        meter.Look(Area(1, Monster(1, 900)), 100);
        meter.Look(Area(1, Monster(1, 800)), 100); // same millisecond - skipped
        meter.Look(Area(1, Monster(1, 800)), 200); // ...and picked up here

        Assert.Equal(200, meter.Observed);
    }
}
