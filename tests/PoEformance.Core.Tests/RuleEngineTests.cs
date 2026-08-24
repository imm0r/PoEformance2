using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the configured rules decide, and - more to the point - what stops them.
/// </summary>
/// <remarks>
/// The gates are the half worth testing. A rule that fires when it should is visible the first
/// time somebody plays with it; a rule that fires while the player is alt-tabbed types into
/// whatever they alt-tabbed to, and nothing about that is visible from in the game.
/// </remarks>
public class RuleEngineTests
{
    private const string Id = "rule-1";

    private static RuleEffect Press(string key = "Q", int cooldown = 0)
        => new(RuleEffectKind.KeyPress, CooldownMs: cooldown) { Key = key };

    private static Rule OneRule(RuleCondition condition, params RuleEffect[] effects)
        => new(Id, "Test", condition, effects) { Enabled = true };

    private static RuleSettings Profile(params Rule[] rules)
        => new RuleSettings(
            Enabled: true,
            Profile: "Default",
            Profiles: [new RuleProfile("Default", [new RuleGroup("Group", rules) { InTown = true, InHideout = true }])])
        {
            MinInputGapMs = 0,
        }.Normalised();

    private static RuleEngine Engine(RuleSettings settings)
    {
        var engine = new RuleEngine();
        engine.Configure(settings);
        return engine;
    }

    private static RuleState Playing() => new()
    {
        InGame = true,
        GameFocused = true,
        Alive = true,
    };

    [Fact]
    public void ArmsNothingUntilItIsSwitchedOn()
    {
        // First run must press nothing. The settings file arms key presses, so the way for it
        // to fail is a tool that does nothing.
        Assert.False(RuleSettings.Default.Enabled);
        Assert.Empty(RuleSettings.Default.Profiles[0].Groups);

        var engine = new RuleEngine();
        RuleTick tick = engine.Evaluate(Playing(), 0);

        Assert.True(tick.Quiet);
        Assert.Equal("off", tick.Reason);
    }

    [Fact]
    public void SendsAKeyWhenTheConditionHolds()
    {
        RuleEngine engine = Engine(Profile(OneRule(RuleCondition.Of(RuleFact.InGame), Press())));

        RuleInput input = Assert.Single(engine.Evaluate(Playing(), 1000).Inputs);

        Assert.Equal(RuleEffectKind.KeyPress, input.Kind);
        Assert.Equal(0x51, Assert.Single(input.Keys));   // Q
    }

