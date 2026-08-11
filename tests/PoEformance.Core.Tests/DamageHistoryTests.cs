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
    private static WorldEntity Monster(
        uint id, int life, int max = 1000, ItemRarity rarity = ItemRarity.Unknown, float away = 0f)
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: $"Metadata/Monsters/Test{id}",
            Kind: EntityKind.Monster,
            WorldX: away,
            WorldY: 0f,
            WorldZ: 0f,
            Rarity: rarity,
            Life: new Vital(life, max, 0, 0),
            EnergyShield: new Vital(0, 0, 0, 0),
            Name: $"Test {id}");

    /// <summary>The player at the origin, so a monster's WorldX is its distance.</summary>
    private static WorldEntity Player()
        => new(
            Id: 999,
            Address: 0x999,
            Path: "Metadata/Player",
            Kind: EntityKind.Player,
            WorldX: 0f,
            WorldY: 0f,
            WorldZ: 0f,
            Life: new Vital(100, 100, 0, 0));

    private static WorldSnapshot Area(uint hash, params WorldEntity[] entities)
        => new(true, null, entities, new float[16], AreaHash: hash);

    private static WorldSnapshot Seen(uint hash, params WorldEntity[] entities)
        => new(true, Player(), entities, new float[16], AreaHash: hash);

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
    public void ASAMPLESaysWhatItWasFoughtAgainst()
    {
        // A rate on its own does not say whether it was a good moment: five thousand into a
        // rare is a build working, and five thousand into forty white monsters is a build that
        // cannot single-target. The census is what turns the bar into something readable.
        var meter = new DamageMeter { CountKills = false };

        WorldEntity[] pack =
        [
            Monster(1, 1000, rarity: ItemRarity.Normal),
            Monster(2, 1000, rarity: ItemRarity.Normal),
            Monster(3, 1000, rarity: ItemRarity.Magic),
            Monster(4, 1000, rarity: ItemRarity.Rare),
            Monster(5, 1000, rarity: ItemRarity.Unique),
        ];

        meter.Look(Seen(1, pack), 0);
        meter.Look(Seen(1, pack), 500);

        MonsterCensus nearby = meter.History.In(1)[^1].Nearby;

        Assert.Equal(new MonsterCensus(2, 1, 1, 1), nearby);
        Assert.Equal(5, nearby.All);
    }

    [Fact]
    public void ANDOnlyWhatIsActuallyNearby()
    {
        // "In the entity list" is not "around you": the list is a bubble a good deal wider than
        // the screen, so counting all of it would report monsters nobody was fighting.
        var meter = new DamageMeter { CountKills = false };

        WorldEntity[] spread =
        [
            Monster(1, 1000, rarity: ItemRarity.Normal, away: 10f),
            Monster(2, 1000, rarity: ItemRarity.Normal, away: DamageMeter.NearbyWorldUnits * 3f),
        ];

        meter.Look(Seen(1, spread), 0);
        meter.Look(Seen(1, spread), 500);

        Assert.Equal(1, meter.History.In(1)[^1].Nearby.All);
    }

    [Fact]
    public void ANDWithNoPlayerToMeasureFromEverythingCounts()
    {
        // The honest degradation: there is no distance without a player, and the entity list is
        // already a bubble around them - so it is the same question at a coarser radius rather
        // than a different question, and reporting nothing would read as an empty map.
        var meter = new DamageMeter { CountKills = false };

        WorldEntity[] far = [Monster(1, 1000, rarity: ItemRarity.Rare, away: 99_999f)];

        meter.Look(Area(1, far), 0);
        meter.Look(Area(1, far), 500);

        Assert.Equal(new MonsterCensus(0, 0, 1, 0), meter.History.In(1)[^1].Nearby);
    }

    [Fact]
    public void THEBestWindowIsTheMostThatActuallyLandedOverThatLong()
    {
        // Four quarter-seconds at 1000/s, then four at 100/s. The best second is the first
        // four; the best two seconds has to include the quiet half and so reads as the mean.
        var history = new DamageHistory();
        for (int i = 0; i < 4; i++)
        {
            history.Add(new DamageSample(250 * (i + 1), 1, 1000f, 0f, 0f));
        }

        for (int i = 4; i < 8; i++)
        {
            history.Add(new DamageSample(250 * (i + 1), 1, 100f, 0f, 0f));
        }

        Assert.Equal(1000f, history.Best(1, 1), 1f);
        Assert.Equal(550f, history.Best(2, 1), 1f);

        // Longer than anything held is not a number: a window that was never filled would
        // divide a burst by time that was never measured.
        Assert.Equal(0f, history.Best(30, 1));
    }

    [Fact]
    public void ANDAWindowNeverSpansAHole()
    {
        // Two bursts either side of a loading screen are two bursts. Added across the gap they
        // would report one that never happened - the pause simply deleted.
        var history = new DamageHistory();
        history.Add(new DamageSample(250, 1, 1000f, 0f, 0f));
        history.Add(new DamageSample(500, 1, 1000f, 0f, 0f));

        // ... a minute of nothing ...
        history.Add(new DamageSample(60_250, 1, 1000f, 0f, 0f));
        history.Add(new DamageSample(60_500, 1, 1000f, 0f, 0f));

        // Half a second either side, so no window of a whole second was ever fought.
        Assert.Equal(0f, history.Best(1, 1));
        Assert.Equal(1000f, history.Best(0.5, 1), 1f);
    }

    [Fact]
    public void ANDItIsMeasuredOverREALTimeRatherThanOverSamples()
    {
        // A sample carries its own span, so a slow read that produced a longer bucket counts
        // for the longer time it covered. Counting samples instead would let a stuttering
        // machine report a burst it never sustained.
        var history = new DamageHistory();
        history.Add(new DamageSample(1000, 1, 1000f, 0f, 0f, SpanMs: 1000));

        // One sample, but it IS a whole second of a thousand a second.
        Assert.Equal(1000f, history.Best(1, 1), 1f);
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
