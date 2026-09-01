using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The history behind the one-line status.
/// </summary>
/// <remarks>
/// Built on a plain measurement from playing: with six rules configured, no status survives a
/// second. The status line holds one tick's reason and a tick is 33 ms, so every question
/// actually asked of it - did that rule fire, when did it stop, was the cull aimed - is a
/// question about the recent past that a field showing only the present cannot answer.
/// </remarks>
public class RuleLogTests
{
    [Fact]
    public void ARepeatedActBecomesOneLineWithACount()
    {
        // Twenty lines saying "fired" push everything else off the page, and the thing worth
        // seeing in a log is almost always the line that is NOT repeating.
        var log = new RuleLog();
        log.Acted(1000, "Spark", "KeyPress R");
        log.Acted(1500, "Spark", "KeyPress R");
        log.Acted(2000, "Spark", "KeyPress R");

        RuleLogEntry entry = Assert.Single(log.Recent(10));
        Assert.Equal(3, entry.Count);

        // The time moves to the NEWEST of them: a line reading "x3" is asked when it last
        // happened, not when the run started.
        Assert.Equal(2000, entry.AtMs);
    }

    [Fact]
    public void AHeldBlockIsWrittenOnceRatherThanEveryTick()
    {
        // The whole reason a block is a state and an act is an event. At 30 ticks a second an
        // unbound key would otherwise fill the entire history in under seven seconds.
        var log = new RuleLog();
        for (long tick = 0; tick < 200; tick += 33)
        {
            log.Blocked(tick, "Power Siphon - Rare", "no key to press");
        }

        RuleLogEntry entry = Assert.Single(log.Recent(10));
        Assert.True(entry.Blocked);
        Assert.Equal(1, entry.Count);

        // Held, so it keeps the time it STARTED - which is what "since when has this been
        // broken" is asking.
        Assert.Equal(0, entry.AtMs);
    }

    [Fact]
    public void AChangedBlockIsWrittenAgain()
    {
        var log = new RuleLog();
        log.Blocked(0, "Cull", "no key to press");
        log.Blocked(100, "Cull", "no key to press");
        log.Blocked(200, "Cull", "nothing to aim at");

        IReadOnlyList<RuleLogEntry> recent = log.Recent(10);
        Assert.Equal(2, recent.Count);

        // Newest first, because that is the line being looked for.
        Assert.Equal("nothing to aim at", recent[0].What);
        Assert.Equal("no key to press", recent[1].What);
    }

    [Fact]
    public void ActingClearsTheHeldBlockSoTheNextOneIsSeen()
    {
        // Without this a rule that alternates between working and failing writes its failure
        // once and then looks permanently fixed - the log would say the problem went away.
        var log = new RuleLog();
        log.Blocked(0, "Spark", "game not focused");
        log.Acted(100, "Spark", "KeyPress R");
        log.Blocked(200, "Spark", "game not focused");

        Assert.Equal(3, log.Recent(10).Count);
    }

    [Fact]
    public void TwoRulesDoNotShareOneHeldState()
    {
        // Blocked-on-change is per RULE. Keyed globally, six rules alternating the same reason
        // would each suppress the others and the log would show one of them at random.
        var log = new RuleLog();
        log.Blocked(0, "Spark", "cooling down");
        log.Blocked(1, "Flame Wall", "cooling down");
        log.Blocked(2, "Spark", "cooling down");

        IReadOnlyList<RuleLogEntry> recent = log.Recent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Flame Wall", recent[0].Rule);
        Assert.Equal("Spark", recent[1].Rule);
    }

    [Fact]
    public void ItStopsGrowing()
    {
        var log = new RuleLog();
        for (int index = 0; index < RuleLog.Keep + 50; index++)
        {
            // Distinct, so nothing collapses and every one is a real entry.
            log.Acted(index, "Spark", $"press {index}");
        }

        Assert.Equal(RuleLog.Keep, log.Count);

        // The OLDEST go, not the newest.
        Assert.Equal($"press {RuleLog.Keep + 49}", log.Recent(1)[0].What);
    }

    [Fact]
    public void AgesReadAtAGlance()
    {
        Assert.Equal("0.0s", RuleLog.Age(0));
        Assert.Equal("0.4s", RuleLog.Age(400));
        Assert.Equal("3s", RuleLog.Age(3_400));
        Assert.Equal("2m", RuleLog.Age(150_000));
        Assert.Equal("1h", RuleLog.Age(3_700_000));

        // A clock that ran backwards is a test's clock, not a real one, and it reads as now
        // rather than as a negative age.
        Assert.Equal("0.0s", RuleLog.Age(-500));
    }

