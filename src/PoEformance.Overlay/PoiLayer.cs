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
    private static readonly Vector4 DimText = OverlayInk.Quiet;

    private readonly RoutePlanner _planner;

    /// <summary>
    /// How every drawn thing looks. Shared with the overlay, so one editor covers both.
    /// </summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>The id this window's lock and click-through are filed under.</summary>
    public const string ChromeId = "poi";

    /// <summary>Whether this window is pinned in place or handed to the mouse.</summary>
    public WindowChrome Chrome { get; set; } = new();

    /// <summary>
    /// Finds the texture for a chosen picture, or zero when the shape should be drawn.
    /// </summary>
    /// <remarks>
    /// Handed in rather than owned, for the same reason the terrain layer's upload is: this
    /// class projects and draws, and it has no business holding a texture cache. Unset by
    /// default, which draws the shapes - so nothing here depends on a renderer existing.
    /// </remarks>
    public Func<string, IntPtr>? IconFor { get; set; }

    // Which colour each destination holds. Kept BY ADDRESS rather than by position, so
    // removing one route leaves the others' colours alone - a colour that moved when a
    // neighbour was dropped would make the map lie about which line goes where.
    private readonly Dictionary<ulong, int> _slots = [];

    /// <summary>Which kinds are marked. Everything that is a destination rather than a thing.</summary>
    /// <remarks>
    /// EVERY kind but None, and it is worth saying why rather than leaving the list to be
    /// read as a choice. Chest was the one missing, on the reasoning that a chest is a thing
    /// you find and not a place you walk to - which is wrong for the only chests that get
    /// this far. Classify already threw out the thousands of pots and passage chests; what
    /// survives is strongboxes and league reward chests, and a strongbox guarded by ten packs
    /// is exactly the kind of thing somebody wants a line drawn to. It cost the Vaal chest
    /// twice over: the classifier let it through and this dropped it again.
    /// </remarks>
    public HashSet<PoiKind> DrawnKinds { get; } =
    [
        PoiKind.AreaTransition, PoiKind.Waypoint, PoiKind.Checkpoint,
        PoiKind.Mechanic, PoiKind.Shrine, PoiKind.Npc, PoiKind.Chest,
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

    /// <summary>
    /// Called when one of the switches above moved, so the choice is written down.
    /// </summary>
    /// <remarks>
    /// SEVERAL OF THESE PERSIST - showPoi, poiLabels, poiRoutes, poiArrows all have a key in the
    /// settings file - and until this existed the two edited from inside the picker changed the
    /// value and told nobody. Holding a value and announcing that it moved are separate jobs,
    /// and a switch that does only the first is indistinguishable from one that does neither:
    /// the file is written when this fires and at no other time.
    /// </remarks>
    public Action? Changed { get; set; }

    /// <summary>
    /// Keep marking chests that have already been opened.
    /// </summary>
    /// <remarks>
    /// Off, because an opened chest is the one marker that actively misleads: it says "there
    /// is something over here" about a place already visited, and a map full of them is a map
    /// full of wasted walks. It stays available because the same fact answers a different
    /// question in a party - "did somebody already do that side".
    /// </remarks>
    public bool ShowSpent { get; set; }

    public PoiLayer(RoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    /// <summary>
    /// The colour a place is drawn in, taken from its SHAPE rather than its kind.
    /// </summary>
    /// <remarks>
    /// The shape is the finer distinction of the two, and it is the one worth colouring by: a
    /// breach and a ritual are both "a mechanic is here" and would share a colour, while being
    /// the two markers most worth telling apart at a glance.
    /// </remarks>
    private uint ColourFor(PoiGlyph glyph) => Style.Colour(StyleCatalogue.ForGlyph(glyph));

    /// <summary>
    /// The colour a destination's route is drawn in, held for as long as it is a destination.
    /// </summary>
    /// <remarks>
    /// Assigned to the lowest FREE slot rather than by position in the list, so dropping one
    /// route does not recolour the rest. The colours are chosen to stay apart on a dark, busy
    /// map and are deliberately not a hue sweep: half of one lands on the game's own map
    /// colours or on the terrain outline, and a route the same colour as a wall is worse than
    /// no route.
    /// </remarks>
    private uint RouteColour(ulong address) => Style.Colour(StyleCatalogue.ForRoute(RouteSlot(address)));

    /// <summary>Which route slot a destination holds, assigning one if it has none.</summary>
    private int RouteSlot(ulong address)
    {
        if (!_slots.TryGetValue(address, out int slot))
        {
            slot = 0;
            while (slot < RoutePlanner.MaxRoutes && _slots.ContainsValue(slot))
            {
                slot++;
            }

            slot %= RoutePlanner.MaxRoutes;
            _slots[address] = slot;
        }

        return slot;
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
    /// <param name="Remembered">
    /// Whether this comes from the memory of a place rather than from this read. A terrain
    /// landmark is never one: it is read out of the ground, which does not go out of range.
    /// </param>
    private readonly record struct Place(
        ulong Id, string Name, PoiKind Kind, float WorldX, float WorldY, float Height, string Icon,
        bool Spent = false, bool Remembered = false);

    /// <summary>Everything markable in the area, from both sources.</summary>
    private List<Place> PlacesIn(WorldSnapshot snapshot)
    {
        var places = new List<Place>();

        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (entity.IsPlace && DrawnKinds.Contains(entity.Poi))
            {
                places.Add(new Place(
                    entity.Address, entity.PoiName, entity.Poi,
                    entity.WorldX, entity.WorldY, entity.TerrainHeight, entity.MapIcon,
                    entity.IsSpent, entity.IsRemembered));
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
    /// <remarks>
    /// CLIPPED, once per piece of the map that may be drawn on - see <see cref="MapView.Uncovered"/>.
    /// A marker is a point and could be tested instead, and is; a ROUTE is a line hundreds of
    /// pixels long that has to be CUT where the game's interface starts rather than dropped,
    /// and a LABEL runs off to the right of the point that was tested. Only a clip rectangle
    /// answers either, and ImGui has one at a time.
    ///
    /// The places are gathered ONCE, outside the loop: the pieces do not overlap, so a marker
    /// lands in exactly one of them and the repeated passes cost a rejected point test rather
    /// than a second marker. Building the list per piece would allocate it per piece per frame.
    /// </remarks>
    public void DrawOnMap(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(player);

        ReleaseUnused();

        List<Place> places = PlacesIn(snapshot);

        foreach (ScreenRect piece in map.Uncovered)
        {
            draw.PushClipRect(piece.TopLeft, piece.BottomRight, intersect_with_current_clip_rect: true);
            DrawRoutes(draw, map, snapshot, player);
            DrawPlaces(draw, map, places, player);
            draw.PopClipRect();
        }
    }

    /// <summary>Every planned route, in its own colour.</summary>
    private void DrawRoutes(ImDrawListPtr draw, MapView map, WorldSnapshot snapshot, WorldEntity player)
    {
        if (!ShowRoutes)
        {
            return;
        }

        foreach (RouteView route in _planner.Routes)
        {
            string key = StyleCatalogue.ForRoute(RouteSlot(route.Target));
            if (Style.Visible(key))
            {
                DrawRoute(draw, map, snapshot, player, route, Style.Colour(key), Style.Width(key, 0f));
            }
        }
    }

    /// <summary>The landmark markers, and their names when the large map is open.</summary>
    private void DrawPlaces(
        ImDrawListPtr draw, MapView map, List<Place> places, WorldEntity player)
    {
        float radius = map.IsLargeMap ? 5f : 3.5f;

        foreach (Place place in places)
        {
            Vector2 at = map.Project(
                place.WorldX, place.WorldY, place.Height,
                player.WorldX, player.WorldY, player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            // A shape per kind of place, because at this size the silhouette is what carries
            // the meaning - the entity dots are circles, and a marker has to be told apart
            // from those and from each other while three pixels across.
            PoiGlyph glyph = PoiGlyphs.For(place.Icon, place.Kind);
            string key = StyleCatalogue.ForGlyph(glyph);

            if (!Style.Visible(key))
            {
                continue;
            }

            // A chest already opened is the one marker that is actively MISLEADING - it says
            // "there is something over here" about a place somebody has already been. Hidden
            // by default; kept behind a switch because on a map run with a party it also
            // answers "did we do this side already".
            if (place.Spent && !ShowSpent)
            {
                continue;
            }

            // A destination takes its ROUTE's colour, which is the whole reason several
            // routes can be read at once: the line and the end it leads to match.
            //
            // Faded once the game has stopped listing it. A place does not move - that is why
            // it is worth remembering at all - so the marker is where the thing is; what it
            // can no longer promise is that nobody has been there since, and a dimmer marker
            // is that difference said without taking the landmark off the map.
            bool routed = _planner.IsTarget(place.Id);
            float fade = place.Remembered ? OverlayStyle.RememberedAlpha : 1f;
            uint chosen = routed ? RouteColour(place.Id) : ColourFor(glyph);
            uint colour = OverlayStyle.Faded(chosen, fade);
            float size = Style.Sized(key, radius);

            // A chosen picture instead of the shape, and the SHAPE when there is none or it
            // could not be loaded - a marker that vanished because a file moved would read as
            // there being nothing there.
            IntPtr icon = IconFor?.Invoke(Style[key].Icon) ?? IntPtr.Zero;
            if (icon != IntPtr.Zero)
            {
                // Untinted unless a colour was chosen: somebody supplying a picture supplied
                // its colours, and multiplying it by this glyph's default would look broken.
                draw.AddImage(
                    icon, at - new Vector2(size, size), at + new Vector2(size, size),
                    Vector2.Zero, Vector2.One,
                    OverlayStyle.Faded(Style[key].ColourOr(0xFFFFFFFF), fade));
            }
            else
            {
                PoiGlyphPainter.Draw(draw, at, size, colour, glyph, Style.Width(key, 0f));
            }

            if (routed)
            {
                draw.AddCircle(at, size + 4f, colour, 16, 2f);
            }

            if (ShowLabels && map.IsLargeMap)
            {
                draw.AddText(
                    at + new Vector2(size + 3f, -7f),
                    OverlayStyle.Faded(Style[StyleCatalogue.Keys.PlaceLabel].ColourOr(chosen), fade),
                    place.Name);
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
        RouteView route, uint colour, float chosenWidth)
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

        float thickness = chosenWidth > 0f ? chosenWidth : map.IsLargeMap ? 2.5f : 1.5f;
        float arrowSize = Style.Sized(StyleCatalogue.Keys.RouteArrow, 6f);
        float sinceArrow = ArrowSpacing * 0.4f;   // one early, so a short route gets one at all

        Vector2 previous = Project(route.Cells[0]);
        for (int i = 1; i < route.Cells.Count; i++)
        {
            Vector2 next = Project(route.Cells[i]);
            draw.AddLine(previous, next, colour, thickness);

            if (ShowArrows && map.IsLargeMap && Style.Visible(StyleCatalogue.Keys.RouteArrow))
            {
                sinceArrow = DrawArrows(draw, previous, next, colour, sinceArrow, arrowSize);
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
    private static float DrawArrows(
        ImDrawListPtr draw, Vector2 from, Vector2 to, uint colour, float since, float size)
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
            Arrow(draw, from + (direction * at), direction, colour, size);
            since = 0f;
        }

        return since + (length % ArrowSpacing);
    }

    /// <summary>A chevron pointing the way the route runs.</summary>
    private static void Arrow(ImDrawListPtr draw, Vector2 at, Vector2 direction, uint colour, float size)
    {
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

        // Out of the way while it is lying over one of the game's own panels - see
        // WindowChrome.Covered. ShowPicker is left alone: this is the picker getting out from
        // under the stash for a moment, not the user closing it.
        if (Chrome.Covered(ChromeId))
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(360, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(40, 320), ImGuiCond.FirstUseEver);

        bool open = ShowPicker;
        bool expanded = ImGui.Begin(
            "Points of interest", ref open, Chrome.Flags(ChromeId, ImGuiWindowFlags.NoFocusOnAppearing));

        // Outside the expanded test: a collapsed window still covers its title bar, and where
        // it covers is what the next frame weighs against the game's panels.
        Chrome.Measure(ChromeId);

        if (expanded)
        {
            // Before the body, and the close button declared so they stop short of it.
            Chrome.TitleButtons(ChromeId, closable: true);

            if (player is null)
            {
                ImGui.TextColored(DimText, "not in an area");
            }
            else
            {
                DrawPickerBody(snapshot, player);
            }

            // LAST, after the contents. The menu declines to open over a control, and what is
            // under the cursor is only known once the controls have been submitted - asked
            // first it would steal the right-click every control here has its own use for.
            Chrome.Menu(ChromeId);
        }

        ImGui.End();

        // Only on an actual change: this runs every frame, and announcing "it is still open"
        // sixty times a second would rewrite the settings file sixty times a second.
        if (open != ShowPicker)
        {
            ShowPicker = open;
            Changed?.Invoke();
        }
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
                Changed?.Invoke();
            }
        }
        else
        {
            ImGui.TextColored(DimText, "click places to draw the way there - several at once");
        }

        ImGui.Separator();

        // Spent places are dropped from the LIST and stay on the map. An abyss that has
        // been run leaves its whole trail marked, and every one of those markers is a place
        // nobody is going to walk to again - as is a looted strongbox. Still routed to means
        // still listed, or the line on the map could not be turned off again.
        List<Place> places =
        [
            .. PlacesIn(snapshot)
                .Where(place => !place.Spent || _planner.IsTarget(place.Id))
                .OrderBy(place => Distance(place, player)),
        ];

        if (places.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing marked nearby");
            return;
        }

        foreach ((Place place, int repeats) in Collapse(places))
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
            string spent = place.Spent ? "  (opened)" : string.Empty;
            string more = repeats > 0 ? $"  x{repeats + 1}" : string.Empty;
            string away = route is { Cells.Count: >= 2 }
                ? $"{route.LengthCells:F0} walk"
                : route is not null && route.Status.Length > 0
                    ? route.Status
                    : routed
                        ? "looking for a way..."
                        : $"{Distance(place, player) / MapView.WorldToGrid:F0} direct";

            ImGui.PushStyleColor(
                ImGuiCol.Text,
                ImGui.ColorConvertU32ToFloat4(OverlayStyle.Faded(
                    routed ? RouteColour(place.Id) : ColourFor(PoiGlyphs.For(place.Icon, place.Kind)),
                    place.Remembered ? OverlayStyle.RememberedAlpha : 1f)));

            // ### rather than ##: the label is built from game data and everything after a ##
            // would be read as the identity, so two places could collapse into one row.
            bool clicked = ImGui.Selectable($"{place.Name}{spent}{more}  -  {away}###{place.Id:X}", routed);

            ImGui.PopStyleColor();

            if (clicked)
            {
                _planner.Toggle(place.Id, place.WorldX, place.WorldY);
            }
        }
    }

    /// <summary>
    /// One row per KIND of place, nearest first, with how many more of it there are.
    /// </summary>
    /// <remarks>
    /// A list is for choosing, and forty-eight rows reading "Abyss Crack Inactive" are not a
    /// choice - an Abyss map filled the panel with them and buried the one checkpoint in it,
    /// which is the only row anybody was going to click. The markers on the MAP stay: where
    /// the abyss runs is worth seeing, and that is what a map is for.
    ///
    /// Nearest survives because it is the only one of a repeated kind anybody walks to. A
    /// place already being routed to is never collapsed away - it has a line on the map in
    /// its own colour, and the row is how that line gets turned off again.
    /// </remarks>
    /// <param name="places">Places, already sorted by distance.</param>
    private List<(Place Place, int Repeats)> Collapse(List<Place> places)
    {
        var shown = new List<(Place Place, int Repeats)>();
        var firstOfKind = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Place place in places)
        {
            if (_planner.IsTarget(place.Id))
            {
                shown.Add((place, 0));
                continue;
            }

            if (firstOfKind.TryGetValue(place.Name, out int at))
            {
                shown[at] = (shown[at].Place, shown[at].Repeats + 1);
                continue;
            }

            firstOfKind[place.Name] = shown.Count;
            shown.Add((place, 0));
        }

        return shown;
    }

    private static float Distance(Place place, WorldEntity player)
    {
        float dx = place.WorldX - player.WorldX;
        float dy = place.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

}
