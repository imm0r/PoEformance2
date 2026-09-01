using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Damaging ground as an input to the dodge.
/// </summary>
/// <remarks>
/// SYNTHETIC ON PURPOSE, and it is worth saying why rather than leaving it to look like laziness.
/// The two committed captures were checked: the closest a ground effect ever comes to the player
/// is 38 world units in one and 44 in the other, so NOBODY EVER STANDS IN ONE in the recorded
/// material. A replay cannot exercise the case this feature exists for, and a test built on one
/// would be testing that nothing happens.
///
/// What the captures DID settle is written into the code they justify: no ground effect is ever
/// marked friendly, so filtering on that flag would be protection in name only.
/// </remarks>
public class GroundEvasionTests
{
    private static readonly IReadOnlyList<EscapeOption> Compass =
    [
        new(0, 0, 1), new(1, 1, 0), new(2, 0, -1), new(3, -1, 0),
    ];

    private static WorldEntity Ground(float x, float y, int type) =>
        new(1, 0x9000, "Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect",
            EntityKind.Effect, x, y, 0, IsGroundEffect: true, GroundType: type);

    private static WorldEntity Player(float x, float y) =>
        new(9, 0x1, "Metadata/Characters/Player", EntityKind.Player, x, y, 0);

    private static WorldSnapshot Area(WorldEntity player, params WorldEntity[] rest) =>
        new(InGame: true, Player: player, Entities: [player, .. rest], Matrix: new float[16]);

