using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Whether the entity list describes the same monster more than once.
/// </summary>
/// <remarks>
/// The observation behind this: the browser listed twelve monsters of one kind, the map drew
/// four dots for them, and the game's own counter said four remained. The dots come from the
/// same list the browser does, one per entity at its own position, so twelve entries on four
/// dots is twelve entries on four places.
///
/// What these tests are really pinning is the SEPARATION of the two shapes. "Several keys,
/// one object" is safe to collapse because there is only one monster; "separate objects, one
/// position" is a judgement that a pack stacked on its spawn point would fail. One number
/// covering both would hide exactly the distinction the fix depends on.
/// </remarks>
public class EntityDuplicatesTests
{
    private static WorldEntity Monster(uint id, ulong address, float x, float y, string path = "Metadata/Monsters/Hyena")
        => new(
            Id: id,
            Address: address,
            Path: path,
            Kind: EntityKind.Monster,
            WorldX: x,
            WorldY: y,
            WorldZ: 0f,
            Life: new Vital(100, 100, 0, 0));

    /// <summary>A list that says each thing once reports nothing repeated.</summary>
    [Fact]
    public void DistinctMonstersRepeatNothing()
    {
        EntityDuplicates counted = EntityDuplicates.Of(
        [
            Monster(1, 0xA000, 10f, 10f),
            Monster(2, 0xB000, 20f, 20f),
            Monster(3, 0xC000, 30f, 30f),
        ]);

        Assert.Equal(3, counted.Entries);
        Assert.Equal(3, counted.Addresses);
        Assert.Equal(3, counted.Places);
        Assert.False(counted.Any);
    }

    /// <summary>
    /// Several ids pointing at ONE object: the same monster, listed more than once.
    /// </summary>
    /// <remarks>
    /// The shape that is safe to collapse - there is only one monster, and the extra rows are
    /// the map handing out more than one key for it.
    /// </remarks>
    [Fact]
    public void SeveralKeysOnOneObjectAreCounted()
    {
        EntityDuplicates counted = EntityDuplicates.Of(
        [
            Monster(1, 0xA000, 10f, 10f),
            Monster(2, 0xA000, 10f, 10f),
            Monster(3, 0xA000, 10f, 10f),
            Monster(4, 0xB000, 20f, 20f),
        ]);

        Assert.Equal(4, counted.Entries);
        Assert.Equal(2, counted.Addresses);
        Assert.Equal(2, counted.Places);
        Assert.Equal(2, counted.SharedAddress);
        Assert.Contains("share an object", counted.Describe());
    }

    /// <summary>
    /// Separate objects on a byte-identical position - the shape that needs a judgement.
    /// </summary>
    [Fact]
    public void SeparateObjectsOnOnePlaceAreCountedApartFromThat()
    {
        EntityDuplicates counted = EntityDuplicates.Of(
        [
            Monster(1, 0xA000, 10f, 10f),
            Monster(2, 0xB000, 10f, 10f),
            Monster(3, 0xC000, 10f, 10f),
        ]);

        Assert.Equal(3, counted.Entries);
        Assert.Equal(3, counted.Addresses);  // three real objects
        Assert.Equal(1, counted.Places);     // on one spot
        Assert.Equal(0, counted.SharedAddress);
        Assert.Equal(2, counted.SharedPlace);
        Assert.Contains("not an object", counted.Describe());
    }

    /// <summary>Same spot, different monster: not the same thing twice.</summary>
    /// <remarks>
    /// The path is part of the key for this reason. A spider standing where a hyena stands is
    /// two monsters, and a position-only key would quietly make it one.
    /// </remarks>
    [Fact]
    public void DifferentKindsOnOneSpotAreNotDuplicates()
    {
        EntityDuplicates counted = EntityDuplicates.Of(
        [
            Monster(1, 0xA000, 10f, 10f, "Metadata/Monsters/Hyena"),
            Monster(2, 0xB000, 10f, 10f, "Metadata/Monsters/Spider"),
        ]);

        Assert.Equal(2, counted.Places);
        Assert.False(counted.Any);
    }

    /// <summary>Positions that merely round to the same value are left alone.</summary>
    /// <remarks>
    /// The key is the exact bits rather than a tolerance, because two monsters can genuinely
    /// stand a hair apart and a tolerance would swallow them.
    /// </remarks>
    [Fact]
    public void NearlyEqualPositionsAreNotTheSamePlace()
    {
        EntityDuplicates counted = EntityDuplicates.Of(
        [
            Monster(1, 0xA000, 10f, 10f),
            Monster(2, 0xB000, 10.0001f, 10f),
        ]);

        Assert.Equal(2, counted.Places);
        Assert.False(counted.Any);
    }

    /// <summary>Only monsters are counted - stacked ground effects are not a fault.</summary>
    [Fact]
    public void NonMonstersAreLeftOut()
    {
        WorldEntity effect = Monster(1, 0xA000, 10f, 10f) with { Kind = EntityKind.Effect };
        WorldEntity alsoEffect = Monster(2, 0xB000, 10f, 10f) with { Kind = EntityKind.Effect };

        EntityDuplicates counted = EntityDuplicates.Of([effect, alsoEffect]);

        Assert.Equal(0, counted.Entries);
        Assert.False(counted.Any);
        Assert.Contains("no monsters", counted.Describe());
    }

    /// <summary>The twelve-rows-four-dots case, as it was actually seen.</summary>
    [Fact]
    public void TheObservedCaseReadsAsThreeEntriesPerMonster()
    {
        var listed = new List<WorldEntity>();
        for (uint place = 0; place < 4; place++)
        {
            for (uint copy = 0; copy < 3; copy++)
            {
                // Distinct ids and distinct objects, on four places - the shape that has to
                // be told apart from twelve real monsters.
                listed.Add(Monster(
                    id: (place * 3) + copy + 1,
                    address: 0xA000 + ((ulong)place * 3) + copy,
                    x: 100f * place,
                    y: 0f));
            }
        }

        EntityDuplicates counted = EntityDuplicates.Of(listed);

        Assert.Equal(12, counted.Entries);
        Assert.Equal(4, counted.Places);
        Assert.Equal(8, counted.SharedPlace);
        Assert.True(counted.Any);
    }
}
