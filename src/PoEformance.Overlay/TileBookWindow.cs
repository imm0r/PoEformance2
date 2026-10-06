using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;

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

    /// <summary>The lists the book was built from, compared by reference to notice a new one.</summary>
    private IReadOnlyList<string>? _installed;
    private IReadOnlyDictionary<string, int>? _placedOf;
    private IReadOnlyDictionary<string, string>? _needsOf;
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

    /// <inheritdoc/>
    protected override string Caption => "Search for any terrain tile or room";

    /// <inheritdoc/>
    protected override string Grammar => "arena  ·  kind:room  ·  set:woods  ·  here:yes  ·  needs:InputVertexColor  ·  folder:areatransitions";

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
        IReadOnlyDictionary<string, string>? needs = Needs?.Ready(placings, _tileset);
        if (!ReferenceEquals(_installed, installed) || !ReferenceEquals(_placedOf, placings) || !ReferenceEquals(_needsOf, needs))
        {
            _installed = installed;
            _placedOf = placings;
            _needsOf = needs;
            _page = TileBook.Of(installed, placings, needs);
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
    /// What the portrait loads for a key: a tile's geometry, or a room's doodads in the chosen unit.
    /// </summary>
    /// <remarks>
    /// THE UNIT RIDES IN THE KEY, so switching it is a different key and the portrait reloads -
    /// the same way the item book's drop or held choice does - without the portrait knowing about
    /// rooms. A tile's choices ride the same way - see <see cref="TileKey"/> - and the load reads the
    /// named tileset's overrides itself, so the key is the whole of what is drawn and two tilesets
    /// are two keys.
    /// </remarks>
    public static MonsterModel Load(Func<string, byte[]?> read, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!TileBook.IsRoom(key))
        {
            TileKey tile = TileKey.Read(key);
            return TileModels.Of(
                read,
                tile.Path,
                ground: tile.Ground,
                walls: tile.Walls,
                shaded: true,
                swaps: tile.Tileset.Length > 0 ? TilesetIndex.Overrides(read, tile.Tileset) : null,
                tileset: tile.Tileset.Length > 0 ? TilesetIndex.Short(tile.Tileset) : string.Empty);
        }

        return RoomModels.Of(read, key, shaded: true);
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
        }

        ImGui.Separator();

        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        string key = room ? chosen : new TileKey(chosen, _ground, _walls, Placing(chosen)).ToString();
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
