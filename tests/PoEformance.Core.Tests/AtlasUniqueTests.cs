using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which maps count as unique, now that the GAME answers it rather than data/atlas-maps.json.
/// </summary>
/// <remarks>
/// WHY THE SOURCE CHANGED. WorldAreas.dat carries IsUniqueMapArea, and read over the whole 442-row
/// table it disagrees with the shipped file on six ids IN BOTH DIRECTIONS. The file is a
/// hand-maintained port of that table; the column is what the client itself decides with. There is
/// no version of this where the hand-maintained copy is the better source.
///
/// WHAT IT CHANGES. Two things ask whether a map is unique, and both of them reach it through
/// AtlasMapNames.Of: the atlas grouping's catch-all group, and the rule that a ritual line may
/// never take a unique map. So the switch is one method, and these tests are what say so.
///
/// THE ONE THING THAT MUST NOT MOVE is what the file said. MapDataReport compares the two sources
/// and AtlasRatings resolves its names through the file's table; a reconciliation built on a table
/// that had already been corrected would cheerfully report that everything agrees.
/// </remarks>
public class AtlasUniqueTests
{
    /// <summary>The six ids the two sources part company on, measured over all 442 rows.</summary>
    private static readonly string[] GameSaysUnique =
        ["ExpeditionLeagueBoss", "RitualLeagueBoss", "MapVoidReliquary", "Map_HildaCampsite"];

    private static readonly string[] GameSaysOrdinary =
        ["MapUniqueInitialTower", "MapUniqueReactor_04"];

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

