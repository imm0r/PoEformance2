using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The ranges drawn over the game while a rule is being built.
/// </summary>
/// <remarks>
/// The project's own working rule pointed at its rule engine: a radius is a number in a text
/// field, and the honest way to know whether 30 is the right one is to see the circle with the
/// monsters it counts inside it. What is tested here is that the ring describes the rule it
/// came from - a ring that showed the wrong number, or turned green at the wrong moment, would
/// be worse than no ring at all.
/// </remarks>
public class RulePreviewTests
{
    private static RuleState Fighting(params NearMonster[] monsters) => new()
    {
        InGame = true,
        GameFocused = true,
        CursorGround = (100f, 100f),
        Monsters = monsters,
    };

    [Fact]
    public void ARingCarriesWhatItReadsAndWhatItNeeds()
    {
        RuleCondition condition = RuleExpression.Parse("MonsterCountWithin(30) >= 3").Condition!;
        RuleState state = Fighting(
            new NearMonster(10, ItemRarity.Normal),
            new NearMonster(20, ItemRarity.Normal));

        PreviewRing ring = Assert.Single(RulePreview.Rings(condition, state));

        Assert.False(ring.AtCursor);
        Assert.Equal(30, ring.Radius);
        Assert.Equal(2, ring.Reads);
        Assert.False(ring.Holds);
        Assert.Contains("MonsterCountWithin = 2", ring.Label, StringComparison.Ordinal);
        Assert.Contains(">= 3", ring.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ARingSaysSoWhenTheRuleIsSatisfied()
    {
        RuleCondition condition = RuleExpression.Parse("MonsterCountWithin(30) >= 2").Condition!;
        RuleState state = Fighting(
            new NearMonster(10, ItemRarity.Normal),
            new NearMonster(20, ItemRarity.Normal));

        Assert.True(Assert.Single(RulePreview.Rings(condition, state)).Holds);
    }

    [Fact]
    public void ANegatedConditionTurnsTheRingTheOtherWayRound()
    {
        // The ring is green when the RULE is happy, not when the count is high. A "no monsters
        // within 30" condition is satisfied by an empty circle, and a ring that ignored the
        // negation would be lying about the rule it came from.
        RuleCondition condition = RuleExpression.Parse("!(MonsterCountWithin(30) >= 1)").Condition!;

        Assert.True(Assert.Single(RulePreview.Rings(condition, Fighting())).Holds);
        Assert.False(Assert.Single(
            RulePreview.Rings(condition, Fighting(new NearMonster(5, ItemRarity.Normal)))).Holds);
    }

    [Fact]
    public void CursorRingsAndPlayerRingsAreToldApart()
    {
        // Same units, different CENTRE - and the ring has to say which, or a rule aimed at the
        // cursor is checked against a circle drawn round the character.
        RuleCondition condition =
            RuleExpression.Parse("MonsterCountWithin(30) >= 1 && MonsterCountAtCursor(25) >= 1").Condition!;

        IReadOnlyList<PreviewRing> rings = RulePreview.Rings(condition, Fighting());

        Assert.Equal(2, rings.Count);
        Assert.Single(rings, ring => !ring.AtCursor && ring.Radius == 30);
        Assert.Single(rings, ring => ring.AtCursor && ring.Radius == 25);
    }

    [Fact]
    public void OneCircleIsDrawnOnce()
    {
        // Two conditions about the same circle. Drawing both paints the same ring twice, in the
        // same place, at the same size - which reads as one ring and hides that two things are
        // being measured.
        RuleCondition condition = RuleExpression
            .Parse("MonsterCountWithin(40) >= 3 && RareOrUniqueCountWithin(40) >= 1").Condition!;

        PreviewRing ring = Assert.Single(RulePreview.Rings(condition, Fighting()));

        Assert.Contains("MonsterCountWithin", ring.Label, StringComparison.Ordinal);
        Assert.Contains("RareOrUniqueCountWithin", ring.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleWithNoRadiusDrawsNothing()
    {
        Assert.Empty(RulePreview.Rings(RuleExpression.Parse("InMap && Alive").Condition!, Fighting()));
        Assert.Empty(RulePreview.Rings(null, Fighting()));
    }

    [Fact]
    public void DrawingTheRangesNeverConsumesAnInterval()
    {
        // A preview is drawn every frame. An EverySeconds leaf beside a radius one would have
        // its timer ticked by the DRAWING, so the rule sharing that interval never comes round
        // - the debug view changing what it is debugging.
        RuleCondition condition =
            RuleExpression.Parse("EverySeconds(5) && MonsterCountWithin(30) >= 1").Condition!;

        RuleState state = Fighting(new NearMonster(5, ItemRarity.Normal));
        for (int frame = 0; frame < 20; frame++)
        {
            RulePreview.Rings(condition, state);
        }

        var timers = new RuleTimers();
        timers.Tick(0);
        Assert.True(condition.Holds(state, timers, "rule"));
    }

    [Fact]
    public void TheEngineDrawsRangesForTheRuleBeingEdited_EvenWhileItIsOff()
    {
        // A rule is switched off for most of the time it is being built, and refusing to show
        // its ranges then would take the tool away exactly when it is wanted.
        RuleCondition condition = RuleExpression.Parse("MonsterCountWithin(25) >= 2").Condition!;
        var engine = new RuleEngine();
        engine.Configure(new RuleSettings(
            Enabled: true,
            Profile: "P",
            Profiles:
            [
                new RuleProfile("P",
                [
                    new RuleGroup("G", [new Rule("chosen", "Being built", condition, []) { Enabled = false }])
                    {
                        InTown = true,
                        InHideout = true,
                    },
                ]),
            ]));

        RuleState state = Fighting(new NearMonster(5, ItemRarity.Normal));

        // Nothing named: nothing drawn.
        engine.Evaluate(state, 0);
        Assert.Empty(engine.LastPreview);

        engine.PreviewRuleId = "chosen";
        engine.Evaluate(state, 100);
        Assert.Equal(25, Assert.Single(engine.LastPreview).Radius);

        // And a rule that is not there any more stops drawing rather than keeping the last one.
        engine.PreviewRuleId = "gone";
        engine.Evaluate(state, 200);
        Assert.Empty(engine.LastPreview);
    }

    [Fact]
    public void EveryLeafIsListedWithItsVerdict()
    {
        // The rings carry the range counters; everything else in the rule was invisible, so
        // somebody watching a rule that will not fire was left guessing which of the other
        // conditions was the one saying no.
        RuleCondition condition = RuleCondition.All(
            RuleCondition.Of(RuleFact.InMap),
            RuleCondition.Of(RuleFact.Mana, Compare.AtLeast, 2500),
            new RuleCondition { Fact = RuleFact.HasBuff, Text = "lightning_infusion", Negate = true });

        RuleState state = Fighting() with
        {
            InTown = false,
            InHideout = false,
            Vitals = new Vitals(new Vital(100, 100, 0, 0), new Vital(5746, 5746, 0, 0), default),
        };
        IReadOnlyList<PreviewFact> facts = RulePreview.Facts(condition, state);

        Assert.Equal(3, facts.Count);

        Assert.Equal("InMap", facts[0].Label);
        Assert.True(facts[0].Holds);

        Assert.Equal("Mana = 5746, needs >= 2500", facts[1].Label);
        Assert.True(facts[1].Holds);
        Assert.True(facts[1].Known);

        // The buff is not on, and the leaf is negated - so it HOLDS, and says what it asked.
        Assert.Equal("not HasBuff(lightning_infusion)", facts[2].Label);
        Assert.True(facts[2].Holds);
    }

    [Fact]
    public void AnUnreadableNumberIsItsOwnState()
    {
        // "Mana = -, needs >= 2500" is a different problem from "Mana = 1900, needs >= 2500".
        // The engine treats both as "does not hold"; a person debugging must see which.
        RuleCondition condition = RuleCondition.Of(RuleFact.Mana, Compare.AtLeast, 2500);
        IReadOnlyList<PreviewFact> facts = RulePreview.Facts(condition, Fighting());

        PreviewFact mana = Assert.Single(facts);
        Assert.False(mana.Holds);
        Assert.False(mana.Known);
        Assert.StartsWith("Mana = -", mana.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeLeafCarriesItsRadiusInTheLabel()
    {
        RuleCondition condition = new()
        {
            Fact = RuleFact.MonsterCountAtCursor, Argument = 450, Compare = Compare.AtLeast, Value = 3,
        };

        IReadOnlyList<PreviewFact> facts = RulePreview.Facts(condition, Fighting());
        Assert.Contains("(450u)", Assert.Single(facts).Label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListIsBounded()
    {
        RuleCondition condition = RuleCondition.All(
            [.. Enumerable.Range(0, RulePreview.MaxFacts * 2).Select(_ => RuleCondition.Of(RuleFact.InMap))]);

        Assert.Equal(RulePreview.MaxFacts, RulePreview.Facts(condition, Fighting()).Count);
    }
}
