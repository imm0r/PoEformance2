using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The Actor's action fields, against the session that found them.
/// </summary>
/// <remarks>
/// THE EVIDENCE FOR THE OFFSETS, kept executable so a drift breaks a test rather than a
/// feature. Nothing here is synthetic: the recording is one real session in which the player
/// click-moved once and cast five spells, and every assertion below is a claim about what the
/// GAME did, not about what this code computes.
///
/// The load-bearing one is <see cref="MoveDestinationIsWhereThePlayerCameToRest"/>. A pair of
/// integers that predicts, a second and a half early and to within less than one grid cell,
/// where a character will stop walking is a destination; no structural argument can reach that
/// standard and none is offered. The rest fence in what that pair means: the two action slots
/// never overlap, the wrapper's origin is the actor's own square, and the skill pointer names
/// the skill.
///
/// WHAT THESE TESTS DO NOT ESTABLISH, because the session did not contain it: that a MONSTER's
/// actor behaves the same way (only the player was sampled), that the cast target is aimed
/// where the cursor was (the facing bytes were never read, so they are not in the file), and
/// the destination on more than the single arrival this recording holds.
/// </remarks>
public class ActionFieldsTests
{
    /// <summary>One click-move and five casts, 1374 frames. The session that found the fields.</summary>
    private const string Fixture = "session-2026-08-actions.rec";

    /// <summary>World units per grid cell: a terrain tile is 250 across 23 cells.</summary>
    private const double WorldPerGrid = 250.0 / 23.0;

    /// <summary>FixedRun - the animation the click-move ran under.</summary>
    private const int FixedRun = 195;

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

