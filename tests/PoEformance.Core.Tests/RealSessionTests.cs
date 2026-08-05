using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// Runs the drift report against a REAL captured Path of Exile 2 session
/// (<c>tests/fixtures/session-2026-08.rec</c>, 869 bytes).
/// </summary>
/// <remarks>
/// This is the test the whole record/replay layer exists for. Every other test builds a
/// synthetic process from the schema, which can only ever confirm that the code agrees
/// with itself; this one replays actual game memory, so it would catch a schema or reader
/// change that stops matching reality - on any machine, in CI, with no game installed.
///
/// The fixture is the session that resolved the 2026-08 drift: the AreaInstance +0x08
/// wave, the inline LocalPlayerStruct, and the world-to-screen matrix relocated to
/// WorldData+0x11C. If a future change breaks one of those, this test says so.
/// </remarks>
public class RealSessionTests
{
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08.rec");
        }
    }

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    private static ReplayMemoryReader LoadSession() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    [Fact]
    public void RecordedSession_IsSmallEnoughToShare()
    {
        // The whole point of dropping the module image from recordings. A session that
        // cannot be sent to someone is a session nobody debugs together.
        var info = new FileInfo(FixturePath);
        Assert.True(info.Length < 64 * 1024, $"fixture grew to {info.Length} bytes");
    }

    [Fact]
    public void RecordedSession_CarriesItsResolvedStatics()
    {
        ReplayMemoryReader replay = LoadSession();

        Assert.Equal(6, replay.ResolvedStatics.Count);
        Assert.Equal(0x7FF6D28F9C48UL, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(0x7FF6D28AFCD0UL, replay.ResolvedStatics["GameCullSize"]);
        Assert.Equal(20652, replay.ProcessId);
    }

    [Fact]
    public void RealGameMemory_PassesEverySchemaInvariant()
    {
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(
            replay, new PatternScanner(replay), schema, writer,
            verbose: true, knownStatics: replay.ResolvedStatics);

        Assert.True(result.AllGood, writer.ToString());
        Assert.Equal(0, result.Failed);
        Assert.Equal(12, result.Passed);
    }

    [Fact]
    public void RealGameMemory_ConfirmsTheOffsetsRecoveredIn2026_08()
    {
        // Each assertion below is one of the findings that cost a live debugging round.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();

        ulong gameState = replay.ReadPointer(replay.ResolvedStatics["GameStates"]);
        StructDef gs = schema.Structs["GameState"];
        ulong inGameState = replay.ReadPointer(
            gameState + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));

        StructDef igs = schema.Structs["InGameState"];
        ulong areaInstance = replay.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
        ulong worldData = replay.ReadPointer(inGameState + (ulong)igs.OffsetOf("WorldData"));
        Assert.NotEqual(0UL, areaInstance);
        Assert.NotEqual(0UL, worldData);

        StructDef ai = schema.Structs["AreaInstance"];

        // The +0x08 wave: the entity map lives at the shifted offset.
        Assert.NotEqual(0UL, replay.ReadPointer(areaInstance + (ulong)ai.OffsetOf("AwakeEntities")));

        // A plausible area level proves we are reading the real struct, not garbage.
        int areaLevel = replay.Read<int>(areaInstance + (ulong)ai.OffsetOf("CurrentAreaLevel"));
        Assert.InRange(areaLevel, 1, 100);

        // LocalPlayerStruct is INLINE - the base is the ADDRESS of PlayerInfo, not the
        // value stored there. Following it must land on a real character.
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        ulong playerBase = areaInstance + (ulong)ai.OffsetOf("PlayerInfo");
        ulong playerEntity = replay.ReadPointer(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"));
        ulong details = replay.ReadPointer(playerEntity + (ulong)schema.Structs["Entity"].OffsetOf("EntityDetailsPtr"));
        string path = replay.ReadStdWString(details + (ulong)schema.Structs["EntityDetails"].OffsetOf("Path"));
        Assert.StartsWith("Metadata/Characters/", path, StringComparison.Ordinal);

        // The matrix at its recovered home, pointing the way PoE2's fixed camera does.
        StructDef wd = schema.Structs["WorldData"];
        ulong wRow = worldData + (ulong)wd.OffsetOf("W2SMatrix") + 0x30;
        Assert.Equal(0.466531f, replay.Read<float>(wRow), 4);      // real captured value
        Assert.Equal(0.751464f, replay.Read<float>(wRow + 8), 4);
    }

    /// <summary>The richer session that captured the player's components + Render position.</summary>
    private static string PlayerFixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir!.FullName, "tests", "fixtures", "session-2026-08-player.rec");
        }
    }

    [Fact]
    public void RealPlayer_HasEveryExpectedComponent()
    {
        // The int32-index fix: a real player carries a dozen-plus components, not the 3
        // that survived the int64 mis-read.
        var replay = ReplayMemoryReader.Load(File.OpenRead(PlayerFixturePath));
        OffsetSchema schema = LoadSchema();
        var chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        PoEformance.Game.Entities.Entity? player = new PoEformance.Game.Entities.EntityReader(replay, schema).Read(chain.PlayerEntity);
        Assert.NotNull(player);
        Assert.True(player!.Components.Count >= 12, $"only {player.Components.Count} components");
        foreach (string expected in (string[])["Render", "Life", "Positioned", "Actor", "Player", "Stats"])
        {
            Assert.True(player.Has(expected), $"player missing {expected} component");
        }
    }

    [Fact]
    public void RealPlayer_ProjectsToScreenCentre_TheMatrixProof()
    {
        // The decisive, end-to-end proof against real memory: resolve the player, read its
        // Render position, project through the column-major matrix - the camera follows the
        // player, so it MUST land at screen centre (NDC ~0). This is what "MATRIX PROVEN"
        // means, pinned as a regression.
        var replay = ReplayMemoryReader.Load(File.OpenRead(PlayerFixturePath));
        OffsetSchema schema = LoadSchema();

        PoEformance.Game.Diagnostics.PlayerProbeResult r =
            new PoEformance.Game.Diagnostics.PlayerProbe(replay, schema).Probe(replay.ResolvedStatics["GameStates"]);

        Assert.True(r.InGame);
        Assert.True(r.HasRender);
        Assert.True(r.Projected);
        Assert.True(r.ProjectsToCentre, $"NDC magnitude was {r.NdcMagnitude:F3}, expected ~0");
        Assert.True(r.NdcMagnitude < 0.02, $"NDC magnitude {r.NdcMagnitude:F4}");
    }
}
