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
