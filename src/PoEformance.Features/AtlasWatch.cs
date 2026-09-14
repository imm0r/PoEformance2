using System.Numerics;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// One thing a map contains, as the game words it.
/// </summary>
/// <remarks>
/// THREE FIELDS RATHER THAN A STRING, and the two extra ones are what the atlas was missing.
/// The line drawn on a node is a name - "Breach" - and a name is the least of what the game
/// knows: it also has the sentence explaining what a breach does to the area, and the art it
/// draws for it in its own interface. Both were read out of <c>atlas-content.json</c> and then
/// thrown away one layer later, because the reader published words.
///
/// So a content is carried whole from here on. What is DRAWN is still <see cref="Text"/>; the
/// other two are what the overlay reaches for when the cursor stops on a map, and when it can
/// find the picture the game uses.
/// </remarks>
/// <param name="Text">The line to draw, with the node's own number already written into it.</param>
/// <param name="Detail">
/// The game's fuller sentence, or empty when it says nothing the text does not. An effect line
/// IS the sentence, so only a named badge usually carries one.
/// </param>
/// <param name="Icon">
/// The game's own art name for it, without a folder or an extension - "AtlasIconContentBreach".
/// Empty when the data has none. It is a NAME, not a path: where the picture for it is found is
/// the overlay's business, not the reader's.
/// </param>
public readonly record struct AtlasSaid(string Text, string Detail = "", string Icon = "")
{
    /// <summary>Reads as its text, so anything that only wants the line still gets it.</summary>
    public override string ToString() => Text;
}

/// <summary>One map on the atlas, with everything the drawing needs already worked out.</summary>
/// <param name="Where">Its centre on the screen, in pixels.</param>
/// <param name="Name">What to call it - the game's name, or its raw id when it is new.</param>
/// <param name="Group">The kind of map it is, or null when it is an ordinary one.</param>
/// <param name="Contents">What the game says is in it, already turned into words.</param>
/// <param name="Route">
/// The way here from the nearest map that can be entered now, in screen positions, as one or
/// more unbroken RUNS - empty when this is not a routing target or none of it can be placed.
///
/// Runs rather than one list of points because a route can cross a map the panel has no
/// position for, and joining the two sides of that gap draws a line along no connection
/// anybody can walk. Each run is at least two points; the hole between them is the truth.
/// </param>
/// <param name="Rating">
/// What this map is worth running, on somebody's own scale, or null when nobody has said.
/// </param>
/// <param name="BestRating">
/// The top of that scale, carried so the drawing can colour a rating without knowing where the
/// ratings came from. Nought when there are none.
/// </param>
/// <param name="Hops">How many maps have to be run to get here. 0 for one you can enter now.</param>
/// <param name="Biome">
/// Which biome the map is in, as the game numbers them, or <see cref="AtlasBiomes.None"/> when
/// there is none to draw. See <see cref="AtlasBiomes"/> for why that is not nought.
/// </param>
/// <param name="Shown">
/// Whether the GAME is painting this node itself.
/// </param>
/// <remarks>
/// <see cref="Shown"/> is what stops the content pictures being a copy. The game draws its own
/// icon on every node it is showing, with its own tooltip saying the same words - so drawing ours
/// there adds a second picture of the same thing, slightly lower. What it cannot draw is a node
/// it is not showing: out in the fog, or scrolled far enough out that it stops painting them.
/// That is where a picture is the only thing saying what is in a map, and it is where ours goes.
/// </remarks>
public sealed record AtlasMark(
    (int X, int Y) Grid,
    Vector2 Where,
    string MapId,
    string Name,
    AtlasNodeState State,
    AtlasGroup? Group,
    IReadOnlyList<AtlasSaid> Contents,
    IReadOnlyList<IReadOnlyList<Vector2>> Route,
    int Hops,
    int? Rating = null,
    int BestRating = 0,
    int Biome = AtlasBiomes.None,
    bool Shown = false);

/// <summary>
/// What the atlas looks like right now. Immutable, published whole, drawn as-is.
/// </summary>
/// <param name="Marks">The maps worth drawing, after hiding and searching have had their say.</param>
/// <param name="Web">
/// Every connection between drawn maps, as pairs of screen positions, and only when asked for.
/// </param>
/// <param name="Status">
/// What happened, in words. "atlas closed" is the ordinary case and is not a fault - this is
/// read every tick and says nothing most of the time.
/// </param>
public sealed record AtlasView(
    IReadOnlyList<AtlasMark> Marks,
    IReadOnlyList<(Vector2 From, Vector2 To)> Web,
    int Total,
    int Open,
    int Reachable,
    string Status,
    ScreenRect? Hovered = null)
{
    public static AtlasView Closed { get; } = new([], [], 0, 0, 0, "atlas closed");

    /// <summary>Whether there is anything at all to draw.</summary>
    public bool Anything => Marks.Count > 0 || Web.Count > 0;

    /// <summary>
    /// Whether the cursor is on a map, which is when the game puts its own panel up.
    /// </summary>
    /// <remarks>
    /// REPORTED, NOT ACTED ON. This used to switch the whole overlay off, because the game's
    /// panel is drawn over the node and every label and line here would land across it. It no
    /// longer does: the panel is a measured interface part like the orbs and the title bar, so
    /// the overlay keeps off THAT rectangle and goes on drawing everywhere else. Blanking the
    /// atlas is now only the fallback for the frame where the panel could not be measured -
    /// see <see cref="AtlasHoverPanel"/>, which is where that decision lives.
    /// </remarks>
    public bool Hovering => Hovered is not null;
}

