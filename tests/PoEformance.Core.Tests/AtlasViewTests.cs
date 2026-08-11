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
        Dictionary<(int X, int Y), IReadOnlyList<string>>? words = null,
        Vector2 cursor = default)
        => AtlasWatch.Compose(
            live,
            settings ?? AtlasSettings.Default,
            grouping ?? AtlasGrouping.None,
            routes ?? AtlasRoutes.None,
            words ?? NoWords,
            cursor);

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

        // One unbroken run of three: hiding a map takes away its LABEL, not its position, so
        // the way through it is still a way somebody can walk.
        Assert.Equal(3, Assert.Single(citadel.Route).Count);

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

        Assert.Equal(3, Assert.Single(within.Route).Count);
    }

    [Fact]
    public void AROUTEIsBrokenAtAMapNothingCanBePlacedFor()
    {
        // NOT joined across, which is what this used to do. A route can cross a map the panel
        // has no position for - one the atlas has not materialised, or a grid position the edge
        // table names that no map sits on - and a line from the map before it to the map after
        // runs along no connection anybody can walk. With several steps missing that is a
        // straight line between two maps nowhere near each other: the arbitrary line.
        AtlasNode here = Node(0, 0, "MapAugury", AtlasNodeState.Open, joined: [(1, 0)]);
        AtlasNode between = Node(1, 0, "MapArroyo", AtlasNodeState.Locked, joined: [(0, 0), (2, 0)]);
        AtlasNode far = Node(2, 0, "MapFar", AtlasNodeState.Locked, joined: [(1, 0)]);

        // Routed with the whole atlas, composed with the middle one missing.
        AtlasRoutes routes = AtlasRoutes.From([here, between, far]);
        var groups = new[] { new AtlasGroup("Far", "#FFFFFF", Maps: ["MapFar"], Route: true) };

        AtlasMark mark = Assert.Single(
            Compose([here, far], AtlasSettings.Default, Grouping(groups), routes).Marks,
            found => found.MapId == "MapFar");

        // Two ends and nothing in between: neither is a run of its own, so nothing is drawn -
        // and the hop count still says how far it is, so the map does not look next door.
        Assert.Empty(mark.Route);
        Assert.Equal(2, mark.Hops);
    }

    [Fact]
    public void ANDTheRunsEitherSideOfAHoleAreStillDrawn()
    {
        // Broken is not the same as dropped. What can be placed is still the truth about where
        // the route goes; only the join across the hole was ever an invention.
        var centres = new Dictionary<(int X, int Y), Vector2>
        {
            [(0, 0)] = new(0, 0),
            [(1, 0)] = new(10, 0),
            // (2,0) is missing - the hole
            [(3, 0)] = new(30, 0),
            [(4, 0)] = new(40, 0),
        };

        IReadOnlyList<IReadOnlyList<Vector2>> runs =
            AtlasWatch.Screened([(0, 0), (1, 0), (2, 0), (3, 0), (4, 0)], centres);

        Assert.Equal(2, runs.Count);
        Assert.Equal([new Vector2(0, 0), new Vector2(10, 0)], runs[0]);
        Assert.Equal([new Vector2(30, 0), new Vector2(40, 0)], runs[1]);
    }

    [Fact]
    public void ANDALoneStepIsNoRunAtAll()
    {
        // One point is a corner nobody can see. Kept, it would put the entry dot on a map the
        // route does not begin at, which reads as a starting point and is not one.
        var centres = new Dictionary<(int X, int Y), Vector2> { [(0, 0)] = new(0, 0), [(3, 0)] = new(30, 0) };

        Assert.Empty(AtlasWatch.Screened([(0, 0), (1, 0), (2, 0), (3, 0)], centres));
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
    public void ANodeThePanelDidNotPlaceIsDROPPEDRatherThanLeftWhereItWas()
    {
        // The bug that made an atlas look like a spider's web. Keeping the last-seen position
        // reads as harmless - a third of a second of lag is invisible on a label - and on a
        // LINE it is not: dragging the atlas re-lays every node, so a node that missed a tick
        // sits exactly one drag behind, and every line to it is a ray with that same offset.
        // They are all parallel because they all share it.
        AtlasNode here = Node(0, 0) with { Address = 0x1000 };
        AtlasNode gone = Node(1, 0) with { Address = 0x2000 };

        var placed = new Dictionary<ulong, (Vector2 Position, Vector2 Size)>
        {
            [0x1000] = (new Vector2(500, 600), new Vector2(40, 20)),
        };

        AtlasNode kept = Assert.Single(AtlasWatch.Live([here, gone], placed));

        Assert.Equal(0x1000ul, kept.Address);
        Assert.Equal(new Vector2(500, 600), kept.Screen);
    }

    [Fact]
    public void NOTHINGIsDrawnWhileTheCursorIsOnAMap()
    {
        // The game puts its own panel over a hovered node - what the map is, its biome, what is
        // in it - and every label and line here would be drawn across it.
        AtlasNode node = Node(1, 1);   // drawn at 100,100, forty by twenty

        Assert.True(AtlasWatch.Hovered([node], new Vector2(120, 110)));
        Assert.False(AtlasWatch.Hovered([node], new Vector2(300, 110)));

        Assert.False(Compose([node], new AtlasSettings(), cursor: new Vector2(120, 110)).Anything);
        Assert.True(Compose([node], new AtlasSettings(), cursor: new Vector2(300, 110)).Anything);
    }

    [Fact]
    public void ANDEveryMapCountsRatherThanOnlyTheDrawnOnes()
    {
        // The game shows its panel over a map whether or not this overlay chose to label it,
        // and it is the OTHER maps' labels and lines that would be drawn across it.
        AtlasNode shown = Node(3, 3);
        AtlasNode finished = Node(1, 1, state: AtlasNodeState.Completed);

        AtlasView view = Compose(
            [shown, finished], new AtlasSettings(HideCompleted: true), cursor: new Vector2(120, 110));

        Assert.Single(view.Marks);          // the finished one is hidden, as asked
        Assert.True(view.Hovering);         // and hovering it still stops the drawing
    }

    [Fact]
    public void ANDACursorNobodyHasReportedIsNotTheCorner()
    {
        // Nought is "not asked yet", not a position. Taken literally it sits inside whatever
        // node happens to be drawn at the top-left, and the atlas would never draw at all.
        Assert.False(AtlasWatch.Hovered([Node(0, 0)], default));
        Assert.True(Compose([Node(0, 0)], new AtlasSettings()).Anything);
    }

    [Fact]
    public void ANDTheGettingOutOfTheWayCanBeTurnedOff()
    {
        AtlasNode node = Node(1, 1);
        Assert.True(Compose([node], new AtlasSettings(HideOnHover: false), cursor: new Vector2(120, 110)).Anything);
    }

    [Fact]
    public void ANDHIDINGAMapHidesTheLinesToItTOO()
    {
        // Which is the point of hiding: a line to a map that is not on the screen is a line to
        // nothing. This went unnoticed while the connections read as empty - the web drew nought
        // lines and looked right - and announced itself the moment they worked, as two thousand
        // lines across an atlas showing a hundred maps.
        AtlasNode shown = Node(0, 0, joined: [(1, 0)]);
        AtlasNode finished = Node(1, 0, state: AtlasNodeState.Completed, joined: [(0, 0)]);

        Assert.Single(Compose([shown, finished], new AtlasSettings(Web: true, HideCompleted: false)).Web);
        Assert.Empty(Compose([shown, finished], new AtlasSettings(Web: true, HideCompleted: true)).Web);
    }

    [Fact]
    public void ANDSoDoesSearchingOneOut()
    {
        // Same rule through the other filter, because it is the same question: the web is
        // between the maps ON THE SCREEN, whichever way the rest came to be off it.
        AtlasNode augury = Node(0, 0, mapId: "MapAugury", joined: [(1, 0)]);
        AtlasNode ravine = Node(1, 0, mapId: "MapRavine", joined: [(0, 0)]);

        var searched = new AtlasSettings(Web: true, Search: "Augury");
        var grouping = new AtlasGrouping([], AtlasMapNames.Empty);

        Assert.Single(Compose([augury, ravine], new AtlasSettings(Web: true), grouping).Web);
        Assert.Empty(Compose([augury, ravine], searched, grouping).Web);
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
    public void ANDHowManyOfThemGoesWHEREITHEGAMEPUTSIT()
    {
        // The number belongs INSIDE the wording, in the gap the game left for it, and only in
        // the wordings that left one. Appended to everything instead, a plain effect's
        // magnitude of one - which the game writes as 64 - turned every line on the atlas into
        // "Area contains Abysses x64", which is what sent somebody looking at this.
        AtlasContentNames contents = LoadedContents();

        AtlasNode node = Node(0, 0, tokens: [0x00C0_0963, 0x0040_6872]);
        IReadOnlyList<string> said = AtlasWatch.Words(node, contents);

        Assert.Equal(["Contains 3 additional Shrines", "Area contains Abysses"], said);
    }

    [Fact]
    public void ANDTheSameEffectAtTwoStrengthsStaysTwoLines()
    {
        // Which is why the de-duplication is on the finished words: on the id alone, the
        // second of these would be dropped as a repeat of the first.
        AtlasNode node = Node(0, 0, tokens: [0x0040_0963, 0x00C0_0963]);
        IReadOnlyList<string> said = AtlasWatch.Words(node, LoadedContents());

        Assert.Equal(["Contains 1 additional Shrines", "Contains 3 additional Shrines"], said);
    }

    [Fact]
    public void ABADGEIsANameAndNeverACount()
    {
        // A badge's high half is a category tag, not a magnitude - the same content id with the
        // tag on it is the same content, and reading the tag as a number would say so twice.
        AtlasNode node = Node(0, 0, badges: [0x0068, 0x0002_0068]);
        Assert.Equal(["Ritual"], AtlasWatch.Words(node, LoadedContents()));
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
