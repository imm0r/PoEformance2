using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// When an arena found in the ground counts as done, and when it does not.
/// </summary>
/// <remarks>
/// THE ASYMMETRY IS THE WHOLE TEST. Drawing "cleared" over an arena that still holds its boss
/// is a marker that lies about the one thing somebody consults it for; leaving a cleared one
/// Active is yesterday's picture. So every gate that stands between a monster leaving the
/// entity list and an arena being marked done is checked here: something has to have been
/// there, the player has to have been close enough to see it happen, and it has to have
/// stayed gone.
///
/// And the undo, which is what lets the timer be short: a boss that comes back from a phase
/// puts its arena back the way it was, on screen, rather than leaving it wrong until the area
/// is left.
/// </remarks>
public class BossArenaTests
{
    private const ulong ArenaId = 0x8000_0000_0000_0001;

    /// <summary>An arena at a world position, which is what the reader hands over as grid cells.</summary>
    private static TerrainLandmark Arena(float worldX, float worldY)
        => new(
            ArenaId,
            "Metadata/Terrain/Maps/Somewhere/Plantaton_Boss_01.tdt",
            "Plantaton Boss",
            PoiKind.BossArena,
            (int)(worldX / MapView.WorldToGrid),
            (int)(worldY / MapView.WorldToGrid),
            4);

    private static WorldEntity Player(float x, float y)
        => new(1, 0x1000, "Metadata/Characters/Int", EntityKind.Player, x, y, 0);

    private static WorldEntity Monster(float x, float y, ItemRarity rarity = ItemRarity.Unique)
        => new(
            7, 0x2000, "Metadata/Monsters/Plantaton/Plantaton", EntityKind.Monster, x, y, 0,
            Rarity: rarity, Name: "Plantaton");

    /// <summary>One frame: an area holding one arena, with whoever is standing in it.</summary>
    private static WorldSnapshot Frame(
        WorldEntity player, uint area = 1, params WorldEntity[] monsters)
    {
        var terrain = new TerrainGrid(
            [0], 1, 1, 0, 0, null, string.Empty, [Arena(1000f, 1000f)]);

        return new WorldSnapshot(
            true, player, [player, .. monsters], new float[16],
            Terrain: terrain, AreaHash: area);
    }

    [Fact]
    public void AnArenaWithItsBossInItIsNotDone()
    {
        var arenas = new BossArenas();
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 0);
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 60_000);

        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void ABossThatWasThereAndIsGoneClearsTheArena()
    {
        var arenas = new BossArenas();
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 0);

        // Gone, with the player still standing in the room. The wait is a delay rather than a
        // judgement - see ClearAfterMs - so the moment before it is up, nothing has changed.
        arenas.Note(Frame(Player(1000f, 1000f)), 1_000);
        Assert.False(arenas.IsCleared(ArenaId));

        arenas.Note(Frame(Player(1000f, 1000f)), 1_000 + arenas.ClearAfterMs);
        Assert.True(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void ABossThatComesBackUndoesIt()
    {
        var arenas = new BossArenas();
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 0);

        // Two empty frames: the first starts the clock, the second finds it run out. One
        // frame could never be enough - there would be nothing to measure the wait against.
        arenas.Note(Frame(Player(1000f, 1000f)), 5_000);
        arenas.Note(Frame(Player(1000f, 1000f)), 10_000);
        Assert.True(arenas.IsCleared(ArenaId));

        // A phase, not a death. The marker goes back the moment the thing is in the room
        // again, which is what makes a short wait safe.
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 11_000);
        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void NothingIsClearedFromAcrossTheMap()
    {
        var arenas = new BossArenas();
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 0);

        // The player walks off and the game stops listing what is over there. That is the
        // entity list going quiet, not a kill, and it is the single most likely way to mark
        // an arena done that nobody has touched.
        arenas.Note(Frame(Player(9000f, 9000f)), 10_000);
        arenas.Note(Frame(Player(9000f, 9000f)), 30_000);

        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void AnArenaNobodyEverSawABossInIsNotDone()
    {
        var arenas = new BossArenas();

        // Walking into an empty room proves nothing: plenty of bosses are not spawned until
        // something is touched, and an arena marked done on arrival would be worse than one
        // never marked at all.
        arenas.Note(Frame(Player(1000f, 1000f)), 0);
        arenas.Note(Frame(Player(1000f, 1000f)), 60_000);

        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void OnlyAUniqueCounts()
    {
        var arenas = new BossArenas();

        // A rare wandering through an arena is not its boss, so its death is not the arena's.
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f, ItemRarity.Rare)), 0);
        arenas.Note(Frame(Player(1000f, 1000f)), 30_000);

        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void AMonsterOutsideTheArenaIsNotInIt()
    {
        var arenas = new BossArenas();

        // Far enough away to be in another room. It is not the arena's boss and its
        // disappearance cannot clear the arena.
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(4000f, 4000f)), 0);
        arenas.Note(Frame(Player(1000f, 1000f)), 30_000);

        Assert.False(arenas.IsCleared(ArenaId));
    }

    [Fact]
    public void ANewAreaStartsFromNothing()
    {
        var arenas = new BossArenas();
        arenas.Note(Frame(Player(1000f, 1000f), 1, Monster(1000f, 1000f)), 0);
        arenas.Note(Frame(Player(1000f, 1000f)), 5_000);
        arenas.Note(Frame(Player(1000f, 1000f)), 10_000);
        Assert.True(arenas.IsCleared(ArenaId));

        // The next map's arena shares the landmark id - it is built from the tile path and the
        // place, neither of which the area changes - so a state kept across the hash would
        // mark the new map's boss dead before it was met.
        arenas.Note(Frame(Player(1000f, 1000f), 2), 20_000);
        Assert.False(arenas.IsCleared(ArenaId));
        Assert.Equal(0, arenas.ClearedCount);
    }

    [Fact]
    public void AFrameWithNoTerrainIsHarmless()
    {
        var arenas = new BossArenas();
        arenas.Note(new WorldSnapshot(false, null, [], new float[16]), 0);
        arenas.Note(new WorldSnapshot(true, Player(0f, 0f), [], new float[16]), 1_000);

        Assert.Equal(0, arenas.ClearedCount);
    }
}
