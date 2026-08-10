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
/// The way here from the nearest map that can be entered now, in screen positions, or empty
/// when this is not a routing target. Includes both ends.
/// </param>
/// <param name="Hops">How many maps have to be run to get here. 0 for one you can enter now.</param>
public sealed record AtlasMark(
    (int X, int Y) Grid,
    Vector2 Where,
    string MapId,
    string Name,
    AtlasNodeState State,
    AtlasGroup? Group,
    IReadOnlyList<string> Contents,
    IReadOnlyList<Vector2> Route,
    int Hops);

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
    string Status)
{
    public static AtlasView Closed { get; } = new([], [], 0, 0, 0, "atlas closed");

    /// <summary>Whether there is anything at all to draw.</summary>
    public bool Anything => Marks.Count > 0 || Web.Count > 0;
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

    public AtlasWatch(
        IMemoryReader reader,
        OffsetSchema schema,
        ulong gameStatesStatic,
        AtlasContentNames? contents = null,
        AtlasMapNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _atlas = new AtlasReader(reader, schema, new UiElementReader(reader, schema));
        _contents = contents ?? AtlasContentNames.Empty;
        _grouping = new AtlasGrouping(_settings.Sorting, names ?? AtlasMapNames.Empty);
        Names = names ?? AtlasMapNames.Empty;
    }

    /// <summary>The map names in force, kept so settings can be replaced without them.</summary>
    public AtlasMapNames Names { get; }

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
            Volatile.Write(ref _grouping, new AtlasGrouping(value.Sorting, Names));
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

        // WHERE the maps are, every tick, because that is the half that changes while somebody
        // drags the atlas about. Reading it says whether the panel is open at all, so nothing
        // more expensive happens while it is shut.
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

        // The studied nodes, moved to where they are now. A node the panel has since dropped
        // keeps the position it was last seen at for up to one interval, which is invisible
        // next to re-reading every id thirty times a second to avoid it.
        var live = new List<AtlasNode>(_studied.Count);
        foreach (AtlasNode node in _studied)
        {
            live.Add(placed.TryGetValue(node.Address, out (Vector2 Position, Vector2 Size) now)
                ? node with { Screen = now.Position, Size = now.Size }
                : node);
        }

        // The ritual line, off the same panel and with the same live positions. Its own first
        // read is one byte, so this costs nothing while no line is being drawn.
        Ritual?.Service(panel, chain.UiRoot, live, RitualWorth);

        return Compose(live, settings, Volatile.Read(ref _grouping), _routes, _said);
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
        IReadOnlyDictionary<(int X, int Y), IReadOnlyList<string>> words)
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

        foreach (AtlasNode node in live)
        {
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

            IReadOnlyList<Vector2> route = [];
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
                hops));
        }

        return new AtlasView(
            marks,
            settings.Web ? Weave(live, centres) : [],
            live.Count,
            routes.Open,
            routes.Reachable,
            string.Empty);
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

    /// <summary>A route's grid positions turned into screen ones, dropping any not drawn.</summary>
    /// <remarks>
    /// A route can pass through a node the atlas has scrolled away from, which has no position
    /// this tick. Dropping the point rather than the route keeps the line going the right way
    /// with one corner cut - and the alternative is a route that vanishes whenever it happens
    /// to cross the edge of the screen.
    /// </remarks>
    private static IReadOnlyList<Vector2> Screened(
        IReadOnlyList<(int X, int Y)> path,
        Dictionary<(int X, int Y), Vector2> centres)
    {
        var drawn = new List<Vector2>(path.Count);
        foreach ((int X, int Y) step in path)
        {
            if (centres.TryGetValue(step, out Vector2 at))
            {
                drawn.Add(at);
            }
        }

        return drawn.Count >= 2 ? drawn : [];
    }

    /// <summary>Every connection between drawn nodes, each one once.</summary>
    /// <remarks>
    /// Once, not twice: connections are mutual, so both ends list each other and drawing
    /// them as they come would put every line on the screen on top of itself. The lower grid
    /// position owns the line.
    /// </remarks>
    private static IReadOnlyList<(Vector2 From, Vector2 To)> Weave(
        IReadOnlyList<AtlasNode> live,
        Dictionary<(int X, int Y), Vector2> centres)
    {
        var lines = new List<(Vector2, Vector2)>();
        foreach (AtlasNode node in live)
        {
            if (!centres.TryGetValue(node.Grid, out Vector2 from))
            {
                continue;
            }

            foreach ((int X, int Y) other in node.Connections)
            {
                bool mine = node.Grid.X < other.X || (node.Grid.X == other.X && node.Grid.Y <= other.Y);
                if (mine && centres.TryGetValue(other, out Vector2 to))
                {
                    lines.Add((from, to));
                }
            }
        }

        return lines;
    }
}
