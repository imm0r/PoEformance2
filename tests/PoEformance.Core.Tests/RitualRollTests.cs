using System.Text.Json;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game's own reward roll, reproduced.
/// </summary>
/// <remarks>
/// CHECKED AGAINST THE AUTHORITY ITSELF, not against a second reading of it. The generator and
/// the reservoir pass were reverse-engineered from the PoE2 binary by yokkenUA for GameHelper2's
/// Atlas plugin; a port checked only against my own understanding of that code would agree with
/// whatever I misread. So the reference's <c>TinyMt32</c> and <c>PredictModPass</c> were lifted
/// VERBATIM into a throwaway console program, run over 400 random seeds and every reservoir pick
/// across a grid of line states, and its output committed as
/// <c>tests/fixtures/ritual-roll-vectors.json</c>. These tests replay that.
///
/// It is the strongest check available without the game: it cannot prove the reference matches
/// PoE2 - only a live roll can - but it does prove this port matches the reference, which is the
/// half that is mine to get wrong.
///
/// The vectors cover the parts a transcription slip would land in: the seeding's three phases,
/// the state transition's unusual shift direction, the tempering, the rejection rule in
/// <c>Below</c> at bounds from 1 to 0xFFFFFFFE, and the reservoir's skip-without-drawing rule.
/// </remarks>
public class RitualRollTests
{
    private sealed record Vectors(List<SeedCase> Seeds, List<PickCase> Picks, List<int> PoolRows);

    private sealed record SeedCase(uint W0, uint W1, uint W2, uint W3, List<uint> Draws, List<uint> Below);

    private sealed record PickCase(uint Line, uint Cc, uint Ci, uint Mc, int Granted, int Pick);

