using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The flask decision, and the vital arithmetic under it. Both are pure, so the rules that
/// actually matter in play are covered here instead of by playing.
/// </summary>
public class AutoFlaskTests
{
    private static Vitals Pools(int life = 100, int lifeMax = 100, int mana = 100, int manaMax = 100,
        int manaReservedPercent = 0, int es = 0, int esMax = 0)
        => new(
            new Vital(life, lifeMax, 0, 0),
            new Vital(mana, manaMax, 0, manaReservedPercent),
            new Vital(es, esMax, 0, 0));

    private static AutoFlask Engine(params FlaskRule[] rules) => new(rules, enabled: true);

    // ── the arithmetic ───────────────────────────────────────────────────────────

    [Fact]
    public void Percent_IsMeasuredAgainstTheUnreservedPool()
    {
        // THE rule worth pinning. Half of mana reserved by auras: a FULL globe holds 50 of
        // a 100 max. Against Max that reads 50%, which would make any threshold below 50
        // fire forever. Against the unreserved pool it correctly reads 100%.
        var halfReserved = new Vital(Current: 50, Max: 100, ReservedFlat: 0, ReservedPercent: 5000);

        Assert.Equal(50, halfReserved.Reserved);
        Assert.Equal(50, halfReserved.Unreserved);
        Assert.Equal(100, halfReserved.Percent);
    }

    [Fact]
    public void Percent_CombinesFlatAndProportionalReservations()
    {
        // reserved = ceil(2500/10000 * 200) + 20 = 50 + 20 = 70; usable = 130.
        var vital = new Vital(Current: 65, Max: 200, ReservedFlat: 20, ReservedPercent: 2500);

        Assert.Equal(70, vital.Reserved);
        Assert.Equal(130, vital.Unreserved);
        Assert.Equal(50, vital.Percent);
    }

    [Fact]
    public void Percent_IsUnknownRatherThanZero_WhenThereIsNoPool()
    {
        // An unloaded or fully reserved pool must not read as "empty" - that is the
        // difference between "nothing to measure" and "emergency".
        Assert.Equal(-1, new Vital(0, 0, 0, 0).Percent);
        Assert.Equal(-1, new Vital(0, 100, 100, 0).Percent);
    }

