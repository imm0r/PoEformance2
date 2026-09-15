using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which maps the atlas can actually hold, against the real table.
/// </summary>
/// <remarks>
/// THE ASSUMPTION THESE EXIST TO KILL. data/atlas-maps.json is named for the atlas and is not about
/// the atlas: it is a copy of WorldAreas.dat, which is EVERY AREA THE GAME HAS. Its 442 rows hold
/// 83 personal hideouts, 123 campaign zones, 16 Sanctum floors, the login scene, the
/// character-select screen, a row called NULL, the developers' Design and Programming worlds - and
/// a row called "Atlas", which is the atlas itself. Reading that file as "the atlas maps" has
/// quietly shaped this project since the file was ported from GameHelper2 under that name.
///
/// SETTLED, AGAINST THE GAME, AND ACTED ON. Four separate 0.5.5 captures reach
/// "Data/Balance/EndgameMaps.dat" off an atlas node and all four report 173 rows of 0xF1; the
/// newest of them (session-2026-09-endgamemaps.rec) was taken with this walker in place and carries
/// the row block, so the rows themselves are here: 173 rows naming 173 DIFFERENT areas. The file
/// was 440 entries of which 267 were areas the atlas can never send anybody to, and has been cut to
/// exactly those 173 - so the nine ids below are now absent from BOTH sides rather than present in
/// one of them.
///
/// THE CHECK THAT COULD HAVE REFUTED IT PASSED. 140 map ids were read off real atlas nodes in that
/// capture and every single one is in the table - the only direction that can test it, because an
/// id on a node IS an atlas map whatever any file says. The three earlier captures still run the
/// table-identity check: "the table is 173 rows" is a claim about the GAME, and one recording can
/// only ever be a claim about that recording.
///
/// AND ONE HYPOTHESIS DIED HERE, which is the other reason these tests are worth reading. The
/// MapContentSet column was expected to be the game's own version of data/atlas-maps.json's curated
/// tags. It is not. Its five values - All, DisallowAll, IrradiatedOnly, IrradiatedAndPowerfulBossOnly,
/// QuestAreaOnly - are about WHICH CONTENT MAY ROLL on a map, a different axis entirely, and "All"
/// alone covers 107 of the 173 including 91 with no curated tag at all. The tests below say so in
/// numbers rather than leaving the name to suggest otherwise.
/// </remarks>
public class EndgameMapSessionTests
{
    /// <summary>
    /// Captures that reach the table, newest first. The walk runs against the first that has rows.
    /// </summary>
    /// <remarks>
    /// A LIST rather than one name: a capture taken before this walker existed reaches the table
    /// and stops there, so the row-reading tests run against the first entry that actually carries
    /// the block, and a fresh capture is added rather than swapped in.
    /// </remarks>
    private static readonly string[] Captures =
    [
        "session-2026-09-mapcontent.rec",
        "session-2026-09-contentset.rec",
        "session-2026-09-endgamemaps.rec",
        "session-2026-09-catalogue.rec",
        "session-2026-09-atlas.rec",
    ];

    /// <summary>Areas that were plainly in the file and that the atlas can never send anybody to.</summary>
    private static readonly string[] NotAtlasMaps =
    [
        "G_login",              // the login screen
        "CharacterSelect",      // the character-select screen
        "NULL",                 // a row called NULL
        "TN_WorldMap",          // the atlas itself
        "Design",               // a developer scene
        "G1_1",                 // an Act 1 campaign zone
        "Sanctum_1",            // a Trial of the Sekhemas floor
        "HideoutSpace",         // a personal hideout
        "PersonalHideout",
    ];

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

    private static AtlasMapNames Names() => AtlasMapNames.Load(Path.Combine(Root.FullName, "data", "atlas-maps.json"));

    /// <summary>The table off one capture, reached the cheap way: from the first node that names it.</summary>
    private static EndgameMapCatalogue Read(ReplayMemoryReader replay, out IReadOnlyList<AtlasNode> nodes)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        var catalogue = new EndgameMapCatalogue(replay, schema);

        List<AtlasNode> read = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        nodes = read;

        foreach (AtlasNode node in read)
        {
            if (node.MapId.Length > 0 && catalogue.ReadFromNode(node.Address))
            {
                break;
            }
        }

