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
/// <param name="Connections">The grid positions this node has a line to.</param>
/// <param name="Biome">Which biome it belongs to, as the game numbers them.</param>
/// <param name="Screen">Where it is drawn, for putting anything on top of it.</param>
/// <param name="BadgeIds">
/// The contents hanging off the node as objects, RAW - the low half identifies the content and
/// the high half is how much of it there is. See <see cref="World.AtlasContentNames"/>.
/// </param>
/// <param name="ContentTokens">
/// The contents that arrive as bare numbers instead. Only a node the game has actually drawn
/// has them, so an atlas scrolled far away reports none - which is not the same as a map with
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

    /// <summary>Most connections taken from one node, for the same reason.</summary>
    public const int MostConnections = 32;

    /// <summary>And most contents, which is the same guard on a different length.</summary>
    public const int MostContents = 64;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly int[] _path;
    private readonly uint _visibleMask;
    private readonly uint _mapNodeFp;
    private readonly uint _mistNodeFp;
    private readonly int _flags;

    private readonly int _grid;
    private readonly int _connections;
    private readonly int _contentVector;
    private readonly int _badgeBegin;
    private readonly int _badgeEnd;
    private readonly int _badgeContentId;
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

        _grid = node.OffsetOf("GridPosition");
        _connections = node.OffsetOf("ConnectionsVector");
        _contentVector = node.OffsetOf("ContentVector");
        _badgeBegin = node.OffsetOf("BadgeVectorBegin");
        _badgeEnd = node.OffsetOf("BadgeVectorEnd");
        _badgeContentId = (int)node.Constants["BadgeContentId"];
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

        for (int i = 0; i < children.Count; i++)
        {
            if (Node(i, children[i], placed) is AtlasNode node)
            {
                found.Add(node);
            }
        }

        return found;
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
    /// </remarks>
    public Dictionary<ulong, (Vector2 Position, Vector2 Size)> Where(ulong uiRoot, UiScale scale)
    {
        ulong panel = Panel(uiRoot);
        return panel == 0
            ? []
            : _elements.ReadSiblings(panel, _elements.Children(panel, MostNodes), scale);
    }

    /// <summary>One child of the panel, when it turns out to be a map.</summary>
    private AtlasNode? Node(int index, ulong element, Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed)
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
            Connections(element),
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

    /// <summary>The grid positions this node is joined to.</summary>
    private List<(int X, int Y)> Connections(ulong element)
    {
        var joined = new List<(int X, int Y)>();

        ulong first = _reader.ReadPointer(element + (ulong)_connections);
        ulong last = _reader.ReadPointer(element + (ulong)_connections + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return joined;
        }

        long count = (long)(last - first) / 8;
        if (count <= 0 || count > MostConnections)
        {
            return joined;   // a length out of game memory, treated as a bad read
        }

        for (long i = 0; i < count; i++)
        {
            ulong at = first + (ulong)(i * 8);
            joined.Add((_reader.Read<int>(at), _reader.Read<int>(at + 4)));
        }

        return joined;
    }

    /// <summary>
    /// The contents that hang off the node as objects, by their raw id.
    /// </summary>
    /// <remarks>
    /// A vector of POINTERS, each to a badge whose id sits at a fixed offset - so this is two
    /// levels rather than one, and a badge that does not resolve is skipped rather than
    /// recorded as content nought.
    /// </remarks>
    private List<uint> Badges(ulong element)
    {
        var ids = new List<uint>();

        ulong first = _reader.ReadPointer(element + (ulong)_badgeBegin);
        ulong last = _reader.ReadPointer(element + (ulong)_badgeEnd);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return ids;
        }

        long count = (long)(last - first) / 8;
        if (count <= 0 || count > MostContents)
        {
            return ids;
        }

        for (long i = 0; i < count; i++)
        {
            ulong badge = _reader.ReadPointer(first + (ulong)(i * 8));
            if (MemoryReaderExtensions.IsPlausiblePointer(badge)
                && _reader.TryRead(badge + (ulong)_badgeContentId, out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
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

        return said;
    }
}
