using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The AreaInstance tail hunt: finds the player slot, the entity maps and the terrain struct
/// by their shape, wherever a patch has pushed them, and names the delta.
/// </summary>
/// <remarks>
/// The synthetic layouts here are the schema's own offsets plus a chosen delta, so the
/// tests keep working when the schema is updated after a real wave - what they assert is
/// that the hunt reports the delta, not any particular number.
/// </remarks>
public class AreaInstanceHuntTests
{
    private const ulong ModuleBase = 0x140000000;
    private const ulong AreaInstanceAddr = 0x40_0000;

    /// <summary>
    /// Lays out an AreaInstance whose tail sits <paramref name="delta"/> bytes past the
    /// schema's offsets, with every fingerprint the hunt looks for.
    /// </summary>
    private static FakeMemoryReader BuildArea(OffsetSchema schema, int delta, ulong at = AreaInstanceAddr, bool environmentsMoved = true)
    {
        var fake = new FakeMemoryReader { ModuleBase = ModuleBase, ModuleSize = 0x10000 };
        fake.Place(at + AreaInstanceHunt.WindowStart, new byte[AreaInstanceHunt.WindowEnd - AreaInstanceHunt.WindowStart]);

        StructDef area = schema.Structs["AreaInstance"];
        StructDef local = schema.Structs["LocalPlayerStruct"];
        StructDef map = schema.Structs["StdMap"];
        StructDef node = schema.Structs["StdMapNode"];
        StructDef terrain = schema.Structs["TerrainMetadata"];
        int entityDetails = schema.Structs["Entity"].OffsetOf("EntityDetailsPtr");
        int path = schema.Structs["EntityDetails"].OffsetOf("Path");

        // The player slot: ServerData pointer, and 0x20 later the player entity.
        ulong player = at + (ulong)(area.OffsetOf("PlayerInfo") + delta);
        fake.Place(player + (ulong)local.OffsetOf("ServerDataPtr"), 0x55_0000UL);
        fake.Place(player + (ulong)local.OffsetOf("LocalPlayerPtr"), 0x60_0000UL);
        fake.Place(0x60_0000UL + (ulong)entityDetails, 0x70_0000UL);
        fake.PlaceStdWString(0x70_0000UL + (ulong)path, "Metadata/Characters/Int/IntFour", 0x71_0000);

        // The awake map: a nil sentinel whose parent is a real node holding a monster.
        ulong awake = at + (ulong)(area.OffsetOf("AwakeEntities") + delta);
        fake.Place(awake + (ulong)map.OffsetOf("Head"), 0x80_0000UL);
        fake.Place(awake + (ulong)map.OffsetOf("Size"), 42L);
        fake.Place(0x80_0000UL, new byte[0x30]);
        fake.Place<byte>(0x80_0000UL + (ulong)node.OffsetOf("IsNil"), 1);
        fake.Place(0x80_0000UL + (ulong)node.OffsetOf("Parent"), 0x81_0000UL);
        fake.Place(0x81_0000UL, new byte[0x30]);
        fake.Place(0x81_0000UL + (ulong)node.OffsetOf("ValueEntityPtr"), 0x82_0000UL);
        fake.Place(0x82_0000UL + (ulong)entityDetails, 0x83_0000UL);
        fake.PlaceStdWString(0x83_0000UL + (ulong)path, "Metadata/Monsters/Skeletons/SkeletonSoldier", 0x84_0000);

        // The sleeping map: empty, so only its sentinel says what it is.
        ulong sleeping = at + (ulong)(area.OffsetOf("SleepingEntities") + delta);
        fake.Place(sleeping + (ulong)map.OffsetOf("Head"), 0x85_0000UL);
        fake.Place(sleeping + (ulong)map.OffsetOf("Size"), 0L);
        fake.Place(0x85_0000UL, new byte[0x30]);
        fake.Place<byte>(0x85_0000UL + (ulong)node.OffsetOf("IsNil"), 1);

        // The terrain struct: vtable, back-pointer to the owner, tile counts and their twins.
        ulong terrainAt = at + (ulong)(area.OffsetOf("TerrainMetadata") + delta);
        fake.Place(terrainAt, ModuleBase + 0x1234);
        fake.Place(terrainAt + 8, at);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesX"), 39L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesY"), 45L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesPlusOneX"), 40L);
        fake.Place(terrainAt + (ulong)terrain.OffsetOf("TotalTilesPlusOneX") + 8, 46L);

        // Environments: a vector of three int keys, wherever the test wants it.
        ulong environments = at + (ulong)(area.OffsetOf("Environments") + (environmentsMoved ? delta : 0));
        fake.Place(environments, 0x90_0000UL);
        fake.Place(environments + 8, 0x90_0000UL + 12);
        fake.Place(environments + 16, 0x90_0000UL + 16);

