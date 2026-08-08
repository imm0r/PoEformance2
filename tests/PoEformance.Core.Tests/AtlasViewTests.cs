using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Turning a read of the atlas into what gets drawn on it.
/// </summary>
/// <remarks>
/// Everything checked here is a DECISION rather than an address: what order the hiding happens
/// in, whether a route past its hop limit still leaves its map on the atlas, whether a mutual
/// connection is drawn once or twice. None of it can be reached through a live read, and all of
/// it is the kind of thing that looks right and is quietly wrong.
/// </remarks>
public class AtlasViewTests
{
    private static AtlasNode Node(
        int x,
        int y,
        string mapId = "MapAugury",
        AtlasNodeState state = AtlasNodeState.Locked,
        (int X, int Y)[]? joined = null,
        uint[]? badges = null,
        uint[]? tokens = null)
        => new(
            Index: (x * 100) + y,
            Address: 0x1000,
            MapId: mapId,
            Grid: (x, y),
            State: state,
            Biome: 0,
            Connections: joined ?? [],
            // A position and a size, so the centre is somewhere checkable rather than zero.
            Screen: new Vector2(x * 100, y * 100),
            Size: new Vector2(40, 20),
            BadgeIds: badges ?? [],
            ContentTokens: tokens ?? []);

    private static readonly Dictionary<(int X, int Y), IReadOnlyList<string>> NoWords = [];

    private static AtlasGrouping Grouping(params AtlasGroup[] groups)
        => new(groups, AtlasMapNames.Empty);

    private static AtlasView Compose(
        IReadOnlyList<AtlasNode> live,
        AtlasSettings? settings = null,
        AtlasGrouping? grouping = null,
        AtlasRoutes? routes = null,
        Dictionary<(int X, int Y), IReadOnlyList<string>>? words = null)
        => AtlasWatch.Compose(
            live,
            settings ?? AtlasSettings.Default,
            grouping ?? AtlasGrouping.None,
            routes ?? AtlasRoutes.None,
            words ?? NoWords);

    [Fact]
    public void AMarkSitsAtTheMiddleOfItsNodeRatherThanItsCorner()
    {
        // The label goes on the map, and a node is a box: drawn from its top-left it would sit
        // up and to the left of everything it names.
        AtlasView view = Compose([Node(1, 2)]);

        AtlasMark mark = Assert.Single(view.Marks);
        Assert.Equal(new Vector2(100 + 20, 200 + 10), mark.Where);
    }

    [Fact]
    public void AFinishedMapIsLeftOutUNLESSSomethingIsRoutingToIt()
    {
        AtlasNode done = Node(1, 1, state: AtlasNodeState.Completed);

        Assert.Empty(Compose([done]).Marks);
        Assert.Single(Compose([done], new AtlasSettings(HideCompleted: false)).Marks);
    }

    [Fact]
    public void ANDHidingHappensAFTERTheRoutingQuestion()
    {
        // The trap the reference records: a map worth routing to is one nobody has reached, so
        // culling the unreachable BEFORE asking about routes hides exactly the routes somebody
        // turned on - and the feature looks broken while every setting reads correct.
        AtlasNode target = Node(2, 0, "MapUberBoss_CopperCitadel", AtlasNodeState.Locked);
        AtlasNode here = Node(0, 0, "MapAugury", AtlasNodeState.Open, joined: [(1, 0)]);
        AtlasNode between = Node(1, 0, "MapArroyo", AtlasNodeState.Locked, joined: [(0, 0), (2, 0)]);
        target = target with { Connections = [(1, 0)] };

        var groups = new[] { new AtlasGroup("Citadels", "#FF4040", Maps: ["MapUberBoss_CopperCitadel"], Route: true) };
        AtlasRoutes routes = AtlasRoutes.From([here, between, target]);

        AtlasView view = Compose(
            [here, between, target],
            new AtlasSettings(HideUnreachable: true),
            Grouping(groups),
            routes);

        // Hiding unreachable maps removes the one in between, which is the point of the
        // setting - but the routed target stays, with its way there drawn through the gap.
        Assert.DoesNotContain(view.Marks, mark => mark.MapId == "MapArroyo");

        AtlasMark citadel = Assert.Single(view.Marks, mark => mark.MapId == "MapUberBoss_CopperCitadel");
        Assert.Equal(2, citadel.Hops);
        Assert.Equal(3, citadel.Route.Count);

        // And with routing switched off, the same setting does hide it - so the exception
        // above is the routing, not the hiding quietly not working.
        var quiet = new[] { groups[0] with { Route = false } };
        AtlasView hidden = Compose(
            [here, between, target],
            new AtlasSettings(HideUnreachable: true),
            Grouping(quiet),
            routes);

        Assert.DoesNotContain(hidden.Marks, mark => mark.MapId == "MapUberBoss_CopperCitadel");
    }

