using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game's own names for the numbers in a Stats component - and the off-by-one.
/// </summary>
/// <remarks>
/// The table's key is the 0-based row index of the game's Stats.csv, and its own header says
/// that is what memory holds. It is not: memory holds that index PLUS ONE. Established
/// against a live level-96 character rather than argued about, and confirmed several times
/// over in one reading - see StatNames.
///
/// Read straight, the same character's stats are individually plausible and collectively
/// nonsense: id 1 becomes "item_drop_slots" with a value of 96, and the three resistances at
/// 75, 55 and 75 become fire damage modifiers. That is how an off-by-one hides, and it is why
/// this is pinned rather than remembered.
/// </remarks>
public class StatNamesTests
{
    private static StatNames Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "stat_name_map.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return StatNames.Load(Path.Combine(dir.FullName, "data", "stat_name_map.tsv"));
    }

    [Fact]
    public void TheIdsInMemoryAreOneAheadOfTheTable()
    {
        StatNames names = Load();
        Assert.True(names.Count > 25_000, $"only {names.Count} names loaded");

        // What the live character read, with what it has to resolve to.
        Assert.Equal("level", names.Of(1));
        Assert.Equal("base_maximum_life", names.Of(118));
        Assert.Equal("base_maximum_mana", names.Of(122));
        Assert.Equal("movement_velocity_+permyriad", names.Of(236));
        Assert.Equal("cold_damage_resistance_%", names.Of(298));
        Assert.Equal("fire_damage_resistance_%", names.Of(299));
        Assert.Equal("lightning_damage_resistance_%", names.Of(300));
        Assert.Equal("chaos_damage_resistance_%", names.Of(301));
    }

    /// <summary>An id with no row is left as a number rather than guessed at.</summary>
    [Fact]
    public void AnIdWithNoNameIsNotGivenOne()
    {
        StatNames names = Load();

        Assert.Null(names.Of(0));            // nothing is one ahead of row -1
        Assert.Null(names.Of(9_000_000));    // past the end of the table

        // And a table that was never loaded names nothing, rather than throwing where the
        // browser would then show no stats at all.
        Assert.Null(StatNames.Empty.Of(1));
        Assert.Equal(0, StatNames.Empty.Count);
        Assert.Equal(0, StatNames.Load("no/such/file.tsv").Count);
    }
}