/// <summary>
/// Serves the atlas from the reader thread.
/// </summary>
/// <remarks>
/// The same arrangement as the interface browser, and for the same reason: reading the atlas is
/// a few hundred pointer chains and a wide string each, and the render thread is the one place
/// that must not do it. So this reads wherever the reading already happens and publishes a
/// finished view; the overlay draws whatever came back.
///
/// TWO RATES, because the atlas has two kinds of fact. What a node IS - its id, its contents,
/// what it connects to - cannot change while somebody looks at it, so it is read on an interval.
/// WHERE it is changes every time the atlas is dragged, so that is read every tick.
///
/// The difference is not small, and the first version of this got it wrong by re-reading
/// everything each tick while a comment claimed otherwise. The slow half is an id string down
/// three pointers, a connection list and two content vectors PER NODE, against a few hundred
/// nodes; the fast half is five reads each, sharing one walk of the chain above the panel
/// (<c>UiElementReader.ReadSiblings</c>). At thirty ticks a second that is the difference
/// between an atlas that can be read live and one that cannot.
///
/// IDLE UNLESS THE PANEL IS OPEN. Finding that out costs three child reads, which is nothing
/// next to the entity map - and the atlas is closed for almost all of a session.
///
/// The positions LAG BY ONE READ. Reads run at 30 Hz and frames at whatever the game runs at, so
/// a label trails its node slightly while the atlas is being dragged and sits exactly right the
/// moment it stops. That is the cost of keeping memory reads off the render thread, and it is
/// the right way round: a label a frame behind is a great deal better than a frame that waited
/// for a read.
/// </remarks>
public sealed class AtlasWatch
{
    /// <summary>
    /// How long the slow half of the read is kept before it is taken again.
    /// </summary>
    /// <remarks>
    /// A third of a second. The things it reads change when somebody runs a map, which is
    /// minutes apart - this is short enough that a completed map goes grey while the atlas is
    /// still open, and long enough that the cost disappears next to the world read.
    /// </remarks>
    public const long RestudyMs = 333;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly AtlasReader _atlas;
    private readonly AtlasContentNames _contents;

    private AtlasSettings _settings = AtlasSettings.Default;
    private AtlasGrouping _grouping = AtlasGrouping.None;
    private AtlasView _view = AtlasView.Closed;

    // The slow half's answers, kept between reads: what each node IS, and how to get to it.
    private IReadOnlyList<AtlasNode> _studied = [];
    private AtlasRoutes _routes = AtlasRoutes.None;
    private readonly Dictionary<(int X, int Y), IReadOnlyList<AtlasSaid>> _said = [];
    private long _studiedAt;
    private int _studiedCount = -1;

    /// <summary>
    /// Every map id this session has actually seen on an atlas, which only ever grows.
    /// </summary>
    /// <remarks>
    /// NOT the last study's nodes, which is what this used to be reported from. A study replaces
    /// the node list wholesale, so "seen on the atlas" meant "on screen a third of a second ago"
    /// and the report's column shrank again the moment somebody scrolled away. The difference
    /// between "the file lists it" and "this session met it" needs the accumulation.
    /// </remarks>
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    // The one-shot check, as a sequence number rather than a queued command - the same trick
    // the interface browser uses. A flag would be re-served every tick; a queue would need
    // draining, ordering and a lifetime for one button.
    private int _checkWanted;
    private int _checkServed;
    private IReadOnlyList<string> _checked = [];

    /// <summary>
    /// The map data as the game has it beside the files, published for the interface.
    /// </summary>
    /// <remarks>
    /// Rebuilt whenever the catalogue changes - which is once a session - and otherwise handed
    /// out unchanged, so the tab that draws it costs nothing per frame. Immutable, so it crosses
    /// from this thread to the drawing one without a lock.
    /// </remarks>
    private MapDataReport _mapData = MapDataReport.Empty;

    private IReadOnlyDictionary<string, int> _ritualWorth = new Dictionary<string, int>();

    private float _cursorX;
    private float _cursorY;

