using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The second action session: what it settles, and the two things it could not.
/// </summary>
/// <remarks>
/// RECORDED TO ANSWER THE MONSTER QUESTION, and it does not - which is the most useful thing
/// about it and the reason it is committed rather than discarded. Eighty-six seconds, 1390
/// frames, a player who demonstrably fought (98 frames of Spark, 6 of Flamewall, 23,000 world
/// units of running), and NOT ONE monster in the file. Two separate causes, both asserted below,
/// both fixed in the same change as these tests:
///
///  1. THE HUNT ONLY SAMPLED THE PLAYER. Its loop read the player's Actor and Render and nothing
///     else, so the entity list in this file is whatever the STARTUP scan happened to see -
///     seven entities, frozen, none of them a monster, for the whole session. A recording holds
///     only what the running build read, and no build had ever read a monster's actor.
///  2. A FAILED RESOLUTION WAS CACHED. The player pointer is one address all session, so the
///     one bad frame at the start - a loading screen - stuck, and every later frame returned
///     nothing. Replayed against the build that made it, this file yields ZERO samples out of
///     1390 readable frames.
///
/// What it DOES settle is worth as much: it re-derives the action offsets from scratch, in a
/// different area, on a different day, with 43 arrivals where the first session had four. Two
/// independent sessions agreeing is the difference between a measurement and a coincidence.
/// </remarks>
public class FightSessionTests
{
    private const string Fixture = "session-2026-08-fight.rec";

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

    /// <summary>Replays every frame through the hunt's own sampler.</summary>
    private static (List<ActionHuntSample> Samples, ActionHunt Hunt, ReplayMemoryReader Replay) Replay()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new ActionHunt(replay, schema);