    [Theory]
    [InlineData(false, true, false, "not in game")]
    [InlineData(true, false, false, "game not focused")]
    [InlineData(true, true, true, "a panel is open")]
    public void RefusesToSynthesiseInputOutsideTheGame(bool inGame, bool focused, bool panel, string expected)
    {
        // None of these three is a preference. A key pressed on a loading screen goes
        // somewhere, an unfocused keystroke lands in whatever window has focus, and a panel
        // has its own key handling - so all three are checked in the DECISION, where no
        // caller can reach around them.
        //
        // Conditioned on Alive rather than InGame, so the rule HOLDS in all three cases and it
        // is the gate being tested rather than the condition. On InGame the first case would
        // pass for the wrong reason - the rule not firing at all.
        RuleEngine engine = Engine(Profile(OneRule(RuleCondition.Of(RuleFact.Alive), Press())));

        RuleTick tick = engine.Evaluate(
            new RuleState { InGame = inGame, GameFocused = focused, InPanel = panel, Alive = true },
            1000);

        Assert.Empty(tick.Inputs);
        Assert.Contains(expected, tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StillDrawsWhileAPanelIsOpen()
    {
        // The panel gate is about KEYS. A caption is not input, and hiding it here would be
        // this engine second-guessing the overlay's own panel handling.
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.Text, "up"))));

        RuleTick tick = engine.Evaluate(Playing() with { InPanel = true }, 1000);

        Assert.Single(tick.Drawings);
    }

    [Fact]
    public void MakesItselfNoticedInTheBackgroundOnlyWhenAskedTo()
    {
        RuleSettings settings = Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.Text, "up")));

        RuleState away = Playing() with { GameFocused = false };

        Assert.Empty(Engine(settings).Evaluate(away, 1000).Drawings);
        Assert.Single(Engine(settings with { NoticeInBackground = true }).Evaluate(away, 1000).Drawings);
    }

    [Fact]
    public void ACueIsHeldBackByTheSameSwitchAsACaption()
    {
        // A sound is the half of "noticed" that reaches somebody who is not looking at the
        // screen, so a rule beeping about a rare monster goes on beeping into whatever they
        // alt-tabbed to. Unlike a caption drawn where they cannot see it, that is impossible
        // to ignore - which is why the switch covers both and is not called "draw".
        RuleSettings settings = Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.Sound)));

        RuleState away = Playing() with { GameFocused = false };

        Assert.Empty(Engine(settings).Evaluate(away, 1000).Sounds);
        Assert.Single(Engine(settings with { NoticeInBackground = true }).Evaluate(away, 1000).Sounds);
    }

    [Fact]
    public void HoldsAnEffectToItsCooldown()
    {
        RuleEngine engine = Engine(Profile(OneRule(RuleCondition.Of(RuleFact.InGame), Press(cooldown: 1000))));

        Assert.Single(engine.Evaluate(Playing(), 0).Inputs);
        Assert.Empty(engine.Evaluate(Playing(), 500).Inputs);
        Assert.Single(engine.Evaluate(Playing(), 1000).Inputs);
    }

    [Fact]
    public void KeepsCooldownsAcrossAnEditOfTheRules()
    {
        // Every keystroke in a threshold field republishes the settings. Clearing cooldowns
        // there would hand the belt - or a key rule - a free re-fire per character typed.
        RuleSettings settings = Profile(OneRule(RuleCondition.Of(RuleFact.InGame), Press(cooldown: 5000)));
        RuleEngine engine = Engine(settings);

        Assert.Single(engine.Evaluate(Playing(), 0).Inputs);

        engine.Configure(settings);
        Assert.Empty(engine.Evaluate(Playing(), 100).Inputs);
    }

    [Fact]
    public void PutsAFloorUnderHowFastTheToolMayType()
    {
        // Two rules, neither with a cooldown of its own. The per-rule setting says how often
        // ONE rule may act; the global gap is what stops a profile turning into a stream.
        RuleSettings settings = Profile(
            new Rule("a", "A", RuleCondition.Of(RuleFact.InGame), [Press("Q")]),
            new Rule("b", "B", RuleCondition.Of(RuleFact.InGame), [Press("W")]));

        RuleEngine engine = Engine(settings with { MinInputGapMs = 100 });

        Assert.Single(engine.Evaluate(Playing(), 0).Inputs);
        Assert.Empty(engine.Evaluate(Playing(), 50).Inputs);
        Assert.Single(engine.Evaluate(Playing(), 100).Inputs);
    }

    [Fact]
    public void ARuleHeldBackByTheGlobalGapDoesNotSitOutItsOwnCooldown()
    {
        // It never got its turn, so stamping it as having acted would punish it for something
        // the engine did. The next tick after the gap has to be able to fire it.
        RuleSettings settings = Profile(
            new Rule("a", "A", RuleCondition.Of(RuleFact.InGame), [Press("Q", cooldown: 10_000)]),
            new Rule("b", "B", RuleCondition.Of(RuleFact.InGame), [Press("W", cooldown: 10_000)]));

        RuleEngine engine = Engine(settings with { MinInputGapMs = 100 });

        Assert.Equal("a", Assert.Single(engine.Evaluate(Playing(), 0).Inputs).RuleId);
        Assert.Empty(engine.Evaluate(Playing(), 10).Inputs);
        Assert.Equal("b", Assert.Single(engine.Evaluate(Playing(), 200).Inputs).RuleId);
    }

    [Fact]
    public void AHighPriorityRuleCanSilenceTheOnesBelowIt()
    {
        RuleSettings settings = Profile(
            new Rule("low", "Low", RuleCondition.Of(RuleFact.InGame), [Press("Q")]) { Priority = 10 },
            new Rule("high", "High", RuleCondition.Of(RuleFact.InGame), [Press("W")])
            {
                Priority = 90,
                AllowLower = false,
            });

        RuleInput input = Assert.Single(Engine(settings).Evaluate(Playing(), 0).Inputs);

        Assert.Equal("high", input.RuleId);
    }

    [Fact]
    public void AndLeavesThemAloneWhenItIsNotAskedTo()
    {
        RuleSettings settings = Profile(
            new Rule("low", "Low", RuleCondition.Of(RuleFact.InGame), [Press("Q")]) { Priority = 10 },
            new Rule("high", "High", RuleCondition.Of(RuleFact.InGame), [Press("W")]) { Priority = 90 });

        Assert.Equal(2, Engine(settings with { MinInputGapMs = 0 }).Evaluate(Playing(), 0).Inputs.Count);
    }

    [Fact]
    public void TakesTheKeyFromTheGamesOwnBindingWhenAskedForASlot()
    {
        // The AHK tool's output binding rather than the reference plugin's stored letter. A
        // rebound flask changes what the rule presses without the rule being touched.
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.KeyPress) { KeySource = KeySource.FlaskSlot, Slot = 2 })));

        engine.Bind(new FlaskKeys(new Dictionary<int, ushort> { [2] = 0x52 }, KeyBindingSource.GameConfig, "test"));

        Assert.Equal(0x52, Assert.Single(Assert.Single(engine.Evaluate(Playing(), 0).Inputs).Keys));
    }

    [Fact]
    public void SaysSoWhenThereIsNoKeyToPress()
    {
        // An unbound slot and a condition that never holds look identical from outside, and
        // the fix for each is completely different.
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.KeyPress) { KeySource = KeySource.FlaskSlot, Slot = 4 })));

        RuleTick tick = engine.Evaluate(Playing(), 0);

        Assert.Empty(tick.Inputs);
        Assert.Contains("no key to press", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupRunsOnlyWhereItSaysItDoes()
    {
        var settings = new RuleSettings(
            Enabled: true,
            Profile: "Default",
            Profiles:
            [
                new RuleProfile("Default",
                [
                    new RuleGroup("Maps only", [OneRule(RuleCondition.Of(RuleFact.InGame), Press())])
                    {
                        InTown = false,
                        InHideout = false,
                        InMaps = true,
                    },
                ]),
            ]).Normalised();

        RuleEngine engine = Engine(settings);

        Assert.Empty(engine.Evaluate(Playing() with { InTown = true }, 0).Inputs);
        Assert.Empty(engine.Evaluate(Playing() with { InHideout = true }, 100).Inputs);
        Assert.Single(engine.Evaluate(Playing(), 200).Inputs);
    }

    [Fact]
    public void AnEmptyConditionFiresNothing()
    {
        // The shape a rule has while it is being built. Logic would call an empty "all"
        // vacuously true, which here means a half-drawn rule starts pressing keys.
        RuleEngine engine = Engine(Profile(OneRule(new RuleCondition { Kind = ConditionKind.All }, Press())));

        Assert.Empty(engine.Evaluate(Playing(), 0).Inputs);
    }

    [Fact]
    public void AThresholdDoesNotFireOnANumberNobodyCouldRead()
    {
        // The whole reason an unreadable pool is null rather than 0. On a loading screen the
        // reference plugin's LifePercent reads 0, and every "below 35" rule in the profile
        // fires at once.
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.LifePercent, Compare.AtMost, 35),
            Press())));

        Assert.Empty(engine.Evaluate(Playing(), 0).Inputs);

        var pools = new Vitals(new Vital(30, 100, 0, 0), default, default);
        Assert.Single(engine.Evaluate(Playing() with { Vitals = pools }, 1000).Inputs);
    }

    [Fact]
    public void ACaptionStaysUpForAMomentAfterItsConditionStops()
    {
        // A rule fired by an interval or by a single event is otherwise drawn for one frame,
        // which in practice means never seen.
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.InTown),
            new RuleEffect(RuleEffectKind.Text, "in town") { LingerMs = 500 })));

        Assert.Single(engine.Evaluate(Playing() with { InTown = true }, 1000).Drawings);
        Assert.Single(engine.Evaluate(Playing(), 1200).Drawings);
        Assert.Empty(engine.Evaluate(Playing(), 1600).Drawings);
    }

    [Fact]
    public void ACaptionShowsWhatIsTrueNow_NotWhatFiredIt()
    {
        RuleEngine engine = Engine(Profile(OneRule(
            RuleCondition.Of(RuleFact.InGame),
            new RuleEffect(RuleEffectKind.Text, "life {LifePercent}%"))));

        var pools = new Vitals(new Vital(42, 100, 0, 0), default, default);

        Assert.Equal("life 42%", Assert.Single(engine.Evaluate(Playing() with { Vitals = pools }, 0).Drawings).Text);

        // And an unreadable pool shows as a dash rather than as zero, which on a life caption
        // is the difference between "not loaded" and "about to die".
        Assert.Equal("life -%", Assert.Single(engine.Evaluate(Playing(), 100).Drawings).Text);
    }

    [Fact]
    public void TwoRulesWithTheSameIntervalKeepSeparateClocks()
    {
        // Timer identity comes from the rule's id and the leaf's position, so nobody has to
        // invent a unique name - and a copied rule does not silently share its original's.
        RuleCondition every = new RuleCondition { Fact = RuleFact.EverySeconds, Argument = 1 };
        RuleSettings settings = Profile(
            new Rule("a", "A", every, [Press("Q")]),
            new Rule("b", "B", every, [Press("W")]));

        RuleEngine engine = Engine(settings with { MinInputGapMs = 0 });

        Assert.Equal(2, engine.Evaluate(Playing(), 0).Inputs.Count);
        Assert.Empty(engine.Evaluate(Playing(), 500).Inputs);
        Assert.Equal(2, engine.Evaluate(Playing(), 1000).Inputs.Count);
    }

    [Fact]
    public void ReportsWhyNothingHappened()
    {
        RuleEngine engine = Engine(Profile(OneRule(RuleCondition.Of(RuleFact.InTown), Press())));

        Assert.Contains("watching", engine.Evaluate(Playing(), 0).Reason, StringComparison.Ordinal);
    }
}
