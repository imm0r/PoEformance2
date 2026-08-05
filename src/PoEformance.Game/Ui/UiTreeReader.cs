using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>
/// One UI element as the browser shows it: what it is, where it is, and what it holds.
/// </summary>
/// <param name="Text">
/// The text the element DISPLAYS. Most of the tree has no StringId at all - the C++-built
/// widgets are anonymous - so this is frequently the only thing that identifies a node.
/// </param>
/// <param name="Visible">Visible AND every ancestor visible - what "on screen" means.</param>
/// <param name="SelfVisible">
/// This element's own flag, shown next to <paramref name="Visible"/> on purpose: the two
/// disagreeing is precisely the "the panel is closed" case, and seeing which of the two is
/// false is what turns "why is this not on screen" into an answer.
/// </param>
/// <param name="Position">Top-left in WINDOW pixels, ready to draw a highlight at.</param>
/// <param name="UnscaledPosition">The same point in the game's 2560x1600 UI space.</param>
/// <param name="Depth">Levels below the browsed root, for indentation.</param>
public sealed record UiNode(
    ulong Address,
    ulong Parent,
    string StringId,
    string Text,
    bool Visible,
    bool SelfVisible,
    int ChildCount,
    Vector2 Position,
    Vector2 Size,
    Vector2 UnscaledPosition,
    Vector2 RelativePosition,
    uint Flags,
    byte ScaleIndex,
    float LocalMultiplier,
    ulong ItemPtr,
    int Depth,
    int IndexInParent,
    IReadOnlyList<ulong> Children)
{
    /// <summary>What to show in a tree row: the id, else the text, else the address.</summary>
    public string Label
        => StringId.Length > 0 ? StringId
            : Text.Length > 0 ? $"\"{Text}\""
            : $"0x{Address:X}";

    public bool Contains(Vector2 point)
        => point.X >= Position.X && point.X <= Position.X + Size.X
           && point.Y >= Position.Y && point.Y <= Position.Y + Size.Y;
}

/// <summary>
/// Walks the game's UI element tree for the browser: nodes, search and hit-testing.
/// </summary>
/// <remarks>
/// The tree is walked DOWNWARD, and that is the whole design. <see cref="UiElementReader"/>
/// resolves ONE element by climbing its parent chain, which is right for the two map elements
/// it was written for and quadratic for a tree: every node would re-read every ancestor, so a
/// few hundred visible nodes turn into thousands of reads of the same handful of parents.
/// Descending instead carries the accumulated position, scale space and visibility down from
/// the parent that was just read, which makes each node a fixed handful of reads regardless
/// of how deep it sits. The accumulation rules are the same ones - they have to be, or the
/// browser would disagree with the map about where an element is.
///
/// Only the browsed root is resolved by climbing, once.
/// </remarks>
public sealed class UiTreeReader
{
    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _self;
    private readonly int _childrenFirst;
    private readonly int _childrenLast;
    private readonly int _stringId;
    private readonly int _text;
    private readonly int _parent;
    private readonly int _positionModifier;
    private readonly int _relativePosition;
    private readonly int _localScaleMultiplier;
    private readonly int _flags;
    private readonly int _scaleIndex;
    private readonly int _unscaledSize;
    private readonly int _itemPtr;
    private readonly uint _flagShouldModifyPos;
    private readonly uint _flagIsVisible;
    private readonly int _maxDepth;

    public UiTreeReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _elements = new UiElementReader(reader, schema);

