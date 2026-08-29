using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The planner deciding WHICH WAY to roll, not just whether to.
/// </summary>
/// <remarks>
/// WHY STEERING EXISTS AT ALL, in the owner's own words: a boss channels a beam at you, and you
/// are pointing at the boss, because that is what you do when you are fighting one. The game
/// rolls where you are pointing. So a tool that only presses the dodge key at the right moment
/// rolls you straight along the beam - correct timing, fatal direction.
///
/// The geometry that separates those is tested in <see cref="EscapeTests"/> and the screen-to-
/// world conversion in <see cref="ScreenBasisTests"/>; this is the layer that joins them to the
/// gates, and what it has to get right is mostly about REFUSING: not steering when it cannot
/// work out which way is which, not rolling when nowhere is better, and not quietly steering with
/// half the keys.
/// </remarks>
public class EvasionSteeringTests
{
    private const ushort SomeKey = 0x20; // space

    /// <summary>
    /// Where the player stands. NOT the world origin, and that is not decoration.
    /// </summary>
    /// <remarks>
    /// The planner reads a target of exactly (0, 0) as "the wrapper would not read" - see
    /// <see cref="EvasionPlanner"/> - so a test built around the world origin has every one of
    /// its threats silently discarded and reads as a feature that does nothing. Real areas put
    /// the player in the thousands, so this does too, and every coordinate below is an offset
    /// from it and stays legible.
    /// </remarks>
    private const float PlayerX = 5000;
    private const float PlayerY = 5000;

    /// <summary>
    /// A camera whose basis is exactly "up is world -Y, right is world +X".
    /// </summary>
    /// <remarks>
    /// Orthographic and centred on the player - column-major, matching
    /// <see cref="WorldToScreen"/>: ndcX = (x - player)/1000, ndcY = -(y - player)/1000, w = 1.
    /// Deliberately not a realistic isometric camera, because what these tests are about is the
    /// DECISION, and a realistic one would make every expected direction a diagonal for no gain.
    /// The real article is put to a real matrix in <c>ScreenBasisTests</c>, which is the right
    /// place to ask whether the conversion is right.
    /// </remarks>
    private static float[] Camera()
    {
        var matrix = new float[16];
        matrix[0] = 0.001f;                 // ndcX = +(x - player)
        matrix[12] = -0.001f * PlayerX;
        matrix[5] = -0.001f;                // ndcY = -(y - player), so screen up is world -Y
        matrix[13] = 0.001f * PlayerY;
        matrix[15] = 1f;
        return matrix;
    }

    private static WorldSnapshot World(params WorldEntity[] monsters)
    {
        var player = new WorldEntity(
            1, 0x1000, "Metadata/Characters/Int", EntityKind.Player, PlayerX, PlayerY, 0);
        return new WorldSnapshot(true, player, [player, .. monsters], Camera());
    }

    /// <summary>
    /// A monster committed to an action, in coordinates RELATIVE TO THE PLAYER.
    /// </summary>
    /// <param name="dx">Where it stands, and where its action starts.</param>
    /// <param name="tdx">Where the action is aimed. (0, 0) is the player's own feet.</param>
    private static WorldEntity Monster(
        float dx, float dy, float tdx, float tdy,
        ItemRarity rarity = ItemRarity.Unique, uint id = 7)
        => new(
            id, 0x2000 + id, "Metadata/Monsters/Boss/Boss", EntityKind.Monster,
            PlayerX + dx, PlayerY + dy, 0,
            Rarity: rarity,
            Name: "Boss",
            Action: new ActorAction(
                ActionKind.Skill, 2, PlayerX + tdx, PlayerY + tdy, PlayerX + dx, PlayerY + dy, 0, 195));

    /// <summary>
    /// Four lines through the player, covering all eight directions a roll can take.
    /// </summary>
    /// <remarks>
    /// Only the first is AIMED at the player - the rest are aimed past them. That is deliberate
    /// and it is the shape of the situation being tested: one attack coming for you, and enough
    /// else in flight that there is nowhere to put yourself. The other three still count towards
    /// where it is safe to land, which is the whole point of the steering seeing more than the
    /// gates admit.
    /// </remarks>
    private static WorldSnapshot Surrounded() => World(
        Monster(-3000, 0, 0, 0, id: 1),
        Monster(0, -3000, 0, 3000, id: 2),
        Monster(-3000, -3000, 3000, 3000, id: 3),
        Monster(-3000, 3000, 3000, -3000, id: 4));

    private static EvasionSettings Settings(
        bool steer = true,
        int key = SomeKey,
        ItemRarity warnFrom = ItemRarity.Normal,
        ItemRarity actFrom = ItemRarity.Normal,
        MovementKeys? keys = null)
        => new(
            Warn: new EvasionGate(true, warnFrom),
            Act: new EvasionGate(true, actFrom),
            DangerRadius: 90f,
            CooldownMs: 0,
            DodgeKey: key,
            Steer: steer,
            RollDistance: 400f,
            Keys: keys,
            OnlyDangerousAnimations: false);

    private static EvasionTick Run(EvasionSettings settings, WorldSnapshot world)
        => new EvasionPlanner(settings).Evaluate(world, AnimationNames.Empty, true, 1000);

