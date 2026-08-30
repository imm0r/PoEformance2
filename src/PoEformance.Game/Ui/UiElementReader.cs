using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>
/// Converts the game's UI coordinate space into window pixels.
/// </summary>
/// <remarks>
/// The game lays its interface out in a fixed 2560x1600 space and scales that to whatever
/// window it is running in, so every position and size read off a UiElement is in the FIXED
/// space and means nothing until converted.
///
/// Two scale factors exist because the game does not scale both axes the same way. Width
/// uses the window MINUS the letterbox bars on either side; height uses the full window.
/// Each element then picks which pair applies via its ScaleIndex - so an element can be
/// scaled by width on one axis and height on the other. Getting this wrong is invisible at
/// 16:10 (where the two factors are equal) and obvious on an ultrawide, which is exactly the
/// kind of bug that only shows up on someone else's monitor.
/// </remarks>
public readonly record struct UiScale(int WindowWidth, int WindowHeight, int Cull)
{
    /// <summary>The interface's design resolution; all UI coordinates are in this space.</summary>
    public const float BaseWidth = 2560f;
    public const float BaseHeight = 1600f;

    /// <summary>Width factor: the window minus the letterbox bars on both sides.</summary>
    public float WidthFactor => (WindowWidth - (2f * Cull)) / BaseWidth;

    /// <summary>Height factor: the full window height.</summary>
    public float HeightFactor => WindowHeight / BaseHeight;

    /// <summary>
    /// The scale pair an element uses, per its ScaleIndex and own multiplier.
    /// </summary>
    /// <remarks>
    /// Index 3 mixing the two axes is the case worth noticing - and an UNKNOWN index means
    /// no scaling at all rather than a guess, matching the reference, so a misread byte
    /// leaves the element unscaled instead of wildly misplaced.
    /// </remarks>
    public (float Width, float Height) For(byte scaleIndex, float localMultiplier)
    {
        float multiplier = float.IsFinite(localMultiplier) && localMultiplier != 0 ? localMultiplier : 1f;
        return scaleIndex switch
        {
            1 => (WidthFactor * multiplier, WidthFactor * multiplier),
            2 => (HeightFactor * multiplier, HeightFactor * multiplier),
            3 => (WidthFactor * multiplier, HeightFactor * multiplier),
            _ => (multiplier, multiplier),
        };
    }
}

