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
        // W2SMatrix: 0x1A8 -> 0x1A0, and briefly 0x11C in 2026-08 before the scene test
        // disproved it. = CameraStructure(0x98) + 0x108, per GameHelper2 and the AHK tool.
        Assert.Equal(0x1A0, schema.Structs["WorldData"].OffsetOf("W2SMatrix"));
        Assert.Equal(0x5A0, schema.Structs["AreaInstance"].OffsetOf("PlayerInfo"));
        Assert.Equal(0x6E0, schema.Structs["AreaInstance"].OffsetOf("AwakeEntities"));
        Assert.Equal(0x8C0, schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata"));
        Assert.Equal(0x8B0, schema.Structs["Actor"].OffsetOf("AnimationId"));
        Assert.Equal(0x69, schema.Structs["Targetable"].OffsetOf("IsTargetable"));
        Assert.Equal(0x21E0, schema.Structs["ServerDataOffsets"].OffsetOf("League"));
        Assert.Equal(0x10, schema.Structs["GameState"].Constants["StateEntrySize"]);
        Assert.Equal(4, schema.Structs["GameState"].Constants["InGameStateIndex"]);

        // The drift-alarm invariants that motivated the whole schema design.
        Assert.IsType<Invariant.Range>(schema.Structs["Actor"].Field("AnimationId")!.Invariant);

        // The matrix deliberately has NONE. A unit-vector check there once accepted an
        // unrelated block of unit rows and rejected the real matrix, so verification moved
        // to MatrixHunt, which scores candidates against the live scene. Pinned so nobody
        // reintroduces a structural check that cannot tell a decoy from the real thing.
        Assert.Null(schema.Structs["WorldData"].Field("W2SMatrix")!.Invariant);
    }

    [Fact]
    public void EveryFieldThatNamesAnotherStructFindsIt()
    {
        // A typo in a "struct" key does not fail, it just quietly stops unfolding - the one
        // failure mode of this whole mechanism that nobody would notice from the screen.
        OffsetSchema schema = SchemaJson.Load(SchemaPath);

        foreach (StructDef layout in schema.Structs.Values)
        {
            foreach (FieldDef field in layout.Fields)
            {
                if (field.Target is null)
                {
                    continue;
                }

                Assert.True(
                    schema.Structs.TryGetValue(field.Target, out StructDef? target),
                    $"{layout.Name}.{field.Name} names struct '{field.Target}', which does not exist here.");
                Assert.True(
                    target!.Fields.Count > 0,
                    $"{layout.Name}.{field.Name} names '{field.Target}', which has no fields to show.");
            }
        }

        // The case that asked for the mechanism, pinned: a vital is not behind that pointer,
        // it IS that pointer's eight bytes, and reading it as an address was the wrong answer
        // that still looked like a number.
        FieldDef health = schema.Structs["Life"].Field("Health")!;
        Assert.Equal("Vital", health.Target);
        Assert.True(health.Inline);
    }

    [Fact]
    public void InventoriesRecordsInlineStorageRatherThanAHeapVector()
    {
        OffsetSchema schema = SchemaJson.Load(SchemaPath);

        // The name has to stay exactly the component's, or the entity browser stops
        // finding it and the component goes back to reading as undescribed.
        StructDef inventories = schema.Structs["Inventories"];

        Assert.Equal(0x20, inventories.OffsetOf("EntriesVec"));
        Assert.Equal(0x30, inventories.OffsetOf("EntriesCapacityEnd"));
        Assert.Equal(0x150, inventories.Constants["SubObjectStride"]);

        // The numbers check each other, which is the only reason to pin them: the
        // capacity end observed live sat at +0xB8, and that is exactly the buffer at
        // +0x38 plus sixteen 8-byte slots. Lose either constant and this stops adding up.
        Assert.Equal(0xB8L, inventories.Constants["InlineBuffer"] + (8 * inventories.Constants["InlineSlots"]));
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

    [Fact]
    public void DuplicateStructName_FailsLoud()
    {
        // A second definition of the same name silently replaces the first when JSON is
        // deserialised into a dictionary - taking any fields or constants only the first
        // declared with it. That really happened here, and only a test three layers away
        // caught it, so the loader now refuses it outright.
        const string json = """
        {
          "version": 1,
          "structs": {
            "Thing": { "fields": { "A": { "offset": "0x10", "type": "i32" } }, "consts": { "K": "1" } },
            "Thing": { "fields": { "A": { "offset": "0x10", "type": "i32" } } }
          }
        }
        """;

        var error = Assert.Throws<InvalidDataException>(
            () => SchemaJson.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))));

        Assert.Contains("Thing", error.Message, StringComparison.Ordinal);
        Assert.Contains("twice", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateStaticName_FailsLoud()
    {
        const string json = """
        {
          "version": 1,
          "statics": {
            "Anchor": { "pattern": "48 8B ^ ?? ?? ?? ??" },
            "Anchor": { "pattern": "48 8B ^ ?? ?? ?? ??" }
          }
        }
        """;

        Assert.Throws<InvalidDataException>(
            () => SchemaJson.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))));
    }
}