    [Fact]
    public void ABeamAimedAtThePlayerIsRolledAcrossIt()
    {
        // THE CASE THE FEATURE WAS BUILT FOR. The boss is up the screen and its action is aimed
        // at the character's feet, so along the beam - towards the boss or away from it - stays
        // in it. Across is the only direction that leaves.
        EvasionTick tick = Run(Settings(), World(Monster(0, -1500, tdx: 0, tdy: 0)));

        Assert.True(tick.Dodge);
        Assert.Contains(tick.Steer, new[] { MoveDirection.Left, MoveDirection.Right });

        // And it says so, because a direction chosen invisibly cannot be argued with.
        Assert.Contains(tick.Steer.ToString(), tick.Reason, StringComparison.Ordinal);
        Assert.Contains("units clear", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SteeringOffLeavesTheDirectionToThePlayer()
    {
        // The default, and a good mode rather than a degraded one: the tool supplies the timing
        // and the player keeps the steering. Hours of play went through exactly this.
        EvasionTick tick = Run(Settings(steer: false), World(Monster(0, -1500, 0, 0)));

        Assert.True(tick.Dodge);
        Assert.Equal(MoveDirection.None, tick.Steer);
        Assert.DoesNotContain("units clear", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SteeringAvoidsAThreatThatWasNeverWorthDrawing()
    {
        // The gates say what is worth a MARKER and what is worth a KEYSTROKE. Where to land is a
        // third question, and a white monster's slam makes a spot dangerous whether or not
        // anybody wanted a ring drawn round it - so the steering sees everything even when the
        // overlay is set to show only bosses.
        WorldSnapshot world = World(
            Monster(0, -1500, tdx: 0, tdy: 0, rarity: ItemRarity.Unique, id: 1),
            Monster(-1000, 0, tdx: -400, tdy: 0, rarity: ItemRarity.Normal, id: 2));

        EvasionTick tick = Run(
            Settings(warnFrom: ItemRarity.Unique, actFrom: ItemRarity.Unique), world);

        Threat drawn = Assert.Single(tick.Draw);
        Assert.Equal(ItemRarity.Unique, drawn.Rarity);

        // Left is across the beam and straight into the slam nobody asked to see. Right is the
        // only direction that is across one and clear of the other.
        Assert.True(tick.Dodge);
        Assert.Equal(MoveDirection.Right, tick.Steer);
    }

    [Fact]
    public void NowhereSaferMeansNoRollAtAll()
    {
        // Surrounded. Rolling would spend the charge AND take the player's aim away for the
        // length of it, to end up just as exposed - so it declines, and says which it was.
        EvasionTick tick = Run(Settings(), Surrounded());

        Assert.False(tick.Dodge);
        Assert.Equal(MoveDirection.None, tick.Steer);
        Assert.Contains("no direction is any safer", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusingToSteerDoesNotSpendTheCooldown()
    {
        // The corollary, and a bug if it were missed: a tick that declined to roll must leave
        // the next one free to. Otherwise standing in a bad spot for one read would put the
        // dodge on cooldown for the moment the player steps out of it.
        var planner = new EvasionPlanner(Settings() with { CooldownMs = 5000 });

        Assert.False(planner.Evaluate(Surrounded(), AnimationNames.Empty, true, 1_000).Dodge);

        // One beam, a moment later: it rolls, rather than reporting a cooldown it never used.
        WorldSnapshot single = World(Monster(0, -1500, 0, 0));
        Assert.True(planner.Evaluate(single, AnimationNames.Empty, true, 1_100).Dodge);
    }

    [Fact]
    public void ACameraThatCannotAnswerStillRolls()
    {
        // The one place refusing would be wrong. "I could not work out which way is which" must
        // not become "you take the hit": the unsteered roll is the behaviour this feature had
        // for hours before steering existed, and the player is still pointing somewhere.
        var player = new WorldEntity(
            1, 0x1000, "Metadata/Characters/Int", EntityKind.Player, PlayerX, PlayerY, 0);
        var blind = new WorldSnapshot(
            true, player, [player, Monster(0, -1500, 0, 0)], new float[16]);

        EvasionTick tick = Run(Settings(), blind);

        Assert.True(tick.Dodge);
        Assert.Equal(MoveDirection.None, tick.Steer);
        Assert.Contains("unsteered", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingMovementKeyStopsSteeringRatherThanHalfDoingIt()
    {
        // Three keys out of four would silently remove three of the eight directions, and the
        // tool would then pick the best of what was left and roll there with nothing to say a
        // better direction was never considered.
        EvasionSettings settings = Settings(keys: MovementKeys.Default with { Left = 0 });
        Assert.False(settings.CanSteer);

        EvasionTick tick = Run(settings, World(Monster(0, -1500, 0, 0)));
        Assert.True(tick.Dodge);
        Assert.Equal(MoveDirection.None, tick.Steer);
    }

    [Fact]
    public void TheChosenDirectionMapsBackToKeysToHold()
    {
        // The join between the decision and the keyboard. The index IS the direction, so this
        // cannot drift out of step with a parallel table - there is no parallel table.
        EvasionTick tick = Run(Settings(), World(Monster(0, -1500, 0, 0)));
        IReadOnlyList<ushort> keys = MovementKeys.Default.KeysFor(tick.Steer);

        ushort expected = tick.Steer == MoveDirection.Left
            ? (ushort)MovementKeys.Default.Left
            : (ushort)MovementKeys.Default.Right;
        Assert.Equal([expected], keys);
    }

    [Fact]
    public void SteeringNeedsADodgeKeyLikeEverythingElse()
    {
        Assert.False(Settings(key: 0).CanSteer);
    }
}