/// <summary>One UI element, resolved to window pixels.</summary>
public sealed record UiElement(
    ulong Address,
    Vector2 Position,
    Vector2 Size,
    bool Visible,
    string StringId)
{
    public float Left => Position.X;
    public float Top => Position.Y;
    public float Right => Position.X + Size.X;
    public float Bottom => Position.Y + Size.Y;
    public Vector2 Centre => Position + (Size / 2f);

    public bool Contains(float x, float y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Reads the game's UI element tree: position, size, visibility and children.
/// </summary>
/// <remarks>
/// An element only knows where it sits RELATIVE to its parent, so an absolute position means
/// walking up the chain and accumulating - which is why this exists as its own reader rather
/// than a field read. Two subtleties in that walk are easy to miss and both come straight
/// from the reference implementation:
///
/// - the parent's PositionModifier is added only when the CHILD asks for it via its own
///   flag, not when the parent has one;
/// - when parent and child sit in different scale spaces, the accumulated position must be
///   CONVERTED between them before the child's own offset is added.
///
/// Visibility is likewise not a single flag: an element is visible only if every ancestor is
/// too, so a panel being closed hides its whole subtree without touching their flags.
/// </remarks>
public sealed class UiElementReader
{
    private readonly IMemoryReader _reader;
    private readonly int _self;
    private readonly int _childrenFirst;
    private readonly int _childrenLast;
    private readonly int _stringId;
    private readonly int _parent;
    private readonly int _positionModifier;
    private readonly int _relativePosition;
    private readonly int _localScaleMultiplier;
    private readonly int _flags;
    private readonly int _scaleIndex;
    private readonly int _unscaledSize;
    private readonly uint _flagShouldModifyPos;
    private readonly uint _flagIsVisible;
    private readonly int _maxDepth;

    public UiElementReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef ui = schema.Structs["UiElementBase"];
        _self = ui.OffsetOf("Self");
        _childrenFirst = ui.OffsetOf("ChildrenFirst");
        _childrenLast = ui.OffsetOf("ChildrenLast");
        _stringId = ui.OffsetOf("StringIdPtr");
        _parent = ui.OffsetOf("ParentPtr");
        _positionModifier = ui.OffsetOf("PositionModifier");
        _relativePosition = ui.OffsetOf("RelativePosition");
        _localScaleMultiplier = ui.OffsetOf("LocalScaleMultiplier");
        _flags = ui.OffsetOf("Flags");
        _scaleIndex = ui.OffsetOf("ScaleIndex");
        _unscaledSize = ui.OffsetOf("UnscaledSize");
        _flagShouldModifyPos = (uint)ui.Constants["FlagShouldModifyPos"];
        _flagIsVisible = (uint)ui.Constants["FlagIsVisible"];
        _maxDepth = (int)ui.Constants["MaxParentChainDepth"];
    }

    /// <summary>
    /// True when this address really is a UiElement.
    /// </summary>
    /// <remarks>
    /// Every element stores a pointer to itself, so a stale or wrong pointer is caught before
    /// its other fields are trusted - much cheaper than discovering it through nonsense
    /// coordinates later.
    /// </remarks>
    public bool IsUiElement(ulong address)
        => MemoryReaderExtensions.IsPlausiblePointer(address)
           && _reader.ReadPointer(address + (ulong)_self) == address;

    /// <summary>
    /// Where a set of one parent's children are drawn, without re-walking the chain per child.
    /// </summary>
    /// <remarks>
    /// The same answer as <see cref="Read"/> for each of them, arrived at once instead of
    /// hundreds of times. Every child of one parent shares the ENTIRE chain above that parent,
    /// and <see cref="UnscaledPosition"/> re-walks it from scratch every call - so reading five
    /// hundred siblings costs five hundred walks of the same ten elements, all returning the
    /// same number.
    ///
    /// That is the difference between a panel that can be read every tick and one that cannot.
    /// The atlas is the case that made it worth having: several hundred nodes under one panel,
    /// re-read as somebody drags it about.
    ///
    /// Everything constant across the siblings - the parent's accumulated position, its
    /// modifier, its scale space - is read once here. What remains per child is its own
    /// relative position, flags, scale and size.
    /// </remarks>
    /// <returns>Position and size in window pixels, by child address. Missing for a bad one.</returns>
    public Dictionary<ulong, (Vector2 Position, Vector2 Size)> ReadSiblings(
        ulong parent, IReadOnlyList<ulong> children, UiScale scale)
    {
        ArgumentNullException.ThrowIfNull(children);

        var placed = new Dictionary<ulong, (Vector2, Vector2)>(children.Count);
        if (!IsUiElement(parent))
        {
            return placed;
        }

        Vector2 parentPosition = UnscaledPosition(parent, scale);
        Vector2 modifier = ReadVector2(parent + (ulong)_positionModifier);
        byte parentIndex = _reader.Read<byte>(parent + (ulong)_scaleIndex);
        float parentMultiplier = _reader.Read<float>(parent + (ulong)_localScaleMultiplier);
        (float parentW, float parentH) = scale.For(parentIndex, parentMultiplier);

        foreach (ulong child in children)
        {
            if (!IsUiElement(child))
            {
                continue;
            }

            Vector2 relative = ReadVector2(child + (ulong)_relativePosition);

            // The modifier belongs to the parent but is applied only when the CHILD opts in,
            // so it is read once above and chosen per child here.
            uint flags = _reader.Read<uint>(child + (ulong)_flags);
            Vector2 above = (flags & _flagShouldModifyPos) != 0 ? parentPosition + modifier : parentPosition;

            byte index = _reader.Read<byte>(child + (ulong)_scaleIndex);
            float multiplier = _reader.Read<float>(child + (ulong)_localScaleMultiplier);
            (float childW, float childH) = scale.For(index, multiplier);

            Vector2 unscaled = index == parentIndex && multiplier.Equals(parentMultiplier)
                ? above + relative
                : new Vector2(
                    childW != 0 ? (above.X * parentW / childW) + relative.X : relative.X,
                    childH != 0 ? (above.Y * parentH / childH) + relative.Y : relative.Y);

            Vector2 size = ReadVector2(child + (ulong)_unscaledSize);

            placed[child] = (
                new Vector2((unscaled.X * childW) + scale.Cull, unscaled.Y * childH),
                new Vector2(size.X * childW, size.Y * childH));
        }

        return placed;
    }

    /// <summary>Reads one element resolved to window pixels, or null if it is not one.</summary>
    public UiElement? Read(ulong address, UiScale scale, bool withStringId = true)
    {
        if (!IsUiElement(address))
        {
            return null;
        }

        byte scaleIndex = _reader.Read<byte>(address + (ulong)_scaleIndex);
        float multiplier = _reader.Read<float>(address + (ulong)_localScaleMultiplier);
        (float scaleW, float scaleH) = scale.For(scaleIndex, multiplier);

        Vector2 unscaled = UnscaledPosition(address, scale);
        var position = new Vector2((unscaled.X * scaleW) + scale.Cull, unscaled.Y * scaleH);

        Vector2 size = ReadVector2(address + (ulong)_unscaledSize);
        size = new Vector2(size.X * scaleW, size.Y * scaleH);

        string stringId = withStringId
            ? _reader.ReadStdWString(address + (ulong)_stringId)
            : string.Empty;

        return new UiElement(address, position, size, IsVisible(address), stringId);
    }

    /// <summary>
    /// Accumulates this element's position in UI space by walking up the parent chain.
    /// </summary>
    public Vector2 UnscaledPosition(ulong address, UiScale scale)
        => UnscaledPosition(address, scale, 0);

    private Vector2 UnscaledPosition(ulong address, UiScale scale, int depth)
    {
        Vector2 relative = ReadVector2(address + (ulong)_relativePosition);

        ulong parent = _reader.ReadPointer(address + (ulong)_parent);
        if (depth >= _maxDepth || !IsUiElement(parent))
        {
            return relative;
        }

        Vector2 parentPosition = UnscaledPosition(parent, scale, depth + 1);

        // The modifier belongs to the parent but is applied only when the CHILD opts in.
        uint flags = _reader.Read<uint>(address + (ulong)_flags);
        if ((flags & _flagShouldModifyPos) != 0)
        {
            parentPosition += ReadVector2(parent + (ulong)_positionModifier);
        }

        byte myIndex = _reader.Read<byte>(address + (ulong)_scaleIndex);
        float myMultiplier = _reader.Read<float>(address + (ulong)_localScaleMultiplier);
        byte parentIndex = _reader.Read<byte>(parent + (ulong)_scaleIndex);
        float parentMultiplier = _reader.Read<float>(parent + (ulong)_localScaleMultiplier);

        if (myIndex == parentIndex && myMultiplier.Equals(parentMultiplier))
        {
            return parentPosition + relative;
        }

        // Different scale spaces: bring the parent's position into this element's space
        // before adding the child's own offset, or the offset means something else.
        (float parentW, float parentH) = scale.For(parentIndex, parentMultiplier);
        (float myW, float myH) = scale.For(myIndex, myMultiplier);
        return new Vector2(
            myW != 0 ? (parentPosition.X * parentW / myW) + relative.X : relative.X,
            myH != 0 ? (parentPosition.Y * parentH / myH) + relative.Y : relative.Y);
    }

    /// <summary>
    /// True when this element AND every ancestor is visible.
    /// </summary>
    /// <remarks>
    /// The whole chain matters: a closed panel leaves its children's own flags set, so
    /// checking only the element itself reports hidden UI as visible.
    /// </remarks>
    public bool IsVisible(ulong address)
    {
        for (int depth = 0; depth <= _maxDepth; depth++)
        {
            if (!IsUiElement(address))
            {
                return false;
            }

            if ((_reader.Read<uint>(address + (ulong)_flags) & _flagIsVisible) == 0)
            {
                return false;
            }

            ulong parent = _reader.ReadPointer(address + (ulong)_parent);
            if (!IsUiElement(parent))
            {
                return true; // reached the root
            }

            address = parent;
        }

        return true;
    }

    /// <summary>
    /// This element's OWN visible bit, without asking about its ancestors.
    /// </summary>
    /// <remarks>
    /// NOT A CHEAPER <see cref="IsVisible"/> and not a substitute for it: on its own this says
    /// nothing about whether the element is on screen, because a closed panel leaves every flag
    /// in its subtree set. It is only an answer for a caller that has ALREADY established the
    /// chain above - measuring the children of a panel whose own visibility was just checked,
    /// which is <c>PanelReader</c>'s case and would otherwise re-walk the same ancestors once
    /// per child.
    /// </remarks>
    public bool IsShowingItself(ulong address)
        => IsUiElement(address)
           && (_reader.Read<uint>(address + (ulong)_flags) & _flagIsVisible) != 0;

    /// <summary>Adds this element and every ancestor of it to <paramref name="into"/>.</summary>
    /// <remarks>
    /// The cheap way to ask "is this element inside that one" for a handful of elements whose
    /// identity matters: the chain is a pointer each element already holds, so walking UP is a
    /// few reads, while searching down from the candidate is the whole subtree. What it is for
    /// is <c>InterfaceReader</c>, which must never report an element the maps live under as part of
    /// the interface - that would take the minimap out of the region it is drawn on.
    /// </remarks>
    /// <param name="order">
    /// Optionally, the same addresses in the order they were walked - LEAF FIRST, so the last
    /// entry is the outermost element the chain reached. That order is the answer to "which
    /// container is this element two levels inside", which is how the atlas's own screen is
    /// found without naming it or trusting an index.
    /// </param>
    public void AndAncestors(ulong address, ISet<ulong> into, List<ulong>? order = null)
    {
        ArgumentNullException.ThrowIfNull(into);

        for (int depth = 0; depth <= _maxDepth && IsUiElement(address) && into.Add(address); depth++)
        {
            order?.Add(address);
            address = _reader.ReadPointer(address + (ulong)_parent);
        }
    }

    /// <summary>Reads the element's child pointers.</summary>
    public List<ulong> Children(ulong address, int max = 512)
    {
        var children = new List<ulong>();
        if (!IsUiElement(address))
        {
            return children;
        }

        ulong first = _reader.ReadPointer(address + (ulong)_childrenFirst);
        ulong last = _reader.ReadPointer(address + (ulong)_childrenLast);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return children;
        }

        long count = (long)(last - first) / 8;
        if (count is < 0 or > 4096)
        {
            return children; // a torn read mid-resize, not a real child list
        }

        for (long i = 0; i < count && children.Count < max; i++)
        {
            ulong child = _reader.ReadPointer(first + (ulong)(i * 8));
            if (MemoryReaderExtensions.IsPlausiblePointer(child))
            {
                children.Add(child);
            }
        }

        return children;
    }

    /// <summary>Reads the nth child directly, without materialising the whole list.</summary>
    public ulong Child(ulong address, int index)
    {
        if (index < 0 || !IsUiElement(address))
        {
            return 0;
        }

        ulong first = _reader.ReadPointer(address + (ulong)_childrenFirst);
        ulong last = _reader.ReadPointer(address + (ulong)_childrenLast);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return 0;
        }

        return (ulong)((index + 1) * 8) > last - first
            ? 0
            : _reader.ReadPointer(first + (ulong)(index * 8));
    }

    private Vector2 ReadVector2(ulong address)
    {
        Span<float> pair = stackalloc float[2];
        return _reader.TryRead(address, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair))
            ? new Vector2(pair[0], pair[1])
            : Vector2.Zero;
    }
}
