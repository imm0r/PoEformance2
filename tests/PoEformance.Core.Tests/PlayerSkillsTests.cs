using PoEformance.Core.Schema;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>The player's granted-skill table, as a set of skill objects with lasting keys.</summary>
public class PlayerSkillsTests
{
    private const ulong Actor = 0x0000_0500_0000_0000;
    private const ulong Spark = 0x0000_0500_2000_0000;
    private const ulong Orb = 0x0000_0500_2001_0000;
    private const ulong Nameless = 0x0000_0500_2002_0000;

    private static OffsetSchema Schema() => RealSessionTests.LiveSchema();

    [Fact]
    public void TheTableIsReadAsASet_KeyedByDatRowWhereItReaches()
    {
        // Three skills: two whose dat row is reachable, and so are keyed by it and named, and
        // one whose row pointer is null, which keeps the object as its key and no name.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(
            fake, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark"),
            new SkillTableFixture.Skill(Orb, "orb_of_storms"),
            new SkillTableFixture.Skill(Nameless, string.Empty));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(3, skills.Count);
        Assert.Equal(2, skills.Named);
        Assert.True(skills.Contains(Spark));
        Assert.False(skills.Contains(0x0000_0500_2003_0000));

        Assert.Equal("spark", skills.IdentityOf(Spark).Id);
        Assert.NotEqual(Spark, skills.KeyOf(Spark));           // the row, not the object
        Assert.NotEqual(skills.KeyOf(Spark), skills.KeyOf(Orb));
        Assert.Equal(Nameless, skills.KeyOf(Nameless));         // the object, for want of a row
        Assert.Equal(0UL, skills.KeyOf(0x1234));
    }

    [Fact]
    public void AnIdThatDoesNotLookLikeOneIsRefused()
    {
        // A pointer that happens to reach text is not a row: one character outside a dat id's
        // alphabet is enough to keep the object as the key.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "sp@rk!"));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);

        Assert.Equal(1, skills.Count);
        Assert.Equal(0, skills.Named);
        Assert.Equal(Spark, skills.KeyOf(Spark));
    }

    [Fact]
    public void ReadOnTheClock_AndTheIdentitiesAreKept()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "spark"));

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);
        int version = skills.Version;

        long before = fake.Reads;
        skills.Refresh(Actor, PlayerSkills.RefreshMs - 1);
        Assert.Equal(before, fake.Reads);                       // within the pause, nothing is read

        skills.Refresh(Actor, PlayerSkills.RefreshMs);
        long reread = fake.Reads - before;

        // The vector's two pointers and the table itself: the identity's two hops and its
        // string were paid the first time and are not paid again.
        Assert.Equal(3, reread);
        Assert.Equal(version, skills.Version);                  // the same set is not a change
        Assert.Equal("spark", skills.IdentityOf(Spark).Id);
    }

    [Fact]
    public void AnotherActorIsAnotherTable()
    {
        const ulong other = 0x0000_0500_0100_0000;
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        SkillTableFixture.Place(fake, schema, Actor, new SkillTableFixture.Skill(Spark, "spark"));
        SkillTableFixture.Place(fake, schema, other);

        var skills = new PlayerSkills(fake, schema);
        skills.Refresh(Actor, 0);
        int version = skills.Version;

        skills.Refresh(other, 1);

        Assert.Equal(0, skills.Count);
        Assert.False(skills.Contains(Spark));
        Assert.NotEqual(version, skills.Version);

        skills.Refresh(0, 2);
        Assert.Equal(0, skills.Count);
    }
}