    [Fact]
    public void AMapPastItsHopLimitKeepsItsLabelAndLosesItsLine()
    {
        // "Too far to walk to" is a statement about the ROUTE. Dropping the map as well would
        // make a hop limit into a second, surprising way of hiding maps.
        AtlasNode here = Node(0, 0, "MapAugury", AtlasNodeState.Open, joined: [(1, 0)]);
        AtlasNode between = Node(1, 0, "MapArroyo", AtlasNodeState.Locked, joined: [(0, 0), (2, 0)]);
        AtlasNode far = Node(2, 0, "MapFar", AtlasNodeState.Locked, joined: [(1, 0)]);
        AtlasRoutes routes = AtlasRoutes.From([here, between, far]);

        var tight = new[] { new AtlasGroup("Far", "#FFFFFF", Maps: ["MapFar"], Route: true, MaxHops: 1) };
        AtlasMark mark = Assert.Single(
            Compose([here, between, far], AtlasSettings.Default, Grouping(tight), routes).Marks,
            found => found.MapId == "MapFar");

        Assert.Empty(mark.Route);
        Assert.Equal(2, mark.Hops);   // still says how far, so the limit explains itself

        var loose = new[] { new AtlasGroup("Far", "#FFFFFF", Maps: ["MapFar"], Route: true, MaxHops: 2) };
        AtlasMark within = Assert.Single(
            Compose([here, between, far], AtlasSettings.Default, Grouping(loose), routes).Marks,
            found => found.MapId == "MapFar");

        Assert.Equal(3, within.Route.Count);
    }

    [Fact]
    public void ARouteThroughSomewhereScrolledOffTheAtlasKeepsGoing()
    {
        // A route can pass through a node the atlas is not drawing this tick. Dropping the
        // point cuts a corner; dropping the route makes the line flicker out whenever it
        // crosses the edge of the screen.
        AtlasNode here = Node(0, 0, "MapAugury", AtlasNodeState.Open, joined: [(1, 0)]);
        AtlasNode between = Node(1, 0, "MapArroyo", AtlasNodeState.Locked, joined: [(0, 0), (2, 0)]);
        AtlasNode far = Node(2, 0, "MapFar", AtlasNodeState.Locked, joined: [(1, 0)]);

        // Routed with the whole atlas, composed with the middle one missing.
        AtlasRoutes routes = AtlasRoutes.From([here, between, far]);
        var groups = new[] { new AtlasGroup("Far", "#FFFFFF", Maps: ["MapFar"], Route: true) };

        AtlasMark mark = Assert.Single(
            Compose([here, far], AtlasSettings.Default, Grouping(groups), routes).Marks,
            found => found.MapId == "MapFar");

        Assert.Equal(2, mark.Route.Count);
        Assert.Equal(2, mark.Hops);
    }

