using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Choosing what an aiming effect points the cursor at.
/// </summary>
/// <remarks>
/// The half a threshold rule was missing for a skill that has to be POINTED at its target.
/// "A rare within range is nearly dead" is a fact about the area; a cull needs the cursor on
/// that rare, and before this the rule pressed its key at whatever happened to be under the
/// pointer - a cast into empty floor that looked, from outside, exactly like the feature
/// working.
/// </remarks>
public class RuleAimTests
{
    private static RuleState With(params NearMonster[] monsters) => new()
    {
        InGame = true,
        GameFocused = true,
        Alive = true,
        Monsters = [.. monsters.OrderBy(m => m.Distance)],
    };

    /// <summary>A targetable monster at a distance, with an address so it can be aimed at.</summary>
    private static NearMonster At(double distance, ItemRarity rarity, double life, ulong address, bool targetable = true)
        => new(distance, rarity, (float)distance, 0, life, 10f, address, Targetable: targetable);

    /// <summary>The region an aim looks in: a count within a radius of the player.</summary>
    private static RuleCondition Within(double radius, RuleFact fact = RuleFact.MonsterCountWithin)
        => RuleCondition.Of(fact, Compare.AtLeast, 1) with { Argument = radius };

    [Fact]
    public void TakesTheSTRONGESTThingUnderTheThreshold()
    {
        // The owner's call, and the opposite of what the facts answer. With two things under
        // the threshold at once the rare is the one worth a cull - the white monster beside it
        // dies to anything, and spending the cast on it wastes the window on the rare.
        RuleState state = With(
            At(10, ItemRarity.Normal, 2, 0xA),
            At(20, ItemRarity.Rare, 8, 0xB),
            At(30, ItemRarity.Magic, 4, 0xC));

        NearMonster target = Assert.NotNull(state.AimTarget(Within(100), null, 20));
        Assert.Equal(0xBUL, target.Address);
        Assert.Equal(ItemRarity.Rare, target.Rarity);
    }

