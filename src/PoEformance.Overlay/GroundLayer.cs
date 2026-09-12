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
/// ground types and an index per TILE CORNER into it - see <see cref="TerrainGroundTypes"/>,
/// which also carries the two checks this refuses to draw without. An earlier version of this
/// layer took the index from a landscape nibble instead, which was wrong and shipped; the corner
/// array is measured rather than supposed, and that class doc says on what.
///
/// WALLS AND CEILINGS ONLY WHEN THEY ARE WHAT IS LEFT - see TerrainGrid.FindGroundRegions, which
/// does the filtering so this draws what it is given. An area is mostly scenery you cannot enter,
/// so naming every wall patch buries the labels worth reading; but an area whose floor carries
/// the game's unnamed slot has nothing BUT walls and abysses to name, and there they are the
/// whole of what this can say.
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

    /// <summary>How often each type has been written this frame. Reused rather than rebuilt.</summary>
    private readonly Dictionary<string, int> _written = new(StringComparer.Ordinal);

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
    ///
    /// It is the only filter this layer applies. Which TYPES are worth naming is decided one
    /// level down, per area, off the measured walkable share - a question about the ground rather
    /// than about the drawing.
    /// </remarks>
    public int MinTiles { get; set; } = GroundSettings.Default.MinTiles;

    /// <summary>
    /// How many times one ground type may be written on the map.
    /// </summary>
    /// <remarks>
    /// THE FILTER THAT MATTERS MOST, and the one this layer shipped without. A ground type is not
    /// a room: every region of it carries the SAME word, so the twentieth "maelstrom_abyss" adds
    /// a position and no information, while costing the map its legibility. Size cannot thin them
    /// because the pieces are not small - see GroundSettings for the screenshot that settled it.
    ///
    /// The regions arrive largest-first within a type (TerrainRooms.Ranked), so the ones kept are
    /// the biggest - the places somebody can see they are standing in.
    /// </remarks>
    public int MaxPatches { get; set; } = GroundSettings.Default.MaxPatches;

    /// <summary>Takes the settings as they were loaded.</summary>
    public void Apply(GroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Enabled = settings.Show;
        MinTiles = settings.MinTiles;
        MaxPatches = settings.MaxPatches;
    }

    /// <summary>The settings as they stand now, for writing down.</summary>
    public GroundSettings Saved() => new(Enabled, MinTiles, MaxPatches);

    /// <summary>What the ground read came back as, for the readout. Empty when nothing was read.</summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>
    /// What the corner array actually holds, when the reading could not be believed.
    /// </summary>
    /// <remarks>
    /// ONLY WHEN IT FAILED, because that is the only time it is worth the space. A verdict says
    /// the pairing is wrong; these say what the values ARE, which is the thing that decides what
    /// to do about it - a handful of small ones is a fixed terrain classification, and values
    /// scattered over a whole byte are not a classification at all. See
    /// <see cref="TerrainGroundTypes.Lines"/>.
    /// </remarks>
    public IReadOnlyList<string> Diagnosis { get; private set; } = [];

    /// <summary>Writes the ground-type names onto the map.</summary>
    public void DrawOnMap(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        // Kept even on the frames that draw nothing: "why is this empty" is answered by the
        // note, and a note that only exists while the layer is on cannot answer it. From the
        // GRID rather than from the ground, because the ground is null exactly when the read
        // gave up - which is the case most in need of an explanation.
        Note = snapshot.Terrain?.GroundNote ?? string.Empty;

        // WHY THE MAP LOOKS DIFFERENT HERE. Naming walls and abysses is the fallback for an area
        // whose walkable ground carries no name, and without this sentence the switch between
        // the two reads as the feature being erratic rather than as a decision.
        //
        // The CONSEQUENCE only. The note it is appended to already ends in "0 of them ground you
        // can stand on", so a clause repeating the count read as "0 of them ... and none of them
        // is" on screen - the same fact twice, which makes a reader look for the difference.
        if (snapshot.Terrain is { NamingUnstandableGround: true } && Note.Length > 0)
        {
            Note += ", so the walls and the abyss are named instead";
        }

        Diagnosis = snapshot.Terrain?.Ground is { Trusted: false } ground ? ground.Lines : [];

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
        _written.Clear();

        foreach (TerrainRoom region in regions)
        {
            if (region.Tiles < MinTiles)
            {
                continue;
            }

            // COUNTED BEFORE THE PROJECTION, so the cap is a fact about the AREA rather than
            // about where the map happens to be scrolled. Counting only what lands on screen
            // would let the same type reappear as somebody pans, which reads as the setting not
            // working.
            ref int written = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_written, region.Path, out _);
            if (written >= MaxPatches)
            {
                continue;
            }

            written++;

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