    /// <summary>
    /// WorldAreas read from the game, which is what data/atlas-maps.json would have to stop being.
    /// </summary>
    /// <remarks>
    /// Read ONCE PER SESSION, on the first study that can reach it: the table is reachable only
    /// through an atlas node (see WorldAreaCatalogue) and never moves afterwards.
    ///
    /// ON THE READING PATH, not only on the check button, which is a deliberate change. The unique
    /// flag in force now comes from this table (AtlasMapNames.LearnUnique), and a correction that
    /// only happens when somebody presses a diagnostic button is a correction that never happens.
    /// The cost is one fourteen-block read of half a megabyte plus its strings, once, against an
    /// atlas study that reads an id, a connection list and two content vectors per node every
    /// third of a second - so it is less than one study, and then it is nothing for the session.
    /// </remarks>
    private readonly WorldAreaCatalogue _catalogue;

    /// <summary>
    /// How many studies may try to reach the WorldAreas table before the session gives up.
    /// </summary>
    /// <remarks>
    /// A budget rather than a retry, because there are only two outcomes: the chain works and the
    /// first candidate node settles it, or the offsets are wrong and EVERY node fails the same
    /// way. The second must not cost a few hundred pointer reads every third of a second forever.
    /// The check button still walks every node, which is where a person is asking why.
    /// </remarks>
    private const int CatalogueTries = 8;

    private int _catalogueTries = CatalogueTries;

    public AtlasWatch(
        IMemoryReader reader,
        OffsetSchema schema,
        ulong gameStatesStatic,
        AtlasContentNames? contents = null,
        AtlasMapNames? names = null,
        AtlasRatings? ratings = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _atlas = new AtlasReader(reader, schema, new UiElementReader(reader, schema));
        _catalogue = new WorldAreaCatalogue(reader, schema);
        _contents = contents ?? AtlasContentNames.Empty;
        Names = names ?? AtlasMapNames.Empty;
        Ratings = ratings ?? AtlasRatings.Empty;
        _grouping = new AtlasGrouping(_settings.Sorting, Names, Ratings);

        // Before any atlas has been seen the report is the files alone, which is the honest
        // starting state: it says what is shipped and that nothing has been read yet.
        _mapData = MapDataReport.Build(null, Names, Ratings);
    }

    /// <summary>The map names in force, kept so settings can be replaced without them.</summary>
    public AtlasMapNames Names { get; }

    /// <summary>What each map is worth running, for the same reason as the names.</summary>
    public AtlasRatings Ratings { get; }

    /// <summary>
    /// The ritual line, when one is being drawn. Null unless somebody attached it.
    /// </summary>
    /// <remarks>
    /// Rides this read rather than taking its own: the panel address, the interface root and
    /// the studied nodes are all here already, and resolving them a second time each tick would
    /// be pure duplication for a feature that is idle almost always.
    /// </remarks>
    public RitualWatch? Ritual { get; set; }

    /// <summary>What each ritual reward is worth to this player. Read on the reader thread.</summary>
    public IReadOnlyDictionary<string, int> RitualWorth
    {
        get => Volatile.Read(ref _ritualWorth);
        set
        {
            Volatile.Write(ref _ritualWorth, value ?? new Dictionary<string, int>());
            Ritual?.Reconsider();
        }
    }

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public AtlasView View => Volatile.Read(ref _view);

    /// <summary>
    /// Where the cursor is, in the pixels the overlay draws in. Published by the render thread.
    /// </summary>
    /// <remarks>
    /// TWO FLOATS rather than a Vector2 field, because eight bytes are not written atomically
    /// and this crosses a thread boundary every frame. The worst a torn pair can do is one tick
    /// of a hover test taken at a corner the cursor passed through, which is a frame nobody
    /// sees - but a Vector2 field would be a race with no name on it, and the two floats say
    /// out loud that this is published rather than shared.
    ///
    /// Set every frame rather than folded into the settings: the settings rebuild the grouping
    /// when they are replaced, and a cursor moving would throw that away sixty times a second.
    /// </remarks>
    public Vector2 Cursor
    {
        get => new(Volatile.Read(ref _cursorX), Volatile.Read(ref _cursorY));
        set
        {
            Volatile.Write(ref _cursorX, value.X);
            Volatile.Write(ref _cursorY, value.Y);
        }
    }

    /// <summary>What the last check made of each step of the walk. Empty until one is asked for.</summary>
    public IReadOnlyList<string> Checked => Volatile.Read(ref _checked);

    /// <summary>What the game says about every map, beside what the files say.</summary>
    public MapDataReport MapData => Volatile.Read(ref _mapData);

    /// <summary>
    /// Asks for one account of the read, served on the next tick.
    /// </summary>
    /// <remarks>
    /// EVERY ATLAS OFFSET IS UNCONFIRMED - ported from the reference with the game
    /// unavailable - so "nothing came back" has at least four causes: the panel's child path,
    /// the flag fingerprints, the two-hop chain to a node's data, and the fields at the end of
    /// it. This is what turns that into a reading instead of a hunt, and it is the first thing
    /// to press when the atlas is open and the overlay is blank.
    /// </remarks>
    public void CheckTheRead() => Interlocked.Increment(ref _checkWanted);

