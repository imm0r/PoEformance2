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
/// THE MONSTER BOOK'S LIST AND THE MONSTER BOOK'S PANE. The grid, the query grammar and the
/// pane split are the same parts; the model is drawn by a <see cref="MonsterPortrait"/> of its own,
/// because an item's .ao is the same file format as a monster's and the portrait only ever needed
/// the .ao paths - see MonsterModels.Of. The item is handed to it as a variety carrying nothing but
/// a name and that one path, which is everything the portrait reads off one.
///
/// NO FACET RAIL AND NO COLUMN CHOOSER, yet. Six columns fit, and the questions worth a rail - which
/// class, base or unique - are one word in the query. Both are the monster book's and can follow
/// when the list grows columns worth choosing between.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ItemBookWindow(Func<ItemVisuals> table)
{
    /// <summary>How long a query may be. The monster book's limit, for the same reason.</summary>
    private const uint QueryLength = 256;

    /// <summary>What the box above the book is.</summary>
    private const string Caption = "Search for any item with a model";

    /// <summary>What the box takes, said on hover.</summary>
    private const string Grammar = "sword  ·  class:bow drop>40  ·  kind:unique  ·  not unique";

    /// <summary>The box's background while the query does not read. The monster book's.</summary>
    private static readonly Vector4 Wrong = new(0.32f, 0.11f, 0.11f, 1f);

    /// <summary>A unique's row is in the game's own unique colour.</summary>
    private static readonly Vector4 UniqueInk = OverlayInk.Rarity(ItemRarity.Unique);

    private readonly PaneSplit _split = new(0.42f, "item-list");
    private readonly DataGrid _grid = new();

    private readonly List<int> _shown = [];
    private readonly List<int> _columns = [];
    private readonly List<ColumnRange> _ranges = [];

    private ItemVisuals _of = ItemVisuals.Empty;
    private ItemBook _page = ItemBook.Empty;

    private string _query = string.Empty;
    private QueryTerm? _term;
    private string _error = string.Empty;
    private bool _refilter = true;

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

        _columns.Clear();
        for (var at = 0; at < _page.Store.Columns.Length; at++)
        {
            _columns.Add(at);
        }

        _grid.Resort();
        _refilter = true;
        Filter();
    }

    private void Header(ItemVisuals all)
    {
        ImGui.TextDisabled(Caption);

        string count = $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{all.Count.ToString(CultureInfo.InvariantCulture)} items";
        float reserve = ImGui.CalcTextSize(count).X + ImGui.GetStyle().ItemSpacing.X;

        bool wrong = _error.Length > 0;
        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search("###item-find", string.Empty, ref _query, QueryLength, reserve))
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

        ImGui.SameLine();
        ImGui.TextDisabled(count);
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
