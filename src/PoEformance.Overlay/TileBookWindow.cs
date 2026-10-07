using System.Globalization;
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

    /// <summary>The search running or run for <see cref="_searchedFor"/> in <see cref="_searchedIn"/>.</summary>
    private Task<RoomSearch>? _searching;
    private RoomSearch? _found;
    private string _searchedFor = string.Empty;
    private TerrainGrid? _searchedIn;
    private int _ghosted = -1;

    /// <summary>Whether a room places the level editor's tools, as the box has it - what the settings keep.</summary>
    public bool Tools
    {
        get => _tools;
        set => _tools = value;
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
    public static MonsterModel Load(Func<string, byte[]?> read, string key)
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
                laid: TileOrientation.OfPlacement(tile.Laid));
        }

        RoomKey room = RoomKey.Read(key);
        return RoomModels.Of(read, room.Path, shaded: true, doodads: room.Doodads, tools: room.Tools);
    }

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
            ? new RoomKey(chosen, _doodads, _tools).ToString()
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
    }

    /// <summary>
    /// Where in the current area a room it loaded was laid: found by its ground, listed, and outlined on the large map when picked.
    /// </summary>
    /// <remarks>
    /// OFF THE FRAME, once per room and area: the search tries the room eight ways round at every
    /// tile corner of the area, which is cheap per try and not per frame. A room found in several
    /// places lists them all - the map is where a person tells them apart - and one found nowhere
    /// exactly lists the nearest with how many corners they miss by. See RoomFinder.
    /// </remarks>
    private void Whereabouts(string room)
    {
        TerrainGrid? grid = Terrain?.Invoke();
        if (grid?.Ground is not { } ground || Read is not { } read)
        {
            ImGui.TextDisabled("where it lies: the area's ground types are not read");
            return;
        }

        if (!ReferenceEquals(grid, _searchedIn) || !string.Equals(room, _searchedFor, StringComparison.Ordinal))
        {
            _searchedIn = grid;
            _searchedFor = room;
            _found = null;
            _ghosted = -1;
            Ghost?.Invoke(null);
            int wide = grid.TilesX;
            int tall = grid.TilesY;
            _searching = Task.Run(() => RoomFinder.Find(RoomLayout.Read(read(room)), ground, wide, tall));
        }

        if (_found is null && _searching is { IsCompleted: true } done)
        {
            _found = done.IsCompletedSuccessfully ? done.Result : RoomSearch.Not($"the search failed: {done.Exception?.GetBaseException().Message}");
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

        string left = found.Left > 0
            ? string.Create(CultureInfo.InvariantCulture, $"; {found.Left} slots bigger than a tile left out")
            : string.Empty;
        ImGui.TextDisabled(ImGuiText.Escape(found.Found
            ? string.Create(CultureInfo.InvariantCulture,
                $"where it lies: {found.Candidates.Count + found.More} place{(found.Candidates.Count + found.More == 1 ? string.Empty : "s")} fit all {found.Corners} corners{left} - pick one to outline it on the large map")
            : string.Create(CultureInfo.InvariantCulture,
                $"where it lies: nowhere fits all {found.Corners} corners{left}; the nearest:")));

        float rows = Math.Min(found.Candidates.Count, 6);
        if (rows == 0)
        {
            return;
        }

        if (ImGui.BeginChild("##roomwhere", new Vector2(0f, (rows * ImGui.GetTextLineHeightWithSpacing()) + ImGui.GetStyle().FramePadding.Y), ImGuiChildFlags.Borders))
        {
            for (var one = 0; one < found.Candidates.Count; one++)
            {
                RoomCandidate where = found.Candidates[one];
                string label = string.Create(CultureInfo.InvariantCulture,
                    $"tile {where.X}, {where.Y}  ·  {RoomFinder.Said(where.Turn)}  ·  {where.Matched}/{where.Corners} corners##where{one}");
                if (ImGui.Selectable(label, _ghosted == one))
                {
                    _ghosted = _ghosted == one ? -1 : one;
                    Ghost?.Invoke(_ghosted >= 0 ? new RoomGhost(grid, room, where) : null);
                }
            }

            if (found.More > 0)
            {
                ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"{found.More} more not listed"));
            }
        }

        ImGui.EndChild();
    }

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
