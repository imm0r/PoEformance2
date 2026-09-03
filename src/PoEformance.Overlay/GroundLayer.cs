using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Writes what the GROUND is on the game's map, in the names the area itself lists.
/// </summary>
/// <remarks>
/// A LEVEL ABOVE THE ROOM NAMES. A room name says which file the game built a block from -
/// "BonesOuter_St_3" - which is only meaningful to somebody who has read the layout. A ground
/// type says what the block IS: bone_abyss, waypoint_ground, bone_fill. The two are drawn by
/// two layers because they answer different questions and a person will want one or the other,
/// rarely both at once.
///
/// WHY IT CAN BE DRAWN AT ALL is worth keeping here, because the route to it was long and the
/// obvious one is a dead end: a room file names its ground types and never its tiles, so the
/// chain room-to-tile-to-position does not exist. What does exist is the area's own list of
/// ground types and a nibble per cell indexing it - see <see cref="TerrainGroundTypes"/>, which
/// also carries the two checks this refuses to draw without.
///
/// NO PINNING AND NO ROUTE, unlike <see cref="RoomLayer"/>. A ground type is not a destination;
/// "the abyss" covers a third of the area and walking to its centroid means nothing. What this
/// is for is reading the map, so it writes names and stops.
///
/// THE LARGE MAP ONLY, for the reason the room names are: a name per region on a minimap is a
/// block of text with a map somewhere underneath.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GroundLayer
{
    /// <summary>The plate under a name, so it reads over the terrain rather than into it.</summary>
    /// <remarks>ABGR as ImGui packs it. The same plate the room names use, deliberately.</remarks>
    private const uint Plate = 0xB4_1A1614;

    private readonly List<ScreenRect> _wanted = [];
    private readonly List<int> _kept = [];
    private readonly List<(string Name, Vector2 Label, Vector2 Size)> _onScreen = [];

    /// <summary>How every drawn thing looks. Shared with the overlay, so one editor covers both.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Whether the ground types are written on the map at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Regions smaller than this are not named.
    /// </summary>
    /// <remarks>
    /// A REAL FILTER HERE, unlike on the rooms. An area has a handful of ground types rather
    /// than hundreds of room files, so its regions are few and large and their sizes actually
    /// spread - the specks a threshold drops are the one-tile slivers where two types meet,
    /// which is exactly what nobody wants named.
    /// </remarks>
    public int MinTiles { get; set; } = GroundSettings.Default.MinTiles;

    /// <summary>Takes the settings as they were loaded.</summary>
    public void Apply(GroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Enabled = settings.Show;
        MinTiles = settings.MinTiles;
    }

    /// <summary>The settings as they stand now, for writing down.</summary>
    public GroundSettings Saved() => new(Enabled, MinTiles);

    /// <summary>What the ground read came back as, for the readout. Empty when nothing was read.</summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>Writes the ground-type names onto the map.</summary>
    public void DrawOnMap(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        // Kept even on the frames that draw nothing: "why is this empty" is answered by the
        // note, and a note that only exists while the layer is on cannot answer it.
        Note = snapshot.Terrain?.Ground?.Note ?? string.Empty;

        if (!Enabled
            || !map.IsLargeMap
            || snapshot.Terrain is not TerrainGrid terrain
            || !Style.Visible(StyleCatalogue.Keys.Ground))
        {
            return;
        }

        // GroundRegions is empty unless the read passed both checks - see TerrainGroundTypes.
        // A plausible map of nonsense is worse than no map, so the refusal lives at the source
        // rather than in a flag this layer could forget to test.
        IReadOnlyList<TerrainRoom> regions = terrain.GroundRegions;
        if (regions.Count == 0)
        {
            return;
        }

        uint colour = Style.Colour(StyleCatalogue.Keys.Ground);
        float dot = Style.Sized(StyleCatalogue.Keys.Ground, 2.5f);

        _onScreen.Clear();
        _wanted.Clear();

        foreach (TerrainRoom region in regions)
        {
            if (region.Tiles < MinTiles)
            {
                continue;
            }

            Vector2 at = map.Project(
                region.GridX * MapView.WorldToGrid,
                region.GridY * MapView.WorldToGrid,
                terrain.HeightAt((int)region.GridX, (int)region.GridY),
                player.WorldX,
                player.WorldY,
                player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            string name = TerrainGroundTypes.NameFor(region.Path);
            Vector2 size = ImGui.CalcTextSize(name);
            var label = new Vector2(at.X - (size.X * 0.5f), at.Y - size.Y - dot - 3f);

            _onScreen.Add((name, label, size));
            _wanted.Add(new ScreenRect(label.X, label.Y, label.X + size.X, label.Y + size.Y));
        }

        // The regions arrive rarest-first and largest-first within that (TerrainRooms.Ranked),
        // so a name that would land on one already written is the less informative of the two.
        LabelPacking.Keep(_wanted, _kept);

        foreach (int i in _kept)
        {
            (string name, Vector2 label, Vector2 size) = _onScreen[i];
            draw.AddRectFilled(
                label - new Vector2(3f, 1f), label + size + new Vector2(3f, 1f), Plate, 2f);
            draw.AddText(label, colour, name);
        }
    }
}
