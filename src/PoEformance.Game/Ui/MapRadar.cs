using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>
/// One of the game's two maps, resolved to screen pixels and ready to project onto.
/// </summary>
/// <param name="Centre">Where the player sits on this map, in window pixels.</param>
/// <param name="Diagonal">The map's diagonal in pixels - the projection's length scale.</param>
/// <param name="Zoom">The map's own zoom. This is what the camera matrix cannot know about.</param>
/// <param name="Visible">
/// Whether the game is actually showing this map. The two swap: opening the large one hides
/// the minimap, and both UI elements exist either way, so projecting markers without asking
/// paints them onto a map that is not on screen.
/// </param>
/// <param name="Left">Left edge of the rectangle this map occupies, for clipping.</param>
/// <param name="Top">Top edge of that rectangle.</param>
/// <param name="Width">Width of that rectangle.</param>
/// <param name="Height">Height of that rectangle.</param>
public readonly record struct MapView(
    Vector2 Centre,
    float Diagonal,
    float Zoom,
    bool IsLargeMap,
    bool Visible = true,
    float Left = 0f,
    float Top = 0f,
    float Width = 0f,
    float Height = 0f)
{
    /// <summary>
    /// True when a projected point falls on this map.
    /// </summary>
    /// <remarks>
    /// Markers are placed by projecting a world delta, which happily lands hundreds of
    /// pixels outside a minimap the size of a postage stamp - and a marker outside the map
    /// it belongs to is just a dot floating in the middle of the game. A map with no
    /// measured rectangle accepts everything rather than rejecting everything.
    /// </remarks>
    public bool Contains(Vector2 point)
        => Width <= 0 || Height <= 0
           || (point.X >= Left && point.X <= Left + Width
               && point.Y >= Top && point.Y <= Top + Height);

    /// <summary>The map's fixed viewing angle. Not the 3D camera's - the map has its own.</summary>
    public const double CameraAngle = 38.7 * Math.PI / 180.0;

    /// <summary>World units per grid cell (250/23). The map works in grid units.</summary>
    public const float WorldToGrid = 250f / 23f;

    /// <summary>Divisor turning a terrain-height difference into the same grid units.</summary>
    public const float HeightToGrid = 10.86957f;

    /// <summary>Numerator of the zoom-to-scale relation, from the reference.</summary>
    public const float ScaleBase = 240f;

    public bool IsUsable => Diagonal > 0 && Zoom > 0;

    /// <summary>
    /// How many pixels one grid cell measures across this map, along its wider axis.
    /// </summary>
    /// <remarks>
    /// The horizontal half of the transform below, on its own. What it is for is choosing how
    /// finely to sample something drawn ACROSS the map rather than at one point: a step
    /// measured in grid cells means nothing on screen until it is multiplied by this, and a
    /// fixed step packs into a solid block at one zoom and leaves gaps at another.
    /// </remarks>
    public float PixelsPerGridCell
        => Zoom > 0 ? (float)(Diagonal * Math.Cos(CameraAngle) * Zoom / ScaleBase) : 0f;

    /// <summary>
    /// Projects a world position onto this map, relative to the player.
    /// </summary>
    /// <remarks>
    /// This is the game's OWN map transform, and it shares nothing with the world-to-screen
    /// matrix: a fixed isometric angle, everything measured as a delta from the player, and
    /// the scale coming from the map's zoom. That is exactly why markers projected with the
    /// camera matrix drift away from the game's map markers as soon as the map is zoomed -
    /// the matrix has no zoom to account for.
    ///
    /// Height enters through TERRAIN height, not the entity's own z: on the map an entity is
    /// placed by the ground it stands on, so a monster on a ledge sits where the ledge is.
    /// </remarks>
    public Vector2 Project(
        float worldX, float worldY, float terrainHeight,
        float playerWorldX, float playerWorldY, float playerTerrainHeight)
    {
        float scale = ScaleBase / Zoom;
        float cos = (float)(Diagonal * Math.Cos(CameraAngle) / scale);
        float sin = (float)(Diagonal * Math.Sin(CameraAngle) / scale);

        float dx = (worldX - playerWorldX) / WorldToGrid;
        float dy = (worldY - playerWorldY) / WorldToGrid;
        float dz = (terrainHeight - playerTerrainHeight) / HeightToGrid;

        return Centre + new Vector2((dx - dy) * cos, (dz - (dx + dy)) * sin);
    }
}

/// <summary>
/// Resolves the game's large map and minimap from the UI tree.
/// </summary>
/// <remarks>
/// Both are ordinary UI elements with three extra fields - zoom, and two shifts - so the
/// element reader does the position work and this only adds the map-specific parts.
///
/// The two maps are NOT symmetric, and PoE2 differs from the upstream reference on all three
/// counts (each verified by the AHK tool against this game):
///
/// - the LARGE map's accumulated position is already its CENTRE, because the element is
///   positioned relative to the screen centre in the UI tree. The minimap's is its top-left,
///   so that one needs half its size added.
/// - the large map's UnscaledSize reads 0, so its diagonal has to come from somewhere else.
///   The minimap's diagonal is the right substitute and is cached for that purpose; the
///   window diagonal is the fallback when no minimap has been seen yet.
/// - with the minimap's diagonal used directly, the zoom needs no correction factor. The
///   factor upstream applies exists purely to scale the WINDOW diagonal down to the map's,
///   so applying it here as well would shrink everything twice.
/// </remarks>
public sealed class MapRadarReader
{
    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _mapParent;
    private readonly int _largeMap;
    private readonly int _miniMap;
    private readonly int _shift;
    private readonly int _defaultShift;
    private readonly int _zoom;

