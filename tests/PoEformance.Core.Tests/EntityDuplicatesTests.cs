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
    /// <param name="render">
    /// The Render component - what identifies the thing in the world. Defaults to following
    /// the entity address, so a caller that does not care gets a distinct object; passing the
    /// same value for two entities is how "one monster wearing several entities" is spelled.
    /// </param>
    private static WorldEntity Monster(
        uint id, ulong address, float x, float y,
        string path = "Metadata/Monsters/Hyena", ulong render = 0)
        => new(
            Id: id,
            Address: address,
            Path: path,
            Kind: EntityKind.Monster,
            WorldX: x,
            WorldY: y,
            WorldZ: 0f,
            Life: new Vital(100, 100, 0, 0),
            Render: render == 0 ? address : render);

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
            Monster(1, 0xA001, 10f, 10f, render: 0xF00D),
            Monster(2, 0xA002, 10f, 10f, render: 0xF00D),
            Monster(3, 0xA003, 10f, 10f, render: 0xF00D),
            Monster(4, 0xB000, 20f, 20f),
        ]);

        Assert.Equal(4, counted.Entries);
        Assert.Equal(2, counted.Addresses);
        Assert.Equal(2, counted.Places);
        Assert.Equal(2, counted.SharedObject);
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
        Assert.Equal(0, counted.SharedObject);
        Assert.Equal(2, counted.SharedPlace);

        // NOT a fault, and this is the correction the recorded session forced: it holds
        // pairs of Firewall anomalies on one tile - separate objects the game stacks because
        // that is what a wall of fire is, classified as monsters by their path. A rule keyed
        // on position deleted half of every one of them.
        Assert.False(counted.Any);
        Assert.Contains("which is normal", counted.Describe());
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
                // Twelve entity objects with twelve ids, but three of them over each of
                // four RENDER components - which is what the dissector found: ids 229, 230
                // and 231 shared every component address, differing only in Positioned.
                listed.Add(Monster(
                    id: (place * 3) + copy + 1,
                    address: 0xA000 + ((ulong)place * 3) + copy,
                    x: 100f * place,
                    y: 0f,
                    render: 0xF000 + place));
            }
        }

        EntityDuplicates counted = EntityDuplicates.Of(listed);

        Assert.Equal(12, counted.Entries);
        Assert.Equal(4, counted.Addresses);   // four real monsters
        Assert.Equal(8, counted.SharedObject);
        Assert.True(counted.Any);
    }
}

/// <summary>
/// The collapsing as the reader actually does it, against a recorded session.
/// </summary>
/// <remarks>
/// These exist for the reason the noise filter's twin does: every test above could pass while
/// the collapsing was wired to nothing. Removing the call from the read path would break none
/// of them, and a rule nobody consults is worth nothing.
///
/// Against a REAL recording, which also answers what a synthetic list cannot: whether the game
/// really does hand out several entity objects for one monster. It does - this fixture was
/// recorded long before the twelve-rows-four-dots observation, and it carries the same thing.
/// </remarks>
public class EntityDuplicatesInTheReadTests
{
    private static WorldSnapshot Read()
    {
        var replay = PoEformance.Core.Memory.ReplayMemoryReader.Load(
            File.OpenRead(RealSessionTests.SceneFixturePath));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        return world.Read(replay.ResolvedStatics["GameStates"]);
    }

    /// <summary>
    /// The reader collapses NOTHING here - and that is the finding, not a gap.
    /// </summary>
    /// <remarks>
    /// This recording holds three pairs of Firewall anomalies standing on one tile: separate
    /// objects with separate Render components, which the game stacks because that is what a
    /// wall of fire is. They classify as monsters by their path, so a rule keyed on POSITION
    /// collapsed one of every pair - a live thing quietly deleted from the overlay, which is
    /// the exact failure the position key was warned about and then walked into.
    ///
    /// Keyed on the Render component instead, they are correctly left alone. Pinning zero
    /// here is therefore a guard against going back: any collapsing on this fixture means the
    /// key has loosened into a coincidence again.
    /// </remarks>
    [Fact]
    public void StackedGroundEffectsAreNotCollapsed()
    {
        WorldSnapshot snapshot = Read();

        Assert.Equal(0, snapshot.Collapsed);
    }

    /// <summary>Nothing that comes out of the reader wears two entities.</summary>
    /// <remarks>
    /// The invariant the damage meter depends on: it credits a monster's remaining pool per
    /// entry, so one monster behind two entries is paid for twice.
    /// </remarks>
    [Fact]
    public void WhatComesOutHoldsOneEntityPerObject()
    {
        EntityDuplicates counted = EntityDuplicates.Of(Read().Entities);

        Assert.False(counted.Any, counted.Describe());
        Assert.Equal(counted.Entries, counted.Addresses);
    }

    /// <summary>
    /// Positions DO repeat here, and the count says so without calling it a fault.
    /// </summary>
    /// <remarks>
    /// The half that keeps the readout honest: a row that cried duplicate at every stacked
    /// effect would be one nobody reads by the second map.
    /// </remarks>
    [Fact]
    public void SharedPlacesAreReportedWithoutBeingAFault()
    {
        EntityDuplicates counted = EntityDuplicates.Of(Read().Entities);

        Assert.True(counted.SharedPlace > 0, "the fixture should hold stacked anomalies");
        Assert.False(counted.Any);
        Assert.Contains("which is normal", counted.Describe());
    }

    /// <summary>Every entity carries the component that identifies it.</summary>
    /// <remarks>
    /// Without this the collapsing above would key on zero for everything and merge the whole
    /// area into one monster - a failure too large to miss, which is exactly why it is worth
    /// one line to make impossible.
    /// </remarks>
    [Fact]
    public void EveryEntityCarriesItsRenderComponent()
    {
        Assert.All(Read().Entities, entity => Assert.NotEqual(0ul, entity.Render));
    }
}
