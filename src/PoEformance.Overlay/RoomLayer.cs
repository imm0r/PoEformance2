using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Writes the layout's own room names onto the game's map, and pins the ones worth walking to.
/// </summary>
/// <remarks>
/// THE LAYOUT IN WORDS. The terrain layer draws the area's shape; this says what the parts of
/// it are called - "exit_01", "overlay_bridge_03", "3open_01" - because the game builds an area
/// out of named room files and every tile carries the name of the one it belongs to. Both come
/// from the same read (see <see cref="TerrainRooms"/>), and they answer different halves of the
/// same question: the outline shows there is a way through over there, the name says it is the
/// exit.
///
/// THE LARGE MAP ONLY, and that is not a limitation to be lifted later. This writes a name on
/// every room in the area; on a minimap the size of a postage stamp that is a solid block of
/// text with a map somewhere underneath. What still shows on the minimap is what somebody
/// PINNED, and that goes through the ordinary place markers - see <see cref="Picked"/>.
///
/// HOW THE MOUSE GETS HERE AT ALL, since the overlay is normally transparent to it. Two facts,
/// both of them <see cref="WindowChrome"/>'s and both checked there rather than assumed: the
/// cursor's position keeps arriving while the overlay is click-through, because
/// ClickableTransparentOverlay reads it with <c>GetCursorPos</c> rather than from window
/// messages - so HOVERING costs nothing and needs no permission. Button presses do come from
/// messages, so a CLICK needs the overlay to be clickable, which is one global flag asserted
/// for exactly as long as it is wanted. Here that is "the cursor is on a room AND ctrl is
/// held", which is as narrow as it can be made: without ctrl every click still reaches the
/// game, so dragging and zooming the map are untouched.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RoomLayer
{
    /// <summary>Ctrl, as the system reports it - see <see cref="ScreenInput.IsDown"/>.</summary>
    private const int VkControl = 0x11;

    /// <summary>How near the cursor has to be to a room's dot to count as pointing at it.</summary>
    /// <remarks>
    /// Generous, because the alternative is worse than a stray hit: the dot is a few pixels
    /// across and the label above it is what the eye is actually aiming at, so the label's own
    /// rectangle counts as well and this only has to cover the gap between the two.
    /// </remarks>
    private const float ReachPixels = 10f;

    /// <summary>The plate under a name, so it reads over the terrain rather than into it.</summary>
    /// <remarks>
    /// ABGR as ImGui packs it. Not a style entry: a label with no backing is unreadable over a
    /// lit map rather than merely different, so this is not a choice to offer.
    /// </remarks>
    private const uint Plate = 0xB4_1A1614;

    private readonly RoutePlanner _planner;

    /// <summary>Which rooms are pinned, by area id, exactly as the settings file keeps them.</summary>
    /// <remarks>
    /// EVERY area's, not just this one's. Walking out of an area must not drop what was pinned
    /// in it - the picks are written back whole, and one area's visit would otherwise erase
    /// every other area's line.
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> _picks = new(StringComparer.Ordinal);

    private readonly List<TerrainRoom> _picked = [];

    // What _picked was resolved against, so it is rebuilt when either changes and not per
    // frame. The grid is compared by REFERENCE: the reader hands out the same instance for as
    // long as the area lasts, and builds a new one when it changes.
    private TerrainGrid? _resolved;
    private string _area = string.Empty;

    public RoomLayer(RoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    /// <summary>How every drawn thing looks. Shared with the overlay, so one editor covers both.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Whether the names are written on the map at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Rooms smaller than this are scenery and are left unnamed.</summary>
    public int MinTiles { get; set; } = RoomSettings.Default.MinTiles;

    /// <summary>Only rooms whose name contains this, when it is set.</summary>
    public string Filter { get; set; } = string.Empty;

    /// <summary>Called when a pick moved, so the choice is written down.</summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// The rooms pinned in the area the player is in.
    /// </summary>
    /// <remarks>
    /// Drawn by <see cref="PoiLayer"/> rather than here, and that is the whole point of pinning
    /// one: a pinned room becomes an ordinary place, with a marker, a name and a route, on
    /// whichever map is open. This layer's own drawing is the browsing half.
    /// </remarks>
    public IReadOnlyList<TerrainRoom> Picked => _picked;

    /// <summary>Takes the settings as they were loaded.</summary>
    public void Apply(RoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Enabled = settings.Show;
        MinTiles = settings.MinTiles;
        Filter = settings.Filter;

        _picks.Clear();
        if (settings.Picked is not null)
        {
            foreach ((string area, IReadOnlyList<string> keys) in settings.Picked)
            {
                _picks[area] = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Whatever was resolved belongs to the old picks, so it is resolved again on the next
        // frame rather than left standing beside a list it no longer matches.
        _resolved = null;
    }

    /// <summary>
    /// The settings as they stand now, for writing down.
    /// </summary>
    /// <remarks>
    /// An area whose picks are all gone loses its entry, and a file with no picks at all keeps
    /// no key: an untouched setting must leave the file as it was found, and unpinning the one
    /// room you pinned is exactly that.
    /// </remarks>
    public RoomSettings Saved()
    {
        var picked = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach ((string area, HashSet<string> keys) in _picks)
        {
            if (keys.Count > 0)
            {
                picked[area] = [.. keys];
            }
        }

        return new RoomSettings(Enabled, MinTiles, Filter, picked.Count > 0 ? picked : null);
    }

    /// <summary>Writes the names onto the map, and answers the mouse over them.</summary>
    public void DrawOnMap(
        ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        // First, and whatever else this frame does: the pinned rooms are drawn by the place
        // layer, so they have to be resolved even on the frames this one draws nothing.
        Resolve(snapshot);

        if (!Enabled
            || !map.IsLargeMap
            || snapshot.Terrain is not TerrainGrid terrain
            || !Style.Visible(StyleCatalogue.Keys.Room))
        {
            return;
        }

        uint colour = Style.Colour(StyleCatalogue.Keys.Room);
        float dot = Style.Sized(StyleCatalogue.Keys.Room, 2.5f);
        Vector2 mouse = ImGui.GetMousePos();

        // PROJECTED ONCE, then drawn once per piece of map that may be drawn on. An area has
        // thousands of rooms and the pieces are re-entered for every one of them, so doing the
        // projection inside that loop would repeat the whole area's arithmetic per piece - and
        // the map's own bounds throw most of it away in the first pass anyway.
        _onScreen.Clear();
        TerrainRoom? under = null;
        float nearest = float.MaxValue;

        // The cursor has to be ON the map for anything to be pointed at, and the map's own test
        // is the one that knows about the game's interface: a label whose room sits at the edge
        // can still run under the orbs, and a tooltip raised from there would describe a room
        // the cursor is not on.
        bool onMap = map.Contains(mouse);

        foreach (TerrainRoom room in terrain.Rooms)
        {
            // THE FILTER THAT DOES THE WORK, and size is not it. An area's tile grid is a full
            // rectangle while its walkable ground is a subset, so the buildings along the road,
            // the sea beside them and the wall behind the fence are all rooms with names - and
            // scenery blocks are LARGE, so a threshold in tiles keeps exactly the labels worth
            // dropping. Ground somebody can stand on is what tells the two apart.
            if (!room.IsWalkable
                || room.Tiles < MinTiles
                || (Filter.Length > 0
                    && !room.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Vector2 at = Project(map, terrain, room, player);
            if (!map.Contains(at))
            {
                continue;
            }

            Vector2 size = ImGui.CalcTextSize(room.Name);
            var label = new Vector2(at.X - (size.X * 0.5f), at.Y - size.Y - dot - 3f);

            // Pinned rooms are left to the place layer, which draws them with a marker and
            // their route's colour. Drawing them here as well would put two names on one spot,
            // one of them in the wrong colour - but they still answer the cursor, because
            // unpinning one has to be possible where pinning it was.
            _onScreen.Add(new OnScreen(room, at, label, size, IsPicked(room)));

            // The cursor is tested against the label as well as the dot, because the label is
            // what the eye aims at - and ties go to the nearest DOT, so two rooms whose labels
            // overlap resolve to the one actually being pointed at.
            float away = Vector2.Distance(mouse, at);
            bool touching = away <= ReachPixels
                || (mouse.X >= label.X - 3f && mouse.X <= label.X + size.X + 3f
                    && mouse.Y >= label.Y - 1f && mouse.Y <= label.Y + size.Y + 1f);

            if (onMap && touching && away < nearest)
            {
                nearest = away;
                under = room;
            }
        }

        foreach (ScreenRect piece in map.Uncovered)
        {
            draw.PushClipRect(piece.TopLeft, piece.BottomRight, intersect_with_current_clip_rect: true);

            foreach (OnScreen shown in _onScreen)
            {
                if (shown.Pinned)
                {
                    continue;
                }

                draw.AddRectFilled(
                    shown.Label - new Vector2(3f, 1f),
                    shown.Label + shown.Size + new Vector2(3f, 1f),
                    Plate,
                    3f);
                draw.AddText(shown.Label, colour, shown.Room.Name);
                draw.AddCircleFilled(shown.At, dot, colour, 8);
            }

            draw.PopClipRect();
        }

        if (under is not null)
        {
            Answer(under, mouse, terrain);
        }
    }

    /// <summary>One room as it landed on screen this frame.</summary>
    private readonly record struct OnScreen(
        TerrainRoom Room, Vector2 At, Vector2 Label, Vector2 Size, bool Pinned);

    // Reused rather than built per frame: this is the one per-frame allocation the layer would
    // otherwise make, and it would be made while the map is open and nothing else is.
    private readonly List<OnScreen> _onScreen = [];

    /// <summary>The tooltip, and the click that pins or unpins what it describes.</summary>
    /// <remarks>
    /// On the FOREGROUND list rather than the one the map is drawn on: a tooltip that a later
    /// layer paints over is a tooltip that cannot be read, and this is drawn outside any ImGui
    /// window so <c>SetTooltip</c> is not available to do it properly.
    /// </remarks>
    private void Answer(TerrainRoom room, Vector2 mouse, TerrainGrid terrain)
    {
        bool picked = IsPicked(room);
        bool ctrl = ScreenInput.IsDown(VkControl);

        string[] lines =
        [
            room.Path,
            $"Tiles ({room.MinTileX},{room.MinTileY})-({room.MaxTileX},{room.MaxTileY})"
            + $"   {room.Tiles} across, {room.WalkableTiles} walkable"
            + $"   ground {terrain.HeightAt((int)room.GridX, (int)room.GridY):F0}",
            $"Centre ({room.GridX:F1}, {room.GridY:F1}) in grid cells",
            picked ? "Ctrl + click to unpin it" : "Ctrl + click to pin it, with a route",
        ];

        ImDrawListPtr front = ImGui.GetForegroundDrawList();
        var pad = new Vector2(8f, 6f);
        float width = 0f;
        float height = 0f;
        foreach (string line in lines)
        {
            Vector2 size = ImGui.CalcTextSize(line);
            width = Math.Max(width, size.X);
            height += size.Y + 2f;
        }

        // Kept on screen: a room at the right-hand edge of the map would otherwise describe
        // itself off the side of it.
        Vector2 io = ImGui.GetIO().DisplaySize;
        var box = new Vector2(width, height) + (pad * 2f);
        var from = new Vector2(
            Math.Clamp(mouse.X + 16f, 0f, Math.Max(0f, io.X - box.X)),
            Math.Clamp(mouse.Y + 16f, 0f, Math.Max(0f, io.Y - box.Y)));

        front.AddRectFilled(from, from + box, 0xE6_1A1614, 4f);
        front.AddRect(from, from + box, Style.Colour(StyleCatalogue.Keys.Room), 4f);

        Vector2 where = from + pad;
        uint text = 0xFF_E6E6E6;
        foreach (string line in lines)
        {
            front.AddText(where, text, line);
            where.Y += ImGui.CalcTextSize(line).Y + 2f;
        }

        if (!ctrl)
        {
            return;
        }

        // The one frame the overlay asks for the mouse, and only while ctrl is held over a
        // room. The press rather than the release, for WindowChrome's reason: the flag is read
        // at the top of the next frame, so the first half of a click may still have gone to the
        // game.
        ImGui.SetNextFrameWantCaptureMouse(true);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Toggle(room);
        }
    }

    /// <summary>Pins a room or drops it, and starts or stops the route with it.</summary>
    private void Toggle(TerrainRoom room)
    {
        if (_area.Length == 0)
        {
            return;   // no area id means no key to file the choice under
        }

        if (!_picks.TryGetValue(_area, out HashSet<string>? keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _picks[_area] = keys;
        }

        if (!keys.Remove(room.Key))
        {
            keys.Add(room.Key);
        }

        // The route follows the pin, because pinning a room is saying "I want to go there".
        // Only on the click, though: coming back to an area restores the marker and leaves the
        // routes off, which is the planner's own rule for crossing an area boundary.
        _planner.Toggle(room.Id, room.GridX * MapView.WorldToGrid, room.GridY * MapView.WorldToGrid);

        _resolved = null;
        Changed?.Invoke();
    }

    /// <summary>How many rooms are pinned in the area the player is in, matched or not.</summary>
    /// <remarks>
    /// The STORED count rather than <see cref="Picked"/>'s, and the difference is the point: an
    /// endgame map is generated per instance, so a pick made in one is a key that will never
    /// match again in the next. Those are invisible on the map and still in the settings file,
    /// which is exactly the state <see cref="Forget"/> exists to end.
    /// </remarks>
    public int PinnedHere
        => _picks.TryGetValue(_area, out HashSet<string>? keys) ? keys.Count : 0;

    /// <summary>Drops every pick made in this area, including ones no room matches any more.</summary>
    public void Forget()
    {
        if (_picks.Remove(_area))
        {
            foreach (TerrainRoom room in _picked)
            {
                if (_planner.IsTarget(room.Id))
                {
                    _planner.Toggle(room.Id, room.GridX * MapView.WorldToGrid, room.GridY * MapView.WorldToGrid);
                }
            }

            _picked.Clear();
            _resolved = null;
            Changed?.Invoke();
        }
    }

    private bool IsPicked(TerrainRoom room)
        => _picks.TryGetValue(_area, out HashSet<string>? keys) && keys.Contains(room.Key);

    /// <summary>Matches the saved keys against the area's rooms, when either has changed.</summary>
    private void Resolve(WorldSnapshot snapshot)
    {
        TerrainGrid? terrain = snapshot.Terrain;
        if (ReferenceEquals(terrain, _resolved) && _area == snapshot.Area.Id)
        {
            return;
        }

        _resolved = terrain;
        _area = snapshot.Area.Id;
        _picked.Clear();

        if (terrain is null || !_picks.TryGetValue(_area, out HashSet<string>? keys) || keys.Count == 0)
        {
            return;
        }

        foreach (TerrainRoom room in terrain.Rooms)
        {
            if (keys.Contains(room.Key))
            {
                _picked.Add(room);
            }
        }
    }

    /// <summary>
    /// Where a room sits on the map.
    /// </summary>
    /// <remarks>
    /// At the ground's height under its centre, like every other marker: on the map a thing is
    /// placed by the floor it stands on, so a room up on a ledge belongs where the ledge is.
    /// </remarks>
    private static Vector2 Project(
        MapView map, TerrainGrid terrain, TerrainRoom room, WorldEntity player)
        => map.Project(
            room.GridX * MapView.WorldToGrid,
            room.GridY * MapView.WorldToGrid,
            terrain.HeightAt((int)room.GridX, (int)room.GridY),
            player.WorldX,
            player.WorldY,
            player.TerrainHeight);
}