        var samples = new List<ActionHuntSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(replay.ResolvedStatics["GameStates"]) is { } sample)
            {
                samples.Add(sample);
            }
        }

        return (samples, hunt, replay);
    }

    [Fact]
    public void OneUnreadableFrameNoLongerSwitchesTheWholeHuntOff()
    {
        // THE REGRESSION, and it is the expensive kind: nothing crashed, nothing warned, the
        // report simply said "no frames" about a session that was readable from frame 7 to the
        // end. The cache keyed a failed resolution against the player pointer, which never
        // changes, so the retry condition could not come back true.
        (List<ActionHuntSample> samples, _, ReplayMemoryReader replay) = Replay();

        Assert.True(replay.FrameCount > 1300, $"fixture has only {replay.FrameCount} frames");
        Assert.True(
            samples.Count > replay.FrameCount - 20,
            $"only {samples.Count} of {replay.FrameCount} frames sampled - the resolution cache is poisoning again");
    }

    [Fact]
    public void ASecondSessionIndependentlyFindsTheSameActionOffsets()
    {
        // The corroboration. Every offset in the schema came from ONE recording; this is a
        // different area on a different day, and the hunt re-derives them from scratch - so
        // they are a property of the game rather than of that session.
        (List<ActionHuntSample> samples, ActionHunt hunt, _) = Replay();
        ActionHuntFindings findings = ActionHunt.Analyze(samples, hunt.AnimationIdOffset, hunt.PlayerCastTypes);

        OffsetSchema schema = RealSessionTests.Schema();
        int movePtr = schema.Structs["Actor"].OffsetOf("MoveActionPtr");
        int actionId = schema.Structs["Actor"].OffsetOf("ActionId");
        int targetGrid = schema.Structs["ActionWrapper"].OffsetOf("TargetGrid");

        // The move-action pointer: set while acting, null while idle, on its schema offset.
        ActionPointerCandidate pointer = Assert.Single(findings.Pointers);
        Assert.Equal(movePtr, pointer.Offset);
        Assert.True(pointer.ActingNonNull > 0.85, $"set on only {pointer.ActingNonNull:P0} of acting frames");
        Assert.Equal(0, pointer.QuietNonNull, 3);

        // The id, as a short, on its schema offset.
        Assert.Contains(findings.Ids, c => c.Offset == actionId && c.Kind == "i16");

        // And the destination: the pair the player then walked to, at the schema's offset
        // inside the block behind the schema's pointer.
        DestinationCandidate best = findings.Destinations[0];
        Assert.Equal(movePtr, best.PointerOffset);
        Assert.Equal(targetGrid, best.PairOffset);
        Assert.True(best.Segments >= 20, $"only {best.Segments} arrivals");
        Assert.True(best.FitQuality > 0.95, $"fit {best.FitQuality:F3}");

        // The scale the fit recovers IS the grid-to-world factor, found here without being
        // told it: the destination is in grid cells, independently in a second session.
        Assert.Equal(PoEformance.Game.Ui.MapView.WorldToGrid, best.Scale, 0.5);
    }

    [Fact]
    public void ThePlayerActsAndTheReaderAgreesWithTheHunt()
    {
        // The production reader against the same frames: it must see the same session the raw
        // hunt did - a lot of moving, some casting, and targets that are real places.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema) { ReadActions = true };

        int moves = 0, skills = 0, none = 0;
        float furthest = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame += 5)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(replay.ResolvedStatics["GameStates"]);
            if (snapshot.Player?.Action is not { } action)
            {
                continue;
            }

            switch (action.Kind)
            {
                case ActionKind.Move:
                    moves++;
                    furthest = MathF.Max(furthest, action.Reach);
                    break;
                case ActionKind.Skill:
                    skills++;
                    break;
                case ActionKind.None:
                    none++;
                    break;
                default:
                    break;
            }
        }

        Assert.True(moves > 50, $"only {moves} move actions through the snapshot");
        Assert.True(skills > 0, $"no skill actions through the snapshot");
        Assert.True(none > 0, "the player was never idle, which cannot be right");

        // A real destination, not a decoded zero - and not the far side of the map either.
        Assert.InRange(furthest, 100f, 20_000f);
    }

    [Fact]
    public void ThisRecordingCannotAnswerTheMonsterQuestionAndSaysSo()
    {
        // THE NEGATIVE, asserted so it cannot be mistaken for a pass. The check must report
        // "nothing to say" rather than "no problems found" - a monster question answered by a
        // file with no monsters in it is the failure this whole diagnostic exists to avoid.
        (List<ActionHuntSample> samples, _, _) = Replay();
        MonsterActionFindings findings = MonsterActionCheck.Analyze(samples);

        Assert.Equal(0, findings.MonsterSightings);
        Assert.Empty(findings.Arrivals);

        using var sink = new StringWriter();
        MonsterActionCheck.Report(findings, sink);
        Assert.Contains("NOTHING TO SAY", sink.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntityListInThisFileIsFrozenAndHoldsNoMonster()
    {
        // WHY it cannot answer, measured rather than asserted from the story: the entity map
        // was walked once at startup and never again, so the same seven entities are served at
        // second one and second eighty-six. This is what a sampling loop that reads only the
        // player leaves behind, and the reason the loop now walks the map every tick.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        replay.Seek(100);
        WorldSnapshot early = world.Read(gameStates);
        replay.Seek((uint)(replay.FrameCount - 1));
        WorldSnapshot late = world.Read(gameStates);

        Assert.Equal(early.Entities.Count, late.Entities.Count);
        Assert.DoesNotContain(early.Entities, e => e.Kind == EntityKind.Monster);
        Assert.DoesNotContain(late.Entities, e => e.Kind == EntityKind.Monster);

        // The player IS in there and IS moving, so the file is not simply empty - which is
        // exactly why the missing monsters were invisible until somebody replayed it.
        Assert.Contains(late.Entities, e => e.Kind == EntityKind.Player);
    }

    [Fact]
    public void TheFacingIsInThisFileWhereTheFirstSessionHadNone()
    {
        // The first recording could not run the bearing cross-check because its build never
        // read the rotation bytes. This one does, on essentially every frame - so the next
        // fight recording will carry what the monster check needs.
        (List<ActionHuntSample> samples, _, _) = Replay();

        int read = samples.Count(s => !float.IsNaN(s.Facing));
        Assert.True(read > samples.Count - 20, $"facing read on only {read} of {samples.Count} frames");
    }
}
