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
/// SETTLED, AGAINST THE GAME. Four separate 0.5.5 captures reach "Data/Balance/EndgameMaps.dat" off
/// an atlas node and all four report 173 rows of 0xF1; the newest of them
/// (session-2026-09-endgamemaps.rec) was taken with this walker in place and carries the row block,
/// so the rows themselves are here: 173 rows naming 173 DIFFERENT areas. Against the file's 440
/// entries, 267 are areas the atlas can never send anybody to.
///
/// THE CHECK THAT COULD HAVE REFUTED IT PASSED. 140 map ids were read off real atlas nodes in that
/// capture and every single one is in the table - the only direction that can test it, because an
/// id on a node IS an atlas map whatever any file says. The three earlier captures still run the
/// table-identity check: "the table is 173 rows" is a claim about the GAME, and one recording can
/// only ever be a claim about that recording.
/// </remarks>
public class EndgameMapSessionTests
{
    /// <summary>
    /// Captures that reach the table, newest first. The walk runs against the first that has rows.
    /// </summary>
    /// <remarks>
    /// A LIST rather than one name, so a fresh capture turns the skipped tests on by being added
    /// here instead of by anything being rewritten.
    /// </remarks>
    private static readonly string[] Captures =
    [
        "session-2026-09-endgamemaps.rec",
        "session-2026-09-catalogue.rec",
        "session-2026-09-atlas.rec",
    ];

    /// <summary>Areas plainly in the file that the atlas can never send anybody to.</summary>
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
        // Three captures rather than one because "the table is 173 rows" is a claim about the
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
    public void ThingsThatAreObviouslyNotAtlasMapsAreNotInIt()
    {
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            AtlasMapNames file = Names();
            foreach (string id in NotAtlasMaps)
            {
                Assert.True(file.All.ContainsKey(id), $"{id} should be in the file - that is the point");
                Assert.False(endgame.Holds(id), $"{id} is not an atlas map and EndgameMaps should not name it");
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
                "data/atlas-maps.json lists 440; 173 of this table's maps are in it, and 267 of its"
                + " entries are not atlas maps at all",
                said,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheReportCountsTheSameThingTheTabShows()
    {
        // The number a person reads off Inspect -> Map Data, pinned where it is computed. 267 of
        // the file's 440 entries describe somewhere the atlas cannot reach - which is the whole
        // finding, in one integer.
        (EndgameMapCatalogue endgame, ReplayMemoryReader replay, _) = Walked();
        using (replay)
        {
            AtlasMapNames names = Names();
            var areas = new WorldAreaCatalogue(replay, RealSessionTests.LiveSchema());
            MapDataReport report = MapDataReport.Build(
                areas, names, AtlasRatings.Empty, onAtlas: null, endgame: endgame);

            Assert.Equal(173, report.AtlasMaps);
            Assert.Equal(267, report.NotAtlasMaps);
            Assert.Equal(440, report.InFile);
            Assert.Contains("# atlas maps\t173\tfile entries that are not atlas maps\t267",
                report.ToText(), StringComparison.Ordinal);

            // Every rated map had better be one the atlas can send you to, or a rating is advice
            // about a hideout.
            Assert.True(report.Maps.Single(row => row.Id == "MapSunTemple").AtlasMap);
            Assert.False(report.Maps.Single(row => row.Id == "G_login").AtlasMap);
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
