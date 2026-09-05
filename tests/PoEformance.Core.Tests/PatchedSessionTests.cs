using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The first two recordings from the 2026-09-04 content patch (0.5.5):
/// <c>tests/fixtures/session-2026-09-patch.rec</c>, a bare attach, and
/// <c>session-2026-09-patch-2.rec</c>, an attach standing in an area with the report's
/// offline capture switched on.
/// </summary>
/// <remarks>
/// Between them they settle GameState: the exact GameStates pattern is gone and a fallback
/// recovers the static (the second recording keeps the site bytes as a note); the object
/// grew by eight bytes at +0x08, so the state stack and the state array sit +0x08 from where
/// they were; and read with the NEW offsets the game is in the InGame state, which is what
/// the person recording it was looking at. Read with the OLD offsets the same bytes name
/// the Escape state as "InGameState", and that misreading is kept as a test because it
/// looked exactly like an InGameState drift for a day.
///
/// What neither recording can settle: the AreaInstance and UI offsets. Both were made
/// before the schema knew about GameState, so nothing was ever read from the real
/// InGameState - the offsets for those come from GameHelper2 and wait for the next attach.
/// </remarks>
public class PatchedSessionTests
{
    internal static string FixturePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    private static ReplayMemoryReader BareAttach() => ReplayMemoryReader.Load(File.OpenRead(FixturePath("session-2026-09-patch.rec")));

    private static ReplayMemoryReader InArea() => ReplayMemoryReader.Load(File.OpenRead(FixturePath("session-2026-09-patch-2.rec")));

    [Fact]
    public void GameStates_RecoveredByFallback_LeadsToAStateArray()
    {
        ReplayMemoryReader replay = InArea();
        OffsetSchema schema = RealSessionTests.CurrentSchema();

        ulong gameStates = replay.ResolvedStatics["GameStates"];
        Assert.True(DriftReport.LooksLikeGameStates(replay, schema, gameStates));

        // Thirteen entries at the array offset, eleven of them allocated and distinct - only
        // ChangePassword and Credits are empty in an in-area session.
        StructDef gs = schema.Structs["GameState"];
        ulong statesBase = replay.ReadPointer(gameStates) + (ulong)gs.OffsetOf("States");
        var distinct = new HashSet<ulong>();
        for (long i = 0; i < gs.Constants["TotalStates"]; i++)
        {
            Assert.True(replay.TryRead(statesBase + (ulong)(i * gs.Constants["StateEntrySize"]), out ulong entry));
            if (entry != 0)
            {
                distinct.Add(entry);
            }
        }

        Assert.Equal(11, distinct.Count);
    }

    [Fact]
    public void TheFingerprint_AcceptedTheSiteThroughTheOldLayout_WhichIsWhyItStillWorked()
    {
        // On the day the fallback ran, the schema still had the array at 0x48, so the
        // fingerprint read the SECOND halves of the entries - which are pointers too, ten of
        // them distinct. Right answer, wrong reason; kept because the next time a struct
        // grows, a fingerprint that passes is not proof that the offsets under it are right.
        ReplayMemoryReader replay = BareAttach();
        OffsetSchema stale = RealSessionTests.CurrentSchema().AsOf(RealSessionTests.PrePatchEra);

        Assert.True(DriftReport.LooksLikeGameStates(replay, stale, replay.ResolvedStatics["GameStates"]));
    }

    [Fact]
    public void TheSecondRecording_KeepsTheFallbackSite_WhichIsNowThePrimaryPattern()
    {
        ReplayMemoryReader replay = InArea();
        OffsetSchema schema = RealSessionTests.CurrentSchema();

        Assert.Equal("2 at 7FF7B86EBFAE: 48 39 2D E3 5E 4C 04 0F 85 20 01 00 00 B9 48 01 00 00", replay.Notes["fallback:GameStates"]);

        // The primary pattern is the site's bytes with the RIP displacement and the jump
        // distance wildcarded - what the report told the reader to write back.
        string primary = schema.Statics["GameStates"].Pattern;
        Assert.StartsWith("48 39 2D ^ ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? B9 48 01 00 00", primary, StringComparison.Ordinal);
    }

    [Fact]
    public void GameState_GrewByEightBytes_AndTheNewOffsetsNameTheInGameState()
    {
        ReplayMemoryReader replay = InArea();
        OffsetSchema schema = RealSessionTests.CurrentSchema();
        StructDef gs = schema.Structs["GameState"];
        ulong gameState = replay.ReadPointer(replay.ResolvedStatics["GameStates"]);

        // The inserted slot reads zero; the stack vector's three pointers follow it in order.
        Assert.Equal(0UL, replay.Read<ulong>(gameState + 0x08));
        ulong first = replay.Read<ulong>(gameState + 0x10);
        ulong last = replay.Read<ulong>(gameState + (ulong)gs.OffsetOf("CurrentStateVecLast"));
        ulong capacity = replay.Read<ulong>(gameState + 0x20);
        Assert.True(first < last && last <= capacity);
        Assert.Equal(0x10UL, last - first); // one state on the stack

        // The stack's one entry is the InGame entry of the array: same object, by address.
        ulong active = replay.ReadPointer(last - 0x10);
        ulong inGameEntry = replay.ReadPointer(gameState + (ulong)gs.OffsetOf("States") + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
        Assert.Equal(active, inGameEntry);
        Assert.Equal(0x5D27CE51410UL, active);

        // Every allocated entry is a {ptr, ptr - 0x10} pair.
        for (long i = 0; i < gs.Constants["TotalStates"]; i++)
        {
            ulong entry = gameState + (ulong)gs.OffsetOf("States") + (ulong)(i * gs.Constants["StateEntrySize"]);
            ulong x = replay.Read<ulong>(entry);
            ulong y = replay.Read<ulong>(entry + 8);
            Assert.True(x == 0 ? y == 0 : x == y + 0x10, $"entry {i}: 0x{x:X} / 0x{y:X}");
        }

        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);
        Assert.Equal(active, chain.InGameState);
    }

    [Fact]
    public void TheOldOffsets_ReadTheEscapeState_AsIfItWereInGameState()
    {
        // The day-one misreading, kept: 0x48 + 4 * 0x10 lands on the second half of entry 3,
        // and the walk carried on into that object as though nothing was wrong.
        ReplayMemoryReader replay = InArea();
        OffsetSchema stale = RealSessionTests.CurrentSchema().AsOf(RealSessionTests.PrePatchEra);
        Assert.Equal(0x48, stale.Structs["GameState"].OffsetOf("States"));

        GameChainAddresses chain = GameChain.Resolve(replay, stale, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.Unreadable, chain.State);
        Assert.Equal(0x5D296A02000UL, chain.InGameState);
        Assert.Equal(0x5D2DED757E4UL, chain.AreaInstance);
        Assert.NotEqual(0UL, chain.AreaInstance % 8);
        Assert.Equal(0UL, chain.WorldData);
    }
}
