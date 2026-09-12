using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The first in-area recording from the 0.5.5 client, made with the re-based schema
/// (<c>tests/fixtures/session-2026-09-live.rec</c>: 7,577 frames, 1.8 MB, 2026-09-11).
/// </summary>
/// <remarks>
/// The recording that turns the 2026-09-11 offsets from "agreeing with the reference" into
/// "read from the game": the whole chain resolves through the shipped schema, every
/// invariant passes, and the frozen pre-patch schema fails at its first row on the same
/// bytes - which is the mirror image of what every 2026-08 fixture does, and the reason
/// this file replays through <see cref="RealSessionTests.LiveSchema"/> rather than the
/// recorded one.
///
/// What it cannot answer, and why: the build that made it never walked the interface tree
/// (UiRootStruct.UiRootPtr was not read), so nothing here speaks to StringIdPtr, TextPtr or
/// ItemPtr. It did read the ImportantUiElements slots, and those say something the schema
/// had only from the reference: at the 0.5.4 MapParentPtr slot sits an object whose +0x28
/// and +0x30 are packed integers rather than the two map pointers, and at the 0.5.4
/// RightPanelPtr slot sits something that is not a UiElement - exactly what one qword of
/// misalignment looks like, and consistent with GameHelper2 moving those pointers by -8.
/// </remarks>
public class LiveClientSessionTests
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-live.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    [Fact]
    public void TheStaticsResolvedThroughThePrimaryPatterns()
    {
        ReplayMemoryReader replay = Load();

        Assert.Equal(30064, replay.ProcessId);
        Assert.Equal(6, replay.ResolvedStatics.Count);
        // A fallback hit is recorded as a note; none here, so the re-anchored GameStates
        // pattern (allocator immediate 0x148) found its site itself.
        Assert.DoesNotContain(replay.Notes.Keys, k => k.StartsWith("fallback:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShippedSchemaPassesEveryInvariant_AndTheRecordedOneFailsAtGameState()
    {
        ReplayMemoryReader replay = Load();
        var live = new StringWriter();
        DriftReportResult onLive = DriftReport.Run(
            replay, new PatternScanner(replay), RealSessionTests.LiveSchema(), live,
            verbose: true, knownStatics: replay.ResolvedStatics);

        Assert.True(onLive.AllGood, live.ToString());
        Assert.Equal(0, onLive.Failed);
        Assert.Equal(11, onLive.Passed);
        Assert.Contains("state   InGame", live.ToString());

        // The same bytes through the pre-patch layout: the stack's end pointer at the old
        // +0x10 is not in the file (the new build reads +0x18), and the chain stops there.
        var frozen = new StringWriter();
        DriftReportResult onFrozen = DriftReport.Run(
            replay, new PatternScanner(replay), RealSessionTests.Schema(), frozen,
            verbose: true, knownStatics: replay.ResolvedStatics);

        Assert.False(onFrozen.AllGood);
        Assert.Contains(onFrozen.Failures, f => f.StructName == "GameState" && f.FieldName == "CurrentStateVecLast");
        Assert.Contains("InGameState not reachable", frozen.ToString());
    }

    [Fact]
    public void TheChainReachesThePlayer_AndTheAreaInstanceCarriesItsFingerprints()
    {
        ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        Assert.Equal(GameStateKind.InGame, chain.State);
        Assert.NotEqual(0UL, chain.PlayerEntity);
        Assert.True(AreaInstanceHunt.LooksLikeAreaInstance(replay, schema, chain.AreaInstance));

        // WorldAreaDetailsPtr still names the AreaInstance itself, as it did in 21 of 22
        // pre-patch recordings - so that identity survived the patch along with the offset.
        ulong details = replay.ReadPointer(chain.WorldData + (ulong)schema.Structs["WorldData"].OffsetOf("WorldAreaDetailsPtr"));
        Assert.Equal(chain.AreaInstance, details);

        StructDef ai = schema.Structs["AreaInstance"];
        Assert.True(replay.TryRead(chain.AreaInstance + (ulong)ai.OffsetOf("CurrentAreaLevel"), out int level));
        Assert.Equal(2, level);
    }

    [Fact]
    public void TheStateArrayAt0x50_HoldsInGameStateAtIndex4()
    {
        // The first qwords this time, not the second halves the patched recording was read
        // through (see PatchedSessionTests): all thirteen entries present, entry 4 is the
        // InGameState the rest of the chain then validates.
        ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef gs = schema.Structs["GameState"];
        ulong gameState = replay.ReadPointer(replay.ResolvedStatics["GameStates"]);
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

        Assert.Equal(11, distinct.Count);
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(replay.ReadPointer(statesBase + (ulong)(4 * gs.Constants["StateEntrySize"])), chain.InGameState);
    }

    [Fact]
    public void TheOldMapParentAndRightPanelSlots_DoNotLeadWhereTheSchemaSaid()
    {
        // The evidence for moving LeftPanelPtr, RightPanelPtr and MapParentPtr by -8. It is
        // negative evidence about the OLD slots, read by the build that still used them; the
        // new slots were not read, so their confirmation is the next recording's.
        ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        replay.Seek((uint)(replay.FrameCount - 1));
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        ulong uiRootStruct = chain.UiRoot;
        Assert.NotEqual(0UL, uiRootStruct);

        StructDef important = schema.Structs["ImportantUiElements"];
        Assert.Equal(0x7C0, important.OffsetOf("MapParentPtr"));
        Assert.Equal(0x6D0, important.OffsetOf("LeftPanelPtr"));
        Assert.Equal(0x6D8, important.OffsetOf("RightPanelPtr"));

        // The 0.5.4 map parent slot: a pointer, to something whose +0x28/+0x30 are not the
        // two map pointers (MapParentStruct) but packed integers.
        ulong oldMapParent = replay.ReadPointer(uiRootStruct + 0x7C8);
        Assert.True(MemoryReaderExtensions.IsPlausiblePointer(oldMapParent));
        Assert.True(replay.TryRead(oldMapParent + 0x28, out ulong notLarge));
        Assert.True(replay.TryRead(oldMapParent + 0x30, out ulong notMini));
        Assert.False(MemoryReaderExtensions.IsPlausiblePointer(notLarge));
        Assert.False(MemoryReaderExtensions.IsPlausiblePointer(notMini));

        // The 0.5.4 right-panel slot: a pointer to something that is not a UiElement.
        ulong oldRight = replay.ReadPointer(uiRootStruct + 0x6E0);
        Assert.True(MemoryReaderExtensions.IsPlausiblePointer(oldRight));
        Assert.True(replay.TryRead(oldRight + 0x08, out ulong self));
        Assert.NotEqual(oldRight, self);

        // The two the reference left alone still lead to elements (self-pointer at +0x08).
        foreach (string name in new[] { "PassiveSkillTreePanel", "WorldMapPanelPtr" })
        {
            ulong element = replay.ReadPointer(uiRootStruct + (ulong)important.OffsetOf(name));
            Assert.True(replay.TryRead(element + 0x08, out ulong elementSelf));
            Assert.Equal(element, elementSelf);
        }

        // And the new slots are simply absent from the file.
        Assert.False(replay.TryRead(uiRootStruct + 0x7C0, out ulong _));
        Assert.False(replay.TryRead(uiRootStruct + 0x6D0, out ulong _));
    }

    [Fact]
    public void TheInterfaceTreeWasNeverWalked_SoTheUiElementRowsStayUnconfirmed()
    {
        // Written down so nobody reads "all invariants pass" as covering StringIdPtr, TextPtr
        // or ItemPtr: the root element pointer is not in the file, so no element was.
        ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        replay.Seek((uint)(replay.FrameCount - 1));
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        Assert.False(replay.TryRead(chain.UiRoot + (ulong)schema.Structs["UiRootStruct"].OffsetOf("UiRootPtr"), out ulong _));
    }
}
