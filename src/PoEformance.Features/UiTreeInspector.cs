using System.Numerics;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>
/// What the browser wants to see. Published by the window, read by the reader thread.
/// </summary>
/// <remarks>
/// STATE, not commands, for everything that persists: which node is selected, which are open,
/// what the query is. A queue of commands would need draining, ordering and a lifetime; a
/// state record is published with one reference assignment and the reader simply serves the
/// newest one - the same trick <see cref="SnapshotFeed"/> uses in the other direction.
///
/// The two sequence numbers are how a ONE-SHOT action fits into that. "Pick what is under
/// the cursor" is not a state - re-serving it every tick would drag the selection along with
/// the mouse - so the window increments a counter and the reader acts once per new value.
/// </remarks>
/// <param name="Root">Where to browse from. 0 means the game's own UI root.</param>
/// <param name="Expanded">Nodes whose children are wanted - exactly the open tree rows.</param>
public sealed record UiTreeRequest(
    bool Enabled = false,
    ulong Root = 0,
    IReadOnlySet<ulong>? Expanded = null,
    ulong Selected = 0,
    string Search = "",
    int SearchSequence = 0,
    int PickSequence = 0,
    Vector2 Cursor = default)
{
    public static UiTreeRequest Idle { get; } = new();

    public IReadOnlySet<ulong> ExpandedOrEmpty => Expanded ?? EmptySet;

    private static readonly HashSet<ulong> EmptySet = [];
}

/// <summary>What the reader found. Immutable, published whole, drawn as-is.</summary>
/// <param name="Nodes">The root and the children of every expanded node, by address.</param>
/// <param name="SelectedPath">
/// Child indices from the root down to the selection - the "[35][2][0]" form a path gets
/// written down in.
/// </param>
/// <param name="PickChain">Root-to-leaf addresses of the last pick, so the tree can open to it.</param>
public sealed record UiTreeView(
    ulong Root,
    IReadOnlyDictionary<ulong, UiNode> Nodes,
    UiNode? Selected,
    IReadOnlyList<int> SelectedPath,
    IReadOnlyList<UiNode> SearchResults,
    string SearchQuery,
    IReadOnlyList<ulong> PickChain,
    int PickSequence,
    string Status)
{
    public static UiTreeView Empty { get; } =
        new(0, new Dictionary<ulong, UiNode>(), null, [], [], string.Empty, [], 0, "not connected");
}

/// <summary>
/// Serves the UI browser from the reader thread.
/// </summary>
/// <remarks>
/// The browser needs reads that follow what the user is doing - open this node, search that
/// subtree, what is under the cursor - and the renderer is the one place those reads must not
/// happen: it is the thread drawing frames, and a search across the whole interface would
/// stall every one of them. So the window states what it wants, this serves it wherever the
/// reading already happens, and the window draws the answer that came back.
///
/// It is also OFF unless the browser is open. A tree read per tick is cheap next to the
/// entity map, and it is still pure waste while nobody is looking at it.
/// </remarks>
public sealed class UiTreeInspector
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly UiTreeReader _tree;
    private readonly ulong _gameStatesStatic;

    // Both crossing the thread boundary by reference assignment, like the snapshot feed:
    // the records are immutable, so a half-built one cannot be observed.
    private UiTreeRequest _request = UiTreeRequest.Idle;
    private UiTreeView _view = UiTreeView.Empty;

    // Serviced-once markers for the sequence-numbered actions.
    private int _servedSearch;
    private int _servedPick;
    private IReadOnlyList<UiNode> _lastResults = [];
    private string _lastQuery = string.Empty;
    private IReadOnlyList<ulong> _lastPick = [];

    public UiTreeInspector(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _tree = new UiTreeReader(reader, schema);
    }

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public UiTreeView View => Volatile.Read(ref _view);

    /// <summary>Tells the reader what to look at. Called from the render thread.</summary>
    public void Request(UiTreeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>
    /// Reads one tree view. Called on the reader thread, in the same pass as a world read.
    /// </summary>
    public void Service(UiScale scale)
    {
        UiTreeRequest request = Volatile.Read(ref _request);
        if (!request.Enabled)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _view, Build(request, scale));
        }
        catch (Exception exception)
        {
            // Same rule as the snapshot feed: a stale view beats a dead servicing thread,
            // and every pointer here goes stale on a zone change by design.
            Volatile.Write(ref _view, View with { Status = $"read failed: {exception.Message}" });
        }
    }

    private UiTreeView Build(UiTreeRequest request, UiScale scale)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        ulong root = request.Root != 0 ? request.Root : chain.UiRoot;

        if (root == 0 || !_tree.IsElement(root))
        {
            return UiTreeView.Empty with
            {
                Status = chain.InGame ? "UI root did not resolve" : "not in an area",
            };
        }

        // The pick first: it can change what is worth reading, and serving it in the same
        // pass means the click and the tree that opens to it are one answer, not two.
        if (request.PickSequence != _servedPick)
        {
            _servedPick = request.PickSequence;
            _lastPick = _tree.HitTest(root, request.Cursor, scale);
        }

        if (request.SearchSequence != _servedSearch)
        {
            _servedSearch = request.SearchSequence;
            _lastQuery = request.Search;
            _lastResults = _tree.Search(root, request.Search, scale);
        }

        // The pick's chain is opened implicitly - the window asked for a node it cannot have
        // known the address of, so waiting for it to ask again would cost a whole round trip
        // and show a collapsed tree in between.
        IReadOnlySet<ulong> expanded = request.ExpandedOrEmpty;
        if (_lastPick.Count > 0)
        {
            var union = new HashSet<ulong>(expanded);
            union.UnionWith(_lastPick);
            expanded = union;
        }

        Dictionary<ulong, UiNode> nodes = _tree.ReadTree(root, scale, expanded);

        // The selection may sit outside the read tree - picked from screen, or found by a
        // search into a subtree nobody opened - so it is resolved on its own rather than
        // looked up, and the detail pane never goes blank because of where a node happens
        // to be.
        UiNode? selected = null;
        IReadOnlyList<int> path = [];
        if (request.Selected != 0)
        {
            selected = nodes.TryGetValue(request.Selected, out UiNode? found)
                ? found
                : _tree.ReadOne(request.Selected, scale);

            if (selected is not null)
            {
                path = _tree.IndexPath(root, request.Selected);
            }
        }

        return new UiTreeView(
            root, nodes, selected, path, _lastResults, _lastQuery, _lastPick, _servedPick,
            $"{nodes.Count} nodes read");
    }
}
