using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Sorting the atlas into the kinds of map somebody goes looking for.
/// </summary>
/// <remarks>
/// The shipped groups are checked against the shipped DATA rather than against themselves,
/// because that is where this can go wrong: a group naming a map id that is not in the table is
/// a group that silently matches nothing, and it looks perfectly correct in the source.
/// </remarks>
public class AtlasGroupTests
{
    private static string DataFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas-maps.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "data", "atlas-maps.json");
    }

    private static AtlasMapNames Names() => AtlasMapNames.Load(DataFile());

    private static AtlasGrouping Shipped() => new(DefaultAtlasGroups.Groups, Names());

    [Fact]
    public void AMapIsCalledWhatTheGameCallsIt()
    {
        AtlasMapNames names = Names();

        Assert.Equal("Lost Towers", names.Called("MapLostTowers"));
        Assert.Equal("The Copper Citadel", names.Called("MapUberBoss_CopperCitadel"));
        Assert.True(names.Of("ExpeditionLogBook_Heath").Unique);
    }

    [Fact]
    public void ANDAMapNobodyWroteDownStillHasSomethingToShow()
    {
        // A league adds maps this file has not heard of. The answer is the raw id on the
        // screen - which is also how somebody notices the file needs a line adding.
        AtlasMapNames names = Names();

        Assert.Equal(AtlasMapInfo.Unknown, names.Of("MapSomethingNewNextLeague"));
        Assert.Equal("MapSomethingNewNextLeague", names.Called("MapSomethingNewNextLeague"));
        Assert.Equal(string.Empty, names.Called(null));
    }

    [Fact]
    public void EVERYShippedGroupActuallyMatchesSomething()
    {
        // The failure this catches: a group listing an id that is spelled wrong, or that the
        // data file does not carry. Nothing about it looks broken - it simply never appears.
        AtlasMapNames names = Names();

        foreach (AtlasGroup group in DefaultAtlasGroups.Groups)
        {
            Assert.False(group.SaysNothing, $"{group.Name} names no maps at all");

            foreach (string id in group.Maps ?? [])
            {
                Assert.True(
                    names.Of(id).Name.Length > 0,
                    $"{group.Name} names {id}, which is not in the map table");
            }
        }
    }

    [Fact]
    public void ANDTHETagDataAloneWouldNotHaveDoneIt()
    {
        // Why groups can name their maps at all, kept as a test because it is the surprising
        // half. The maps players call towers are these five; only ONE carries the game's own
        // "tower" tag, and that tag also covers the Precursor Towers, which are a different
        // thing. A grouping built on tags alone would get this wrong in both directions.
        AtlasMapNames names = Names();

        string[] towers = ["MapBluff", "MapMesa", "MapSwampTower", "MapAlpineRidge", "MapLostTowers"];
        int tagged = towers.Count(id => names.Of(id).Tagged("tower"));
        Assert.Equal(1, tagged);

        Assert.True(names.Of("MapPrecursorTower").Tagged("tower"));

        AtlasGrouping grouping = Shipped();
        foreach (string id in towers)
        {
            Assert.Equal("Towers", grouping.Of(id)?.Name);
        }

        Assert.NotEqual("Towers", grouping.Of("MapPrecursorTower")?.Name);
    }

    [Fact]
    public void AGroupCanBeTheGamesOwnTag()
    {
        // The other half: where the tag says what somebody means, the tag is the better rule,
        // because it keeps working when a league adds another one.
        AtlasGrouping grouping = Shipped();

        Assert.Equal("Citadels", grouping.Of("MapUberBoss_CopperCitadel")?.Name);
        Assert.Equal("Citadels", grouping.Of("MapMothersoul_Female")?.Name);
        Assert.Equal("Expedition", grouping.Of("ExpeditionLogBook_Prairie")?.Name);
        Assert.Equal("Lineage", grouping.Of("MapDerelictMansion")?.Name);
    }

    [Fact]
    public void THEFirstMatchingGroupWins()
    {
        // A citadel is also a unique map, and it is worth seeing as a citadel - so the order
        // of the list is a decision. Checked with a made-up pair rather than by trusting the
        // shipped order to stay put.
        var names = AtlasMapNames.Empty;
        var first = new AtlasGroup("First", "#FFFFFF", Maps: ["MapThing"]);
        var second = new AtlasGroup("Second", "#000000", Maps: ["MapThing"]);

        Assert.Equal("First", new AtlasGrouping([first, second], names).Of("MapThing")?.Name);
        Assert.Equal("Second", new AtlasGrouping([second, first], names).Of("MapThing")?.Name);
    }

    [Fact]
    public void ANDNoGroupIsAnAnswerTooRatherThanAnAccident()
    {
        AtlasGrouping grouping = Shipped();

        // An ordinary map belongs to nothing, which is what keeps an atlas readable.
        Assert.Null(grouping.Of("MapAugury"));
        Assert.Null(grouping.Of(string.Empty));
    }

    [Fact]
    public void ADisabledGroupHoldsNothing()
    {
        var group = new AtlasGroup("Off", "#FFFFFF", Maps: ["MapThing"], Enabled: false);
        Assert.False(group.Holds("MapThing", AtlasMapInfo.Unknown));

        var on = group with { Enabled = true };
        Assert.True(on.Holds("MapThing", AtlasMapInfo.Unknown));
    }

    [Fact]
    public void AGroupThatNamesNothingIsCalledOutRatherThanMatchingEverything()
    {
        // The shape that arrives from a hand-edited file: a group with a name, a colour and
        // no rule at all. Matching everything would repaint the whole atlas.
        var empty = new AtlasGroup("Nothing", "#FFFFFF");

        Assert.True(empty.SaysNothing);
        Assert.False(empty.Holds("MapAnything", AtlasMapInfo.Unknown));
        Assert.False(empty.Holds("MapUniqueReactor_04", new AtlasMapInfo("Site of the Chosen", true, [])));
    }

    [Fact]
    public void THEUniqueCatchAllTakesWhateverTheNamedGroupsLeft()
    {
        AtlasGrouping grouping = Shipped();

        // Site of the Chosen is unique and in no named group, so it lands in the catch-all.
        Assert.Equal("Unique maps", grouping.Of("MapUniqueReactor_04")?.Name);

        // The gateways are unique too, and they are Progression first.
        Assert.Equal("Progression", grouping.Of("MapUniqueReactor_01")?.Name);
    }

    [Fact]
    public void AFinishedMapIsNotSomewhereToBeSent()
    {
        // Routing asks about the state as well as the group, because "interesting" and "worth
        // walking to" stop being the same thing the moment a map has been run.
        var groups = new[] { new AtlasGroup("Citadels", "#FF4040", Tag: "arbiter", Route: true) };
        var grouping = new AtlasGrouping(groups, Names());

        Assert.True(grouping.RouteTo("MapUberBoss_CopperCitadel", AtlasNodeState.Open, out AtlasGroup? open));
        Assert.Equal("Citadels", open?.Name);

        Assert.False(grouping.RouteTo("MapUberBoss_CopperCitadel", AtlasNodeState.Completed, out AtlasGroup? done));
        Assert.Equal("Citadels", done?.Name);   // still grouped, just not routed to
    }

    [Fact]
    public void ANDNothingRoutesUntilSomebodyAsksForIt()
    {
        // Ten sets of lines across an atlas is not ten times the help. If this ever fails,
        // somebody has shipped a default that draws the screen full on first run.
        Assert.All(DefaultAtlasGroups.Groups, group => Assert.False(group.Route));
    }

    [Fact]
    public void SETTINGSKeepTheDifferenceBetweenUnsetAndEmptied()
    {
        // Absent means "never chosen" and takes the shipped list; empty means "group nothing"
        // and is honoured. A list that grows its defaults back cannot be emptied.
        Assert.Equal(DefaultAtlasGroups.Groups, AtlasSettings.Default.Sorting);
        Assert.Empty(new AtlasSettings(Groups: []).Sorting);
    }

    [Fact]
    public void SETTINGSSurviveBeingWrittenAndReadBack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"atlas-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AtlasSettings(
                Web: true,
                Search: "citadel",
                Groups: [new AtlasGroup("Mine", "#123456", Tag: "arbiter", Route: true, MaxHops: 4)]);

            Assert.True(AtlasStore.Save(settings, path));

            AtlasSettings back = AtlasStore.Load(path);
            Assert.True(back.Web);
            Assert.Equal("citadel", back.Search);
            AtlasGroup group = Assert.Single(back.Sorting);
            Assert.Equal("Mine", group.Name);
            Assert.Equal("#123456", group.Colour);
            Assert.True(group.Route);
            Assert.Equal(4, group.MaxHops);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDAMissingOrBrokenFileLeavesTheShippedOnes()
    {
        Assert.Equal(AtlasSettings.Default, AtlasStore.Load(Path.Combine(Path.GetTempPath(), "no-atlas-here.json")));

        string path = Path.Combine(Path.GetTempPath(), $"atlas-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Equal(AtlasSettings.Default, AtlasStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
