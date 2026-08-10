using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which monsters count as corpses. Every case here is one the AHK reference got wrong
/// first and recorded, so they are pinned rather than rediscovered.
/// </summary>
public class CorpseFilterTests
{
    private static MonsterSigns Alive(int hp = 100) => new(hp, Targetable: true, IsBoss: false);

    [Fact]
    public void ZeroHealth_IsACorpseImmediately()
    {
        var filter = new CorpseFilter();
        Assert.True(filter.IsCorpse(0x1000, new MonsterSigns(0, Targetable: true, IsBoss: false), 1000));
    }

    [Fact]
    public void LivingMonster_IsKept()
    {
        var filter = new CorpseFilter();
        Assert.False(filter.IsCorpse(0x1000, Alive(), 1000));
        Assert.False(filter.IsCorpse(0x1000, Alive(1), 5000));
    }

    [Fact]
    public void CorpseWithStaleHealth_IsCaughtByTargetable()
    {
        // THE case that leaves red dots on a cleared screen: a dead monster can keep
        // reporting health above zero indefinitely while its targetable byte goes to 0.
        // Health alone would keep drawing it forever.
        var filter = new CorpseFilter { UntargetableMs = 400 };
        var corpse = new MonsterSigns(Health: 250, Targetable: false, IsBoss: false);

        Assert.False(filter.IsCorpse(0x1000, corpse, 1000));   // clock starts
        Assert.False(filter.IsCorpse(0x1000, corpse, 1300));   // still inside the window
        Assert.True(filter.IsCorpse(0x1000, corpse, 1400));
    }

    [Fact]
    public void BriefUntargetableFlicker_DoesNotHideALiveMonster()
    {
        // The byte dips during animations. Deciding on the first reading would make
        // monsters blink off the overlay mid-fight.
        var filter = new CorpseFilter { UntargetableMs = 400 };

        Assert.False(filter.IsCorpse(0x1000, new MonsterSigns(90, false, false), 1000));
        Assert.False(filter.IsCorpse(0x1000, new MonsterSigns(90, true, false), 1100));
        Assert.False(filter.IsCorpse(0x1000, new MonsterSigns(90, false, false), 1200));

        // The clock restarted at 1200, so the old window must not carry over.
        Assert.False(filter.IsCorpse(0x1000, new MonsterSigns(90, false, false), 1500));
        Assert.True(filter.IsCorpse(0x1000, new MonsterSigns(90, false, false), 1600));
    }

    [Fact]
    public void Boss_SurvivesAnUntargetablePhase()
    {
        // Bosses go untargetable during phase transitions. Hiding one at that moment is
        // worse than showing a corpse, which is why the reference exempts them outright.
        var filter = new CorpseFilter { UntargetableMs = 400 };
        var phasing = new MonsterSigns(Health: 5000, Targetable: false, IsBoss: true);

        Assert.False(filter.IsCorpse(0x2000, phasing, 1000));
        Assert.False(filter.IsCorpse(0x2000, phasing, 20_000));

        // Dead is still dead, boss or not.
        Assert.True(filter.IsCorpse(0x2000, phasing with { Health = 0 }, 21_000));
    }

    [Fact]
    public void MissingComponents_AreNotTakenAsDeath()
    {
        // "The component is not there" and "the component says zero" mean opposite things.
        // Conflating them deletes live monsters whenever a read hiccups - the reference
        // hit exactly this and had to add an ever-alive guard to undo it.
        var filter = new CorpseFilter();
        var unknown = new MonsterSigns(Health: null, Targetable: null, IsBoss: false);

        Assert.False(filter.IsCorpse(0x3000, unknown, 1000));
        Assert.False(filter.IsCorpse(0x3000, unknown, 60_000));
    }

    [Fact]
    public void HealthIsTrustedOverTargetable_WhenItSaysAlive()
    {
        // A monster with health left and no targetable component stays; there is no signal
        // that it died, and inventing one is how a fight loses its markers.
        var filter = new CorpseFilter();
        Assert.False(filter.IsCorpse(0x4000, new MonsterSigns(100, null, false), 1000));
    }

    [Fact]
    public void ReusedAddress_IsNotInheritedFromTheDeadMonster()
    {
        // The game reuses entity addresses. A fresh monster landing on one that was being
        // timed must not inherit the verdict - it reads targetable, which clears the entry
        // on the first frame it is seen.
        var filter = new CorpseFilter { UntargetableMs = 400 };
        var corpse = new MonsterSigns(200, Targetable: false, IsBoss: false);

        filter.IsCorpse(0x5000, corpse, 1000);
        Assert.True(filter.IsCorpse(0x5000, corpse, 2000));

        Assert.False(filter.IsCorpse(0x5000, Alive(), 2100));         // new tenant
        Assert.False(filter.IsCorpse(0x5000, corpse with { }, 2200)); // its own clock, from scratch
    }

