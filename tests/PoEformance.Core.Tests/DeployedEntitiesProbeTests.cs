using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The deployed-entities probe over a synthetic world: chain -> player -> Actor -> the
/// candidate vectors, cross-referenced against a synthetic entity map.
/// </summary>
/// <remarks>
/// These tests are about the ONE distinction the probe exists to make. An empty vector, a
/// vector at the wrong offset and a vector read at the wrong stride all produce "no deployed
/// entities", and treating those three as one is what let Actor.DeployedEntities sit at a
/// stale offset while its schema comment declared the symptom harmless. So there is a test
/// for each, and the verdicts have to differ.
/// </remarks>
public class DeployedEntitiesProbeTests
{
    private const ulong GameStatesStatic = 0x1_000000;
    private const ulong GameStateAddr = 0x2_000000;
    private const ulong InGameStateAddr = 0x3_000000;
    private const ulong AreaInstanceAddr = 0x4_000000;
    private const ulong PlayerEntityAddr = 0x6_000000;
    private const ulong PlayerDetailsAddr = 0x7_000000;
    private const ulong PlayerLookupAddr = 0x8_000000;
    private const ulong PlayerVecAddr = 0x9_000000;
    private const ulong BucketDataAddr = 0xA_000000;
    private const ulong ActorAddr = 0xB_000000;
    private const ulong PathDataAddr = 0xC_000000;
    private const ulong NamePtrAddr = 0xD_000000;
    private const ulong EntriesAddr = 0xE_000000;
    private const ulong MapHead = 0xF_000000;
    private const ulong NodeBase = 0x11_000000;

