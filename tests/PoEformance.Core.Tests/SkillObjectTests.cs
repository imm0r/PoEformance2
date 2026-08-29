using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// What <c>Actor.CurrentSkillPtr</c> actually is, measured against the real fight.
/// </summary>
/// <remarks>
/// THIS CORRECTS A CLAIM THIS PROJECT MADE, which is why it exists rather than being folded into
/// the hunt's own tests. The schema recorded the pointer as one-to-one with the animation id -
/// "3 ids, 3 pointers, no crossover" - from 27 casting frames of a single session. Over the
/// monster session it is FOUR objects to THREE animation ids, with two distinct objects both
/// playing 299. The original observation was not wrong; it was a small sample, and the rule
/// drawn from it was too strong.
///
/// WHY IT MATTERS RATHER THAN BEING A DETAIL: "the pointer identifies the skill" is exactly what
/// a reader would build on, and it would work - until two skills share an animation, at which
/// point a per-skill filter silently treats them as one, or a cached lookup returns the wrong
/// name. The pointer is FINER than the skill (an instance, or one of two skills behind an
/// animation), so it can be used as a key for "is this the same cast as last frame" and not as a
/// key for "which skill is this".
///
/// THE OTHER TWO MEASUREMENTS HERE ARE NEGATIVE RESULTS, and they are the ones that shaped
/// <see cref="Game.Diagnostics.SkillHunt"/>: the action wrapper does not carry the skill in its
/// first 0x200, and the skill object is absent for most of the frames on which a skill action is
/// committed. Both are the sort of thing that is cheap to measure once and expensive to
/// rediscover after building on the assumption.
/// </remarks>
public class SkillObjectTests
{
    private const string Fixture = "session-2026-08-monsters.rec";

    /// <summary>How much of the wrapper any recording actually holds.</summary>
    private const int WrapperCaptured = 0x200;

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

    /// <summary>One casting frame's pointers. Cached - it replays the whole session.</summary>
    private sealed record Cast(ulong Skill, ulong Wrapper, byte[]? WrapperBlock, int Animation);

    private static readonly Lazy<List<Cast>> Casts = new(() =>
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var entities = new EntityReader(replay, schema);
        var actions = new ActionReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int currentSkill = schema.Structs["Actor"].OffsetOf("CurrentSkillPtr");
        int skillAction = schema.Structs["Actor"].OffsetOf("SkillActionPtr");

        var casts = new List<Cast>();
        ulong actor = 0;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (!chain.InGame)
            {
                continue;
            }

            if (actor == 0)
            {
                actor = entities.Read(chain.PlayerEntity)?.Component("Actor") ?? 0;
                if (actor == 0)
                {
                    continue;
                }
            }

            ulong wrapper = replay.ReadPointer(actor + (ulong)skillAction);
            if (!MemoryReaderExtensions.IsPlausiblePointer(wrapper))
            {
                continue;
            }

            var block = new byte[WrapperCaptured];
            casts.Add(new Cast(
                replay.ReadPointer(actor + (ulong)currentSkill),
                wrapper,
                replay.TryRead(wrapper, block) ? block : null,
                actions.Read(actor).AnimationId));
        }

