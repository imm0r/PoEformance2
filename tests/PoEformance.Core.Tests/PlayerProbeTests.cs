using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The player probe end to end over a synthetic in-game world: static -> game state ->
/// area -> inline player -> Render component -> project through the matrix. Proves the
/// whole chain wires together and that the "player projects to centre" verdict works,
/// synthetically; the real recording then confirms it against actual memory.
/// </summary>
public class PlayerProbeTests
{
    private const ulong GameStatesStatic = 0x1_000000;
    private const ulong GameStateAddr = 0x2_000000;
    private const ulong InGameStateAddr = 0x3_000000;
    private const ulong AreaInstanceAddr = 0x4_000000;
    private const ulong WorldDataAddr = 0x5_000000;
    private const ulong PlayerEntityAddr = 0x6_000000;
    private const ulong PlayerDetailsAddr = 0x7_000000;
    private const ulong PlayerLookupAddr = 0x8_000000;
    private const ulong PlayerVecAddr = 0x9_000000;
    private const ulong BucketDataAddr = 0xA_000000;
    private const ulong RenderComponentAddr = 0xB_000000;
    private const ulong PathDataAddr = 0xC_000000;
    private const ulong NamePtrAddr = 0xD_000000;

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>
    /// Builds a minimal but faithful in-game world. The player sits at world origin and the
    /// matrix is identity, so a correct projection puts it dead centre (NDC 0).
    /// </summary>
    private static FakeMemoryReader BuildInGameWorld(OffsetSchema schema, float playerX, float playerY, float playerZ, float[] matrix)
    {
        var fake = new FakeMemoryReader();

        StructDef gs = schema.Structs["GameState"];
        StructDef igs = schema.Structs["InGameState"];
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        StructDef wd = schema.Structs["WorldData"];
        StructDef eDef = schema.Structs["Entity"];
        StructDef dDef = schema.Structs["EntityDetails"];
        StructDef lDef = schema.Structs["ComponentLookup"];
        StructDef bDef = schema.Structs["StdBucket"];
        StructDef entryDef = schema.Structs["ComponentLookupEntry"];
        StructDef render = schema.Structs["Render"];
        int entrySize = (int)entryDef.Constants["Size"];

        // static -> gameState -> inGameState (well-known index).
        fake.Place(GameStatesStatic, GameStateAddr);
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]), InGameStateAddr);

        // inGameState -> area + world data.
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), AreaInstanceAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("WorldData"), WorldDataAddr);

        // area -> inline player (LocalPlayerPtr at +0x20 of the PlayerInfo field address).
        ulong playerBase = AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo");
        fake.Place(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);

        // world data -> the matrix.
        ulong matrixAddr = WorldDataAddr + (ulong)wd.OffsetOf("W2SMatrix");
        for (int i = 0; i < 16; i++)
        {
            fake.Place<float>(matrixAddr + (ulong)(i * 4), matrix[i]);
        }

        // player entity: details, one Render component via the lookup.
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("EntityDetailsPtr"), PlayerDetailsAddr);
        fake.Place<uint>(PlayerEntityAddr + (ulong)eDef.OffsetOf("Id"), 1);
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("ComponentsVec"), PlayerVecAddr);
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("ComponentsVecLast"), PlayerVecAddr + 8);
        fake.Place(PlayerVecAddr, RenderComponentAddr);

        fake.PlaceStdWString(PlayerDetailsAddr + (ulong)dDef.OffsetOf("Path"), "Metadata/Characters/Int/IntFour", PathDataAddr);
        fake.Place(PlayerDetailsAddr + (ulong)dDef.OffsetOf("ComponentLookupPtr"), PlayerLookupAddr);

        ulong bucketBase = PlayerLookupAddr + (ulong)lDef.OffsetOf("Bucket");
        fake.Place<int>(bucketBase + (ulong)bDef.OffsetOf("Capacity"), 4);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("Data"), BucketDataAddr);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("DataLast"), BucketDataAddr + (ulong)entrySize);
        fake.Place(BucketDataAddr + (ulong)entryDef.OffsetOf("NamePtr"), NamePtrAddr);
        fake.Place<long>(BucketDataAddr + (ulong)entryDef.OffsetOf("Index"), 0);
        fake.PlaceUtf8(NamePtrAddr, "Render");

        // Render component: the world position.
        fake.Place<float>(RenderComponentAddr + (ulong)render.OffsetOf("CurrentWorldPosition"), playerX);
        fake.Place<float>(RenderComponentAddr + (ulong)render.OffsetOf("CurrentWorldPosition") + 4, playerY);
        fake.Place<float>(RenderComponentAddr + (ulong)render.OffsetOf("CurrentWorldPosition") + 8, playerZ);

        return fake;
    }

    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    [Fact]
    public void Probe_ReadsPlayerComponentsAndProjectsToCentre()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildInGameWorld(schema, 0, 0, 0, Identity);

        var writer = new StringWriter();
        PlayerProbeResult r = new PlayerProbe(fake, schema).ProbeAndReport(GameStatesStatic, writer);

        Assert.True(r.InGame);
        Assert.Equal("Metadata/Characters/Int/IntFour", r.Path);
        Assert.True(r.HasRender);
        Assert.True(r.Projected);
        Assert.True(r.ProjectsToCentre, writer.ToString());
        Assert.Contains("MATRIX PROVEN", writer.ToString());
    }

    [Fact]
    public void Probe_ReportsOffCentre_WhenTheMatrixIsWrong()
    {
        // A shifted translation row moves the player far from centre - the failure the
        // real proof guards against.
        OffsetSchema schema = LoadSchema();
        float[] wrong = (float[])Identity.Clone();
        wrong[12] = 0.9f; // column-major x-translation -> shifts the player off to the side
        FakeMemoryReader fake = BuildInGameWorld(schema, 0, 0, 0, wrong);

        PlayerProbeResult r = new PlayerProbe(fake, schema).Probe(GameStatesStatic);

        Assert.True(r.Projected);
        Assert.False(r.ProjectsToCentre);
    }

    [Fact]
    public void Probe_ReportsNotInGame_WhenTheChainIsBroken()
    {
        OffsetSchema schema = LoadSchema();
        var empty = new FakeMemoryReader(); // nothing placed: static resolves to nothing
        PlayerProbeResult r = new PlayerProbe(empty, schema).Probe(GameStatesStatic);
        Assert.False(r.InGame);
    }
}
