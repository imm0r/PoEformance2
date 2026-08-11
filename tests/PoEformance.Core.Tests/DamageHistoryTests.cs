using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the damage did over a map, kept so it can be drawn as a shape.
/// </summary>
/// <remarks>
/// The graph is ImGui and cannot be tested here. What can is everything the graph is made of:
/// that a sample is a RATE rather than a running total, that its three parts are the same
/// three numbers the readout shows, and that a new map does not begin with the last one's
/// totals subtracted from nothing.
/// </remarks>
public class DamageHistoryTests
{
    private static WorldEntity Monster(uint id, int life, int max = 1000)
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: $"Metadata/Monsters/Test{id}",
            Kind: EntityKind.Monster,
            WorldX: 0f,
            WorldY: 0f,
            WorldZ: 0f,
            Life: new Vital(life, max, 0, 0),
            EnergyShield: new Vital(0, 0, 0, 0),
            Name: $"Test {id}");

    private static WorldSnapshot Area(uint hash, params WorldEntity[] entities)
        => new(true, null, entities, new float[16], AreaHash: hash);

    [Fact]
    public void ASAMPLEIsARateRatherThanARunningTotal()
    {
        // A total over a map only ever goes up, and the shape of a ramp says nothing. What a
        // damage graph is asked is when the damage was happening, which is the rate.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Monster(1, 1000)), 0);
        meter.Look(Area(1, Monster(1, 500)), 500);    // 500 over half a second
        meter.Look(Area(1, Monster(1, 250)), 1000);   // 250 over the next half

        IReadOnlyList<DamageSample> samples = meter.History.In(1);

        Assert.Equal(2, samples.Count);
        Assert.Equal(1000f, samples[0].Watched, 1f);   // 500 in 0.5s
        Assert.Equal(500f, samples[1].Watched, 1f);    // 250 in 0.5s
    }

    [Fact]
    public void ANDNothingIsWrittenDownUntilTheStretchIsLongEnoughToBeOne()
    {
        // The meter is fed thirty times a second and most readings carry no damage at all, so
        // a sample per read is a graph of the sampling rate rather than of the fighting.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Monster(1, 1000)), 0);
        meter.Look(Area(1, Monster(1, 900)), 30);
        meter.Look(Area(1, Monster(1, 800)), 60);

        Assert.Empty(meter.History.Samples());

        meter.Look(Area(1, Monster(1, 700)), DamageHistory.IntervalMs + 30);
        Assert.Single(meter.History.Samples());
    }

    [Fact]
    public void THESplitIsTheSameThreeNumbersTheReadoutShows()
    {
        // Taken as differences of the meter's own totals rather than accumulated alongside
        // them, so whatever the counting decides - a credit refused for distance, a monster
        // that came back - the graph and the readout cannot disagree.
        var meter = new DamageMeter { CountKills = true, CreditWithin = 0f };

        meter.Look(Area(1, Monster(1, 1000), Monster(2, 1000)), 0);
        meter.Look(Area(1, Monster(1, 400), Monster(2, 1000)), 500);   // 600 watched off #1
        meter.Look(Area(1, Monster(2, 1000)), 1000);                   // #1 vanishes with 400 left

        IReadOnlyList<DamageSample> samples = meter.History.In(1);
        float watched = 0f;
        float credited = 0f;
        float untouched = 0f;

        foreach (DamageSample sample in samples)
        {
            watched += sample.Watched * 0.5f;      // each sample covers half a second
            credited += sample.Credited * 0.5f;
            untouched += sample.Untouched * 0.5f;
        }

        Assert.Equal(meter.Observed, (long)Math.Round(watched));
        Assert.Equal(meter.CreditedHurt, (long)Math.Round(credited));
        Assert.Equal(meter.CreditedUntouched, (long)Math.Round(untouched));
    }

    [Fact]
    public void ANEWMapDoesNotStartWithTheLastOnesTotalsSubtracted()
    {
        // The meter's totals restart at nought in a new area. Leaving the sampling baselines
        // where they were would make the first sample of the map the whole of the last one,
        // negated - a spike of damage nobody did, pointing down.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Monster(1, 1000)), 0);
        meter.Look(Area(1, Monster(1, 200)), 500);

        meter.Look(Area(2, Monster(5, 1000)), 1000);
        meter.Look(Area(2, Monster(5, 900)), 1500);

        foreach (DamageSample sample in meter.History.In(2))
        {
            Assert.True(sample.Watched >= 0f, $"a new map produced {sample.Watched}");
        }

        // And the map before it is still there to look back at, filed under its own area.
        Assert.NotEmpty(meter.History.In(1));
        Assert.Equal([2u, 1u], meter.History.Areas());
    }

    [Fact]
    public void THETallestBarIsWhatAGraphHasToBeScaledTo()
    {
        var history = new DamageHistory();
        history.Add(new DamageSample(0, 1, 100f, 0f, 0f));
        history.Add(new DamageSample(250, 1, 50f, 50f, 100f));
        history.Add(new DamageSample(500, 2, 900f, 0f, 0f));

        Assert.Equal(200f, history.Highest(1));
        Assert.Equal(900f, history.Highest(2));
        Assert.Equal(900f, history.Highest());

        // Nought is a real answer - a whole map of standing still - and a caller has to treat
        // it as "no scale" rather than dividing by it.
        Assert.Equal(0f, new DamageHistory().Highest());
    }

    [Fact]
    public void ANDTheOldestSamplesFallOffRatherThanGrowingWithoutEnd()
    {
        var history = new DamageHistory();
        for (int i = 0; i < DamageHistory.Capacity + 10; i++)
        {
            history.Add(new DamageSample(i, 1, i, 0f, 0f));
        }

        Assert.Equal(DamageHistory.Capacity, history.Count);

        // Oldest first, and the ten oldest are gone.
        IReadOnlyList<DamageSample> held = history.Samples();
        Assert.Equal(10, held[0].AtMs);
        Assert.Equal(DamageHistory.Capacity + 9, held[^1].AtMs);
    }
}
