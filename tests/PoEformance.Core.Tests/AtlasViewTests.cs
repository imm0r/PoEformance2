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
        uint[]? tokens = null,
        bool shown = false)
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
            ContentTokens: tokens ?? [],
            Shown: shown);

    private static readonly Dictionary<(int X, int Y), IReadOnlyList<AtlasSaid>> NoWords = [];

    /// <summary>
    /// Just the lines a content list would draw.
    /// </summary>
    /// <remarks>
    /// A content carries its description and its art name as well now, and almost every test
    /// here is about WHICH LINES come out and in what order. Comparing whole records would
    /// spell all three into every expectation and hide the one field being tested.
    /// </remarks>
    private static string[] Texts(IReadOnlyList<AtlasSaid> said)
        => [.. said.Select(one => one.Text)];

    private static AtlasGrouping Grouping(params AtlasGroup[] groups)
        => new(groups, AtlasMapNames.Empty);

    /// <summary>
    /// The settings these tests compose against unless one of them says otherwise.
    /// </summary>
    /// <remarks>
    /// NOT the shipped default, and deliberately: a map here is Locked unless a test says
    /// otherwise - that is the state most of an atlas is in - while the shipped settings hide
    /// unreachable maps, so every test about search, ratings, the web or hovering would be
    /// composing against an empty view and passing for the wrong reason.
    ///
    /// What the shipped default actually does is one test of its own, below, which is where a
    /// change to it should be argued with rather than in thirty unrelated expectations.
    /// </remarks>
    private static readonly AtlasSettings Showing = new(HideUnreachable: false);

    private static AtlasView Compose(
        IReadOnlyList<AtlasNode> live,
        AtlasSettings? settings = null,
        AtlasGrouping? grouping = null,
        AtlasRoutes? routes = null,
        Dictionary<(int X, int Y), IReadOnlyList<AtlasSaid>>? words = null,
        Vector2 cursor = default)
        => AtlasWatch.Compose(
            live,
            settings ?? Showing,
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
    public void OUTOfTheBoxTheAtlasShowsWhatCanBeRunAndWhatWasAskedFor()
    {
        // THE SHIPPED DEFAULT, pinned here because it is a judgement rather than a mechanism.
        // Most of an atlas is maps with no way to them and maps already finished - hundreds of
        // each - and drawn, they bury the dozen that can actually be entered. So both are off
        // by default, and what is left on screen is what can be run plus whatever routing was
        // asked to point at.
        AtlasNode open = Node(0, 0, "MapAugury", AtlasNodeState.Open);
        AtlasNode locked = Node(1, 0, "MapArroyo", AtlasNodeState.Locked);
        AtlasNode done = Node(2, 0, "MapBluff", AtlasNodeState.Completed);

        AtlasView view = AtlasWatch.Compose(
            [open, locked, done], AtlasSettings.Default, AtlasGrouping.None, AtlasRoutes.None, NoWords);

        Assert.Equal("MapAugury", Assert.Single(view.Marks).MapId);

        // And all three are still COUNTED, so the settings page can say how much was hidden
        // rather than leaving an empty atlas looking like a failed read.
        Assert.Equal(3, view.Total);
    }

    [Fact]
    public void AFinishedMapIsLeftOutUNLESSSomethingIsRoutingToIt()
    {
        AtlasNode done = Node(1, 1, state: AtlasNodeState.Completed);

        Assert.Empty(Compose([done]).Marks);
        Assert.Single(Compose([done], Showing with { HideCompleted = false }).Marks);
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
        AtlasView found = Compose(live, Showing with { Search = "arroyo" }, new AtlasGrouping([], names));
        Assert.Equal("MapArroyo", Assert.Single(found.Marks).MapId);

        // Whitespace is not a search. Trimming it here is what stops a stray space in the box
        // from emptying the atlas.
        Assert.Equal(2, Compose(live, Showing with { Search = "   " }).Marks.Count);
    }

    [Fact]
    public void THEWebDrawsEachConnectionOnceRatherThanFromBothEnds()
    {
        // Connections are mutual, so both ends list each other. Drawn as they come, every line
        // on the atlas is on top of itself - twice the cost for exactly the same picture.
        AtlasNode left = Node(0, 0, joined: [(1, 0)]);
        AtlasNode right = Node(1, 0, joined: [(0, 0)]);

        AtlasView view = Compose([left, right], Showing with { Web = true });

        Assert.Single(view.Web);
        Assert.Empty(Compose([left, right]).Web);
    }

    [Fact]
    public void ANDALineToSomewhereNotDrawnIsNotDrawnEither()
    {
        AtlasNode alone = Node(0, 0, joined: [(9, 9)]);
        Assert.Empty(Compose([alone], Showing with { Web = true }).Web);
    }

    [Fact]
    public void AMARKCarriesItsRatingAndTheScaleItIsAgainst()
    {
        // Both, because the drawing colours a rating and must not have to know where ratings
        // come from to do it - the scale travels with the value.
        AtlasRatings ratings = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Augury"] = 4, ["Lost Towers"] = 8 }, LoadedNames());

        var grouping = new AtlasGrouping([], LoadedNames(), ratings);

        AtlasMark mark = Assert.Single(Compose([Node(0, 0, "MapAugury")], grouping: grouping).Marks);

        Assert.Equal(4, mark.Rating);
        Assert.Equal(8, mark.BestRating);
    }

    [Fact]
    public void ANDAMapNobodyRatedCarriesNothingRatherThanNought()
    {
        // Nought is a rating - the worst one - and a map nobody has judged is not the worst
        // map, it is an unjudged one. Drawn as a red pill it would be an opinion nobody held.
        AtlasRatings ratings = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Lost Towers"] = 8 }, LoadedNames());

        AtlasMark mark = Assert.Single(
            Compose([Node(0, 0, "MapAugury")], grouping: new AtlasGrouping([], LoadedNames(), ratings)).Marks);

        Assert.Null(mark.Rating);

        // But the SCALE is still in force, which is what tells the drawing that this map is
        // unrated rather than that the ratings are switched off - the difference between a
        // question mark and no pill at all.
        Assert.Equal(8, mark.BestRating);
    }

    [Fact]
    public void ANDTheyCanBeTurnedOff()
    {
        AtlasRatings ratings = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Augury"] = 4 }, LoadedNames());

        var grouping = new AtlasGrouping([], LoadedNames(), ratings);

        AtlasMark mark = Assert.Single(
            Compose([Node(0, 0, "MapAugury")], Showing with { Ratings = false }, grouping).Marks);

        Assert.Null(mark.Rating);

        // No scale either, and that is the whole of how "off" is told from "unrated": both
        // carry no rating, and only one of them carries a scale to be unrated against.
        Assert.Equal(0, mark.BestRating);
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

        var placed = new Dictionary<ulong, Placed>
        {
            [0x1000] = new Placed(new Vector2(500, 600), new Vector2(40, 20), Shown: true),
        };

        AtlasNode kept = Assert.Single(AtlasWatch.Live([here, gone], placed));

        Assert.Equal(0x1000ul, kept.Address);
        Assert.Equal(new Vector2(500, 600), kept.Screen);
    }

    [Fact]
    public void WHEREAMapUnderTheCursorIsDrawnIsReported()
    {
        // The game puts its own panel over a hovered node - what the map is, its biome, what is
        // in it - and every label and line here would be drawn across it. The RECTANGLE rather
        // than a yes, because the overlay now keeps off that panel instead of switching itself
        // off, and where the node is drawn is where the panel comes up.
        // SHOWN, because that is the only case a panel exists for: the game puts a panel over a
        // node it is PAINTING, and over one it is not there is nothing to keep off.
        AtlasNode node = Node(1, 1, shown: true);   // drawn at 100,100, forty by twenty

        Assert.Equal(
            new ScreenRect(100, 100, 140, 120), AtlasWatch.Hovered([node], new Vector2(120, 110)));
        Assert.Null(AtlasWatch.Hovered([node], new Vector2(300, 110)));

        Assert.True(Compose([node], Showing, cursor: new Vector2(120, 110)).Hovering);
        Assert.False(Compose([node], Showing, cursor: new Vector2(300, 110)).Hovering);
    }

    [Fact]
    public void ANDTheViewItselfNoLongerBlanksOnAHover()
    {
        // What is drawn is no longer this record's decision: the panel over a hovered map is a
        // measured interface part, so the overlay keeps off it and goes on drawing - and hiding
        // everything is the fallback the overlay reaches for when it cannot find that part.
        // Deciding it here would take that choice away from the only thread that has both the
        // atlas view and the interface parts in the same frame.
        AtlasView view = Compose([Node(1, 1, shown: true)], Showing, cursor: new Vector2(120, 110));

        Assert.True(view.Hovering);
        Assert.True(view.Anything);
    }

    [Fact]
    public void ANDEveryMapCountsRatherThanOnlyTheDrawnOnes()
    {
        // The game shows its panel over a map whether or not this overlay chose to label it,
        // and it is the OTHER maps' labels and lines that would be drawn across it.
        // BOTH PAINTED BY THE GAME. What this is about is a map the OVERLAY hides, not one the
        // GAME is not drawing - the two look alike in a fixture and are opposites in the thing
        // being tested: the game still puts its panel over a map this tool chose not to label.
        AtlasNode shown = Node(3, 3, shown: true);
        AtlasNode finished = Node(1, 1, state: AtlasNodeState.Completed, shown: true);

        AtlasView view = Compose(
            [shown, finished], Showing with { HideCompleted = true }, cursor: new Vector2(120, 110));

        Assert.Single(view.Marks);          // the finished one is hidden, as asked
        Assert.True(view.Hovering);         // and hovering it is still noticed
    }

    [Fact]
    public void ANDACursorNobodyHasReportedIsNotTheCorner()
    {
        // Nought is "not asked yet", not a position. Taken literally it sits inside whatever
        // node happens to be drawn at the top-left, and the atlas would report a hover forever.
        Assert.Null(AtlasWatch.Hovered([Node(0, 0)], default));
        Assert.False(Compose([Node(0, 0)], Showing).Hovering);
    }

    [Fact]
    public void ANDTheHoverIsReportedWhateverTheSettingSays()
    {
        // The setting decides what to do when the game's panel cannot be measured, which is a
        // question about the interface rather than about the atlas. Folding it in here would
        // leave the overlay unable to tell "not hovering" from "hovering, told not to care".
        AtlasNode node = Node(1, 1, shown: true);
        Assert.True(
            Compose([node], Showing with { HideOnHover = false }, cursor: new Vector2(120, 110))
                .Hovering);
    }

    [Fact]
    public void ABADGESOwnWordsBeatTheGenericNameItsIdCarries()
    {
        // MEASURED ON A LIVE ATLAS, and it is the answer to a question three other tables did not
        // hold. The game draws a GOLD "Powerful Map Boss" and a RED "Deadly Map Boss" with two
        // different tooltips - but both nodes carry badge id 0x64, whose content row is named
        // "Powerful Map Boss". The tier is in the BADGE ELEMENT'S OWN STRING, which reads
        // "[DeadlyMapBoss|Deadly Map Boss]" in the game's packed markup.
        //
        // The schema had this written down a day before anyone looked: "it says MORE than the id
        // can, and the tier it names is lost the moment the id is all that is kept". It was.
        AtlasNode node = Node(1, 1, badges: [0x64]) with
        {
            BadgeWords = new Dictionary<uint, string> { [0x64] = "[DeadlyMapBoss|Deadly Map Boss]" },
        };

        AtlasSaid said = Assert.Single(AtlasWatch.Words(node, LoadedContents()));

        // THE MARKUP IS STRIPPED, the same as everywhere else the game's strings are shown.
        Assert.Equal("Deadly Map Boss", said.Text);

        // AND THE ID'S OWN SENTENCE STAYS AS THE DETAIL, so the name gets more specific and
        // nothing is traded away for it.
        Assert.Equal("Area contains a Powerful Map Boss", said.Detail);
    }

    [Fact]
    public void ANDWithoutOneTheIdStillNamesIt()
    {
        // The ordinary case: most badges arrive as a bare id from the vector, with no element to
        // ask, and the content table is all there is. Nothing changes for them.
        AtlasSaid said = Assert.Single(AtlasWatch.Words(Node(1, 1, badges: [0x64]), LoadedContents()));

        Assert.Equal("Powerful Map Boss", said.Text);
    }

    [Fact]
    public void ANDANodeTheGAMEIsNotPaintingIsNotAHoverAtAll()
    {
        // REPORTED FROM A LIVE CLIENT, and it blanked the overlay exactly where the overlay was
        // the only thing on screen. The answer here feeds a fallback that hides everything on a
        // frame where the game's hover panel cannot be measured - and over a node the game is not
        // painting there is no panel to measure, ever, because the game puts none up. So hovering
        // a fogged node blanked the whole atlas, for good, with nothing to say why.
        //
        // The symptom named the cause: the content pictures sit just BELOW the plate, so their
        // outer rim is outside the node's rectangle and read normally, and a few pixels further
        // in everything vanished.
        //
        // NOT THE SAME QUESTION as the test above it. There a map is hidden by the OVERLAY while
        // the game still paints it - the panel exists and must be kept off. Here the GAME is not
        // painting it, so there is nothing to keep off and nothing to hide from.
        AtlasNode fogged = Node(1, 1);

        Assert.Null(AtlasWatch.Hovered([fogged], new Vector2(120, 110)));
        Assert.False(Compose([fogged], Showing, cursor: new Vector2(120, 110)).Hovering);

        // And the same node, once the game IS drawing it, is a hover again.
        Assert.NotNull(AtlasWatch.Hovered([fogged with { Shown = true }], new Vector2(120, 110)));
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

        Assert.Single(Compose([shown, finished], Showing with { Web = true, HideCompleted = false }).Web);
        Assert.Empty(Compose([shown, finished], Showing with { Web = true, HideCompleted = true }).Web);
    }

    [Fact]
    public void ANDSoDoesSearchingOneOut()
    {
        // Same rule through the other filter, because it is the same question: the web is
        // between the maps ON THE SCREEN, whichever way the rest came to be off it.
        AtlasNode augury = Node(0, 0, mapId: "MapAugury", joined: [(1, 0)]);
        AtlasNode ravine = Node(1, 0, mapId: "MapRavine", joined: [(0, 0)]);

        var searched = Showing with { Web = true, Search = "Augury" };
        var grouping = new AtlasGrouping([], AtlasMapNames.Empty);

        Assert.Single(Compose([augury, ravine], Showing with { Web = true }, grouping).Web);
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
        IReadOnlyList<AtlasSaid> said = AtlasWatch.Words(node, contents);

        Assert.Equal(["Breach"], Texts(said));
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
        IReadOnlyList<AtlasSaid> said = AtlasWatch.Words(node, contents);

        Assert.Equal(["Contains 3 additional Shrines", "Area contains Abysses"], Texts(said));
    }

    [Fact]
    public void ANDTheSameEffectAtTwoStrengthsStaysTwoLines()
    {
        // Which is why the de-duplication is on the finished words: on the id alone, the
        // second of these would be dropped as a repeat of the first.
        AtlasNode node = Node(0, 0, tokens: [0x0040_0963, 0x00C0_0963]);
        IReadOnlyList<AtlasSaid> said = AtlasWatch.Words(node, LoadedContents());

        Assert.Equal(["Contains 1 additional Shrines", "Contains 3 additional Shrines"], Texts(said));
    }

    [Fact]
    public void ABADGEIsANameAndNeverACount()
    {
        // A badge's high half is a category tag, not a magnitude - the same content id with the
        // tag on it is the same content, and reading the tag as a number would say so twice.
        AtlasNode node = Node(0, 0, badges: [0x0068, 0x0002_0068]);
        Assert.Equal(["Ritual"], Texts(AtlasWatch.Words(node, LoadedContents())));
    }

    [Fact]
    public void ANDSomethingTheTableHasNeverHeardOfIsLeftOutRatherThanShownAsANumber()
    {
        AtlasNode node = Node(0, 0, badges: [0xBEEF]);
        Assert.Empty(AtlasWatch.Words(node, LoadedContents()));
    }

    [Fact]
    public void ACONTENTCarriesItsPictureAndItsFullWordingAsWellAsItsLine()
    {
        // Both were read out of the data file and thrown away one layer down, because the
        // reader published words. The line is what gets DRAWN; the other two are what makes an
        // icon explain itself and a short badge name mean something under the pointer.
        AtlasNode node = Node(0, 0, badges: [0x0065]);
        AtlasSaid said = Assert.Single(AtlasWatch.Words(node, LoadedContents()));

        Assert.Equal("Breach", said.Text);
        Assert.Equal("Area contains an Otherworldly Breach", said.Detail);
        Assert.Equal("AtlasIconContentBreach", said.Icon);
    }

    [Fact]
    public void WHETHERTheGameIsDrawingANodeItselfReachesTheDrawing()
    {
        // The whole worth of the content pictures hangs on this. On a node the game is painting,
        // it draws its own icon with its own tooltip saying the same words, so ours is a copy a
        // few pixels lower - the first build of this shipped exactly that. The drawing needs to
        // know which nodes those are, so the flag travels with the position: it changes as the
        // atlas is scrolled and as fog lifts, at the same rate.
        Assert.True(Assert.Single(Compose([Node(0, 0, shown: true)]).Marks).Shown);
        Assert.False(Assert.Single(Compose([Node(0, 0)]).Marks).Shown);
    }

    [Fact]
    public void WHATTheGameSaysItDoesNotShowIsNotShown()
    {
        // Badge 0x006e is a developer's placeholder - "[DNT] Breach City - Not Shown to
        // Players", described as "DNT No visual identity = not shown" - and it went onto four
        // maps of a real atlas, in the same plate as everything the game does mean to say.
        AtlasNode node = Node(0, 0, badges: [0x006E]);

        Assert.Empty(AtlasWatch.Words(node, LoadedContents()));

        // The row is still IN the table, so the id is recognised rather than reported as one
        // nothing has heard of - it is the drawing that skips it.
        Assert.NotNull(LoadedContents().Badge(0x006E));
    }

    [Fact]
    public void ANDTheMarkIsTheBracketRatherThanTheLettersDNT()
    {
        // The game has used "[UNUSED]" and bare brackets for the same thing, and no real
        // content has ever begun with one. Matching "DNT" would leave those drawn.
        Assert.True(AtlasWatch.Placeholder("[DNT] Breach City - Not Shown to Players"));
        Assert.True(AtlasWatch.Placeholder("[UNUSED] Something"));
        Assert.False(AtlasWatch.Placeholder("Breach"));
        Assert.False(AtlasWatch.Placeholder("Area contains Abysses"));
        Assert.False(AtlasWatch.Placeholder(string.Empty));
        Assert.False(AtlasWatch.Placeholder(null));
    }

    [Fact]
    public void ANDDropsTheWordingWhenItOnlyRepeatsTheLine()
    {
        // Which is the usual case for an effect: its label IS its description, so keeping both
        // would put the same sentence twice into one tooltip.
        AtlasNode node = Node(0, 0, tokens: [0x0040_6872]);
        AtlasSaid said = Assert.Single(AtlasWatch.Words(node, LoadedContents()));

        Assert.Equal("Area contains Abysses", said.Text);
        Assert.Equal(string.Empty, said.Detail);
    }

    [Fact]
    public void CONTENTSCanBeTurnedOffWithoutTurningOffTheAtlas()
    {
        var words = new Dictionary<(int X, int Y), IReadOnlyList<AtlasSaid>>
        {
            [(0, 0)] = [new AtlasSaid("Breach")],
        };

        Assert.Equal(["Breach"], Texts(Compose([Node(0, 0)], words: words).Marks[0].Contents));
        Assert.Empty(Compose([Node(0, 0)], Showing with { Contents = false }, words: words).Marks[0].Contents);
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

    /// <summary>The shipped map table, so a rating fixture can resolve real names to real ids.</summary>
    private static AtlasMapNames LoadedNames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas-maps.json")))
        {
            dir = dir.Parent;
        }

        return AtlasMapNames.Load(Path.Combine(dir!.FullName, "data", "atlas-maps.json"));
    }
}
