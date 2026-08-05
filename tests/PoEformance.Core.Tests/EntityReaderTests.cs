using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The component-lookup reader, exercised against a synthetic entity built the way the
/// game lays one out: a component pointer vector plus a hash bucket of (name, index)
/// entries. Proving the traversal here means the only thing left to verify in-game is
/// the offsets, which the schema already validates.
/// </summary>
public class EntityReaderTests
{
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
    /// Lays out an entity in a FakeMemoryReader exactly as the schema describes: details,
    /// component vector, hash bucket, and the name strings.
    /// </summary>
    private static (FakeMemoryReader Fake, ulong EntityAddr) BuildEntity(
        OffsetSchema schema,
        string path,
        (string Name, ulong ComponentAddr)[] components)
    {
        const ulong entityAddr = 0x10_0000;
        const ulong detailsAddr = 0x20_0000;
        const ulong lookupAddr = 0x30_0000;
        const ulong vecAddr = 0x40_0000;
        const ulong bucketDataAddr = 0x50_0000;
        const ulong nameHeap = 0x60_0000;

        StructDef eDef = schema.Structs["Entity"];
        StructDef dDef = schema.Structs["EntityDetails"];
        StructDef lDef = schema.Structs["ComponentLookup"];
        StructDef bDef = schema.Structs["StdBucket"];
        StructDef entryDef = schema.Structs["ComponentLookupEntry"];
        int entrySize = (int)entryDef.Constants["Size"];

        var fake = new FakeMemoryReader();
        fake.Place(entityAddr + (ulong)eDef.OffsetOf("EntityDetailsPtr"), detailsAddr);
        fake.Place<uint>(entityAddr + (ulong)eDef.OffsetOf("Id"), 12345);

        // Component pointer vector: one pointer per component, in index order.
        fake.Place(entityAddr + (ulong)eDef.OffsetOf("ComponentsVec"), vecAddr);
        fake.Place(entityAddr + (ulong)eDef.OffsetOf("ComponentsVecLast"), vecAddr + (ulong)(components.Length * 8));
        for (int i = 0; i < components.Length; i++)
        {
            fake.Place(vecAddr + (ulong)(i * 8), components[i].ComponentAddr);
        }

        // Details -> path + component lookup.
        fake.PlaceStdWString(detailsAddr + (ulong)dDef.OffsetOf("Path"), path, 0x70_0000);
        fake.Place(detailsAddr + (ulong)dDef.OffsetOf("ComponentLookupPtr"), lookupAddr);

        // Lookup -> bucket (bucket base is lookup + Bucket offset).
        ulong bucketBase = lookupAddr + (ulong)lDef.OffsetOf("Bucket");
        fake.Place<int>(bucketBase + (ulong)bDef.OffsetOf("Capacity"), components.Length * 2);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("Data"), bucketDataAddr);
        fake.Place(bucketBase + (ulong)bDef.OffsetOf("DataLast"), bucketDataAddr + (ulong)(components.Length * entrySize));

        // Bucket entries: (namePtr, index). Names go on a small heap.
        for (int i = 0; i < components.Length; i++)
        {
            ulong entryAddr = bucketDataAddr + (ulong)(i * entrySize);
            ulong namePtr = nameHeap + (ulong)(i * 0x40);
            fake.Place(entryAddr + (ulong)entryDef.OffsetOf("NamePtr"), namePtr);
            fake.Place<long>(entryAddr + (ulong)entryDef.OffsetOf("Index"), i);
            fake.PlaceUtf8(namePtr, components[i].Name);
        }

        return (fake, entityAddr);
    }

    [Fact]
    public void Read_ResolvesPathAndEveryComponent()
    {
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, ulong entityAddr) = BuildEntity(schema, "Metadata/Monsters/Skeleton/SkeletonMelee",
        [
            ("Positioned", 0x11_0000),
            ("Render", 0x12_0000),
            ("Life", 0x13_0000),
            ("Actor", 0x14_0000),
        ]);

        var reader = new EntityReader(fake, schema);
        Entity? entity = reader.Read(entityAddr);

        Assert.NotNull(entity);
        Assert.Equal(12345u, entity!.Id);
        Assert.Equal("Metadata/Monsters/Skeleton/SkeletonMelee", entity.Path);
        Assert.Equal(4, entity.Components.Count);
        Assert.True(entity.Has("Render"));
        Assert.Equal(0x12_0000UL, entity.Component("Render"));
        Assert.Equal(0x13_0000UL, entity.Component("Life"));
        Assert.False(entity.Has("Chest"));
        Assert.Equal(0UL, entity.Component("Chest"));
    }

    [Fact]
    public void Read_ToleratesEmptyHashSlots()
    {
        // Real buckets carry empty slots with an out-of-range index; those must be skipped,
        // not turned into bogus components.
        OffsetSchema schema = LoadSchema();
        (FakeMemoryReader fake, ulong entityAddr) = BuildEntity(schema, "Metadata/Characters/Int/IntFour",
        [
            ("Render", 0x12_0000),
            ("Life", 0x13_0000),
        ]);

        // Corrupt the first bucket entry's index to an empty-slot marker.
        StructDef bDef = schema.Structs["StdBucket"];
        StructDef lDef = schema.Structs["ComponentLookup"];
        StructDef entryDef = schema.Structs["ComponentLookupEntry"];
        ulong bucketData = fake.ReadPointer(0x30_0000 + (ulong)lDef.OffsetOf("Bucket") + (ulong)bDef.OffsetOf("Data"));
        fake.Place<long>(bucketData + (ulong)entryDef.OffsetOf("Index"), -1);

        var reader = new EntityReader(fake, schema);
        Entity? entity = reader.Read(entityAddr);

        Assert.NotNull(entity);
        Assert.False(entity!.Has("Render")); // its slot was emptied
        Assert.True(entity.Has("Life"));
    }

    [Fact]
    public void Read_ReturnsNullForAnImplausibleEntity()
    {
        OffsetSchema schema = LoadSchema();
        var reader = new EntityReader(new FakeMemoryReader(), schema);
        Assert.Null(reader.Read(0));
        Assert.Null(reader.Read(0x1234)); // below the pointer floor
        Assert.Null(reader.Read(0x50_0000)); // plausible address but no details pointer
    }
}
