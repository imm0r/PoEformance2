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
/// THE ITEM BOOK'S PARTS, with a tile in the pane instead of an item: the grid, the query grammar,
/// the facet rail, the column chooser and the splits behave as they do there, and the picture is a
/// <see cref="MonsterPortrait"/> of its own whose load is <see cref="Load"/> rather than the .ao
/// walk: <see cref="TileModels.Of"/> for a tile, <see cref="RoomModels.Of"/> for a room.
///
/// "ONLY THIS AREA" IS A TERM IN THE QUERY - <c>here:yes</c> - written and read back by the
/// checkbox above the list, so the rail, the count and the text all agree on what is shown.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TileBookWindow(Func<IReadOnlyList<string>> install, Func<IReadOnlyDictionary<string, int>> placed)
{
    /// <summary>How long a query may be. The monster book's limit, for the same reason.</summary>
    private const uint QueryLength = 256;

    /// <summary>How many of a field's values the rail offers. The monster book's dozen.</summary>
    private const int MostFacets = 12;

    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// THE SET - Woods, Maps, Act4 - is the handful of answers a click is for; names and folders
    /// have thousands and are typed. "Here" is the same as the checkbox, counted.
    /// </remarks>
    private static readonly (string Label, string Field)[] Rails =
    [
        ("Kind", "kind"),
        ("Sets", "set"),
        ("This area", "here"),
    ];

    /// <summary>What each boundary is known by - to ImGui, and in the settings file.</summary>
    private const string RailPane = "rail";
    private const string ListPane = "list";

    /// <summary>What the box above the book is.</summary>
    private const string Caption = "Search for any terrain tile or room";

    /// <summary>What the box takes, said on hover.</summary>
    private const string Grammar = "arena  ·  kind:room  ·  set:woods  ·  here:yes  ·  folder:areatransitions";

    /// <summary>The box's background while the query does not read. The monster book's.</summary>
    private static readonly Vector4 Wrong = new(0.32f, 0.11f, 0.11f, 1f);

    /// <summary>The query term the "only this area" checkbox writes.</summary>
    private const string HereField = "here";

    /// <summary>What separates a room's path from its unit in the key the portrait loads by.</summary>
    private const char UnitMark = '|';

    /// <summary>What a room's doodad positions are read as. See RoomModels - the file does not say.</summary>
    private RoomUnit _unit = RoomUnit.Cells;

    private readonly PaneSplit _rail = new(0.18f, RailPane);
    private readonly PaneSplit _split = new(0.45f, ListPane);
    private readonly DataGrid _grid = new();

    /// <summary>What each of the rail's fields holds within the rows that are left.</summary>
    private readonly Dictionary<string, List<Facet>> _facets = new(StringComparer.Ordinal);

    private readonly List<int> _shown = [];
    private readonly List<int> _columns = [];
    private readonly List<ColumnRange> _ranges = [];

    /// <summary>The two lists the book was built from, compared by reference to notice a new one.</summary>
    private IReadOnlyList<string>? _installed;
    private IReadOnlyDictionary<string, int>? _placedOf;
    private TileBook _page = TileBook.Empty;

    /// <summary>Which columns are on, one per column of the store.</summary>
    private bool[] _visible = [];

    /// <summary>The columns somebody chose, by name, or null for the book's own starting set.</summary>
    private IReadOnlyList<string>? _wanted;

    private bool _railOpen = true;

    private string _query = string.Empty;
    private QueryTerm? _term;
    private string _error = string.Empty;
    private bool _refilter = true;
    private RowSet? _matched;

    private string _chosen = string.Empty;
    private int _chosenRow = -1;

    /// <summary>What the portrait is handed - a name to show and the tile's path as the key - made once per choice.</summary>
    private MonsterVariety? _subject;
    private string _subjectKey = string.Empty;

    private DataGridHooks? _hooks;

    /// <summary>Draws the tile, or null where the overlay did not wire one up. Its Load is set by the overlay.</summary>
    public MonsterPortrait? Model { get; set; }

    /// <summary>Called when anything the settings file keeps has moved.</summary>
    public Action? Changed { get; set; }

    /// <summary>The columns that are on, by name, in the store's order - what the settings keep.</summary>
    public IReadOnlyList<string> Columns => [.. _columns.Select(one => _page.Store.Columns[one].Name)];

    /// <summary>How wide each dragged column is, by name.</summary>
    public IReadOnlyDictionary<string, int> ColumnWidths => _grid.Widths;

    /// <summary>Where the two boundaries sit.</summary>
    public IReadOnlyDictionary<string, double> Panes => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [RailPane] = _rail.Share,
        [ListPane] = _split.Share,
    };

    /// <summary>Whether the facet rail has a pane of its own.</summary>
    public bool RailOpen => _railOpen;

    /// <summary>Takes what the settings file said about the layout, and wires the writes back.</summary>
    public void Show(
        IReadOnlyList<string>? columns,
        bool? rail = null,
        IReadOnlyDictionary<string, int>? widths = null,
        IReadOnlyDictionary<string, double>? panes = null)
    {
        _wanted = columns is { Count: > 0 } ? columns : null;
        _railOpen = rail ?? _railOpen;
        _grid.Restore(widths);

        if (panes is not null)
        {
            if (panes.TryGetValue(RailPane, out double railShare))
            {
                _rail.Restore(railShare);
            }

            if (panes.TryGetValue(ListPane, out double listShare))
            {
                _split.Restore(listShare);
            }
        }

        _grid.Settled = Moved;
        _rail.Settled = Moved;
        _split.Settled = Moved;

        Layout();
    }

    private void Moved() => Changed?.Invoke();

    /// <summary>The tab's contents, in the monospaced face the monster book is drawn in.</summary>
    public void DrawTab()
    {
        OverlayFonts.PushMono();
        try
        {
            Book();
        }
        finally
        {
            OverlayFonts.PopMono();
        }
    }

    private void Book()
    {
        Read(install(), placed());

        if (_page.Count == 0)
        {
            ImGui.TextDisabled("No tiles yet - the install's list is read shortly after start-up, and an area's tiles once one is loaded.");
            return;
        }

        Header();
        Filter();

        float tall = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);

        if (_railOpen)
        {
            float rail = _rail.Left();
            if (ImGui.BeginChild("##tile-rail", new Vector2(rail, tall), ImGuiChildFlags.Borders))
            {
                Rail();
            }

            ImGui.EndChild();

            _rail.Bar(tall);
        }

        float left = _split.Left();
        if (ImGui.BeginChild("##tile-list", new Vector2(left, tall), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar(tall);

        if (ImGui.BeginChild("##tile-model", new Vector2(0f, tall), ImGuiChildFlags.Borders))
        {
            Pane();
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Works the two lists into columns, once per pair.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE: the install's list arrives once from a background walk, and the area's
    /// placements are a new dictionary whenever the area changes - and only then. The selection
    /// follows the path into the new book.
    /// </remarks>
    private void Read(IReadOnlyList<string> installed, IReadOnlyDictionary<string, int> placings)
    {
        if (ReferenceEquals(_installed, installed) && ReferenceEquals(_placedOf, placings))
        {
            return;
        }

        _installed = installed;
        _placedOf = placings;
        _page = TileBook.Of(installed, placings);
        _chosenRow = _page.Row(_chosen);
        _ranges.Clear();
        Layout();
    }

    /// <summary>Which columns are on: what was chosen by name, or the book's own starting set.</summary>
    private void Layout()
    {
        DataColumn[] all = _page.Store.Columns;
        _visible = new bool[all.Length];

        for (var at = 0; at < all.Length; at++)
        {
            _visible[at] = _wanted is null ? _page.Shown[at] : _wanted.Contains(all[at].Name);
        }

        // THE FIRST COLUMN IS NOT OPTIONAL: it carries the row's selectable.
        if (all.Length > 0)
        {
            _visible[0] = true;
        }

        Rebuild();
        _refilter = true;
        Filter();
    }

    private void Rebuild()
    {
        _columns.Clear();
        for (var at = 0; at < _visible.Length; at++)
        {
            if (_visible[at])
            {
                _columns.Add(at);
            }
        }

        _grid.Resort();
    }

    private void Header()
    {
        ImGui.TextDisabled(Caption);

        bool wrong = _error.Length > 0;
        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search("###tile-find", string.Empty, ref _query, QueryLength))
        {
            _refilter = true;
        }

        bool typing = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        if (wrong)
        {
            ImGui.PopStyleColor();
        }

        if (!typing && hovered)
        {
            ImGui.SetTooltip(wrong ? _error : Grammar);
        }

        if (ImGui.Button("Facets"))
        {
            _railOpen = !_railOpen;
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                _railOpen
                    ? "Fold the facet rail away. Nothing is lost - what it clicked is in the query."
                    : "Show the facet rail: what the rows that are left are made of.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Columns"))
        {
            ImGui.OpenPopup("##tile-columns");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Which of the {_page.Store.Columns.Length} columns the table shows."
                + " Drag across a column's histogram to filter by it; right-click one to undo that.");
        }

        Chooser();

        // READ BACK OUT OF THE QUERY, so editing the text by hand moves the tick.
        ImGui.SameLine();
        bool only = ColumnQuery.Holds(_term, HereField, TileBook.Here);
        if (ImGui.Checkbox("Only this area", ref only))
        {
            _query = ColumnQuery.Toggle(_query, HereField, TileBook.Here);
            _refilter = true;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(
            $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{_page.Count.ToString(CultureInfo.InvariantCulture)} tiles");

        if (wrong)
        {
            ImGui.TextColored(OverlayInk.Warn, ImGuiText.Escape(_error));
        }
    }

    private void Filter()
    {
        if (!_refilter)
        {
            return;
        }

        _refilter = false;

        QueryResult parsed = ColumnQuery.Parse(_query);
        _error = parsed.Error;

        // A BROKEN QUERY LEAVES THE LAST ANSWER ON SCREEN - every query is broken while it is
        // being typed, and a list that emptied at each of those keystrokes would flicker.
        if (_error.Length > 0)
        {
            return;
        }

        _term = parsed.Term;
        RowSet? rows = _page.Matching(_term, out string why);
        if (rows is null)
        {
            _error = why;
            return;
        }

        _matched = rows;
        rows.CopyTo(_shown);
        _grid.Resort();

        _ranges.Clear();
        DataColumn[] columns = _page.Store.Columns;
        for (var at = 0; at < columns.Length; at++)
        {
            if (ColumnQuery.RangeOf(_term, columns[at].Name) is { } range)
            {
                _ranges.Add(new ColumnRange(at, range.Least, range.Most));
            }
        }

        Counted();
    }

    /// <summary>
    /// Counts every value of every field the rail offers, against the rows that are left.
    /// </summary>
    /// <remarks>
    /// ONLY WHEN THE FILTER MOVES, never per frame, and into lists that are kept - a keystroke
    /// re-counts rather than re-allocates.
    /// </remarks>
    private void Counted()
    {
        if (_matched is null)
        {
            return;
        }

        foreach ((_, string field) in Rails)
        {
            if (!_facets.TryGetValue(field, out List<Facet>? into))
            {
                into = [];
                _facets[field] = into;
            }

            _page.Facets(_matched, field, into, MostFacets);
        }
    }

    /// <summary>
    /// The facet rail: what the rows that are left are made of, and a click to narrow them.
    /// </summary>
    /// <remarks>
    /// THE MONSTER BOOK'S RAIL. Nothing is stored here - a click writes the value into the query
    /// and the tick beside it is read back out of the query, so editing the text by hand moves the
    /// ticks. A zero is shown dim rather than hidden.
    /// </remarks>
    private void Rail()
    {
        if (_matched is null)
        {
            ImGui.TextDisabled("Nothing counted yet.");
            return;
        }

        foreach ((string label, string field) in Rails)
        {
            if (!_facets.TryGetValue(field, out List<Facet>? facets) || facets.Count == 0)
            {
                continue;
            }

            if (!OverlayLayout.Subsection(label, openByDefault: true))
            {
                continue;
            }

            ImGui.Indent();

            try
            {
                foreach (Facet facet in facets)
                {
                    Value(field, facet);
                }
            }
            finally
            {
                ImGui.Unindent();
            }
        }
    }

    private void Value(string field, Facet facet)
    {
        bool on = ColumnQuery.Holds(_term, field, facet.Value);
        string count = facet.Count.ToString(CultureInfo.InvariantCulture);

        float room = ImGui.GetContentRegionAvail().X;
        float wide = ImGui.CalcTextSize(count).X + ImGui.GetStyle().ItemSpacing.X;
        bool quiet = facet.Count == 0 && !on;

        // BY VALUE AND NOT BY POSITION: the rail is re-ordered on every keystroke.
        ImGui.PushID(facet.Value);

        try
        {
            if (quiet)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.Quiet);
            }

            if (ImGui.Selectable(
                    facet.Value,
                    on,
                    ImGuiSelectableFlags.None,
                    new Vector2(MathF.Max(1f, room - wide), 0f)))
            {
                _query = ColumnQuery.Toggle(_query, field, facet.Value);
                _refilter = true;
            }

            if (quiet)
            {
                ImGui.PopStyleColor();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(count);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>Which columns show, grouped the way somebody would look for them.</summary>
    private void Chooser()
    {
        if (!ImGui.BeginPopup("##tile-columns"))
        {
            return;
        }

        try
        {
            DataColumn[] all = _page.Store.Columns;
            string group = string.Empty;

            for (var at = 0; at < all.Length; at++)
            {
                // The book lays its columns out group by group, so a heading is wherever it changes.
                if (!string.Equals(_page.Groups[at], group, StringComparison.Ordinal))
                {
                    if (group.Length > 0)
                    {
                        ImGui.Separator();
                    }

                    group = _page.Groups[at];
                    ImGui.TextDisabled(group);
                }

                // THE FIRST COLUMN IS SHOWN AS FIXED rather than as a checkbox that refuses to clear.
                if (at == 0)
                {
                    ImGui.TextDisabled($"{all[at].Name} (always)");
                    continue;
                }

                bool on = _visible[at];
                if (ImGui.Checkbox(all[at].Name, ref on))
                {
                    _visible[at] = on;
                    Rebuild();
                    _wanted = Columns;
                    Changed?.Invoke();
                }
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>Takes a range a drag picked out of a histogram, by writing it into the query.</summary>
    private void Range(int column, double least, double most)
    {
        if (column < 0 || column >= _page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Ranged(_query, _page.Store.Columns[column].Name, least, most);
        if (!string.Equals(made, _query, StringComparison.Ordinal))
        {
            _query = made;
            _refilter = true;
        }
    }

    private void Clear(int column)
    {
        if (column < 0 || column >= _page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Drop(_query, _page.Store.Columns[column].Name);
        if (!string.Equals(made, _query, StringComparison.Ordinal))
        {
            _query = made;
            _refilter = true;
        }
    }

    /// <summary>The list. Hooks are kept in a field so no frame makes a delegate.</summary>
    private void Grid()
    {
        _hooks ??= new DataGridHooks(
            Ink: row => _page.HereRows[row] ? OverlayInk.Name : null,
            Hover: row => _page.Paths[row],
            Ranged: Range,
            Cleared: Clear);

        int chosen = _grid.Draw("##tiles", _page.Store, _columns, _shown, _chosenRow, _ranges, _hooks);
        if (chosen == _chosenRow)
        {
            return;
        }

        _chosenRow = chosen;
        _chosen = chosen >= 0 && chosen < _page.Paths.Length ? _page.Paths[chosen] : string.Empty;
    }

    /// <summary>
    /// What the portrait loads for a key: a tile's geometry, or a room's doodads in the chosen unit.
    /// </summary>
    /// <remarks>
    /// THE UNIT RIDES IN THE KEY, so switching it is a different key and the portrait reloads -
    /// the same way the item book's AOFile choice does - without the portrait knowing about rooms.
    /// </remarks>
    public static MonsterModel Load(Func<string, byte[]?> read, string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int mark = key.LastIndexOf(UnitMark);
        if (mark < 0)
        {
            return TileModels.Of(read, key);
        }

        RoomUnit unit = Enum.TryParse(key[(mark + 1)..], out RoomUnit said) ? said : RoomUnit.Cells;
        return RoomModels.Of(read, key[..mark], unit);
    }

    /// <summary>What the tile or room is and where it is used, then its geometry under it.</summary>
    private void Pane()
    {
        int row = _page.Row(_chosen);
        if (row < 0)
        {
            ImGui.TextDisabled("Choose a tile or a room on the left.");
            return;
        }

        bool room = TileBook.IsRoom(_chosen);
        (string set, string folder, string name) = TileBook.Split(_chosen);
        ImGui.TextUnformatted(name);
        ImGui.TextDisabled(ImGuiText.Escape(
            (room ? "room  ·  " : "tile  ·  ") + (folder.Length > 0 ? $"{set}  ·  {folder}" : set)));

        int placed = _page.Placed[row];
        ImGui.TextDisabled(room
            ? placed > 0 ? "loaded for this area" : "not loaded for this area"
            : placed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"placed {placed} times in this area")
                : "not placed in this area");

        ImGui.TextDisabled(ImGuiText.Escape(_chosen));

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

        ImGui.Separator();

        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        string key = room ? _chosen + UnitMark + _unit.ToString() : _chosen;
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: name);
        }

        Vector2 room_ = ImGui.GetContentRegionAvail();
        model.Draw(_subject, _subjectKey, room_.X, room_.Y);
    }
}
