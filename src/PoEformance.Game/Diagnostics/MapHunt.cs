using System.Numerics;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Game.Diagnostics;

/// <summary>One element the map hunt looked at, and what its bytes say.</summary>
/// <param name="Route">How it was reached - the chain the schema uses, or the reference's.</param>
/// <param name="Address">The element.</param>
/// <param name="IdAt098">The wide string behind +0x098, this schema's StringIdPtr.</param>
/// <param name="IdAt128">The wide string behind +0x128, GameHelper2's 0.5.5 StringIdPtr.</param>
/// <param name="Flags">The element's flags word at the schema's offset.</param>
/// <param name="UnscaledSize">Its size in UI units at the schema's offset.</param>
/// <param name="Children">How many children it lists.</param>
/// <param name="ShiftSignatures">
/// Offsets, within the window, of a float pair reading (0, -20) - the large map's resting
/// DefaultShift in every client so far. The offset is the pair's first float.
/// </param>
/// <param name="ZoomLike">Aligned floats in the window that a map zoom could be.</param>
/// <param name="WindowRead">How many bytes of the element the reader served.</param>
public sealed record MapCandidate(
    string Route,
    ulong Address,
    string IdAt098,
    string IdAt128,
    uint Flags,
    Vector2 UnscaledSize,
    int Children,
    IReadOnlyList<int> ShiftSignatures,
    IReadOnlyList<(int Offset, float Value)> ZoomLike,
    int WindowRead);

/// <summary>What one frame of the map hunt saw.</summary>
/// <param name="UiManager">The KB/M UI manager (InGameState.UiRootStructPtr).</param>
/// <param name="ManagerChildren">How many children the manager lists - it is an element too.</param>
/// <param name="Candidates">Every element examined this frame.</param>
public sealed record MapHuntSample(ulong UiManager, int ManagerChildren, IReadOnlyList<MapCandidate> Candidates);

/// <summary>
/// Reads the map elements every way they might be reached, and brings their bytes home.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: the 0.5.5 patch of 2026-09-04 left the map radar drawing nothing, and the
/// first two captures from that client could say why only in part. The chain this schema
/// walks - ImportantUiElements.MapParentPtr, then the two pointers at +0x28/+0x30 - still
/// resolves to two UiElements, but they read a FULL-SCREEN size each, a zoom of exactly zero
/// at the reference's 0.5.5 offset, and they sit 0x2E0 bytes apart, which is smaller than any
/// object that could carry a field at 0x390. GameHelper2's own 0.5.5 commit stopped using the
/// map-parent chain altogether and walks child 6 of the UI manager, then its children 0 and
/// 1, for the two map VIEWPORTS - and this tool had never read those, so no recording can say
/// whether they are the maps, whether they carry the zoom, or where.
///
/// This is the switch that makes the bytes land in a <c>--record</c> file: both routes are
/// followed, every element on either is captured whole (<see cref="WindowBytes"/>), and the
/// windows are scanned for the two things a map element is known to hold - the resting shift
/// (0, -20) and a zoom in the range the game allows. Values that CHANGE while the person
/// zooms and pans are the answer; a constant one is furniture. That is why the console asks
/// them to work the map while it runs.
///
/// The same windows settle the StringIdPtr question for free: both candidate id slots are
/// read on every element, so whichever names the maps is the one to keep.
/// </remarks>
public sealed class MapHunt
{
    /// <summary>How much of each candidate element to capture.</summary>
    /// <remarks>
    /// Past every offset any client has ever put a map field at (0x3E0 in 0.4.x) with room to
    /// spare, and small enough that a session of ten such windows a frame stays shareable
    /// against the recorder's redundancy filter.
    /// </remarks>
    public const int WindowBytes = 0x800;

    /// <summary>The second id slot to read - GameHelper2's 0.5.5 StringIdPtr.</summary>
    /// <remarks>
    /// A literal on purpose: the schema's StringIdPtr is 0x098, and this hunt exists partly to
    /// find out whether that or the reference's slot names the map elements. A schema field for
    /// the rival would be a claim; this is a question.
    /// </remarks>
    public const int RivalStringIdAt = 0x128;

    /// <summary>The large map's resting shift in every client so far: (0, -20).</summary>
    public const float DefaultShiftY = -20f;

    /// <summary>The zoom range the game allows, with a margin either side.</summary>
    public const float LeastZoom = 0.3f;
    public const float MostZoom = 3.0f;

