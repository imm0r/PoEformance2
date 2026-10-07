using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The tile reference book: every terrain tile and room the install has, a tile's geometry and a room's doodads.
/// </summary>
/// <remarks>
/// THE ITEM BOOK'S PARTS, with a tile in the pane instead of an item, and the picture is a
/// <see cref="MonsterPortrait"/> of its own whose load is <see cref="Load"/> rather than the .ao
/// walk: <see cref="TileModels.Of"/> for a tile, <see cref="RoomModels.Of"/> for a room.
///
/// "ONLY THIS AREA" IS A TERM IN THE QUERY - <c>here:yes</c> - written and read back by the
/// checkbox above the list, so the rail, the count and the text all agree on what is shown.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TileBookWindow : BookWindow<TileBook>
{
    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// THE SET - Woods, Maps, Act4 - is the handful of answers a click is for; names and folders
    /// have thousands and are typed. "Here" is the same as the checkbox, counted.
    /// </remarks>
    private static readonly (string Label, string Field)[] Offered =
    [
        ("Kind", "kind"),
        ("Sets", "set"),
        ("This area", "here"),
        ("With the clock", "clock"),
    ];

    /// <summary>The query term the "only this area" checkbox writes.</summary>
    private const string HereField = "here";

    /// <summary>The colour the unpainted ground is drawn in: a dark, flat grey that stays behind the props.</summary>
    public static readonly Vector3 GroundInk = new(0.34f, 0.34f, 0.35f);

    private readonly Func<IReadOnlyList<string>> _install;
    private readonly Func<IReadOnlyDictionary<string, int>> _placed;

    /// <summary>Whether a tile is drawn with its ground block. See TileModels.Of.</summary>
    private bool _ground = true;

    /// <summary>Whether a tile is drawn with its black walls. See TileModels.BlackWall.</summary>
    private bool _walls = true;

    /// <summary>
    /// The tileset a tile is drawn as, or empty for the tile's own materials.
    /// </summary>
    /// <remarks>
    /// KEPT ACROSS TILES, and used only where the chosen tile is one the tileset places - so a look
    /// through one area's tiles stays in that area, and a tile it does not place is drawn as itself.
    /// </remarks>
    private string _tileset = string.Empty;

    /// <summary>
    /// The placement a tile is turned to, or -1 for the file's own orientation - see TileOrientation.Placement.
    /// </summary>
    /// <remarks>
    /// KEPT ACROSS TILES like the tileset, and used only where the chosen tile was laid that way here.
    /// </remarks>
    private int _laid = -1;

    /// <summary>Whether a room places the level editor's tools - see RoomModels.IsTool. Kept in the settings; see <see cref="Tools"/>.</summary>
    private bool _tools;

    /// <summary>Most doodads a room places - see RoomModels.UsualDoodads. Kept in the settings; see <see cref="Doodads"/>.</summary>
    private int _doodads = RoomModels.UsualDoodads;

    /// <summary>
    /// The slider's value while it is being dragged, apart from <see cref="_doodads"/>.
    /// </summary>
    /// <remarks>
    /// A ROOM IS RELOADED WHEN THE SLIDER IS LET GO, not at every step of the drag: the ship in Port's
    /// boss room is 179 MB of files, and a reload per step would read it dozens of times over.
    /// </remarks>
    private int _dragging = RoomModels.UsualDoodads;

    /// <summary>The lists the book was built from, compared by reference to notice a new one.</summary>
    private IReadOnlyList<string>? _installed;
    private IReadOnlyDictionary<string, int>? _placedOf;
    private IReadOnlyDictionary<string, string>? _needsOf;
    private IReadOnlySet<string>? _clocksOf;
    private TileBook _page = TileBook.Empty;

    /// <summary>What the portrait is handed - a name to show and the tile's path as the key - made once per choice.</summary>
    private MonsterVariety? _subject;
    private string _subjectKey = string.Empty;

    public TileBookWindow(Func<IReadOnlyList<string>> install, Func<IReadOnlyDictionary<string, int>> placed)
        : base("tile", TileBook.Empty, 0.18f, 0.45f)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(placed);
        _install = install;
        _placed = placed;
    }

    /// <summary>Which tilesets place which tile, for the "drawn as" choice. Null leaves the choice out.</summary>
    public TilesetCatalog? Tilesets { get; init; }

    /// <summary>What each placed tile needs that the compiler has not got, read off the frame for the "needs" column. Null leaves the column empty.</summary>
    public AreaNeeds? Needs { get; init; }

    /// <summary>Which tiles of the whole install run with the clock, read in the background for clock:yes. Null leaves the install out of it.</summary>
    public TileClockCatalog? Clocks { get; init; }

    /// <summary>
    /// The ways each tile was laid in the current area, by path, one bit per TileOrientation.Placement. Null leaves the choice out.
    /// </summary>
    public Func<IReadOnlyDictionary<string, byte>>? Laid { get; init; }

    /// <summary>
    /// Most doodads a room places, as the slider has it - what the settings keep.
    /// </summary>
    /// <remarks>Out-of-range values come back at the slider's nearest end; zero, "not set", is the usual.</remarks>
    public int Doodads
    {
        get => _doodads;
        set
        {
            _doodads = value <= 0 ? RoomModels.UsualDoodads : Math.Clamp(value, RoomModels.LeastDoodads, RoomModels.MostDoodads);
            _dragging = _doodads;
        }
    }

    /// <summary>The current area's terrain, for finding where a room it loaded was laid. Null leaves the search out.</summary>
    public Func<TerrainGrid?>? Terrain { get; init; }

    /// <summary>How to read a file out of the install - the room's own, for the search.</summary>
    public Func<string, byte[]?>? Read { get; init; }

    /// <summary>Told which candidate the large map should outline, or null for none.</summary>
    public Action<RoomGhost?>? Ghost { get; init; }

    /// <summary>The tile the player stands on, for the "around you" row. Null leaves the row out.</summary>
    public Func<(int X, int Y)?>? Here { get; init; }

    /// <summary>The search running or run for <see cref="_searchedFor"/> in <see cref="_searchedIn"/>.</summary>
    private Task<RoomSearch>? _searching;
    private RoomSearch? _found;

    /// <summary>The rows of <see cref="_found"/> in words, made once per search rather than once per frame.</summary>
    private string[] _rows = [];
    private string _searchedFor = string.Empty;
    private TerrainGrid? _searchedIn;

    /// <summary>Which row the map outlines: a candidate's index, <see cref="GhostAround"/>, or -1 for none.</summary>
    private int _ghosted = -1;

    /// <summary>The "around you" row's value of <see cref="_ghosted"/>.</summary>
    private const int GhostAround = -2;

    /// <summary>Whether a room whose place is outlined is drawn as the area laid it - see LaidRoomModels.</summary>
    private bool _asLaid = true;

    /// <summary>Whether a laid room's pieces are set at their tiles' own levels rather than fitted to the area's ground - see LaidRoomModels.</summary>
    private bool _atLevel;

    /// <summary>Every tile file's identity read for the area in <see cref="_identitiesOf"/> - one read per file across every room searched there.</summary>
    private ConcurrentDictionary<string, TileIdentity?> _identities = new(StringComparer.OrdinalIgnoreCase);
    private TerrainGrid? _identitiesOf;

    /// <summary>The "around you" probe running, and the search it belongs to - one at a time.</summary>
    private (Task<RoomPlace?> Task, RoomSearch For)? _probing;
    private RoomPlace? _around;
    private string _aroundRow = string.Empty;

    /// <summary>The outlined candidate's misses in words, made once per pick - see <see cref="Parting"/>.</summary>
    private string[] _partLines = [];
    private string _partsHeader = "##roomparts";
    private RoomSearch? _partsOf;
    private RoomCandidate? _partsFor;
    private RoomSearch? _probedIn;
    private (int X, int Y) _probedAt = (-1, -1);

    /// <summary>Whether the room search lists places covering no walkable ground too, and whether the search in hand did.</summary>
    private bool _anywhere;
    private bool _searchedAnywhere;

    /// <summary>Whether a room places the level editor's tools, as the box has it - what the settings keep.</summary>
    public bool Tools
    {
        get => _tools;
        set => _tools = value;
    }

    /// <summary>Whether the room search lists places covering no walkable ground too, as the box has it - what the settings keep.</summary>
    public bool Anywhere
    {
        get => _anywhere;
        set => _anywhere = value;
    }

    /// <inheritdoc/>
    protected override string Caption => "Search for any terrain tile or room";

    /// <inheritdoc/>
    protected override string Grammar => "arena  ·  kind:room  ·  set:woods  ·  here:yes  ·  clock:yes  ·  needs:InputVertexColor  ·  folder:areatransitions";

    /// <inheritdoc/>
    protected override string Noun => "tiles";

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Field)> Rails => Offered;

    /// <summary>
    /// Works the two lists into columns, once per pair.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE: the install's list arrives once from a background walk, and the area's
    /// placements are a new dictionary whenever the area changes - and only then.
    /// </remarks>
    protected override TileBook Current()
    {
        IReadOnlyList<string> installed = _install();
        IReadOnlyDictionary<string, int> placings = _placed();

        // THE NEEDS ARRIVE LATER THAN THE LISTS, read off the frame as the tileset is chosen, and the
        // book is built again when they do - a reference compare per frame until then.
        AreaReading? reading = Needs?.Ready(placings, _tileset);
        IReadOnlyDictionary<string, string>? needs = reading?.Needs;
        IReadOnlySet<string>? clocks = Clocks?.Ready;
        if (!ReferenceEquals(_installed, installed) || !ReferenceEquals(_placedOf, placings) || !ReferenceEquals(_needsOf, needs)
            || !ReferenceEquals(_clocksOf, clocks))
        {
            _installed = installed;
            _placedOf = placings;
            _needsOf = needs;
            _clocksOf = clocks;
            _page = TileBook.Of(installed, placings, needs, reading?.Clocked, clocks);
        }

        return _page;
    }

    /// <inheritdoc/>
    protected override string WhyEmpty()
        => "No tiles yet - the install's list is read shortly after start-up, and an area's tiles once one is loaded.";

    /// <inheritdoc/>
    protected override Vector4? Ink(int row) => Page.HereRows[row] ? OverlayInk.Name : null;

    /// <summary>The "only this area" checkbox, read back out of the query so editing the text by hand moves the tick.</summary>
    protected override void HeaderExtras()
    {
        ImGui.SameLine();
        bool only = Holds(HereField, TileBook.Here);
        if (ImGui.Checkbox("Only this area", ref only))
        {
            Toggle(HereField, TileBook.Here);
        }

        if (Clocks is { Reading: true } clocking)
        {
            (int done, int of) = clocking.Progress;
            ImGui.SameLine();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"clock: {done} of {of} read"));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Reading every tile of the install - its templates' materials and their shader graphs, no geometry -"
                    + " to fill the \"clock\" column for all of them: clock:yes finds the tiles whose picture runs with the game's clock."
                    + " Rooms are answered only where the current area places them.");
            }
        }

        if (Needs is { Reading: true } reading)
        {
            (int done, int of) = reading.Progress;
            ImGui.SameLine();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"needs: {done} of {of} read"));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Reading every tile and room of this area the way the pane does, to fill the \"needs\" column:"
                    + " what each one's shader graphs leave out. Search it with needs:InputVertexColor.");
            }
        }
    }

    /// <summary>
    /// What the portrait loads for a key: a tile's geometry, or a room's doodads up to the chosen cap.
    /// </summary>
    /// <remarks>
    /// THE CAP RIDES IN THE KEY - see <see cref="RoomKey"/> - so changing it is a different key and the
    /// portrait reloads, the same way the item book's drop or held choice does, without the portrait
    /// knowing about rooms. A tile's choices ride the same way - see <see cref="TileKey"/> - and the load reads the
    /// named tileset's overrides itself, so the key is the whole of what is drawn and two tilesets
    /// are two keys.
    /// </remarks>
    /// <param name="read">How to read a file out of the install.</param>
    /// <param name="key">What to load - see <see cref="TileKey"/> and <see cref="RoomKey"/>.</param>
    /// <param name="terrain">The current area, for a room laid from its tiles; null leaves such a room out.</param>
    /// <param name="progress">Where the build says how far it has got, for the pane's bar, or null.</param>
    public static MonsterModel Load(Func<string, byte[]?> read, string key, Func<TerrainGrid?>? terrain = null, ModelProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        // THE PATH BEFORE THE MARK says which it is: a room's key carries words after it too.
        int mark = key.IndexOf(TileKey.Mark, StringComparison.Ordinal);
        if (!TileBook.IsRoom(mark >= 0 ? key[..mark] : key))
        {
            TileKey tile = TileKey.Read(key);
            return TileModels.Of(
                read,
                tile.Path,
                ground: tile.Ground,
                walls: tile.Walls,
                shaded: true,
                swaps: tile.Tileset.Length > 0 ? TilesetIndex.Overrides(read, tile.Tileset) : null,
                tileset: tile.Tileset.Length > 0 ? TilesetIndex.Short(tile.Tileset) : string.Empty,
                laid: TileOrientation.OfPlacement(tile.Laid),
                progress: progress);
        }

        RoomKey room = RoomKey.Read(key);
        if (!room.TryLaid(out int x, out int y, out int turn, out int area))
        {
            return RoomModels.Of(read, room.Path, shaded: true, doodads: room.Doodads, tools: room.Tools, progress: progress);
        }

        // ONLY IN THE AREA THE PLACE WAS FOUND IN: a place is a tile of one area's grid, and the key
        // names that grid so a new area cannot lay the room over somebody else's tiles.
        TerrainGrid? grid = terrain?.Invoke();
        return grid is not null && Stamp(grid) == area
            ? LaidRoomModels.Of(read, room.Path, grid, x, y, turn, shaded: true, doodads: room.Doodads, tools: room.Tools, atLevel: room.AtLevel, progress: progress)
            : MonsterModel.None with { Why = "the area this place was found in is gone - pick the room's place again" };
    }

    /// <summary>The mark a key carries for the area a room's place was found in - the grid's own, for this session.</summary>
    public static int Stamp(TerrainGrid grid) => RuntimeHelpers.GetHashCode(grid);

    /// <summary>What the tile or room is and where it is used, then its geometry under it.</summary>
    protected override void Pane()
    {
        string chosen = Chosen;
        int row = Page.Row(chosen);
        if (row < 0)
        {
            ImGui.TextDisabled("Choose a tile or a room on the left.");
            return;
        }

        bool room = TileBook.IsRoom(chosen);
        (string set, string folder, string name) = TileBook.Split(chosen);
        ImGui.TextUnformatted(name);
        ImGui.TextDisabled(ImGuiText.Escape(
            (room ? "room  ·  " : "tile  ·  ") + (folder.Length > 0 ? $"{set}  ·  {folder}" : set)));

        int placed = Page.Placed[row];
        ImGui.TextDisabled(room
            ? placed > 0 ? "loaded for this area" : "not loaded for this area"
            : placed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"placed {placed} times in this area")
                : "not placed in this area");

        ImGui.TextDisabled(ImGuiText.Escape(chosen));

        if (!room)
        {
            ImGui.Checkbox("ground", ref _ground);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The tile's ground block, drawn plain. A tileset's MaterialsList names the ground textures - the dump lists them -"
                    + " but the ground has no texture coordinates, and how the engine makes them is not yet known."
                    + " Off shows the props alone, where the ground rises around them.");
            }

            ImGui.SameLine();
            ImGui.Checkbox("black walls", ref _walls);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The shapes painted with blacknofog.dds: walls hanging from a cliff's edge to close the gap under it."
                    + " The game's camera never looks behind them; off leaves them out.");
            }

            DrawnAs(chosen);
            LaidAs(chosen);
        }
        else
        {
            DoodadCap();
            if (placed > 0)
            {
                Whereabouts(chosen);
            }
        }

        ImGui.Separator();

        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        string key = room
            ? LaidKey(chosen)
            : new TileKey(chosen, _ground, _walls, Placing(chosen), Laying(chosen)).ToString();
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: name);
        }

        Vector2 avail = ImGui.GetContentRegionAvail();
        model.Draw(_subject, _subjectKey, avail.X, avail.Y);
    }

    /// <summary>The chosen tileset where it places this tile, or empty - see <see cref="_tileset"/>.</summary>
    private string Placing(string tile)
    {
        if (_tileset.Length == 0 || Tilesets?.Ready is not { } index)
        {
            return string.Empty;
        }

        foreach (string one in index.Of(tile))
        {
            if (string.Equals(one, _tileset, StringComparison.OrdinalIgnoreCase))
            {
                return one;
            }
        }

        return string.Empty;
    }

    /// <summary>What the doodads slider is for.</summary>
    private static readonly string DoodadsSaid = string.Create(CultureInfo.InvariantCulture,
        $"How many doodads a room places before the rest are left out, and {RoomModels.TrianglesPerDoodad} triangles for each. The picture is drawn on the processor, so more turns more slowly. Usually {RoomModels.UsualDoodads}; the room reloads when the slider is let go. Ctrl+click types a number.");

    /// <summary>
    /// The slider for how many doodads a room places, applied when it is let go.
    /// </summary>
    private void DoodadCap()
    {
        ImGui.SetNextItemWidth(Math.Min(ImGui.GetContentRegionAvail().X, 260f));
        ImGui.SliderInt("doodads at most##roomcap", ref _dragging, RoomModels.LeastDoodads, RoomModels.MostDoodads,
            "%d", ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.AlwaysClamp);
        if (ImGui.IsItemDeactivatedAfterEdit() && _dragging != _doodads)
        {
            _doodads = Math.Clamp(_dragging, RoomModels.LeastDoodads, RoomModels.MostDoodads);
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(DoodadsSaid);
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("tools##roomtools", ref _tools))
        {
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The level editor's own pieces: walk blockers and markers. The game places them and does not draw them -"
                + " the room names DoodadInvisible beside a blocker - so they are hidden unless this is on."
                + " Everything from Metadata/Terrain/Doodads/Tools/ counts, which is a rule on the folder's name.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("as laid##roomlaid", ref _asLaid);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("With a place picked below, the room as the area laid it there: the tiles the game actually put down -"
                + " the joins to the map included - each turned the way the game laid it, and the room's doodads set on them.\n"
                + "Off, or with no place picked: the room's doodads as its file has them.");
        }

        if (_asLaid)
        {
            ImGui.SameLine();
            ImGui.Checkbox("tile level##roomlevel", ref _atLevel);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Each piece set at its tiles' own level - the height the area's ground is measured from - rather than"
                    + " raised until its ground meets the area's on average.\n"
                    + "Which of the two the game does is not settled: compare both with the game, and see the \"level:\" line under the picture.");
            }
        }
    }

    /// <summary>The key a room loads under: laid where picked, at the tiles' levels where asked.</summary>
    private string LaidKey(string chosen)
    {
        string laid = LaidPlace();
        return new RoomKey(chosen, _doodads, _tools, laid, AtLevel: laid.Length > 0 && _atLevel).ToString();
    }

    /// <summary>The place the room is drawn laid at, as the key writes it - or empty for the room as its file has it.</summary>
    private string LaidPlace()
    {
        if (!_asLaid || _searchedIn is not { } grid || _found is not { } found)
        {
            return string.Empty;
        }

        RoomCandidate? picked = _ghosted >= 0 && _ghosted < found.Candidates.Count
            ? found.Candidates[_ghosted]
            : _ghosted == GhostAround ? _around?.Where : null;
        return picked is { } place ? RoomKey.LaidAt(place.X, place.Y, place.Turn, Stamp(grid)) : string.Empty;
    }

    /// <summary>
    /// Where in the current area a room it loaded was laid: found by its tiles and ground, listed, and outlined on the large map when picked.
    /// </summary>
    /// <remarks>
    /// OFF THE FRAME, once per room, area and choice of ground: the search tries the room eight ways
    /// round at every tile corner of the area, which is cheap per try and not per frame. The list is
    /// best first, with what each place agrees on - the map is where a person tells them apart. See
    /// RoomFinder.
    /// </remarks>
    private void Whereabouts(string room)
    {
        TerrainGrid? grid = Terrain?.Invoke();
        if (grid?.Ground is not { } ground || Read is not { } read)
        {
            ImGui.TextDisabled("where it lies: the area's ground types are not read");
            return;
        }

        if (!ReferenceEquals(grid, _identitiesOf))
        {
            _identitiesOf = grid;
            _identities = new ConcurrentDictionary<string, TileIdentity?>(StringComparer.OrdinalIgnoreCase);
        }

        if (ImGui.Checkbox("anywhere##roomanywhere", ref _anywhere))
        {
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off, the usual: only places whose footprint covers ground somebody can stand on are listed -"
                + " the void past the map's edge fits a room's rim hundreds of times over.\n"
                + "On: every place, for a room of pure scenery that covers no walkable ground.");
        }

        ImGui.SameLine();

        if (!ReferenceEquals(grid, _searchedIn) || !string.Equals(room, _searchedFor, StringComparison.Ordinal) || _anywhere != _searchedAnywhere)
        {
            _searchedIn = grid;
            _searchedFor = room;
            _searchedAnywhere = _anywhere;
            _found = null;
            _ghosted = -1;
            _around = null;
            _probedIn = null;
            _probedAt = (-1, -1);
            Ghost?.Invoke(null);
            int wide = grid.TilesX;
            int tall = grid.TilesY;
            TerrainTiles? laid = grid.Tiles;

            // EACH TILE FILE ONCE PER AREA, its inheritance followed, whichever room's search reads it first.
            ConcurrentDictionary<string, TileIdentity?> known = _identities;
            TileIdentity? Identity(string path) => known.GetOrAdd(path, one => TileIdentity.Of(TileModels.Defined(read, one).Definition));
            bool anywhere = _anywhere;
            _searching = Task.Run(() => RoomFinder.Find(
                RoomLayout.Read(read(room)), ground, wide, tall, laid, Identity, walkable: grid.WalkableTileMask(), anywhere: anywhere));
        }

        if (_found is null && _searching is { IsCompleted: true } done)
        {
            _found = done.IsCompletedSuccessfully ? done.Result : RoomSearch.Not($"the search failed: {done.Exception?.GetBaseException().Message}");
            _rows = new string[_found.Candidates.Count];
            for (var one = 0; one < _rows.Length; one++)
            {
                RoomMisses? misses = one < _found.Misses.Count ? _found.Misses[one] : null;
                _rows[one] = Said(_found.Candidates[one], _found.TileChecked, misses) + string.Create(CultureInfo.InvariantCulture, $"##where{one}");
            }
        }

        if (_found is not { } found)
        {
            ImGui.TextDisabled("where it lies: looking for it in this area...");
            return;
        }

        if (found.Why.Length > 0)
        {
            ImGui.TextDisabled(ImGuiText.Escape("where it lies: " + found.Why));
            return;
        }

        string standing = found.Standing ? " over walkable ground" : string.Empty;

        // THE BEST SHARE SAID OUTRIGHT where nothing fits: a "nearest" agreeing on under half its
        // corners is no place at all, and the outline would otherwise read as a claim.
        int best = found.Candidates.Count > 0 ? Share(found.Candidates[0]) : 0;
        ImGui.TextDisabled(ImGuiText.Escape(found.TileChecked
            ? string.Create(CultureInfo.InvariantCulture,
                $"where it lies: places{standing} ranked by the tiles laid, then the corners - {found.Fits} fit all {found.Corners} corners; pick one to outline it on the large map")
            : found.Found
                ? string.Create(CultureInfo.InvariantCulture,
                    $"where it lies: {found.Fits} place{(found.Fits == 1 ? string.Empty : "s")}{standing} fit all {found.Corners} corners - pick one to outline it on the large map")
                : string.Create(CultureInfo.InvariantCulture,
                    $"where it lies: nowhere{standing} fits all {found.Corners} corners; the nearest agrees on {best}% - pick one to see where it parts:")));

        // WHAT THE ROOM'S FILE GAVE THE SEARCH, on a line of its own and wrapped: at the end of the
        // line above it was cut off by the pane's edge, the overrides with it.
        var facts = new List<string>(3);
        if (found.Free > 0)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"{found.Free} corners the room leaves unnamed are free"));
        }

        if (found.Left > 0)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"{found.Left} slots bigger than a tile left out of the ground"));
        }

        facts.Add(found.Overrides
            ? string.Create(CultureInfo.InvariantCulture, $"{found.Overridden} inner corners named by the file's ground overrides")
            : found.OverridesWhy.Length > 0
                ? "the file's tail did not read: " + found.OverridesWhy
                : "no ground overrides in the file");
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(ImGuiText.Escape("the room's file: " + string.Join("  ·  ", facts)));
        ImGui.PopTextWrapPos();

        Around(grid, room, found);

        float rows = Math.Min(found.Candidates.Count, 6);
        if (rows == 0)
        {
            Parting(found);
            return;
        }

        if (ImGui.BeginChild("##roomwhere", new Vector2(0f, (rows * ImGui.GetTextLineHeightWithSpacing()) + ImGui.GetStyle().FramePadding.Y), ImGuiChildFlags.Borders))
        {
            for (var one = 0; one < found.Candidates.Count; one++)
            {
                RoomCandidate where = found.Candidates[one];
                if (ImGui.Selectable(one < _rows.Length ? _rows[one] : "##where", _ghosted == one))
                {
                    _ghosted = _ghosted == one ? -1 : one;
                    RoomMisses misses = one < found.Misses.Count ? found.Misses[one] : RoomMisses.None;
                    Ghost?.Invoke(_ghosted >= 0 ? new RoomGhost(grid, room, where, misses) : null);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(RowSaid);
                }
            }

            if (found.More > 0)
            {
                ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"{found.More} more that fit all corners, not listed"));
            }
        }

        ImGui.EndChild();
        Parting(found);
    }

    /// <summary>
    /// Every place the outlined candidate parts with the area, one line each, with a button to copy them.
    /// </summary>
    /// <remarks>
    /// THE DATA FOR WHAT IS NOT DECODED YET. Each tile names what the room asks for and what was laid,
    /// whether it can be walked on, the room sides its slot lies on and that slot's edge and exit
    /// numbers as the file writes them - poe_data_tools names the exit pairs and does not say what
    /// their values mean, and a join's tiles beside a slot's numbers is how to find out. Worked out
    /// once per pick, not per frame.
    /// </remarks>
    private void Parting(RoomSearch found)
    {
        RoomCandidate? picked = _ghosted >= 0 && _ghosted < found.Candidates.Count
            ? found.Candidates[_ghosted]
            : _ghosted == GhostAround ? _around?.Where : null;
        if (picked is not { } candidate)
        {
            return;
        }

        if (!ReferenceEquals(found, _partsOf) || _partsFor != candidate)
        {
            _partsOf = found;
            _partsFor = candidate;
            IReadOnlyList<RoomPart> parts = found.Parts(candidate);
            int elsewhere = parts.Count(one => one.Join == RoomJoin.None);
            _partLines = new string[parts.Count];
            for (var one = 0; one < parts.Count; one++)
            {
                _partLines[one] = PartSaid(parts[one]);
            }

            _partsHeader = string.Create(CultureInfo.InvariantCulture,
                $"where tile {candidate.X}, {candidate.Y} ({RoomFinder.Said(candidate.Turn)}) parts with the area: {parts.Count} misses, {elsewhere} of them at no join##roomparts");
        }

        if (!ImGui.CollapsingHeader(_partsHeader))
        {
            return;
        }

        if (ImGui.SmallButton("copy##roompartscopy"))
        {
            ImGui.SetClipboardText(string.Join('\n', _partLines));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copies every line below, to paste where the room's joins are being worked out.");
        }

        float rows = Math.Clamp(_partLines.Length, 1, 8);
        if (ImGui.BeginChild("##roomparts", new Vector2(0f, (rows * ImGui.GetTextLineHeightWithSpacing()) + ImGui.GetStyle().FramePadding.Y), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            foreach (string line in _partLines)
            {
                ImGui.TextUnformatted(line);
            }
        }

        ImGui.EndChild();
    }

    /// <summary>One miss in a line's words.</summary>
    private static string PartSaid(RoomPart part)
    {
        string join = part.Join switch
        {
            RoomJoin.Opening => "opening",
            RoomJoin.Cap => "cap",
            RoomJoin.Corner => "join corner",
            _ => "elsewhere",
        };
        string sides = part.Sides.Length > 0 ? " (" + part.Sides + ")" : string.Empty;
        if (part.IsCorner)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"corner {part.X}, {part.Y}  ·  {join}  ·  room corner {part.RoomU}, {part.RoomV}{sides}  ·  wants {part.Wanted}  ·  laid {part.Laid}");
        }

        var exits = new System.Text.StringBuilder();
        for (var side = 0; side < part.SideEdges.Count && side < 4; side++)
        {
            string edge = part.SideEdges[side].Length > 0 ? Path.GetFileNameWithoutExtension(part.SideEdges[side]) : "-";
            string pair = (2 * side) + 1 < part.SideExits.Count
                ? string.Create(CultureInfo.InvariantCulture, $"{part.SideExits[2 * side]}/{part.SideExits[(2 * side) + 1]}")
                : "?";
            exits.Append(CultureInfo.InvariantCulture, $" {"DRUL"[side]}:{edge} {pair}");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"tile {part.X}, {part.Y}  ·  {join}  ·  slot {part.RoomU}, {part.RoomV}{sides}  ·  {(part.Walkable ? "walkable" : "not walkable")}  ·  wants {part.Wanted}  ·  laid {part.Laid}  ·  slot sides{exits}");
    }

    /// <summary>What a candidate row's figures mean.</summary>
    private const string RowSaid = "Corners: the room's ground types against the ground laid at every tile corner.\n"
        + "Tiles: each of the room's slots against the tile laid where it falls - size, tag, edge and ground types, whichever way round.\n"
        + "Big: the slots bigger than one tile, which the ground leaves out and which make a room this room.\n"
        + "Places are ranked by their tiles, then their corners: where a room lies, the area lays its own tiles along the sides it joins the map by.\n"
        + "On the map, red dots are corners that disagree and orange rings tiles that do; cyan marks the ones at a join -"
        + " a ring for a rim tile turned to walkable ground with walkable ground beyond it, a ring with a dot for a miss beside it,"
        + " and a dot for its corners.\n"
        + "Joins: how many such runs; beside them: of the corners and tiles no join explains, how many agree.";

    /// <summary>A candidate in a row's words, with how its misses fall where they were sorted into joins.</summary>
    private static string Said(RoomCandidate where, bool tileChecked, RoomMisses? misses)
    {
        string tiles = !tileChecked
            ? string.Empty
            : where.Big > 0
                ? string.Create(CultureInfo.InvariantCulture, $"  ·  tiles {where.TilesAgree}/{where.Tiles}, big {where.BigAgree}/{where.Big}")
                : string.Create(CultureInfo.InvariantCulture, $"  ·  tiles {where.TilesAgree}/{where.Tiles}");
        string joins = misses is { Classified: true } sorted
            ? string.Create(CultureInfo.InvariantCulture, $"  ·  joins {sorted.Joins}  ·  {Beside(where, sorted)}% beside them")
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"tile {where.X}, {where.Y}  ·  {RoomFinder.Said(where.Turn)}  ·  {where.Matched}/{where.Corners} corners ({Share(where)}%){tiles}{joins}");
    }

    /// <summary>
    /// The "around you" row: the room laid its best way over the tile the player stands on, whether or not that made the list.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES OF THE COMPARISON. Standing in a room, a person knows where it is; this says how the
    /// room's own corners and slots meet what the area laid there, so a list that ranks somewhere else
    /// first shows by how much and where - pick the row and the map marks it. Worked out off the frame
    /// whenever the player reaches another tile, one probe at a time.
    /// </remarks>
    private void Around(TerrainGrid grid, string room, RoomSearch found)
    {
        if (Here is null)
        {
            return;
        }

        if (_probing is { Task.IsCompleted: true } done)
        {
            _probing = null;
            if (ReferenceEquals(done.For, found))
            {
                _around = done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
                _aroundRow = _around is { } scored ? "around you: " + Said(scored.Where, found.TileChecked, scored.Misses) + "##wherearound" : string.Empty;
                if (_ghosted == GhostAround)
                {
                    Ghost?.Invoke(_around is { } moved ? new RoomGhost(grid, room, moved.Where, moved.Misses) : null);
                }
            }
        }

        if (_probing is null && Here() is { } here && (here != _probedAt || !ReferenceEquals(found, _probedIn)))
        {
            _probedAt = here;
            _probedIn = found;
            _probing = (Task.Run(() => found.Around(here.X, here.Y)), found);
        }

        if (_around is not { } around)
        {
            return;
        }

        if (ImGui.Selectable(_aroundRow, _ghosted == GhostAround))
        {
            _ghosted = _ghosted == GhostAround ? -1 : GhostAround;
            Ghost?.Invoke(_ghosted == GhostAround ? new RoomGhost(grid, room, around.Where, around.Misses) : null);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The room laid its best way over the tile you stand on - most tiles agreeing, then most corners -"
                + " whether or not that made the list below. Standing in the room, this is where it is: what it scores here"
                + " against the list's first row says whether the search can tell it apart.\n" + RowSaid);
        }
    }

    /// <summary>
    /// How much of a candidate's corners and tiles agree that no join explains, as a whole percentage rounded down - 100 where every miss is a join's.
    /// </summary>
    private static int Beside(RoomCandidate where, RoomMisses misses)
    {
        int beside = where.Corners + where.Tiles - misses.Openings.Count - misses.Caps.Count - misses.JoinCorners.Count;
        return beside > 0 ? (beside - misses.Elsewhere) * 100 / beside : 100;
    }

    /// <summary>How much of a candidate's corners agree, as a whole percentage, rounded down so nothing short of all reads 100.</summary>
    private static int Share(RoomCandidate where) => where.Corners > 0 ? where.Matched * 100 / where.Corners : 0;

    /// <summary>The ways the current area laid a tile, one bit per placement - zero where it did not, or the tables were not read.</summary>
    private byte LaidHere(string tile) => Laid?.Invoke().GetValueOrDefault(tile) ?? 0;

    /// <summary>The chosen placement where the area laid this tile that way, or -1 - see <see cref="_laid"/>.</summary>
    private int Laying(string tile) => _laid >= 0 && (LaidHere(tile) & (1 << _laid)) != 0 ? _laid : -1;

    /// <summary>
    /// The "laid as" choice: the tile as its file holds it, or turned the way the current area laid it.
    /// </summary>
    /// <remarks>
    /// ONLY THE WAYS THIS AREA LAID IT, read off each tile's RotationSelector through the engine's own
    /// tables - the decode the heights already read the ground through, so a turn offered here is one
    /// the game made, not one this could make. A ROOM HAS NO SUCH CHOICE: no tile names its room, so
    /// nothing in memory says where or how a room was laid - see RoomFiles.
    /// </remarks>
    private void LaidAs(string tile)
    {
        if (Laid is null)
        {
            return;
        }

        ImGui.TextDisabled("laid as");
        ImGui.SameLine();
        byte laid = LaidHere(tile);
        if (laid == 0)
        {
            int row = Page.Row(tile);
            ImGui.TextDisabled(row >= 0 && Page.Placed[row] > 0
                ? "the file's own orientation - how this area laid it was not read (the rotation tables were not found)"
                : "the file's own orientation - not placed in this area");
            return;
        }

        const string Own = "as in the file";
        int current = Laying(tile);
        ImGui.SetNextItemWidth(Math.Min(ImGui.GetContentRegionAvail().X, 260f));
        if (ImGui.BeginCombo("##laidas", current >= 0 ? "as here: " + TileOrientation.OfPlacement(current) : Own))
        {
            if (ImGui.Selectable(Own, current < 0))
            {
                _laid = -1;
            }

            for (int placement = 0; placement < 8; placement++)
            {
                if ((laid & (1 << placement)) == 0)
                {
                    continue;
                }

                string name = "as here: " + TileOrientation.OfPlacement(placement);
                if (ImGui.Selectable(name + "##laid" + placement.ToString(CultureInfo.InvariantCulture), placement == current))
                {
                    _laid = placement;
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("How this area laid the tile down, from each placed tile's RotationSelector through the engine's own tables -"
                + " the same decode the map's ground heights are read through. Turns are counter-clockwise in the file's own X and Y,"
                + " a mirror flips X first. Rooms have no such choice: nothing in memory says how a room was laid.");
        }

        int ways = System.Numerics.BitOperations.PopCount(laid);
        ImGui.SameLine();
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"laid {ways} way{(ways == 1 ? string.Empty : "s")} here"));
    }

    /// <summary>
    /// The "drawn as" choice: the tile's own materials, or those of a tileset that places it.
    /// </summary>
    /// <remarks>
    /// ONLY TILESETS THAT PLACE THIS TILE, by its whole path - see TilesetIndex - because a tileset's
    /// overrides name the materials of the tiles it places, and offering one that never places the
    /// tile would offer a choice that changes nothing.
    /// </remarks>
    private void DrawnAs(string tile)
    {
        if (Tilesets is not { } catalog)
        {
            return;
        }

        ImGui.TextDisabled("drawn as");
        ImGui.SameLine();
        if (catalog.Ready is not { } index)
        {
            ImGui.TextDisabled(catalog.Reading ? "reading every tileset..." : "no tilesets yet - the install's list is read shortly after start-up");
            return;
        }

        IReadOnlyList<string> placing = index.Of(tile);
        if (placing.Count == 0)
        {
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
                $"the tile's own materials - none of {index.Searched} tilesets places it"));
            return;
        }

        const string Own = "the tile's own materials";
        string current = Placing(tile);
        ImGui.SetNextItemWidth(Math.Min(ImGui.GetContentRegionAvail().X, 360f));
        if (ImGui.BeginCombo("##drawnas", current.Length > 0 ? TilesetIndex.Short(current) : Own))
        {
            if (ImGui.Selectable(Own, current.Length == 0))
            {
                _tileset = string.Empty;
            }

            foreach (string one in placing)
            {
                if (ImGui.Selectable(TilesetIndex.Short(one) + "##" + one, string.Equals(one, current, StringComparison.OrdinalIgnoreCase)))
                {
                    _tileset = one;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(ImGuiText.Escape(one));
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The tilesets that place this tile. Each swaps some of the tile's materials for its own"
                + " (its TileMaterialOverrides) - the desert tilesets swap every cliff and ledge."
                + " The ground stays plain: which texture a tileset lays on it is listed in the dump, not yet drawn.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"{placing.Count} of {index.Searched} tilesets place it"));
    }
}
