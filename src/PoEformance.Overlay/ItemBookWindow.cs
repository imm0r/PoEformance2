using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// The item reference book: every item the game has a 3D model for, and the model itself.
/// </summary>
/// <remarks>
/// THE MONSTER BOOK'S PARTS. The grid, the query grammar, the facet rail, the column chooser and
/// the pane splits behave as they do there; the model is drawn by a <see cref="MonsterPortrait"/>
/// of its own, because an item's .ao is the same file format as a monster's and the portrait only
/// ever needed the .ao paths - see MonsterModels.OfFiles. The item is handed to it as a variety
/// carrying nothing but a name and that one path, which is everything the portrait reads off one.
///
/// THE LAYOUT IS WRITTEN DOWN like the monster book's - which columns, how wide, where the panes
/// split and whether the rail is open - under settings of its own, because the two tables have
/// different columns.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ItemBookWindow(Func<ItemVisuals> table)
{
    /// <summary>How long a query may be. The monster book's limit, for the same reason.</summary>
    private const uint QueryLength = 256;

    /// <summary>How many of a field's values the rail offers. The monster book's dozen.</summary>
    private const int MostFacets = 12;

    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// THE TWO WITH A HANDFUL OF ANSWERS. An item has one class and is base or unique, and those are
    /// the questions a click answers; names and file names have thousands of values and are typed.
    /// </remarks>
    private static readonly (string Label, string Field)[] Rails =
    [
        ("Classes", "class"),
        ("Kind", "kind"),
    ];

    /// <summary>What each boundary is known by - to ImGui, and in the settings file.</summary>
    private const string RailPane = "rail";
    private const string ListPane = "list";

    /// <summary>What the box above the book is.</summary>
    private const string Caption = "Search for any item with a model";

    /// <summary>What the box takes, said on hover.</summary>
    private const string Grammar = "sword  ·  class:bow drop>40  ·  kind:unique  ·  not unique";

    /// <summary>The box's background while the query does not read. The monster book's.</summary>
    private static readonly Vector4 Wrong = new(0.32f, 0.11f, 0.11f, 1f);

    /// <summary>A unique's row is in the game's own unique colour.</summary>
    private static readonly Vector4 UniqueInk = OverlayInk.Rarity(ItemRarity.Unique);

    private readonly PaneSplit _rail = new(0.18f, RailPane);
    private readonly PaneSplit _split = new(0.45f, ListPane);
    private readonly DataGrid _grid = new();

    /// <summary>What each of the rail's fields holds within the rows that are left.</summary>
    private readonly Dictionary<string, List<Facet>> _facets = new(StringComparer.Ordinal);

    private readonly List<int> _shown = [];
    private readonly List<int> _columns = [];
    private readonly List<ColumnRange> _ranges = [];

    private ItemVisuals _of = ItemVisuals.Empty;
    private ItemBook _page = ItemBook.Empty;

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

    /// <summary>Whether the pane shows AOFile2 rather than the first file. See ItemVisuals.</summary>
    private bool _second;

    /// <summary>
    /// What the portrait is handed, made once per choice rather than once per frame.
    /// </summary>
    /// <remarks>
    /// The portrait compares the KEY to decide whether to reload, and keeps the variety it was
    /// handed with it; a fresh one each frame would be an allocation per frame for nothing.
    /// </remarks>
    private MonsterVariety? _subject;
    private string _subjectKey = string.Empty;

    private DataGridHooks? _hooks;

    /// <summary>Draws the item's model, or null where the overlay did not wire one up.</summary>
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
        ItemVisuals all = table();
        Read(all);

        if (_page.Count == 0)
        {
            ImGui.TextDisabled(all.Say.Count > 0
                ? ImGuiText.Escape(all.Say[0])
                : "No item table yet - it is read from the install shortly after start-up.");
            return;
        }

        Header(all);
        Filter();

        float tall = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);

        if (_railOpen)
        {
            float rail = _rail.Left();
            if (ImGui.BeginChild("##item-rail", new Vector2(rail, tall), ImGuiChildFlags.Borders))
            {
                Rail();
            }

            ImGui.EndChild();

            _rail.Bar(tall);
        }

        float left = _split.Left();
        if (ImGui.BeginChild("##item-list", new Vector2(left, tall), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar(tall);

        if (ImGui.BeginChild("##item-model", new Vector2(0f, tall), ImGuiChildFlags.Borders))
        {
            Pane(all);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Works the table into columns, once per table.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE, the monster book's rule: the table arrives from a background read
    /// after start-up, and the selection follows the key rather than the row number into it.
    /// </remarks>
    private void Read(ItemVisuals all)
    {
        if (ReferenceEquals(_of, all))
        {
            return;
        }

        _of = all;
        _page = ItemBook.Of(all);
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

    private void Header(ItemVisuals all)
    {
        ImGui.TextDisabled(Caption);

        bool wrong = _error.Length > 0;
        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search("###item-find", string.Empty, ref _query, QueryLength))
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
            ImGui.OpenPopup("##item-columns");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Which of the {_page.Store.Columns.Length} columns the table shows."
                + " Drag across a column's histogram to filter by it; right-click one to undo that.");
        }

        Chooser();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(
            $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{all.Count.ToString(CultureInfo.InvariantCulture)} items");

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
        if (!ImGui.BeginPopup("##item-columns"))
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
            Ink: row => _page.Unique[row] ? UniqueInk : null,
            Hover: row => _page.Paths[row],
            Ranged: Range,
            Cleared: Clear);

        int chosen = _grid.Draw("##items", _page.Store, _columns, _shown, _chosenRow, _ranges, _hooks);
        if (chosen == _chosenRow)
        {
            return;
        }

        _chosenRow = chosen;
        _chosen = chosen >= 0 && chosen < _page.Paths.Length ? _page.Paths[chosen] : string.Empty;
    }

    /// <summary>What the item is and which file it is drawn from, then the model under it.</summary>
    private void Pane(ItemVisuals all)
    {
        if (_chosen.Length == 0 || all.Find(_chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose an item on the left.");
            return;
        }

        if (one.Unique)
        {
            ImGui.TextColored(UniqueInk, ImGuiText.Escape(one.Name));
        }
        else
        {
            ImGui.TextUnformatted(one.Name);
        }

        ImGui.TextDisabled(one.Unique
            ? "unique"
            : ImGuiText.Escape(string.Create(
                CultureInfo.InvariantCulture,
                $"{(one.Class.Length > 0 ? one.Class : "no class")}  ·  drop {one.DropLevel}  ·  {one.Width}x{one.Height}")));

        // WHICH .ao IS SHOWN IS A CHOICE AND SAID AS ONE: the table has two columns for it and
        // nothing yet says what separates them - see ItemVisuals. Only offered where both are filled.
        bool both = one.Ao.Length > 0 && one.Ao2.Length > 0;
        bool second = both ? _second : one.Ao.Length == 0;
        if (both)
        {
            if (ImGui.RadioButton("AOFile", !_second))
            {
                _second = false;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("AOFile2", _second))
            {
                _second = true;
            }

            second = _second;
        }

        string ao = second ? one.Ao2 : one.Ao;
        ImGui.TextDisabled(ImGuiText.Escape((second ? "AOFile2  " : "AOFile   ") + ao));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape($"{one.Path}\nart {one.Art}\nicon {one.Icon}"));
        }

        ImGui.Separator();

        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        string key = one.Path + "|" + ao;
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: one.Name, AoFiles: [ao]);
        }

        Vector2 room = ImGui.GetContentRegionAvail();
        model.Draw(_subject, _subjectKey, room.X, room.Y);
    }
}
