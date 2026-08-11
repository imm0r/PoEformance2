using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where on the map the damage and the time went.
/// </summary>
/// <remarks>
/// The painting is ImGui and cannot be tested here. What can is the part that decides what the
/// picture SAYS: which patch a position falls on, that the three measurements stay apart, that
/// areas do not bleed into each other, and above all the scale - one boss standing in one place
/// must not flatten the rest of the map into the same shade of nothing.
/// </remarks>
public class HeatMapTests
{
    private static WorldEntity Player(float x, float y)
        => new(
            Id: 999,
            Address: 0x999,
            Path: "Metadata/Player",
            Kind: EntityKind.Player,
            WorldX: x,
            WorldY: y,
            WorldZ: 0f,
            Life: new Vital(100, 100, 0, 0));

    private static WorldEntity Monster(uint id, int life)
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: $"Metadata/Monsters/Test{id}",
            Kind: EntityKind.Monster,
            WorldX: 0f,
            WorldY: 0f,
            WorldZ: 0f,
            Rarity: ItemRarity.Normal,
            Life: new Vital(life, 1000, 0, 0),
            EnergyShield: new Vital(0, 0, 0, 0),
            Name: $"Test {id}");

    /// <summary>A snapshot with the player somewhere in particular.</summary>
    private static WorldSnapshot At(uint hash, float x, float y, params WorldEntity[] entities)
        => new(true, Player(x, y), entities, new float[16], AreaHash: hash);

    /// <summary>One patch's worth of world units, so a test can step one across.</summary>
    private static float OnePatch => HeatMap.Step * MapView.WorldToGrid;

    /// <summary>
    /// The MIDDLE of the nth patch along, which is the only unambiguous place to stand.
    /// </summary>
    /// <remarks>
    /// A position exactly on a boundary belongs to whichever side the floating-point division
    /// lands on, and stepping by whole patches puts every one of them on a boundary - which is
    /// how the first version of these tests quietly built eighteen patches where it meant
    /// twenty. Real positions are never on the line; a fixture should not be either.
    /// </remarks>
    private static float Patch(float nth) => (nth + 0.5f) * OnePatch;

    [Fact]
    public void EVERYTHINGOnOnePatchAddsUp()
    {
        var heat = new HeatMap();
        heat.Add(1, Patch(0), 0f, dealt: 100, taken: 5, seconds: 0.5f);
        heat.Add(1, Patch(0) + (OnePatch * 0.2f), 0f, dealt: 50, taken: 0, seconds: 0.5f);

        (int x, int y, HeatCell cell) = Assert.Single(heat.In(1));

        Assert.Equal((0, 0), (x, y));
        Assert.Equal(150, cell.Dealt);
        Assert.Equal(5, cell.Taken);
        Assert.Equal(1f, cell.Seconds, 0.01f);
    }

    [Fact]
    public void ANDAPatchAwayIsItsOwnPlace()
    {
        var heat = new HeatMap();
        heat.Add(1, Patch(0), 0f, 100, 0, 0.5f);
        heat.Add(1, Patch(1), 0f, 100, 0, 0.5f);

        Assert.Equal(2, heat.In(1).Count);
    }

    [Fact]
    public void AREASDoNotBleedIntoEachOther()
    {
        // The same corner of two different maps is two different places, and a picture that
        // added them would be a picture of neither.
        var heat = new HeatMap();
        heat.Add(1, 0f, 0f, 100, 0, 0.5f);
        heat.Add(2, 0f, 0f, 999, 0, 0.5f);

        Assert.Equal(100, Assert.Single(heat.In(1)).Cell.Dealt);
        Assert.Equal(999, Assert.Single(heat.In(2)).Cell.Dealt);
    }

    [Fact]
    public void THEScaleIsTheNINETYFIFTHRatherThanTheHighest()
    {
        // The whole readability of the picture. One boss standing on one patch is ten times
        // anything else on the map, and scaled against that every other fight is the same shade
        // of nothing - so the top is cut off and everything below it is told apart.
        var heat = new HeatMap();
        for (int i = 1; i <= 40; i++)
        {
            heat.Add(1, Patch(i), 0f, dealt: 100, taken: 0, seconds: 0f);
        }

        heat.Add(1, Patch(0), 0f, dealt: 1_000_000, taken: 0, seconds: 0f);   // the boss

        Assert.Equal(41, heat.In(1).Count);
        Assert.Equal(100f, heat.Hottest(1, HeatOf.Dealt));
    }

    [Fact]
    public void ANDTheThreeMeasurementsAreScaledApart()
    {
        // A map can be busy with damage and quiet with danger. Sharing one scale would make
        // the second picture a copy of the first.
        var heat = new HeatMap();
        heat.Add(1, 0f, 0f, dealt: 10_000, taken: 3, seconds: 2f);

        Assert.Equal(10_000f, heat.Hottest(1, HeatOf.Dealt));
        Assert.Equal(3f, heat.Hottest(1, HeatOf.Taken));
        Assert.Equal(2f, heat.Hottest(1, HeatOf.Time));
    }

    [Fact]
    public void ANEMPTYAreaHasNoScaleRatherThanAScaleOfNought()
    {
        // A caller has to treat it as "nothing to paint" rather than dividing by it.
        Assert.Equal(0f, new HeatMap().Hottest(1, HeatOf.Dealt));
    }

    [Fact]
    public void ANOTHINGHappenedIsNotAPlace()
    {
        // Standing still outside an area, or a read with no damage and no time - a patch for
        // every one of those would fill the map with marks nothing happened on.
        var heat = new HeatMap();
        heat.Add(1, 0f, 0f, 0, 0, 0f);
        heat.Add(0, 0f, 0f, 100, 0, 1f);   // no area to file it under

        Assert.Equal(0, heat.Count);
    }

    [Fact]
    public void THEMeterFilesWhatItMeasuredWhereThePlayerWas()
    {
        // The one thing the meter was never writing down alongside how much there was.
        var meter = new DamageMeter { CountKills = false };
        float here = Patch(0);
        float there = Patch(10);

        meter.Look(At(1, here, 0f, Monster(1, 1000)), 0);
        meter.Look(At(1, here, 0f, Monster(1, 400)), 500);        // 600 dealt, standing here
        meter.Look(At(1, there, 0f, Monster(1, 400)), 1000);      // walked over there
        meter.Look(At(1, there, 0f, Monster(1, 100)), 1500);      // 300 dealt over there

        IReadOnlyList<(int X, int Y, HeatCell Cell)> patches = meter.Heat.In(1);

        Assert.Equal(2, patches.Count);
        Assert.Equal(600, patches.Single(patch => patch.X == 0).Cell.Dealt);
        Assert.Equal(300, patches.Single(patch => patch.X == 10).Cell.Dealt);

        // Time lands where the damage did, so the third picture is of the same run.
        Assert.True(patches.All(patch => patch.Cell.Seconds > 0f));
    }
}
