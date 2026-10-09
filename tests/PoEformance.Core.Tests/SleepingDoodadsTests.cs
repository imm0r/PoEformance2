using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>One read of the area's entity maps for the rooms' doodads, against a process laid out as the schema says.</summary>
public class SleepingDoodadsTests
{
    private const ulong AreaInstance = 0x40_0000;
    private const ulong Head = 0x48_0000;
    private const ulong Root = 0x48_1000;
    private const ulong Second = 0x48_2000;
    private const ulong Pot = 0x60_0000;
    private const ulong Monster = 0x61_0000;
    private const ulong Render = 0x62_0000;
    private const ulong Animated = 0x63_0000;
    private const string PotPath = "Metadata/Terrain/Vaal/Doodads/VaalPotCluster01";
    private const string PotModel = "Metadata/Terrain/Vaal/Doodads/VaalPotCluster01_Light.ao";

    /// <summary>The sleeping map's entities are walked, and only one whose path a room names is read as far as where it stands and what it loaded - compared without case.</summary>
    [Fact]
    public void THESLEEPINGMapIsWalkedAndOnlyANamedPathIsReadAsFarAsItsPlaceAndModel()
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        FakeMemoryReader fake = Area(schema);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PotPath.ToUpperInvariant() };

        DoodadSurvey survey = SleepingDoodads.Read(fake, schema, AreaInstance, paths);

        Assert.Equal(string.Empty, survey.Why);
        Assert.Equal((2, 2, 0, 2), (survey.SleepingSize, survey.SleepingNodes, survey.AwakeNodes, survey.Named));
        DoodadSighting found = Assert.Single(survey.Found);
        Assert.Equal((7u, PotPath, PotModel, 1250f, 2500f, -115f, true), (found.Id, found.Path, found.Model, found.X, found.Y, found.Z, found.Asleep));
        Assert.True(survey.Milliseconds >= 0d);
    }

    /// <summary>Outside the game, or with no stub to look for, there is no read and the survey says why.</summary>
    [Fact]
    public void NOAreaOrNoStubIsSaidRatherThanRead()
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        FakeMemoryReader fake = Area(schema);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PotPath };

        Assert.Equal("not in an area", SleepingDoodads.Read(fake, schema, 0, paths).Why);
        Assert.Equal("the area's rooms name no doodad", SleepingDoodads.Read(fake, schema, AreaInstance, new HashSet<string>()).Why);
        Assert.Equal(0, fake.Reads);
    }

    /// <summary>A model record's name as a room's line writes it: cut at the '@' and with forward slashes.</summary>
    [Fact]
    public void AMODELRecordsNameIsCutAtTheAtAndSlashedForward()
    {
        Assert.Equal("Metadata/Doodads/Rock.ao", SleepingDoodads.ModelPath("Metadata\\Doodads\\Rock.ao@3"));
        Assert.Equal("Metadata/Doodads/Rock.ao", SleepingDoodads.ModelPath("Metadata/Doodads/Rock.ao"));
    }

    /// <summary>
    /// An area whose sleeping map holds two entities - a pot with Render and Animated components, and a monster - and whose awake map is empty.
    /// </summary>
    private static FakeMemoryReader Area(OffsetSchema schema)
    {
        var fake = new FakeMemoryReader();
        StructDef area = schema.Structs["AreaInstance"];
        StructDef map = schema.Structs["StdMap"];
        StructDef node = schema.Structs["StdMapNode"];

        // THE SLEEPING MAP: a head whose parent is the root, the root's left child the second node, every other link the head.
        ulong sleeping = AreaInstance + (ulong)area.OffsetOf("SleepingEntities");
        fake.Place(sleeping + (ulong)map.OffsetOf("Head"), Head);
        fake.Place(sleeping + (ulong)map.OffsetOf("Size"), 2L);
        fake.Place(Head + (ulong)node.OffsetOf("Parent"), Root);
        fake.Place(Root, Node(node, left: Second, right: Head, id: 7, entity: Pot));
        fake.Place(Second, Node(node, left: Head, right: Head, id: 0x4000_0009, entity: Monster));

        // THE AWAKE MAP: empty - a head of nought.
        fake.Place(AreaInstance + (ulong)area.OffsetOf("AwakeEntities") + (ulong)map.OffsetOf("Head"), 0UL);

        Entity(fake, schema, Pot, 7, PotPath, details: 0x70_0000, text: 0x71_0000, lookup: 0x72_0000, entries: 0x73_0000, names: 0x74_0000, vector: 0x75_0000,
            ("Render", Render), ("Animated", Animated));
        Entity(fake, schema, Monster, 0x4000_0009, "Metadata/Monsters/Vaal/VaalGuardian", details: 0x76_0000, text: 0x77_0000, lookup: 0, entries: 0, names: 0, vector: 0);

        StructDef render = schema.Structs["Render"];
        fake.Place(Render + (ulong)render.OffsetOf("CurrentWorldPosition"), 1250f);
        fake.Place(Render + (ulong)render.OffsetOf("CurrentWorldPosition") + 4, 2500f);
        fake.Place(Render + (ulong)render.OffsetOf("CurrentWorldPosition") + 8, -115f);
        fake.Place(Render + (ulong)render.OffsetOf("TerrainHeight"), -115f);
        fake.Place(Render + (ulong)render.OffsetOf("CharacterModelBounds") + 8, 0f);

        // THE MODEL'S CHAIN: the component's info, the info's file record, the record's name with the '@' the game may append.
        const ulong info = 0x78_0000;
        const ulong record = 0x79_0000;
        fake.Place(Animated + (ulong)schema.Structs["Animated"].OffsetOf("ModelInfoPtr"), info);
        fake.Place(info + (ulong)schema.Structs["AnimatedModelInfo"].OffsetOf("ModelFileRecordPtr"), record);
        fake.PlaceStdWString(record + (ulong)schema.Structs["FileInfoValue"].OffsetOf("Name"), PotModel + "@2", 0x7A_0000);
        return fake;
    }

    /// <summary>A map node's bytes: its links, its key and the entity it holds.</summary>
    private static byte[] Node(StructDef node, ulong left, ulong right, uint id, ulong entity)
    {
        var bytes = new byte[0x30];
        BitConverter.GetBytes(left).CopyTo(bytes, node.OffsetOf("Left"));
        BitConverter.GetBytes(right).CopyTo(bytes, node.OffsetOf("Right"));
        BitConverter.GetBytes(id).CopyTo(bytes, node.OffsetOf("KeyId"));
        BitConverter.GetBytes(entity).CopyTo(bytes, node.OffsetOf("ValueEntityPtr"));
        return bytes;
    }

    /// <summary>An entity with its path and, where any are given, its components reachable through its lookup.</summary>
    private static void Entity(
        FakeMemoryReader fake, OffsetSchema schema, ulong at, uint id, string path,
        ulong details, ulong text, ulong lookup, ulong entries, ulong names, ulong vector, params (string Name, ulong Address)[] components)
    {
        StructDef entity = schema.Structs["Entity"];
        StructDef detail = schema.Structs["EntityDetails"];
        fake.Place(at + (ulong)entity.OffsetOf("EntityDetailsPtr"), details);
        fake.Place(at + (ulong)entity.OffsetOf("Id"), id);
        fake.PlaceStdWString(details + (ulong)detail.OffsetOf("Path"), path, text);
        if (components.Length == 0)
        {
            return;
        }

        StructDef bucket = schema.Structs["StdBucket"];
        StructDef entry = schema.Structs["ComponentLookupEntry"];
        int entrySize = checked((int)entry.Constants["Size"]);
        fake.Place(at + (ulong)entity.OffsetOf("ComponentsVec"), vector);
        fake.Place(at + (ulong)entity.OffsetOf("ComponentsVecLast"), vector + (ulong)(8 * components.Length));
        fake.Place(details + (ulong)detail.OffsetOf("ComponentLookupPtr"), lookup);
        ulong at0 = lookup + (ulong)schema.Structs["ComponentLookup"].OffsetOf("Bucket");
        fake.Place(at0 + (ulong)bucket.OffsetOf("Data"), entries);
        fake.Place(at0 + (ulong)bucket.OffsetOf("DataLast"), entries + (ulong)(entrySize * components.Length));
        fake.Place(at0 + (ulong)bucket.OffsetOf("Capacity"), components.Length);
        fake.Place(entries, new byte[entrySize * components.Length]);
        for (var one = 0; one < components.Length; one++)
        {
            ulong name = names + (ulong)(0x40 * one);
            ulong slot = entries + (ulong)(entrySize * one);
            fake.Place(vector + (ulong)(8 * one), components[one].Address);
            fake.Place(slot + (ulong)entry.OffsetOf("NamePtr"), name);
            fake.Place(slot + (ulong)entry.OffsetOf("Index"), one);
            fake.PlaceUtf8(name, components[one].Name);
        }
    }
}
