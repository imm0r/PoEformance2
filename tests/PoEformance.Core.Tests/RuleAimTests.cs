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

    /// <summary>A monster at a distance, with an address so it can be aimed at.</summary>
    private static NearMonster At(double distance, ItemRarity rarity, double life, ulong address)
        => new(distance, rarity, (float)distance, 0, life, 10f, address);

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

        NearMonster target = Assert.NotNull(state.AimTarget(100, null, 20));
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

        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(100, null, 10)).Address);
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

        Assert.Null(state.AimTarget(100, null, 10));
    }

    [Fact]
    public void TheRadiusAndTheRarityBothNarrowIt()
    {
        RuleState state = With(
            At(10, ItemRarity.Magic, 5, 0xA),
            At(50, ItemRarity.Rare, 5, 0xB),
            At(500, ItemRarity.Unique, 1, 0xC));

        // Out of range, however low it is.
        Assert.Equal(0xBUL, Assert.NotNull(state.AimTarget(100, null, 20)).Address);

        // And a rarity that is asked for by name excludes the stronger one.
        Assert.Equal(0xAUL, Assert.NotNull(state.AimTarget(100, ItemRarity.Magic, 20)).Address);
        Assert.Null(state.AimTarget(100, ItemRarity.Unique, 20));
    }

    [Fact]
    public void AMonsterWithNoReadableLifeIsNeverAimedAt()
    {
        // Same rule the cull facts follow. A pool that did not resolve is not a monster at
        // zero, and aiming at one would be the tool acting on a number it does not have.
        RuleState state = With(new NearMonster(10, ItemRarity.Rare, 10, 0, null, 10f, 0xA));

        Assert.Null(state.AimTarget(100, null, 100));
    }

    [Fact]
    public void SomethingWithNoAddressCannotBeConfirmedAndSoIsNotAimedAt()
    {
        // The address is what the hover check compares against. Without one the cursor could be
        // placed but never verified, which is the one thing this design exists to avoid.
        RuleState state = With(At(10, ItemRarity.Rare, 5, 0));

        Assert.Null(state.AimTarget(100, null, 20));
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
            AimRadius = 100,
            AimAtOrBelowPercent = 10,
        };

        var rule = new Rule("r", "Power Siphon", RuleCondition.Of(RuleFact.InGame), [effect]) { Enabled = true };
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
    public void AHandEditedAimSpecIsBroughtIntoRange()
    {
        RuleEffect wild = new RuleEffect(RuleEffectKind.KeyPress)
        {
            AimAt = AimTarget.Rare,
            AimRadius = 0,
            AimAtOrBelowPercent = 900,
        }.Normalised();

        // A radius of 0 finds nothing and a threshold of 900 can never be missed; both are how
        // a hand-edited file quietly stops aiming at what it says it aims at.
        Assert.InRange(wild.AimRadius, 1, 10_000);
        Assert.InRange(wild.AimAtOrBelowPercent, 0, 100);
    }
}