    /// <summary>How many children of a candidate to follow one level down.</summary>
    /// <remarks>
    /// The viewports the reference names may be containers with the map element beneath; two
    /// levels covers that without turning a container's whole subtree into a capture.
    /// </remarks>
    public const int ChildrenToFollow = 3;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly UiElementReader _elements;
    private readonly int _mapParent;
    private readonly int _largeMap;
    private readonly int _miniMap;
    private readonly int _stringId;
    private readonly int _flags;
    private readonly int _unscaledSize;
    private readonly int _childrenFirst;
    private readonly int _childrenLast;
    private readonly int _viewportsChild;
    private readonly int _largeViewport;
    private readonly int _miniViewport;
    private readonly byte[] _window = new byte[WindowBytes];

    public MapHunt(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _elements = new UiElementReader(reader, schema);

        _mapParent = schema.Structs["ImportantUiElements"].OffsetOf("MapParentPtr");
        StructDef parent = schema.Structs["MapParentStruct"];
        _largeMap = parent.OffsetOf("LargeMapPtr");
        _miniMap = parent.OffsetOf("MiniMapPtr");

        StructDef ui = schema.Structs["UiElementBase"];
        _stringId = ui.OffsetOf("StringIdPtr");
        _flags = ui.OffsetOf("Flags");
        _unscaledSize = ui.OffsetOf("UnscaledSize");
        _childrenFirst = ui.OffsetOf("ChildrenFirst");
        _childrenLast = ui.OffsetOf("ChildrenLast");

        // Absent from schemas older than 0.5.5 (the frozen one the fixtures replay through);
        // without it only the first route is walked.
        if (schema.Structs.TryGetValue("MapViewports", out StructDef? viewports))
        {
            _viewportsChild = (int)viewports.Constants["ChildOfUiManager"];
            _largeViewport = (int)viewports.Constants["LargeMapChild"];
            _miniViewport = (int)viewports.Constants["MiniMapChild"];
        }
        else
        {
            _viewportsChild = -1;
        }
    }

    /// <summary>Reads one frame, or null when the interface is not there to read.</summary>
    public MapHuntSample? SampleFrame(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        ulong manager = chain.UiRoot;
        if (manager == 0)
        {
            return null;
        }

        var candidates = new List<MapCandidate>();

        // Route 1: the chain this schema walks.
        ulong parent = _reader.ReadPointer(manager + (ulong)_mapParent);
        if (MemoryReaderExtensions.IsPlausiblePointer(parent))
        {
            Examine("MapParentPtr", parent, candidates, depth: 0);
            Examine("MapParentPtr.LargeMapPtr", _reader.ReadPointer(parent + (ulong)_largeMap), candidates, depth: 1);
            Examine("MapParentPtr.MiniMapPtr", _reader.ReadPointer(parent + (ulong)_miniMap), candidates, depth: 1);
        }

        // Route 2: the reference's 0.5.5 walk, from the manager as if it were an element -
        // which it is: its Self points at itself and it lists over a hundred children. The
        // count comes from the vector's bounds, not from how many children could be read.
        int managerChildren = 0;
        if (_elements.IsUiElement(manager))
        {
            ulong first = _reader.ReadPointer(manager + (ulong)_childrenFirst);
            ulong last = _reader.ReadPointer(manager + (ulong)_childrenLast);
            managerChildren = last > first && last - first < 0x10000 ? (int)((last - first) / 8) : 0;
        }
        ulong viewports = _viewportsChild >= 0 ? _elements.Child(manager, _viewportsChild) : 0;
        if (viewports != 0)
        {
            Examine($"UiManager/{_viewportsChild}", viewports, candidates, depth: 0);
            Examine($"UiManager/{_viewportsChild}/{_largeViewport}", _elements.Child(viewports, _largeViewport), candidates, depth: 1);
            Examine($"UiManager/{_viewportsChild}/{_miniViewport}", _elements.Child(viewports, _miniViewport), candidates, depth: 1);
        }

        return new MapHuntSample(manager, managerChildren, candidates);
    }

    /// <summary>Captures one element and, one level down, its first children.</summary>
    private void Examine(string route, ulong address, List<MapCandidate> into, int depth)
    {
        if (!_elements.IsUiElement(address))
        {
            return;
        }

        int read = 0;
        Array.Clear(_window);
        if (_reader.TryRead(address, _window))
        {
            read = WindowBytes;
        }
        else
        {
            // The object may end before the window does; take what is there rather than
            // nothing, which is the failure mode a whole-window read has on a small object.
            for (int size = WindowBytes / 2; size >= 0x100; size /= 2)
            {
                if (_reader.TryRead(address, _window.AsSpan(0, size)))
                {
                    read = size;
                    break;
                }
            }
        }

        _reader.TryRead(address + (ulong)_flags, out uint flags);
        Span<float> extent = stackalloc float[2];
        _reader.TryRead(address + (ulong)_unscaledSize, System.Runtime.InteropServices.MemoryMarshal.AsBytes(extent));
        List<ulong> children = _elements.Children(address, 64);

        into.Add(new MapCandidate(
            route,
            address,
            _reader.ReadStdWString(address + (ulong)_stringId, 64),
            _reader.ReadStdWString(address + RivalStringIdAt, 64),
            flags,
            new Vector2(extent[0], extent[1]),
            children.Count,
            ShiftSignatures(read),
            ZoomLike(read),
            read));

        if (depth < 2)
        {
            for (int i = 0; i < Math.Min(ChildrenToFollow, children.Count); i++)
            {
                Examine($"{route}/{i}", children[i], into, depth + 1);
            }
        }
    }

