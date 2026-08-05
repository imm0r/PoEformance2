using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The settings: what the user decides, and how that becomes rules the engine can run.
/// </summary>
public class AutoFlaskSettingsTests
{
    private static FlaskKeys Keys(params (int Slot, ushort Key)[] bound)
        => new(bound.ToDictionary(b => b.Slot, b => b.Key), KeyBindingSource.GameConfig, "test");

    [Fact]
    public void Defaults_ArmNothing()
    {
        // First run must press no keys. Every slot is present so the page has a full belt
        // to show, and every one of them is off.
        Assert.False(AutoFlaskSettings.Default.Enabled);
        Assert.Equal(AutoFlaskSettings.SlotCount, AutoFlaskSettings.Default.Slots.Count);
        Assert.All(AutoFlaskSettings.Default.Slots, slot => Assert.False(slot.Enabled));
    }

    [Fact]
    public void ToRules_TakesTheKeyFromTheGameBinding()
    {
        var settings = new AutoFlaskSettings(true, [new FlaskSlotSettings(2, true, VitalKind.Mana, 30)]);

        FlaskRule rule = settings.ToRules(Keys((2, 0x51))).Single();

        Assert.Equal(0x51, rule.Key);      // Q, because that is what the game is bound to
        Assert.Equal(2, rule.Slot);
        Assert.Equal(VitalKind.Mana, rule.Vital);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void ToRules_LeavesASlotOffWhenItsKeyCannotBeSent()
    {
        // Enabled in the settings, but there is no key to press. Arming it would mean
        // evaluating a rule whose only possible outcome is a keystroke that does nothing.
        var settings = new AutoFlaskSettings(true, [new FlaskSlotSettings(3, true, VitalKind.Life, 50)]);

        FlaskRule rule = settings.ToRules(Keys()).Single();

        Assert.Equal(0, rule.Key);
        Assert.False(rule.Enabled);
    }

    [Theory]
    [InlineData(5000, 100)]
    [InlineData(-5, 1)]
    [InlineData(65, 65)]
    public void Normalised_ClampsThresholdsIntoRange(int written, int expected)
    {
        // The file is hand-editable, so it can arrive saying 5000%. Unclamped, that rule
        // fires on a completely full globe, forever.
        var settings = new AutoFlaskSettings(true, [new FlaskSlotSettings(1, true, VitalKind.Life, written)]);

        Assert.Equal(expected, settings.Normalised().Slots[0].ThresholdPercent);
    }

    [Fact]
    public void Normalised_FillsInMissingSlotsAndDropsDuplicates()
    {
        // A file listing only slot 2 would otherwise leave the page a row short of the belt
        // it is describing, and two entries for one slot would make "which one wins"
        // depend on iteration order.
        var settings = new AutoFlaskSettings(false,
        [
            new FlaskSlotSettings(2, true, VitalKind.Mana, 25),
            new FlaskSlotSettings(2, false, VitalKind.Life, 90),
        ]);

        AutoFlaskSettings normalised = settings.Normalised();

        Assert.Equal(AutoFlaskSettings.SlotCount, normalised.Slots.Count);
        Assert.Equal([1, 2, 3, 4, 5], normalised.Slots.Select(s => s.Slot));
        Assert.True(normalised.Slots[1].Enabled);          // the first entry wins
        Assert.Equal(25, normalised.Slots[1].ThresholdPercent);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-flasks-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AutoFlaskSettings(true,
            [
                new FlaskSlotSettings(1, true, VitalKind.EnergyShield, 40),
                new FlaskSlotSettings(2, false, VitalKind.Mana, 30),
            ]);

            Assert.True(AutoFlaskSettingsStore.Save(settings, path));
            AutoFlaskSettings loaded = AutoFlaskSettingsStore.Load(path);

            Assert.True(loaded.Enabled);
            Assert.Equal(VitalKind.EnergyShield, loaded.Slots[0].Vital);
            Assert.Equal(40, loaded.Slots[0].ThresholdPercent);
            Assert.Equal(AutoFlaskSettings.SlotCount, loaded.Slots.Count);   // normalised on load

            // The enum is written by NAME, so the file survives a future member being
            // inserted into VitalKind - an ordinal would silently mean something else.
            Assert.Contains("EnergyShield", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptFile_FallsBackToDoingNothing()
    {
        // The failure mode of a settings file that arms key presses must be "off".
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-flasks-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            AutoFlaskSettings loaded = AutoFlaskSettingsStore.Load(path);

            Assert.False(loaded.Enabled);
            Assert.All(loaded.Slots, slot => Assert.False(slot.Enabled));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Configure_SwapsRulesWithoutHandingOutAFreeRefire()
    {
        // Editing a threshold must not reset cooldowns: otherwise every keystroke typed
        // into the config page would let a flask that just fired fire again.
        var engine = new AutoFlask([new FlaskRule("Slot 1", VitalKind.Life, 65, Key: 0x31)], enabled: true);
        var pools = new Vitals(new Vital(10, 100, 0, 0), default, default);

        Assert.Single(engine.Evaluate(pools, gameFocused: true, nowMs: 1000).Used);

        engine.Configure([new FlaskRule("Slot 1", VitalKind.Life, 70, Key: 0x31)], enabled: true);

        Assert.Empty(engine.Evaluate(pools, gameFocused: true, nowMs: 1100).Used);
        Assert.Contains("cooling down", engine.LastTick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArmedWithNoSlotEnabled_SaysSoInsteadOfLookingBusy()
    {
        // Exactly what a live run showed: master switch on, every slot off, and the status
        // read "watching (life 100, mana 21...)" - which is what a WORKING tool says. It
        // could never fire, and nothing in the readout admitted it.
        var engine = new AutoFlask(
            [new FlaskRule("Slot 1", VitalKind.Life, 65, Key: 0x31) { Enabled = false }],
            enabled: true);

        FlaskTick tick = engine.Evaluate(
            new Vitals(new Vital(10, 100, 0, 0), default, default), gameFocused: true, nowMs: 1000);

        Assert.Empty(tick.Used);
        Assert.Equal("no slot enabled", tick.Reason);
        Assert.False(engine.AnyRuleEnabled());
    }

    [Fact]
    public void Configure_CanArmAndDisarmWhileRunning()
    {
        var engine = new AutoFlask([new FlaskRule("Slot 1", VitalKind.Life, 65, Key: 0x31)], enabled: false);
        var pools = new Vitals(new Vital(10, 100, 0, 0), default, default);

        Assert.Equal("disabled", engine.Evaluate(pools, gameFocused: true, nowMs: 1000).Reason);

        engine.Configure(engine.Rules, enabled: true);
        Assert.Single(engine.Evaluate(pools, gameFocused: true, nowMs: 2000).Used);
    }
}
