using PoEformance.Core.Diagnostics;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// Runs the real <see cref="DriftReport"/> engine - the exact code path the App takes
/// against the live game - over a synthetic process laid out like PoE2. Because the
/// engine lives in Core, this both proves the pipeline and lets us see its output
/// without the game.
/// </summary>
public class DriftReportTests
{
    private const ulong ModuleBase = 0x140000000;
    private const ulong GameStateAddr = 0x20_0000;
    private const ulong InGameStateAddr = 0x30_0000;
    private const ulong AreaInstanceAddr = 0x40_0000;
    private const ulong WorldDataAddr = 0x45_0000;
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

    /// <summary>A fake game whose memory satisfies the schema at the CURRENT offsets.</summary>
    private static FakeMemoryReader BuildHealthyGame(OffsetSchema schema)
    {
        var fake = new FakeMemoryReader { ModuleBase = ModuleBase };

        // Module image: the GameStates pattern with a RIP disp landing on a static cell.
        var module = new byte[0x4000];
        byte[] pattern = [0x48, 0x39, 0x2D, 0, 0, 0, 0, 0x0F, 0x85, 0x11, 0x22, 0x33, 0x44, 0xB9, 0x40, 0x01, 0x00, 0x00];
        const int instrOffset = 0x800;
        const int staticCell = 0x3000;
        pattern.CopyTo(module, instrOffset);
        BitConverter.GetBytes(staticCell - (instrOffset + 7)).CopyTo(module, instrOffset + 3);
        fake.ModuleSize = (uint)module.Length;
        fake.Place(ModuleBase, module);
        fake.Place(ModuleBase + staticCell, GameStateAddr);

        StructDef gs = schema.Structs["GameState"];
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("CurrentStateVecLast"), 0UL);

        // Every state object exists, each at its own address - the array the GameStates
        // fingerprint looks for - with the in-game one where the chain expects it.
        long entrySize = gs.Constants["StateEntrySize"];
        for (long i = 0; i < gs.Constants["TotalStates"]; i++)
        {
            fake.Place(GameStateAddr + (ulong)gs.OffsetOf("States") + (ulong)(i * entrySize),
                i == gs.Constants["InGameStateIndex"] ? InGameStateAddr : 0x21_0000UL + (ulong)(i * 0x1000));
        }

