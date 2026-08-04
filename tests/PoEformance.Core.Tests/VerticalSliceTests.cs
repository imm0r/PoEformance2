using PoEformance.Core.Memory;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// End-to-end proof of the whole Core pipeline against a synthetic process laid out
/// like the real game: pattern scan resolves the static, the pointer chain walks
/// static -> GameState -> InGameState -> AreaInstance -> player, and the validator
/// confirms every invariant on the way. This is the same flow Program.cs runs against
/// the live game.
/// </summary>
public class VerticalSliceTests
{
    private const ulong ModuleBase = 0x140000000;
    private const ulong GameStateAddr = 0x20_0000;
    private const ulong InGameStateAddr = 0x30_0000;
    private const ulong AreaInstanceAddr = 0x40_0000;
    private const ulong PlayerInfoAddr = 0x50_0000;
    private const ulong PlayerEntityAddr = 0x60_0000;
    private const ulong EntityDetailsAddr = 0x70_0000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>Builds a fake process whose memory matches the schema's expectations.</summary>
    private static FakeMemoryReader BuildFakeGame(OffsetSchema schema)
    {
        var fake = new FakeMemoryReader { ModuleBase = ModuleBase };

        // Module image containing the GameStates pattern with a RIP displacement that
        // lands on a static cell holding the GameState pointer.
        var module = new byte[0x4000];
        byte[] patternBytes = [0x48, 0x39, 0x2D, 0, 0, 0, 0, 0x0F, 0x85, 0x11, 0x22, 0x33, 0x44, 0xB9, 0x40, 0x01, 0x00, 0x00];
        const int instrOffset = 0x800;
        const int staticCell = 0x3000; // where the disp32 points
        patternBytes.CopyTo(module, instrOffset);
        BitConverter.GetBytes(staticCell - (instrOffset + 7)).CopyTo(module, instrOffset + 3);
        fake.ModuleSize = (uint)module.Length;
        fake.Place(ModuleBase, module);
        fake.Place(ModuleBase + staticCell, GameStateAddr); // *static = GameState

        StructDef gs = schema.Structs["GameState"];
        long entrySize = gs.Constants["StateEntrySize"];
        long igsIndex = gs.Constants["InGameStateIndex"];

        // GameState: state array with InGameState at its well-known index.
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("CurrentStateVecLast"), 0UL);
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("States") + (ulong)(igsIndex * entrySize), InGameStateAddr);

        // InGameState -> AreaInstance + WorldData.
        StructDef igs = schema.Structs["InGameState"];
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), AreaInstanceAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("WorldData"), 0x45_0000UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("UiRootStructPtr"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("GamepadUiRootStructPtr"), 0UL);

        // AreaInstance: level, hash, player info, entity list.
        StructDef ai = schema.Structs["AreaInstance"];
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("CurrentAreaLevel"), 68);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("CurrentAreaHash"), 0xDEAD1234u);
        // LocalPlayerStruct is INLINE at AreaInstance+PlayerInfo: the fields live right
        // there (ServerDataPtr at +0x00, LocalPlayerPtr at +0x20) - there is no
        // separate PlayerInfo allocation to point at.
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("AwakeEntities"), 0x48_0000UL);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("SleepingEntities"), 0UL);

        // PlayerInfo -> player entity -> details -> a Characters path.
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        ulong playerBase = AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo");
        fake.Place(playerBase + (ulong)lp.OffsetOf("ServerDataPtr"), 0x55_0000UL);
        fake.Place(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);
        fake.Place(PlayerEntityAddr + 0x08, EntityDetailsAddr);
        fake.PlaceStdWString(EntityDetailsAddr + 0x08, "Metadata/Characters/Int/IntFour", 0x71_0000);

        return fake;
    }

    [Fact]
    public void FullChain_ScanWalkValidate_AllInvariantsPass()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildFakeGame(schema);

        // 1. Pattern scan resolves the GameStates static.
        var resolver = new StaticResolver(new PatternScanner(fake));
        ResolvedStatic gameStates = resolver.Resolve(schema.Statics["GameStates"]);
        Assert.True(gameStates.Found, gameStates.Detail);
        Assert.Equal(ModuleBase + 0x3000, gameStates.Address);

        // 2. Walk the chain exactly like Program.RunDriftReport does.
        ulong gameState = fake.ReadPointer(gameStates.Address);
        Assert.Equal(GameStateAddr, gameState);

        StructDef gs = schema.Structs["GameState"];
        ulong inGameState = fake.ReadPointer(
            gameState + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
        Assert.Equal(InGameStateAddr, inGameState);

        StructDef igs = schema.Structs["InGameState"];
        ulong areaInstance = fake.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
        Assert.Equal(AreaInstanceAddr, areaInstance);

        // 3. Validate: nothing on the walked path may fail.
        var validator = new SchemaValidator(fake);
        var failures = new List<FieldCheck>();
        foreach ((string name, ulong addr) in new (string, ulong)[]
                 {
                     ("GameState", gameState),
                     ("InGameState", inGameState),
                     ("AreaInstance", areaInstance),
                     ("LocalPlayerStruct", areaInstance + (ulong)schema.Structs["AreaInstance"].OffsetOf("PlayerInfo")),
                 })
        {
            foreach (FieldCheck check in validator.ValidateStruct(schema.Structs[name], addr))
            {
                if (check.Outcome == CheckOutcome.Fail)
                {
                    failures.Add(check);
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void FullChain_SurvivesRecordAndReplay()
    {
        // The same walk, recorded - then replayed from the file with the live "game"
        // gone. Byte-identical behaviour is the whole point of the recording layer.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildFakeGame(schema);

        var file = new MemoryStream();
        ulong liveArea;
        using (var recorder = new RecordingMemoryReader(fake, file))
        {
            recorder.MarkFrame();
            var resolver = new StaticResolver(new PatternScanner(recorder));
            ResolvedStatic gameStates = resolver.Resolve(schema.Statics["GameStates"]);
            ulong gameState = recorder.ReadPointer(gameStates.Address);
            StructDef gs = schema.Structs["GameState"];
            ulong inGameState = recorder.ReadPointer(
                gameState + (ulong)gs.OffsetOf("States")
                + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
            liveArea = recorder.ReadPointer(inGameState + (ulong)schema.Structs["InGameState"].OffsetOf("AreaInstanceData"));
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        var replayResolver = new StaticResolver(new PatternScanner(replay));
        ResolvedStatic replayed = replayResolver.Resolve(schema.Statics["GameStates"]);
        Assert.True(replayed.Found);

        ulong rGameState = replay.ReadPointer(replayed.Address);
        StructDef rgs = schema.Structs["GameState"];
        ulong rInGameState = replay.ReadPointer(
            rGameState + (ulong)rgs.OffsetOf("States")
            + (ulong)(rgs.Constants["InGameStateIndex"] * rgs.Constants["StateEntrySize"]));
        ulong replayArea = replay.ReadPointer(rInGameState + (ulong)schema.Structs["InGameState"].OffsetOf("AreaInstanceData"));

        Assert.Equal(liveArea, replayArea);

        // And the drifted-matrix alarm still fires against the replay: the recorded
        // session has no valid matrix at the W2S offset, so unitVector3 must FAIL
        // (unreadable), not silently pass.
        List<FieldCheck> checks = new SchemaValidator(replay)
            .ValidateStruct(schema.Structs["WorldData"], 0x45_0000UL);
        Assert.Contains(checks, c => c.FieldName == "W2SMatrix" && c.Outcome == CheckOutcome.Fail);
    }
}
