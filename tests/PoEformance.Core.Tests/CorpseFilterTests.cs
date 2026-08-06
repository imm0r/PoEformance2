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
}

/// <summary>
/// Telling a ground effect apart from a monster.
/// </summary>
/// <remarks>
/// Written from a live report: a screen carrying far too many dots, the player's own flame
/// wall drawn as an enemy, and every one of those effects wearing a health bar. All three are
/// the same thing - an effect is built from the same components a monster is, so deciding
/// "monster" from the metadata path alone cannot tell them apart.
/// </remarks>
public class PassingEffectTests
{
    private static MonsterSigns Signs(bool friendly, bool temporary, bool? targetable)
        => new(100, targetable, false, ItemRarity.Normal, default, default, friendly, temporary);

    [Fact]
    public void AHOSTILEThingThatExpiresAndCannotBeTargetedIsAnEffect()
    {
        // The flame wall. It has Life, so it was a monster to everything downstream.
        Assert.True(Signs(friendly: false, temporary: true, targetable: false).IsPassingEffect);
    }

    [Fact]
    public void ANDSoIsOneThatOffersNoTargetableComponentAtAll()
    {
        // No component is not evidence of being fightable. Falls the same way as untargetable
        // - the reference reads a missing component and an untargetable one identically here.
        Assert.True(Signs(friendly: false, temporary: true, targetable: null).IsPassingEffect);
    }

    [Fact]
    public void BUTATargetableOneIsAMonsterThatHappensToExpire()
    {
        // The let-out that keeps this from hiding real summons. Plenty of genuine monsters
        // expire on their own, and those are worth every bit of the drawing.
        Assert.False(Signs(friendly: false, temporary: true, targetable: true).IsPassingEffect);
    }

    [Fact]
    public void ANDAFriendlyOneIsNeverDiscardedHere()
    {
        // Whether to draw your own minions is a preference and belongs to the overlay. This
        // answers a question of fact, so it does not get to make that call.
        Assert.False(Signs(friendly: true, temporary: true, targetable: false).IsPassingEffect);
    }

    [Fact]
    public void ANDANOrdinaryMonsterIsUntouched()
    {
        // The case that must not regress: a permanent hostile monster, which is most of them.
        Assert.False(Signs(friendly: false, temporary: false, targetable: true).IsPassingEffect);
        Assert.False(Signs(friendly: false, temporary: false, targetable: null).IsPassingEffect);
    }
}
