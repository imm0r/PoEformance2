using System.Numerics;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

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
public sealed record AtlasMark(
    (int X, int Y) Grid,
    Vector2 Where,
    string MapId,
    string Name,
    AtlasNodeState State,
    AtlasGroup? Group,
    IReadOnlyList<string> Contents,
    IReadOnlyList<IReadOnlyList<Vector2>> Route,
    int Hops,
    int? Rating = null,
    int BestRating = 0,
    int Biome = AtlasBiomes.None);

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
    bool Hovering = false)
{
    public static AtlasView Closed { get; } = new([], [], 0, 0, 0, "atlas closed");

    /// <summary>Whether there is anything at all to draw.</summary>
    /// <remarks>
    /// False while a map is hovered, because the game is showing its own panel over that node
    /// and everything here would be drawn across it. See <see cref="AtlasWatch.Hovered"/>.
    /// </remarks>
    public bool Anything => !Hovering && (Marks.Count > 0 || Web.Count > 0);
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
    private readonly Dictionary<(int X, int Y), IReadOnlyList<string>> _said = [];
    private long _studiedAt;
    private int _studiedCount = -1;

    // The one-shot check, as a sequence number rather than a queued command - the same trick
    // the interface browser uses. A flag would be re-served every tick; a queue would need
    // draining, ordering and a lifetime for one button.
    private int _checkWanted;
    private int _checkServed;
    private IReadOnlyList<string> _checked = [];

    private IReadOnlyDictionary<string, int> _ritualWorth = new Dictionary<string, int>();

    private float _cursorX;
    private float _cursorY;

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
        _contents = contents ?? AtlasContentNames.Empty;
        Names = names ?? AtlasMapNames.Empty;
        Ratings = ratings ?? AtlasRatings.Empty;
        _grouping = new AtlasGrouping(_settings.Sorting, Names, Ratings);
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
        Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed = _atlas.Where(panel, scale);

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
        IReadOnlyDictionary<ulong, (Vector2 Position, Vector2 Size)> placed)
    {
        ArgumentNullException.ThrowIfNull(studied);
        ArgumentNullException.ThrowIfNull(placed);

        var live = new List<AtlasNode>(studied.Count);
        foreach (AtlasNode node in studied)
        {
            if (placed.TryGetValue(node.Address, out (Vector2 Position, Vector2 Size) now))
            {
                live.Add(node with { Screen = now.Position, Size = now.Size });
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
        IReadOnlyDictionary<(int X, int Y), IReadOnlyList<string>> words,
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
                settings.Contents && words.TryGetValue(node.Grid, out IReadOnlyList<string>? said) ? said : [],
                route,
                hops,
                settings.Ratings ? grouping.Rated(node.MapId) : null,

                // NOUGHT when the ratings are switched off, and that is what tells the drawing
                // the difference between the two ways of having no rating: switched off means
                // draw nothing, while a scale in force and no value on this map means the map
                // is UNRATED and should say so. Without this they are the same null.
                settings.Ratings ? grouping.BestRating : 0,
                settings.Biomes ? node.Biome : AtlasBiomes.None));
        }

        return new AtlasView(
            marks,
            settings.Web ? Weave(marks, joined) : [],
            live.Count,
            routes.Open,
            routes.Reachable,
            string.Empty,
            settings.HideOnHover && Hovered(live, cursor));
    }

    /// <summary>
    /// Whether the cursor is on a map, which is when the game shows its own panel about it.
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
    /// </remarks>
    public static bool Hovered(IReadOnlyList<AtlasNode> live, Vector2 cursor)
    {
        ArgumentNullException.ThrowIfNull(live);

        // A cursor that has not been reported yet reads as the top-left corner, which is inside
        // any node that happens to be drawn there. Nought is "not asked", not a position.
        if (cursor == default)
        {
            return false;
        }

        foreach (AtlasNode node in live)
        {
            if (node.Size.X > 0 && node.Size.Y > 0
                && cursor.X >= node.Screen.X && cursor.X <= node.Screen.X + node.Size.X
                && cursor.Y >= node.Screen.Y && cursor.Y <= node.Screen.Y + node.Size.Y)
            {
                return true;
            }
        }

        return false;
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

            return _atlas.Describe(chain.UiRoot, scale);
        }
        catch (Exception exception)
        {
            return [$"the check itself failed: {exception.Message}"];
        }
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

        _said.Clear();
        foreach (AtlasNode node in live)
        {
            _said[node.Grid] = Words(node, _contents);
        }
    }

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
    /// </remarks>
    public static IReadOnlyList<string> Words(AtlasNode node, AtlasContentNames contents)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(contents);

        var said = new List<string>();

        foreach (uint raw in node.BadgeIds)
        {
            Add(contents.Badge(raw)?.Label);
        }

        foreach (uint raw in node.ContentTokens)
        {
            Add(contents.Effect(raw)?.Say(raw));
        }

        return said;

        void Add(string? word)
        {
            if (!string.IsNullOrEmpty(word) && !said.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                said.Add(word);
            }
        }
    }

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