    /// <summary>What to draw and how to sort it. Replaced whole, from whichever thread saved it.</summary>
    public AtlasSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value);

            // The grouping caches its decisions, so it is rebuilt rather than mutated - a
            // cache of answers from the old groups would outlive the change that replaced them.
            Volatile.Write(ref _grouping, new AtlasGrouping(value.Sorting, Names, Ratings));
        }
    }

    /// <summary>
    /// Reads the atlas once. Called on the reader thread, in the same pass as a world read.
    /// </summary>
    public void Service(UiScale scale, long nowMs)
    {
        AtlasSettings settings = Volatile.Read(ref _settings);
        if (!settings.Enabled)
        {
            Forget();
            Volatile.Write(ref _view, AtlasView.Closed with { Status = "atlas overlay off" });
            Asked(scale);
            return;
        }

        try
        {
            Volatile.Write(ref _view, Build(settings, scale, nowMs));
        }
        catch (Exception exception)
        {
            // A stale view beats a dead servicing thread. Every pointer here belongs to a
            // panel that can be closed between two reads, so this is an ordinary event.
            Forget();
            Volatile.Write(ref _view, AtlasView.Closed with { Status = $"read failed: {exception.Message}" });
        }

        // OUTSIDE the try, and reached even with the overlay switched off. Both are the point:
        // the moment somebody wants an account of the walk is the moment the walk is failing,
        // and a check that only runs when the read already worked reports on nothing.
        Asked(scale);
    }

    /// <summary>Serves the one-shot check, if one has been asked for since the last.</summary>
    private void Asked(UiScale scale)
    {
        int wanted = Volatile.Read(ref _checkWanted);
        if (wanted != _checkServed)
        {
            _checkServed = wanted;
            Volatile.Write(ref _checked, Check(scale));
        }
    }

    private AtlasView Build(AtlasSettings settings, UiScale scale, long nowMs)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        if (chain.UiRoot == 0)
        {
            Forget();
            Ritual?.Service(0, 0, [], RitualWorth);
            return AtlasView.Closed with { Status = chain.InGame ? "UI root did not resolve" : "not in an area" };
        }

        // Resolved ONCE and passed on: the ritual line hangs off the same element, and walking
        // three children twice a tick to arrive at the same address is nothing but duplication.
        ulong panel = _atlas.Panel(chain.UiRoot);

        // IS IT OPEN - asked first, and asked of the panel's visibility rather than of its
        // contents. Closing the atlas does not empty it: the panel and its several hundred
        // nodes stay in the tree with readable positions, so "are there any maps" says yes to
        // an atlas nobody is looking at, and the overlay went on writing map names over the
        // game until the player opened it again. This is also what keeps the read idle: a
        // walk up a handful of parents, against several hundred nodes read for nothing.
        if (!_atlas.IsOpen(panel))
        {
            Forget();
            Ritual?.Service(0, 0, [], RitualWorth);
            return AtlasView.Closed;
        }

        // WHERE the maps are, every tick, because that is the half that changes while somebody
        // drags the atlas about.
        Dictionary<ulong, Placed> placed = _atlas.Where(panel, scale);

        if (placed.Count == 0)
        {
            Forget();
            Ritual?.Service(0, 0, [], RitualWorth);
            return AtlasView.Closed;
        }

        // WHAT they are, on the interval - and at once when the count of drawn elements
        // changes, which is what opening a region looks like from here. This is the expensive
        // half: an id string, a connection list and two content vectors per node.
        if (placed.Count != _studiedCount || nowMs - _studiedAt >= RestudyMs || nowMs < _studiedAt)
        {
            Study(_atlas.Read(chain.UiRoot, scale), placed.Count, nowMs);
        }

        if (_studied.Count == 0)
        {
            Ritual?.Service(0, 0, [], RitualWorth);

            // NOT "atlas closed", which is what this used to say. The panel is open and has
            // things in it; none of them read as a map. That is what a wrong fingerprint looks
            // like, and it is the one state where saying the ordinary thing sends somebody
            // looking in the wrong place entirely.
            return AtlasView.Closed with
            {
                Total = placed.Count,
                Status = $"the panel is open with {placed.Count} things in it, and none of them read as a map"
                         + " - press \"check the read\"",
            };
        }

        List<AtlasNode> live = Live(_studied, placed);

        // The ritual line, off the same panel and with the same live positions. Its own first
        // read is one byte, so this costs nothing while no line is being drawn.
        Ritual?.Service(panel, chain.UiRoot, live, RitualWorth);

        return Compose(live, settings, Volatile.Read(ref _grouping), _routes, _said, Cursor);
    }

    /// <summary>
    /// The studied nodes at the positions they have NOW, dropping any the panel did not place.
    /// </summary>
    /// <remarks>
    /// DROPPED, not left where it was last seen, and that one word is a bug this cost a while.
    /// Keeping the stale position looks harmless - a third of a second of lag is invisible on a
    /// label - and it is not harmless on a LINE. Dragging the atlas re-lays every node; a node
    /// that missed a tick is left behind by exactly the distance dragged, so every line to one
    /// of them becomes a ray with that same offset, and they are all PARALLEL because they all
    /// share it. Hundreds of them across the screen, worse the further the atlas is scrolled,
    /// which is exactly the shape of "everything that missed a tick is one scroll behind".
    ///
    /// It hid until the connections started reading: with no connections there were no lines,
    /// and a stale label a third of a second behind is a thing nobody sees.
    ///
    /// A node the panel did not place is a node the panel is not drawing, so there is nothing
    /// correct to draw for it - which is what the reference does, without remarking on it.
    /// </remarks>
    public static List<AtlasNode> Live(
        IReadOnlyList<AtlasNode> studied,
        IReadOnlyDictionary<ulong, Placed> placed)
    {
        ArgumentNullException.ThrowIfNull(studied);
        ArgumentNullException.ThrowIfNull(placed);

        var live = new List<AtlasNode>(studied.Count);
        foreach (AtlasNode node in studied)
        {
            if (placed.TryGetValue(node.Address, out Placed now))
            {
                // Shown comes from the LIVE read rather than from the studied one, with the
                // position: whether the game is drawing a node changes as the atlas is scrolled
                // and as fog lifts, which is exactly the rate the position changes at.
                live.Add(node with { Screen = now.Position, Size = now.Size, Shown = now.Shown });
            }
        }

        return live;
    }

    /// <summary>
    /// Turns a read of the atlas into the view that gets drawn.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM THE READING, and not for tidiness. Everything that can be got wrong here
    /// is a decision rather than an address - what order the hiding happens in, whether a route
    /// past the hop limit still leaves its map on the atlas, whether a connection is drawn once
    /// or twice - and none of it can be reached by a test through a live memory read. So the
    /// decisions live where a made-up atlas can be handed to them.
    /// </remarks>
    /// <param name="live">The nodes as just read, whose positions are this tick's.</param>
    /// <param name="routes">Every way across the atlas, from the last study.</param>
    /// <param name="words">What each node's contents say, from the last study.</param>
    public static AtlasView Compose(
        IReadOnlyList<AtlasNode> live,
        AtlasSettings settings,
        AtlasGrouping grouping,
        AtlasRoutes routes,
        IReadOnlyDictionary<(int X, int Y), IReadOnlyList<AtlasSaid>> words,
        Vector2 cursor = default)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grouping);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(words);

        // Where everything is, this tick. Taken from the LIVE read rather than from the
        // studied one, because that is the half that changes while somebody drags the atlas.
        var centres = new Dictionary<(int X, int Y), Vector2>(live.Count);
        foreach (AtlasNode node in live)
        {
            centres[node.Grid] = node.Screen + (node.Size * 0.5f);
        }

        string search = settings.Search.Trim();
        var marks = new List<AtlasMark>(live.Count);

        // Kept for the web, which is drawn between the maps that survive the hiding below -
        // and a node's own list is gone by then, because a mark carries no connections.
        var joined = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>(live.Count);

        foreach (AtlasNode node in live)
        {
            joined[node.Grid] = node.Connections;

            string name = grouping.Called(node.MapId);
            if (search.Length > 0 && !name.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool routed = grouping.RouteTo(node.MapId, node.State, out AtlasGroup? group);

            // Hiding runs AFTER the routing question. A map worth routing to is one nobody
            // has reached, so hiding the unreachable first would hide exactly the routes
            // somebody turned on - the reference learned this one and says so.
            if (settings.HideCompleted && node.State == AtlasNodeState.Completed && !routed)
            {
                continue;
            }

            if (settings.HideUnreachable && node.State == AtlasNodeState.Locked && !routed)
            {
                continue;
            }

            IReadOnlyList<IReadOnlyList<Vector2>> route = [];
            int hops = -1;
            if (routed)
            {
                IReadOnlyList<(int X, int Y)>? path = routes.To(node.Grid);
                if (path is { Count: > 0 })
                {
                    hops = path.Count - 1;
                    int limit = group?.MaxHops ?? 0;
                    if (limit <= 0 || hops <= limit)
                    {
                        route = Screened(path, centres);
                    }
                }
            }

            marks.Add(new AtlasMark(
                node.Grid,
                centres[node.Grid],
                node.MapId,
                name,
                node.State,
                group,
                settings.Contents && words.TryGetValue(node.Grid, out IReadOnlyList<AtlasSaid>? said) ? said : [],
                route,
                hops,
                settings.Ratings ? grouping.Rated(node.MapId) : null,

                // NOUGHT when the ratings are switched off, and that is what tells the drawing
                // the difference between the two ways of having no rating: switched off means
                // draw nothing, while a scale in force and no value on this map means the map
                // is UNRATED and should say so. Without this they are the same null.
                settings.Ratings ? grouping.BestRating : 0,
                settings.Biomes ? node.Biome : AtlasBiomes.None,
                node.Shown));
        }

        return new AtlasView(
            marks,
            settings.Web ? Weave(marks, joined) : [],
            live.Count,
            routes.Open,
            routes.Reachable,
            string.Empty,

            // WHATEVER THE SETTING SAYS. The hovered map is a fact about the screen, and the
            // setting is about what to do when the game's panel over it could not be measured -
            // a decision the overlay makes with the interface parts in hand, not this one.
            Hovered(live, cursor));
    }

    /// <summary>
    /// Where the map under the cursor is drawn, which is where the game puts its own panel.
    /// </summary>
    /// <remarks>
    /// GEOMETRY, because the game does not offer the answer. The AHK tool went looking: the
    /// world-entity hover chains (the MouseOver chain off InGameState, and the hover tracker)
    /// resolve AREA ENTITIES only and never see the interface, and two flat scans proved PoE2
    /// keeps no "hovered UiElement" slot anywhere - only whole panels are pointed at, never the
    /// leaf under the cursor. It solved the same problem for inventory items by descending the
    /// interface tree to whatever contains the cursor.
    ///
    /// Here that descent is one step: every map is a child of the one panel and its rectangle
    /// was read this tick anyway, so this is a walk of a list that already exists.
    ///
    /// Against ALL the maps rather than the drawn ones. The game shows its panel over a map
    /// whether or not this overlay chose to label it, and the labels and lines of OTHER maps
    /// are what would be drawn across it.
    ///
    /// THE RECTANGLE RATHER THAN A YES, because what is done with the answer has changed: the
    /// overlay used to switch itself off and now keeps off the panel the game put up. Where the
    /// node is drawn is what a reader looking for that panel has to start from, and it is also
    /// the one line a readout can print when the panel is not found.
    /// </remarks>
    public static ScreenRect? Hovered(IReadOnlyList<AtlasNode> live, Vector2 cursor)
    {
        ArgumentNullException.ThrowIfNull(live);

        // A cursor that has not been reported yet reads as the top-left corner, which is inside
        // any node that happens to be drawn there. Nought is "not asked", not a position.
        if (cursor == default)
        {
            return null;
        }

        foreach (AtlasNode node in live)
        {
            if (node.Size.X > 0 && node.Size.Y > 0
                && cursor.X >= node.Screen.X && cursor.X <= node.Screen.X + node.Size.X
                && cursor.Y >= node.Screen.Y && cursor.Y <= node.Screen.Y + node.Size.Y)
            {
                return new ScreenRect(
                    node.Screen.X, node.Screen.Y,
                    node.Screen.X + node.Size.X, node.Screen.Y + node.Size.Y);
            }
        }

        return null;
    }

    /// <summary>One account of the walk, from wherever the chain currently reaches.</summary>
    /// <remarks>
    /// Resolves the chain again rather than reusing the one <see cref="Build"/> just had,
    /// because the interesting case is the one where that failed - and a check that only runs
    /// when the read already worked reports on the wrong thing entirely.
    /// </remarks>
    private IReadOnlyList<string> Check(UiScale scale)
    {
        try
        {
            GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
            if (chain.UiRoot == 0)
            {
                return [chain.InGame ? "the UI root did not resolve" : "not in an area - the atlas is read from the interface"];
            }

            var said = new List<string>(_atlas.Describe(chain.UiRoot, scale));
            said.AddRange(Catalogue(scale, chain.UiRoot));
            return said;
        }
        catch (Exception exception)
        {
            return [$"the check itself failed: {exception.Message}"];
        }
    }

    /// <summary>
    /// The whole WorldAreas table, read through the first node that names it.
    /// </summary>
    /// <remarks>
    /// The study reads this by itself now (see <see cref="Learn"/>); what is left here is the
    /// EXHAUSTIVE attempt, which is what a person pressing the button is asking for. The study
    /// gives up after one candidate node because a failing chain fails on all of them, and this is
    /// where that assumption gets tested against every node on the atlas.
    /// </remarks>
    private IReadOnlyList<string> Catalogue(UiScale scale, ulong uiRoot)
    {
        // The nodes the last tick studied, when there are any: a second full read of the panel
        // here would cost as much as the walk it is only meant to start.
        IReadOnlyList<AtlasNode> nodes = _studied.Count > 0 ? _studied : _atlas.Read(uiRoot, scale);
        if (_catalogue.All.Count == 0)
        {
            foreach (AtlasNode node in nodes)
            {
                if (node.MapId.Length > 0 && _catalogue.ReadFromNode(node.Address))
                {
                    Names.LearnUnique(_catalogue.All);
                    break;
                }
            }
        }

        // Republished on every check, not only on the first: a check can be pressed with the atlas
        // scrolled somewhere the study path has not published from yet.
        foreach (AtlasNode node in nodes)
        {
            if (node.MapId.Length > 0)
            {
                _seen.Add(node.MapId);
            }
        }

        Republish();

        var said = new List<string> { string.Empty };
        said.AddRange(_catalogue.Describe(Names));
        return said;
    }

    /// <summary>
    /// Takes the slow half again: what each node is, and every route across the atlas.
    /// </summary>
    /// <remarks>
    /// The contents are turned into WORDS here rather than at drawing time, because that is
    /// this half's whole point - the words for a node change when the node does, which is
    /// never while somebody is looking at it.
    /// </remarks>
    private void Study(List<AtlasNode> live, int elements, long nowMs)
    {
        _studied = live;
        _studiedCount = elements;
        _studiedAt = nowMs;
        _routes = AtlasRoutes.From(live);

        bool fresh = false;
        _said.Clear();
        foreach (AtlasNode node in live)
        {
            _said[node.Grid] = Words(node, _contents);
            if (node.MapId.Length > 0)
            {
                fresh |= _seen.Add(node.MapId);
            }
        }

        // The report is rebuilt only when something in it changed - the table arriving, or a map
        // scrolled into view for the first time. Four hundred rows and eighty ratings are not a
        // thing to build three times a second for an atlas nobody moved.
        if (Learn(live) || fresh)
        {
            Republish();
        }
    }

    /// <summary>
    /// Reads WorldAreas once and hands the game's unique flag to the map table.
    /// </summary>
    /// <remarks>
    /// ONE CANDIDATE NODE PER STUDY, not all of them. A node carrying a map id either resolves its
    /// EndgameMaps row or the chain is wrong, and if the chain is wrong the next node fails
    /// identically - so walking the rest buys nothing and costs four pointer reads each.
    /// </remarks>
    /// <returns>Whether the table was read on this pass.</returns>
    private bool Learn(IReadOnlyList<AtlasNode> live)
    {
        if (_catalogueTries <= 0 || _catalogue.All.Count > 0)
        {
            return false;
        }

        foreach (AtlasNode node in live)
        {
            if (node.MapId.Length == 0)
            {
                continue;
            }

            // Spent HERE rather than on entry, so a study that read no map ids at all - which is
            // what a wrong node fingerprint looks like - does not burn the budget on nothing.
            _catalogueTries--;
            if (!_catalogue.ReadFromNode(node.Address))
            {
                return false;
            }

            Names.LearnUnique(_catalogue.All);
            return true;
        }

        return false;
    }

    /// <summary>Publishes the game-against-file report for the interface to draw.</summary>
    private void Republish()
        => Volatile.Write(ref _mapData, MapDataReport.Build(_catalogue, Names, Ratings, _seen));

    private void Forget()
    {
        _studied = [];
        _studiedCount = -1;
        _routes = AtlasRoutes.None;
        _said.Clear();
    }

    /// <summary>
    /// What a node's badges and tokens say, in words, de-duplicated.
    /// </summary>
    /// <remarks>
    /// The two arrive separately and overlap: a breach is a badge AND a token on the same
    /// node, and listing it twice is how a port announces that it has two tables rather than
    /// that the map has two breaches. The de-duplication is on the finished words rather than
    /// on the id, so "Contains 3 additional Shrines" survives beside a plain one.
    ///
    /// A BADGE NEVER CARRIES A NUMBER. It is the bold line at the top of the game's own
    /// tooltip - the name of the thing - and its high half is a category tag rather than a
    /// magnitude. Only the effect lines beneath it count anything, and only the ones whose
    /// wording has somewhere to put a number.
    ///
    /// AND WHAT THE GAME SAYS IT DOES NOT SHOW, this does not show either. One row of that table
    /// is a developer's placeholder - "[DNT] Breach City - Not Shown to Players", described as
    /// "DNT No visual identity = not shown" - and it went straight onto four maps of a real
    /// atlas, in the same plate as everything the game does mean to say. The row is KEPT, so its
    /// id stays known rather than being reported as one nothing has heard of; it is the drawing
    /// that skips it. See <see cref="Placeholder"/>.
    /// </remarks>
    public static IReadOnlyList<AtlasSaid> Words(AtlasNode node, AtlasContentNames contents)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(contents);

        var said = new List<AtlasSaid>();

        foreach (uint raw in node.BadgeIds)
        {
            // Label rather than Say: see the badge paragraph above. Its high half is a
            // category tag, and writing that into a "{0}" would number the thing with it.
            if (contents.Badge(raw) is { } badge)
            {
                Add(badge, badge.Label);
            }
        }

        foreach (uint raw in node.ContentTokens)
        {
            if (contents.Effect(raw) is { } effect)
            {
                Add(effect, effect.Say(raw));
            }
        }

        return said;

        void Add(AtlasContent content, string word)
        {
            if (word.Length == 0 || Placeholder(word))
            {
                return;
            }

            // A plain loop rather than Exists with a lambda: this runs for every content of
            // every node on a full atlas, and the lambda would capture `word` into a fresh
            // closure each time round.
            foreach (AtlasSaid already in said)
            {
                if (already.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            // THE DETAIL IS DROPPED WHEN IT REPEATS THE LINE, which is the usual case for an
            // effect: its label IS its description, so carrying both would put the same
            // sentence twice in a tooltip. A badge is the other way round - "Breach" against
            // "Area contains an Otherworldly Breach" - and that pair is the whole point.
            string detail = content.Description.Equals(word, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : content.Description;

            said.Add(new AtlasSaid(word, detail, content.Icon));
        }
    }

    /// <summary>
    /// Whether a content's wording is a placeholder rather than something to show a player.
    /// </summary>
    /// <remarks>
    /// A SQUARE BRACKET AT THE FRONT IS THE GAME'S OWN MARK for a row that is not finished
    /// content: "[DNT] Breach City - Not Shown to Players" in the content table, "[DNT-UNUSED]
    /// Vastiri Outpost" and "[DNT] Ship" among the map names. DNT is "do not translate", which
    /// is how a string that never reaches a player is flagged for the translators - and a
    /// string the translators are told to leave alone is a string nobody meant to be read.
    ///
    /// Matched on the BRACKET rather than on the letters DNT, because the bracket is the part
    /// that is consistent: the game has used "[UNUSED]" and bare "[...]" for the same thing, and
    /// a real content has never begun with one. It is deliberately not matched on the
    /// description, which says useful things about real content in every other row.
    ///
    /// Only the DRAWING is spared it. The table keeps the row, so the id is still recognised -
    /// dropping it would turn a known-and-hidden content into an unknown one, which is the state
    /// this project reports as something worth investigating.
    /// </remarks>
    public static bool Placeholder(string? word)
        => word is { Length: > 0 } && word[0] == '[';

    /// <summary>
    /// A route's grid positions turned into screen ones, BROKEN wherever a step is missing.
    /// </summary>
    /// <remarks>
    /// A route can pass through a map the panel has no position for - one the atlas has not
    /// materialised, or a grid position the edge table names that no map sits on. This used to
    /// drop the step and carry on, on the reasoning that a route with one corner cut still
    /// goes the right way. It does not: the line then runs STRAIGHT ACROSS the gap, along no
    /// connection anybody can walk, and with several steps missing what is drawn is a straight
    /// line between two maps that are nowhere near each other. That is the arbitrary line.
    ///
    /// So a missing step ENDS a run and the next found step starts a new one, which is what the
    /// reference does - its DrawNodePath sets its previous point back to nothing rather than
    /// joining across. What is drawn is then only ever real connections, with holes where the
    /// atlas cannot say.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<Vector2>> Screened(
        IReadOnlyList<(int X, int Y)> path,
        IReadOnlyDictionary<(int X, int Y), Vector2> centres)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(centres);

        var runs = new List<IReadOnlyList<Vector2>>();
        var run = new List<Vector2>();

        foreach ((int X, int Y) step in path)
        {
            if (centres.TryGetValue(step, out Vector2 at))
            {
                run.Add(at);
                continue;
            }

            Close();
        }

        Close();
        return runs;

        // A single point is a corner nobody can see, not a piece of route: two are needed
        // before there is a line, and keeping the stragglers would only put the entry dot
        // somewhere the route does not go.
        void Close()
        {
            if (run.Count >= 2)
            {
                runs.Add(run);
                run = [];
            }
            else
            {
                run.Clear();
            }
        }
    }

    /// <summary>Every connection between DRAWN maps, each one once.</summary>
    /// <remarks>
    /// BETWEEN THE MAPS ON SCREEN, not between every map on the atlas, and the difference is
    /// the whole of it: hiding the finished maps and the ones with no way there is how an
    /// atlas is made readable, and a line to a map that was hidden is a line to nothing. It
    /// went unnoticed while connections read as empty - the web drew nought lines and looked
    /// right - and announced itself the moment they worked, on an atlas grown to 1281 maps
    /// with 108 of them shown: two thousand lines across a screen showing a hundred nodes.
    ///
    /// Once, not twice: connections are mutual, so both ends list each other and drawing
    /// them as they come would put every line on the screen on top of itself. The lower grid
    /// position owns the line.
    /// </remarks>
    private static IReadOnlyList<(Vector2 From, Vector2 To)> Weave(
        IReadOnlyList<AtlasMark> drawn,
        IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> joined)
    {
        var centres = new Dictionary<(int X, int Y), Vector2>(drawn.Count);
        foreach (AtlasMark mark in drawn)
        {
            centres[mark.Grid] = mark.Where;
        }

        var lines = new List<(Vector2, Vector2)>();
        foreach (AtlasMark mark in drawn)
        {
            if (!joined.TryGetValue(mark.Grid, out IReadOnlyList<(int X, int Y)>? others))
            {
                continue;
            }

            foreach ((int X, int Y) other in others)
            {
                bool mine = mark.Grid.X < other.X || (mark.Grid.X == other.X && mark.Grid.Y <= other.Y);
                if (mine && centres.TryGetValue(other, out Vector2 to))
                {
                    lines.Add((mark.Where, to));
                }
            }
        }

        return lines;
    }
}
