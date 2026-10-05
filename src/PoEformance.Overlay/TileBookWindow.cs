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

    /// <summary>What separates a room's path from its unit, or a tile's from <see cref="Bare"/>, in the key the portrait loads by.</summary>
    private const char UnitMark = '|';

    /// <summary>What a tile's key ends in when its ground is left out.</summary>
    private const string Bare = "bare";

    /// <summary>The colour the unpainted ground is drawn in: a dark, flat grey that stays behind the props.</summary>
    public static readonly Vector3 GroundInk = new(0.34f, 0.34f, 0.35f);

    private readonly Func<IReadOnlyList<string>> _install;
    private readonly Func<IReadOnlyDictionary<string, int>> _placed;

    /// <summary>What a room's doodad positions are read as. See RoomModels - the file does not say.</summary>
    private RoomUnit _unit = RoomUnit.Cells;

    /// <summary>Whether a tile is drawn with its ground block. See TileModels.Of.</summary>
    private bool _ground = true;

    /// <summary>The two lists the book was built from, compared by reference to notice a new one.</summary>
    private IReadOnlyList<string>? _installed;
    private IReadOnlyDictionary<string, int>? _placedOf;
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

    /// <inheritdoc/>
    protected override string Caption => "Search for any terrain tile or room";

    /// <inheritdoc/>
    protected override string Grammar => "arena  ·  kind:room  ·  set:woods  ·  here:yes  ·  folder:areatransitions";

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
        if (!ReferenceEquals(_installed, installed) || !ReferenceEquals(_placedOf, placings))
        {
            _installed = installed;
            _placedOf = placings;
            _page = TileBook.Of(installed, placings);
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
    }

    /// <summary>
    /// What the portrait loads for a key: a tile's geometry, or a room's doodads in the chosen unit.
    /// </summary>
    /// <remarks>
    /// THE UNIT RIDES IN THE KEY, so switching it is a different key and the portrait reloads -
    /// the same way the item book's drop or held choice does - without the portrait knowing about
    /// rooms. A tile's ground rides the same way: <see cref="Bare"/> after the mark leaves it out.
    /// </remarks>
    public static MonsterModel Load(Func<string, byte[]?> read, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int mark = key.LastIndexOf(UnitMark);
        if (mark < 0)
        {
            return TileModels.Of(read, key);
        }

        if (key.AsSpan(mark + 1).Equals(Bare, StringComparison.Ordinal))
        {
            return TileModels.Of(read, key[..mark], ground: false);
        }

        RoomUnit unit = Enum.TryParse(key[(mark + 1)..], out RoomUnit said) ? said : RoomUnit.Cells;
        return RoomModels.Of(read, key[..mark], unit);
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

        if (room)
        {
            // THE FILE DOES NOT SAY WHAT ITS POSITIONS COUNT, so the reading is a choice in plain
            // sight - and the line under the picture says which one the doodads' reach supports.
            ImGui.TextDisabled("Doodad positions in");
            ImGui.SameLine();
            if (ImGui.RadioButton("cells (23 per tile)", _unit == RoomUnit.Cells))
            {
                _unit = RoomUnit.Cells;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("world units", _unit == RoomUnit.World))
            {
                _unit = RoomUnit.World;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Neither reference says which unit a room's doodad positions are in."
                    + " The line under the picture says whether any doodad lies past the room's edge when read as cells.");
            }
        }
        else
        {
            ImGui.Checkbox("ground", ref _ground);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The tile's ground block, drawn plain - nothing says which ground texture an area lays on it."
                    + " Off shows the props alone, where the ground rises around them.");
            }
        }

        ImGui.Separator();

        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        string key = room ? chosen + UnitMark + _unit.ToString() : _ground ? chosen : chosen + UnitMark + Bare;
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: name);
        }

        Vector2 avail = ImGui.GetContentRegionAvail();
        model.Draw(_subject, _subjectKey, avail.X, avail.Y);
    }
}