    [Fact]
    public void Tracking_DoesNotGrowForeverAcrossAnArea()
    {
        // Monsters leave; their timers must not accumulate for a whole session.
        var filter = new CorpseFilter();
        var corpse = new MonsterSigns(50, Targetable: false, IsBoss: false);

        for (ulong address = 0x6000; address < 0x6100; address += 8)
        {
            filter.IsCorpse(address, corpse, 1000);
        }

        Assert.Equal(32, filter.Tracking);

        // Nothing seen since; well past the forget window.
        filter.IsCorpse(0x9000, corpse, 60_000);
        Assert.Equal(1, filter.Tracking);
    }
    /// <summary>
    /// The clock belongs to the MONSTER, so a key that changes between readings breaks it.
    /// </summary>
    /// <remarks>
    /// Not a hypothetical. The game wears several entity objects on one monster, the reader
    /// keeps only one of them, and which one depends on the order the entity map is walked in
    /// - which shifts as the tree rebalances around things dying and spawning. Keyed on the
    /// entity, that flip restarts this clock every time it happens, the threshold is never
    /// reached, and a cleared screen keeps its dots. That is exactly what came back on the
    /// overlay once the collapsing landed, and it is why the reader now keys this on the
    /// Render component.
    ///
    /// This test states the hazard rather than the fix: it shows what a changing key does, so
    /// the next person to pass something entity-shaped in here has it in writing.
    /// </remarks>
    [Fact]
    public void AKeyThatChangesBetweenReadingsNeverTimesAnythingOut()
    {
        var filter = new CorpseFilter();
        MonsterSigns corpse = new(500, Targetable: false, IsBoss: false);

        // Five seconds of a monster reading untargetable the whole time - but seen under a
        // different entity each reading, as an entity-keyed caller would report it.
        for (long now = 1000; now <= 6000; now += 100)
        {
            Assert.False(
                filter.IsCorpse(0x2000 + (ulong)now, corpse, now),
                $"a corpse was declared at {now} despite the key never repeating");
        }

        // And the same five seconds under one identity: recognised, and long before the end.
        var steady = new CorpseFilter();
        Assert.False(steady.IsCorpse(0x9000, corpse, 1000));
        Assert.True(steady.IsCorpse(0x9000, corpse, 1000 + steady.UntargetableMs));
    }
}

/// <summary>
/// Telling a ground effect apart from a monster.
/// </summary>
/// <remarks>
/// Written from a live report: a screen carrying far too many dots, the player's own flame
/// wall drawn as an enemy, and every one of those effects wearing a health bar. All three are
/// the same thing - an effect is built from the same components a monster is, so deciding
/// "monster" from the metadata path alone cannot tell them apart.
///
/// TWO RULES, and the difference between them is what the first fix got half right. Being an
/// effect is a fact and takes no view of sides; being dropped by the reader is what happens to
/// the HOSTILE ones. Collapsing the two left friendly effects with no health bar and a dot,
/// which is the second report from the same player.
/// </remarks>
public class PassingEffectTests
{
    private static MonsterSigns Signs(bool friendly, bool temporary, bool? targetable)
        => new(100, targetable, false, ItemRarity.Normal, default, default, friendly, temporary);

    [Fact]
    public void AThingThatExpiresAndCannotBeTargetedIsAnEffect()
    {
        // The flame wall. It has Life, so it was a monster to everything downstream.
        Assert.True(Signs(friendly: false, temporary: true, targetable: false).IsEffect);
    }

    [Fact]
    public void ANDSoIsOneThatOffersNoTargetableComponentAtAll()
    {
        // No component is not evidence of being fightable. Falls the same way as untargetable
        // - the reference reads a missing component and an untargetable one identically here.
        Assert.True(Signs(friendly: false, temporary: true, targetable: null).IsEffect);
    }

    [Fact]
    public void ANDBeingYOURSDoesNotMakeItAMonster()
    {
        // The bug this rule was split for. Your own flame wall is an effect on exactly the
        // same evidence as an enemy's, and reading it as a monster is what left it on the map
        // after the health bars had correctly given up on it.
        Assert.True(Signs(friendly: true, temporary: true, targetable: false).IsEffect);
    }

    [Fact]
    public void BUTATargetableOneIsAMonsterThatHappensToExpire()
    {
        // The let-out that keeps this from hiding real summons. Plenty of genuine monsters
        // expire on their own, and those are worth every bit of the drawing. It holds for
        // your own side too: a minion or a totem is targetable, so it is never an effect.
        Assert.False(Signs(friendly: false, temporary: true, targetable: true).IsEffect);
        Assert.False(Signs(friendly: true, temporary: true, targetable: true).IsEffect);
    }

    [Fact]
    public void ANDANOrdinaryMonsterIsUntouched()
    {
        // The case that must not regress: a permanent hostile monster, which is most of them.
        Assert.False(Signs(friendly: false, temporary: false, targetable: true).IsEffect);
        Assert.False(Signs(friendly: false, temporary: false, targetable: null).IsEffect);
    }

    [Fact]
    public void ONLYTheHostileOnesAreDroppedByTheReader()
    {
        // What the snapshot may throw away. A hostile effect is nobody's business downstream;
        // a friendly one is carried, because "do not draw it" is a decision the overlay makes
        // and the inspector does not.
        Assert.True(Signs(friendly: false, temporary: true, targetable: false).IsHostileEffect);
        Assert.False(Signs(friendly: true, temporary: true, targetable: false).IsHostileEffect);
    }

    [Fact]
    public void ANDNothingThatIsNotAnEffectIsDropped()
    {
        // The clause that stops the discard rule from widening into "friendly things go".
        Assert.False(Signs(friendly: false, temporary: false, targetable: true).IsHostileEffect);
        Assert.False(Signs(friendly: false, temporary: true, targetable: true).IsHostileEffect);
    
}
}
