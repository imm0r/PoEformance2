using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The report that has to exist BEFORE data/atlas-maps.json is touched, against the real table
/// (<c>tests/fixtures/session-2026-09-catalogue.rec</c>).
/// </summary>
/// <remarks>
/// WHAT IT IS FOR. Moving the atlas onto values read out of memory replaces things a person can
/// open in an editor with things nobody can see. This report is what keeps them visible, and these
/// tests pin the numbers it produces on a real 442-row table so a later client changing one of
/// them shows up as a failure rather than as a surprise in the overlay.
///
/// THE RATINGS ARE THE PART THAT CAN BREAK QUIETLY, and the report was built around them.
/// data/atlas-ratings.json is written by display name and resolved to ids at load through
/// data/atlas-maps.json's ENGLISH names - never through the game's translated ones - so the
/// ratings already survive a German client. What they do not survive is the file's name column
/// being cut, because that column is their lookup table. The report says, per rating, whether the
/// game supplies the same name, which is exactly the question "can this line survive the cut".
/// </remarks>
public class MapDataReportTests
{
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

    private static AtlasMapNames Names => AtlasMapNames.Load(Path.Combine(Root.FullName, "data", "atlas-maps.json"));

    private static AtlasRatings Ratings(AtlasMapNames names)
        => AtlasRatings.Load(Path.Combine(Root.FullName, "data", "atlas-ratings.json"), names);

