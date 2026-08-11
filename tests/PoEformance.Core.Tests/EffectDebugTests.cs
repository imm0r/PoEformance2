using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Keeping the ground effects, which the read throws away on purpose.
/// </summary>
/// <remarks>
/// The drawing is ImGui and cannot be tested here. What can is the part that would be
/// dangerous to get wrong: a kept effect must be invisible to everything that was protected
/// by dropping it. It carries Life and a position, so let back in as a MONSTER it would be
/// counted, health-barred and shot at by every rule that asks for monsters - which is the
/// screen full of unexplained enemy markers that made the reader drop it in the first place.
/// </remarks>
public class EffectDebugTests
{
    private static WorldEntity Effect(uint id)
        => new(
            Id: id,
            Address: 0x2000 + id,
            Path: "Metadata/Monsters/Firewall/Firewall",
            Kind: EntityKind.Effect,
            WorldX: 0f,
            WorldY: 0f,
            WorldZ: 0f,
            Rarity: ItemRarity.Normal,
            Life: new Vital(1000, 1000, 0, 0),
            EnergyShield: new Vital(0, 0, 0, 0),
            IsEffect: true,
            Name: "Firewall");

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

    private static WorldSnapshot Area(uint hash, params WorldEntity[] entities)
        => new(true, null, entities, new float[16], AreaHash: hash);

    [Fact]
    public void AKEPTEffectIsNotDamageTheMeterCounts()
    {
        // The rule that matters. A flame wall's Life falling is the wall expiring, not damage
        // anybody dealt - and a build that puts twenty of them down would otherwise report its
        // own scenery as its damage.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Effect(1), Monster(2, 1000)), 0);
        meter.Look(Area(1, Effect(1) with { Life = new Vital(200, 1000, 0, 0) }, Monster(2, 600)), 500);

        Assert.Equal(400, meter.Observed);   // the monster's 400, and none of the wall's 800
    }

    [Fact]
    public void ANDNotSomethingThatVanishedAndCountsAsAKill()
    {
        // The other half: a wall that expires is not a kill, so it must not be credited when
        // it leaves the snapshot - which is where the far larger number would come from.
        var meter = new DamageMeter { CountKills = true, CreditWithin = 0f };

        meter.Look(Area(1, Effect(1)), 0);
        meter.Look(Area(1), 500);

        Assert.Equal(0, meter.Total);
        Assert.Empty(meter.Kills.In(1));
    }

    [Fact]
    public void ANDNotAMonsterInTheCensusEither()
    {
        // The census is "what was this bar fought against". Twenty of the player's own walls
        // would make every pack look like a horde.
        var meter = new DamageMeter { CountKills = false };

        meter.Look(Area(1, Effect(1), Effect(2), Monster(3, 1000)), 0);
        meter.Look(Area(1, Effect(1), Effect(2), Monster(3, 900)), 500);

        Assert.Equal(1, meter.History.In(1)[^1].Nearby.All);
    }

    [Fact]
    public void THELayerFindsThemAndTheOrdinaryMonstersAreNotAmongThem()
    {
        // What the debug layer draws, and what it leaves alone: an overlay that ringed every
        // monster as well would say nothing about where the effects are.
        WorldSnapshot snapshot = Area(1, Effect(1), Effect(2), Monster(3, 1000));

        IReadOnlyList<EffectSort> sorts = EffectCensus.Sorts(snapshot);

        EffectSort sort = Assert.Single(sorts);
        Assert.Equal(2, sort.Count);
        Assert.Contains("Firewall", sort.Path, StringComparison.Ordinal);
    }
}
