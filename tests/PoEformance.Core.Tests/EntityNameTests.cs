using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The name the game shows for an entity, off the Render component.
/// </summary>
/// <remarks>
/// Found live in the dissector on an Affliction NPC: a pointer with size 11 one qword on -
/// exactly the length of "Elder Madox" - and capacity 15 after that. A std::wstring, and the
/// layout the reader already expects.
///
/// The ADDRESS took two goes. The dissector numbers its rows from wherever the view starts,
/// not from the component, so reading a row number as an offset put the field 0xC0 too low -
/// and it landed on the offset GameHelper2 has commented out for the same field, which made a
/// wrong answer look confirmed. What settled it was the view's own contents: a row holding the
/// world position, which is at a known offset, says where the view began.
///
/// WHAT THESE DO NOT CHECK IS THE OFFSET. The fixture puts its string wherever the schema says
/// the field is, so it follows the schema anywhere: moving Name to 0xA8 leaves every test here
/// green. That holds for every offset in this project, and it is why the schema records why
/// each one is what it is. What is checked here is the reading AROUND it - the short-string
/// case, the absent component, and which of two names a label uses - which is where the
/// mistakes that survive a live look actually get made.
/// </remarks>
public class EntityNameTests
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

    [Fact]
    public void ANameIsReadFromWhereTheSchemaSaysItIs()
    {
        const ulong render = 0x10_0000;
        const ulong heap = 0x20_0000;

        OffsetSchema schema = LoadSchema();
        int at = schema.Structs["Render"].OffsetOf("Name");

        var fake = new FakeMemoryReader();
        fake.PlaceStdWString(render + (ulong)at, "Elder Madox", heap);

        Assert.Equal("Elder Madox", new RenderReader(fake, schema).ReadName(render));
    }

    [Fact]
    public void ANDMostThingsHaveNoneAtAll()
    {
        // Scenery, effects and ground items read empty, which is the ordinary case rather
        // than a failure - the whole map is made of them.
        const ulong render = 0x10_0000;

        OffsetSchema schema = LoadSchema();
        int at = schema.Structs["Render"].OffsetOf("Name");

        var fake = new FakeMemoryReader();
        fake.PlaceStdWString(render + (ulong)at, string.Empty, 0x20_0000);

        Assert.Equal(string.Empty, new RenderReader(fake, schema).ReadName(render));
    }

    [Fact]
    public void ANDAnEntityWithNoRenderComponentIsNotAskedAboutTwice()
    {
        // Zero is what "this entity has no Render" looks like from the caller. Asking memory
        // about it would be a read guaranteed to fail, on the tick of every entity that has
        // no position.
        OffsetSchema schema = LoadSchema();
        Assert.Equal(string.Empty, new RenderReader(new FakeMemoryReader(), schema).ReadName(0));
    }

    [Fact]
    public void THEGamesNameWinsOverTheFileName()
    {
        // "Zar Wali, the Bone Tyrant" is what the game says; "MonsterBossZarWali01" is what
        // the file is called, and only one of those is worth putting on a map.
        var named = new WorldEntity(
            1, 0x100, "Metadata/Monsters/Boss/MonsterBossZarWali01", EntityKind.Monster,
            0f, 0f, 0f, Name: "Zar Wali, the Bone Tyrant");

        Assert.Equal("Zar Wali, the Bone Tyrant", named.ShortName);
        Assert.Equal("MonsterBossZarWali01", named.FileName);
    }

    [Fact]
    public void ANDTheFileNameIsStillThereWhenThereIsNoName()
    {
        // The fallback carries most of the map. A label that vanished for everything unnamed
        // would be a worse answer than an ugly one.
        var plain = new WorldEntity(
            1, 0x100, "Metadata/Terrain/Rocks/Rock_01", EntityKind.Terrain, 0f, 0f, 0f);

        Assert.Equal("Rock_01", plain.ShortName);
        Assert.Equal("Rock_01", plain.FileName);
    }

    [Fact]
    public void ANDAPlaceIsCalledWhatTheGameCallsIt()
    {
        // The points-of-interest list reads from the same name, so a marked place stops being
        // "Brazier Lever 03" the moment the game has something better to say.
        var place = new WorldEntity(
            1, 0x100, "Metadata/Terrain/Objects/BrazierLever03", EntityKind.Terrain, 0f, 0f, 0f,
            Poi: PoiKind.Quest, Name: "Ancient Brazier");

        Assert.Equal("Ancient Brazier", place.PoiName);
    }
}
