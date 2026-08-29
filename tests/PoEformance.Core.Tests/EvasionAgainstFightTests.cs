using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The planner against the real fight, rather than against monsters somebody made up.
/// </summary>
/// <remarks>
/// THE SYNTHETIC TESTS PROVE THE RULES; THIS PROVES THEY FIRE. Every gate in
/// <see cref="EvasionPlannerTests"/> is checked on entities built to check it, and a feature can
/// pass all of that and still see nothing in a real area - because the actions never read, or
/// because the thresholds are set where nothing in a real fight lands.
///
/// So this replays <c>session-2026-08-monsters.rec</c>, 130 seconds in which 54 monsters
/// attacked the player, and asks what the planner would have shown and done. The recording is
/// the same one that settled the offsets, which is the point: the numbers it produces here are
/// the ones the overlay would have drawn that evening.
/// </remarks>
public class EvasionAgainstFightTests
{
    private const string Fixture = "session-2026-08-monsters.rec";

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", Fixture);
        }
    }

    /// <summary>Replays every frame through the planner. Cached - it walks the whole session.</summary>
    private static readonly Lazy<(List<EvasionTick> Ticks, int Frames)> Session = new(() => Replay(
        new EvasionSettings(
            Warn: new EvasionGate(true, ItemRarity.Normal),
            Act: new EvasionGate(true, ItemRarity.Normal),
            DangerRadius: 90f,
            CooldownMs: 1200,
            DodgeKey: 0x20,
            OnlyDangerousAnimations: false)));

    private static (List<EvasionTick> Ticks, int Frames) Replay(EvasionSettings settings)
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();

        // The reader must be asked for actions, exactly as the app asks when the feature is on.
        var world = new WorldReader(replay, schema) { ReadActions = true };
        var planner = new EvasionPlanner(settings);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var ticks = new List<EvasionTick>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);

            // The recording's own clock, so the cooldown behaves as it did live rather than
            // being handed a fresh millisecond per frame.
            ticks.Add(planner.Evaluate(snapshot, AnimationNames.Empty, true, replay.FrameTimes[(int)frame]));
        }

        return (ticks, (int)replay.FrameCount);
    }

    [Fact]
    public void TheRealFightProducesThreatsToDraw()
    {
        (List<EvasionTick> ticks, int frames) = Session.Value;

        int withThreats = ticks.Count(t => t.Draw.Count > 0);
        Assert.True(frames > 1_500, $"fixture has only {frames} frames");
        Assert.True(withThreats > 300, $"only {withThreats} of {frames} frames had anything to draw");

        // And they are real places, not decoded zeroes: a threat with no target is filtered out
        // by the planner, so every one that survives has somewhere to put a marker.
        Assert.All(ticks.SelectMany(t => t.Draw).Take(500), threat =>
        {
            Assert.NotEqual(0f, threat.TargetX);
            Assert.True(threat.DistanceToPlayer >= 0);
        });
    }

    [Fact]
    public void SomeOfThoseThreatsAreAimedAtThePlayerAndWouldHaveDodged()
    {
        // The half that presses a key. A session in which nothing is ever aimed at the player
        // would mean the radius is set where no real attack lands - a feature that draws
        // beautifully and never acts.
        (List<EvasionTick> ticks, _) = Session.Value;

        int aimed = ticks.Count(t => t.AimedCount > 0);
        int dodges = ticks.Count(t => t.Dodge);

        Assert.True(aimed > 50, $"only {aimed} frames had an action aimed at the player");
        Assert.True(dodges > 0, "the planner never decided to dodge in a whole fight");

        // The cooldown has to be doing its job: a wind-up is committed for many frames, so a
        // dodge per aimed frame would be the bug the cooldown exists to prevent.
        Assert.True(dodges < aimed / 2, $"{dodges} dodges over {aimed} aimed frames - the cooldown is not biting");
    }

    [Fact]
    public void TheRarityFloorActuallyThinsTheFight()
    {
        // The setting the user asked for, measured on a real pack rather than asserted: raising
        // the floor to Rare has to leave strictly fewer threats than Normal does, or the gate is
        // not doing anything.
        (List<EvasionTick> everything, _) = Session.Value;
        (List<EvasionTick> raresOnly, _) = Replay(new EvasionSettings(
            Warn: new EvasionGate(true, ItemRarity.Rare),
            Act: new EvasionGate(false),
            DangerRadius: 90f,
            DodgeKey: 0x20));

        int all = everything.Sum(t => t.Draw.Count);
        int rare = raresOnly.Sum(t => t.Draw.Count);

        Assert.True(all > 0, "the unfiltered run found nothing to compare against");
        Assert.True(rare < all, $"the Rare floor kept {rare} of {all} threats - it is not filtering");
    }

    [Fact]
    public void ANonsenseTypeFilterSilencesTheWholeFight()
    {
        // The other half of the type gate: a filter matching nothing must leave nothing, which
        // is what proves the filter is consulted at all rather than being decorative.
        (List<EvasionTick> ticks, _) = Replay(new EvasionSettings(
            Warn: new EvasionGate(true, ItemRarity.Normal, OnlyPaths: ["NoSuchMonsterAnywhere"]),
            Act: new EvasionGate(false),
            DangerRadius: 90f));

        Assert.Equal(0, ticks.Sum(t => t.Draw.Count));
    }

    [Fact]
    public void WithBothGatesOffTheReaderIsNeverAsked()
    {
        // The priced setting: with everything off the planner must not even look, so the
        // four reads per monster per tick are not paid for a feature nobody switched on.
        var settings = new EvasionSettings(Warn: new EvasionGate(false), Act: new EvasionGate(false));
        Assert.False(settings.NeedsActions);

        (List<EvasionTick> ticks, _) = Replay(settings);
        Assert.All(ticks, t => Assert.Equal("disabled", t.Reason));
    }
}