    [Fact]
    public void AmongEqualsItTakesTheOneClosestToDying()
    {
        // Ties inside a rarity go to the lowest bar: among equals that is the one the cast is
        // most likely to land on before something else kills it.
        RuleState state = With(
            At(10, ItemRarity.Rare, 9, 0xA),
            At(20, ItemRarity.Rare, 3, 0xB),
            At(30, ItemRarity.Rare, 7, 0xC));

        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(Within(100), null, 10)).Address);
    }

    [Fact]
    public void AThresholdItCannotMeetAimsAtNothing()
    {
        // Not "the least healthy of the healthy ones". A rule whose aim spec disagrees with its
        // own condition must find NOTHING, so it reports rather than pressing a key at a
        // monster that is nowhere near dead.
        RuleState state = With(
            At(10, ItemRarity.Rare, 60, 0xA),
            At(20, ItemRarity.Unique, 55, 0xB));

        Assert.Null(state.AimTarget(Within(100), null, 10));
    }

    [Fact]
    public void TheRadiusAndTheRarityBothNarrowIt()
    {
        RuleState state = With(
            At(10, ItemRarity.Magic, 5, 0xA),
            At(50, ItemRarity.Rare, 5, 0xB),
            At(500, ItemRarity.Unique, 1, 0xC));

        // Out of range, however low it is.
        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(Within(100), null, 20)).Address);

        // And a rarity that is asked for by name excludes the stronger one.
        Assert.Equal(0xAUL, Assert.NotNull(state.AimTarget(Within(100), ItemRarity.Magic, 20)).Address);
        Assert.Null(state.AimTarget(Within(100), ItemRarity.Unique, 20));
    }

    [Fact]
    public void AMonsterWithNoReadableLifeIsNeverAimedAt()
    {
        // Same rule the cull facts follow. A pool that did not resolve is not a monster at
        // zero, and aiming at one would be the tool acting on a number it does not have.
        RuleState state = With(new NearMonster(10, ItemRarity.Rare, 10, 0, null, 10f, 0xA, Targetable: true));

        Assert.Null(state.AimTarget(Within(100), null, 100));
    }

    [Fact]
    public void SomethingWithNoAddressCannotBeConfirmedAndSoIsNotAimedAt()
    {
        // The address is what the hover check compares against. Without one the cursor could be
        // placed but never verified, which is the one thing this design exists to avoid.
        RuleState state = With(At(10, ItemRarity.Rare, 5, 0));

        Assert.Null(state.AimTarget(Within(100), null, 20));
    }

    [Fact]
    public void AnAimingRuleThatFindsNothingReportsItAndDoesNotFire()
    {
        // "Nothing to aim at" and "the condition never held" look identical from outside and
        // want completely different fixes - the same argument that made "no key to press" a
        // reported state rather than a silent skip.
        var effect = new RuleEffect(RuleEffectKind.KeyPress)
        {
            Key = "R",
            AimAt = AimTarget.Rare,
            AimAtOrBelowPercent = 10,
        };

        var rule = new Rule("r", "Power Siphon", Within(100), [effect]) { Enabled = true };
        var settings = new RuleSettings(true, "P", [new RuleProfile("P", [new RuleGroup("G", [rule])])])
        {
            MinInputGapMs = 0,
            CooldownJitterMs = 0,
        };

        var engine = new RuleEngine(new Random(1));
        engine.Configure(settings);

        // Healthy rare only: the condition holds, the aim finds nothing.
        RuleTick quiet = engine.Evaluate(With(At(10, ItemRarity.Rare, 80, 0xA)), 0);
        Assert.Empty(quiet.Inputs);
        Assert.Contains("aim", quiet.Reason, StringComparison.OrdinalIgnoreCase);

        // It has NOT been stamped as acted, so the very next tick can fire once one appears.
        RuleTick fired = engine.Evaluate(With(At(10, ItemRarity.Rare, 6, 0xB)), 1);
        RuleInput input = Assert.Single(fired.Inputs);

        AimPoint aim = Assert.NotNull(input.Aim);
        Assert.Equal(0xBUL, aim.Address);
        Assert.Equal(10f, aim.Z);
    }

    [Fact]
    public void SkipsWhatTheGameWouldNotLetAClickLandOn()
    {
        // A boss between phases is the strongest thing in range and reads untargetable. Aimed
        // at, the pointer lands, the hover check fails and the cast is skipped - a rule that
        // fires and does nothing. The cast goes to the next thing the rule is about instead.
        RuleState state = With(
            At(10, ItemRarity.Unique, 5, 0xA, targetable: false),
            At(20, ItemRarity.Rare, 8, 0xB));

        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(Within(100), null, 20)).Address);

        // And with nothing targetable in range there is nothing to aim at, however low it is.
        Assert.Null(With(At(10, ItemRarity.Unique, 5, 0xA, targetable: false)).AimTarget(Within(100), null, 20));
    }

    [Fact]
    public void LooksWhereTheRulesRangeConditionsLook()
    {
        // The region is the rule's, not the effect's. A rule about what is under the CURSOR
        // aims at what is under the cursor, however near the player something else stands.
        RuleState state = With(
            At(10, ItemRarity.Rare, 5, 0xA),
            new NearMonster(300, ItemRarity.Normal, 305, 300, 5, 10f, 0xB, Targetable: true))
            with { CursorGround = (300f, 300f) };

        RuleCondition atCursor = Within(30, RuleFact.MonsterCountAtCursor);
        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(atCursor, null, 20)).Address);

        // Every range condition in the tree counts, at any depth: the rare near the player is
        // in the OR's second region, and it is the stronger of the two.
        RuleCondition either = RuleCondition.All(
            RuleCondition.Of(RuleFact.InGame),
            RuleCondition.Any(atCursor, Within(50)));
        Assert.Equal(0xAUL, Assert.NotNull(state.AimTarget(either, null, 20)).Address);

        // A region's own filter holds for the aim as it does for the count: beside a
        // rares-and-uniques condition the white monster is not among the candidates.
        RuleCondition rares = Within(30, RuleFact.RareOrUniqueCountAtCursor);
        Assert.Null(state.AimTarget(rares, null, 20));
    }

    [Fact]
    public void ARuleWithNoRangeConditionHasNowhereToAim()
    {
        // Reported under its own reason: "nothing to aim at" is a room that says no, this is
        // a rule written wrong, and only the second one needs the editor opened.
        var effect = new RuleEffect(RuleEffectKind.KeyPress) { Key = "R", AimAt = AimTarget.AnyMonster };
        var rule = new Rule("r", "Living Bomb", RuleCondition.Of(RuleFact.InGame), [effect]) { Enabled = true };
        var settings = new RuleSettings(true, "P", [new RuleProfile("P", [new RuleGroup("G", [rule])])])
        {
            MinInputGapMs = 0,
            CooldownJitterMs = 0,
        };

        var engine = new RuleEngine(new Random(1));
        engine.Configure(settings);

        RuleTick tick = engine.Evaluate(With(At(10, ItemRarity.Rare, 50, 0xA)), 0);
        Assert.Empty(tick.Inputs);
        Assert.Contains("no range condition", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEffectThatDrawsNeverTakesTheCursor()
    {
        // Moving the player's mouse to place a caption would be the tool reaching into the game
        // to change something it was only asked to describe.
        var caption = new RuleEffect(RuleEffectKind.Text) { AimAt = AimTarget.Rare };
        Assert.False(caption.Aims);

        var press = new RuleEffect(RuleEffectKind.KeyPress) { AimAt = AimTarget.Rare };
        Assert.True(press.Aims);

        // And an effect nobody asked to aim keeps the old behaviour exactly.
        Assert.False(new RuleEffect(RuleEffectKind.KeyPress).Aims);
    }

    [Fact]
    public void WhatTheAimDidIsKeptWhereSomethingCanShowIt()
    {
        // The half that was missing when this was first built: the sequence that places the
        // pointer runs on its own thread and finishes AFTER the tick that started it, so every
        // outcome it had - confirmed, missed, pulled away - was handed to a callback that threw
        // it away. "It never fires" and "it aims and the confirmation rejects it" are different
        // problems, and neither was visible anywhere.
        var engine = new RuleEngine(new Random(1));

        Assert.Equal(string.Empty, engine.AimNote(0));

        engine.Aimed("power-siphon", "on target", string.Empty, 10_000);

        // The age is part of it: an outcome is an event, so a bare line cannot say whether it
        // is happening now or left over from the last map.
        Assert.Equal("on target (just now)", engine.AimNote(10_400));
        Assert.Equal("on target (3 s ago)", engine.AimNote(13_500));

        // And a clock that went backwards - which a test clock does - reads as "just now"
        // rather than as a negative age.
        Assert.Equal("on target (just now)", engine.AimNote(9_000));

        engine.Forget();
        Assert.Equal(string.Empty, engine.AimNote(20_000));
    }

    [Fact]
    public void AHandEditedAimSpecIsBroughtIntoRange()
    {
        RuleEffect wild = new RuleEffect(RuleEffectKind.KeyPress)
        {
            AimAt = AimTarget.Rare,
            AimAtOrBelowPercent = 900,
        }.Normalised();

        // A threshold of 900 can never be missed, which is how a hand-edited file quietly
        // stops aiming at what it says it aims at.
        Assert.InRange(wild.AimAtOrBelowPercent, 0, 100);
    }
}