        StructDef ui = schema.Structs["UiElementBase"];
        _self = ui.OffsetOf("Self");
        _childrenFirst = ui.OffsetOf("ChildrenFirst");
        _childrenLast = ui.OffsetOf("ChildrenLast");
        _stringId = ui.OffsetOf("StringIdPtr");
        _text = ui.OffsetOf("TextPtr");
        _parent = ui.OffsetOf("ParentPtr");
        _positionModifier = ui.OffsetOf("PositionModifier");
        _relativePosition = ui.OffsetOf("RelativePosition");
        _localScaleMultiplier = ui.OffsetOf("LocalScaleMultiplier");
        _flags = ui.OffsetOf("Flags");
        _scaleIndex = ui.OffsetOf("ScaleIndex");
        _unscaledSize = ui.OffsetOf("UnscaledSize");
        _itemPtr = ui.OffsetOf("ItemPtr");
        _flagShouldModifyPos = (uint)ui.Constants["FlagShouldModifyPos"];
        _flagIsVisible = (uint)ui.Constants["FlagIsVisible"];
        _maxDepth = (int)ui.Constants["MaxParentChainDepth"];
    }

    /// <summary>What a parent hands its children: everything they need to place themselves.</summary>
    private readonly record struct Frame(
        Vector2 UnscaledPosition, byte ScaleIndex, float Multiplier, bool Visible, int Depth);

    /// <summary>
    /// Reads the root plus the children of every expanded node.
    /// </summary>
    /// <remarks>
    /// The set of expanded nodes IS the request: the browser draws a tree, so what it needs
    /// is exactly the levels the user has opened, and asking for that directly means the cost
    /// follows what is on screen instead of the size of the interface.
    ///
    /// <paramref name="budget"/> bounds a pathological expansion - a node with hundreds of
    /// children, all expanded - so one unlucky click cannot turn into a read that outlasts
    /// the frame it was asked for. Hitting it truncates the tree rather than failing it.
    /// </remarks>
    public Dictionary<ulong, UiNode> ReadTree(
        ulong root, UiScale scale, IReadOnlySet<ulong> expanded, int budget = 2048)
    {
        ArgumentNullException.ThrowIfNull(expanded);

        var nodes = new Dictionary<ulong, UiNode>();
        if (!IsElement(root))
        {
            return nodes;
        }

        // The one upward walk. Everything below the root inherits from its parent instead.
        var start = new Frame(
            _elements.UnscaledPosition(root, scale),
            _reader.Read<byte>(root + (ulong)_scaleIndex),
            _reader.Read<float>(root + (ulong)_localScaleMultiplier),
            _elements.IsVisible(root),
            0);

        var queue = new Queue<(ulong Address, ulong Parent, int Index, Frame Frame)>();
        queue.Enqueue((root, _reader.ReadPointer(root + (ulong)_parent), 0, start));

        while (queue.Count > 0 && nodes.Count < budget)
        {
            (ulong address, ulong parent, int index, Frame frame) = queue.Dequeue();
            if (nodes.ContainsKey(address))
            {
                continue; // a cycle, or the same element reachable twice
            }

            UiNode node = Build(address, parent, index, frame, scale);
            nodes[address] = node;

            if (!expanded.Contains(address))
            {
                continue;
            }

            Frame childFrame = ChildFrame(node);
            for (int i = 0; i < node.Children.Count; i++)
            {
                queue.Enqueue((node.Children[i], address, i, childFrame));
            }
        }

        return nodes;
    }

    /// <summary>Reads a single node, resolving its position by climbing. For a detail pane.</summary>
    public UiNode? ReadOne(ulong address, UiScale scale)
    {
        if (!IsElement(address))
        {
            return null;
        }

        var frame = new Frame(
            _elements.UnscaledPosition(address, scale),
            _reader.Read<byte>(address + (ulong)_scaleIndex),
            _reader.Read<float>(address + (ulong)_localScaleMultiplier),
            _elements.IsVisible(address),
            0);

        return Build(address, _reader.ReadPointer(address + (ulong)_parent), 0, frame, scale);
    }

    /// <summary>
    /// The element under a screen point, as the chain from the root down to it.
    /// </summary>
    /// <remarks>
    /// The chain rather than just the leaf, because the browser has to OPEN the tree to show
    /// what was picked - and because the path is usually more interesting than the endpoint
    /// when the question is "what is this thing part of".
    ///
    /// At each level the LAST matching child wins, which is the game's own draw order: later
    /// siblings are drawn on top, so the last one containing the point is the one visible
    /// there. Only visible children are considered.
    ///
    /// Known limitation, inherited from how the interface is built: a full-screen transparent
    /// layer (the notification host, for one) contains every point, so the descent can stop
    /// inside it and never reach the panel underneath. That is a real element genuinely under
    /// the cursor, not a bug in the walk - when it happens, the tree is the way in.
    /// </remarks>
    public List<ulong> HitTest(ulong root, Vector2 point, UiScale scale)
    {
        var chain = new List<ulong>();
        if (!IsElement(root))
        {
            return chain;
        }

        UiNode? current = ReadOne(root, scale);
        if (current is null)
        {
            return chain;
        }

        chain.Add(root);

        for (int depth = 0; depth < _maxDepth && current is not null; depth++)
        {
            Frame frame = ChildFrame(current);
            UiNode? best = null;

            for (int i = 0; i < current.Children.Count; i++)
            {
                UiNode child = Build(current.Children[i], current.Address, i, frame, scale);
                if (child.Visible && child.Size.X > 0 && child.Size.Y > 0 && child.Contains(point))
                {
                    best = child; // later siblings draw on top, so the last match wins
                }
            }

            if (best is null)
            {
                break;
            }

            chain.Add(best.Address);
            current = best;
        }

        return chain;
    }

    /// <summary>
    /// Finds elements whose id or displayed text contains <paramref name="query"/>.
    /// </summary>
    /// <remarks>
    /// Text as well as id, because the anonymous widgets are exactly the ones worth finding:
    /// searching a tree of blanks for a blank finds nothing, while the label the game shows
    /// is on screen and therefore the thing anyone would type.
    ///
    /// Breadth-first and bounded twice - by result count and by a deadline - because this
    /// runs over the whole interface on a thread that also feeds the overlay. An interrupted
    /// search returns what it found, which is the useful answer; there is nothing to be
    /// gained by making the caller wait for completeness it did not ask for.
    /// </remarks>
    public List<UiNode> Search(
        ulong root, string query, UiScale scale, int maxResults = 200, int budget = 20_000)
    {
        var results = new List<UiNode>();
        if (string.IsNullOrWhiteSpace(query) || !IsElement(root))
        {
            return results;
        }

        var seen = new HashSet<ulong>();
        var queue = new Queue<(ulong Address, ulong Parent, int Index, Frame Frame)>();

        UiNode? start = ReadOne(root, scale);
        if (start is null)
        {
            return results;
        }

        queue.Enqueue((root, start.Parent, 0, new Frame(
            start.UnscaledPosition, start.ScaleIndex, start.LocalMultiplier, start.Visible, 0)));

        int visited = 0;
        while (queue.Count > 0 && results.Count < maxResults && visited < budget)
        {
            (ulong address, ulong parent, int index, Frame frame) = queue.Dequeue();
            if (!seen.Add(address))
            {
                continue;
            }

            visited++;
            UiNode node = Build(address, parent, index, frame, scale);

            if (node.StringId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || node.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(node);
            }

            Frame childFrame = ChildFrame(node);
            for (int i = 0; i < node.Children.Count; i++)
            {
                queue.Enqueue((node.Children[i], address, i, childFrame));
            }
        }

        return results;
    }

    /// <summary>
    /// The child indices from <paramref name="root"/> down to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// This is the form a path gets written down in - "[35][2][0][0][1]" - and the form it
    /// gets pasted back into code as, so a browser that cannot produce it leaves the RE work
    /// unfinished. Walked UPWARD from the target, since the parent chain is a link each
    /// element already holds and searching downward for it would be the whole tree.
    /// </remarks>
    public List<int> IndexPath(ulong root, ulong target)
    {
        var path = new List<int>();
        ulong current = target;

        for (int depth = 0; depth < _maxDepth && current != root; depth++)
        {
            ulong parent = _reader.ReadPointer(current + (ulong)_parent);
            if (!IsElement(parent))
            {
                return []; // never reached the root: not a descendant of it
            }

            int index = IndexOfChild(parent, current);
            if (index < 0)
            {
                return [];
            }

            path.Insert(0, index);
            current = parent;
        }

        return current == root ? path : [];
    }

    /// <summary>The frame a node hands its children: its position, its scale space, its visibility.</summary>
    private Frame ChildFrame(UiNode node)
        => new(node.UnscaledPosition, node.ScaleIndex, node.LocalMultiplier, node.Visible, node.Depth + 1);

    private UiNode Build(ulong address, ulong parent, int index, Frame frame, UiScale scale)
    {
        uint flags = _reader.Read<uint>(address + (ulong)_flags);
        byte scaleIndex = _reader.Read<byte>(address + (ulong)_scaleIndex);
        float multiplier = _reader.Read<float>(address + (ulong)_localScaleMultiplier);
        Vector2 relative = ReadVector2(address + (ulong)_relativePosition);

        // The root is its own frame - it was resolved by climbing, so it must not be shifted
        // by a parent it did not accumulate.
        Vector2 unscaled = frame.Depth == 0
            ? frame.UnscaledPosition
            : Accumulate(frame, address, parent, relative, flags, scaleIndex, multiplier, scale);

        (float scaleW, float scaleH) = scale.For(scaleIndex, multiplier);
        Vector2 size = ReadVector2(address + (ulong)_unscaledSize);
        bool selfVisible = (flags & _flagIsVisible) != 0;

        return new UiNode(
            address,
            parent,
            _reader.ReadStdWString(address + (ulong)_stringId),
            _reader.ReadStdWString(address + (ulong)_text),
            frame.Visible && selfVisible,
            selfVisible,
            ChildCount(address),
            new Vector2((unscaled.X * scaleW) + scale.Cull, unscaled.Y * scaleH),
            new Vector2(size.X * scaleW, size.Y * scaleH),
            unscaled,
            relative,
            flags,
            scaleIndex,
            multiplier,
            _reader.ReadPointer(address + (ulong)_itemPtr),
            frame.Depth,
            index,
            Children(address));
    }

    /// <summary>
    /// Adds a child's own offset to its parent's accumulated position.
    /// </summary>
    /// <remarks>
    /// The same two rules <see cref="UiElementReader"/> applies while climbing, applied here
    /// while descending: the parent's modifier counts only when the CHILD opts in, and a
    /// change of scale space converts the accumulated position before the offset is added.
    /// Both are easy to leave out and invisible at 16:10 with a uniform scale chain, which
    /// is the shape of bug that only appears on someone else's monitor.
    /// </remarks>
    private Vector2 Accumulate(
        Frame frame, ulong address, ulong parent, Vector2 relative,
        uint flags, byte scaleIndex, float multiplier, UiScale scale)
    {
        Vector2 parentPosition = frame.UnscaledPosition;
        if ((flags & _flagShouldModifyPos) != 0 && IsElement(parent))
        {
            parentPosition += ReadVector2(parent + (ulong)_positionModifier);
        }

        if (scaleIndex == frame.ScaleIndex && multiplier.Equals(frame.Multiplier))
        {
            return parentPosition + relative;
        }

        (float parentW, float parentH) = scale.For(frame.ScaleIndex, frame.Multiplier);
        (float myW, float myH) = scale.For(scaleIndex, multiplier);
        return new Vector2(
            myW != 0 ? (parentPosition.X * parentW / myW) + relative.X : relative.X,
            myH != 0 ? (parentPosition.Y * parentH / myH) + relative.Y : relative.Y);
    }

    /// <summary>True when this address really is a UiElement - it points at itself.</summary>
    public bool IsElement(ulong address)
        => MemoryReaderExtensions.IsPlausiblePointer(address)
           && _reader.ReadPointer(address + (ulong)_self) == address;

    private int ChildCount(ulong address)
    {
        ulong first = _reader.ReadPointer(address + (ulong)_childrenFirst);
        ulong last = _reader.ReadPointer(address + (ulong)_childrenLast);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return 0;
        }

        long count = (long)(last - first) / 8;
        return count is < 0 or > 4096 ? 0 : (int)count;
    }

    private List<ulong> Children(ulong address, int max = 512)
    {
        int count = Math.Min(ChildCount(address), max);
        var children = new List<ulong>(count);
        if (count == 0)
        {
            return children;
        }

        ulong first = _reader.ReadPointer(address + (ulong)_childrenFirst);

        // One read for the whole pointer array rather than one per child: a container with
        // a hundred children is the normal case here, not the exceptional one.
        var bytes = new byte[count * 8];
        if (!_reader.TryRead(first, bytes))
        {
            return children;
        }

        for (int i = 0; i < count; i++)
        {
            ulong child = BitConverter.ToUInt64(bytes, i * 8);
            children.Add(MemoryReaderExtensions.IsPlausiblePointer(child) ? child : 0);
        }

        return children;
    }

    private int IndexOfChild(ulong parent, ulong child)
    {
        List<ulong> children = Children(parent);
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] == child)
            {
                return i;
            }
        }

        return -1;
    }

    private Vector2 ReadVector2(ulong address)
    {
        Span<float> pair = stackalloc float[2];
        return _reader.TryRead(address, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair))
            ? new Vector2(pair[0], pair[1])
            : Vector2.Zero;
    }
}