    [Fact]
    public void ASearchLeavesOnlyWhatWasSearchedFor()
    {
        var names = AtlasMapNames.Empty;
        AtlasNode[] live = [Node(0, 0, "MapAugury"), Node(1, 0, "MapArroyo")];

        // With no name table the label falls back to the id, which is what gets searched.
        AtlasView found = Compose(live, new AtlasSettings(Search: "arroyo"), new AtlasGrouping([], names));
        Assert.Equal("MapArroyo", Assert.Single(found.Marks).MapId);

        // Whitespace is not a search. Trimming it here is what stops a stray space in the box
        // from emptying the atlas.
        Assert.Equal(2, Compose(live, new AtlasSettings(Search: "   ")).Marks.Count);
    }

    [Fact]
    public void THEWebDrawsEachConnectionOnceRatherThanFromBothEnds()
    {
        // Connections are mutual, so both ends list each other. Drawn as they come, every line
        // on the atlas is on top of itself - twice the cost for exactly the same picture.
        AtlasNode left = Node(0, 0, joined: [(1, 0)]);
        AtlasNode right = Node(1, 0, joined: [(0, 0)]);

        AtlasView view = Compose([left, right], new AtlasSettings(Web: true));

        Assert.Single(view.Web);
        Assert.Empty(Compose([left, right]).Web);
    }

    [Fact]
    public void ANDALineToSomewhereNotDrawnIsNotDrawnEither()
    {
        AtlasNode alone = Node(0, 0, joined: [(9, 9)]);
        Assert.Empty(Compose([alone], new AtlasSettings(Web: true)).Web);
    }

    [Fact]
    public void CONTENTSAreSaidOnceEvenWhenTheGameSaysThemTwice()
    {
        // A breach arrives as a badge AND as a token. Listing it twice announces that the port
        // has two tables, not that the map has two breaches.
        AtlasContentNames contents = LoadedContents();

        // 0x0065 is Breach in both tables.
        AtlasNode node = Node(0, 0, badges: [0x0065], tokens: [0x0065]);
        IReadOnlyList<string> said = AtlasWatch.Words(node, contents);

        Assert.Equal(["Breach"], said);
    }

    [Fact]
    public void ANDHowManyOfThemIsKeptRatherThanFoldedAway()
    {
        // "Ritual x3" is a different map from "Ritual", so the de-duplication has to be on the
        // finished words - on the id alone it would throw the count away.
        AtlasContentNames contents = LoadedContents();

        AtlasNode node = Node(0, 0, badges: [0x0068, 0x0003_0068]);
        IReadOnlyList<string> said = AtlasWatch.Words(node, contents);

        Assert.Equal(2, said.Count);
        Assert.Contains("Ritual", said);
        Assert.Contains("Ritual x3", said);
    }

    [Fact]
    public void ANDSomethingTheTableHasNeverHeardOfIsLeftOutRatherThanShownAsANumber()
    {
        AtlasNode node = Node(0, 0, badges: [0xBEEF]);
        Assert.Empty(AtlasWatch.Words(node, LoadedContents()));
    }

    [Fact]
    public void CONTENTSCanBeTurnedOffWithoutTurningOffTheAtlas()
    {
        var words = new Dictionary<(int X, int Y), IReadOnlyList<string>> { [(0, 0)] = ["Breach"] };

        Assert.Equal(["Breach"], Compose([Node(0, 0)], words: words).Marks[0].Contents);
        Assert.Empty(Compose([Node(0, 0)], new AtlasSettings(Contents: false), words: words).Marks[0].Contents);
    }

    [Fact]
    public void ANAtlasWithNothingWorthDrawingSaysSoRatherThanLookingBroken()
    {
        Assert.False(AtlasView.Closed.Anything);
        Assert.Equal("atlas closed", AtlasView.Closed.Status);

        AtlasView all = Compose([Node(0, 0, state: AtlasNodeState.Completed)]);
        Assert.False(all.Anything);
        Assert.Equal(1, all.Total);       // one node was read; all of it was hidden
        Assert.Equal(string.Empty, all.Status);
    }

    private static AtlasContentNames LoadedContents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas-content.json")))
        {
            dir = dir.Parent;
        }

        return AtlasContentNames.Load(Path.Combine(dir!.FullName, "data", "atlas-content.json"));
    }
}
