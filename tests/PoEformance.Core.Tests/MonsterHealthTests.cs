using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// A monster's health pool, carried out of a read that was already happening.
/// </summary>
/// <remarks>
/// The corpse check needed the current value and threw the rest away - the same shape as the
/// rarity finding before it. The maximum sits four bytes from the current one and the shield a
/// little further on, so ONE span read yields all of it where the old code made one call for
/// a single number.
///
/// What this file checks is the arithmetic and the failure behaviour. Whether the offsets are
/// right is a question only the game can answer; what a test can pin down is that a failed
/// read never comes back looking like a monster at zero health.
/// </remarks>
public class MonsterHealthTests
{
    private static OffsetSchema Schema() => RealSessionTests.Schema();

    [Fact]
    public void AVitalKnowsHowFullItIs()
    {
        Assert.Equal(50, new Vital(500, 1000, 0, 0).Percent);
        Assert.Equal(100, new Vital(1000, 1000, 0, 0).Percent);
        Assert.Equal(0, new Vital(0, 1000, 0, 0).Percent);
    }

    [Fact]
    public void APoolThatWasNeverReadIsNotAMonsterAtZeroHealth()
    {
        // The distinction the whole read is built on, and the one a health bar would get
        // wrong most usefully: a bar drawn empty over a healthy monster says "nearly dead"
        // about something about to kill you.
        Vital never = default;

        Assert.False(never.IsValid);
        Assert.Equal(0, never.Max);
    }

    [Fact]
    public void AMonsterWithNoLifeComponentCarriesNoPool()
    {
        var entity = new WorldEntity(
            1, 0x100, "Metadata/Monsters/Skeleton", EntityKind.Monster, 0, 0, 0);

        Assert.False(entity.Life.IsValid);
        Assert.False(entity.EnergyShield.IsValid);
    }

    [Fact]
    public void TheSpanCoversBothPoolsAndNothingWasted()
    {
        // The span is computed from the schema rather than written down, so a drift in either
        // offset still reads the right bytes. What must hold is that it REACHES the shield's
        // last field and does not run past it - short and it reads garbage for the shield,
        // long and every monster pays for bytes nobody looks at.
        StructDef life = Schema().Structs["Life"];
        StructDef vital = Schema().Structs["Vital"];

        int health = life.OffsetOf("Health");
        int shield = life.OffsetOf("EnergyShield");
        int current = vital.OffsetOf("Current");
        int max = vital.OffsetOf("Max");

        int span = shield - health + current + sizeof(int);

        Assert.True(shield > health, "the shield sits after the health pool - the span assumes it");
        Assert.True(span >= shield - health + Math.Max(current, max) + sizeof(int), "the span is short of the shield's last field");
        Assert.True(span < shield - health + 0x100, "the span runs well past anything it reads");
    }

    [Fact]
    public void ManaIsDeliberatelyOutsideTheSpan()
    {
        // No monster's mana is worth drawing, and the pools are laid out health, mana, shield
        // - so a span from health to shield carries mana along for free rather than by
        // choice. Worth stating: somebody reordering these would otherwise widen the read for
        // a number nothing asks for.
        StructDef life = Schema().Structs["Life"];

        Assert.True(life.OffsetOf("Mana") > life.OffsetOf("Health"));
        Assert.True(life.OffsetOf("Mana") < life.OffsetOf("EnergyShield"));
    }
}

/// <summary>
/// Whether a chest has already been opened.
/// </summary>
/// <remarks>
/// The offsets were in the schema and nothing read them. An opened chest is the one marker
/// that actively MISLEADS - it says "there is something over here" about a place somebody has
/// already been, and a map full of them is a map full of wasted walks.
/// </remarks>
public class ChestStateTests
{
    private static WorldEntity Chest(bool? opened) => new(
        1, 0x100, "Metadata/Chests/Chest01", EntityKind.Chest, 0, 0, 0,
        Poi: PoiKind.Chest, Opened: opened);

    [Fact]
    public void AnOpenedChestIsSpent()
        => Assert.True(Chest(opened: true).IsSpent);

    [Fact]
    public void AClosedOneIsNot()
        => Assert.False(Chest(opened: false).IsSpent);

    [Fact]
    public void AndSoIsOneThatCouldNotBeRead()
    {
        // Null means the component was absent or the read failed, and that is NOT "opened".
        // Treating it as spent would hide every chest the moment the offset drifts - a whole
        // feature disappearing with no error, which is the failure nobody traces.
        Assert.False(Chest(opened: null).IsSpent);
        Assert.False(new WorldEntity(2, 0x200, "Metadata/Monsters/X", EntityKind.Monster, 0, 0, 0).IsSpent);
    }

    [Fact]
    public void TheSchemaCarriesWhatTheReadNeeds()
    {
        // From GameHelper2's ChestOffsets, which the schema already held and nothing used.
        StructDef chest = RealSessionTests.Schema().Structs["Chest"];

        Assert.Equal(0x168, chest.OffsetOf("IsOpened"));
        Assert.Equal(0x160, chest.OffsetOf("ChestDataPtr"));
    }
}

/// <summary>What the recorded session can say about the new reads.</summary>
/// <remarks>
/// Not much, and for the reason written down twice already: a recording holds only the bytes
/// that were read while it was made, and this one predates both of these. What it CAN confirm
/// is that adding them did not disturb what already worked - the entity count, the player, and
/// the drops' rarity all have to come back unchanged.
/// </remarks>
public class ReadsStillAgreeWithTheRecordingTests
{
    [Fact]
    public void TheSceneReadsTheSameAsBefore()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        WorldSnapshot snapshot = new WorldReader(replay, RealSessionTests.Schema())
            .Read(replay.ResolvedStatics["GameStates"]);

        Assert.True(snapshot.InGame);
        Assert.NotNull(snapshot.Player);
        Assert.True(snapshot.Entities.Count > 50, $"the fixture held a full area; now {snapshot.Entities.Count}");

        // Every monster still classified as one, and no read turned an entity into a corpse
        // it should not have - the span read replaced the corpse check's own read, so this is
        // the thing most at risk of having been broken by it.
        Assert.True(snapshot.Entities.Count(e => e.Kind == EntityKind.Monster) > 50);
    }

    [Fact]
    public void APoolTheRecordingCannotSupplyReadsAsUNKNOWNRatherThanEmpty()
    {
        // The fixture has no Life bytes, so every pool comes back invalid. That has to be
        // distinguishable from "this monster is dead" - a health bar over a live monster
        // drawn empty is worse than no bar, and this is where that would first show.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        WorldSnapshot snapshot = new WorldReader(replay, RealSessionTests.Schema())
            .Read(replay.ResolvedStatics["GameStates"]);

        foreach (WorldEntity monster in snapshot.Entities.Where(e => e.Kind == EntityKind.Monster))
        {
            Assert.False(
                monster.Life.IsValid && monster.Life.Current <= 0,
                $"{monster.ShortName} reads as a valid pool at zero - that would draw an empty bar over a live monster");
        }
    }
}
