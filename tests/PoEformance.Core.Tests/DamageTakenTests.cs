using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What came off the player's own pool - the other half of the question.
/// </summary>
/// <remarks>
/// Measured the same way as the damage out, so it carries the same blind spot: a hit fully
/// undone by a recharge inside one read is invisible. What these cover is the part that is a
/// decision - that life and shield are one pool, that a death is not a hit, and that the worst
/// hit is one read's drop rather than a stretch's worth.
/// </remarks>
public class DamageTakenTests
{
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

    private static WorldSnapshot At(uint hash, int life, int max = 1000, int shield = 0, int shieldMax = 0)
        => new(
            true,
            Player(),
            [],
            new float[16],
            PlayerVitals: new Vitals(
                new Vital(life, max, 0, 0), default, new Vital(shield, shieldMax, 0, 0)),
            AreaHash: hash);

    [Fact]
    public void WHATComesOffThePoolIsCounted()
    {
        var meter = new DamageMeter();

        meter.Look(At(1, 1000), 0);
        meter.Look(At(1, 700), 500);
        meter.Look(At(1, 500), 1000);

        Assert.Equal(500, meter.Taken);
        Assert.Equal(300, meter.WorstHit);
    }

    [Fact]
    public void LIFEAndShieldAreOnePoolBecauseThatIsWhatRunsOut()
    {
        // Split, a shield popping and refilling reads as a stream of damage and recoveries
        // rather than as one hit - and it is the total running out that kills.
        var meter = new DamageMeter();

        meter.Look(At(1, 1000, shield: 500, shieldMax: 500), 0);
        meter.Look(At(1, 1000, shield: 0, shieldMax: 500), 500);      // shield gone
        meter.Look(At(1, 800, shield: 0, shieldMax: 500), 1000);      // then life

        Assert.Equal(700, meter.Taken);
        Assert.Equal(500, meter.WorstHit);
    }

    [Fact]
    public void RECOVERYIsNotNegativeDamage()
    {
        // A pool going up is not a hit, and it must not be able to cancel one out - the
        // baseline simply moves, so the next drop is measured from where the player is.
        var meter = new DamageMeter();

        meter.Look(At(1, 1000), 0);
        meter.Look(At(1, 400), 500);
        meter.Look(At(1, 1000), 1000);   // healed back up
        meter.Look(At(1, 900), 1500);

        Assert.Equal(700, meter.Taken);
        Assert.Equal(600, meter.WorstHit);
    }

    [Fact]
    public void ADEATHIsNotAHit()
    {
        // At zero the pool is unread rather than empty - and counting the whole of it as one
        // enormous hit on the way back in would make every death the worst hit of the map.
        var meter = new DamageMeter();

        meter.Look(At(1, 1000), 0);
        meter.Look(At(1, 700), 500);
        meter.Look(At(1, 0), 1000);        // dead - the pool goes unread, not to nought
        meter.Look(At(1, 1000), 5000);     // back on their feet
        meter.Look(At(1, 950), 5500);

        Assert.Equal(350, meter.Taken);
        Assert.Equal(300, meter.WorstHit);
    }

    [Fact]
    public void THEWorstHitSaysWhatShareOfThePoolItTookAndWhatWasThere()
    {
        // Four thousand is a scratch or most of a life bar depending on the character, and the
        // tool knows which - so the share is the part that means something.
        var meter = new DamageMeter();

        meter.Look(At(1, 1000, max: 1000), 0);
        meter.Look(At(1, 250, max: 1000), 500);

        Assert.Equal(750, meter.WorstHit);
        Assert.Equal(0.75f, meter.WorstHitShare, 0.01f);
    }

    [Fact]
    public void ANDItRidesTheSameTimelineAsTheDamageOut()
    {
        // On the same sample, so the two can be read against each other: "the moment it stopped
        // killing things is the moment it started taking hits" is a shape, and it only shows on
        // one timeline.
        var meter = new DamageMeter();

        meter.Look(At(1, 1000), 0);
        meter.Look(At(1, 900), 500);

        DamageSample sample = Assert.Single(meter.History.In(1));
        Assert.Equal(200f, sample.Taken, 1f);   // 100 over half a second
    }

    [Fact]
    public void ANDANewAreaStartsAgain()
    {
        var meter = new DamageMeter();

        meter.Look(At(1, 1000), 0);
        meter.Look(At(1, 400), 500);

        meter.Look(At(2, 1000), 1000);
        meter.Look(At(2, 950), 1500);

        Assert.Equal(50, meter.Taken);
        Assert.Equal(50, meter.WorstHit);
    }
}
