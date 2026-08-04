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
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]), InGameStateAddr);

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
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo"), PlayerInfoAddr);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("AwakeEntities"), 0x48_0000UL);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("SleepingEntities"), 0UL);
        fake.Place(AreaInstanceAddr + (ulong)ai.OffsetOf("Environments"), 0x49_0000UL);

        StructDef lp = schema.Structs["LocalPlayerStruct"];
        fake.Place(PlayerInfoAddr + (ulong)lp.OffsetOf("ServerDataPtr"), 0x55_0000UL);
        fake.Place(PlayerInfoAddr + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);
        fake.Place(PlayerEntityAddr + 0x08, EntityDetailsAddr);
        fake.PlaceStdWString(EntityDetailsAddr + 0x08, "Metadata/Characters/Int/IntFour", 0x71_0000);

        return fake;
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
        Assert.Contains("ok    WorldData.W2SMatrix", text);
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

        // The alarm fires on EXACTLY the drifted fields: PlayerInfo (0x598 -> 0x5A0) and
        // AwakeEntities (0x6D8 -> 0x6E0) read nothing valid at their stale offsets. The
        // walk then honestly reports LocalPlayerStruct as unreachable instead of
        // validating garbage - the failure surfaces at its true source, one level up.
        Assert.Contains(result.Failures, f => f.StructName == "AreaInstance" && f.FieldName == "PlayerInfo");
        Assert.Contains(result.Failures, f => f.StructName == "AreaInstance" && f.FieldName == "AwakeEntities");
        Assert.Contains("LocalPlayerStruct not reachable", writer.ToString());

        // But the low, un-drifted fields still pass - this is a targeted alarm, not noise.
        Assert.Contains(result.Checks, c =>
            c.StructName == "AreaInstance" && c.FieldName == "CurrentAreaLevel" && c.Outcome == CheckOutcome.Pass);
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
            "CurrentStateVecLast": { "offset": "0x10", "type": "ptr", "invariant": { "kind": "plausiblePtr" } },
            "States": { "offset": "0x48", "type": "ptr" }
          },
          "consts": { "StateEntrySize": "0x10", "InGameStateIndex": "4" }
        },
        "InGameState": {
          "fields": {
            "AreaInstanceData": { "offset": "0x290", "type": "ptr", "invariant": { "kind": "nonNullPtr" } },
            "WorldData": { "offset": "0x368", "type": "ptr", "invariant": { "kind": "nonNullPtr" } }
          }
        },
        "AreaInstance": {
          "fields": {
            "CurrentAreaLevel": { "offset": "0xC4", "type": "i32", "invariant": { "kind": "range", "min": 0, "max": 100 } },
            "PlayerInfo": { "offset": "0x598", "type": "ptr", "invariant": { "kind": "nonNullPtr" } },
            "AwakeEntities": { "offset": "0x6D8", "type": "ptr", "invariant": { "kind": "nonNullPtr" } }
          }
        },
        "WorldData": {
          "fields": {
            "W2SMatrix": { "offset": "0x1A0", "type": "mat4x4", "invariant": { "kind": "unitVector3", "at": "0x30" } }
          }
        },
        "LocalPlayerStruct": {
          "fields": {
            "LocalPlayerPtr": { "offset": "0x20", "type": "ptr",
              "invariant": { "kind": "stringContains", "needle": "Metadata/Characters", "hops": ["0x00", "0x08"], "stringAt": "0x08" } }
          }
        }
      }
    }
    """;
}
