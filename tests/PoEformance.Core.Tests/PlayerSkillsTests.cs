using PoEformance.Core.Schema;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>The player's granted-skill table, as a set of skill objects with lasting keys and names.</summary>
public class PlayerSkillsTests
{
    private const ulong Actor = 0x0000_0500_0000_0000;
    private const ulong Spark = 0x0000_0500_2000_0000;
    private const ulong Orb = 0x0000_0500_2001_0000;
    private const ulong Nameless = 0x0000_0500_2002_0000;

    private static OffsetSchema Schema() => RealSessionTests.LiveSchema();

    private static string Hex(int offset) => $"+0x{offset:X}";

    [Fact]
    public void TheTableIsReadAsASet_KeyedByDatRowWhereItReaches()
    {
        // Three skills: two whose dat row is reachable, and so are keyed by it and named, and
        // one whose object leads nowhere, which keeps the object as its key and no name.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(
            fake, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark", "Spark"),
            new SkillTableFixture.Skill(Orb, "orb_of_storms", "Orb of Storms"),
            new SkillTableFixture.Skill(Nameless, string.Empty));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(3, skills.Count);
        Assert.Equal(2, skills.Named);
        Assert.True(skills.Contains(Spark));
        Assert.False(skills.Contains(0x0000_0500_2003_0000));

        Assert.Equal("spark", skills.IdentityOf(Spark).Id);
        Assert.Equal("Spark", skills.IdentityOf(Spark).Name);
        Assert.NotEqual(Spark, skills.KeyOf(Spark));           // the row, not the object
        Assert.NotEqual(skills.KeyOf(Spark), skills.KeyOf(Orb));
        Assert.Equal(Nameless, skills.KeyOf(Nameless));         // the object, for want of a row
        Assert.Equal(0UL, skills.KeyOf(0x1234));

        Assert.Equal(Orb, skills.ByName("Orb of Storms"));
        Assert.Equal(0UL, skills.ByName("Flame Wall"));

        int direct = schema.Structs["ActiveSkillDetails"].OffsetOf("ActiveSkillsDatPtr");
        Assert.Equal($"row at {Hex(direct)}", skills.RouteNote);
    }

    [Fact]
    public void TheRowIsReachedThroughGrantedEffectsWhenTheDirectPointerIsNot()
    {
        // What the game offered in 0.5.5: nothing at the direct field, and the row at the end
        // of the chain both references resolve a name through - at the column dat-schema's
        // widths compute.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(
            fake, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark", "Spark", SkillTableFixture.Route.ThroughGrantedEffects));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(1, skills.Named);
        Assert.Equal("Spark", skills.IdentityOf(Spark).Name);
        Assert.Equal(Spark, skills.ByName("Spark"));

        int perLevel = schema.Structs["ActiveSkillDetails"].OffsetOf("GrantedEffectsPerLevelDatRow");
        int column = schema.Structs["GrantedEffectsDat"].OffsetOf("ActiveSkill");
        Assert.Equal($"via {Hex(perLevel)} then {Hex(column)}", skills.RouteNote);
    }

    [Fact]
    public void TheReferencesColumnIsTriedToo()
    {
        // The two witnesses disagree on the column by eight bytes; whichever reaches a row wins.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(
            fake, schema, Actor,
            new SkillTableFixture.Skill(
                Spark, "spark", "Spark", SkillTableFixture.Route.ThroughGrantedEffectsPerReference));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal("Spark", skills.IdentityOf(Spark).Name);
        int column = (int)schema.Structs["GrantedEffectsDat"].Constants["ActiveSkillPerGameHelper2"];
        Assert.EndsWith($"then {Hex(column)}", skills.RouteNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowIsHuntedForWhenNeitherKnownPlaceHoldsIt()
    {
        // A row's fingerprint - a first field that reaches a plain id - is what a search of
        // the object looks for, so a layout the references have not caught up with still names
        // the skill, and the readout says where it was.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(
            fake, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark", "Spark", SkillTableFixture.Route.Hunted));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(1, skills.Named);
        Assert.Equal("Spark", skills.IdentityOf(Spark).Name);
        Assert.Equal($"hunted: row at {Hex(SkillTableFixture.HuntedAt)}", skills.RouteNote);
    }

    [Fact]
    public void AnIdThatDoesNotLookLikeOneIsRefused()
    {
        // A pointer that happens to reach text is not a row: one character outside a dat id's
        // alphabet is enough to keep the object as the key.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "sp@rk!", "Spark"));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(1, skills.Count);
        Assert.Equal(0, skills.Named);
        Assert.Equal(Spark, skills.KeyOf(Spark));
        Assert.Equal(0UL, skills.ByName("Spark"));
    }

    [Fact]
    public void ReadOnTheClock_AndTheIdentitiesAreKept()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "spark", "Spark"));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);
        int version = skills.Version;

        long before = fake.Reads;
        skills.Refresh(Actor, PlayerSkills.RefreshMs - 1);
        Assert.Equal(before, fake.Reads);                       // within the pause, nothing is read

        skills.Refresh(Actor, PlayerSkills.RefreshMs);
        long reread = fake.Reads - before;

        // The vector's two pointers and the table itself: the identity's hops and its strings
        // were paid the first time and are not paid again.
        Assert.Equal(3, reread);
        Assert.Equal(version, skills.Version);                  // the same set is not a change
        Assert.Equal("Spark", skills.IdentityOf(Spark).Name);
    }

    [Fact]
    public void AnotherActorIsAnotherTable()
    {
        const ulong other = 0x0000_0500_0100_0000;
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "spark", "Spark"));
        SkillTableFixture.Place(fake, schema, other);

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);
        int version = skills.Version;

        skills.Refresh(other, 1);

        Assert.Equal(0, skills.Count);
        Assert.False(skills.Contains(Spark));
        Assert.Equal(0UL, skills.ByName("Spark"));
        Assert.NotEqual(version, skills.Version);

        skills.Refresh(0, 2);
        Assert.Equal(0, skills.Count);
    }
}