    private static GroundEffectTypeTable Types()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "ground-effect-types.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return GroundEffectTypeTable.Load(
            null,
            Path.Combine(dir.FullName, "data", "ground-tables.json"),
            Path.Combine(dir.FullName, "data", "ground-effect-types.json"));
    }

    // ── The geometry ───────────────────────────────────────────────────────

    [Fact]
    public void StandingInsideAPatchScoresBelowZero()
    {
        // THE WHOLE TRICK, and the reason "roll out of the fire" needed no special case: a
        // threat's score is a raw distance and nothing is ever inside it, where ground has an
        // edge. Taking the radius off puts the middle of a burning patch below every spot
        // outside it, which is an ordering the direction chooser already knew how to act on.
        var patch = new GroundHazard(0, 0, 20f, "IgnitedGround");

        Assert.True(Escape.SafetyFrom(patch, 0, 0) < 0);
        Assert.Equal(-20, Escape.SafetyFrom(patch, 0, 0), 3);
        Assert.Equal(0, Escape.SafetyFrom(patch, 20, 0), 3);
        Assert.Equal(30, Escape.SafetyFrom(patch, 50, 0), 3);
    }

    [Fact]
    public void ADodgeWillNotLandInBurningGround()
    {
        // The case the avoidance switch exists for. North is clear, east has fire in it, and
        // without the ground in the scoring both score identically on the threat alone.
        var patch = new GroundHazard(100, 0, 40f, "IgnitedGround");

        EscapeChoice? withGround = Escape.Best(
            [], [patch], Compass, playerX: 0, playerY: 0, rollDistance: 100);

        Assert.NotNull(withGround);
        Assert.NotEqual(1, withGround.Value.Index);
    }

    [Fact]
    public void RollingOutOfAPatchBeatsStandingInIt()
    {
        // Standing in it scores negative, so any direction that leaves scores higher - which is
        // how the escape falls out of the existing "is this better than standing still" rule.
        var patch = new GroundHazard(0, 0, 20f, "IgnitedGround");

        EscapeChoice? choice = Escape.Best(
            [], [patch], Compass, playerX: 0, playerY: 0, rollDistance: 100);

        Assert.NotNull(choice);
        Assert.True(choice.Value.Safety > 0, "the roll should end outside the patch");
    }

    [Fact]
    public void AnEscapeThatLandsInASecondPatchIsNotAnEscape()
    {
        // The worst case over every danger, not the sum - the rule the threats already followed
        // and the one that stops a roll trading one fire for another.
        var here = new GroundHazard(0, 0, 20f, "IgnitedGround");
        var north = new GroundHazard(0, 100, 40f, "CausticCloud");

        EscapeChoice? choice = Escape.Best(
            [], [here, north], Compass, playerX: 0, playerY: 0, rollDistance: 100);

        Assert.NotNull(choice);
        Assert.NotEqual(0, choice.Value.Index);
    }

    [Fact]
    public void GroundAloneIsEnoughToChooseADirection()
    {
        // Best used to refuse when there were no threats. Ground is a danger with no monster
        // behind it, so a list of only patches has to be answerable - otherwise the escape
        // switch would be on and silent whenever nothing was winding up, which is exactly when
        // it is meant to work.
        Assert.NotNull(Escape.Best(
            [], [new GroundHazard(0, 0, 20f, "IgnitedGround")], Compass, 0, 0, 100));

        // And nothing at all still refuses.
        Assert.Null(Escape.Best([], [], Compass, 0, 0, 100));
    }

    // ── The planner ────────────────────────────────────────────────────────

    private static EvasionPlanner Armed(EvasionSettings settings) =>
        new(settings with
        {
            Warn = new EvasionGate(true, ItemRarity.Normal),
            Act = new EvasionGate(true, ItemRarity.Normal),
            DodgeKey = 0x20,
        })
        { GroundTypes = Types() };

    [Fact]
    public void StandingInHarmfulGroundIsAReasonToRoll()
    {
        // The new trigger, and the only one in this planner that is not about an incoming action.
        EvasionPlanner planner = Armed(EvasionSettings.Default with
        {
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 0)), AnimationNames.Empty, gameFocused: true, nowMs: 10_000);

        Assert.True(tick.Dodge);
        Assert.Contains("IgnitedGround", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheEscapeSwitchOffItWatchesAndDoesNothing()
    {
        // Off by default, because this presses the key where the tool used to do nothing at all.
        EvasionPlanner planner = Armed(EvasionSettings.Default with { GroundRadius = 20f });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 0)), AnimationNames.Empty, true, 10_000);

        Assert.False(tick.Dodge);
        Assert.False(EvasionSettings.Default.EscapeGroundEffects);

        // And it says it can SEE the patch. "watching (nothing incoming)" on a screen full of
        // fire is the shape of readout that sends somebody hunting a bug that is not there.
        Assert.Contains("patch", tick.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpfulGroundIsNotSomethingToRollOutOf()
    {
        // THE PAYOFF OF CLASSIFYING THE TABLE. Six of its 53 rows grant something, and rolling
        // off a Consecration would be the tool actively making things worse. Row 6 is that one.
        EvasionPlanner planner = Armed(EvasionSettings.Default with
        {
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 6)), AnimationNames.Empty, true, 10_000);

        Assert.False(tick.Dodge);
    }

    [Fact]
    public void GroundNobodyCanNameCountsAsHarmful()
    {
        // Uncertainty resolves towards moving: leaving a neutral patch costs a roll charge, and
        // staying in a burning one costs life. A planner with NO table at all is the same case -
        // it must not quietly decide everything is safe.
        var planner = new EvasionPlanner(EvasionSettings.Default with
        {
            Warn = new EvasionGate(true, ItemRarity.Normal),
            Act = new EvasionGate(true, ItemRarity.Normal),
            DodgeKey = 0x20,
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 999)), AnimationNames.Empty, true, 10_000);

        Assert.True(tick.Dodge);
    }

    [Fact]
    public void GroundThatBurnedOutIsNotRolledAwayFrom()
    {
        // A remembered sighting is ground the game stopped listing, which for ground means it is
        // over. Steering around one would be steering around nothing, and would keep doing it.
        EvasionPlanner planner = Armed(EvasionSettings.Default with
        {
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 0) with { RememberedForMs = 500 }),
            AnimationNames.Empty, true, 10_000);

        Assert.False(tick.Dodge);
    }

    [Fact]
    public void StandingWellClearOfAPatchIsNotAReasonToRoll()
    {
        // The radius is a guess, so the one thing this must not do is fire on ground that is
        // merely nearby. 38 world units is the closest approach in either committed capture.
        EvasionPlanner planner = Armed(EvasionSettings.Default with
        {
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        });

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(38, 0, 0)), AnimationNames.Empty, true, 10_000);

        Assert.False(tick.Dodge);
    }

    [Fact]
    public void EscapingStillNeedsActingToBeSwitchedOn()
    {
        // Pressing a key is the act gate's business, whatever prompted it. The gate's RARITY
        // floor is not consulted - a patch of fire has no rarity - but its on/off switch is.
        var planner = new EvasionPlanner(EvasionSettings.Default with
        {
            Warn = new EvasionGate(true, ItemRarity.Normal),
            Act = new EvasionGate(false, ItemRarity.Normal),
            DodgeKey = 0x20,
            EscapeGroundEffects = true,
            GroundRadius = 20f,
        })
        { GroundTypes = Types() };

        EvasionTick tick = planner.Evaluate(
            Area(Player(0, 0), Ground(5, 0, 0)), AnimationNames.Empty, true, 10_000);

        Assert.False(tick.Dodge);
        Assert.Contains("acting is off", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsAvoidWithoutPressing()
    {
        // The split the two switches exist for: changing the direction of a roll that was going
        // to happen anyway is free, and pressing the key in a new situation is the user's call.
        Assert.True(EvasionSettings.Default.AvoidGroundEffects);
        Assert.False(EvasionSettings.Default.EscapeGroundEffects);
        Assert.True(EvasionSettings.Default.UsesGround);

        // A radius of zero would make a patch a bare point and the switch would never fire.
        Assert.True((EvasionSettings.Default with { GroundRadius = 0f }).Normalised().GroundRadius >= 2f);
    }
}
