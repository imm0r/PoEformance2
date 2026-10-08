using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// A reference book: a query box, a facet rail, a column chooser, a clipped grid and a pane
/// beside it. What the monster, item, tile and effect books share.
/// </summary>
/// <remarks>
/// ONE SHELL WHERE THERE WERE FOUR. The three newer books were written by copying the monster
/// book's window and swapping the table, which left some four hundred lines - the filter, the rail,
/// the chooser, the drag-to-range, the grid hooks, the settings plumbing - in four places. Two bugs
/// then had to be fixed four times: a query that would not read left the previous table's row
/// numbers in the list across a rebuild, and three of the four handed the parser's error text to
/// a printf. A rule that lives in one place is a rule that is fixed once.
///
/// THREE PANES AND ONE FILTER. The rail on the left says what the rows that are left are made of
/// and narrows them by a click; the query box says the same thing in words; a drag across a
/// column's histogram says it in numbers. None of the three keeps any state of its own - all of
/// them edit the query TEXT, and everything else on screen is read back out of it. That is why the
/// ticks in the rail move when the text is edited by hand, and why emptying the box clears
/// everything at once: there is nowhere else for a filter to hide.
///
/// WHAT A BOOK STILL DECIDES is what it is a book OF: where its table comes from and when it has
/// changed (<see cref="Current"/>), which fields the rail offers (<see cref="Rails"/>), which rows
/// are inked (<see cref="Ink"/>), what the pane beside the list shows (<see cref="Pane"/>), and -
/// for the monster book, which has a detail pane and a footer of its own - how the panes are laid
/// out (<see cref="Body"/>, <see cref="Header"/>, <see cref="Footer"/>).
///
/// THE WHOLE BOOK IS IN THE MONOSPACE, which is a reading decision and was asked for from the live
/// client. A book is a table of figures, and the body face is a serif whose digits are OLD-STYLE,
/// where a 6 stands taller than a 7; a column of those does not line up. It costs nothing where
/// there is no monospace on the machine: the push is a no-op.
/// </remarks>
/// <typeparam name="TBook">The table, as <see cref="ColumnBook"/> works it out.</typeparam>
[SupportedOSPlatform("windows")]
public abstract class BookWindow<TBook>
    where TBook : ColumnBook
{
    /// <summary>How long a query may be. Shorter than the parser's own limit, on purpose.</summary>
    protected const uint QueryLength = 256;

    /// <summary>
    /// How many of a field's values the rail offers.
    /// </summary>
    /// <remarks>
    /// TWELVE OF FIVE THOUSAND, and which twelve is the whole point: the rail counts every value
    /// against the rows that are left and shows the commonest, so the skills it offers are the ones
    /// the monsters in front of somebody actually cast. A list of all of them would be a scrollbar.
    /// </remarks>
    protected const int MostFacets = 12;

    /// <summary>What the two boundaries every book has are known by - to ImGui, and in the settings file.</summary>
    private const string RailPane = "rail";
    private const string ListPane = "list";

    /// <summary>The box's background while the query does not read. Dark enough to type on.</summary>
    private static readonly Vector4 Wrong = new(0.32f, 0.11f, 0.11f, 1f);

    private readonly string _prefix;
    private readonly PaneSplit _rail;
    private readonly PaneSplit _split;
    private readonly DataGrid _grid = new();

    /// <summary>What each of the rail's fields holds within the rows that are left.</summary>
    private readonly Dictionary<string, List<Facet>> _facets = new(StringComparer.Ordinal);

    /// <summary>Which rows the search leaves, as row numbers into the book.</summary>
    private readonly List<int> _shown = [];

    /// <summary>Which columns are on, as store column numbers, in the order they are drawn.</summary>
    private readonly List<int> _columns = [];

    /// <summary>
    /// The ranges the query puts on columns, worked out from it rather than kept beside it.
    /// </summary>
    /// <remarks>
    /// DERIVED AND NEVER STORED, which is what makes the histogram and the box one filter instead
    /// of two. A drag writes "life 120..260" into the query; this reads it back out so the bins can
    /// be lit. Deleting the text by hand clears the shading, because there is nothing else holding
    /// it - and that is the property the whole design asks for.
    /// </remarks>
    private readonly List<ColumnRange> _ranges = [];

    /// <summary>Which columns are on, by store column number.</summary>
    private bool[] _visible = [];

    /// <summary>
    /// The columns somebody chose, BY NAME, or null while the defaults stand.
    /// </summary>
    /// <remarks>
    /// BY NAME AND NOT BY NUMBER, because this is what gets written to the settings file and read
    /// back a release later: a column added anywhere but the end shifts every number after it, and
    /// a saved view would then quietly show a different set of columns than the one it saved.
    /// </remarks>
    private IReadOnlyList<string>? _wanted;

    /// <summary>
    /// Whether the facet rail has a pane of its own.
    /// </summary>
    /// <remarks>
    /// IT COSTS WIDTH, AND WIDTH IS THE SCARCE THING HERE. A book is read WHILE PLAYING, over a game
    /// that wants the screen - and three panes wide enough to read every column is most of a
    /// monitor. So the rail folds away, the setting is remembered, and nothing is lost by folding
    /// it, because everything the rail does is written into the query, which stays.
    /// </remarks>
    private bool _railOpen = true;

    private string _query = string.Empty;
    private string _chosen = string.Empty;

    /// <summary>The query as a tree, or null while it takes everything.</summary>
    private QueryTerm? _term;

    /// <summary>Why the query does not read, and which character it gave up on.</summary>
    private string _error = string.Empty;
    private int _errorAt;

    /// <summary>Which rows the query leaves, as a set - what the rail counts against. Null until this book has answered once.</summary>
    private RowSet? _matched;

    /// <summary>The selected row, or -1. Kept beside the path so the grid can mark it.</summary>
    private int _chosenRow = -1;

    /// <summary>The filter has to run again - the table changed, or somebody typed.</summary>
    private bool _refilter = true;

    /// <summary>What the grid asks of this window. Made once - see <see cref="Grid"/>.</summary>
    private DataGridHooks? _hooks;

    /// <param name="prefix">What this book's ImGui ids start with - "item" gives "##item-rail" and "###item-find".</param>
    /// <param name="empty">The book with nothing in it, shown until a table arrives.</param>
    /// <param name="railShare">How much of the width the rail starts with.</param>
    /// <param name="listShare">How much of what is left the list starts with.</param>
    protected BookWindow(string prefix, TBook empty, float railShare, float listShare)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(empty);

        _prefix = prefix;
        Page = empty;
        _rail = new PaneSplit(railShare, RailPane);
        _split = new PaneSplit(listShare, ListPane);
    }

    /// <summary>Draws the model, or null where the overlay did not wire one up.</summary>
    /// <remarks>
    /// SET FROM OUTSIDE rather than made here, because it needs the install to read and the
    /// renderer to upload to, and a window has neither - see EntityOverlay.AttachMonsterBook. Null
    /// is the ordinary case on a machine with no game installed, and the pane simply has no picture.
    /// </remarks>
    public MonsterPortrait? Model { get; set; }

    /// <summary>Told when somebody changes the layout, so the choice can be kept.</summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// The model of the chosen row: shown in its own window, with the book keeping the window's switch, the export row and the files.
    /// </summary>
    /// <remarks>
    /// ONE PLACE FOR WHAT EVERY BOOK DOES WITH ITS MODEL, now that the picture is a window of its own -
    /// see MonsterPortrait.Show. The two folds are closed until opened; their state is ImGui's.
    /// </remarks>
    /// <param name="subject">What to show, or null for nothing.</param>
    /// <param name="key">What tells one subject from another.</param>
    /// <param name="title">What the window's title calls it.</param>
    protected void ModelTools(MonsterVariety? subject, string key, string title)
    {
        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        model.Show(subject, key, title);
        model.DrawOpener();
        if (OverlayLayout.Subsection($"view & export##{_prefix}-model-export"))
        {
            model.DrawExport();
        }

        if (OverlayLayout.Subsection($"files & surveys##{_prefix}-model-files"))
        {
            model.DrawFiles();
        }

        model.DrawWindow();
    }

    /// <summary>Which columns are showing, by name, for whoever writes the settings file.</summary>
    public IReadOnlyList<string> Columns => [.. _columns.Select(one => Page.Store.Columns[one].Name)];

    /// <summary>How wide each column is, by name, for whoever writes the settings file.</summary>
    public IReadOnlyDictionary<string, int> ColumnWidths => _grid.Widths;

    /// <summary>Whether the facet rail has a pane, for whoever writes the settings file.</summary>
    public bool RailOpen => _railOpen;

    /// <summary>Where the boundaries are, by the name each was made with.</summary>
    /// <remarks>
    /// A DICTIONARY AND NOT TWO NUMBERS, so a book with a third pane needs no new settings key and
    /// an old file missing one of them simply leaves that boundary at its default - which is what a
    /// build that had two panes writes for a build that has three.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Panes
    {
        get
        {
            var panes = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [RailPane] = _rail.Share,
                [ListPane] = _split.Share,
            };
            MorePanes(panes);
            return panes;
        }
    }

    /// <summary>The table as it is worked out now. What the window draws.</summary>
    protected TBook Page { get; private set; }

    /// <summary>The rows the query leaves, in the grid's order.</summary>
    protected IReadOnlyList<int> Shown => _shown;

    /// <summary>The selected row's key, or empty.</summary>
    protected string Chosen => _chosen;

    /// <summary>The query as typed, which is what a click in the rail edits.</summary>
    protected string Query => _query;

    /// <summary>The query as a tree, or null while it takes everything.</summary>
    protected QueryTerm? Term => _term;

    /// <summary>Why the query does not read. Empty while it does.</summary>
    protected string Error => _error;

    /// <summary>The rail's boundary, for a book that works the pane widths out ahead of drawing them.</summary>
    protected PaneSplit RailSplit => _rail;

    /// <summary>The list's boundary. See <see cref="RailSplit"/>.</summary>
    protected PaneSplit ListSplit => _split;

    /// <summary>What the box above the book is, said above it rather than inside it.</summary>
    protected abstract string Caption { get; }

    /// <summary>What the box takes, said on hover.</summary>
    /// <remarks>
    /// THE CAPTION SAYS WHAT THE BOX IS AND THE HOVER SAYS WHAT IT TAKES, which is the arrangement
    /// the live client drew. The grammar used to be the box's own greyed hint, where it was only
    /// ever visible while the box was EMPTY - so it vanished at the first keystroke, which is
    /// exactly when somebody is wondering what else they may write.
    /// </remarks>
    protected abstract string Grammar { get; }

    /// <summary>What a row is, plural, for the count under the box: "monsters", "items".</summary>
    protected abstract string Noun { get; }

    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// NOT EVERY FIELD A QUERY CAN NAME. A rail is for the questions with a handful of answers
    /// worth clicking - which tag, which type, which class - and "name" has 994 distinct values
    /// over the monster table's 2733 rows, which is a list of the table rather than a way into it.
    /// Those are still searchable by typing; they are just not worth a column of clicks.
    /// </remarks>
    protected abstract IReadOnlyList<(string Label, string Field)> Rails { get; }

    /// <summary>How many of the rail's sections start open. All of them, unless a book has many.</summary>
    protected virtual int OpenRails => int.MaxValue;

    /// <summary>One text line's worth of height held back under the panes, for a book with a footer.</summary>
    protected virtual float Reserved => 0f;

    /// <summary>
    /// The table as it stands this frame.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE and not by count: a table arrives from a background read after
    /// start-up, and two different tables can perfectly well hold the same number of rows. A book
    /// returns the same instance while its sources have not changed and a new one when they have,
    /// and this window rebuilds its view on the new one - the selection following the KEY rather
    /// than the row number into it, because a new table is a different set of rows in a different
    /// order.
    /// </remarks>
    protected abstract TBook Current();

    /// <summary>Why the book is empty, for the line shown instead of it.</summary>
    protected abstract string WhyEmpty();

    /// <summary>A colour for a row's first cell, where it has one - a boss, a unique, a tile placed here.</summary>
    protected abstract Vector4? Ink(int row);

    /// <summary>The pane beside the list: what the chosen row is, and its model under that.</summary>
    protected abstract void Pane();

    /// <summary>Puts back what a settings file remembered. Nulls leave the defaults.</summary>
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
            Put(panes, RailPane, _rail);
            Put(panes, ListPane, _split);
            RestorePanes(panes);
        }

        // WIRED HERE rather than where the fields are made, because a field initialiser cannot
        // see this window's own Changed - and all of these write the same settings file.
        // Assigning the same method again on a second Show is harmless.
        _grid.Settled = Moved;
        _rail.Settled = Moved;
        _split.Settled = Moved;
        Wired();

        Layout();
    }

    /// <summary>Puts a remembered boundary back, where the file has it.</summary>
    protected static void Put(IReadOnlyDictionary<string, double> panes, string name, PaneSplit split)
    {
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(split);

        if (panes.TryGetValue(name, out double share))
        {
            split.Restore(share);
        }
    }

    /// <summary>A book with a third boundary adds it to what the settings file keeps.</summary>
    protected virtual void MorePanes(Dictionary<string, double> into)
    {
    }

    /// <summary>And puts it back. See <see cref="MorePanes"/>.</summary>
    protected virtual void RestorePanes(IReadOnlyDictionary<string, double> panes)
    {
    }

    /// <summary>Wires whatever else of this book's writes the settings file. Called from <see cref="Show"/>.</summary>
    protected virtual void Wired()
    {
    }

    /// <summary>Somebody dragged a boundary or a column edge, and it came to rest.</summary>
    protected void Moved() => Changed?.Invoke();

    /// <summary>
    /// Selects a row by its key, from outside the book.
    /// </summary>
    /// <remarks>
    /// THE ROW NUMBER IS ALLOWED TO BE HIDDEN. A search or a range may be filtering the row out,
    /// and the grid returns the selection unchanged when it cannot see it (DataGrid.Draw), so the
    /// pane shows the row either way - it reads the KEY. The highlight comes back with the filter
    /// that hid it.
    /// </remarks>
    /// <returns>Whether the book has a row for that key.</returns>
    public bool Choose(string path)
    {
        if (path is not { Length: > 0 })
        {
            return false;
        }

        _chosen = path;
        _chosenRow = Page.Row(path);
        return _chosenRow >= 0;
    }

    /// <summary>Draws the tab, in the face a table of figures belongs in.</summary>
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
        TBook now = Current();
        if (!ReferenceEquals(now, Page))
        {
            Read(now);
        }

        if (Page.Count == 0)
        {
            ImGui.TextDisabled(ImGuiText.Escape(WhyEmpty()));
            return;
        }

        Header();
        Filter();

        // Handed to the GRIPS as well as to the panes: a pane asking for the rest of the height
        // would take a footer's line too and put it under the bottom of the window; so would a
        // grip, which is the half that was missed the first time - see PaneSplit.Bar.
        float tall = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - Reserved);
        Body(tall);
        Footer();
    }

    /// <summary>
    /// The panes, side by side: the rail where it is open, the list, and the pane beside it.
    /// </summary>
    /// <remarks>
    /// EACH IS A CHILD WINDOW, which settles the hit tests by construction rather than by
    /// submission order: ImGui's ItemHoverable begins with "if (g.HoveredWindow != window) return
    /// false", so nothing in one pane can reach into the next however wide it spans.
    /// </remarks>
    protected virtual void Body(float tall)
    {
        RailPaneDrawn(tall);
        ListPaneDrawn(tall);

        if (ImGui.BeginChild($"##{_prefix}-model", new Vector2(0f, tall), ImGuiChildFlags.Borders))
        {
            Pane();
        }

        ImGui.EndChild();
    }

    /// <summary>The rail and its grip, where the rail is open. Nothing otherwise.</summary>
    protected void RailPaneDrawn(float tall)
    {
        if (!_railOpen)
        {
            return;
        }

        float rail = _rail.Left();
        if (ImGui.BeginChild($"##{_prefix}-rail", new Vector2(rail, tall), ImGuiChildFlags.Borders))
        {
            Rail();
        }

        ImGui.EndChild();

        _rail.Bar(tall);
    }

    /// <summary>The list and its grip.</summary>
    protected void ListPaneDrawn(float tall)
    {
        float left = _split.Left();
        if (ImGui.BeginChild($"##{_prefix}-list", new Vector2(left, tall), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar(tall);
    }

    /// <summary>The line under the panes, where a book has one. Nothing by default.</summary>
    protected virtual void Footer()
    {
    }

    /// <summary>
    /// Takes a new table: the view is rebuilt on it, the selection following the key.
    /// </summary>
    /// <remarks>
    /// THE RANGES DO NOT SURVIVE, deliberately. A range is a pair of numbers that only means
    /// anything against the distribution it was dragged out of, and a new table is a new
    /// distribution - so keeping them would leave the list filtered by something nobody can see
    /// the reason for any more. The columns DO survive: "show me armour" means the same thing
    /// whatever the table says about it.
    ///
    /// NEITHER DO THE ROWS. The list held row numbers into the OLD table, and a query that does
    /// not read right now - which is every query while it is being typed - would have left them
    /// standing: wrong rows under the right headings, or an index past the end of a smaller
    /// table, which is a crash in the grid. So the old answer is dropped here, and
    /// <see cref="Filter"/> shows every row until the query reads again.
    /// </remarks>
    private void Read(TBook book)
    {
        Page = book;
        _chosenRow = Page.Row(_chosen);
        _ranges.Clear();
        _matched = null;
        _shown.Clear();
        Layout();
    }

    /// <summary>Works out which columns are on, from what was chosen or from the defaults.</summary>
    private void Layout()
    {
        DataColumn[] all = Page.Store.Columns;
        _visible = new bool[all.Length];

        for (var at = 0; at < all.Length; at++)
        {
            _visible[at] = _wanted is null
                ? Page.Shown[at]
                : _wanted.Contains(all[at].Name);
        }

        // THE FIRST COLUMN IS NOT OPTIONAL: it carries the row's selectable, so a table without it
        // is a table nothing can be picked out of.
        if (all.Length > 0)
        {
            _visible[0] = true;
        }

        Rebuild();

        // Filtered here rather than on the next frame, so the count in the header is the count of
        // what is in the list underneath it from the very first frame.
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

    /// <summary>
    /// The caption, the query box, the row of controls under it, the count and what is wrong.
    /// </summary>
    /// <remarks>
    /// STILL A SEARCH BOX TO ANYBODY WHO WANTS ONE. A bare word is a term of the grammar, so typing
    /// "undead" does exactly what it always did; tag:undead and life&gt;200 are there for whoever
    /// wants them. That is deliberate and it is the reason the grammar was written the way it was:
    /// a viewer that has to be learned before it answers anything gets opened once.
    ///
    /// A book with controls that sit over particular panes lays this row out itself - see the
    /// monster book - out of the same pieces: <see cref="SearchBox"/>, <see cref="FacetsButton"/>,
    /// <see cref="ColumnsButton"/>, <see cref="Caret"/>.
    /// </remarks>
    protected virtual void Header()
    {
        ImGui.TextDisabled(Caption);
        SearchBox(0f);

        FacetsButton();
        ImGui.SameLine();
        ColumnsButton();
        HeaderExtras();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Counted());

        if (_error.Length > 0)
        {
            ImGui.TextColored(OverlayInk.Warn, ImGuiText.Escape(_error));
        }
    }

    /// <summary>Anything a book puts on the row of controls after the Columns button. Nothing by default.</summary>
    protected virtual void HeaderExtras()
    {
    }

    /// <summary>"120 of 2733 monsters" - the answer to the query.</summary>
    protected string Counted()
        => $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Page.Count.ToString(CultureInfo.InvariantCulture)} {Noun}";

    /// <summary>
    /// The query box, red while the query does not read, and the grammar on hover.
    /// </summary>
    /// <remarks>
    /// THE ROWS ON SCREEN ARE LEFT ALONE WHILE IT IS BROKEN: halfway through typing a query is a
    /// broken query, and they are the last answer somebody got - see <see cref="Filter"/>.
    ///
    /// THE TOOLTIP IS NOT SHOWN WHILE IT IS BEING TYPED IN. A tooltip follows the mouse, the mouse
    /// is over the box that was just clicked, and a label that then sits there through the whole
    /// query is the one thing this window has already been asked to take away.
    ///
    /// AND THE ERROR IS ESCAPED ON ITS WAY INTO THE TOOLTIP, because SetTooltip is printf and the
    /// error quotes what was typed - "'life%&gt;' needs a number after it" - and a percent sign in a
    /// word is legal in this grammar, 207 stat ids carry one. See ImGuiText.
    /// </remarks>
    /// <param name="reserve">Room to leave on the right of the box.</param>
    /// <returns>Where the box is and how tall, for <see cref="Caret"/>.</returns>
    protected (Vector2 At, float Tall) SearchBox(float reserve)
    {
        bool wrong = _error.Length > 0;
        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search($"###{_prefix}-find", string.Empty, ref _query, QueryLength, reserve))
        {
            _refilter = true;
        }

        Vector2 box = ImGui.GetItemRectMin();
        float below = ImGui.GetItemRectSize().Y;
        bool typing = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        if (wrong)
        {
            ImGui.PopStyleColor();
        }

        if (!typing && hovered)
        {
            ImGui.SetTooltip(wrong ? ImGuiText.Escape(_error) : Grammar);
        }

        return (box, below);
    }

    /// <summary>The button that folds the rail away and back.</summary>
    protected void FacetsButton()
    {
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
    }

    /// <summary>The button that opens the column chooser, and the chooser itself.</summary>
    protected void ColumnsButton()
    {
        if (ImGui.Button("Columns"))
        {
            ImGui.OpenPopup($"##{_prefix}-columns");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Which of the {Page.Store.Columns.Length} columns the table shows."
                + " Drag across a column's histogram to filter by it; right-click one to undo that.");
        }

        Chooser();
    }

    /// <summary>Points at the character the query stopped reading at, and says why.</summary>
    /// <param name="box">Where the query box was drawn - what <see cref="SearchBox"/> returned.</param>
    /// <param name="below">How tall it was.</param>
    protected void Caret(Vector2 box, float below)
    {
        if (_error.Length == 0)
        {
            return;
        }

        // COUNTED FROM 1, which is what the parser hands back so that this can exist at all. A
        // column of zero is a fault the table found rather than the text - an unknown field name -
        // and there is nothing under the box to point at.
        if (_errorAt > 0)
        {
            int at = Math.Clamp(_errorAt - 1, 0, _query.Length);
            float x = box.X + ImGui.GetStyle().FramePadding.X + ImGui.CalcTextSize(_query[..at]).X;

            ImGui.GetWindowDrawList().AddText(
                new Vector2(x, box.Y + below - (ImGui.GetFontSize() * 0.25f)),
                ImGui.GetColorU32(OverlayInk.Warn),
                "^");
        }

        ImGui.TextColored(OverlayInk.Warn, ImGuiText.Escape(_error));
    }

    /// <summary>Whether a term at the top of the query names this field and value - what a tick reads.</summary>
    protected bool Holds(string field, string value) => ColumnQuery.Holds(_term, field, value);

    /// <summary>Adds this field and value to the query, or takes it back out - what a click does.</summary>
    protected void Toggle(string field, string value)
    {
        _query = ColumnQuery.Toggle(_query, field, value);
        _refilter = true;
    }

    /// <summary>
    /// The facet rail: what the rows that are left are made of, and a click to narrow them.
    /// </summary>
    /// <remarks>
    /// EVERY COUNT IS AGAINST THE ROWS THAT ARE LEFT, so the rail answers "what is in this set"
    /// rather than "what is in the table". Click a value and it is added to the query as one more
    /// thing that must hold; click it again and it comes back out. Nothing is stored here - the
    /// tick beside a value is read back out of the query text, which is why editing that text by
    /// hand moves the ticks.
    ///
    /// A ZERO IS SHOWN, DIM, rather than hidden. "There are no casters left in this set" is an
    /// answer somebody came for, and a rail that drops what it cannot offer looks like a rail that
    /// has lost the field.
    /// </remarks>
    private void Rail()
    {
        if (_matched is null)
        {
            ImGui.TextDisabled("Nothing counted yet.");
            return;
        }

        IReadOnlyList<(string Label, string Field)> rails = Rails;
        for (var at = 0; at < rails.Count; at++)
        {
            (string label, string field) = rails[at];
            if (!_facets.TryGetValue(field, out List<Facet>? facets) || facets.Count == 0)
            {
                continue;
            }

            if (!OverlayLayout.Subsection(label, openByDefault: at < OpenRails))
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
        bool on = Holds(field, facet.Value);
        string count = facet.Count.ToString(CultureInfo.InvariantCulture);

        float room = ImGui.GetContentRegionAvail().X;
        float wide = ImGui.CalcTextSize(count).X + ImGui.GetStyle().ItemSpacing.X;
        bool quiet = facet.Count == 0 && !on;

        // BY VALUE AND NOT BY POSITION: the rail is re-counted and re-ordered on every keystroke,
        // so a row's place in it is not its identity and an id built from one would hand a click
        // to whatever has moved into that slot.
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
                Toggle(field, facet.Value);
            }

            if (quiet)
            {
                ImGui.PopStyleColor();
            }

            // The whole value on hover, because a rail is narrow and these are the game's own ids.
            if (ImGui.IsItemHovered() && ImGui.BeginTooltip())
            {
                try
                {
                    ImGui.TextUnformatted($"{field}:{facet.Value}");
                }
                finally
                {
                    ImGui.EndTooltip();
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled(count);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Which columns show, grouped the way somebody would look for them.
    /// </summary>
    /// <remarks>
    /// GROUPED AND NOT LISTED. Twenty-nine names in the order the store happens to hold them is a
    /// list nobody reads to the end; "Defence" with five things under it is one somebody can find
    /// armour in. The groups come from the book rather than from here, because what a column IS is
    /// a fact about the table and not about this popup - and a group's columns need not sit
    /// together in the store, so each heading gathers its own.
    /// </remarks>
    private void Chooser()
    {
        if (!ImGui.BeginPopup($"##{_prefix}-columns"))
        {
            return;
        }

        try
        {
            DataColumn[] all = Page.Store.Columns;
            var drawn = 0;

            foreach (string group in Groups())
            {
                if (drawn > 0)
                {
                    ImGui.Separator();
                }

                ImGui.TextDisabled(group);

                for (var at = 0; at < all.Length; at++)
                {
                    if (!string.Equals(Page.Groups[at], group, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // THE FIRST COLUMN IS SHOWN AS FIXED rather than as a checkbox that refuses to
                    // clear - a control that does nothing when clicked is worse than no control.
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

                drawn++;
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>The group headings, once each, in the order the columns first use them.</summary>
    private List<string> Groups()
    {
        var said = new List<string>(8);
        foreach (string group in Page.Groups)
        {
            if (!said.Contains(group))
            {
                said.Add(group);
            }
        }

        return said;
    }

    /// <summary>
    /// Runs the query over the table, when it has to.
    /// </summary>
    /// <remarks>
    /// A BROKEN QUERY LEAVES THE LAST ANSWER ON SCREEN. Every query is broken while it is being
    /// typed - "tag:" is halfway to something - and a list that emptied at each of those
    /// keystrokes would flicker through nothing on the way to every answer.
    ///
    /// UNLESS THERE IS NO LAST ANSWER FOR THIS TABLE, which is the frame after a rebuild - see
    /// <see cref="Read"/>. Then the rows shown are every row, which is what an empty box would
    /// give, rather than row numbers that belonged to a table that is gone.
    ///
    /// AND A FIELD THE TABLE HAS NOT GOT IS THE TABLE'S ANSWER, not the parser's: "bogus:thing"
    /// reads perfectly well and only this table can say there is no such column.
    /// </remarks>
    private void Filter()
    {
        if (!_refilter)
        {
            return;
        }

        _refilter = false;

        QueryResult parsed = ColumnQuery.Parse(_query);
        _error = parsed.Error;
        _errorAt = parsed.Column;

        if (_error.Length > 0)
        {
            Unanswered();
            return;
        }

        _term = parsed.Term;

        RowSet? rows = Page.Matching(_term, out string why);
        if (rows is null)
        {
            _error = why;
            _errorAt = 0;
            Unanswered();
            return;
        }

        Answered(rows);
        Ranges();
    }

    /// <summary>Every row, where a table has not been answered about at all yet.</summary>
    private void Unanswered()
    {
        if (_matched is not null)
        {
            return;
        }

        var all = new RowSet(Page.Count);
        all.All();
        Answered(all);
        _ranges.Clear();
    }

    private void Answered(RowSet rows)
    {
        _matched = rows;
        rows.CopyTo(_shown);

        // The grid sorts what it is given, and it has just been given a different list.
        _grid.Resort();
        Counts();
    }

    /// <summary>Reads the query's ranges back out, so the histograms can light what it picked.</summary>
    private void Ranges()
    {
        _ranges.Clear();
        DataColumn[] columns = Page.Store.Columns;

        for (var at = 0; at < columns.Length; at++)
        {
            if (ColumnQuery.RangeOf(_term, columns[at].Name) is { } range)
            {
                _ranges.Add(new ColumnRange(at, range.Least, range.Most));
            }
        }
    }

    /// <summary>
    /// Counts every value of every field the rail offers, against the rows that are left.
    /// </summary>
    /// <remarks>
    /// ONLY WHEN THE FILTER MOVES, never per frame. Five fields is some nine thousand values, and
    /// each one is an AND of two bitsets and a popcount - well under a millisecond all told, and
    /// still not something to do sixty times a second for a rail nobody is looking at.
    ///
    /// INTO LISTS THAT ARE KEPT, so a keystroke re-counts rather than re-allocates.
    /// </remarks>
    private void Counts()
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

            Page.Facets(_matched, field, into, MostFacets);
        }
    }

    /// <summary>Takes a range a drag picked out, by writing it into the query.</summary>
    /// <remarks>
    /// THIS RUNS EVERY FRAME WHILE THE DRAG IS HAPPENING, which is what makes the list move with
    /// the cursor - so a range that has not changed must cost nothing. Comparing the text it would
    /// produce is the cheapest way to ask, and it is exact.
    /// </remarks>
    private void Range(int column, double least, double most)
    {
        if (column < 0 || column >= Page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Ranged(_query, Page.Store.Columns[column].Name, least, most);
        if (string.Equals(made, _query, StringComparison.Ordinal))
        {
            return;
        }

        _query = made;
        _refilter = true;
    }

    private void Clear(int column)
    {
        if (column < 0 || column >= Page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Drop(_query, Page.Store.Columns[column].Name);
        if (string.Equals(made, _query, StringComparison.Ordinal))
        {
            return;
        }

        _query = made;
        _refilter = true;
    }

    /// <summary>
    /// The list, drawn by a grid that knows nothing about what the rows are.
    /// </summary>
    /// <remarks>
    /// THE TWO THINGS THE GRID CANNOT WORK OUT FOR ITSELF are handed to it here: which rows are
    /// inked - a flag the table carries that no column shows - and that hovering a row should
    /// print its key, which is the string somebody came to copy.
    ///
    /// KEPT IN A FIELD rather than written at the call, because a lambda written there is a new
    /// delegate every frame. They read <see cref="Page"/> when they are called rather than
    /// closing over the book that existed when they were made, so a table that arrives later is
    /// picked up without rebuilding them.
    /// </remarks>
    private void Grid()
    {
        _hooks ??= new DataGridHooks(
            Ink: Ink,
            Hover: row => Page.Paths[row],
            Ranged: Range,
            Cleared: Clear);

        int chosen = _grid.Draw($"##{_prefix}s", Page.Store, _columns, _shown, _chosenRow, _ranges, _hooks);

        if (chosen == _chosenRow)
        {
            return;
        }

        _chosenRow = chosen;
        _chosen = chosen >= 0 && chosen < Page.Paths.Length ? Page.Paths[chosen] : string.Empty;
    }
}