    private List<int> ShiftSignatures(int read)
    {
        var found = new List<int>();
        for (int at = 0; at + 8 <= read; at += 4)
        {
            float x = BitConverter.ToSingle(_window, at);
            float y = BitConverter.ToSingle(_window, at + 4);
            if (x == 0f && y == DefaultShiftY)
            {
                found.Add(at);
            }
        }

        return found;
    }

    private List<(int Offset, float Value)> ZoomLike(int read)
    {
        var found = new List<(int, float)>();
        for (int at = 0; at + 4 <= read; at += 4)
        {
            float f = BitConverter.ToSingle(_window, at);
            if (f is >= LeastZoom and <= MostZoom && float.IsFinite(f))
            {
                found.Add((at, f));
            }
        }

        return found;
    }

    /// <summary>Prints what a run saw, candidate by candidate, with the frame-to-frame verdicts.</summary>
    public static void Report(IReadOnlyList<MapHuntSample> samples, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("map hunt");
        if (samples.Count == 0)
        {
            output.WriteLine("  nothing sampled - the interface never resolved.");
            return;
        }

        output.WriteLine($"  {samples.Count} frames; the UI manager lists {samples[^1].ManagerChildren} children.");
        output.WriteLine("  A map element carries a (0, -20) resting shift and a zoom that MOVES when the map is");
        output.WriteLine("  zoomed. Offsets below are relative to the element; the reference's 0.5.5 layout says");
        output.WriteLine("  Shift 0x350, DefaultShift 0x358, Zoom 0x390.");

        foreach (string route in samples.SelectMany(s => s.Candidates).Select(c => c.Route).Distinct())
        {
            List<MapCandidate> seen = [.. samples.SelectMany(s => s.Candidates).Where(c => c.Route == route)];
            MapCandidate last = seen[^1];
            int addresses = seen.Select(c => c.Address).Distinct().Count();
            output.WriteLine();
            output.WriteLine($"  {route}: 0x{last.Address:X}{(addresses > 1 ? $" ({addresses} distinct addresses)" : "")}, {seen.Count} frames, {last.WindowRead} bytes read");
            output.WriteLine($"    id@098 '{last.IdAt098}'   id@128 '{last.IdAt128}'   flags 0x{last.Flags:X}   size {last.UnscaledSize.X:F0}x{last.UnscaledSize.Y:F0}   children {last.Children}");

            var shiftAt = seen.SelectMany(c => c.ShiftSignatures).GroupBy(o => o)
                .Where(g => g.Count() >= seen.Count / 2).Select(g => g.Key).OrderBy(o => o).ToList();
            output.WriteLine(shiftAt.Count > 0
                ? $"    (0, -20) at: {string.Join(", ", shiftAt.Select(o => $"0x{o:X}"))}"
                : "    no (0, -20) pair in the window");

            // A zoom moves; furniture does not. Group by offset, keep the ones present in most
            // frames, and say which of those took more than one value.
            var byOffset = seen.SelectMany(c => c.ZoomLike).GroupBy(z => z.Offset)
                .Where(g => g.Count() >= seen.Count / 2)
                .Select(g => (Offset: g.Key, Min: g.Min(z => z.Value), Max: g.Max(z => z.Value), Distinct: g.Select(z => z.Value).Distinct().Count()))
                .OrderBy(g => g.Offset).ToList();
            var moving = byOffset.Where(g => g.Distinct > 1).ToList();
            output.WriteLine(moving.Count > 0
                ? $"    zoom-like floats that CHANGED: {string.Join(", ", moving.Select(g => $"0x{g.Offset:X} {g.Min:F3}..{g.Max:F3}"))}"
                : $"    no zoom-like float changed across the frames ({byOffset.Count} constant ones in range{(byOffset.Count > 0 ? ": " + string.Join(", ", byOffset.Take(12).Select(g => $"0x{g.Offset:X}={g.Min:F2}")) : "")})");
        }

        output.WriteLine();
        output.WriteLine("  If nothing changed, the map was not zoomed while this ran: zoom it in and out, drag");
        output.WriteLine("  it, open and close the large map, and run again.");
    }
}
