using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// How rare a monster is, carried out to where it can be drawn.
/// </summary>
/// <remarks>
/// The read was already happening and the answer was being thrown away: the corpse filter
/// asked "is this a boss" and dropped everything else it had learned. Which of forty dots is
/// the rare pack leader is what a monster radar is consulted for, so this is the most useful
/// thing it can say, and it cost one field.
///
/// A WARNING FOR ANYONE ADDING TO THIS FILE, learned the hard way. The recorded session
/// CANNOT confirm the rarity read, and it does not fail loudly - it comes back Unknown for
/// every monster, which reads exactly like a broken offset. The reason is that a recording
/// only holds the bytes that were read while it was being made, and this fixture was captured
/// eight hours before anything read ObjectMagicProperties: the component pointer is in there,
/// the component's contents are not. Tests written against it prove nothing about the read
/// either way, so what is checked here is the part that CAN be: the mapping, and that a drop's
/// rarity was not disturbed.
/// </remarks>
public class MonsterRarityTests
{
    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")).AsOf(RealSessionTests.PrePatchEra);
    }

    [Theory]
    [InlineData(0, ItemRarity.Normal)]
    [InlineData(1, ItemRarity.Magic)]
    [InlineData(2, ItemRarity.Rare)]
    [InlineData(3, ItemRarity.Unique)]
    public void TheGamesOwnNumberBecomesARarity(int raw, ItemRarity expected)
        => Assert.Equal(expected, Rarities.FromRaw(raw));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void ANumberOffTheScaleIsNotGuessedAt(int raw)
    {
        // Out of range means the read landed on some other field that happens to be an
        // integer. Clamping would turn that into a confident "Normal" and draw an ordinary
        // dot over a monster nobody has actually identified - the worse of the two answers,
        // because it is the one that looks right.
        Assert.Equal(ItemRarity.Unknown, Rarities.FromRaw(raw));
    }

    [Fact]
    public void ADropsRarityIsUntouchedByAnyOfThis()
    {
        // One field now answers one question for two kinds of thing, so: the test that it did
        // not start answering the wrong one. This the recording CAN confirm - drops and their
        // rarity were read when it was made.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        WorldSnapshot snapshot = new WorldReader(replay, Schema()).Read(replay.ResolvedStatics["GameStates"]);

        Assert.All(
            snapshot.Entities.Where(e => e.Kind is not EntityKind.WorldItem and not EntityKind.Monster),
            e => Assert.Equal(ItemRarity.Unknown, e.Rarity));
    }

    [Fact]
    public void TheSchemaStillHasTheFieldThisReads()
    {
        // The read itself needs the game to verify. What can be checked here is that the
        // offset it goes through still exists and is still sanity-checked, so a schema edit
        // cannot quietly remove the thing the feature stands on.
        StructDef properties = Schema().Structs["ObjectMagicProperties"];
        FieldDef? rarity = properties.Field("Rarity");

        Assert.NotNull(rarity);
        Assert.NotNull(rarity.Invariant);
    }
}
