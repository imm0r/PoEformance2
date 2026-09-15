using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The shipped stat table against the game's own.
/// </summary>
/// <remarks>
/// WHAT THIS CAUGHT, and it is the reason the reader beside it exists. data/stat_name_map.tsv is
/// keyed by Stats.dat ROW INDEX, and an index is a position: a league that inserts a row moves
/// every name after it. The copy this project shipped had been extracted in June, and measured
/// against the running client in September, TEN of the 148 rows a capture covers still agreed. The
/// first disagreement was at index 4678.
///
/// 4678 IS THE POINT. StatNames records that every live-verified reading of the off-by-one lies
/// between 1 and 2034, with one at 4290, and says in as many words that beyond that it is
/// EXTRAPOLATION - "a high id can be off by a different amount than a low one, and nothing here
/// would say so". Every check anybody had ever made lay below the break, which is exactly why the
/// file looked sound while the entity browser named half the table wrongly.
///
/// IT IS NOT UNFIXABLE, AND A WRONG CONCLUSION IS RECORDED HERE ON PURPOSE. Two regenerations in a
/// row still disagreed, and that was read as the two tables being NUMBERED differently - which
/// would have meant no export could ever agree. Both of those exports had been pointed at a stale
/// CSV dump. Corrected, the file matches the client exactly: 27281 rows against 27281, and all 148
/// the capture names. The conclusion was reached from two measurements that were both real and both
/// of the wrong thing.
///
/// SO THE CASE FOR READING MEMORY IS THE NARROWER ONE. Not "the file cannot agree" - it can, and
/// does. It is that the file only agrees while somebody remembers to re-export it, and when it
/// stops agreeing nothing says so: the names stay plausible, just one row off. The game's own table
/// needs no remembering. THE TEST BELOW IS NOW THE ALARM: it asserts that the two agree, so the
/// day the client moves and the export does not, it says so out loud.
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

    /// <summary>Every row of Stats.dat, so a sweep covers the table rather than one corner of it.</summary>
    private static List<uint> EveryRow()
    {
        var asked = new List<uint>(27281);
        for (uint token = 1; token < 27281; token++)
        {
            asked.Add(token);
        }

        return asked;
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
    public void THEShippedTableAndTheGameAgreeExactly()
    {
        // THE ALARM. It passes today because the file was re-exported from the current client, and
        // it is meant to fail the day that stops being true - which is the failure nobody could see
        // before, because a stale name is a real stat's name, just the wrong one.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            Dictionary<long, string> file = ShippedNames();

            // SAME HEIGHT, which is the cheapest half of the check and the one that caught the last
            // bad export: 27000 extracted against 27281 loaded said the two could not line up.
            Assert.Equal(contents.StatsTable!.Rows, file.Count);

            var wrong = new List<string>();
            int checked_ = 0;
            foreach ((uint token, string name) in contents.NameStats(EveryRow()))
            {
                long index = token - contents.StatTokenBase;
                if (!file.TryGetValue(index, out string? mine))
                {
                    wrong.Add($"row {index}: the file has no entry, the game says {name}");
                    continue;
                }

                checked_++;
                if (mine != name)
                {
                    wrong.Add($"row {index}: file {mine}, game {name}");
                }
            }

            // Every row the capture can name, not a sample of them: NameStats reads one row per id
            // asked for, and it was asked for the whole table.
            Assert.True(checked_ > 100, $"only {checked_} rows were nameable - the capture is thin");
            Assert.Empty(wrong);
        }
    }

    [Fact]
    public void STATNAMESPrefersTheGameAndSaysSo()
    {
        // AND THE TWO SOURCES AGREE, which is what makes this a check on the READER rather than on
        // the file. The names come out of an export somebody generated from the game's own data by
        // a completely different route - unpacked files, a Python tool - so the reader landing on
        // the same string for the same row is two independent paths meeting.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            StatNames names = StatNames.Load(Path.Combine(Root.FullName, "data", "stat_name_map.tsv"));
            Assert.False(names.FromGame);
            Assert.Contains("stat_name_map.tsv", names.Source, StringComparison.Ordinal);

            const uint Water = 25862;   // the token a node carries for the Water biome stat
            Assert.Equal("map_water_biome", names.Of(Water));

            names.Learn(Table(replay, Assert.IsType<DatTableFacts>(contents.StatsTable)));

            Assert.True(names.FromGame);
            Assert.Contains("the game", names.Source, StringComparison.Ordinal);
            Assert.Equal("map_water_biome", names.Of(Water));
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
