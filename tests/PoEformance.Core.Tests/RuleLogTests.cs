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
    /// <summary>A log on a clock the test drives, one second per write.</summary>
    private static RuleLog Ticking(DateTime from)
    {
        DateTime at = from;
        return new RuleLog(() =>
        {
            at = at.AddSeconds(1);
            return at;
        });
    }

    [Fact]
    public void ARepeatedActBecomesOneLineWithACount()
    {
        // Twenty lines saying "fired" push everything else off the page, and the thing worth
        // seeing in a log is almost always the line that is NOT repeating.
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 3, 0, DateTimeKind.Local));
        log.Acted("Spark", "KeyPress R");
        log.Acted("Spark", "KeyPress R");
        log.Acted("Spark", "KeyPress R");

        RuleLogEntry entry = Assert.Single(log.Recent(10));
        Assert.Equal(3, entry.Count);

        // The time moves to the NEWEST of them: a line reading "x3" is asked when it last
        // happened, not when the run started.
        Assert.Equal("14:03:03.000", entry.Clock);

        // And the count is what the third column shows, since there is nothing measured.
        Assert.Equal("x3", entry.Measured);
    }

    [Fact]
    public void TheStampIsPreciseEnoughToOrderOneCull()
    {
        // The steps of a cull land within single-digit milliseconds of each other. Cut at the
        // second, the whole sequence prints as one instant and the ordering - which is the
        // entire reason for logging the steps separately - becomes unreadable.
        var log = new RuleLog(() => new DateTime(2026, 9, 1, 14, 3, 7, 42, DateTimeKind.Local));
        log.Acted("Power Siphon", "target found");

        Assert.Equal("14:03:07.042", log.Recent(1)[0].Clock);
    }

    [Fact]
    public void AHeldBlockIsWrittenOnceRatherThanEveryTick()
    {
        // The whole reason a block is a state and an act is an event. At 30 ticks a second an
        // unbound key would otherwise fill the entire history in under seven seconds.
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        for (int tick = 0; tick < 20; tick++)
        {
            log.Blocked("Power Siphon - Rare", "no key to press");
        }

        RuleLogEntry entry = Assert.Single(log.Recent(10));
        Assert.True(entry.Blocked);
        Assert.Equal(1, entry.Count);

        // Held, so it keeps the time it STARTED - which is what "since when has this been
        // broken" is asking.
        Assert.Equal("14:00:01.000", entry.Clock);
    }

    [Fact]
    public void AChangedBlockIsWrittenAgain()
    {
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        log.Blocked("Cull", "no key to press");
        log.Blocked("Cull", "no key to press");
        log.Blocked("Cull", "nothing to aim at");

        IReadOnlyList<RuleLogEntry> recent = log.Recent(10);
        Assert.Equal(2, recent.Count);

        // Newest first, because that is the line being looked for.
        Assert.Equal("nothing to aim at", recent[0].What);
        Assert.Equal("no key to press", recent[1].What);
    }

    [Fact]
    public void TwoActsThatMeasuredDifferentThingsDoNotCollapse()
    {
        // Both say "target found" and they are not the same event. Collapsing on the wording
        // alone would throw away the only part that distinguishes two culls - which monster,
        // at what life - and report two of them as one line reading "x2".
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        log.Acted("Power Siphon", "target found", "#0a0a0a0a  400/4000 10%");
        log.Acted("Power Siphon", "target found", "#0b0b0b0b  120/900 13%");

        IReadOnlyList<RuleLogEntry> recent = log.Recent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(1, recent[0].Count);
    }

    [Fact]
    public void ActingClearsTheHeldBlockSoTheNextOneIsSeen()
    {
        // Without this a rule that alternates between working and failing writes its failure
        // once and then looks permanently fixed - the log would say the problem went away.
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        log.Blocked("Spark", "game not focused");
        log.Acted("Spark", "KeyPress R");
        log.Blocked("Spark", "game not focused");

        Assert.Equal(3, log.Recent(10).Count);
    }

    [Fact]
    public void AnOutcomeThatReadsBadlyIsStillAnEvent()
    {
        // A cull that changed nothing twice in a row is two lines. Routed through Blocked it
        // would be one, and "it is still failing" would look like "it failed once, a while
        // ago" - the opposite of what the second failure means.
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        log.Acted("Power Siphon", "target unchanged", "still 400/4000 10%", bad: true);
        log.Acted("Power Siphon", "target unchanged", "still 400/4000 10%", bad: true);

        RuleLogEntry entry = Assert.Single(log.Recent(10));
        Assert.True(entry.Blocked);
        Assert.Equal(2, entry.Count);
        Assert.Equal("still 400/4000 10%   x2", entry.Measured);
    }

    [Fact]
    public void TwoRulesDoNotShareOneHeldState()
    {
        // Blocked-on-change is per RULE. Keyed globally, six rules alternating the same reason
        // would each suppress the others and the log would show one of them at random.
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        log.Blocked("Spark", "cooling down");
        log.Blocked("Flame Wall", "cooling down");
        log.Blocked("Spark", "cooling down");

        IReadOnlyList<RuleLogEntry> recent = log.Recent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Flame Wall", recent[0].Rule);
        Assert.Equal("Spark", recent[1].Rule);
    }

    [Fact]
    public void ItStopsGrowing()
    {
        RuleLog log = Ticking(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local));
        for (int index = 0; index < RuleLog.Keep + 50; index++)
        {
            // Distinct, so nothing collapses and every one is a real entry.
            log.Acted("Spark", $"press {index}");
        }

        Assert.Equal(RuleLog.Keep, log.Count);

        // The OLDEST go, not the newest.
        Assert.Equal($"press {RuleLog.Keep + 49}", log.Recent(1)[0].What);
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

        engine.Aimed("ps-rare", "hover check: nothing there", "wanted #0a0a0a0a", 4_000);

        RuleLogEntry entry = Assert.Single(engine.Log.Recent(10));
        Assert.Equal("Power Siphon - Rare", entry.Rule);
        Assert.Equal("hover check: nothing there", entry.What);
        Assert.Equal("wanted #0a0a0a0a", entry.Detail);

        // An id that no longer names anything still produces a line rather than an empty one:
        // a rule can be deleted while its own aim is still in flight.
        engine.Aimed("gone", "on target", string.Empty, 4_100);
        Assert.Equal("gone", engine.Log.Recent(1)[0].Rule);
    }

    /// <summary>A rule whose key press aims at the strongest rare within range.</summary>
    private static RuleSettings Culling() => new(
        true,
        "P",
        [new RuleProfile("P", [new RuleGroup("G", [
            new Rule(
                "ps",
                "Power Siphon",
                RuleCondition.Of(RuleFact.InGame),
                [new RuleEffect(RuleEffectKind.KeyPress)
                {
                    Key = "T",
                    AimAt = AimTarget.Rare,
                    AimRadius = 1000,
                    AimAtOrBelowPercent = 10,
                }])
            { Enabled = true },
        ])])])
    {
        MinInputGapMs = 0,
        CooldownJitterMs = 0,
    };

    private static RuleState Around(params NearMonster[] monsters) => new()
    {
        InGame = true,
        GameFocused = true,
        Alive = true,
        Monsters = [.. monsters.OrderBy(m => m.Distance)],
    };

    private static NearMonster Rare(ulong address, int life, int max, double distance = 10)
        => new(distance, ItemRarity.Rare, (float)distance, 0, 100d * life / max, 10f, address, life, max);

    [Fact]
    public void TheCullTraceReadsAsTheStepsItActuallyTook()
    {
        // The order was the complaint, and the cause was that the key press was logged where it
        // was DECIDED - before the hover check that decides whether it goes out at all. Read
        // back, the press appeared to happen before the confirmation that permits it.
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        RuleTick tick = engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        RuleInput input = Assert.Single(tick.Inputs);

        // Step one, at the decision, and it is the only step that can name what was picked.
        RuleLogEntry found = Assert.Single(engine.Log.Recent(10));
        Assert.Equal("target found", found.What);
        Assert.Contains("400/4000 10%", found.Detail, StringComparison.Ordinal);
        Assert.Contains("#000000a1", found.Detail, StringComparison.Ordinal);

        // The press is NOT logged yet, because it has not happened yet.
        Assert.DoesNotContain(engine.Log.Recent(10), e => e.What.Contains("KeyPress", StringComparison.Ordinal));

        // What the aim thread reports, in its own order.
        engine.Aimed("ps", "pointer moved", "812,431", 10);
        engine.Aimed("ps", "hover check: confirmed", "#000000a1", 20);
        engine.Aimed("ps", input.Describes, "sent", 25);

        IReadOnlyList<RuleLogEntry> trace = engine.Log.Recent(10);
        Assert.Equal(
            ["KeyPress T", "hover check: confirmed", "pointer moved", "target found"],
            trace.Select(e => e.What));
    }

    [Fact]
    public void AfterACullTheTargetIsLookedAtAgain()
    {
        // The one step that checks the PREMISE. Everything before it is the tool reporting on
        // its own behaviour; none of it says the cull did anything to the monster.
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        engine.Fired("ps", 0);

        // Too early to judge: the key may not even have reached the game.
        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 100);
        Assert.DoesNotContain(engine.Log.Recent(20), Verdict);

        // Once it is due, and the monster is simply gone from the list.
        engine.Evaluate(Around(), RuleEngine.CullCheckMs + 1);

        RuleLogEntry outcome = engine.Log.Recent(1)[0];
        Assert.Equal("target gone", outcome.What);
        Assert.Equal("was 400/4000 10%", outcome.Detail);
        Assert.False(outcome.Blocked);
    }

    [Fact]
    public void ACullThatChangedNothingIsTheFailureWorthSeeing()
    {
        // Wrong skill on that key, no mana, the skill on its own cooldown: the key goes out at
        // a confirmed target and the monster is exactly as healthy as it was. Every other line
        // in the trace reads green.
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        engine.Fired("ps", 0);
        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), RuleEngine.CullCheckMs + 1);

        RuleLogEntry outcome = engine.Log.Recent(1)[0];
        Assert.Equal("target unchanged", outcome.What);
        Assert.True(outcome.Blocked);
        Assert.Contains("400/4000", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACullThatHurtButDidNotKillSaysHowMuch()
    {
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        engine.Fired("ps", 0);
        engine.Evaluate(Around(Rare(0xA1, 90, 4000)), RuleEngine.CullCheckMs + 1);

        RuleLogEntry outcome = engine.Log.Recent(1)[0];
        Assert.Equal("target hurt, still alive", outcome.What);
        Assert.Contains("400 -> 90 (-310)", outcome.Detail, StringComparison.Ordinal);
        Assert.False(outcome.Blocked);
    }

    [Fact]
    public void AWorldThatWentAwayIsNotReportedAsACull()
    {
        // After a portal every monster is "gone". Answering the check from a loading screen
        // would be the verification inventing a result out of the absence of a world.
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        engine.Fired("ps", 0);

        engine.Evaluate(new RuleState { InGame = false }, RuleEngine.CullCheckMs + 1);

        // And the watch is SPENT rather than left to answer from the next area - where the
        // same monster is equally absent, and for a reason that has nothing to do with a cull.
        engine.Evaluate(Around(), RuleEngine.CullCheckMs + 5000);

        // Counting entries would not do: with nothing around, the rule writes "nothing to aim
        // at" on its own account. What must never appear is a VERDICT on the cull.
        Assert.DoesNotContain(engine.Log.Recent(50), Verdict);
    }

    /// <summary>Whether a line is the after-the-cull check reporting on its target.</summary>
    private static bool Verdict(RuleLogEntry entry)
        => entry.What is "target gone" or "target unchanged" or "target hurt, still alive";

    [Fact]
    public void AnAimThatWasNeverConfirmedIsNeverChecked()
    {
        // The check is armed by the PRESS, not by the decision. A rule that found a target and
        // then had its pointer land on nothing has nothing to verify - reporting "target gone"
        // for it would credit a cull that never happened.
        var engine = new RuleEngine(new Random(1));
        engine.Configure(Culling());

        engine.Evaluate(Around(Rare(0xA1, 400, 4000)), 0);
        engine.Aimed("ps", "hover check: nothing there", "wanted #000000a1", 10);

        engine.Evaluate(Around(), RuleEngine.CullCheckMs + 1);
        Assert.DoesNotContain(engine.Log.Recent(50), Verdict);
    }
}