    private static readonly uint[] Bounds = [1, 2, 3, 100, 65_000, 1_000_000, 0xFFFFFFFEu];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static Vectors Reference()
    {
        string path = Path.Combine(Root(), "tests", "fixtures", "ritual-roll-vectors.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Vectors>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static RitualMods Pool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "ritual-mods.json")))
        {
            dir = dir.Parent;
        }

        return RitualMods.Load(Path.Combine(dir!.FullName, "data", "ritual-mods.json"));
    }

    [Fact]
    public void THEGeneratorDrawsWhatTheGamesDoes()
    {
        // Eight draws from each of four hundred seeds. A wrong shift direction, a missed
        // finalisation round or an off-by-one in the jump all show up in the first draw.
        Vectors reference = Reference();
        Assert.Equal(400, reference.Seeds.Count);

        foreach (SeedCase one in reference.Seeds)
        {
            RitualRandom random = RitualRandom.Seed(one.W0, one.W1, one.W2, one.W3);
            for (int i = 0; i < one.Draws.Count; i++)
            {
                Assert.Equal(one.Draws[i], random.Next());
            }
        }
    }

    [Fact]
    public void ANDBoundsAreRejectedTheSameWayToo()
    {
        // The rejection fires rarely, so a wrong rule passes casual testing and desynchronises
        // the stream on the seeds where it matters. Each case draws through a fresh seed in the
        // same order the reference did.
        Vectors reference = Reference();

        foreach (SeedCase one in reference.Seeds)
        {
            RitualRandom random = RitualRandom.Seed(one.W0, one.W1, one.W2, one.W3);
            for (int i = 0; i < Bounds.Length; i++)
            {
                Assert.Equal(one.Below[i], random.Below(Bounds[i]));
            }
        }
    }

    [Fact]
    public void ANDABoundOfNothingIsNotAskedFor()
    {
        // Guarded rather than trusted: the game never calls it with 0 or 1, but the reservoir's
        // running total starts at the first row's weight, and a pool whose first row somehow had
        // weight zero would ask for exactly this.
        RitualRandom random = RitualRandom.Seed(1, 2, 3, 4);
        Assert.Equal(0u, random.Below(0));
        Assert.Equal(0u, random.Below(1));
    }

    [Fact]
    public void THEReservoirPicksWhatTheGamePicks()
    {
        // 576 picks across a grid of line ids, committed counts, candidate ranks, both mod
        // passes, and the second-pass exclusion - checked by ROW INDEX in the pool, so a pool
        // read in a different order fails rather than quietly rolling different rewards.
        Vectors reference = Reference();
        RitualMods mods = Pool();
        Assert.Equal(67, mods.Count);

        // The reference's vectors carry the exact pool they were rolled against, by row number.
        // Rebuilding it from those rather than from a filter keeps this a test of the ROLL:
        // otherwise a disagreement about which rows are eligible reads as the arithmetic being
        // wrong, which is a different bug in a different place.
        List<RitualMod> pool = [.. reference.PoolRows.Select(row => mods.All[row])];
        Assert.Equal(64, pool.Count);

        Assert.Equal(576, reference.Picks.Count);
        foreach (PickCase one in reference.Picks)
        {
            RitualMod? picked = RitualMods.Pick(one.Line, one.Cc, one.Ci, one.Mc, pool, one.Granted);
            int index = picked is { } got ? pool.IndexOf(got) : -1;
            Assert.Equal(one.Pick, index);
        }
    }

    [Fact]
    public void ANDTheOrderOfThePoolIsPartOfTheAnswer()
    {
        // The reservoir walks the pool IN ORDER, drawing per row. Sorting it - which looks
        // harmless, and which anybody tidying this file might do - rolls different rewards from
        // the same seed. The file keeps the game's row numbers for this reason.
        RitualMods mods = Pool();
        Vectors reference = Reference();
        List<RitualMod> pool = [.. reference.PoolRows.Select(row => mods.All[row])];
        List<RitualMod> shuffled = [.. pool.OrderBy(mod => mod.Weight)];

        RitualMod? straight = RitualMods.Pick(7, 3, 2, 0, pool, 0);
        RitualMod? sorted = RitualMods.Pick(7, 3, 2, 0, shuffled, 0);

        Assert.NotNull(straight);
        Assert.NotEqual(straight, sorted);
    }

    [Fact]
    public void ASecondRewardIsNotRolledUnlessTheStatSaysSo()
    {
        // Which is the ordinary case: the chance is an atlas passive, so most players never see
        // a two-reward node at all. Nothing about the second pass should run for them.
        Assert.False(RitualMods.RollsTwice(1, 0, 0, 0));
        Assert.False(RitualMods.RollsTwice(1, 0, 0, -5));
        Assert.True(RitualMods.RollsTwice(1, 0, 0, 100));
        Assert.True(RitualMods.RollsTwice(1, 0, 0, 250));

        RitualMods mods = Pool();
        List<RitualMod> pool = mods.Available(new Dictionary<int, int>());
        Assert.NotEmpty(pool);

        (RitualMod? first, RitualMod? second) = RitualMods.Rewards(7, 3, 2, pool, 0);
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void ANDTheSecondNeverRepeatsWhatTheFirstGranted()
    {
        // Currency rewards share one stat across their whole trio, so a first pick of Exalted
        // Orbs must block the other two Exalted rows as well - not merely the identical row.
        RitualMods mods = Pool();
        List<RitualMod> pool = mods.Available(new Dictionary<int, int>());

        int checkedPairs = 0;
        for (uint line = 0; line < 40; line++)
        {
            (RitualMod? first, RitualMod? second) = RitualMods.Rewards(line, 2, 1, pool, 100);
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first!.Value.Stat, second!.Value.Stat);
            checkedPairs++;
        }

        Assert.Equal(40, checkedPairs);
    }

    [Fact]
    public void ACONDITIONALRewardNeedsItsStatBeforeItCanRoll()
    {
        RitualMods mods = Pool();

        List<RitualMod> without = mods.Available(new Dictionary<int, int>());
        RitualMod conditional = mods.All.First(mod => mod.Needs != 0 && mod.Weight > 0);

        Assert.DoesNotContain(conditional, without);

        // Either spelling of the id lets it in: the game's own ids run one below the data
        // table's, and which of the two this field holds has not been pinned down.
        Assert.Contains(conditional, mods.Available(new Dictionary<int, int> { [conditional.Needs] = 1 }));
        Assert.Contains(conditional, mods.Available(new Dictionary<int, int> { [conditional.Needs - 1] = 1 }));
    }

    [Fact]
    public void ANDAMissingPoolIsQuietRatherThanFatal()
    {
        // Without it the line still draws and still routes; only the predicted rewards go. That
        // is the ordinary state after a league changes the rewards and before somebody updates
        // the file, so it must not stop the tool.
        Assert.Equal(0, RitualMods.Load(null).Count);
        Assert.Equal(0, RitualMods.Load(Path.Combine(Path.GetTempPath(), "no-ritual-here.json")).Count);
        Assert.Empty(RitualMods.Empty.Available(new Dictionary<int, int>()));
        Assert.Null(RitualMods.Pick(1, 0, 0, 0, [], 0));
    }
}