    /// <summary>Replays every frame and returns the samples, with the animation id beside each.</summary>
    private static (List<ActionHuntSample> Samples, int[] Ids, OffsetSchema Schema) Replay()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var hunt = new ActionHunt(replay, schema);
        var samples = new List<ActionHuntSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(gameStates) is { } sample)
            {
                samples.Add(sample);
            }
        }

        int anim = hunt.AnimationIdOffset;
        return (samples, [.. samples.Select(s => BitConverter.ToInt32(s.Window, anim))], schema);
    }

    private static int GridX(byte[] block, int at) => BitConverter.ToInt32(block, at);

    private static int GridY(byte[] block, int at) => BitConverter.ToInt32(block, at + 4);

    [Fact]
    public void EveryCompletedMoveEndsExactlyOnItsDestination()
    {
        // THE CENTRAL CLAIM OF THE WHOLE FIND, and the reason none of it rests on a
        // structural argument. Read every frame - not only the ones the hunt sampled, which
        // is why this sees four arrivals where the hunt's own report saw one - group the move
        // actions, and ask the game where the character actually stopped.
        //
        // The answer is exact, not approximate: the cell centre, to within a hundredth of a
        // cell, on every completed move. That is also what makes the half-cell conversion in
        // ActionReader a measurement rather than a fudge - drop it and this test fails by a
        // constant 7.69 units in the same direction every time.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var reader = new ActionReader(replay, schema);
        var entities = new EntityReader(replay, schema);
        var render = new RenderReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var track = new List<(ActorAction Action, float X, float Y)>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            PoEformance.Game.Entities.Entity? player = chain.InGame ? entities.Read(chain.PlayerEntity) : null;
            ulong actor = player?.Component("Actor") ?? 0;
            if (actor == 0 || render.Read(player!.Component("Render")) is not { } position)
            {
                continue;
            }

            track.Add((reader.Read(actor), position.X, position.Y));
        }

        int arrivals = 0;
        int interrupted = 0;
        for (int i = 1; i < track.Count; i++)
        {
            // The frame a move action stops being reported: the character is either standing
            // on its destination or was sent somewhere else mid-way.
            if (track[i - 1].Action.Kind != ActionKind.Move || track[i].Action.Kind == ActionKind.Move)
            {
                continue;
            }

            ActorAction move = track[i - 1].Action;

            // Let the position settle: the last frame before anything else starts.
            int settled = i;
            while (settled + 1 < track.Count
                   && settled - i < 5
                   && track[settled + 1].Action.Kind == ActionKind.None)
            {
                settled++;
            }

            double miss = Math.Sqrt(
                ((move.TargetX - track[settled].X) * (move.TargetX - track[settled].X))
                + ((move.TargetY - track[settled].Y) * (move.TargetY - track[settled].Y)));

            // An interrupted move never reached its destination and proves nothing either
            // way; it is counted so the test cannot quietly pass on nothing but interruptions.
            if (miss > 10 * WorldPerGrid)
            {
                interrupted++;
                continue;
            }

            arrivals++;
            Assert.True(
                miss < 1.0,
                $"move to ({move.TargetX:F1}, {move.TargetY:F1}) ended at "
                + $"({track[settled].X:F1}, {track[settled].Y:F1}) - {miss:F2} world units out. "
                + $"A constant {WorldPerGrid / 2:F2}-ish miss means the cell-centre half is gone from ActionReader.");
        }

        Assert.True(arrivals >= 4, $"only {arrivals} completed arrivals found (plus {interrupted} interrupted)");
    }

    [Fact]
    public void MoveDestinationIsWhereThePlayerCameToRest()
    {
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int moveSlot = schema.Structs["Actor"].OffsetOf("MoveActionPtr");
        int target = schema.Structs["ActionWrapper"].OffsetOf("TargetGrid");

        List<int> running = [.. Enumerable.Range(0, samples.Count)
            .Where(i => ids[i] == FixedRun && samples[i].Followed.ContainsKey(moveSlot))];
        Assert.NotEmpty(running);

        // PER RUN, NOT PER SESSION, and the difference is a finding rather than a detail. This
        // read the whole session as one run until the sampler stopped discarding its first
        // frames (see FightSessionTests: a cached failure used to drop them), and the frames
        // that came back carry an EARLIER move with its own destination - grid x 1022 where the
        // later one reads 1014. A destination that changes between runs is the field working;
        // one that changed WITHIN a run would be a cursor, not a place the character was sent.
        var runs = new List<(int Start, int End, int X, int Y)>();
        foreach (int i in running)
        {
            byte[] block = samples[i].Followed[moveSlot];
            int x = GridX(block, target), y = GridY(block, target);

            if (runs.Count > 0 && runs[^1].X == x && runs[^1].Y == y && runs[^1].End == i - 1)
            {
                runs[^1] = (runs[^1].Start, i, x, y);
                continue;
            }

            runs.Add((i, i, x, y));
        }

        Assert.NotEmpty(runs);

        // The misses, for runs long enough to have gone anywhere. A single-frame "run" is the
        // sample that caught the destination changing, not a move.
        double[] misses = [.. runs
            .Where(r => r.End - r.Start + 1 >= 4)
            .Select(r =>
            {
                int settled = Math.Min(r.End + 3, samples.Count - 1);
                double missX = (r.X * WorldPerGrid) - samples[settled].PlayerX;
                double missY = (r.Y * WorldPerGrid) - samples[settled].PlayerY;
                return Math.Sqrt((missX * missX) + (missY * missY));
            })];

        // FOUR of them land within a cell and ONE does not, and both halves are the claim.
        // The four that land do not merely land near: they land at 7.69 world units every
        // time, which is the diagonal of half a cell - the quantisation ActionReader corrects
        // for, showing up here uncorrected. A wrong offset does not produce the same residual
        // four times, it produces scatter.
        double[] arrived = [.. misses.Where(m => m < WorldPerGrid)];
        Assert.True(arrived.Length >= 4, $"only {arrived.Length} of {misses.Length} runs ended on their destination");
        Assert.All(arrived, m => Assert.Equal(7.69, m, 0.1));

        // The one that does not is a move the player BROKE OFF - it stops 244 units out and
        // then stands still for 265 frames, so nothing new was clicked. An abandoned move says
        // nothing about where the field pointed, and counting it would be reporting the
        // player's change of mind as a defect in the offset.
        Assert.True(misses.Length - arrived.Length <= 1, "more moves ended away from their destination than expected");
    }

    [Fact]
    public void TheTwoActionSlotsAreNeverSetAtOnce()
    {
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int moveSlot = schema.Structs["Actor"].OffsetOf("MoveActionPtr");
        int skillSlot = schema.Structs["Actor"].OffsetOf("SkillActionPtr");

        int moving = 0, casting = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            ulong move = BitConverter.ToUInt64(samples[i].Window, moveSlot);
            ulong skill = BitConverter.ToUInt64(samples[i].Window, skillSlot);

            // The claim that makes these one mechanism rather than two coincidences.
            Assert.False(move != 0 && skill != 0, $"both action slots set at sample {i} (animation {ids[i]})");

            if (move != 0)
            {
                moving++;
                Assert.Equal(FixedRun, ids[i]);
            }

            if (skill != 0)
            {
                casting++;
                Assert.NotEqual(FixedRun, ids[i]);
            }
        }

        Assert.True(moving >= 10, $"only {moving} frames with a move action");
        Assert.True(casting >= 20, $"only {casting} frames with a skill action");
    }

    [Fact]
    public void AnActionCanRunWhileTheAnimationSaysIdle()
    {
        // THE FINDING THAT WROTE ITSELF, and it arrived as a failing assertion: this test
        // first claimed no action slot is ever set while the animation reads Idle, which is
        // the tidy expectation. Two frames of the recording say otherwise - a skill action
        // with a real target, ~100 ms, while AnimationId reads 0 and the animation layer at
        // 0x380 reads 0 too. Something was committed that no animation showed.
        //
        // It is asserted rather than merely noted because it is the case an evasion warning
        // cannot afford to miss: a monster committing to an attack with no distinct animation
        // is invisible to a reader that watches AnimationId alone. If a future build stops
        // seeing these frames, that is a regression in reach, not a tidier world.
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int skillSlot = schema.Structs["Actor"].OffsetOf("SkillActionPtr");
        int actionId = schema.Structs["Actor"].OffsetOf("ActionId");
        int target = schema.Structs["ActionWrapper"].OffsetOf("TargetGrid");
        int origin = schema.Structs["ActionWrapper"].OffsetOf("OriginGrid");

        List<int> silent = [.. Enumerable.Range(0, samples.Count)
            .Where(i => ids[i] == 0 && BitConverter.ToUInt64(samples[i].Window, skillSlot) != 0)];
        Assert.NotEmpty(silent);

        foreach (int i in silent)
        {
            // The action id knows, even though the animation does not.
            Assert.Equal(2, BitConverter.ToInt16(samples[i].Window, actionId));

            // And it is a real action, not a half-written pointer: it carries a target some
            // distance from its origin, which is what makes the frames worth catching.
            byte[] block = samples[i].Followed[skillSlot];
            int reach = Math.Abs(GridX(block, target) - GridX(block, origin))
                + Math.Abs(GridY(block, target) - GridY(block, origin));
            Assert.True(reach > 3, $"action at sample {i} aims {reach} cells away - too close to be a real aim");
        }
    }

    [Fact]
    public void ActionIdSaysWhichKindOfActionIsRunning()
    {
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int moveSlot = schema.Structs["Actor"].OffsetOf("MoveActionPtr");
        int skillSlot = schema.Structs["Actor"].OffsetOf("SkillActionPtr");
        int actionId = schema.Structs["Actor"].OffsetOf("ActionId");

        // Read as a SHORT, as PoE1 did. The dword at the same place carries something else in
        // its high half (0x400000), so a careless i32 read would never equal these values.
        for (int i = 0; i < samples.Count; i++)
        {
            short id = BitConverter.ToInt16(samples[i].Window, actionId);
            bool move = BitConverter.ToUInt64(samples[i].Window, moveSlot) != 0;
            bool skill = BitConverter.ToUInt64(samples[i].Window, skillSlot) != 0;

            Assert.Equal(move ? 4224 : skill ? 2 : 0, id);
        }
    }

    [Fact]
    public void CastOriginIsTheCastersOwnSquareAndTargetsDiffer()
    {
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int skillSlot = schema.Structs["Actor"].OffsetOf("SkillActionPtr");
        int target = schema.Structs["ActionWrapper"].OffsetOf("TargetGrid");
        int origin = schema.Structs["ActionWrapper"].OffsetOf("OriginGrid");

        var targets = new HashSet<(int, int)>();
        int checkedCasts = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            if (ids[i] == 0 || !samples[i].Followed.ContainsKey(skillSlot))
            {
                continue;
            }

            byte[] block = samples[i].Followed[skillSlot];

            // The origin is the caster's own square, which is checked against the position
            // read from the Render component in the same frame - two independent fields
            // agreeing is the whole reason to believe the pair is a coordinate at all.
            double originWorldX = GridX(block, origin) * WorldPerGrid;
            double originWorldY = GridY(block, origin) * WorldPerGrid;
            Assert.True(
                Math.Abs(originWorldX - samples[i].PlayerX) < WorldPerGrid
                && Math.Abs(originWorldY - samples[i].PlayerY) < WorldPerGrid,
                $"cast origin ({originWorldX:F0}, {originWorldY:F0}) is not the caster's square "
                + $"({samples[i].PlayerX:F0}, {samples[i].PlayerY:F0})");

            targets.Add((GridX(block, target), GridY(block, target)));
            checkedCasts++;
        }

        Assert.True(checkedCasts >= 20, $"only {checkedCasts} casting frames found");

        // Five casts were aimed at five places. One target for all of them would mean the
        // field is something about the caster, not about the aim.
        Assert.True(targets.Count >= 4, $"only {targets.Count} distinct cast targets");
    }

    [Fact]
    public void SkillPointerIdentifiesTheSkillBeingCast()
    {
        (List<ActionHuntSample> samples, int[] ids, OffsetSchema schema) = Replay();
        int skillPtr = schema.Structs["Actor"].OffsetOf("CurrentSkillPtr");

        var byAnimation = new Dictionary<int, HashSet<ulong>>();
        var byPointer = new Dictionary<ulong, HashSet<int>>();
        for (int i = 0; i < samples.Count; i++)
        {
            ulong value = BitConverter.ToUInt64(samples[i].Window, skillPtr);
            if (ids[i] == 0 || value == 0)
            {
                continue;
            }

            (byAnimation.TryGetValue(ids[i], out HashSet<ulong>? p) ? p : byAnimation[ids[i]] = []).Add(value);
            (byPointer.TryGetValue(value, out HashSet<int>? a) ? a : byPointer[value] = []).Add(ids[i]);
        }

        Assert.Equal(3, byAnimation.Count);
        Assert.Equal(3, byPointer.Count);

        // A ONE-TO-ONE map in both directions. One pointer serving two skills would make it a
        // shared object; one skill using two pointers would make it per-cast state.
        Assert.All(byAnimation, pair => Assert.Single(pair.Value));
        Assert.All(byPointer, pair => Assert.Single(pair.Value));
    }

    [Fact]
    public void ActionReaderTellsTheSameStoryAsTheRawOffsets()
    {
        // The production reader against the session that found the fields: same frames, same
        // conclusions, arrived at through the schema rather than through hard-coded numbers.
        // What this really guards is the grid-to-world conversion, which is the one piece of
        // arithmetic the reader adds and the one place a marker can silently land a hundred
        // times too close to the map's origin.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var reader = new ActionReader(replay, schema);
        var entities = new EntityReader(replay, schema);
        var render = new RenderReader(replay, schema);

        int moves = 0, skills = 0, none = 0;
        double worstMoveMiss = double.MaxValue;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (!chain.InGame)
            {
                continue;
            }

            PoEformance.Game.Entities.Entity? player = entities.Read(chain.PlayerEntity);
            ulong actor = player?.Component("Actor") ?? 0;
            if (actor == 0)
            {
                continue;
            }

            ActorAction action = reader.Read(actor);
            switch (action.Kind)
            {
                case ActionKind.None:
                    none++;
                    break;

                case ActionKind.Move:
                    moves++;

                    // The actor must be somewhere between where the move started and where it
                    // is going - never further from the origin than the action reaches. This
                    // is the conversion under test: a grid pair left unconverted would put the
                    // origin near the map's corner and fail by thousands of units.
                    if (render.Read(player!.Component("Render")) is { } position)
                    {
                        double travelled = Math.Sqrt(
                            ((action.OriginX - position.X) * (action.OriginX - position.X))
                            + ((action.OriginY - position.Y) * (action.OriginY - position.Y)));

                        Assert.True(
                            travelled <= action.Reach + WorldPerGrid,
                            $"actor at ({position.X:F0}, {position.Y:F0}) is {travelled:F0} units from the move's "
                            + $"origin ({action.OriginX:F0}, {action.OriginY:F0}) but the whole action only reaches "
                            + $"{action.Reach:F0} - grid units left unconverted?");

                        worstMoveMiss = Math.Min(worstMoveMiss, action.Reach);
                    }

                    break;

                case ActionKind.Skill:
                    skills++;

                    // The skill object appears when the cast itself starts, not when the
                    // action is committed: through the two frames where a skill action runs
                    // with no animation, this is still null. So the pointer is tied to the
                    // ANIMATED cast, and a reader that treats "skill action" as "there is a
                    // skill pointer to follow" would find nothing exactly in the frames that
                    // arrive earliest - which are the ones a warning wants most.
                    if (action.AnimationId > 0)
                    {
                        Assert.NotEqual(0UL, action.SkillAddress);
                    }

                    break;

                default:
                    Assert.Fail($"unexpected action kind {action.Kind} (raw id {action.RawId})");
                    break;
            }
        }

        Assert.True(moves >= 10, $"only {moves} move actions seen");
        Assert.True(skills >= 20, $"only {skills} skill actions seen");
        Assert.True(none > moves + skills, "most of the session was idle, so None should dominate");

        // The run had somewhere to go: a reach of zero every frame would mean the wrapper
        // read but its pairs did not.
        Assert.True(worstMoveMiss > 100, $"the move action never reached further than {worstMoveMiss:F0} world units");
    }

    [Fact]
    public void MonsterActorsAreNotSettledByThisRecording()
    {
        // A NEGATIVE TEST, and the honest one: every offset here was measured on the PLAYER,
        // while the feature they exist for reads MONSTERS. This asserts what the fixture can
        // actually support - that the reader survives being pointed at other actors without
        // inventing actions - and it deliberately does NOT assert that monster actions read
        // correctly, because nothing in this session shows that they do.
        //
        // When a recording with monsters acting exists, this test is where the real claim
        // goes; until then it stands as the marker that the claim is missing.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        replay.Seek((uint)(replay.FrameCount - 1));

        var world = new PoEformance.Game.World.WorldReader(replay, schema);
        PoEformance.Game.World.WorldSnapshot snapshot = world.Read(replay.ResolvedStatics["GameStates"]);
        var reader = new ActionReader(replay, schema);
        var entities = new EntityReader(replay, schema);

        int looked = 0;
        foreach (PoEformance.Game.World.WorldEntity entity in snapshot.Entities
                     .Where(e => e.Kind == PoEformance.Game.World.EntityKind.Monster)
                     .Take(20))
        {
            ulong actor = entities.Read(entity.Address)?.Component("Actor") ?? 0;
            if (actor == 0)
            {
                continue;
            }

            looked++;
            ActorAction action = reader.Read(actor);

            // The only claim: nothing crashes and nothing comes back as a named action with
            // an absurd reach. An unreadable actor must read as None, not as a monster
            // charging at a place ten thousand units away.
            if (action.Kind is ActionKind.Skill or ActionKind.Move)
            {
                Assert.True(
                    action.Reach < 20_000,
                    $"monster action reaches {action.Reach:F0} world units - that is not a target");
            }
        }

        // The recording holds a quiet moment, so zero monsters with actors is a fine outcome;
        // this only records how much was actually looked at.
        Assert.True(looked >= 0);
    }

    [Fact]
    public void ThePlayersSkillTableStillReadsNoCastTypes()
    {
        // The negative result, asserted so that the day it changes, somebody is told. Every
        // one of the 41 entries reads 0 at the reference's CastType offset while the player
        // demonstrably used three cast types that session - so either the offset is wrong for
        // PoE2 or these entries are the granted effects the reference itself discards.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        replay.Seek((uint)(replay.FrameCount - 1));

        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        PoEformance.Game.Entities.Entity? player = new EntityReader(replay, schema).Read(chain.PlayerEntity);
        Assert.NotNull(player);

        ulong actor = player.Component("Actor");
        Assert.NotEqual(0UL, actor);

        int skills = schema.Structs["Actor"].OffsetOf("ActiveSkills");
        int entrySize = checked((int)schema.Structs["ActiveSkillStructure"].Constants["Size"]);
        int castType = schema.Structs["ActiveSkillDetails"].OffsetOf("CastType");

        ulong begin = replay.ReadPointer(actor + (ulong)skills);
        ulong end = replay.Read<ulong>(actor + (ulong)skills + 8);
        Assert.True(end > begin);

        int count = (int)((end - begin) / (ulong)entrySize);
        Assert.Equal(41, count);

        for (int i = 0; i < count; i++)
        {
            ulong details = replay.ReadPointer(begin + (ulong)(i * entrySize));
            if (details == 0)
            {
                continue;
            }

            Assert.Equal(0, replay.Read<int>(details + (ulong)castType));
        }
    }
}