    /// <summary>A stand-in for what the table says, so a case can be made up rather than hunted.</summary>
    private static Dictionary<string, WorldArea> Areas(params (string Id, bool Unique)[] rows)
    {
        var areas = new Dictionary<string, WorldArea>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, bool unique) in rows)
        {
            areas[id] = new WorldArea(id, string.Empty, true, false, unique, []);
        }

        return areas;
    }

    [Fact]
    public void TheGamesFlagReplacesTheFilesInBothDirections()
    {
        AtlasMapNames names = Names();
        Assert.Equal(0, names.Revision);
        Assert.True(names.Of("MapUniqueReactor_04").Unique, "the file's word, before the game is asked");
        Assert.False(names.Of("MapVoidReliquary").Unique);

        int moved = names.LearnUnique(Areas(("MapUniqueReactor_04", false), ("MapVoidReliquary", true)));

        Assert.Equal(2, moved);
        Assert.False(names.Of("MapUniqueReactor_04").Unique);
        Assert.True(names.Of("MapVoidReliquary").Unique);
        Assert.Equal(1, names.Revision);
    }

    [Fact]
    public void ANDWhatTheFileSaidIsStillThereToBeComparedAgainst()
    {
        // The report's whole job. Correcting the table in place would make it announce that the
        // two sources agree everywhere, which is the one answer it must never be able to give.
        AtlasMapNames names = Names();
        int before = names.Count;

        names.LearnUnique(Areas(("MapUniqueReactor_04", false), ("MapUnheardOf", true)));

        Assert.True(names.All["MapUniqueReactor_04"].Unique);
        Assert.Equal(before, names.Count);
        Assert.DoesNotContain("MapUnheardOf", names.All.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMapOnlyTheGameCallsUniqueStillFallsIntoTheUniqueGroup()
    {
        // A league's new unique map is in the table before it is in the file. Falling PAST the
        // catch-all is the failure; showing the raw id is how somebody notices the file is behind.
        AtlasMapNames names = Names();

        names.LearnUnique(Areas(("MapNextLeagueUnique", true), ("MapNextLeagueOrdinary", false)));

        Assert.True(names.Of("MapNextLeagueUnique").Unique);
        Assert.Equal("MapNextLeagueUnique", names.Called("MapNextLeagueUnique"));
        Assert.Equal(string.Empty, names.Of("MapNextLeagueUnique").Name);

        // An ordinary area is NOT invented: the table holds hideouts, campaign zones and hubs, and
        // an entry for each of them would only be a row saying "nothing known" in a longer way.
        Assert.Equal(AtlasMapInfo.Unknown, names.Of("MapNextLeagueOrdinary"));
    }

    [Fact]
    public void ANEmptyTableCanBeTaughtWithoutTeachingEverySiblingOfIt()
    {
        // Empty used to be a singleton, which was harmless while nothing could change one. It is
        // not harmless now: a shared instance anything can teach carries one client's table into
        // every other holder of it - the ritual watch's fallback, the grouping's None, and every
        // test that asked for a blank one.
        AtlasMapNames mine = AtlasMapNames.Empty;
        AtlasMapNames yours = AtlasMapNames.Empty;

        mine.LearnUnique(Areas(("MapVoidReliquary", true)));

        Assert.True(mine.Of("MapVoidReliquary").Unique);
        Assert.Equal(AtlasMapInfo.Unknown, yours.Of("MapVoidReliquary"));
        Assert.Equal(0, yours.Revision);
    }

    [Fact]
    public void NOTHINGBUTTheFlagMoves()
    {
        // The names in the table are in the CLIENT'S language and this file's are English on
        // purpose - the ratings resolve through them - and the tags share no vocabulary with the
        // curated words at all. Only the column that was measured to be better changes.
        AtlasMapNames names = Names();
        AtlasMapInfo before = names.Of("MapLostTowers");

        names.LearnUnique(Areas(("MapLostTowers", true)));
        AtlasMapInfo after = names.Of("MapLostTowers");

        Assert.Equal("Lost Towers", after.Name);
        Assert.Equal(before.Tags, after.Tags);
        Assert.True(after.Unique);
    }

    [Fact]
    public void TheGroupingForgetsADecisionItTookBeforeTheGameWasAsked()
    {
        // THE BUG THIS EXISTS FOR. The table is reachable only through an atlas node, so it
        // arrives AFTER the first reads have already decided - and cached, for the session - that
        // six maps belong where the file put them.
        AtlasMapNames names = Names();
        var grouping = new AtlasGrouping(DefaultAtlasGroups.Groups, names);

        Assert.Equal("Unique maps", grouping.Of("MapUniqueReactor_04")?.Name);
        Assert.Null(grouping.Of("MapVoidReliquary"));

        names.LearnUnique(Areas(("MapUniqueReactor_04", false), ("MapVoidReliquary", true)));

        Assert.Null(grouping.Of("MapUniqueReactor_04"));
        Assert.Equal("Unique maps", grouping.Of("MapVoidReliquary")?.Name);
    }

    [Fact]
    public void ARitualLineFollowsTheGameToo()
    {
        // The other consumer, and the one with a consequence a player sees: a line planned onto a
        // map the game calls unique is a route the game refuses to let anybody walk.
        AtlasMapNames names = Names();
        AtlasNode[] atlas =
        [
            Node(0, 0, "MapVoidReliquary"),
            Node(1, 0, "MapUniqueReactor_04"),
        ];

        Assert.Equal([(1, 0)], RitualWatch.Blocked(atlas, names));
        Assert.Equal([(0, 0)], RitualWatch.Startable(atlas, names));

        names.LearnUnique(Areas(("MapUniqueReactor_04", false), ("MapVoidReliquary", true)));

        Assert.Equal([(0, 0)], RitualWatch.Blocked(atlas, names));
        Assert.Equal([(1, 0)], RitualWatch.Startable(atlas, names));
    }

    [Fact]
    public void AgainstTheRealTableExactlySixMapsMoveAndTheyAreTheOnesMeasured()
    {
        // The same six the report names, taken here through the switch itself rather than through
        // the comparison - so the two cannot drift apart without one of them failing.
        AtlasMapNames names = Names();
        WorldAreaCatalogue catalogue = Catalogue();

        int moved = names.LearnUnique(catalogue.All);

        Assert.Equal(6, moved);
        Assert.Equal(1, names.Revision);
        Assert.All(GameSaysUnique, id => Assert.True(names.Of(id).Unique, id));
        Assert.All(GameSaysOrdinary, id => Assert.False(names.Of(id).Unique, id));
        Assert.All(GameSaysUnique, id => Assert.False(names.All[id].Unique, id));
        Assert.All(GameSaysOrdinary, id => Assert.True(names.All[id].Unique, id));
    }

    [Fact]
    public void ANDTheFourThatGainItJoinTheUniqueGroupWhileTheTwoThatLoseItLeave()
    {
        // What the change is actually FOR. Four maps start being drawn as unique and two stop,
        // and the two that stop are the ones whose ids say "Unique" in them - which is exactly
        // why the file had them wrong and why a name is not a source.
        AtlasMapNames names = Names();
        var grouping = new AtlasGrouping(DefaultAtlasGroups.Groups, names);

        names.LearnUnique(Catalogue().All);

        // RitualLeagueBoss is claimed by the Ritual group first, which names it outright - first
        // match wins, and that is the decision the group order is making. ExpeditionLeagueBoss is
        // NOT claimed by the Expedition group: the file gives it no tags at all, so the catch-all
        // is the only thing that was ever going to hold it - which is the point of a catch-all.
        Assert.Equal("Ritual", grouping.Of("RitualLeagueBoss")?.Name);
        Assert.Equal("Unique maps", grouping.Of("ExpeditionLeagueBoss")?.Name);
        Assert.Equal("Unique maps", grouping.Of("MapVoidReliquary")?.Name);
        Assert.Equal("Unique maps", grouping.Of("Map_HildaCampsite")?.Name);

        Assert.Null(grouping.Of("MapUniqueInitialTower"));
        Assert.Null(grouping.Of("MapUniqueReactor_04"));
    }

    [Fact]
    public void TheReportSaysWhichFlagIsInForce()
    {
        // Before the table is read the file still decides, and a tab claiming otherwise would be
        // reporting on a switch that has not happened yet.
        AtlasMapNames names = Names();
        AtlasRatings ratings = AtlasRatings.Load(Path.Combine(Root.FullName, "data", "atlas-ratings.json"), names);
        WorldAreaCatalogue catalogue = Catalogue();

        Assert.False(MapDataReport.Build(catalogue, names, ratings).UniqueFromGame);

        names.LearnUnique(catalogue.All);
        MapDataReport after = MapDataReport.Build(catalogue, names, ratings);

        Assert.True(after.UniqueFromGame);
        Assert.Equal(6, after.UniqueDiffers);
        Assert.True(after.UniqueNow(after.Maps.Single(row => row.Id == "MapVoidReliquary")));
        Assert.False(after.UniqueNow(after.Maps.Single(row => row.Id == "MapUniqueReactor_04")));
        Assert.Contains("unique in force\tgame", after.ToText(), StringComparison.Ordinal);
    }

    /// <summary>The real 442-row table, off the committed capture.</summary>
    private static WorldAreaCatalogue Catalogue()
    {
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(
            File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-catalogue.rec")));

        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        var catalogue = new WorldAreaCatalogue(replay, schema);

        foreach (AtlasNode node in new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0)))
        {
            if (node.MapId.Length > 0 && catalogue.ReadFromNode(node.Address))
            {
                break;
            }
        }

        Assert.NotEmpty(catalogue.All);
        return catalogue;
    }

    private static AtlasNode Node(int x, int y, string mapId)
        => new(
            Index: (x * 100) + y,
            Address: 0x1000,
            MapId: mapId,
            Grid: (x, y),
            State: AtlasNodeState.Open,
            Biome: 0,
            Connections: [],
            Screen: default,
            Size: default,
            BadgeIds: [],
            ContentTokens: []);
}
