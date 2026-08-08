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
/// WHERE it is changes every time the atlas is dragged, so that is read every tick. Reading the
/// first at the second's rate is what makes a naive port cost more than it is worth.
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
    private AtlasRoutes _routes = AtlasRoutes.None;
    private readonly Dictionary<(int X, int Y), IReadOnlyList<string>> _said = [];
    private long _studiedAt;
    private int _studiedCount = -1;

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

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public AtlasView View => Volatile.Read(ref _view);

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
    }

    private AtlasView Build(AtlasSettings settings, UiScale scale, long nowMs)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        if (chain.UiRoot == 0)
        {
            Forget();
            return AtlasView.Closed with { Status = chain.InGame ? "UI root did not resolve" : "not in an area" };
        }

        List<AtlasNode> live = _atlas.Read(chain.UiRoot, scale);
        if (live.Count == 0)
        {
            Forget();
            return AtlasView.Closed;
        }

        // The slow half: what each node is, and every route across the atlas. Redone on the
        // interval, and immediately when the atlas has a different number of nodes than it
        // had - which is what a region being opened looks like from here.
        if (live.Count != _studiedCount || nowMs - _studiedAt >= RestudyMs || nowMs < _studiedAt)
        {
            Study(live, nowMs);
        }

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

    /// <summary>
    /// Takes the slow half again: what each node is, and every route across the atlas.
    /// </summary>
    /// <remarks>
    /// The contents are turned into WORDS here rather than at drawing time, because that is
    /// this half's whole point - the words for a node change when the node does, which is
    /// never while somebody is looking at it.
    /// </remarks>
    private void Study(List<AtlasNode> live, long nowMs)
    {
        _studiedCount = live.Count;
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
    /// that the map has two breaches. A magnitude is kept - "Ritual x3" is a different map
    /// from "Ritual" - so the de-duplication is on the finished words rather than on the id.
    /// </remarks>
    public static IReadOnlyList<string> Words(AtlasNode node, AtlasContentNames contents)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(contents);

        var said = new List<string>();

        foreach (uint raw in node.BadgeIds)
        {
            Add(contents.Badge(raw), raw);
        }

        foreach (uint raw in node.ContentTokens)
        {
            Add(contents.Effect(raw), raw);
        }

        return said;

        void Add(AtlasContent? content, uint raw)
        {
            if (content is not { } known || known.Label.Length == 0)
            {
                return;
            }

            uint many = AtlasContentNames.MagnitudeOf(raw);
            string word = many > 1 ? $"{known.Label} x{many}" : known.Label;

            if (!said.Contains(word, StringComparer.OrdinalIgnoreCase))
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