    /// <summary>
    /// The minimap's diagonal, remembered for the large map, which cannot supply its own.
    /// </summary>
    /// <remarks>
    /// Cached rather than read on demand because the minimap is often hidden while the large
    /// map is open - the value is stable, so the last one seen is the right one to keep.
    /// </remarks>
    private float _lastMiniMapDiagonal;

    public MapRadarReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _elements = new UiElementReader(reader, schema);

        _mapParent = schema.Structs["ImportantUiElements"].OffsetOf("MapParentPtr");
        StructDef parent = schema.Structs["MapParentStruct"];
        _largeMap = parent.OffsetOf("LargeMapPtr");
        _miniMap = parent.OffsetOf("MiniMapPtr");
        StructDef map = schema.Structs["MapUiElement"];
        _shift = map.OffsetOf("Shift");
        _defaultShift = map.OffsetOf("DefaultShift");
        _zoom = map.OffsetOf("Zoom");
    }

    /// <summary>Addresses of the two map elements, or 0 when the chain does not resolve.</summary>
    public (ulong LargeMap, ulong MiniMap) Resolve(ulong uiRootStruct)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(uiRootStruct))
        {
            return (0, 0);
        }

        ulong parent = _reader.ReadPointer(uiRootStruct + (ulong)_mapParent);
        return !MemoryReaderExtensions.IsPlausiblePointer(parent)
            ? (0, 0)
            : (_reader.ReadPointer(parent + (ulong)_largeMap),
               _reader.ReadPointer(parent + (ulong)_miniMap));
    }

    /// <summary>
    /// Builds the projectable view of whichever map is asked for, or null when unavailable.
    /// </summary>
    public MapView? Read(ulong uiRootStruct, UiScale scale, bool largeMap)
    {
        (ulong large, ulong mini) = Resolve(uiRootStruct);

        // Read the minimap whenever it resolves, even when the large map was asked for:
        // that is what keeps its diagonal current for the large map to borrow.
        MapView? miniView = mini != 0 ? BuildMiniMap(mini, scale) : null;
        return largeMap ? BuildLargeMap(large, scale) : miniView;
    }

    private MapView? BuildMiniMap(ulong address, UiScale scale)
    {
        UiElement? element = _elements.Read(address, scale, withStringId: false);
        if (element is null || element.Size.X <= 20 || element.Size.Y <= 20)
        {
            return null; // no sensible size means no sensible diagonal
        }

        float diagonal = MathF.Sqrt((element.Size.X * element.Size.X) + (element.Size.Y * element.Size.Y));
        _lastMiniMapDiagonal = diagonal;

        // Top-left position, so the centre is half a size further in, plus the shifts.
        Vector2 centre = element.Position + (element.Size / 2f) + ReadShifts(address);
        return new MapView(
            centre, diagonal, ReadZoom(address), IsLargeMap: false, Visible: element.Visible,
            element.Left, element.Top, element.Size.X, element.Size.Y);
    }

    private MapView? BuildLargeMap(ulong address, UiScale scale)
    {
        UiElement? element = _elements.Read(address, scale, withStringId: false);
        if (element is null)
        {
            return null;
        }

        // Already the centre - the element is positioned against the screen centre, so
        // adding half its size (as the minimap needs) would push it off by that much.
        Vector2 centre = element.Position + ReadShifts(address);

        float diagonal = _lastMiniMapDiagonal > 0
            ? _lastMiniMapDiagonal
            : MathF.Sqrt((float)((scale.WindowWidth * (double)scale.WindowWidth)
                                 + (scale.WindowHeight * (double)scale.WindowHeight)));

        // The large map's own UnscaledSize reads 0, so there is no rectangle to clip to -
        // it covers the window, which is the honest bound for it anyway.
        return new MapView(
            centre, diagonal, ReadZoom(address), IsLargeMap: true, Visible: element.Visible,
            0f, 0f, scale.WindowWidth, scale.WindowHeight);
    }

    private Vector2 ReadShifts(ulong address)
    {
        Span<float> shift = stackalloc float[2];
        Span<float> defaultShift = stackalloc float[2];
        bool ok = _reader.TryRead(address + (ulong)_shift, System.Runtime.InteropServices.MemoryMarshal.AsBytes(shift))
                  && _reader.TryRead(address + (ulong)_defaultShift, System.Runtime.InteropServices.MemoryMarshal.AsBytes(defaultShift));

        return ok
            ? new Vector2(shift[0] + defaultShift[0], shift[1] + defaultShift[1])
            : Vector2.Zero;
    }

    /// <summary>
    /// Reads the zoom, falling back to the game's resting value when it reads implausibly.
    /// </summary>
    /// <remarks>
    /// A zero or wild zoom would divide the whole projection into nonsense, and the offset
    /// has drifted before, so an out-of-range read degrades to a slightly wrong map instead
    /// of markers scattered across the screen.
    /// </remarks>
    private float ReadZoom(ulong address)
    {
        float zoom = _reader.Read<float>(address + (ulong)_zoom);
        return zoom is > 0 and <= 20 ? zoom : 0.5f;
    }
}