    [Fact]
    public void TheEngineWritesWhatFiredAndWhatBlocked()
    {
        // The end-to-end shape: one rule that can act and one that cannot, over one tick.
        var press = new RuleEffect(RuleEffectKind.KeyPress) { Key = "R", CooldownMs = 0 };
        var mute = new RuleEffect(RuleEffectKind.KeyPress) { Key = string.Empty };

        var settings = new RuleSettings(
            true,
            "P",
            [new RuleProfile("P", [new RuleGroup("G", [
                new Rule("a", "Spark", RuleCondition.Of(RuleFact.InGame), [press]) { Enabled = true },
                new Rule("b", "Power Siphon", RuleCondition.Of(RuleFact.InGame), [mute]) { Enabled = true },
            ])])])
        {
            MinInputGapMs = 0,
            CooldownJitterMs = 0,
        };

        var engine = new RuleEngine(new Random(1));
        engine.Configure(settings);

        var state = new RuleState { InGame = true, GameFocused = true, Alive = true };
        engine.Evaluate(state, 500);

        IReadOnlyList<RuleLogEntry> recent = engine.Log.Recent(10);
        Assert.Equal(2, recent.Count);

        RuleLogEntry blocked = Assert.Single(recent, e => e.Blocked);
        Assert.Equal("Power Siphon", blocked.Rule);
        Assert.Equal("no key to press", blocked.What);

        RuleLogEntry acted = Assert.Single(recent, e => !e.Blocked);
        Assert.Equal("Spark", acted.Rule);

        // WHICH key, not just that something happened - with six rules that is the difference
        // between a log you can act on and one that says "a rule did a thing".
        Assert.Contains("R", acted.What, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePacingTheEngineDoesToItselfStaysOutOfTheHistory()
    {
        // A cooldown is the engine working as designed. It is true of most rules most of the
        // time, so logging it would make the history nine tenths pacing - which is how a log
        // ends up holding six seconds of history and no answers.
        var effect = new RuleEffect(RuleEffectKind.KeyPress) { Key = "R", CooldownMs = 5_000 };
        var rule = new Rule("a", "Spark", RuleCondition.Of(RuleFact.InGame), [effect]) { Enabled = true };
        var settings = new RuleSettings(true, "P", [new RuleProfile("P", [new RuleGroup("G", [rule])])])
        {
            MinInputGapMs = 0,
            CooldownJitterMs = 0,
        };

        var engine = new RuleEngine(new Random(1));
        engine.Configure(settings);

        var state = new RuleState { InGame = true, GameFocused = true, Alive = true };
        for (long tick = 0; tick < 1000; tick += 33)
        {
            engine.Evaluate(state, tick);
        }

        // It fired once and then sat on its cooldown for the rest - one entry, not thirty.
        RuleLogEntry entry = Assert.Single(engine.Log.Recent(10));
        Assert.False(entry.Blocked);

        // And the status line still says it, because that is what the status line is for.
        Assert.Contains("cooling down", engine.LastTick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAimOutcomeIsFiledUnderTheRuleThatAimed()
    {
        // "The pointer landed on nothing" without saying whose pointer is not a line anybody
        // can act on once more than one rule aims.
        var effect = new RuleEffect(RuleEffectKind.KeyPress) { Key = "R", AimAt = AimTarget.Rare };
        var rule = new Rule("ps-rare", "Power Siphon - Rare", RuleCondition.Of(RuleFact.InGame), [effect]);
        var settings = new RuleSettings(true, "P", [new RuleProfile("P", [new RuleGroup("G", [rule])])]);

        var engine = new RuleEngine(new Random(1));
        engine.Configure(settings);

        engine.Aimed("ps-rare", "the pointer landed on nothing", 4_000);

        RuleLogEntry entry = Assert.Single(engine.Log.Recent(10));
        Assert.Equal("Power Siphon - Rare", entry.Rule);
        Assert.Equal("the pointer landed on nothing", entry.What);

        // An id that no longer names anything still produces a line rather than an empty one:
        // a rule can be deleted while its own aim is still in flight.
        engine.Aimed("gone", "on target", 4_100);
        Assert.Equal("gone", engine.Log.Recent(1)[0].Rule);
    }
}
