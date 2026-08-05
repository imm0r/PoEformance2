using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Marks the places worth walking to on the game's map, and draws the way to one of them.
/// </summary>
/// <remarks>
/// Separate from the entity dots because these are not the same thing being drawn for the
/// same reason: a monster dot is a transient warning that wants to be small and quiet, while
/// an exit is a landmark that wants a name and stays put for the whole map. Mixing them left
/// the exits indistinguishable among forty dots, which is the state this exists to fix.
///
/// The route is found on the reader thread by <see cref="RoutePlanner"/>. Here it is only
/// projected and drawn - each corner at its own ground height, so the line follows the floor
/// over a hill rather than cutting through it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PoiLayer
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);

    private readonly RoutePlanner _planner;

    /// <summary>Which kinds are marked. Everything that is a destination rather than a thing.</summary>
    public HashSet<PoiKind> DrawnKinds { get; } =
    [
        PoiKind.AreaTransition, PoiKind.Waypoint, PoiKind.Checkpoint,
        PoiKind.Mechanic, PoiKind.Shrine, PoiKind.Npc,
        PoiKind.Quest, PoiKind.Marked,
    ];

    /// <summary>Draw a name next to each marker.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Draw the walkable route to the chosen point.</summary>
    public bool ShowRoute { get; set; } = true;

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
        PoiKind.Marked => Pack(0.85f, 0.85f, 0.95f),
        _ => Pack(0.8f, 0.8f, 0.8f),
    };

    /// <summary>Draws the markers and the route onto whichever map is open.</summary>
    public void DrawOnMap(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        if (ShowRoute)
        {
            DrawRoute(draw, map, snapshot, player);
        }

        ulong target = _planner.Target;
        float radius = map.IsLargeMap ? 5f : 3.5f;

        foreach (WorldEntity poi in snapshot.Entities)
        {
            if (poi.Poi == PoiKind.None || !DrawnKinds.Contains(poi.Poi))
            {
                continue;
            }

            Vector2 at = map.Project(
                poi.WorldX, poi.WorldY, poi.TerrainHeight,
                player.WorldX, player.WorldY, player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            uint colour = ColourFor(poi.Poi);
            bool chosen = poi.Address == target;

            // A diamond, not a circle: the entity dots are circles, and the difference has to
            // survive being three pixels across on a minimap.
            Diamond(draw, at, radius, colour);

            if (chosen)
            {
                draw.AddCircle(at, radius + 4f, colour, 16, 2f);
            }

            if (ShowLabels && map.IsLargeMap)
            {
                draw.AddText(at + new Vector2(radius + 3f, -7f), colour, poi.PoiName);
            }
        }
    }

    /// <summary>
    /// Draws the route as a line that follows the ground.
    /// </summary>
    /// <remarks>
    /// Each corner is projected at ITS OWN ground height rather than the player's. The route
    /// crosses hills, and a line drawn at one height cuts through them - which on a map that
    /// otherwise lines up would read as the route going through a wall.
    /// </remarks>
    private void DrawRoute(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        RouteView route = _planner.View;
        if (route.Cells.Count < 2)
        {
            return;
        }

        TerrainGrid? grid = snapshot.Terrain;
        uint colour = Pack(0.35f, 1f, 0.75f, 0.9f);

        Vector2 Project((int X, int Y) cell)
        {
            float height = grid?.HeightAt(cell.X, cell.Y) ?? player.TerrainHeight;
            return map.Project(
                cell.X * MapView.WorldToGrid, cell.Y * MapView.WorldToGrid, height,
                player.WorldX, player.WorldY, player.TerrainHeight);
        }

        Vector2 previous = Project(route.Cells[0]);
        for (int i = 1; i < route.Cells.Count; i++)
        {
            Vector2 next = Project(route.Cells[i]);
            draw.AddLine(previous, next, colour, map.IsLargeMap ? 2.5f : 1.5f);
            previous = next;
        }

        draw.AddCircleFilled(previous, map.IsLargeMap ? 4f : 3f, colour);
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

        ImGui.SetNextWindowSize(new Vector2(340, 320), ImGuiCond.FirstUseEver);
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
        RouteView route = _planner.View;
        ulong target = _planner.Target;

        if (target != 0)
        {
            string distance = route.Cells.Count >= 2
                ? $"{route.LengthWorld / MapView.WorldToGrid:F0} cells to walk"
                : route.Status.Length > 0 ? route.Status : "finding a way...";

            ImGui.TextColored(new Vector4(0.35f, 1f, 0.75f, 1f), $"routing: {distance}");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear"))
            {
                _planner.Clear();
            }
        }
        else
        {
            ImGui.TextColored(DimText, "click a place to draw the way there");
        }

        ImGui.Separator();

        List<WorldEntity> places = [.. snapshot.Entities
            .Where(e => e.Poi != PoiKind.None && DrawnKinds.Contains(e.Poi))
            .OrderBy(e => Distance(e, player))];

        if (places.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing marked nearby");
            return;
        }

        foreach (WorldEntity place in places)
        {
            float away = Distance(place, player) / MapView.WorldToGrid;
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.ColorConvertU32ToFloat4(ColourFor(place.Poi)));

            // ### rather than ##: the label is built from a game path and everything after a
            // ## would be read as the identity, so two places could collapse into one row.
            bool clicked = ImGui.Selectable(
                $"{place.PoiName}  -  {away:F0}###{place.Address:X}", place.Address == target);

            ImGui.PopStyleColor();

            if (clicked)
            {
                _planner.Request(place.Address == target
                    ? RouteRequest.None
                    : new RouteRequest(place.Address, place.WorldX, place.WorldY));
            }
        }
    }

    private static float Distance(WorldEntity entity, WorldEntity player)
    {
        float dx = entity.WorldX - player.WorldX;
        float dy = entity.WorldY - player.WorldY;
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
