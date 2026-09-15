using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The shipped stat table against the game's own, and why the file has to go.
/// </summary>
/// <remarks>
/// THE PROJECT PREDICTED THIS AND COULD NOT SETTLE IT. StatNames records that every live-verified
/// reading of the stat shift lies between 1 and 2034, with one at 4290, and says in as many words
/// that beyond that it is EXTRAPOLATION: "a high id can be off by a different amount than a low
/// one, and nothing here would say so". This is the measurement that says so.
///
/// TEN OF 148 AGREE. Reading the ids the game holds at the rows a capture covers, the first
/// disagreement is at index 4678 - just above the highest reading anybody had checked, which is
/// exactly why the file looked sound for so long. Every verification was below the break.
///
/// AND IT IS DRIFT RATHER THAN A DIFFERENT KEYING, which matters because those look identical from
/// one wrong name: 134 of the file's names are still in the game's table, moved by 1, 2, 3, 6, 7 or
/// 9 rows. Several insertions at several points, which is precisely the shape StatNames warned a
/// single measured shift could not capture.
/// </remarks>
public class StatTableSessionTests
{
    /// <summary>The capture taken WITH NameStats in place, so it holds the Stats rows themselves.</summary>
    private const string Capture = "session-2026-09-statnames.rec";

    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir;
        }
    }

    private static string Fixture(string name) => Path.Combine(Root.FullName, "tests", "fixtures", name);

    /// <summary>Stats.dat as the atlas work reaches it, which is the only route these captures hold.</summary>
    private static (EndgameMapContentCatalogue Contents, ReplayMemoryReader Replay) Walked()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(Capture)));
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        var endgame = new EndgameMapCatalogue(replay, schema);
        var contents = new EndgameMapContentCatalogue(replay, schema);
        foreach (AtlasNode node in new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0)))
        {
            if (node.MapId.Length > 0 && endgame.ReadFromNode(node.Address))
            {
                break;
            }
        }

        Assert.True(contents.Read(endgame.ContentTable), contents.LastError);
        return (contents, replay);
    }

    private static Dictionary<long, string> ShippedNames()
    {
        var file = new Dictionary<long, string>();
        foreach (string line in File.ReadLines(Path.Combine(Root.FullName, "data", "stat_name_map.tsv")))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && long.TryParse(line.AsSpan(0, tab), out long key))
            {
                file[key] = line[(tab + 1)..].Trim();
            }
        }

        return file;
    }

    [Fact]
    public void THEShippedTableIsSTALEAndTheGameSaysWhere()
    {
        // THE MEASUREMENT. Not "a name looks odd" - the ids the game holds at the rows this capture
        // covers, against the file, one row at a time.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            Dictionary<long, string> file = ShippedNames();
            Assert.Equal(27000, file.Count);

            // Every row the capture can name. NameStats reads one row per id asked for, so this is
            // whatever the atlas work happened to touch - a sample of the table, not a walk of it.
            var asked = new List<uint>();
            for (uint token = 1; token < 27281; token++)
            {
                asked.Add(token);
            }

            IReadOnlyDictionary<uint, string> game = contents.NameStats(asked);
            Assert.NotEmpty(game);

            int same = 0;
            int differs = 0;
            long firstDrift = long.MaxValue;
            foreach ((uint token, string name) in game)
            {
                long index = token - contents.StatTokenBase;
                if (!file.TryGetValue(index, out string? mine))
                {
                    continue;
                }

                if (mine == name)
                {
                    same++;
                    continue;
                }

                differs++;
                firstDrift = Math.Min(firstDrift, index);
            }

            // The shape of the answer rather than its exact numbers, because the sample depends on
            // what the atlas work asked for and that will change: most of what is covered disagrees.
            Assert.True(differs > same * 5, $"same {same}, differs {differs}");

            // AND WHERE IT BREAKS, which is the part that matters: above the highest reading the
            // shift was ever verified at. StatNames names 4290 as the top of its evidence.
            Assert.InRange(firstDrift, 4291, 5000);
        }
    }

    [Fact]
    public void ANDTheFilesNamesAreSTILLThereJustMovedByDifferentAmounts()
    {
        // A DIFFERENT KEYING AND A DRIFT LOOK IDENTICAL from one wrong name, so this separates
        // them: if the file's entries were simply mis-keyed, one shift would put them all right.
        // They are found at several different distances, which is several insertions.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            Dictionary<long, string> file = ShippedNames();
            var asked = new List<uint>();
            for (uint token = 1; token < 27281; token++)
            {
                asked.Add(token);
            }

            var whereTheGameHasIt = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach ((uint token, string name) in contents.NameStats(asked))
            {
                whereTheGameHasIt.TryAdd(name, token - contents.StatTokenBase);
            }

            var distances = new HashSet<long>();
            foreach ((long index, string mine) in file)
            {
                if (whereTheGameHasIt.TryGetValue(mine, out long at) && at != index)
                {
                    distances.Add(at - index);
                }
            }

            // Several distinct distances, all forward: rows were inserted, never removed.
            Assert.True(distances.Count >= 3, $"distances: {string.Join(", ", distances.Order())}");
            Assert.All(distances, by => Assert.True(by > 0, $"moved backwards by {by}"));
        }
    }

    [Fact]
    public void STATNAMESPrefersTheGameAndSaysSo()
    {
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            StatNames names = StatNames.Load(Path.Combine(Root.FullName, "data", "stat_name_map.tsv"));
            Assert.False(names.FromGame);
            Assert.Contains("stat_name_map.tsv", names.Source, StringComparison.Ordinal);

            // The file's answer for a drifted id, before the game is put in front of it.
            const uint Water = 25862;   // the token a node carries for the Water biome stat
            Assert.Equal("map_faridun_city_biome", names.Of(Water));

            // StatTable is reached through the loader's file table in the tool; this capture only
            // holds the atlas route to the same table, so the facts come from there and the READER
            // is what is under test.
            var live = Assert.IsType<DatTableFacts>(contents.StatsTable);
            names.Learn(Table(replay, live));

            Assert.True(names.FromGame);
            Assert.Contains("the game", names.Source, StringComparison.Ordinal);
            Assert.Equal("map_water_biome", names.Of(Water));

            // And a low id, where the file was right all along, stays right.
            Assert.Equal("map_item_drop_rarity_+%", names.Of(1240));
        }
    }

    [Fact]
    public void ARowPastTheEndOfTheTableIsNothingRatherThanAGuess()
    {
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            StatTable table = Table(replay, Assert.IsType<DatTableFacts>(contents.StatsTable));
            Assert.Equal(27281, table.Facts.Rows);

            Assert.Null(table.Of(-1));
            Assert.Null(table.Of(table.Facts.Rows));
            Assert.Null(table.Of(long.MaxValue));
        }
    }

    [Fact]
    public void ANDARowIsReadONCE()
    {
        // The cache is the reason this can sit behind a browser that asks per draw: 27281 rows is
        // 27281 string reads if walked, and an entity carries a handful.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            StatTable table = Table(replay, Assert.IsType<DatTableFacts>(contents.StatsTable));
            Assert.Equal(0, table.Named);

            Assert.Equal("map_water_biome", table.Of(25861));
            Assert.Equal(1, table.Named);

            Assert.Equal("map_water_biome", table.Of(25861));
            Assert.Equal(1, table.Named);
        }
    }

    /// <summary>A StatTable over facts this capture already carries, without a file-table walk.</summary>
    /// <remarks>
    /// StatTable.From needs a LoadedDatTables walk, and no committed capture holds both that walk
    /// AND the Stats rows - a recording contains only the reads its build performed, and no build
    /// has yet done both. So the reader is exercised here through the facts the atlas route
    /// supplies, and the walk is covered separately by DatTableSurvey055Tests, which measures that
    /// Stats.dat IS in the loader's file table with the same 27281 rows.
    /// </remarks>
    private static StatTable Table(ReplayMemoryReader replay, DatTableFacts facts)
        => Assert.IsType<StatTable>(StatTable.Over(replay, facts, RealSessionTests.LiveSchema()));
}