        return fake;
    }

    [Fact]
    public void FindsEveryTailField_AndTheDeltaTheyAgreeOn()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        FakeMemoryReader fake = BuildArea(schema, delta: 0x10);
        StructDef area = schema.Structs["AreaInstance"];

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(fake, schema, AreaInstanceAddr);

        Assert.Equal(0x10, result.Consensus);
        foreach (string field in new[] { "PlayerInfo", "AwakeEntities", "SleepingEntities", "TerrainMetadata", "Environments" })
        {
            TailCandidate found = Assert.IsType<TailCandidate>(result.Candidate(field));
            Assert.Equal(area.OffsetOf(field), found.SchemaOffset);
            Assert.Equal(area.OffsetOf(field) + 0x10, found.FoundOffset);
            Assert.Equal(0x10, found.Delta);
        }

        Assert.Contains("Metadata/Characters", result.Candidate("PlayerInfo")!.Evidence);
        Assert.Contains("42 entities", result.Candidate("AwakeEntities")!.Evidence);
        Assert.Contains("39 x 45", result.Candidate("TerrainMetadata")!.Evidence);
        Assert.Contains("plus-one pair agrees", result.Candidate("TerrainMetadata")!.Evidence);
    }

    [Fact]
    public void AnUnmovedTail_ReportsDeltaZero()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        FakeMemoryReader fake = BuildArea(schema, delta: 0);

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(fake, schema, AreaInstanceAddr);

        Assert.Equal(0, result.Consensus);
        Assert.All(result.Found, c => Assert.Equal(0, c.Delta));
    }

    [Fact]
    public void Environments_IsJudgedInPlaceToo_WhenTheInsertionSitsAboveIt()
    {
        // The 2026-08 wave inserted its field BELOW ~0x5A0 - maybe below Environments, maybe
        // not, which is exactly why that row is still marked unverified. The hunt says
        // which it sees rather than assuming the tail's delta applies.
        OffsetSchema schema = RealSessionTests.Schema();
        FakeMemoryReader fake = BuildArea(schema, delta: 0x10, environmentsMoved: false);

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(fake, schema, AreaInstanceAddr);

        Assert.Equal(0x10, result.Consensus);
        TailCandidate environments = Assert.IsType<TailCandidate>(result.Candidate("Environments"));
        Assert.Equal(0, environments.Delta);
        Assert.Contains("did not move", environments.Evidence);
    }

    [Fact]
    public void AMissingFingerprint_MeansNoConsensus_NotAGuess()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        FakeMemoryReader fake = BuildArea(schema, delta: 0x10);
        StructDef area = schema.Structs["AreaInstance"];

        // Break the terrain back-pointer: the slot now points at some other struct.
        fake.Place(AreaInstanceAddr + (ulong)(area.OffsetOf("TerrainMetadata") + 0x10 + 8), 0x99_0000UL);

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(fake, schema, AreaInstanceAddr);

        Assert.Null(result.Consensus);
        Assert.Null(result.Candidate("TerrainMetadata"));
        Assert.NotNull(result.Candidate("PlayerInfo"));
        Assert.NotNull(result.Candidate("AwakeEntities"));
    }

    [Fact]
    public void EmptyMemory_FindsNothing()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        var fake = new FakeMemoryReader();
        fake.Place(AreaInstanceAddr + AreaInstanceHunt.WindowStart, new byte[AreaInstanceHunt.WindowEnd - AreaInstanceHunt.WindowStart]);

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(fake, schema, AreaInstanceAddr);

        Assert.Empty(result.Found);
        Assert.Null(result.Consensus);
        Assert.False(AreaInstanceHunt.LooksLikeAreaInstance(fake, schema, AreaInstanceAddr));
    }

    [Fact]
    public void ADriftedParentPointer_IsFoundByTheBackPointerProbe()
    {
        // The other kind of drift: InGameState.AreaInstanceData moved by 8, so the schema's
        // slot leads to a struct with none of the fingerprints while the neighbour leads to
        // the real one - which announces itself by pointing back at its own address.
        OffsetSchema schema = RealSessionTests.Schema();
        const ulong inGameState = 0x30_0000;
        const ulong stale = 0x41_0000;
        int areaOffset = schema.Structs["InGameState"].OffsetOf("AreaInstanceData");

        FakeMemoryReader fake = BuildArea(schema, delta: 0);
        fake.Place(stale + AreaInstanceHunt.WindowStart, new byte[AreaInstanceHunt.WindowEnd - AreaInstanceHunt.WindowStart]);
        fake.Place(inGameState + (ulong)areaOffset, stale);
        fake.Place(inGameState + (ulong)areaOffset + 8, AreaInstanceAddr);

        List<PointerCandidate> found = PointerDriftScan.Scan(
            fake, inGameState, areaOffset, radius: 0x40,
            probe: candidate => AreaInstanceHunt.LooksLikeAreaInstance(fake, schema, candidate));

        PointerCandidate hit = Assert.Single(found);
        Assert.Equal(8, hit.Delta);
        Assert.Equal(AreaInstanceAddr, hit.Target);
    }

    [Fact]
    public void RealMemory_PlayerSlotIsFoundWhereTheSchemaSaysItIs()
    {
        // Against a recording: only the reads the session made exist, so the sweep degrades
        // to single slots and finds the tail where that session read it. The recording
        // predates the hunt, so the map sentinel and terrain twins may be absent - the
        // player slot is the one every session reads, and it must land on the schema.
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.NotEqual(0UL, chain.AreaInstance);

        AreaInstanceHuntResult result = AreaInstanceHunt.Run(replay, schema, chain.AreaInstance);

        TailCandidate player = Assert.IsType<TailCandidate>(result.Candidate("PlayerInfo"));
        Assert.Equal(0, player.Delta);
        Assert.All(result.Found.Where(c => c.Field != "Environments"), c => Assert.Equal(0, c.Delta));
    }
}
