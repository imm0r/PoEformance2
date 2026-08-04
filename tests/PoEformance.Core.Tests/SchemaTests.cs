using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

public class SchemaTests
{
    /// <summary>The shipped schema file, relative to the repo root.</summary>
    private static string SchemaPath
    {
        get
        {
            // Walk up from the test bin directory to the repo root.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "schema", "poe2.offsets.json");
        }
    }

    [Fact]
    public void ShippedSchema_LoadsAndCarriesTheKnownAnchors()
    {
        OffsetSchema schema = SchemaJson.Load(SchemaPath);

        // The six pattern anchors the AHK tool scans for.
        Assert.Equal(6, schema.Statics.Count);
        Assert.Contains("GameStates", schema.Statics.Keys);
        Assert.Contains("GameCullSize", schema.Statics.Keys);

        // Spot-check offsets against current ground truth: the AHK-tool port plus the
        // owner-verified 2026-08 +0x08 AreaInstance wave (PlayerInfo 0x598 -> 0x5A0,
        // AwakeEntities 0x6D8 -> 0x6E0, Terrain 0x8B8 -> 0x8C0).
        Assert.Equal(0x290, schema.Structs["InGameState"].OffsetOf("AreaInstanceData"));
        // W2SMatrix: 0x1A8 -> 0x1A0 -> 0x11C, located 2026-08 by the drift scanner.
        Assert.Equal(0x11C, schema.Structs["WorldData"].OffsetOf("W2SMatrix"));
        Assert.Equal(0x5A0, schema.Structs["AreaInstance"].OffsetOf("PlayerInfo"));
        Assert.Equal(0x6E0, schema.Structs["AreaInstance"].OffsetOf("AwakeEntities"));
        Assert.Equal(0x8C0, schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata"));
        Assert.Equal(0x8B0, schema.Structs["Actor"].OffsetOf("AnimationId"));
        Assert.Equal(0x69, schema.Structs["Targetable"].OffsetOf("IsTargetable"));
        Assert.Equal(0x21E0, schema.Structs["ServerDataStructure"].OffsetOf("League"));
        Assert.Equal(0x10, schema.Structs["GameState"].Constants["StateEntrySize"]);
        Assert.Equal(4, schema.Structs["GameState"].Constants["InGameStateIndex"]);

        // The drift-alarm invariants that motivated the whole schema design.
        // The matrix invariant pins the DIRECTION, not just the length: PoE2's camera angle is
        // fixed, so a mere unit-length check would let a wrong offset pass as green.
        var matrixInvariant = Assert.IsType<Invariant.UnitVector3>(schema.Structs["WorldData"].Field("W2SMatrix")!.Invariant);
        Assert.NotNull(matrixInvariant.Expect);
        Assert.Equal(0.751464, matrixInvariant.Expect![2], precision: 5);
        Assert.IsType<Invariant.Range>(schema.Structs["Actor"].Field("AnimationId")!.Invariant);
    }

    [Fact]
    public void MissingField_ThrowsNamingStructAndField()
    {
        OffsetSchema schema = SchemaJson.Load(SchemaPath);
        var ex = Assert.Throws<KeyNotFoundException>(() => schema.Structs["Actor"].OffsetOf("DoesNotExist"));
        Assert.Contains("Actor", ex.Message);
        Assert.Contains("DoesNotExist", ex.Message);
    }

    [Fact]
    public void BadSchema_FailsLoudWithLocation()
    {
        static Stream Json(string s) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s));

        // Unknown type name.
        var ex1 = Assert.Throws<InvalidDataException>(() => SchemaJson.Load(Json(
            """{"version":1,"structs":{"S":{"fields":{"F":{"offset":"0x10","type":"quux"}}}}}""")));
        Assert.Contains("S.F", ex1.Message);

        // Malformed offset.
        var ex2 = Assert.Throws<InvalidDataException>(() => SchemaJson.Load(Json(
            """{"version":1,"structs":{"S":{"fields":{"F":{"offset":"0xZZ","type":"u32"}}}}}""")));
        Assert.Contains("0xZZ", ex2.Message);

        // Pattern without the RIP marker.
        var ex3 = Assert.Throws<InvalidDataException>(() => SchemaJson.Load(Json(
            """{"version":1,"statics":{"A":{"pattern":"48 8B 05"}}}""")));
        Assert.Contains("'^'", ex3.Message);
    }

    [Fact]
    public void Fields_AreSortedByOffset()
    {
        OffsetSchema schema = SchemaJson.Load(SchemaPath);
        foreach (StructDef s in schema.Structs.Values)
        {
            for (int i = 1; i < s.Fields.Count; i++)
            {
                Assert.True(s.Fields[i - 1].Offset <= s.Fields[i].Offset,
                    $"{s.Name}: fields not sorted at {s.Fields[i].Name}");
            }
        }
    }
}