        StructDef igs = schema.Structs["InGameState"];
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), AreaInstanceAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("WorldData"), WorldDataAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("UiRootStructPtr"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("GamepadUiRootStructPtr"), 0UL);

        // WorldData: a real unit-vector w-row so the W2SMatrix invariant passes.
        StructDef wd = schema.Structs["WorldData"];
        fake.Place(WorldDataAddr + (ulong)wd.OffsetOf("WorldAreaDetailsPtr"), 0x46_0000UL);
        fake.Place(WorldDataAddr + (ulong)wd.OffsetOf("CameraStructure"), 0x47_0000UL);
        ulong matrix = WorldDataAddr + (ulong)wd.OffsetOf("W2SMatrix");
        fake.Place<float>(matrix + 0x30, 0.467f);
        fake.Place<float>(matrix + 0x34, 0.467f);
        fake.Place<float>(matrix + 0x38, 0.751f);

        StructDef ai = schema.Structs["AreaInstance"];
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("CurrentAreaLevel"), 68);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("CurrentAreaHash"), 0xDEAD1234u);
        // LocalPlayerStruct is INLINE at AreaInstance+PlayerInfo: the fields live right
        // there (ServerDataPtr at +0x00, LocalPlayerPtr at +0x20) - there is no
        // separate PlayerInfo allocation to point at.
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("AwakeEntities"), 0x48_0000UL);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("SleepingEntities"), 0UL);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("Environments"), 0x49_0000UL);

        StructDef lp = schema.Structs["LocalPlayerStruct"];
        ulong playerBase = AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo");
        fake.Place(playerBase + (ulong)lp.OffsetOf("ServerDataPtr"), 0x55_0000UL);
        fake.Place(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);
        fake.Place(PlayerEntityAddr + 0x08, EntityDetailsAddr);
        fake.PlaceStdWString(EntityDetailsAddr + 0x08, "Metadata/Characters/Int/IntFour", 0x71_0000);

        // The rest of the tail as the hunt recognises it: the awake map's sentinel and
        // root, the sleeping map's sentinel, and the terrain struct pointing back at its
        // owner. Placed so the stale-schema test below sees a complete wave, not one field.
        StructDef map = schema.Structs["StdMap"];
        StructDef node = schema.Structs["StdMapNode"];
        ulong awake = AreaInstanceAddr + (ulong)ai.OffsetOf("AwakeEntities");
        fake.Place(awake + (ulong)map.OffsetOf("Size"), 7L);
        fake.Place(0x48_0000UL, new byte[0x30]);
        fake.Place<byte>(0x48_0000UL + (ulong)node.OffsetOf("IsNil"), 1);
        fake.Place(0x48_0000UL + (ulong)node.OffsetOf("Parent"), 0x48_1000UL);
        fake.Place(0x48_1000UL, new byte[0x30]);
        fake.Place(0x48_1000UL + (ulong)node.OffsetOf("ValueEntityPtr"), 0x48_2000UL);
        fake.Place(0x48_2000UL + 0x08, 0x48_3000UL);
        fake.PlaceStdWString(0x48_3000UL + 0x08, "Metadata/Monsters/Skeletons/SkeletonSoldier", 0x48_4000);
        ulong sleeping = AreaInstanceAddr + (ulong)ai.OffsetOf("SleepingEntities");
        fake.Place(sleeping + (ulong)map.OffsetOf("Head"), 0x48_5000UL);
        fake.Place(sleeping + (ulong)map.OffsetOf("Size"), 0L);
        fake.Place(0x48_5000UL, new byte[0x30]);
        fake.Place<byte>(0x48_5000UL + (ulong)node.OffsetOf("IsNil"), 1);

        StructDef terrain = schema.Structs["TerrainMetadata"];
        ulong terrainAt = AreaInstanceAddr + (ulong)ai.OffsetOf("TerrainMetadata");
        fake.Place(terrainAt, ModuleBase + 0x2000);
        fake.Place(terrainAt + 8, AreaInstanceAddr);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesX"), 39L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesY"), 45L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesPlusOneX"), 40L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesPlusOneX") + 8, 46L);

        return fake;
    }

    [Fact]
    public void HealthyGame_PassesTheGameStatesFingerprint()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildHealthyGame(schema);

        Assert.True(DriftReport.LooksLikeGameStates(fake, schema, ModuleBase + 0x3000));
        Assert.False(DriftReport.LooksLikeGameStates(fake, schema, ModuleBase + 0x3008)); // a slot that holds nothing
    }

    [Fact]
    public void OldSchema_AgainstNewGame_NamesTheWaveInTheReport()
    {
        // The follow-up to the alarm: the report does not stop at "these rows failed", it
        // sweeps the struct and prints where the tail went. The stale schema is 8 bytes
        // behind on every tail field, so the hunt must say +0x8 for each and as the consensus.
        OffsetSchema current = LoadSchema();
        FakeMemoryReader newGame = BuildHealthyGame(current);
        OffsetSchema stale = SchemaJson.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StaleSchemaJson)));
        var writer = new StringWriter();

        DriftReport.Run(newGame, new PatternScanner(newGame), stale, writer, verbose: false);
        string text = writer.ToString();

        Assert.Contains("area instance hunt", text);
        // The stale schema is the pre-2026-08 layout; the fake game is laid out at the
        // CURRENT offsets, so the distance is whatever the two waves since add up to.
        StructDef ai = current.Structs["AreaInstance"];
        int wave = ai.OffsetOf("PlayerInfo") - 0x598;
        Assert.Contains($"PlayerInfo        schema 0x598 -> found 0x{ai.OffsetOf("PlayerInfo"):X} (+0x{wave:X})", text);
        Assert.Contains($"AwakeEntities     schema 0x6D8 -> found 0x{ai.OffsetOf("AwakeEntities"):X} (+0x{wave:X})", text);
        Assert.Contains($"SleepingEntities  schema 0x6E8 -> found 0x{ai.OffsetOf("SleepingEntities"):X} (+0x{wave:X})", text);
        Assert.Contains($"TerrainMetadata   schema 0x8B8 -> found 0x{ai.OffsetOf("TerrainMetadata"):X} (+0x{wave:X})", text);
        Assert.Contains($"the whole tail moved +0x{wave:X}", text);
    }

    [Fact]
    public void DriftedParentPointer_IsNamedInsteadOfBlamedOnTheFields()
    {
        // InGameState.AreaInstanceData one slot too low: the schema's slot reads a pointer
        // to nothing in particular, every AreaInstance row fails, and the answer is the
        // parent - which the report must say, offset and delta included.
        OffsetSchema current = LoadSchema();
        FakeMemoryReader game = BuildHealthyGame(current);
        int areaOffset = current.Structs["InGameState"].OffsetOf("AreaInstanceData");
        const ulong decoy = 0x41_0000;
        game.Place(decoy + AreaInstanceHunt.WindowStart, new byte[AreaInstanceHunt.WindowEnd - AreaInstanceHunt.WindowStart]);
        game.Place(decoy + (ulong)current.Structs["AreaInstance"].OffsetOf("CurrentAreaLevel"), 68);
        game.Place(InGameStateAddr + (ulong)areaOffset, decoy);
        game.Place(InGameStateAddr + (ulong)areaOffset + 8, AreaInstanceAddr);
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(game, new PatternScanner(game), current, writer, verbose: false);
        string text = writer.ToString();

        Assert.True(result.Failed > 0);
        Assert.Contains($"parent: InGameState+0x{areaOffset + 8:X} (+0x8) -> 0x{AreaInstanceAddr:X} carries the AreaInstance fingerprints", text);
        Assert.Contains("InGameState.AreaInstanceData drifted; fix that offset first", text);
    }

    [Fact]
    public void HealthyGame_ReportsNoFailures()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildHealthyGame(schema);
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(fake, new PatternScanner(fake), schema, writer, verbose: true);

        // The fixture's module only carries the GameStates pattern (the other five
        // statics legitimately MISS), so the assertion is: chain resolved, zero FAILs.
        Assert.True(result.GameStatesResolved);
        Assert.Equal(0, result.Failed);

        string text = writer.ToString();
        Assert.Contains("ok    LocalPlayerStruct.LocalPlayerPtr", text);
        // W2SMatrix deliberately carries no structural invariant - a byte pattern cannot
        // tell the real matrix from a decoy, so it is verified by MatrixHunt instead.
        Assert.DoesNotContain("FAIL  WorldData.W2SMatrix", text);
    }

    [Fact]
    public void OldSchema_AgainstNewGame_FlagsExactlyTheDriftedFields()
    {
        // The real scenario: the game moved on (memory laid out at CURRENT offsets) but
        // we hand the report a schema still using the PRE-2026-08 AreaInstance offsets.
        // The report must fail precisely the fields that drifted, and pass the rest.
        OffsetSchema current = LoadSchema();
        FakeMemoryReader newGame = BuildHealthyGame(current);

        OffsetSchema stale = SchemaJson.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StaleSchemaJson)));
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(newGame, new PatternScanner(newGame), stale, writer, verbose: false);

        Assert.True(result.GameStatesResolved); // statics unaffected
        Assert.True(result.Failed > 0);

        // The alarm fires on EXACTLY the drifted fields. AwakeEntities (0x6D8 -> 0x6E0)
        // reads nothing valid at its stale offset. PlayerInfo is an INLINE base, so a
        // stale PlayerInfo offset points the walk at the wrong bytes and the
        // LocalPlayerStruct rows fail directly (the string check can't find the player
        // path) - the alarm lands right on the player fields.
        Assert.Contains(result.Failures, f => f.StructName == "AreaInstance" && f.FieldName == "AwakeEntities");
        Assert.Contains(result.Failures, f => f.StructName == "LocalPlayerStruct" && f.FieldName == "LocalPlayerPtr");

        // But the low, un-drifted fields still pass - this is a targeted alarm, not noise.
        Assert.Contains(result.Checks, c =>
            c.StructName == "AreaInstance" && c.FieldName == "CurrentAreaLevel" && c.Outcome == CheckOutcome.Pass);
    }

    [Fact]
    public void ParentPointerFarAway_IsStillFound_ByTheWholeObjectSweep()
    {
        // The 2026-09-05 shape: the schema's slot is not off by a little, it is not a pointer
        // at all, and the real one sits well outside the near radius.
        OffsetSchema current = LoadSchema();
        FakeMemoryReader game = BuildHealthyGame(current);
        int areaOffset = current.Structs["InGameState"].OffsetOf("AreaInstanceData");
        game.Place(InGameStateAddr + (ulong)areaOffset, 0x5D2DED757E4UL); // heap-range, unaligned, not a struct
        game.Place(InGameStateAddr + (ulong)areaOffset + 0x200, AreaInstanceAddr);
        var writer = new StringWriter();

        DriftReport.Run(game, new PatternScanner(game), current, writer, verbose: false);
        string text = writer.ToString();

        Assert.Contains($"parent: InGameState+0x{areaOffset + 0x200:X} (+0x200) -> 0x{AreaInstanceAddr:X} carries the AreaInstance fingerprints", text);
    }

    [Fact]
    public void NullWorldData_IsHuntedByItsBackReferenceToTheAreaInstance()
    {
        OffsetSchema current = LoadSchema();
        FakeMemoryReader game = BuildHealthyGame(current);
        int worldOffset = current.Structs["InGameState"].OffsetOf("WorldData");
        int areaDetails = current.Structs["WorldData"].OffsetOf("WorldAreaDetailsPtr");
        game.Place(InGameStateAddr + (ulong)worldOffset, 0UL);                  // the schema slot went null
        game.Place(InGameStateAddr + (ulong)worldOffset + 0x48, WorldDataAddr); // the struct moved down the object
        game.Place(WorldDataAddr + (ulong)areaDetails, AreaInstanceAddr);       // and still names its area
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(game, new PatternScanner(game), current, writer, verbose: false);
        string text = writer.ToString();

        Assert.Contains(result.Failures, f => f.StructName == "InGameState" && f.FieldName == "WorldData");
        Assert.Contains("world data hunt", text);
        Assert.Contains($"InGameState+0x{worldOffset + 0x48:X} (+0x48) -> 0x{WorldDataAddr:X} points back at the AreaInstance", text);
        Assert.Contains("state   ", text);
    }

    /// <summary>
    /// A minimal schema pinned to the PRE-2026-08 AreaInstance offsets (PlayerInfo 0x598,
    /// AwakeEntities 0x6D8, TerrainMetadata 0x8B8), everything else current. Used to prove
    /// the report catches the exact drift the owner reported.
    /// </summary>
    private const string StaleSchemaJson = """
    {
      "version": 1,
      "gameVersion": "stale-pre-2026-08",
      "statics": {
        "GameStates": { "pattern": "48 39 2D ^ ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? B9 40 01 00 00" }
      },
      "structs": {
        "GameState": {
          "fields": {
            "CurrentStateVecLast": { "offset": "0x18", "type": "ptr", "invariant": { "kind": "plausiblePtr" } },
            "States": { "offset": "0x50", "type": "ptr" }
          },
          "consts": { "StateEntrySize": "0x10", "InGameStateIndex": "4", "TotalStates": "13" }
        },
        "InGameState": {
          "fields": {
            "AreaInstanceData": { "offset": "0x290", "type": "ptr", "invariant": { "kind": "nonNullPtr" } },
            "UiRootStructPtr": { "offset": "0x2F0", "type": "ptr" },
            "WorldData": { "offset": "0x368", "type": "ptr", "invariant": { "kind": "nonNullPtr" } }
          }
        },
        "AreaInstance": {
          "fields": {
            "CurrentAreaLevel": { "offset": "0xBC", "type": "i32", "invariant": { "kind": "range", "min": 0, "max": 100 } },
            "Environments": { "offset": "0x4C0", "type": "ptr" },
            "PlayerInfo": { "offset": "0x598", "type": "ptr", "invariant": { "kind": "nonNullPtr" } },
            "AwakeEntities": { "offset": "0x6D8", "type": "ptr", "invariant": { "kind": "nonNullPtr" } },
            "SleepingEntities": { "offset": "0x6E8", "type": "ptr" },
            "TerrainMetadata": { "offset": "0x8B8", "type": "ptr" }
          }
        },
        "WorldData": {
          "fields": {
            "W2SMatrix": { "offset": "0x1A0", "type": "mat4x4", "invariant": { "kind": "unitVector3", "at": "0x30" } }
          }
        },
        "LocalPlayerStruct": {
          "fields": {
            "ServerDataPtr": { "offset": "0x00", "type": "ptr" },
            "LocalPlayerPtr": { "offset": "0x20", "type": "ptr",
              "invariant": { "kind": "stringContains", "needle": "Metadata/Characters", "hops": ["0x00", "0x08"], "stringAt": "0x08" } }
          }
        },
        "StdMap": { "fields": { "Head": { "offset": "0x00", "type": "ptr" }, "Size": { "offset": "0x08", "type": "i64" } } },
        "StdMapNode": {
          "fields": {
            "Parent": { "offset": "0x08", "type": "ptr" },
            "IsNil": { "offset": "0x19", "type": "u8" },
            "ValueEntityPtr": { "offset": "0x28", "type": "ptr" }
          }
        },
        "Entity": { "fields": { "EntityDetailsPtr": { "offset": "0x08", "type": "ptr" } } },
        "EntityDetails": { "fields": { "Path": { "offset": "0x08", "type": "stdWString" } } },
        "TerrainMetadata": {
          "fields": {
            "TotalTilesX": { "offset": "0x18", "type": "i64" },
            "TotalTilesY": { "offset": "0x20", "type": "i64" },
            "TotalTilesPlusOneX": { "offset": "0x40", "type": "i64" }
          }
        }
      }
    }
    """;
}
