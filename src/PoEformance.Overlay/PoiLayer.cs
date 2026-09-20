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
    /// The icon sheet markers are cut from, or an empty picture when the shapes should be drawn.
    /// </summary>
    /// <remarks>
    /// Handed in rather than owned, for the same reason the terrain layer's upload is: this
    /// class projects and draws, and it has no business holding a texture cache. Unset by
    /// default, which draws the shapes - so nothing here depends on a renderer existing.
    /// </remarks>
    public Func<IconCache.Picture>? SheetFor { get; set; }

    // Which colour each destination holds. Kept BY ADDRESS rather than by position, so
    // removing one route leaves the others' colours alone - a colour that moved when a
    // neighbour was dropped would make the map lie about which line goes where.
    private readonly Dictionary<ulong, int> _slots = [];

    // The Active and Inactive cells each boss arena resolved to and what its boss is called,
    // with the area and the table revision they were resolved against. See BossArt.
    private readonly Dictionary<ulong, Mark> _art = [];
    private uint _artArea;
    private int _artRevision = -1;

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
        PoiKind.Quest, PoiKind.Marked, PoiKind.BossArena, PoiKind.Room,
    ];

    /// <summary>
    /// The rooms of the layout somebody pinned, which are places like any other.
    /// </summary>
    /// <remarks>
    /// Handed in by the overlay rather than read from the terrain, because which rooms are
    /// pinned is a decision <see cref="RoomLayer"/> owns - this only draws what it is given, in
    /// the same pass as everything else, so a pinned room takes a route's colour and a marker
    /// exactly as an exit does.
    /// </remarks>
    public IReadOnlyList<TerrainRoom> PickedRooms { get; set; } = [];

    /// <summary>
    /// Draw a boss arena as the game's own picture of its boss, where there is one.
    /// </summary>
    /// <remarks>
    /// ON, because the picture says strictly more than the shape it replaces and costs a
    /// dictionary lookup per arena per area to find. Off puts back the spiked star - and the
    /// cell somebody chose for the boss row, which the picture otherwise stands in front of
    /// for the same reason the game's own icon stands in front of one on the unrecognised row.
    /// </remarks>
    public bool ShowBossArt { get; set; } = true;

    /// <summary>Which picture belongs to which arena. Empty until the file is handed over.</summary>
    public BossIcons BossIcons { get; set; } = BossIcons.Empty;

    /// <summary>
    /// What a monster's metadata path is called in the game, for the arena's label.
    /// </summary>
    /// <remarks>
    /// A FUNCTION, because the monster table is replaced mid-session: the shipped export until
    /// the install's own .dat files have been walked, the game's own afterwards. Unset leaves
    /// the label the ground gives it, which is what it always was.
    /// </remarks>
    public Func<string, string>? MonsterName { get; set; }

    /// <summary>
    /// Which arenas have had their boss put down, or null while nothing is watching.
    /// </summary>
    /// <remarks>
    /// Null draws every arena Active, which is what an arena with no Inactive picture does
    /// anyway - so the watcher is an improvement on this layer rather than a dependency of it.
    /// </remarks>
    public BossArenas? Arenas { get; set; }

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
    /// <param name="Art">
    /// A sheet cell this place is drawn as, counted from ONE, or 0 for none. What a boss arena
    /// found in the tiles resolves to - see <see cref="BossArt"/>. Separate from
    /// <paramref name="Icon"/> because the two are different KINDS of claim: the icon is the
    /// name the game itself put on the marker, while this is worked out from an area id and a
    /// tile path, and folding an inference into a field that means "the game says so" is how
    /// the two stop being tellable apart.
    /// </param>
    private readonly record struct Place(
        ulong Id, string Name, PoiKind Kind, float WorldX, float WorldY, float Height, string Icon,
        bool Spent = false, bool Remembered = false, int Art = 0);

    /// <summary>
    /// What one boss arena resolved to: its two cells, and what the game calls its boss.
    /// </summary>
    /// <param name="Active">The cell while the boss lives, counted from ONE, or 0 for none.</param>
    /// <param name="Inactive">The cell once it is down, or 0 - then the Active one is kept.</param>
    /// <param name="Name">The boss's own name, where the curated file carries one, or empty.</param>
    private readonly record struct Mark(int Active, int Inactive, string Name)
    {
        /// <summary>
        /// Nothing known - what a landmark that is not an arena gets.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than default(Mark), because a record struct's default leaves its
        /// string NULL: the one caller reads Name.Length on every landmark of every frame.
        /// </remarks>
        public static Mark None { get; } = new(0, 0, string.Empty);
    }

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
            // Everything the arenas resolved to belongs to ONE area: the tiles are read as the
            // player walks, so the landmark list grows during a map, but a cell worked out for
            // a landmark cannot change while the area does not. Cleared here and filled per
            // landmark below, which follows that growth without rebuilding anything.
            //
            // OR WHILE THE TABLE DOES NOT, which is the other half of it now that the table can
            // be written from inside the tool: an entry filled in while standing in the arena
            // it is about has to reach the marker in front of the person writing it.
            if (snapshot.AreaHash != _artArea || BossIcons.Revision != _artRevision)
            {
                _artArea = snapshot.AreaHash;
                _artRevision = BossIcons.Revision;
                _art.Clear();
            }

            foreach (TerrainLandmark landmark in terrain.Landmarks)
            {
                if (!DrawnKinds.Contains(landmark.Kind))
                {
                    continue;
                }

                // No icon: a landmark is found in the shape of the ground, long before the
                // game has anything there to mark. Its kind picks the shape - except for a
                // boss arena, which the sheet may have the game's own picture of, and which
                // may be able to say WHOSE arena it is rather than "Boss Arena".
                Mark mark = BossMark(landmark, snapshot.Area.Id);
                places.Add(new Place(
                    landmark.Id, mark.Name.Length > 0 ? mark.Name : landmark.Name, landmark.Kind,
                    landmark.GridX * MapView.WorldToGrid, landmark.GridY * MapView.WorldToGrid,
                    terrain.HeightAt(landmark.GridX, landmark.GridY), string.Empty,
                    Art: BossArt(mark, landmark)));
            }

            // The pinned rooms, on the same terms. No icon and no kind of their own beyond
            // Room: what a room is called is the whole of what is known about it, which is
            // also why the name comes straight from the file rather than being tidied.
            if (DrawnKinds.Contains(PoiKind.Room))
            {
                foreach (TerrainRoom room in PickedRooms)
                {
                    places.Add(new Place(
                        room.Id, room.Name, PoiKind.Room,
                        room.GridX * MapView.WorldToGrid, room.GridY * MapView.WorldToGrid,
                        terrain.HeightAt((int)room.GridX, (int)room.GridY), string.Empty));
                }
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

        // Once for the whole list rather than once per marker: the sheet is one texture and
        // cannot change between two markers of the same frame.
        IconCache.Picture sheet = SheetFor?.Invoke() ?? default;

        foreach (Place place in places)
        {
            Vector2 projected = map.Project(
                place.WorldX, place.WorldY, place.Height,
                player.WorldX, player.WorldY, player.TerrainHeight);

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
            float remembered = place.Remembered ? OverlayStyle.RememberedAlpha : 1f;

            // On the map it is drawn where it is; off it, pinned to the frame on the bearing it
            // lies along - or dropped, when edge indicators are off for this map. A landmark is
            // what this is most worth doing for: an exit outside the minimap is exactly the
            // thing somebody is looking for a direction to.
            if (MapEdge.Place(Style, map, projected, Style.Sized(key, radius), remembered)
                is not (Vector2 at, float size, float fade))
            {
                continue;
            }

            uint chosen = routed ? RouteColour(place.Id) : ColourFor(glyph);
            uint colour = OverlayStyle.Faded(chosen, fade);

            // Three answers in order of how much each KNOWS: the game's own icon for a marker
            // nothing here could classify, then the cell somebody chose, then the shape. The
            // last is also what a sheet that did not ship falls back to - a marker that
            // vanished because its picture was missing would read as there being nothing
            // there, which is the one thing a map must never say by accident.
            //
            // THE GAME'S ANSWER GOES FIRST, and only on the unrecognised row - everywhere else
            // GameIcon is 0 and a chosen cell wins as it always did. The row is "I do not know
            // what this is", so an icon chosen on it is a stand-in for the unknown, and being
            // able to name the picture is no longer not knowing. The stand-in keeps the names
            // that have no cell, which is the sharper job for it: it then marks the markers
            // that really are a mystery instead of marking all of them alike.
            //
            // Untinted unless a colour was chosen - ColourOr reads the row's OWN colour, not
            // the catalogue's, so this is white until somebody sets one. The sheet's art
            // carries its own colours and multiplying it by a default would look broken; a
            // colour somebody did set was set to make these stand out, and still does.
            LayerStyle chosenStyle = Style[key];
            Vector2 corner = new(size, size);
            uint tint = OverlayStyle.Faded(chosenStyle.ColourOr(0xFFFFFFFF), fade);
            if (!SheetIcon.Tile(draw, sheet, GameIcon(glyph, place), at - corner, at + corner, tint)
                && !SheetIcon.Draw(draw, sheet, chosenStyle, at - corner, at + corner, tint))
            {
                PoiGlyphPainter.Draw(draw, at, size, colour, glyph, Style.Width(key, 0f));
            }

            if (routed)
            {
                draw.AddCircle(at, size + 4f, colour, 16, 2f);
            }

            if (ShowLabels && map.IsLargeMap)
            {
                Vector2 label = at + new Vector2(size + 3f, -7f);
                uint ink = OverlayStyle.Faded(
                    Style[StyleCatalogue.Keys.PlaceLabel].ColourOr(chosen), fade);
                draw.AddText(label, ink, place.Name);
                Unrecognised(draw, label, ink, glyph, place);
            }
        }
    }

    /// <summary>
    /// The sheet cell holding the game's own icon for a marker this cannot classify.
    /// </summary>
    /// <remarks>
    /// THE NAME THAT FAILED THE KEYWORDS IS ALSO THE CELL'S NAME. An entity's MinimapIcon
    /// component names its icon out of MinimapIcons.dat, and the sheet's cells were named from
    /// art files published under those same names - so the marker the rules gave up on can
    /// still be drawn as the picture the game itself draws for it. It stops being a question
    /// mark without anybody adding a keyword for it, which is the cheapest classification
    /// there is: the game already did it.
    ///
    /// ONLY the unrecognised ones, deliberately. A chest drawn as a chest is a shape that says
    /// "container" across a whole map at a glance, and trading the shapes for the game's art
    /// would give back exactly the map the game already draws. This fills the hole where there
    /// was no shape worth having, and touches nothing that was already working.
    ///
    /// A BOSS ARENA ANSWERS FIRST WHERE IT HAS AN ANSWER, and it is the same argument one step
    /// further on. The game has a picture for about thirty of its bosses and draws it on its
    /// own map; the arena is found in the tiles before anything is standing in it, so the
    /// marker can wear that picture from the moment the area loads. Which picture is
    /// <see cref="BossArt"/>'s business - here it is a cell that was already worked out, and 0
    /// everywhere nothing was.
    ///
    /// 0 for everything else, including a name no cell carries - about half the sheet is not
    /// in the icon set at all - and those fall through to whatever was chosen for the
    /// unrecognised row, or to the shape, and go on being collected by UnrecognisedMarkers.
    ///
    /// WHY THIS BEATS A CHOSEN CELL, which nothing else does. It cost an evening to find:
    /// somebody had set the unrecognised row to a question mark to spot these more easily,
    /// which is exactly what a person who cares about them would do - and that choice then
    /// stood in front of the answer, so the feature was invisible to the one person looking
    /// hardest. An icon on that row is a stand-in for "unknown", and this is the thing that
    /// says it is no longer unknown.
    /// </remarks>
    private static int GameIcon(PoiGlyph glyph, Place place)
        => place.Art > 0 ? place.Art
            : glyph == PoiGlyph.Marker ? IconNames.CellFor(place.Icon)
            : 0;

    /// <summary>
    /// The sheet cell for one boss arena: the game's own picture of whatever lives in it.
    /// </summary>
    /// <remarks>
    /// TWO CELLS ARE RESOLVED AND ONE IS RETURNED, because the pair belongs to the arena while
    /// the choice between them belongs to the frame: <see cref="BossArenas"/> flips to the
    /// Inactive art once the boss has been put down, the way the game flips its own landmarks,
    /// and a boss that comes back flips it straight back. Resolving both at once means that
    /// costs a bool rather than a second walk through the candidates.
    ///
    /// NAMES, NEVER CELL NUMBERS, all the way down - see <see cref="BossIcons"/>. The sheet
    /// grows when art is added and every number after the insertion point moves with it; a
    /// name stays a name, so a boss whose picture arrives in a later release starts working
    /// without anybody editing anything.
    ///
    /// A family with only ONE picture is used for both states. Some of them have no
    /// Active/Inactive pair at all - BreachBoss, ExpeditionBoss - and showing that one twice
    /// says less than the pair does, which is better than showing nothing.
    ///
    /// CACHED PER LANDMARK, because this walks up to half a dozen candidate names through the
    /// sheet's name table and the answer cannot change while the area does not. The cache is
    /// emptied when the area hash moves; see where it is filled in <see cref="PlacesIn"/>.
    /// </remarks>
    private int BossArt(Mark mark, TerrainLandmark landmark)
    {
        if (!ShowBossArt)
        {
            return 0;
        }

        bool cleared = Arenas?.IsCleared(landmark.Id) == true;
        return cleared && mark.Inactive > 0 ? mark.Inactive : mark.Active;
    }

    /// <summary>
    /// What is known about one boss arena: its two pictures and the name of what stands in it.
    /// </summary>
    /// <remarks>
    /// RESOLVED EVEN WHEN THE PICTURES ARE SWITCHED OFF, because the name is not art. The
    /// switch on the tab is about what shape the marker wears; "Saphira, The Dread Consort"
    /// instead of "Plantaton Boss" is the same claim the label always made, made accurately,
    /// and turning the pictures off to see the shapes is no reason to go back to calling the
    /// arena after the tile it is built from. It also keeps the collecting running, which is
    /// what fills the list of arenas still to name.
    /// </remarks>
    private Mark BossMark(TerrainLandmark landmark, string areaId)
    {
        if (landmark.Kind != PoiKind.BossArena)
        {
            return Mark.None;
        }

        if (!_art.TryGetValue(landmark.Id, out Mark mark))
        {
            mark = Resolve(areaId, landmark);
            _art[landmark.Id] = mark;
        }

        return mark;
    }

    /// <summary>
    /// Walks the candidate names and takes the first the sheet actually carries.
    /// </summary>
    /// <remarks>
    /// The sheet is the arbiter, which is what makes a DERIVED candidate safe to try at all:
    /// "G4_3_1_Boss" only wins because there is a picture under exactly that name, and the
    /// hundreds of names that could be built from a tile path resolve to nothing and are
    /// dropped. An arena no candidate matched is written down rather than forgotten, so the
    /// curated file can be filled from what was played - see <see cref="BossIcons.NoteMissing"/>.
    /// </remarks>
    private Mark Resolve(string areaId, TerrainLandmark landmark)
    {
        // THE NAME OUTLIVES THE PICTURE, which is why both are walked for rather than one. An
        // entry written from the model pane names its boss the moment it is saved, while the
        // picture it points at only starts resolving once that art has been laid into the
        // sheet - so a marker that says "Saphira, The Dread Consort" over the ordinary arena
        // shape is the correct middle state, and stopping at the first picture would lose the
        // name of every boss whose cell has not been pasted in yet.
        string called = string.Empty;
        var art = (Active: 0, Inactive: 0);
        foreach (string family in BossIcons.Candidates(areaId, landmark.Path))
        {
            if (called.Length == 0)
            {
                called = BossIcons.NameOf(family);
            }

            if (art.Active == 0 && art.Inactive == 0)
            {
                int active = IconNames.CellFor(BossIcons.Named(family, cleared: false));
                int inactive = IconNames.CellFor(BossIcons.Named(family, cleared: true));
                int plain = active > 0 || inactive > 0 ? 0 : IconNames.CellFor(family);

                if (active > 0 || inactive > 0 || plain > 0)
                {
                    art = (active > 0 ? active : Math.Max(inactive, plain), inactive);
                }
            }

            if (called.Length > 0 && (art.Active > 0 || art.Inactive > 0))
            {
                break;
            }
        }

        if (art.Active > 0 || art.Inactive > 0)
        {
            return new Mark(art.Active, art.Inactive, called.Length > 0 ? called : Called(areaId));
        }

        // THE GAME'S OWN ANSWER, WHERE THE CURATED FILE HAS NONE. WorldAreas says which monster
        // is this area's boss for 125 of the atlas's 173 maps, and the monster table says what
        // it is called - so "Saphira, The Dread Consort" stands over the arena in both of her
        // maps with nothing written down at all. The curated name still wins: it comes from
        // somebody who stood in the room, and the column holds an NPC on two maps.
        if (called.Length == 0)
        {
            called = Called(areaId);
        }

        // COLLECTED ONLY WHEN NOTHING AT ALL IS KNOWN. An arena whose boss the game names but
        // whose picture has not been made yet is not a mystery - it is a row in the to-do list,
        // with its name on it. The log is for the ones nobody can name, which is what makes it
        // worth reading.
        if (called.Length == 0)
        {
            BossIcons.NoteMissing(areaId, landmark.Path, landmark.Name);
        }

        return new Mark(0, 0, called);
    }

    /// <summary>What the game calls the boss of an area, or empty.</summary>
    private string Called(string areaId)
    {
        if (MonsterName is not { } naming)
        {
            return string.Empty;
        }

        foreach (string path in BossIcons.BossesIn(areaId))
        {
            if (naming(path) is { Length: > 0 } named)
            {
                return named;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// What the GAME calls a marker this cannot classify, beside its label.
    /// </summary>
    /// <remarks>
    /// THE ONE MARKER THAT CANNOT ANSWER FOR ITSELF. Every other shape says what it is by
    /// being that shape; the unrecognised one says only "the game marked this and I do not
    /// know what it is" - and gave no way to find out, because the icon name it failed to
    /// recognise was read, used, and then dropped. Three of these turned up in one small area
    /// with nothing to go on but their positions.
    ///
    /// The name is what the KEYWORDS ARE MATCHED AGAINST - see PoiGlyphs.FromName - so it is
    /// drawn raw, joined words and all, rather than spaced out for reading. Spacing it would
    /// show something subtly different from what the matching actually sees, which on the one
    /// screen somebody is using to work out why a marker went unrecognised is the wrong
    /// difference to introduce.
    ///
    /// ONLY on the unrecognised ones. A chest that draws as a chest has already said
    /// everything its icon name would, and forty of these down a map is a wall of file names.
    /// </remarks>
    private static void Unrecognised(
        ImDrawListPtr draw, Vector2 label, uint ink, PoiGlyph glyph, Place place)
    {
        if (glyph != PoiGlyph.Marker || place.Icon.Length == 0)
        {
            return;
        }

        // Skip it when the label IS the icon name: an entity with no name of its own already
        // falls back to the spaced-out form of exactly this string, and printing both makes
        // the map say the same thing twice.
        if (string.Equals(place.Name, PointsOfInterest.Readable(place.Icon), StringComparison.Ordinal))
        {
            return;
        }

        // Quieter than the name, because it is what the tool knows rather than what the thing
        // is called - and in brackets, so it reads as machinery rather than as part of a name.
        // The ink already carries the marker's own fade, so this only takes it down further.
        draw.AddText(
            label + new Vector2(ImGui.CalcTextSize(place.Name).X + 5f, 0f),
            OverlayStyle.Faded(ink, 0.65f),
            $"[{place.Icon}]");
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
            if (ImGui.SmallButton("Clear All"))
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
