using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>How far somebody has got with one map on the atlas.</summary>
public enum AtlasNodeState
{
    /// <summary>Not reachable yet - the path to it has not been cleared.</summary>
    Locked,

    /// <summary>Reachable and not finished.</summary>
    Open,

    /// <summary>Run at least once.</summary>
    Completed,
}

/// <summary>
/// One map on the endgame atlas.
/// </summary>
/// <param name="MapId">
/// The game's own id for the map - <c>MapUniqueReactor_04</c> - which is the same string on
/// every client. The displayed name is not: whatever the language, this is what to match on.
/// </param>
/// <param name="Grid">Where it sits on the atlas, in the game's own grid.</param>
/// <param name="Connections">
/// The grid positions this node has a line to, from the panel's own table of them. A position
/// here need not be a node in the same read: the atlas connects to maps the fog has not shown
/// yet, and those have no element to be found on.
/// </param>
/// <param name="Biome">Which biome it belongs to, as the game numbers them.</param>
/// <param name="Screen">Where it is drawn, for putting anything on top of it.</param>
/// <param name="BadgeIds">
/// The named contents - the bold line at the top of the game's own tooltip. Ids only; a badge
/// carries no magnitude, whichever of the two places it came from. See
/// <see cref="World.AtlasContentNames"/>.
/// </param>
/// <param name="ContentTokens">
/// The contents that arrive as bare numbers instead, RAW: the low half identifies the effect
/// and the high half is how much of it there is. Only a node the game has actually drawn has
/// them, so an atlas scrolled far away reports none - which is not the same as a map with
/// nothing in it.
/// </param>
public sealed record AtlasNode(
    int Index,
    ulong Address,
    string MapId,
    (int X, int Y) Grid,
    AtlasNodeState State,
    byte Biome,
    IReadOnlyList<(int X, int Y)> Connections,
    Vector2 Screen,
    Vector2 Size,
    IReadOnlyList<uint> BadgeIds,
    IReadOnlyList<uint> ContentTokens);

/// <summary>
/// Reads the endgame atlas out of the game's interface.
/// </summary>
/// <remarks>
/// PORTED FROM GameHelper2's Atlas2 plugin and the ImportantUiElements that feed it, which in
/// turn credit yokkenUA's Atlas plugin. NONE OF IT IS CONFIRMED against this client yet - it
/// was written from the reference while the game was not available, so every offset in the
/// schema's Atlas blocks is a hypothesis and the first live run should expect to correct some.
/// <see cref="Describe"/> exists for exactly that: it reports what each step found so the
/// broken step names itself, instead of the whole thing coming back empty.
///
/// The atlas is INTERFACE, not world. Its nodes are UiElements at a fixed child path, so this
/// reads nothing while the panel is closed and costs nothing then either.
///
/// The children of that panel are not all nodes: the you-are-here marker and the region
/// buttons live in the same list with completely different layouts. They are told apart by the
/// UiElement flags word used as a type fingerprint, and that check is not optional - the
/// reference records that following the node pointer chain on the wrong child read a bogus
/// child count and froze the overlay on a multi-megabyte read.
/// </remarks>
public sealed class AtlasReader
{
    /// <summary>Most nodes walked in one pass. A guard on a count that comes from memory.</summary>
    public const int MostNodes = 4096;

    /// <summary>
    /// Most lines taken off the atlas at once, for the same reason.
    /// </summary>
    /// <remarks>
    /// A full atlas draws a few thousand, so this is generous rather than tight - what it is
    /// for is the read where the two pointers are not a vector at all and their difference is
    /// whatever two unrelated words happen to be.
    /// </remarks>
    public const int MostEdges = 16_384;

    /// <summary>And most contents on one node, which is the same guard on a different length.</summary>
    public const int MostContents = 64;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly int[] _path;
    private readonly uint _visibleMask;
    private readonly uint _mapNodeFp;
    private readonly uint _mistNodeFp;
    private readonly int _flags;

    private readonly int _edges;
    private readonly int _edgeStride;
    private readonly int _edgeSource;
    private readonly int _edgeTarget;

