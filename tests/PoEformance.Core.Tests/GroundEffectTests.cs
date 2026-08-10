using PoEformance.Core.Memory;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Things that wear a monster's metadata path without being monsters.
/// </summary>
/// <remarks>
/// Everything under <c>Metadata/Monsters</c> was drawn as an enemy, and a recorded map says
/// how much of that is wrong: of thirty distinct monster-path names over 321 seconds, four
/// carried no Life component at all - a patch of smoke, a projectile in flight, burning
/// ground, and the scenery in a boss arena. All four hostile, all four given a red dot.
///
/// The rule that catches them is the reference's own test for whether an entity is a monster:
/// it has a Life component. What it deliberately does NOT do is decide from a component being
/// unreadable, because an entity nobody could read is not evidence of anything and dropping
/// those would empty the overlay whenever the reading went badly.
/// </remarks>
public class GroundEffectTests
{
    /// <summary>The four non-monsters in the recorded map, by name.</summary>
    /// <remarks>
    /// Named rather than counted, because the point is what they ARE. A count would pass
    /// just as well if the rule started dropping real monsters instead.
    /// </remarks>
    private static readonly string[] NotMonsters =
    [
        "LegionnaireSmokeGround",
        "LightningArrow",
        "Tier3OuterFires",
        "AtziriArenaSnakeHeads",
    ];

    [Fact]
    public void TheThingsWithNoLifeComponentAreNoLongerDrawnAsEnemies()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.MapFixturePath));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var survivors = new HashSet<string>();
        var realMonsters = new HashSet<string>();

        for (int frame = 0; frame <= 6878; frame += 7)
        {
            replay.Seek((uint)frame);
            foreach (WorldEntity monster in world.Read(gameStates).Entities
                .Where(e => e.Kind == EntityKind.Monster))
            {
                if (NotMonsters.Contains(monster.FileName))
                {
                    // Still in the snapshot is fine for a friendly one; being carried as a
                    // hostile monster is not.
                    if (!monster.IsEffect)
                    {
                        survivors.Add(monster.FileName);
                    }
                }
                else if (!monster.IsFriendly && !monster.IsEffect)
                {
                    realMonsters.Add(monster.FileName);
                }
            }
        }

        Assert.Empty(survivors);

        // And the map's real monsters are all still there, so the rule did not simply empty
        // the list. Twenty-five names carried both Life and Targetable in this recording.
        Assert.True(realMonsters.Count >= 25,
            $"only {realMonsters.Count} monster names survived: {string.Join(", ", realMonsters.Order())}");
    }

    /// <summary>An entity nobody could read is not declared an effect.</summary>
    /// <remarks>
    /// The dangerous direction. "No Life component" and "no component list" look identical
    /// from the outside and mean opposite things, and treating the second as the first would
    /// hide live monsters exactly when the reading is going badly - which is when somebody
    /// most needs to see them.
    /// </remarks>
    [Fact]
    public void AnUnreadableEntityIsNotAnEffect()
    {
        var unknown = new MonsterSigns(Health: null, Targetable: null, IsBoss: false, HasLife: null);
        Assert.False(unknown.IsEffect);
        Assert.False(unknown.IsHostileEffect);

        var noLife = new MonsterSigns(Health: null, Targetable: null, IsBoss: false, HasLife: false);
        Assert.True(noLife.IsEffect);
        Assert.True(noLife.IsHostileEffect);

        // And the older rule still stands on its own: something that expires and cannot be
        // targeted is an effect whether or not it has a pool.
        var expiring = new MonsterSigns(
            Health: 100, Targetable: false, IsBoss: false, Temporary: true, HasLife: true);
        Assert.True(expiring.IsEffect);

        // A summoned monster that expires but CAN be targeted is not.
        var summoned = new MonsterSigns(
            Health: 100, Targetable: true, IsBoss: false, Temporary: true, HasLife: true);
        Assert.False(summoned.IsEffect);
    }

    /// <summary>The corpse readout counts what the corpse rule is a question about.</summary>
    /// <remarks>
    /// The player's own Firewall has no Targetable component - nobody targets a wall - and
    /// this build casts twenty of them at a time. Counted, they outnumbered every real
    /// monster in the map by two to one and the readout announced that the component was
    /// "mostly NOT BEING FOUND" on every cast. The verdict is about leftover ENEMY dots, so
    /// that is the population it counts.
    /// </remarks>
    [Fact]
    public void TheCorpseReadoutIsNotDrownedOutByThePlayersOwnEffects()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.MapFixturePath));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int framesCrying = 0, framesJudging = 0;

        for (int frame = 0; frame <= 6878; frame += 7)
        {
            replay.Seek((uint)frame);
            CorpseSigns corpses = world.Read(gameStates).Corpses;
            if (corpses.Seen == 0)
            {
                continue;
            }

            framesJudging++;
            if (corpses.Unreadable > corpses.Targetable + corpses.Untargetable)
            {
                framesCrying++;
            }
        }

        Assert.True(framesJudging > 100, $"only {framesJudging} frames had anything to judge");
        // Not a threshold but a zero, because that is what it measures: over a whole map,
        // no frame has more unreadable hostile monsters than readable ones. A failure here
        // is the alarm doing its job - some hostile monster really is missing its component.
        Assert.Equal(0, framesCrying);
    }
}