    // ── the decision ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fires_WhenThePoolDropsToTheThreshold()
    {
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31));

        Assert.Empty(engine.Evaluate(Pools(life: 70), gameFocused: true, 1000).Used);

        FlaskTick tick = engine.Evaluate(Pools(life: 65), gameFocused: true, 2000);
        FlaskUse use = Assert.Single(tick.Used);
        Assert.Equal("life", use.Rule.Name);
        Assert.Equal(65, use.Percent);
    }

    [Fact]
    public void DoesNotFire_WhenTheGameIsNotFocused()
    {
        // A SAFETY property, not a preference: keystrokes go to whatever holds focus, so
        // firing while alt-tabbed types flask keys into someone's browser or editor.
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31));

        FlaskTick tick = engine.Evaluate(Pools(life: 10), gameFocused: false, 1000);

        Assert.Empty(tick.Used);
        Assert.Contains("focus", tick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotFire_WhenDeadOrUnloaded()
    {
        // At zero life every threshold reads as maximum urgency. Without this guard the
        // whole belt drains into a corpse, and again on every loading screen.
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31));

        Assert.Empty(engine.Evaluate(Pools(life: 0), gameFocused: true, 1000).Used);
        Assert.Empty(engine.Evaluate(Pools(life: 0, lifeMax: 0), gameFocused: true, 2000).Used);
        Assert.Contains("dead", engine.LastTick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotFire_WhenDisabled()
    {
        var engine = new AutoFlask([new FlaskRule("life", VitalKind.Life, 65, Key: 0x31)], enabled: false);
        Assert.Empty(engine.Evaluate(Pools(life: 5), gameFocused: true, 1000).Used);
        Assert.Equal("disabled", engine.LastTick.Reason);
    }

    [Fact]
    public void Cooldown_StopsTheBeltDrainingInOneSecond()
    {
        // Recovery is not instant, so the pool is still below threshold on the very next
        // tick. Without a per-flask cooldown that means every flask, immediately.
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31, CooldownMs: 1500));

        Assert.Single(engine.Evaluate(Pools(life: 20), gameFocused: true, 10_000).Used);
        Assert.Empty(engine.Evaluate(Pools(life: 20), gameFocused: true, 10_100).Used);
        Assert.Empty(engine.Evaluate(Pools(life: 20), gameFocused: true, 11_400).Used);
        Assert.Single(engine.Evaluate(Pools(life: 20), gameFocused: true, 11_500).Used);
    }

    [Fact]
    public void Cooldowns_ArePerFlask()
    {
        // One flask on cooldown must not hold back another - the mana flask should still
        // fire while the life flask is recovering.
        AutoFlask engine = Engine(
            new FlaskRule("life", VitalKind.Life, 65, Key: 0x31, CooldownMs: 5000),
            new FlaskRule("mana", VitalKind.Mana, 30, Key: 0x32, CooldownMs: 5000));

        Assert.Single(engine.Evaluate(Pools(life: 20, mana: 100), gameFocused: true, 1000).Used);

        FlaskTick tick = engine.Evaluate(Pools(life: 20, mana: 10), gameFocused: true, 1100);
        FlaskUse use = Assert.Single(tick.Used);
        Assert.Equal("mana", use.Rule.Name);
    }

    [Fact]
    public void ManaFlask_RespectsReservation()
    {
        // The end-to-end version of the arithmetic test: with 50% of mana reserved and the
        // globe FULL, a 30% mana rule must stay quiet. Measured against Max it would fire
        // on every single tick, forever.
        AutoFlask engine = Engine(new FlaskRule("mana", VitalKind.Mana, 30, Key: 0x32));

        FlaskTick tick = engine.Evaluate(
            Pools(mana: 50, manaMax: 100, manaReservedPercent: 5000), gameFocused: true, 1000);

        Assert.Empty(tick.Used);
    }

    [Fact]
    public void DisabledRules_AreSkipped()
    {
        AutoFlask engine = Engine(
            new FlaskRule("life", VitalKind.Life, 65, Key: 0x31) { Enabled = false },
            new FlaskRule("mana", VitalKind.Mana, 90, Key: 0x32));

        FlaskTick tick = engine.Evaluate(Pools(life: 10, mana: 10), gameFocused: true, 1000);
        Assert.Equal("mana", Assert.Single(tick.Used).Rule.Name);
    }

    [Fact]
    public void Reason_IsAlwaysSet_SoNothingHappenedCanBeExplained()
    {
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31));

        Assert.NotEmpty(engine.Evaluate(null, gameFocused: true, 1000).Reason);
        Assert.NotEmpty(engine.Evaluate(Pools(), gameFocused: true, 1000).Reason);
        Assert.Contains("life 100%", engine.LastTick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Defaults_AreOffAndSensible()
    {
        // A tool that starts pressing keys on first run is not one anybody can trust.
        var engine = new AutoFlask(AutoFlask.DefaultRules);
        Assert.False(engine.Enabled);
        Assert.Empty(engine.Evaluate(Pools(life: 1), gameFocused: true, 1000).Used);
        Assert.Equal(2, AutoFlask.DefaultRules.Count);
    }

    // ── charges and active effects ───────────────────────────────────────────────

    private static FlaskBelt Belt(params EquippedFlask[] flasks) => new(flasks);

    private static ActiveBuffs FlaskBuff(int slot, float timeLeft = 4f)
        => new([new ActiveBuff("flask_effect_life", timeLeft, 5f, 0, slot, IsFlask: true)]);

    [Fact]
    public void EmptyFlask_IsSkippedWithoutBurningItsCooldown()
    {
        // The game ignores a press with too few charges outright - nothing triggers and no
        // in-game cooldown starts. What must not happen is OUR side recording a use that
        // never occurred: the rule would then sit out its own cooldown, and a refill during
        // that window would go unused. Skipping without stamping is what the second half of
        // this test checks.
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31) { Slot = 1 });
        FlaskBelt empty = Belt(new EquippedFlask(1, "Metadata/Items/Flasks/X", Charges: 5, ChargesPerUse: 20));

        FlaskTick tick = engine.Evaluate(Pools(life: 20), true, 1000, belt: empty);

        Assert.Empty(tick.Used);
        Assert.Contains("charges", tick.Reason, StringComparison.OrdinalIgnoreCase);

        // And the slot is NOT suppressed afterwards: once refilled it fires immediately.
        FlaskBelt refilled = Belt(new EquippedFlask(1, "Metadata/Items/Flasks/X", 40, 20));
        Assert.Single(engine.Evaluate(Pools(life: 20), true, 1100, belt: refilled).Used);
    }

    [Fact]
    public void Fires_WhenChargesExactlyCoverOneUse()
    {
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31) { Slot = 1 });
        FlaskBelt exact = Belt(new EquippedFlask(1, "Metadata/Items/Flasks/X", Charges: 20, ChargesPerUse: 20));

        Assert.Single(engine.Evaluate(Pools(life: 20), true, 1000, belt: exact).Used);
    }

    [Fact]
    public void UnknownBelt_DoesNotBlockTheFeature()
    {
        // A belt that could not be read is not evidence of an empty flask. Blocking on it
        // would turn one failed read into a silently disabled feature.
        AutoFlask engine = Engine(new FlaskRule("life", VitalKind.Life, 65, Key: 0x31) { Slot = 1 });
        Assert.Single(engine.Evaluate(Pools(life: 20), true, 1000, belt: FlaskBelt.Empty).Used);
    }

    [Fact]
    public void DoesNotReuse_WhileTheFlasksOwnEffectIsStillRunning()
    {
        // The real charge saver, and the reason buffs are read at all: a cooldown only
        // GUESSES the duration, while the game reports the remaining time as fact.
        AutoFlask engine = Engine(
            new FlaskRule("life", VitalKind.Life, 65, Key: 0x31, CooldownMs: 0) { Slot = 1 });

        FlaskTick blockedTick = engine.Evaluate(Pools(life: 20), true, 1000, FlaskBuff(slot: 1));
        Assert.Empty(blockedTick.Used);
        Assert.Contains("active", blockedTick.Reason, StringComparison.OrdinalIgnoreCase);

        // Another slot's flask being active is irrelevant to this one.
        Assert.Single(engine.Evaluate(Pools(life: 20), true, 1100, FlaskBuff(slot: 3)).Used);
    }

    [Fact]
    public void ExpiredFlaskBuff_DoesNotBlock()
    {
        AutoFlask engine = Engine(
            new FlaskRule("life", VitalKind.Life, 65, Key: 0x31, CooldownMs: 0) { Slot = 1 });

        Assert.Single(engine.Evaluate(Pools(life: 20), true, 1000, FlaskBuff(slot: 1, timeLeft: 0f)).Used);
    }

    [Fact]
    public void BuffTriggeredFlask_FiresOnTheDebuffRegardlessOfLife()
    {
        // Utility flasks are not low-life events. A bleed at full life must still trigger
        // the removal flask, which a threshold rule would never do.
        AutoFlask engine = Engine(
            new FlaskRule("bleed", VitalKind.Life, ThresholdPercent: 0, Key: 0x33)
            {
                Slot = 3,
                TriggerBuff = "bleeding",
            });

        var bleeding = new ActiveBuffs([new ActiveBuff("bleeding", 5f, 5f, 0, 0, IsFlask: false)]);

        Assert.Empty(engine.Evaluate(Pools(life: 100), true, 1000).Used);
        Assert.Single(engine.Evaluate(Pools(life: 100), true, 2000, bleeding).Used);
    }

    [Fact]
    public void BuffLookup_IsForgivingAboutExactNames()
    {
        // The internal names are game identifiers; a person configuring a bleed flask
        // should not have to know the exact one.
        var buffs = new ActiveBuffs([new ActiveBuff("player_bleeding_dot", 3f, 5f, 0, 0, false)]);

        Assert.True(buffs.Has("BLEEDING"));
        Assert.True(buffs.Has("bleed"));
        Assert.False(buffs.Has("frozen"));
        Assert.False(buffs.Has(" "));
    }

    [Fact]
    public void FlaskDetection_MatchesFlaskPaths()
    {
        Assert.True(FlaskBeltReader.IsFlask("Metadata/Items/Flasks/FlaskLife1"));
        Assert.False(FlaskBeltReader.IsFlask("Metadata/Items/Weapons/Bow"));
    }
}