    private readonly int _grid;
    private readonly int _contentVector;
    private readonly int _badgeBegin;
    private readonly int _badgeEnd;
    private readonly int[] _badgeChildPath;
    private readonly int _badgeContentId;
    private readonly uint _badgeRowToContentId;
    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _completedBit;
    private readonly int _accessibleBit;

    private readonly int _mapData;
    private readonly int _biome;
    private readonly int _status;

    public AtlasReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef panel = schema.Structs["AtlasPanel"];
        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];

        _path =
        [
            (int)panel.Constants["PathFromUiRoot0"],
            (int)panel.Constants["PathFromUiRoot1"],
            (int)panel.Constants["PathFromUiRoot2"],
        ];

        _visibleMask = (uint)panel.Constants["VisibleMask"];
        _mapNodeFp = (uint)panel.Constants["MapNodeFingerprint"];
        _mistNodeFp = (uint)panel.Constants["MistNodeFingerprint"];
        _flags = schema.Structs["UiElementBase"].OffsetOf("Flags");

        _edges = panel.OffsetOf("ConnectionsVector");
        _edgeStride = (int)panel.Constants["ConnectionEdgeStride"];
        _edgeSource = (int)panel.Constants["ConnectionEdgeSource"];
        _edgeTarget = (int)panel.Constants["ConnectionEdgeTarget"];

        _grid = node.OffsetOf("GridPosition");
        _contentVector = node.OffsetOf("ContentVector");
        _badgeBegin = node.OffsetOf("BadgeVectorBegin");
        _badgeEnd = node.OffsetOf("BadgeVectorEnd");
        _badgeChildPath = [(int)node.Constants["BadgeChild0"], (int)node.Constants["BadgeChild1"]];
        _badgeContentId = (int)node.Constants["BadgeContentId"];
        _badgeRowToContentId = (uint)node.Constants["BadgeRowToContentId"];
        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _completedBit = (int)node.Constants["CompletedBit"];
        _accessibleBit = (int)node.Constants["AccessibleBit"];

        _mapData = data.OffsetOf("MapDataPtr");
        _biome = data.OffsetOf("BiomeId");
        _status = data.OffsetOf("StatusBits");
    }

    /// <summary>The atlas panel, or zero when it is not there - which is most of the time.</summary>
    public ulong Panel(ulong uiRoot)
    {
        ulong at = uiRoot;
        foreach (int index in _path)
        {
            if (at == 0)
            {
                return 0;
            }

            at = _elements.Child(at, index);
        }

        return at;
    }

    /// <summary>
    /// Every map on the atlas, or an empty list when the panel is closed.
    /// </summary>
    /// <remarks>
    /// Read fresh rather than cached, because the caller decides how often it wants this and
    /// the answer changes as somebody scrolls: the positions are live, and a node that was not
    /// rendered has no content to read.
    /// </remarks>
    public List<AtlasNode> Read(ulong uiRoot, UiScale scale)
    {
        var found = new List<AtlasNode>();
        ulong panel = Panel(uiRoot);
        if (panel == 0)
        {
            return found;
        }

        List<ulong> children = _elements.Children(panel, MostNodes);

        // Every node is a child of the one panel, so where they are drawn is read for all of
        // them at once - the chain above the panel is identical for each and re-walking it per
        // node is most of what reading an atlas would otherwise cost.
        Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed =
            _elements.ReadSiblings(panel, children, scale);

        // And the same for the lines: they are ONE table on the panel, so this is a single
        // read rather than a question asked of each node in turn.
        Dictionary<(int X, int Y), List<(int X, int Y)>> lines = Lines(panel);

        for (int i = 0; i < children.Count; i++)
        {
            if (Node(i, children[i], placed, lines) is AtlasNode node)
            {
                found.Add(node);
            }
        }

        return found;
    }

    /// <summary>
    /// Every line on the atlas, as the neighbours of each grid position.
    /// </summary>
    /// <remarks>
    /// THE PANEL OWNS THIS, not the nodes. The game keeps one flat vector of edges - an
    /// unknown word, the grid position at each end - and a node has no list of its own. Reading
    /// it as if it did is what this used to do, at the same offset on the wrong object: most
    /// nodes then reported no connections and the occasional one reported invented ones, which
    /// is a route to the right map along a way nobody can walk.
    ///
    /// Undirected, because the atlas is: a line listed once as source-to-target is walkable
    /// both ways, and a search that only followed it forwards would call half the atlas
    /// unreachable.
    ///
    /// ONE READ of the whole table. It is a few thousand edges on a full atlas, and the cost of
    /// reading game memory is the number of calls rather than the number of bytes.
    /// </remarks>
    public Dictionary<(int X, int Y), List<(int X, int Y)>> Lines(ulong panel)
    {
        var joined = new Dictionary<(int X, int Y), List<(int X, int Y)>>();

        ulong first = _reader.ReadPointer(panel + (ulong)_edges);
        ulong last = _reader.ReadPointer(panel + (ulong)_edges + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return joined;
        }

        long span = (long)(last - first);
        long count = span / _edgeStride;
        if (span % _edgeStride != 0 || count <= 0 || count > MostEdges)
        {
            return joined;   // a length out of game memory, treated as a bad read
        }

        var bytes = new byte[count * _edgeStride];
        if (!_reader.TryRead(first, bytes))
        {
            return joined;
        }

        for (long i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> edge = bytes.AsSpan((int)(i * _edgeStride), _edgeStride);
            (int X, int Y) from = Pair(edge[_edgeSource..]);
            (int X, int Y) to = Pair(edge[_edgeTarget..]);

            // An empty slot reads as (0,0), and a node joined to itself is not a line. Both
            // would otherwise become a neighbour that sends a route nowhere.
            if (to == (0, 0) || to == from)
            {
                continue;
            }

            Join(joined, from, to);
            Join(joined, to, from);
        }

        return joined;

        static (int X, int Y) Pair(ReadOnlySpan<byte> at)
            => (BitConverter.ToInt32(at), BitConverter.ToInt32(at[4..]));

        static void Join(
            Dictionary<(int X, int Y), List<(int X, int Y)>> all, (int X, int Y) from, (int X, int Y) to)
        {
            if (!all.TryGetValue(from, out List<(int X, int Y)>? mine))
            {
                mine = new List<(int X, int Y)>(4);
                all[from] = mine;
            }

            if (!mine.Contains(to))
            {
                mine.Add(to);
            }
        }
    }

    /// <summary>
    /// Where each map is drawn NOW, and nothing else.
    /// </summary>
    /// <remarks>
    /// The fast half of reading an atlas. What a node IS cannot change while somebody looks at
    /// it, but WHERE it is changes every frame they drag the panel about - so this is what a
    /// caller repeats, and <see cref="Read"/> is what it repeats rarely.
    ///
    /// Keyed by the node's own address, which is what makes the two halves fit together: the
    /// slow half's nodes carry theirs, so a position found here belongs to a known map without
    /// anything having to match them up by order.
    ///
    /// Takes the PANEL rather than the interface root, unlike <see cref="Read"/>: this runs every
    /// tick and the caller has the panel already, so resolving it again here would be three
    /// child reads a tick to arrive at an address that was passed over.
    /// </remarks>
    public Dictionary<ulong, (Vector2 Position, Vector2 Size)> Where(ulong panel, UiScale scale)
        => panel == 0
            ? []
            : _elements.ReadSiblings(panel, _elements.Children(panel, MostNodes), scale);

    /// <summary>One child of the panel, when it turns out to be a map.</summary>
    private AtlasNode? Node(
        int index,
        ulong element,
        Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed,
        Dictionary<(int X, int Y), List<(int X, int Y)>> lines)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(element) || !IsMapNode(element))
        {
            return null;
        }

        // Two hops off the element, and the data is at the end of them - the element itself
        // carries only the grid position and the drawing.
        ulong storage = _reader.ReadPointer(element + (ulong)_dataStorage);
        if (!MemoryReaderExtensions.IsPlausiblePointer(storage))
        {
            return null;
        }

        ulong data = _reader.ReadPointer(storage + (ulong)_data);
        if (!MemoryReaderExtensions.IsPlausiblePointer(data))
        {
            return null;
        }

        if (!_reader.TryRead(data + (ulong)_status, out byte status))
        {
            return null;
        }

        _reader.TryRead(data + (ulong)_biome, out byte biome);
        int gridX = _reader.Read<int>(element + (ulong)_grid);
        int gridY = _reader.Read<int>(element + (ulong)_grid + 4);

        placed.TryGetValue(element, out (Vector2 Position, Vector2 Size) drawn);

        return new AtlasNode(
            index,
            element,
            MapId(data),
            (gridX, gridY),
            StateOf(status),
            biome,
            lines.TryGetValue((gridX, gridY), out List<(int X, int Y)>? joined) ? joined : [],
            drawn.Position,
            drawn.Size,
            Badges(element),
            Tokens(element));
    }

    /// <summary>Whether a panel child is a map rather than a marker or a region button.</summary>
    /// <remarks>
    /// The visible bit is masked off first: the same node reads two different flag words
    /// depending on whether it is on screen, and a fingerprint that changed when somebody
    /// scrolled would be no fingerprint at all.
    /// </remarks>
    private bool IsMapNode(ulong element)
    {
        if (!_reader.TryRead(element + (ulong)_flags, out uint flags))
        {
            return false;
        }

        uint kind = flags & ~_visibleMask;
        return kind == (_mapNodeFp & ~_visibleMask) || kind == (_mistNodeFp & ~_visibleMask);
    }

    /// <summary>A status byte read as the two bits it is.</summary>
    private AtlasNodeState StateOf(byte status)
        => (status & _completedBit) != 0
            ? AtlasNodeState.Completed
            : (status & _accessibleBit) != 0
                ? AtlasNodeState.Open
                : AtlasNodeState.Locked;

    /// <summary>
    /// The map's own id, three pointers down.
    /// </summary>
    /// <remarks>
    /// Wrapper, then string header, then buffer - and the last one is a bare wide string
    /// rather than a std::wstring, so it is read to its terminator rather than to a length.
    /// </remarks>
    private string MapId(ulong data)
    {
        ulong wrapper = _reader.ReadPointer(data + (ulong)_mapData);
        if (!MemoryReaderExtensions.IsPlausiblePointer(wrapper))
        {
            return string.Empty;
        }

        ulong header = _reader.ReadPointer(wrapper);
        if (!MemoryReaderExtensions.IsPlausiblePointer(header))
        {
            return string.Empty;
        }

        ulong buffer = _reader.ReadPointer(header);
        return MemoryReaderExtensions.IsPlausiblePointer(buffer)
            ? _reader.ReadUnicodeString(buffer, 64)
            : string.Empty;
    }

    /// <summary>
    /// The named contents of a node, from BOTH of the places the game keeps them.
    /// </summary>
    /// <remarks>
    /// TWO STORES, and neither one is enough. The badge children are drawn, so they are culled
    /// for a node under fog or off the edge of the screen; the byte vector is filled from the
    /// server's cell state and survives that, but it can only hold what a byte can - so the
    /// contents whose ids run past 255 (Corruption, Grand Mirror) are only ever children.
    ///
    /// The vector is a vector of BYTES. Reading it as a vector of pointers - which this used to
    /// do - divides its length by eight, so a node with fewer than eight badges reported none
    /// at all, and one with more read whatever was at the front of the buffer as an address.
    /// </remarks>
    private List<uint> Badges(ulong element)
    {
        var ids = new List<uint>();

        ulong first = _reader.ReadPointer(element + (ulong)_badgeBegin);
        ulong last = _reader.ReadPointer(element + (ulong)_badgeEnd);
        if (MemoryReaderExtensions.IsPlausiblePointer(first) && last > first)
        {
            long count = (long)(last - first);
            if (count > 0 && count <= MostContents)
            {
                var rows = new byte[count];
                if (_reader.TryRead(first, rows))
                {
                    foreach (byte row in rows)
                    {
                        Keep(row + _badgeRowToContentId);
                    }
                }
            }
        }

        ulong holder = element;
        foreach (int step in _badgeChildPath)
        {
            holder = _elements.Child(holder, step);
        }

        foreach (ulong badge in _elements.Children(holder, MostContents))
        {
            if (_reader.TryRead(badge + (ulong)_badgeContentId, out uint id) && id != 0)
            {
                Keep(id);
            }
        }

        return ids;

        // On the id ALONE, not on the whole word: the same content arrives from the vector as
        // a bare id and from a child with a category tag above it, and comparing the words
        // would list a breach twice.
        void Keep(uint id)
        {
            uint mine = World.AtlasContentNames.IdOf(id);
            if (!ids.Exists(seen => World.AtlasContentNames.IdOf(seen) == mine))
            {
                ids.Add(id);
            }
        }
    }

    /// <summary>The contents that are just numbers in a vector.</summary>
    private List<uint> Tokens(ulong element)
    {
        var tokens = new List<uint>();

        ulong first = _reader.ReadPointer(element + (ulong)_contentVector);
        ulong last = _reader.ReadPointer(element + (ulong)_contentVector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return tokens;
        }

        long count = (long)(last - first) / 4;
        if (count <= 0 || count > MostContents)
        {
            return tokens;
        }

        for (long i = 0; i < count; i++)
        {
            if (_reader.TryRead(first + (ulong)(i * 4), out uint token) && token != 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    /// <summary>
    /// What each step of the walk found, for the run where it comes back empty.
    /// </summary>
    /// <remarks>
    /// Every number here is unconfirmed, so "nothing came back" has at least four causes: the
    /// panel path, the fingerprints, the two-hop chain, and the fields at the end of it. One
    /// line each turns a hunt into a reading.
    /// </remarks>
    public IReadOnlyList<string> Describe(ulong uiRoot, UiScale scale)
    {
        var said = new List<string>();
        ulong panel = Panel(uiRoot);
        said.Add(panel == 0
            ? $"the atlas panel did not resolve at child path {string.Join(", ", _path)} - open the atlas, then check the path"
            : $"atlas panel at 0x{panel:X}");

        if (panel == 0)
        {
            said.AddRange(Hunt(uiRoot));
            return said;
        }

        List<ulong> children = _elements.Children(panel, MostNodes);
        said.Add($"{children.Count} children under it");

        var kinds = new Dictionary<uint, int>();
        foreach (ulong child in children)
        {
            if (_reader.TryRead(child + (ulong)_flags, out uint flags))
            {
                uint kind = flags & ~_visibleMask;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }
        }

        said.Add("flag fingerprints seen (the map-node one should be among them):");
        foreach ((uint kind, int count) in kinds.OrderByDescending(entry => entry.Value).Take(8))
        {
            string known = kind == (_mapNodeFp & ~_visibleMask) ? "  <- map node"
                : kind == (_mistNodeFp & ~_visibleMask) ? "  <- mist node"
                : string.Empty;
            said.Add($"    0x{kind:X}  x{count}{known}");
        }

        // The lines, said separately from the nodes because they come from somewhere else. A
        // full atlas has a few thousand; nought here is the answer that means no route can be
        // drawn to anything, however well the nodes themselves read.
        Dictionary<(int X, int Y), List<(int X, int Y)>> lines = Lines(panel);
        int ends = 0;
        foreach (List<(int X, int Y)> mine in lines.Values)
        {
            ends += mine.Count;
        }

        said.Add(lines.Count == 0
            ? $"NO lines read off the panel at +0x{_edges:X} - without them nothing can be routed to"
            : $"{ends / 2} lines between {lines.Count} places");

        List<AtlasNode> nodes = Read(uiRoot, scale);
        said.Add($"{nodes.Count} of them read as maps");

        foreach (AtlasNode node in nodes.Take(5))
        {
            said.Add(
                $"    [{node.Index}] {(node.MapId.Length > 0 ? node.MapId : "(no id)")} "
                + $"at {node.Grid.X},{node.Grid.Y}  {node.State}  biome {node.Biome}  "
                + $"{node.Connections.Count} connections  "
                + $"{node.BadgeIds.Count} badges  {node.ContentTokens.Count} tokens");
        }

        // Even with the path right, the fingerprints can be the thing that is wrong - and then
        // the panel is found and nothing in it reads as a map. Say where else it could be.
        if (nodes.Count == 0)
        {
            said.AddRange(Hunt(uiRoot));
        }

        return said;
    }

    /// <summary>
    /// Looks for what the atlas panel would have to look like, when the path does not find it.
    /// </summary>
    /// <remarks>
    /// A CHILD PATH IS A TERRIBLE THING TO HARD-CODE and this is the answer to that rather than
    /// an apology for it. "22, 0, 6" is a position in a list somebody else's client built; a
    /// patch that inserts one panel above it moves every atlas offset in the schema, and the
    /// only symptom is an overlay that draws nothing.
    ///
    /// So instead of asking somebody to hunt through the interface browser, this describes the
    /// panel by its SHAPE - hundreds of children that mostly share one flags word, which is
    /// what a grid of map nodes is and what almost nothing else in the interface is - and
    /// reports the paths that match. The answer goes straight into the schema, which is
    /// hot-reloadable, so correcting the drift is an edit rather than a build.
    ///
    /// Bounded on purpose: the tree is deep and this walks it blind.
    /// </remarks>
    public IReadOnlyList<string> Hunt(ulong uiRoot)
    {
        const int mostVisited = 20_000;
        const int deepest = 8;
        const int enoughChildren = 30;

        var said = new List<string>();
        var found = new List<(string Path, int Children, uint Kind, int Share)>();
        var queue = new Queue<(ulong Element, string Path, int Depth)>();
        queue.Enqueue((uiRoot, string.Empty, 0));

        int visited = 0;
        while (queue.Count > 0 && visited < mostVisited)
        {
            (ulong element, string path, int depth) = queue.Dequeue();
            visited++;

            List<ulong> children = _elements.Children(element, MostNodes);
            if (children.Count == 0)
            {
                continue;
            }

            if (children.Count >= enoughChildren)
            {
                var kinds = new Dictionary<uint, int>();
                foreach (ulong child in children)
                {
                    if (_reader.TryRead(child + (ulong)_flags, out uint flags))
                    {
                        uint kind = flags & ~_visibleMask;
                        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    }
                }

                if (kinds.Count > 0)
                {
                    (uint kind, int share) = kinds.MaxBy(entry => entry.Value);

                    // Most of one kind, not merely a plurality: a list of assorted buttons has
                    // a most-common flags word too, and it is not this.
                    if (share * 2 >= children.Count)
                    {
                        found.Add((path, children.Count, kind, share));
                    }
                }
            }

            if (depth < deepest)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    queue.Enqueue((children[i], path.Length == 0 ? $"{i}" : $"{path}, {i}", depth + 1));
                }
            }
        }

        if (found.Count == 0)
        {
            said.Add($"nothing in the interface looks like a grid of map nodes ({visited} elements looked at)");
            said.Add("    if the atlas is open, the UiElement offsets themselves are what to check");
            return said;
        }

        said.Add("elements that look like a grid of map nodes, biggest first:");
        foreach ((string path, int children, uint kind, int share) in
                 found.OrderByDescending(entry => entry.Children).Take(6))
        {
            string known = kind == (_mapNodeFp & ~_visibleMask) ? "  <- the map-node fingerprint"
                : kind == (_mistNodeFp & ~_visibleMask) ? "  <- the mist-node fingerprint"
                : string.Empty;
            said.Add($"    child path {path}  -  {children} children, {share} of them 0x{kind:X}{known}");
        }

        said.Add("    put the best one in AtlasPanel.PathFromUiRoot0..2, and its flags word in");
        said.Add("    MapNodeFingerprint if none of them is marked above - the schema reloads without a rebuild");
        return said;
    }
}