        return catalogue;
    }

    private static ReplayMemoryReader Load(string fixture)
        => ReplayMemoryReader.Load(File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", fixture)));

    [Theory]
    [InlineData("session-2026-09-endgamemaps.rec")]
    [InlineData("session-2026-09-catalogue.rec")]
    [InlineData("session-2026-09-atlas.rec")]
    [InlineData("session-2026-09-atlasrows.rec")]
    public void EveryCaptureReachesTheSameTableOffAnAtlasNode(string fixture)
    {
        // THE MEASUREMENT THAT MATTERS, and it needs no row data: a node's data block holds the
        // EndgameMaps row at +0x290 and the TABLE at +0x298, the two halves of one foreign
        // reference, so ONE atlas node is the whole prerequisite. What it leads to is 173 rows -
        // against the 442 of WorldAreas, which is the file this project has been calling the atlas.
        //
        // Four captures rather than one because "the table is 173 rows" is a claim about the
        // GAME, and one recording can only ever be a claim about that recording.
        using ReplayMemoryReader replay = Load(fixture);
        EndgameMapCatalogue catalogue = Read(replay, out _);

        DatTableFacts facts = Assert.IsType<DatTableFacts>(catalogue.Table);
        Assert.Equal("Data/Balance/EndgameMaps.dat", facts.Path);
        Assert.Equal(173, facts.Rows);
        Assert.Equal(0xF1, facts.RowSize);
    }

    [Fact]
    public void ANodeWhoseOwnPointerIsMissingDoesNotEraseTheTableAnEarlierNodeFound()
    {
        // A caller walks nodes until one works. While the rows will not read, EVERY later node is
        // still tried - and a node whose pointer is absent from the capture used to replace "173
        // rows and none of them names an area" with "0x0 does not read as a dat table": the useful
        // diagnosis overwritten by a useless one, on the seventh attempt at the same table.
        using ReplayMemoryReader replay = Load("session-2026-09-catalogue.rec");
        EndgameMapCatalogue catalogue = Read(replay, out _);

        Assert.NotNull(catalogue.Table);
        Assert.DoesNotContain("0x0 does not read", catalogue.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContentSetColumnPointsAtOneTablesRowGrid()
    {
        // STRUCTURE RATHER THAN STRINGS, and worth keeping now that the names are readable too:
        // this is the check that confirmed the column BEFORE any capture carried them. Rows of one
        // table lie on that table's grid, so the handful the column resolves to must all be a
        // multiple of 0x19 apart - the row size dat-schema computes for EndgameMapContentSet's
        // three columns. A column pointing at the wrong thing does not land on one table's grid
        // five times over. It runs against the older capture on purpose: the pointers are all it
        // needs, and that is the point.
        using ReplayMemoryReader replay = Load("session-2026-09-endgamemaps.rec");
        OffsetSchema schema = RealSessionTests.LiveSchema();
        EndgameMapCatalogue catalogue = Read(replay, out _);

        DatTableFacts facts = Assert.IsType<DatTableFacts>(catalogue.Table);
        var block = new byte[facts.Rows * facts.RowSize];
        Assert.True(replay.TryRead(facts.RowsBegin, block.AsSpan()));

        int at = schema.Structs["EndgameMapsRow"].OffsetOf("MapContentSetRef");
        long size = schema.Structs["EndgameMapContentSetRow"].Constants["ComputedRowSize"];
        Assert.Equal(0x19, size);

        List<ulong> rows = [];
        for (int i = 0; i < facts.Rows; i++)
        {
            ulong set = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan((i * (int)facts.RowSize) + at));
            if (MemoryReaderExtensions.IsPlausiblePointer(set) && !rows.Contains(set))
            {
                rows.Add(set);
            }
        }

        Assert.Equal(5, rows.Count);
        ulong first = rows.Min();
        Assert.All(rows, row => Assert.Equal(0UL, (row - first) % (ulong)size));
    }

    [Fact]
    public void ANDTheCategoriesAreAboutWHATCANROLLRatherThanWhatKindOfMapItIs()
    {
        // THE HYPOTHESIS THIS REFUTES, which is the reason to write the names down. The column was
        // read expecting the game's own version of data/atlas-maps.json's curated tags - a coarser
        // "expedition / tower / arbiter". It is not a coarser version of that. It is a DIFFERENT
        // AXIS: which content may roll on the map. "Irradiated" is the corruption mechanic, not a
        // kind of map, and "All" is the default that 107 of the 173 carry.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            Assert.Equal(
                ["All", "DisallowAll", "IrradiatedAndPowerfulBossOnly", "IrradiatedOnly", "QuestAreaOnly"],
                endgame.Categories.Order(StringComparer.Ordinal));
            Assert.Equal(131, endgame.ContentSets.Count);
            Assert.Equal("All", endgame.ContentSets["MapAugury"]);
            Assert.Equal("IrradiatedOnly", endgame.ContentSets["ExpeditionLogBook_Atoll"]);
        }
    }

    [Fact]
    public void ANDTheyCannotStandInForTheCuratedTags()
    {
        // MEASURED RATHER THAN CONCLUDED FROM THE NAMES. "All" holds the towers, the lineage maps
        // and the one craft map - and 91 ordinary maps with no curated tag at all, which is what
        // makes it a default rather than a classification. Nothing could read a tag out of it.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            AtlasMapNames file = Names();
            string[] all = [.. endgame.ContentSets.Where(pair => pair.Value == "All").Select(pair => pair.Key)];

            Assert.Equal(107, all.Length);
            Assert.Equal(91, all.Count(id => file.Of(id).Tags.Count == 0));
            Assert.All(
                file.All.Where(pair => pair.Value.Tagged("tower")).Select(pair => pair.Key),
                id => Assert.Equal("All", endgame.ContentSets[id]));

            // And six curated tags have no category behind them AT ALL - hideout, quest and ritual
            // never reach one, so a third of the file's words are invisible from here.
            foreach (string tag in (string[])["hideout", "quest", "ritual"])
            {
                Assert.All(
                    file.All.Where(pair => pair.Value.Tagged(tag)).Select(pair => pair.Key),
                    id => Assert.False(endgame.ContentSets.ContainsKey(id), $"{tag}: {id}"));
            }
        }
    }

    [Fact]
    public void WHATTheyDOSayIsThatAnIrradiatedOnlyMapIsAlwaysASpecialOne()
    {
        // The one direction that holds, kept because a one-way implication is still a fact. Every
        // map the game restricts to Irradiated content carries a curated tag - eleven Expedition
        // logbooks and one Breach tower - and so does every IrradiatedAndPowerfulBossOnly one.
        // That is not enough to derive a tag, but it is enough to notice an untagged map appearing
        // in either category, which would mean the file has fallen behind.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            AtlasMapNames file = Names();
            string[] restricted =
            [
                .. endgame.ContentSets
                    .Where(pair => pair.Value is "IrradiatedOnly" or "IrradiatedAndPowerfulBossOnly")
                    .Select(pair => pair.Key),
            ];

            Assert.Equal(16, restricted.Length);
            Assert.All(restricted, id => Assert.NotEmpty(file.Of(id).Tags));
        }
    }

    [Fact]
    public void ThirteenMapsCarryAContentArrayAndTheWalkFollowsOneToItsTable()
    {
        // THIRTEEN IS ENOUGH, which is the thing this was uncertain about: the MapContent column
        // is on 13 of the 173 rows, so a walk that gave up after the first few would find none.
        // Striding the whole table finds one and the table it points at comes free with it.
        //
        // This test used to assert the opposite half - that the capture of the day held the count
        // but not the entries behind it, because the build that recorded it had no reason to read
        // them. That was true of that recording and is the rule worth remembering: a recording
        // holds only the reads its build performed. session-2026-09-mapcontent.rec was taken with
        // this walker in place, so the entries are here and the assertion has moved.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            Assert.Equal(173, endgame.Maps.Count);
            Assert.Equal(13, endgame.ContentArrays);

            Assert.NotEqual(0UL, endgame.ContentTable);
            Assert.Equal("EndgameMaps.MapContent", endgame.ContentTableRoute);
        }
    }

    [Fact]
    public void TheWalkReadsEveryRowAndEachOneNamesADifferentArea()
    {
        // 173 rows, 173 areas, none repeated - so "173 rows" and "173 maps" are the same number
        // here, which is not something a table has to do and therefore worth asserting rather
        // than assuming. Maps counts rows per area precisely so a client where they diverge says so.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            Assert.Equal(173, endgame.RowsNamed);
            Assert.Equal(173, endgame.Maps.Count);
            Assert.Empty(endgame.LastError);
            Assert.All(endgame.Maps.Values, rows => Assert.Equal(1, rows));
        }
    }

    [Fact]
    public void EveryMapItNamesIsOneWorldAreasAlsoHas()
    {
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            var areas = new WorldAreaCatalogue(replay, RealSessionTests.LiveSchema());
            OffsetSchema schema = RealSessionTests.LiveSchema();
            GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
            foreach (AtlasNode node in new AtlasReader(replay, schema, new UiElementReader(replay, schema))
                .Read(chain.UiRoot, new UiScale(3440, 1440, 0)))
            {
                if (node.MapId.Length > 0 && areas.ReadFromNode(node.Address))
                {
                    break;
                }
            }

            // Column 0 of an EndgameMaps row IS a WorldAreas reference, so this can only fail if
            // the column is not what it is believed to be - which is what makes it worth asserting.
            Assert.All(endgame.Maps.Keys, id => Assert.True(areas.All.ContainsKey(id), id));
        }
    }

    [Fact]
    public void EveryMapASessionSawOnTheAtlasIsInIt()
    {
        // GROUND TRUTH, and the only direction that can refute the table: an id read off a real
        // atlas node IS an atlas map, whatever any file says.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, IReadOnlyList<AtlasNode> nodes) = Walked();
        using (replay)
        {
            string[] seen =
                [.. nodes.Select(n => n.MapId).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
            Assert.NotEmpty(seen);

            string[] missing = [.. seen.Where(id => !endgame.Holds(id)).Order(StringComparer.Ordinal)];
            Assert.True(missing.Length == 0, $"seen on the atlas but not in EndgameMaps: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void ThingsThatAreObviouslyNotAtlasMapsAreInNeitherTheTableNorTheFile()
    {
        // These nine are what made the misnaming visible, and they used to be IN the file - that
        // was the point of this test. They are gone from it now, so it checks both halves: the
        // table never named them, and the file has stopped doing so.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            AtlasMapNames file = Names();
            foreach (string id in NotAtlasMaps)
            {
                Assert.False(endgame.Holds(id), $"{id} is not an atlas map and EndgameMaps should not name it");
                Assert.DoesNotContain(id, file.All.Keys, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ItSaysHowMuchOfTheFileIsNotAboutTheAtlasAtAll()
    {
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            string said = string.Join('\n', endgame.Describe(Names()));

            Assert.Contains("\"Data/Balance/EndgameMaps.dat\", 173 rows", said, StringComparison.Ordinal);
            Assert.Contains(
                "data/atlas-maps.json lists 173; 173 of this table's maps are in it, and 0 of its"
                + " entries are not atlas maps at all",
                said,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheReportCountsTheSameThingTheTabShows()
    {
        // The number a person reads off Inspect -> Map Data, pinned where it is computed.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, IReadOnlyList<AtlasNode> nodes) = Walked();
        using (replay)
        {
            AtlasMapNames names = Names();

            // READ FROM A NODE, not merely constructed. Without the walk the catalogue is empty and
            // the report's union is the file alone - every count below still holds, which is what
            // makes leaving it out a mistake that hides rather than fails.
            var areas = new WorldAreaCatalogue(replay, RealSessionTests.LiveSchema());
            foreach (AtlasNode node in nodes)
            {
                if (node.MapId.Length > 0 && areas.ReadFromNode(node.Address))
                {
                    break;
                }
            }

            Assert.Equal(442, areas.All.Count);
            MapDataReport report = MapDataReport.Build(
                areas, names, AtlasRatings.Empty, onAtlas: null, endgame: endgame);

            // NOUGHT is the finished state, and it is the number to watch from here: every entry
            // the file still carries is a map the atlas can reach. It went 267 -> 0 by the file
            // being cut, and it goes back above nought the moment a league adds an area somebody
            // writes into the file that EndgameMaps does not name.
            Assert.Equal(173, report.AtlasMaps);
            Assert.Equal(0, report.NotAtlasMaps);
            Assert.Equal(173, report.InFile);
            Assert.Contains("# atlas maps\t173\tfile entries that are not atlas maps\t0",
                report.ToText(), StringComparison.Ordinal);

            // Every rated map had better be one the atlas can send you to, or a rating is advice
            // about a hideout. G_login is still a ROW here - the union includes everything the game
            // knows - it simply is not a map and no longer pretends to be one in a file.
            Assert.True(report.Maps.Single(row => row.Id == "MapSunTemple").AtlasMap);
            MapDataRow login = report.Maps.Single(row => row.Id == "G_login");
            Assert.False(login.AtlasMap);
            Assert.False(login.InFile);
        }
    }

    /// <summary>
    /// The first capture whose rows actually read.
    /// </summary>
    /// <remarks>
    /// IT THROWS RATHER THAN RETURNING AN EMPTY ONE. With nothing read, Maps is empty and Holds
    /// answers false for everything - so every assertion resting on it would hold for the wrong
    /// reason, and a green test would say "answered, and the answer is yes". A capture whose build
    /// did not walk the table can only ever say "not answered", and this is where that is caught.
    /// </remarks>
    private static (EndgameMapCatalogue Catalogue, ReplayMemoryReader Replay, IReadOnlyList<AtlasNode> Nodes) Walked()
    {
        foreach (string fixture in Captures)
        {
            if (!File.Exists(Path.Combine(Root.FullName, "tests", "fixtures", fixture)))
            {
                continue;
            }

            ReplayMemoryReader replay = Load(fixture);
            EndgameMapCatalogue catalogue = Read(replay, out IReadOnlyList<AtlasNode> nodes);
            if (catalogue.Maps.Count > 0)
            {
                return (catalogue, replay, nodes);
            }

            replay.Dispose();
        }

        throw new InvalidOperationException(
            "no capture in Captures carries the EndgameMaps ROWS. A recording holds only the reads its"
            + " build performed, so a capture taken before this walker existed reaches the table and"
            + " stops there. Take a fresh --record with the atlas open and add it to Captures.");
    }
}