    /// <summary>Ids the synthetic area really contains - what a correct vector must name.</summary>
    private static readonly uint[] LiveIds = [5001, 5002];

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
    /// A world in an area, with a player carrying an Actor component and two entities in
    /// the map. The deployed vector itself is left to each test to place.
    /// </summary>
    private static FakeMemoryReader BuildWorld(OffsetSchema schema)
    {
        var fake = new FakeMemoryReader();

        StructDef gs = schema.Structs["GameState"];
        StructDef igs = schema.Structs["InGameState"];
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        StructDef eDef = schema.Structs["Entity"];
        StructDef dDef = schema.Structs["EntityDetails"];
        StructDef lDef = schema.Structs["ComponentLookup"];
        StructDef bDef = schema.Structs["StdBucket"];
        StructDef entryDef = schema.Structs["ComponentLookupEntry"];
        int entrySize = (int)entryDef.Constants["Size"];

        fake.Place(GameStatesStatic, GameStateAddr);
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("States")
            + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]), InGameStateAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), AreaInstanceAddr);

        ulong playerBase = AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo");
        fake.Place(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);

        // The player entity, carrying exactly one component: Actor.
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("EntityDetailsPtr"), PlayerDetailsAddr);
        fake.Place<uint>(PlayerEntityAddr + (ulong)eDef.OffsetOf("Id"), 1);
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("ComponentsVec"), PlayerVecAddr);
        fake.Place(PlayerEntityAddr + (ulong)eDef.OffsetOf("ComponentsVecLast"), PlayerVecAddr + 8);
        fake.Place(PlayerVecAddr, ActorAddr);

        fake.PlaceStdWString(PlayerDetailsAddr + (ulong)dDef.OffsetOf("Path"), "Metadata/Characters/Int/IntFour", PathDataAddr);
        fake.Place(PlayerDetailsAddr + (ulong)dDef.OffsetOf("ComponentLookupPtr"), PlayerLookupAddr);

        ulong bucketBase = PlayerLookupAddr + (ulong)lDef.OffsetOf("Bucket");
        fake.Place<int>(bucketBase + (ulong)bDef.OffsetOf("Capacity"), 4);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("Data"), BucketDataAddr);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("DataLast"), BucketDataAddr + (ulong)entrySize);
        fake.Place(BucketDataAddr + (ulong)entryDef.OffsetOf("NamePtr"), NamePtrAddr);
        fake.Place<long>(BucketDataAddr + (ulong)entryDef.OffsetOf("Index"), 0);
        fake.PlaceUtf8(NamePtrAddr, "Actor");

        PlaceEntityMap(fake, schema);
        return fake;
    }

    /// <summary>Two entities in the area's map, so a decoded id has something to match.</summary>
    private static void PlaceEntityMap(FakeMemoryReader fake, OffsetSchema schema)
    {
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef m = schema.Structs["StdMap"];
        StructDef n = schema.Structs["StdMapNode"];
        ulong mapStruct = AreaInstanceAddr + (ulong)ai.OffsetOf("AwakeEntities");

        fake.Place(mapStruct + (ulong)m.OffsetOf("Head"), MapHead);
        fake.Place<long>(mapStruct + (ulong)m.OffsetOf("Size"), LiveIds.Length);
        fake.Place(MapHead + (ulong)n.OffsetOf("Parent"), NodeBase);

        for (int i = 0; i < LiveIds.Length; i++)
        {
            ulong node = NodeBase + (ulong)(i * 0x100);

            // The whole node is batch-read in one call, so it must be a contiguous block.
            fake.Place(node, new byte[0x30]);
            fake.Place(node + (ulong)n.OffsetOf("Left"), MapHead);
            fake.Place(node + (ulong)n.OffsetOf("Right"),
                i + 1 < LiveIds.Length ? NodeBase + (ulong)((i + 1) * 0x100) : MapHead);
            fake.Place<uint>(node + (ulong)n.OffsetOf("KeyId"), LiveIds[i]);
            fake.Place(node + (ulong)n.OffsetOf("ValueEntityPtr"), 0x50_0000UL + (ulong)i);
        }
    }

    /// <summary>Writes a deployed-entity vector at <paramref name="offset"/> off the Actor.</summary>
    private static void PlaceVector(
        FakeMemoryReader fake, OffsetSchema schema, int offset, int stride, params uint[] ids)
    {
        StructDef entry = schema.Structs["DeployedEntity"];
        fake.Place(ActorAddr + (ulong)offset, EntriesAddr);
        fake.Place(ActorAddr + (ulong)offset + 8, EntriesAddr + (ulong)(ids.Length * stride));

        for (int i = 0; i < ids.Length; i++)
        {
            ulong at = EntriesAddr + (ulong)(i * stride);
            fake.Place(at, new byte[stride]);
            fake.Place<uint>(at + (ulong)entry.OffsetOf("EntityId"), ids[i]);
            fake.Place<int>(at + (ulong)entry.OffsetOf("ActiveSkillsDatId"), 1234 + i);
            fake.Place<int>(at + (ulong)entry.OffsetOf("DeployedObjectType"), 2);
            fake.Place<int>(at + (ulong)entry.OffsetOf("Counter"), 7);
        }
    }

    /// <summary>Zeroes a candidate, which is what an empty std::vector looks like.</summary>
    private static void PlaceEmpty(FakeMemoryReader fake, int offset)
        => fake.Place(ActorAddr + (ulong)offset, new byte[16]);

    private static int SchemaOffset(OffsetSchema schema)
        => schema.Structs["Actor"].OffsetOf("DeployedEntities");

    [Fact]
    public void Confirms_TheSchemaOffset_WhenItsEntriesNameRealEntities()
    {
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildWorld(schema);
        int at = SchemaOffset(schema);
        int stride = (int)schema.Structs["DeployedEntity"].Constants["Size"];

        PlaceVector(fake, schema, at, stride, LiveIds);
        PlaceEmpty(fake, at - 0x10);
        PlaceEmpty(fake, at + 0x10);

        var writer = new StringWriter();
        DeployedProbeResult r = new DeployedEntitiesProbe(fake, schema).Report(GameStatesStatic, writer);

        Assert.True(r.InGame);
        Assert.False(r.Inconclusive);
        DeployedReading winner = Assert.NotNull(r.Winner);
        Assert.Equal(at, winner.Offset);
        Assert.Equal(2, winner.Matched);
        Assert.Contains("CONFIRMED", writer.ToString());
    }

    [Fact]
    public void ReportsDrift_WhenTheVectorSitsAtANeighbour()
    {
        // The failure this probe was written for: the schema offset reads empty forever
        // while the real vector sits 0x10 away. Nothing about the empty reading says so -
        // only the neighbour's decoded ids do.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildWorld(schema);
        int at = SchemaOffset(schema);
        int stride = (int)schema.Structs["DeployedEntity"].Constants["Size"];

        PlaceEmpty(fake, at);
        PlaceVector(fake, schema, at + 0x10, stride, LiveIds);

        var writer = new StringWriter();
        DeployedProbeResult r = new DeployedEntitiesProbe(fake, schema).Report(GameStatesStatic, writer);

        DeployedReading winner = Assert.NotNull(r.Winner);
        Assert.Equal(at + 0x10, winner.Offset);
        Assert.Contains("DRIFT", writer.ToString());
    }

    [Fact]
    public void ReportsInconclusive_WhenNothingIsDeployed()
    {
        // THE READING THAT MUST NOT LOOK LIKE A PASS. Every candidate is empty, so the
        // session proves nothing - which is exactly what the old schema comment called
        // "not a drift".
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildWorld(schema);
        int at = SchemaOffset(schema);

        PlaceEmpty(fake, at);
        PlaceEmpty(fake, at - 0x10);
        PlaceEmpty(fake, at + 0x10);

        var writer = new StringWriter();
        DeployedProbeResult r = new DeployedEntitiesProbe(fake, schema).Report(GameStatesStatic, writer);

        Assert.True(r.Inconclusive);
        Assert.Null(r.Winner);
        Assert.Contains("INCONCLUSIVE", writer.ToString());
        Assert.Contains("NOT evidence", writer.ToString());
    }

    [Fact]
    public void FindsTheLegacyStride_WhenTheElementSizeIsTheThingThatMoved()
    {
        // The second way to read "nothing deployed" off a correct pointer: the element size.
        // Five 0x14 entries are 100 bytes, which does not divide by 0x18, so the schema
        // stride decodes nothing at all - and the probe has to say WHICH of the two failed.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildWorld(schema);
        int at = SchemaOffset(schema);

        PlaceVector(fake, schema, at, 0x14, LiveIds[0], LiveIds[1], LiveIds[0], LiveIds[1], LiveIds[0]);
        PlaceEmpty(fake, at - 0x10);
        PlaceEmpty(fake, at + 0x10);

        DeployedProbeResult r = new DeployedEntitiesProbe(fake, schema).Run(GameStatesStatic);

        DeployedReading winner = Assert.NotNull(r.Winner);
        Assert.Equal(at, winner.Offset);
        Assert.Equal(0x14, winner.Stride);
        Assert.Equal(5, winner.Count);
    }

    [Fact]
    public void ReportsNoData_WhenTheCandidatesWereNeverRecorded()
    {
        // Replaying any session made before this probe existed: the addresses hold nothing,
        // because nothing ever read them. That is emphatically NOT "nothing was deployed" -
        // reporting it as such would manufacture a measurement from a file without one.
        OffsetSchema schema = LoadSchema();
        FakeMemoryReader fake = BuildWorld(schema); // Actor resolves; its tail is unmapped

        var writer = new StringWriter();
        DeployedProbeResult r = new DeployedEntitiesProbe(fake, schema).Report(GameStatesStatic, writer);

        Assert.True(r.NoData);
        Assert.False(r.Inconclusive);
        Assert.Null(r.Winner);
        Assert.Contains("NO DATA", writer.ToString());
        Assert.DoesNotContain("INCONCLUSIVE", writer.ToString());
    }

    [Fact]
    public void ReportsNotInGame_WhenTheChainIsBroken()
    {
        OffsetSchema schema = LoadSchema();
        DeployedProbeResult r = new DeployedEntitiesProbe(new FakeMemoryReader(), schema).Run(GameStatesStatic);
        Assert.False(r.InGame);
    }
}
