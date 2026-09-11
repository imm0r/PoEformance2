using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The first recording from the 2026-09-04 content patch
/// (<c>tests/fixtures/session-2026-09-patch.rec</c>): a bare attach, eight frames, made
/// with the build that introduced static fallbacks - and with the PRE-patch schema.
/// </summary>
/// <remarks>
/// What it settles: the exact GameStates pattern no longer exists in the patched client and
/// a fallback recovered the static. What it was first READ as settling - "a real state array
/// at the old offset, and an InGameState whose AreaInstanceData slot holds a non-pointer" -
/// it does not. The 0.5.5 layout (owner-verified 2026-09-11, agreeing with GameHelper2)
/// grew GameState by eight bytes at +0x08, so the state array starts at 0x50 and the stack's
/// end pointer sits at 0x18. Read through the old layout, 0x48 + 16i lands on the SECOND
/// qword of entry i-1: this file holds a zero at +0x48 and ten distinct non-null values at
/// +0x58..+0x108, which is exactly what the fingerprint counted, on the wrong half of every
/// entry. The "InGameState" the old build took from index 4 was that second half of entry
/// 3, and the unaligned value at its +0x290 says nothing about InGameState at all.
///
/// The recording cannot confirm the new layout either: the build that made it never read
/// +0x18 or any first qword at 0x50 + 16i, so those slots are simply absent from the file.
/// It replays through the frozen pre-patch schema (<see cref="RealSessionTests.Schema"/>),
/// which is the only layout its reads exist in, and the tests below say what the file can
/// and cannot answer rather than drawing a conclusion about the client from it.
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
    public void ReadThroughThePrePatchLayout_TheFingerprintPassesOnTheSecondHalvesOfTheEntries()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema recorded = RealSessionTests.Schema();

        ulong gameStates = replay.ResolvedStatics["GameStates"];
        Assert.True(DriftReport.LooksLikeGameStates(replay, recorded, gameStates));

        // Ten distinct non-null values at the old 0x48 + 16i - the fingerprint's own read
        // pattern, which is why the fallback's result was believed. Under the 0.5.5 layout
        // those are entries' second qwords, so a count of distinct pointers cannot tell the
        // real array from its neighbour eight bytes on; that is the weakness this file records.
        StructDef gs = recorded.Structs["GameState"];
        ulong gameState = replay.ReadPointer(gameStates);
        ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
        Assert.Equal(0x48, gs.OffsetOf("States"));
        Assert.True(replay.TryRead(statesBase, out ulong first));
        Assert.Equal(0UL, first); // the slot BEFORE the real array is empty

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
    public void ReadThroughThePrePatchLayout_TheOldIndex4LeadsToANonPointer()
    {
        // Kept as the fact it always was - the value the old build found - now with the
        // explanation attached: this is not InGameState.AreaInstanceData, it is +0x290 of
        // whatever entry 3's second qword points at, and the report must still refuse to
        // validate a struct at an unaligned address rather than reading garbage as drift.
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema recorded = RealSessionTests.Schema();
        GameChainAddresses chain = GameChain.Resolve(replay, recorded, replay.ResolvedStatics["GameStates"]);

        Assert.NotEqual(0UL, chain.InGameState);
        Assert.Equal(0x5D2DED757E4UL, chain.AreaInstance);
        Assert.NotEqual(0UL, chain.AreaInstance % 8);
        Assert.Equal(0UL, chain.WorldData);
        Assert.False(AreaInstanceHunt.LooksLikeAreaInstance(replay, recorded, chain.AreaInstance));

        // And the stack's end pointer read at the old +0x10 is the vector's BEGIN under the
        // new layout, so "second-last entry" pointed sixteen bytes before the stack: the
        // unreadable state the first report printed, explained.
        Assert.Equal(GameStateKind.Unreadable, chain.State);
    }

    [Fact]
    public void TheRecordingIsSilentOnTheNewLayout_SoItConfirmsNothingAboutIt()
    {
        // The honest limit of this file: none of the slots the 0.5.5 schema reads were ever
        // read by the build that made it. A replay returns "unread", not a value, so no test
        // here may claim the patched layout from this recording - the next attach does that.
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema live = RealSessionTests.LiveSchema();
        StructDef gs = live.Structs["GameState"];
        ulong gameState = replay.ReadPointer(replay.ResolvedStatics["GameStates"]);

        Assert.Equal(0x50, gs.OffsetOf("States"));
        Assert.Equal(0x18, gs.OffsetOf("CurrentStateVecLast"));
        Assert.False(replay.TryRead(gameState + (ulong)gs.OffsetOf("CurrentStateVecLast"), out ulong _));
        ulong entry4 = gameState + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]);
        Assert.False(replay.TryRead(entry4, out ulong _));

        GameChainAddresses chain = GameChain.Resolve(replay, live, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(0UL, chain.InGameState);
    }
}