    /// <summary>The catalogue off the real table, read the only way there is.</summary>
    private static WorldAreaCatalogue Read(ReplayMemoryReader replay, out HashSet<string> onAtlas)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        List<AtlasNode> nodes = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));

        onAtlas = [.. nodes.Select(n => n.MapId).Where(id => id.Length > 0)];
        var catalogue = new WorldAreaCatalogue(replay, schema);
        foreach (AtlasNode node in nodes)
        {
            if (node.MapId.Length > 0 && catalogue.ReadFromNode(node.Address))
            {
                break;
            }
        }

        return catalogue;
    }

    private static MapDataReport Build()
    {
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(
            File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-catalogue.rec")));

        AtlasMapNames names = Names;
        WorldAreaCatalogue catalogue = Read(replay, out HashSet<string> onAtlas);
        return MapDataReport.Build(catalogue, names, Ratings(names), onAtlas);
    }

    [Fact]
    public void ItHoldsEveryMapEitherSideKnows()
    {
        // 442 areas in the game, 173 in the file, and the union is what a reconciliation needs: a
        // map missing from one side has to be a ROW saying so, not a gap nobody looks at.
        //
        // THE UNION IS NOW THE TABLE. It was 444 - the file carried two showcase ids WorldAreas has
        // never had - and cutting the file to the 173 maps EndgameMaps.dat names took both with
        // them. Every entry the file still has is an area the game really holds.
        MapDataReport report = Build();

        Assert.Equal(442, report.InGame);
        Assert.Equal(173, report.InFile);
        Assert.Equal(442, report.Maps.Count);
        Assert.Contains("\"Data/Balance/WorldAreas.dat\", 442 rows of 0x2E0", report.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNamesAgreeAndTheTrailingSpaceIsStillTrimmed()
    {
        // P2_3 reads "Sel Khari Sanctuary " in the game, and the trimming is why no name is ever
        // reported as differing over a space. It is a campaign zone, so since the file was cut to
        // the atlas's 173 maps it appears here as a GameOnly row - the trim still has to happen,
        // and this is where it is checked.
        MapDataReport report = Build();

        Assert.Equal(0, report.NamesDiffer);
        MapDataRow p2 = report.Maps.Single(row => row.Id == "P2_3");
        Assert.Equal("Sel Khari Sanctuary", p2.GameName);
        Assert.Equal(Agreement.GameOnly, p2.Name);
        Assert.False(p2.InFile);
    }

    [Fact]
    public void UniqueIsReportedAsTheGamesAnswerRatherThanAsAComparison()
    {
        // There is nothing left to compare. The file's "type": "unique" key was removed once the
        // game's IsUniqueMapArea was in force, so this column is simply what the table says - 25
        // of its 442 areas - and the two ids whose names promise otherwise are the check worth
        // keeping, because they are why the hand-kept copy was wrong.
        MapDataReport report = Build();

        Assert.Equal(25, report.Uniques);
        Assert.True(report.Maps.Single(row => row.Id == "MapVoidReliquary").GameUnique);
        Assert.False(report.Maps.Single(row => row.Id == "MapUniqueInitialTower").GameUnique);
        Assert.False(report.Maps.Single(row => row.Id == "MapUniqueReactor_04").GameUnique);
    }

    [Fact]
    public void EveryRatingResolvesAndTheGameSuppliesEveryNameBehindThem()
    {
        // THE ANSWER THE OWNER ASKED FOR. Every line of the ratings file lands on a map, and for
        // every one of them the GAME supplies the same name - so the lookup could be rebuilt from
        // memory and the file's name column is not load-bearing for the ratings after all.
        MapDataReport report = Build();

        Assert.Equal(83, report.Ratings.Count);
        Assert.Equal(0, report.RatingsUnresolved);
        Assert.Equal(0, report.RatingsNeedingTheFile);
        Assert.All(report.Ratings, rating => Assert.True(rating.Resolves && rating.FromGameToo, rating.Name));
    }

    [Fact]
    public void ARatingOnANameNoMapCarriesIsReportedRatherThanDropped()
    {
        // A typo in the ratings file is otherwise a rating that silently never appears.
        AtlasMapNames names = Names;
        var ratings = AtlasRatings.Resolve(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Ice Cave"] = 6, ["Not A Map"] = 3 },
            names);

        MapDataReport report = MapDataReport.Build(null, names, ratings);

        Assert.Equal(1, report.RatingsUnresolved);
        RatingRow orphan = report.Ratings.Single(row => !row.Resolves);
        Assert.Equal("Not A Map", orphan.Name);
        Assert.Empty(orphan.Ids);
    }

    [Fact]
    public void WithoutACatalogueItStillReportsTheFileRatherThanNothing()
    {
        // The state the tab is in before an atlas has been opened. Saying "not read yet" and
        // showing the file is more use than an empty panel that looks broken.
        AtlasMapNames names = Names;
        MapDataReport report = MapDataReport.Build(null, names, Ratings(names));

        Assert.Equal(0, report.InGame);
        Assert.Equal(173, report.InFile);
        Assert.Contains("has not been read", report.Source, StringComparison.Ordinal);
        Assert.All(report.Maps, row => Assert.Equal(Agreement.FileOnly, row.Name));
    }

    [Fact]
    public void TheTextExportCarriesTheHeaderCountsAndEveryRow()
    {
        // Four hundred rows are checked in a spreadsheet, not in an overlay. The counts ride along
        // so a saved file says what it is without being opened next to the tool that made it.
        string text = Build().ToText();

        Assert.Contains("# maps\t442\tin game\t442\tin file\t173", text, StringComparison.Ordinal);
        Assert.Contains("# ratings\t83\tunresolved\t0\tneeding the file\t0", text, StringComparison.Ordinal);
        Assert.Contains("id\tgame name\tfile name\tname\t", text, StringComparison.Ordinal);
        Assert.Contains("MapUniqueReactor_04\t", text, StringComparison.Ordinal);
        // GameOnly, not Differ: after the cut the two sources agree on every name they share, and
        // the 269 areas only the game has are what the column reports instead.
        Assert.Contains("\tGameOnly\t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\tDiffer\t", text, StringComparison.Ordinal);

        // One line per map between the two section headers. Counted by position rather than by
        // what an id looks like: barely a third begin with "Map" and the rest are Expedition,
        // Hideout, G1_ and the like, which is what the first version of this assertion missed.
        string[] lines = text.Split('\n');
        int maps = Array.FindIndex(lines, line => line.StartsWith("id\tgame name", StringComparison.Ordinal));
        int ratings = Array.FindIndex(lines, line => line.StartsWith("rating\tvalue", StringComparison.Ordinal));
        Assert.True(maps > 0 && ratings > maps, "the export is missing one of its section headers");

        Assert.Equal(442, lines[(maps + 1)..ratings].Count(line => line.Contains('\t', StringComparison.Ordinal)));
        Assert.Equal(83, lines[(ratings + 1)..].Count(line => line.Contains('\t', StringComparison.Ordinal)));
    }
}
