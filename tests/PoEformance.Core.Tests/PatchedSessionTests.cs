using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The first recording from the 2026-09-04 content patch
/// (<c>tests/fixtures/session-2026-09-patch.rec</c>): a bare attach, eight frames, made
/// with the build that introduced static fallbacks.
/// </summary>
/// <remarks>
/// What it settles: the exact GameStates pattern no longer exists in the patched client,
/// a fallback recovered the static, and what it leads to is a real state array - ten of the
/// thirteen states allocated, all distinct. What it does NOT settle: the InGameState layout.
/// Its AreaInstanceData slot holds a value that is not a pointer, WorldData and the UI root
/// read null, and the recording cannot say whether the game was in an area, so those rows
/// are questions for the next recording rather than answers in this one.
/// </remarks>
public class PatchedSessionTests
{
    internal static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-patch.rec");
        }
    }

    [Fact]
    public void GameStates_RecoveredByFallback_LeadsToAStateArray()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();

        ulong gameStates = replay.ResolvedStatics["GameStates"];
        Assert.True(DriftReport.LooksLikeGameStates(replay, schema, gameStates));

        // All thirteen entries were read live - which is the fingerprint's own read pattern,
        // and the proof that the fallback path ran rather than the primary.
        StructDef gs = schema.Structs["GameState"];
        ulong gameState = replay.ReadPointer(gameStates);
        ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
        var distinct = new HashSet<ulong>();
        for (long i = 0; i < gs.Constants["TotalStates"]; i++)
        {
            Assert.True(replay.TryRead(statesBase + (ulong)(i * gs.Constants["StateEntrySize"]), out ulong entry));
            if (entry != 0)
            {
                distinct.Add(entry);
            }
        }

        Assert.Equal(10, distinct.Count);
    }

    [Fact]
    public void InGameState_AreaInstanceSlot_NoLongerHoldsAPointer()
    {
        // Recorded as a fact about the patched client, not as a conclusion about the layout:
        // the slot reads a heap-range value that is not even 8-byte aligned, and the report
        // must say so rather than validate a struct at that address.
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        Assert.NotEqual(0UL, chain.InGameState);
        Assert.Equal(0x5D2DED757E4UL, chain.AreaInstance);
        Assert.NotEqual(0UL, chain.AreaInstance % 8);
        Assert.Equal(0UL, chain.WorldData);
        Assert.False(AreaInstanceHunt.LooksLikeAreaInstance(replay, schema, chain.AreaInstance));
    }
}