        return casts;
    });

    [Fact]
    public void TheSkillObjectIsFinerThanTheAnimation()
    {
        // THE CORRECTION. Four objects, three animations - so the schema's "each animation id
        // mapped to one pointer and each pointer to one animation id" does not survive a bigger
        // sample, and a reader must not treat the pointer as the skill's identity.
        var animations = new Dictionary<ulong, HashSet<int>>();
        foreach (Cast cast in Casts.Value.Where(c => MemoryReaderExtensions.IsPlausiblePointer(c.Skill)))
        {
            animations.TryAdd(cast.Skill, []);
            animations[cast.Skill].Add(cast.Animation);
        }

        Assert.Equal(4, animations.Count);
        Assert.Equal(3, animations.Values.SelectMany(a => a).Distinct().Count());

        // Each object still plays exactly one animation - it is the other direction that fails.
        Assert.All(animations.Values, seen => Assert.Single(seen));

        int shared = animations.Values
            .SelectMany(a => a)
            .GroupBy(a => a)
            .Count(group => group.Count() > 1);
        Assert.Equal(1, shared);
    }

    [Fact]
    public void TheActionWrapperDoesNotCarryTheSkillPointer()
    {
        // A negative result, and one worth keeping: PoE1 put the skill in the wrapper at 0x150,
        // which in PoE2 is TargetGrid - so the obvious port is not merely stale, it lands on a
        // field that reads as plausible integers. Nothing in the captured 0x200 ever equals the
        // frame's own skill object.
        //
        // WHAT THIS DOES NOT SAY: that the wrapper has no skill pointer at all. Only 0x200 of it
        // has ever been recorded, and the struct is known to be "mostly unread" - which is why
        // SkillHunt reads 0x400 of it.
        var matches = new HashSet<int>();
        int looked = 0;

        foreach (Cast cast in Casts.Value)
        {
            if (cast.WrapperBlock is not byte[] block || !MemoryReaderExtensions.IsPlausiblePointer(cast.Skill))
            {
                continue;
            }

            looked++;
            for (int offset = 0; offset + sizeof(ulong) <= block.Length; offset += sizeof(ulong))
            {
                if (BitConverter.ToUInt64(block, offset) == cast.Skill)
                {
                    matches.Add(offset);
                }
            }
        }

        Assert.True(looked > 40, $"only {looked} frames had both a wrapper block and a skill object");
        Assert.Empty(matches);
    }

    [Fact]
    public void MostCommittedSkillActionsHaveNoSkillObjectYet()
    {
        // THE TIMING PROBLEM, as a number rather than as a remark. The schema already says the
        // pointer "follows the cast, not the commitment"; this says how much of the cast that
        // costs. A warning that wants to name what is coming BEFORE it lands cannot be built on
        // this pointer alone, and the size of the gap is the argument for hunting the actor's
        // granted-skill table as well.
        List<Cast> withWrapper = [.. Casts.Value.Where(c => c.WrapperBlock is not null)];
        int named = withWrapper.Count(c => MemoryReaderExtensions.IsPlausiblePointer(c.Skill));

        Assert.Equal(122, withWrapper.Count);
        Assert.Equal(53, named);
        Assert.True(named < withWrapper.Count / 2, "more than half of committed skill actions were named");
    }

    [Fact]
    public void NothingInAnyRecordingFollowsTheSkillObjectsOutwardPointers()
    {
        // The gap SkillHunt exists to fill, asserted so that "we already have a recording for
        // that" stays a checkable claim rather than a memory. The object's own 0x200 is in the
        // file; every pointer that LEAVES it is a dead end offline.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));

        var outward = new List<ulong>();
        foreach (ulong at in Casts.Value
                     .Select(c => c.Skill)
                     .Where(MemoryReaderExtensions.IsPlausiblePointer)
                     .Distinct())
        {
            var block = new byte[0x200];
            replay.Seek((uint)(replay.FrameCount - 1));
            if (!replay.TryRead(at, block))
            {
                continue;
            }

            for (int offset = 0; offset + sizeof(ulong) <= block.Length; offset += sizeof(ulong))
            {
                ulong value = BitConverter.ToUInt64(block, offset);
                if (MemoryReaderExtensions.IsPlausiblePointer(value)
                    && (value < at || value >= at + 0x200))
                {
                    outward.Add(value);
                }
            }
        }

        Assert.NotEmpty(outward);
        Assert.All(outward, pointer => Assert.False(
            replay.TryRead(pointer, new byte[8]),
            $"{pointer:X} is readable after all - this fixture could answer the name question offline"));
    }
}
