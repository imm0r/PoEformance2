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
/// wave and the inline LocalPlayerStruct. If a future change breaks one of those, this
/// test says so.
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

    /// <summary>A richer session: a full area with 76 entities, for scene-wide checks.</summary>
    internal static string SceneFixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-scene.rec");
        }
    }

    /// <summary>
    /// A whole map: 7,041 frames over 321 seconds, from entering to the loading screen out.
    /// </summary>
    /// <remarks>
    /// The first recording that holds a session rather than a moment, and the only one that
    /// can be asked what happens over TIME - monsters dying, packs being left behind, the
    /// damage meter's figures accumulating. The earlier fixtures are single instants and
    /// answer none of that.
    ///
    /// 1.4 MB, which is large for a fixture and worth it: every question settled against it
    /// so far had been argued about for days beforehand. It is also what the recorder's own
    /// changes are measured against - the compression, the redundant-read elimination and the
    /// segment size all have their numbers from this file.
    /// </remarks>
    internal static string MapFixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-map.rec");
        }
    }

    /// <summary>
    /// A moment before the 2026-09-04 content patch, which every fixture named 2026-08 or
    /// 2026-09-0[1] predates. The schema as of then is the one those recordings were laid out
    /// under - see <see cref="OffsetSchema.AsOf"/>.
    /// </summary>
    internal static readonly DateTime PrePatchEra = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The shipped schema as it stood for the 2026-08 fixtures, for tests in sibling classes.
    /// </summary>
    /// <remarks>
    /// Every test that replays a committed recording wants the layout its bytes were captured
    /// under, and every synthetic test builds its fake from whatever offsets the schema hands
    /// it, so the pre-patch era serves both. A test about the CURRENT client loads the schema
    /// itself - see <see cref="PatchedSessionTests"/>.
    /// </remarks>
    internal static OffsetSchema Schema() => LoadSchema().AsOf(PrePatchEra);

    /// <summary>The shipped schema for the current client.</summary>
    internal static OffsetSchema CurrentSchema() => LoadSchema();

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
        OffsetSchema schema = Schema();
        var writer = new StringWriter();

        DriftReportResult result = DriftReport.Run(
            replay, new PatternScanner(replay), schema, writer,
            verbose: true, knownStatics: replay.ResolvedStatics);

        Assert.True(result.AllGood, writer.ToString());
        Assert.Equal(0, result.Failed);
        Assert.Equal(11, result.Passed);
    }

    [Fact]
    public void RealGameMemory_ConfirmsTheOffsetsRecoveredIn2026_08()
    {
        // Each assertion below is one of the findings that cost a live debugging round.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = Schema();

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

        // No matrix assertion here on purpose. This session predates locating the matrix,
        // and the value that USED to be asserted (a unit camera-forward row at +0x30) was
        // the fingerprint of the wrong block - see MatrixHuntTests for what replaced it.
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
        OffsetSchema schema = Schema();
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
    public void RealScene_ReadsAWholeAreaOfEntities()
    {
        // The entity map walked against real memory: a populated area, with the player
        // among the entities and monsters carrying real metadata paths and positions.
        var replay = ReplayMemoryReader.Load(File.OpenRead(SceneFixturePath));
        OffsetSchema schema = Schema();

        PoEformance.Game.World.WorldSnapshot snapshot =
            new PoEformance.Game.World.WorldReader(replay, schema).Read(replay.ResolvedStatics["GameStates"]);

        Assert.True(snapshot.InGame);
        Assert.True(snapshot.Entities.Count > 50, $"only {snapshot.Entities.Count} entities");
        Assert.NotNull(snapshot.Player);
        Assert.StartsWith("Metadata/Characters/", snapshot.Player!.Path, StringComparison.Ordinal);
        Assert.Contains(snapshot.Entities, e => e.Kind == PoEformance.Game.World.EntityKind.Monster);

        // Positions must be real world coordinates spread over the area, not a cluster of
        // zeroes from a failed read.
        Assert.True(snapshot.Entities.Max(e => e.WorldX) - snapshot.Entities.Min(e => e.WorldX) > 500);
    }
}
