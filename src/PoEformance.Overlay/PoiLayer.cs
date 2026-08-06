using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Marks the places worth walking to on the game's map, and draws the way to several of them.
/// </summary>
/// <remarks>
/// Separate from the entity dots because these are not the same thing being drawn for the
/// same reason: a monster dot is a transient warning that wants to be small and quiet, while
/// an exit is a landmark that wants a name and stays put for the whole map. Mixing them left
/// the exits indistinguishable among forty dots, which is the state this exists to fix.
///
/// Several routes at once, each in its OWN colour, and the colour is what makes that useful
/// rather than a tangle: a route is only readable if you can tell which end it belongs to, so
/// the destination's marker and label take the route's colour as well. Two exits drawn in one
/// colour would be two lines and a guess.
///
/// The routes are found on the reader thread by <see cref="RoutePlanner"/>. Here they are only
/// projected and drawn - each corner at its own ground height, so a line follows the floor
/// over a hill rather than cutting through it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PoiLayer
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);

    /// <summary>
    /// One colour per route, chosen to stay apart on a dark, busy map.
    /// </summary>
    /// <remarks>
    /// Deliberately not a hue sweep: half of one lands on the game's own map colours or on the
    /// terrain outline, and a route the same colour as a wall is worse than no route.
    /// </remarks>
    private static readonly uint[] RouteColours =
    [
        Pack(0.35f, 1f, 0.75f),    // mint
        Pack(1f, 0.55f, 0.85f),    // pink
        Pack(1f, 0.75f, 0.3f),     // amber
        Pack(0.55f, 0.8f, 1f),     // sky
        Pack(0.8f, 1f, 0.4f),      // lime
    ];

    private readonly RoutePlanner _planner;

    // Which colour each destination holds. Kept BY ADDRESS rather than by position, so
    // removing one route leaves the others' colours alone - a colour that moved when a
    // neighbour was dropped would make the map lie about which line goes where.
    private readonly Dictionary<ulong, int> _slots = [];

    /// <summary>Which kinds are marked. Everything that is a destination rather than a thing.</summary>
    public HashSet<PoiKind> DrawnKinds { get; } =
    [
        PoiKind.AreaTransition, PoiKind.Waypoint, PoiKind.Checkpoint,
        PoiKind.Mechanic, PoiKind.Shrine, PoiKind.Npc,
        PoiKind.Quest, PoiKind.Marked, PoiKind.BossArena,
    ];

    /// <summary>Draw a name next to each marker.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Draw the walkable routes to the chosen places.</summary>
    public bool ShowRoutes { get; set; } = true;

    /// <summary>Draw chevrons along each route, pointing the way it runs.</summary>
    public bool ShowArrows { get; set; } = true;

    /// <summary>Whether the picker window is on screen.</summary>
    public bool ShowPicker { get; set; }

    public PoiLayer(RoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    /// <summary>Colour per kind, so a glance at the map is enough to tell them apart.</summary>
    private static uint ColourFor(PoiKind kind) => kind switch
    {
        PoiKind.AreaTransition => Pack(0.45f, 0.95f, 1f),   // the way out, and the brightest
        PoiKind.Waypoint => Pack(0.55f, 0.75f, 1f),
        PoiKind.Checkpoint => Pack(0.6f, 0.85f, 0.6f),
        PoiKind.Chest => Pack(1f, 0.85f, 0.4f),
        PoiKind.Mechanic => Pack(0.95f, 0.55f, 1f),
        PoiKind.Shrine => Pack(0.5f, 1f, 0.8f),
        PoiKind.Npc => Pack(1f, 0.95f, 0.7f),
        PoiKind.Quest => Pack(1f, 0.8f, 0.25f),   // what the game wants next, and it shows
        PoiKind.BossArena => Pack(1f, 0.4f, 0.35f),
        PoiKind.Marked => Pack(0.85f, 0.85f, 0.95f),
        _ => Pack(0.8f, 0.8f, 0.8f),
    };

    /// <summary>
    /// The colour a destination's route is drawn in, held for as long as it is a destination.
    /// </summary>
    /// <remarks>
    /// Assigned to the lowest FREE slot rather than by position in the list, so dropping one
    /// route does not recolour the rest.
    /// </remarks>
    private uint RouteColour(ulong address)
    {
        if (!_slots.TryGetValue(address, out int slot))
        {
            slot = 0;
            while (slot < RouteColours.Length && _slots.ContainsValue(slot))
            {
                slot++;
            }

            _slots[address] = slot % RouteColours.Length;
            slot = _slots[address];
        }

        return RouteColours[slot];
    }

    /// <summary>Drops colour slots for places no longer routed to, so they can be reused.</summary>
    private void ReleaseUnused()
    {
        if (_slots.Count == 0)
        {
            return;
        }

        foreach (ulong address in _slots.Keys.Where(a => !_planner.IsTarget(a)).ToList())
        {
            _slots.Remove(address);
        }
    }

    /// <summary>
    /// One place worth walking to, whatever it was found in.
    /// </summary>
    /// <remarks>
    /// Entities and terrain landmarks are drawn, listed and routed to identically, so the
    /// difference between "an exit stands there" and "the ground is shaped like an arena" ends
    /// at the reader. It has to: a boss arena is known from the moment the area loads, long
    /// before anything is standing in it.
    /// </remarks>
    /// <param name="Icon">
    /// The game's own name for the marker, where it has one. Carried because it is a FINER
    /// distinction than the kind: a breach and a ritual are both "a mechanic", and they are
    /// the two markers most worth telling apart on sight.
    /// </param>
    private readonly record struct Place(
        ulong Id, string Name, PoiKind Kind, float WorldX, float WorldY, float Height, string Icon);

    /// <summary>Everything markable in the area, from both sources.</summary>
    private List<Place> PlacesIn(WorldSnapshot snapshot)
    {
        var places = new List<Place>();

        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (entity.Poi != PoiKind.None && DrawnKinds.Contains(entity.Poi))
            {
                places.Add(new Place(
                    entity.Address, entity.PoiName, entity.Poi,
                    entity.WorldX, entity.WorldY, entity.TerrainHeight, entity.MapIcon));
            }
        }

        if (snapshot.Terrain is TerrainGrid terrain)
        {
            foreach (TerrainLandmark landmark in terrain.Landmarks)
            {
                if (!DrawnKinds.Contains(landmark.Kind))
                {
                    continue;
                }

                // No icon: a landmark is found in the shape of the ground, long before the
                // game has anything there to mark. Its kind picks the shape instead.
                places.Add(new Place(
                    landmark.Id, landmark.Name, landmark.Kind,
                    landmark.GridX * MapView.WorldToGrid, landmark.GridY * MapView.WorldToGrid,
                    terrain.HeightAt(landmark.GridX, landmark.GridY), string.Empty));
            }
        }

        return places;
    }

    /// <summary>Draws the markers and the routes onto whichever map is open.</summary>
    public void DrawOnMap(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        ReleaseUnused();

        if (ShowRoutes)
        {
            foreach (RouteView route in _planner.Routes)
            {
                DrawRoute(draw, map, snapshot, player, route, RouteColour(route.Target));
            }
        }

        float radius = map.IsLargeMap ? 5f : 3.5f;

        foreach (Place place in PlacesIn(snapshot))
        {
            Vector2 at = map.Project(
                place.WorldX, place.WorldY, place.Height,
                player.WorldX, player.WorldY, player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            // A destination takes its ROUTE's colour, which is the whole reason several
            // routes can be read at once: the line and the end it leads to match.
            bool routed = _planner.IsTarget(place.Id);
            uint colour = routed ? RouteColour(place.Id) : ColourFor(place.Kind);

            // A shape per kind of place, because at this size the silhouette is what carries
            // the meaning - the entity dots are circles, and a marker has to be told apart
            // from those and from each other while three pixels across.
            PoiGlyphPainter.Draw(draw, at, radius, colour, PoiGlyphs.For(place.Icon, place.Kind));

            if (routed)
            {
                draw.AddCircle(at, radius + 4f, colour, 16, 2f);
            }

            if (ShowLabels && map.IsLargeMap)
            {
                draw.AddText(at + new Vector2(radius + 3f, -7f), colour, place.Name);
            }
        }
    }

    /// <summary>
    /// Draws one route as a line that follows the ground, with chevrons along it.
    /// </summary>
    /// <remarks>
    /// Each corner is projected at ITS OWN ground height rather than the player's. The route
    /// crosses hills, and a line drawn at one height cuts through them - which on a map that
    /// otherwise lines up would read as the route going through a wall.
    /// </remarks>
    private void DrawRoute(
        ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player,
        RouteView route, uint colour)
    {
        if (route.Cells.Count < 2)
        {
            return;
        }

        TerrainGrid? grid = snapshot.Terrain;

        Vector2 Project((int X, int Y) cell)
        {
            float height = grid?.HeightAt(cell.X, cell.Y) ?? player.TerrainHeight;
            return map.Project(
                cell.X * MapView.WorldToGrid, cell.Y * MapView.WorldToGrid, height,
                player.WorldX, player.WorldY, player.TerrainHeight);
        }

        float thickness = map.IsLargeMap ? 2.5f : 1.5f;
        float sinceArrow = ArrowSpacing * 0.4f;   // one early, so a short route gets one at all

        Vector2 previous = Project(route.Cells[0]);
        for (int i = 1; i < route.Cells.Count; i++)
        {
            Vector2 next = Project(route.Cells[i]);
            draw.AddLine(previous, next, colour, thickness);

            if (ShowArrows && map.IsLargeMap)
            {
                sinceArrow = DrawArrows(draw, previous, next, colour, sinceArrow);
            }

            previous = next;
        }

        draw.AddCircleFilled(previous, map.IsLargeMap ? 4f : 3f, colour);
    }

    /// <summary>Screen pixels between direction chevrons.</summary>
    private const float ArrowSpacing = 90f;

    /// <summary>
    /// Places chevrons along one segment, and returns how far past the last one it ended.
    /// </summary>
    /// <remarks>
    /// Spaced by SCREEN distance, carried across segments. Spacing them by path points instead
    /// puts none on a long straight run - which is exactly where the direction is least
    /// obvious - and a cluster at every corner, where it is already clear.
    /// </remarks>
    private static float DrawArrows(ImDrawListPtr draw, Vector2 from, Vector2 to, uint colour, float since)
    {
        Vector2 along = to - from;
        float length = along.Length();
        if (length < 0.01f)
        {
            return since;
        }

        Vector2 direction = along / length;

        for (float at = ArrowSpacing - since; at < length; at += ArrowSpacing)
        {
            Arrow(draw, from + (direction * at), direction, colour);
            since = 0f;
        }

        return since + (length % ArrowSpacing);
    }

    /// <summary>A chevron pointing the way the route runs.</summary>
    private static void Arrow(ImDrawListPtr draw, Vector2 at, Vector2 direction, uint colour)
    {
        const float size = 6f;
        var side = new Vector2(-direction.Y, direction.X);
        Vector2 tip = at + (direction * size);
        Vector2 left = at - (direction * size * 0.5f) + (side * size * 0.7f);
        Vector2 right = at - (direction * size * 0.5f) - (side * size * 0.7f);

        draw.AddTriangleFilled(tip, left, right, colour);
    }

    /// <summary>
    /// The picker: nearby places, nearest first, and one click to route to one.
    /// </summary>
    /// <remarks>
    /// A window rather than clicking the map itself, and that is forced rather than chosen -
    /// the overlay is click-through over the game, so a marker cannot be clicked without
    /// taking the click away from the game underneath it.
    /// </remarks>
    public void DrawPicker(WorldSnapshot snapshot, WorldEntity? player)
    {
        if (!ShowPicker)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(360, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(40, 320), ImGuiCond.FirstUseEver);

        bool open = ShowPicker;
        if (ImGui.Begin("Points of interest", ref open, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (player is null)
            {
                ImGui.TextColored(DimText, "not in an area");
            }
            else
            {
                DrawPickerBody(snapshot, player);
            }
        }

        ImGui.End();
        ShowPicker = open;
    }

    private void DrawPickerBody(WorldSnapshot snapshot, WorldEntity player)
    {
        IReadOnlyList<RouteTarget> targets = _planner.Targets;

        if (targets.Count > 0)
        {
            ImGui.TextColored(DimText, $"{targets.Count} of {RoutePlanner.MaxRoutes} routes");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear all"))
            {
                _planner.Clear();
            }

            bool arrows = ShowArrows;
            ImGui.SameLine();
            if (ImGui.Checkbox("arrows", ref arrows))
            {
                ShowArrows = arrows;
            }
        }
        else
        {
            ImGui.TextColored(DimText, "click places to draw the way there - several at once");
        }

        ImGui.Separator();

        List<Place> places = [.. PlacesIn(snapshot).OrderBy(p => Distance(p, player))];

        if (places.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing marked nearby");
            return;
        }

        foreach (Place place in places)
        {
            bool routed = _planner.IsTarget(place.Id);
            RouteView? route = routed ? _planner.For(place.Id) : null;

            // The WALK when it is known, the straight line otherwise. Different numbers, and
            // the difference is the point - a wall between here and there is exactly what a
            // straight line cannot show.
            //
            // A chosen place with no answer yet is one still being searched for, and saying so
            // matters now that a route right across a map takes a second or two: the direct
            // distance sitting there unchanged reads as nothing having happened.
            string away = route is { Cells.Count: >= 2 }
                ? $"{route.LengthCells:F0} walk"
                : route is not null && route.Status.Length > 0
                    ? route.Status
                    : routed
                        ? "looking for a way..."
                        : $"{Distance(place, player) / MapView.WorldToGrid:F0} direct";

            ImGui.PushStyleColor(
                ImGuiCol.Text,
                ImGui.ColorConvertU32ToFloat4(routed ? RouteColour(place.Id) : ColourFor(place.Kind)));

            // ### rather than ##: the label is built from game data and everything after a ##
            // would be read as the identity, so two places could collapse into one row.
            bool clicked = ImGui.Selectable($"{place.Name}  -  {away}###{place.Id:X}", routed);

            ImGui.PopStyleColor();

            if (clicked)
            {
                _planner.Toggle(place.Id, place.WorldX, place.WorldY);
            }
        }
    }

    private static float Distance(Place place, WorldEntity player)
    {
        float dx = place.WorldX - player.WorldX;
        float dy = place.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static void Diamond(ImDrawListPtr draw, Vector2 at, float radius, uint colour)
    {
        draw.AddQuadFilled(
            at + new Vector2(0, -radius), at + new Vector2(radius, 0),
            at + new Vector2(0, radius), at + new Vector2(-radius, 0), colour);
        draw.AddQuad(
            at + new Vector2(0, -radius), at + new Vector2(radius, 0),
            at + new Vector2(0, radius), at + new Vector2(-radius, 0), 0xFF000000, 1f);
    }

    private static uint Pack(float r, float g, float b, float a = 1f)
        => ((uint)(a * 255) << 24) | ((uint)(b * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(r * 255);
}
