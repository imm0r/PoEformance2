using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What every rare and unique cost to put down.
/// </summary>
/// <remarks>
/// The question behind most single-target complaints, and one a rate averaged over a map
/// cannot answer, because no map is one long fight. What these cover is the part that is a
/// decision: which kills are worth a line, when the clock starts, and which of them the tool
/// is entitled to claim at all.
/// </remarks>
public class KillLogTests
{
    private static WorldEntity Monster(uint id, int life, ItemRarity rarity, float away = 0f)
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: $"Metadata/Monsters/Test{id}",
            Kind: EntityKind.Monster,
            WorldX: away,
            WorldY: 0f,
            WorldZ: 0f,
            Rarity: rarity,
            Life: new Vital(life, 1000, 0, 0),
            EnergyShield: new Vital(0, 0, 0, 0),
            Name: $"Test {id}");

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

    private static WorldSnapshot Seen(uint hash, params WorldEntity[] entities)
        => new(true, Player(), entities, new float[16], AreaHash: hash);

    [Fact]
    public void AKILLCostsWhatWasWatchedPlusWhatItStillHad()
    {
        // Both halves, because neither alone is the monster's cost: the watched part misses
        // every killing blow, and the credited remainder is only what was left.
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Rare)), 0);
        meter.Look(Seen(1, Monster(1, 400, ItemRarity.Rare)), 1000);   // 600 watched off
        meter.Look(Seen(1), 2000);                                     // gone with 400 left

        KillRecord kill = Assert.Single(meter.Kills.In(1));

        Assert.Equal(1000, kill.Damage);
        Assert.Equal(ItemRarity.Rare, kill.Rarity);
        Assert.True(kill.Watched);
    }

    [Fact]
    public void THEClockStartsAtTheFirstDamageRatherThanAtTheFirstSighting()
    {
        // A rare that followed you across half a map before anybody hit it did not take a
        // minute to kill, and timing from the sighting would say it did.
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Rare)), 0);
        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Rare)), 30_000);   // followed, unhurt
        meter.Look(Seen(1, Monster(1, 500, ItemRarity.Rare)), 31_000);    // first blood
        meter.Look(Seen(1), 33_000);                                      // down

        Assert.Equal(2f, Assert.Single(meter.Kills.In(1)).Seconds, 0.1f);
    }

    [Fact]
    public void AONESHOTIsSaidToBeOneRatherThanTimedAsZero()
    {
        // Nothing was watched, so there is no fight to time. A printed 0.0s would read as a
        // measurement rather than as the absence of one - and the whole of that kill rests on
        // the assumption that vanishing is dying.
        var meter = new DamageMeter();

        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Unique)), 0);
        meter.Look(Seen(1), 500);

        KillRecord kill = Assert.Single(meter.Kills.In(1));

        Assert.False(kill.Watched);
        Assert.Equal(0f, kill.Seconds);
        Assert.Equal(1000, kill.Damage);
        Assert.Equal(0f, kill.Dps);
    }

    [Fact]
    public void ONLYRaresAndBetterGetALine()
    {
        // A map kills hundreds of white monsters and none of them is a question anybody has.
        var meter = new DamageMeter();

        meter.Look(
            Seen(1, Monster(1, 1000, ItemRarity.Normal), Monster(2, 1000, ItemRarity.Magic),
                Monster(3, 1000, ItemRarity.Rare)),
            0);
        meter.Look(Seen(1), 500);

        KillRecord kill = Assert.Single(meter.Kills.In(1));
        Assert.Equal(ItemRarity.Rare, kill.Rarity);
    }

    [Fact]
    public void AKILLTheToolDoesNotBelieveInDoesNotAppearAtAll()
    {
        // Refused by the distance gate. A log of kills that includes ones the tool does not
        // believe in is not a log of kills, so it never appears rather than appearing with a
        // caveat nobody reads.
        var meter = new DamageMeter { CreditWithin = 100f };

        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Rare, away: 5_000f)), 0);
        meter.Look(Seen(1), 500);

        Assert.Empty(meter.Kills.In(1));
        Assert.True(meter.Withheld > 0);
    }

    [Fact]
    public void ANDCountingVanishedMonstersOffLeavesTheLogEmpty()
    {
        // With the assumption switched off nothing is ever credited, so nothing was killed as
        // far as this tool is concerned - the log has to agree with the figure above it.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Seen(1, Monster(1, 1000, ItemRarity.Rare)), 0);
        meter.Look(Seen(1, Monster(1, 200, ItemRarity.Rare)), 500);
        meter.Look(Seen(1), 1000);

        Assert.Empty(meter.Kills.In(1));
    }

    [Fact]
    public void THESlowestIsTheOneWorthActingOnAndSkipsTheOneShots()
    {
        // A one-shot has no fight to time, so it would lose every "slowest" for a reason that
        // is about the measurement rather than about the monster.
        var log = new KillLog();
        log.Add(new KillRecord(0, 1, "quick", ItemRarity.Rare, 1000, 2f, Watched: true));
        log.Add(new KillRecord(1, 1, "slow", ItemRarity.Rare, 9000, 14f, Watched: true));
        log.Add(new KillRecord(2, 1, "unseen", ItemRarity.Unique, 500, 0f, Watched: false));

        Assert.Equal("slow", Assert.NotNull(log.Worst(1)).Name);

        // Newest first, which is the order they are read in.
        Assert.Equal(["unseen", "slow", "quick"], log.In(1).Select(kill => kill.Name));
    }
}
