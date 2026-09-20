using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// The game's monster table as something a person can read, search and sort.
/// </summary>
/// <remarks>
/// A REFERENCE BOOK RATHER THAN A QUERY. Everything else that touches this table asks about the
/// one thing in front of the player; here nothing is in front of anybody and the table itself is
/// the subject - which is why this is a list beside a detail pane rather than a row per monster.
/// A monster carries three modifier lists, up to sixty-seven skills and ten tags, and none of that
/// fits in a cell.
///
/// EVERY ROW NUMBER IS RESOLVED AND THE NUMBER STAYS. Type, blood, tags, skills and the three
/// modifier columns are all row numbers into other tables, and MonsterVarieties already joins
/// them; what this adds is that a row which resolves to NOTHING is still shown, as "#4211". A
/// list that silently dropped what it could not name would look complete and be wrong - the same
/// trap <see cref="MonsterVarieties.Skills"/> names, and the reason the modifiers here are walked
/// row by row rather than through <see cref="MonsterVarieties.Modifiers"/>, which drops them.
/// Against the shipped export nothing is dropped either way; see <see cref="Mods"/> for why it
/// still matters.
///
/// EVERY NUMBER IT CAN NAME, IT NAMES - and the ones it cannot, it leaves bare. MonsterVariety's
/// own remarks settled the units across 2733 rows: Life, Damage, Xp and ModelSize sit around 100
/// and are PERCENTAGES of the base for the level; AttackSpeed, Speed and the aggro ranges are
/// plainly not, and naming a unit the table cannot prove is how a display ends up confidently
/// wrong. Crit is the trap in that set - it holds 0, 1 or 2 and is a KIND, not a chance - so it is
/// never drawn with a percent sign.
///
/// THE TEXT EVERY ROW IS SEARCHED BY, AND EVERY NUMBER IT PRINTS, IS BUILT ONCE PER TABLE. Joining
/// tags, skills and modifier ids for 2733 monsters is real work; done per frame it is work behind a
/// search box, which is where a tool starts to feel slow. So is formatting a cell: this window used
/// to submit fifteen hundred rows to show forty, which is some seven thousand strings built and
/// thrown away every frame. <see cref="MonsterBook"/> does both once, when the table arrives.
///
/// THREE PANES AND ONE FILTER. The rail on the left says what the rows that are left are made of
/// and narrows them by a click; the query box says the same thing in words; a drag across a
/// column's histogram says it in numbers. None of the three keeps any state of its own - all of
/// them edit the query TEXT, and everything else on screen is read back out of it. That is why the
/// ticks in the rail move when the text is edited by hand, and why emptying the box clears
/// everything at once: there is nowhere else for a filter to hide.
///
/// WHAT IS STILL MISSING, so that nobody has to rediscover it: a comparison of several pinned
/// monsters, and the tie to what is on screen in the game right now. Both are in
/// docs/reading-big-tables.md, stages four and five.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonsterBookWindow(Func<MonsterVarieties> table, Func<StatDescriptions> sentences)
{
    /// <summary>How long a query may be. Shorter than the parser's own limit, on purpose.</summary>
    private const uint QueryLength = 256;

    /// <summary>How many of a field's values the rail offers.</summary>
    /// <remarks>
    /// TWELVE OF FIVE THOUSAND, and which twelve is the whole point: the rail counts every value
    /// against the rows that are left and shows the commonest, so the skills it offers are the ones
    /// the monsters in front of somebody actually cast. A list of all of them would be a scrollbar.
    /// </remarks>
    private const int MostFacets = 12;

    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// NOT EVERY FIELD A QUERY CAN NAME. A rail is for the questions with a handful of answers
    /// worth clicking - which tag, which type, which skill - and "name" has 994 distinct values
    /// over 2733 rows, which is a list of the table rather than a way into it. Those are still
    /// searchable by typing; they are just not worth a column of clicks.
    /// </remarks>
    private static readonly (string Label, string Field)[] Rails =
    [
        ("Tags", "tag"),
        ("Types", "type"),
        ("Skills", "skill"),
        ("Modifiers", "mod"),
        ("Blood", "blood"),
    ];

    /// <summary>The box's background while the query does not read. Dark enough to type on.</summary>
    private static readonly Vector4 Wrong = new(0.32f, 0.11f, 0.11f, 1f);

    /// <summary>Scratch for <see cref="Resistances"/>, held so the pane does not allocate per frame.</summary>
    private static readonly List<string> _resists = [];
    private static readonly List<string> _weak = [];
    private static readonly List<string> _quiet = [];

    /// <summary>What each boundary is known by - to ImGui, and in the settings file.</summary>
    private const string RailPane = "rail";
    private const string ListPane = "list";
    private const string ModelPane = "model";

    /// <summary>What the box above the book is, said above it rather than inside it.</summary>
    private const string Caption = "Search for any monster";

    /// <summary>What the box takes, said on hover. See <see cref="Header"/> for why it is not the box's own hint.</summary>
    private const string Grammar = "undead  ·  tag:undead life>200  ·  skill:fire  ·  not boss";

    private readonly PaneSplit _rail = new(0.2f, RailPane);
    private readonly PaneSplit _split = new(0.46f, ListPane);

    /// <summary>Where the detail pane ends and the model's begins. Of the room those two share.</summary>
    private readonly PaneSplit _model = new(0.58f, ModelPane);
    private readonly DataGrid _grid = new();

    /// <summary>What each of the rail's fields holds within the rows that are left.</summary>
    private readonly Dictionary<string, List<Facet>> _facets = new(StringComparer.Ordinal);

    /// <summary>The table the book was built from, to notice when a different one arrives.</summary>
    private MonsterVarieties _of = MonsterVarieties.Empty;

    /// <summary>And the sentences it was built with, which arrive later than the table does.</summary>
    private StatDescriptions _said = StatDescriptions.Empty;

    private MonsterBook _page = MonsterBook.Empty;

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
    /// Whether the facet rail has a pane of its own.
    /// </summary>
    /// <remarks>
    /// IT COSTS WIDTH, AND WIDTH IS THE SCARCE THING HERE. This window is read WHILE PLAYING, over
    /// a game that wants the screen - and three panes wide enough to read every column is most of
    /// a monitor. So the rail folds away, the setting is remembered, and what is left is the two
    /// panes the window had before: the list and the detail. Nothing is lost by folding it, because
    /// everything the rail does is written into the query, which stays.
    /// </remarks>
    private bool _railOpen = true;

    /// <summary>
    /// Whether the monster's model has a pane of its own.
    /// </summary>
    /// <remarks>
    /// A PANE RATHER THAN A CORNER OF THE DETAIL ONE, which is what it was first. A picture placed
    /// inside a column of text has to be told how much room the words want, and ImGui has no text
    /// flow to ask - so it was measured, and every way of measuring it was wrong in its own way:
    /// the section headers' hit boxes stole the drag, a group containing one measured as the whole
    /// pane, and the width the lists reported moved the picture whenever one was opened. A pane
    /// has no column to measure and nothing to sit beside.
    ///
    /// AND IT IS A CHILD WINDOW, which settles the hit tests by construction rather than by
    /// submission order: ImGui's ItemHoverable begins with "if (g.HoveredWindow != window) return
    /// false", so nothing in the detail pane can reach into this one however wide it spans.
    ///
    /// ON BY DEFAULT for the reason the rail is - a pane nobody knows about is a pane nobody opens
    /// - and folded away by the same button-and-setting pair when the width is wanted elsewhere.
    /// </remarks>
    private bool _modelOpen = true;

    /// <summary>
    /// The columns somebody chose, BY NAME, or null while the defaults stand.
    /// </summary>
    /// <remarks>
    /// BY NAME AND NOT BY NUMBER, because this is what gets written to the settings file and read
    /// back a release later: a column added anywhere but the end shifts every number after it, and
    /// a saved view would then quietly show a different set of columns than the one it saved.
    /// </remarks>
    private IReadOnlyList<string>? _wanted;

    private string _query = string.Empty;
    private string _chosen = string.Empty;

    /// <summary>The query as a tree, or null while it takes everything.</summary>
    private QueryTerm? _term;

    /// <summary>Why the query does not read, and which character it gave up on.</summary>
    private string _error = string.Empty;
    private int _errorAt;

    /// <summary>Which rows the query leaves, as a set - what the rail counts against.</summary>
    private RowSet? _matched;

    /// <summary>The selected row, or -1. Kept beside the path so the grid can mark it.</summary>
    private int _chosenRow = -1;

    /// <summary>The filter has to run again - the table changed, or somebody typed.</summary>
    private bool _refilter = true;

    /// <summary>What the grid asks of this window. Made once - see <see cref="Grid"/>.</summary>
    private DataGridHooks? _hooks;

    /// <summary>Which columns are showing, by name, for whoever writes the settings file.</summary>
    public IReadOnlyList<string> Columns
        => [.. _columns.Select(one => _page.Store.Columns[one].Name)];

    /// <summary>Whether the facet rail has a pane, for whoever writes the settings file.</summary>
    public bool RailOpen => _railOpen;

    /// <summary>And whether the model does.</summary>
    public bool ModelOpen => _modelOpen;

    /// <summary>Told when somebody changes the layout, so the choice can be kept.</summary>
    public Action? Changed { get; set; }

    /// <summary>How wide each column is, by name, for whoever writes the settings file.</summary>
    public IReadOnlyDictionary<string, int> ColumnWidths => _grid.Widths;

    /// <summary>Where the three boundaries are, by the name each was made with.</summary>
    /// <remarks>
    /// A DICTIONARY AND NOT THREE NUMBERS, so a fourth pane needs no new settings key and an old
    /// file missing one of them simply leaves that boundary at its default - which is what a
    /// build that had two panes writes for a build that has three.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Panes => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [RailPane] = _rail.Share,
        [ListPane] = _split.Share,
        [ModelPane] = _model.Share,
    };

    /// <summary>Puts back what a settings file remembered. Nulls leave the defaults.</summary>
    public void Show(
        IReadOnlyList<string>? columns,
        bool? rail = null,
        bool? model = null,
        IReadOnlyDictionary<string, int>? widths = null,
        IReadOnlyDictionary<string, double>? panes = null)
    {
        _wanted = columns is { Count: > 0 } ? columns : null;
        _railOpen = rail ?? _railOpen;
        _modelOpen = model ?? _modelOpen;
        _grid.Restore(widths);

        if (panes is not null)
        {
            Put(panes, RailPane, _rail);
            Put(panes, ListPane, _split);
            Put(panes, ModelPane, _model);
        }

        // WIRED HERE rather than where the fields are made, because a field initialiser cannot
        // see this window's own Changed - and all four of these write the same settings file.
        // Assigning the same method again on a second Show is harmless.
        _grid.Settled = Moved;
        _rail.Settled = Moved;
        _split.Settled = Moved;
        _model.Settled = Moved;

        // And the pane's own capture settings, which are written to the same file: the greying
        // factor decides what the export puts in a PNG, so a slider moved and not written down
        // is a set of icons that stops matching after a restart.
        if (Model is not null)
        {
            Model.Changed = Moved;
        }

        Layout();

        static void Put(IReadOnlyDictionary<string, double> panes, string name, PaneSplit split)
        {
            if (panes.TryGetValue(name, out double share))
            {
                split.Restore(share);
            }
        }
    }

    /// <summary>Somebody dragged a boundary or a column edge, and it came to rest.</summary>
    private void Moved() => Changed?.Invoke();

    /// <summary>
    /// Draws the tab, in the face a table of figures belongs in.
    /// </summary>
    /// <remarks>
    /// THE WHOLE BOOK IN THE MONOSPACE, which is a reading decision rather than a preference and
    /// was asked for from the live client. This window is a table of 2792 rows, a pane of
    /// percentages and ranges, and a pane of status lines - and the body face is a serif whose
    /// digits are OLD-STYLE, where a 6 stands taller than a 7 and a 2 hangs below the line. A
    /// column of those does not line up and does not scan; the monospace's digits are lining and
    /// all one width, which is the whole reason that face is in the atlas (see
    /// <see cref="OverlayFonts"/>). It costs nothing where there is no monospace on the machine:
    /// the push is a no-op and the book looks exactly as it did.
    ///
    /// THE MONSTER'S NAME STAYS IN THE SERIF, which is the one exception and a deliberate one.
    /// It is the only line here that is neither dense nor numeric, and it is what says which
    /// monster the two panes beside it are describing - see <see cref="Identity"/>, whose heading
    /// push lands inside this one and wins, as ImGui's font stack does.
    /// </remarks>
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
        MonsterVarieties all = table();
        StatDescriptions said = sentences();
        Read(all, said);

        if (_page.Count == 0)
        {
            ImGui.TextDisabled("No monster table loaded - see data/monster-varieties.json.");
            return;
        }

        // BEFORE ANYTHING IS DRAWN, because the row of controls goes over panes that do not exist
        // yet - see Measured. The panes below then walk the same chain for real.
        Edges edges = Measured();

        Header(all, said, edges);
        Filter();

        // ONE TEXT LINE HELD BACK FOR THE FOOTER, and handed to the GRIPS as well as to the panes.
        // A pane asking for the rest of the height would take that line too and put the footer
        // under the bottom of the window; so would a grip, which is the half that was missed the
        // first time and is invisible when it goes wrong - see PaneSplit.Bar.
        float tall = MathF.Max(
            1f, ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing());

        if (_railOpen)
        {
            float rail = _rail.Left();
            if (ImGui.BeginChild("##monster-rail", new Vector2(rail, tall), ImGuiChildFlags.Borders))
            {
                Rail();
            }

            ImGui.EndChild();

            _rail.Bar(tall);
        }

        float left = _split.Left();
        if (ImGui.BeginChild("##monster-list", new Vector2(left, tall), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar(tall);

        // THE DETAIL PANE GIVES UP WIDTH ONLY WHEN THE MODEL IS SHOWING. With the model folded
        // away it takes the rest, exactly as it did before there was a model at all.
        float detail = _modelOpen ? _model.Left() : 0f;
        if (ImGui.BeginChild("##monster-detail", new Vector2(detail, tall), ImGuiChildFlags.Borders))
        {
            Detail(all, said);
        }

        ImGui.EndChild();

        if (_modelOpen)
        {
            _model.Bar(tall);

            if (ImGui.BeginChild("##monster-model", new Vector2(0f, tall), ImGuiChildFlags.Borders))
            {
                Portrait(all);
            }

            ImGui.EndChild();
        }

        Footer(all, said, edges);
    }

    /// <summary>Where the panes below will end, as offsets from the left of the window's content.</summary>
    /// <param name="List">The right edge of the list pane, which Columns and Copy list hang from.</param>
    /// <param name="Detail">The right edge of the detail pane: where the query box stops and the footer ends.</param>
    /// <param name="Room">The whole width, which is the model pane's right edge and the window's.</param>
    private readonly record struct Edges(float List, float Detail, float Room);

    /// <summary>
    /// Works out where the panes will end, before any of them is drawn.
    /// </summary>
    /// <remarks>
    /// BECAUSE THE CONTROLS SIT OVER THE PANE EACH ONE BELONGS TO, which is how the live client
    /// drew this window: Facets at the left where the rail is, Columns and Copy list at the right
    /// edge of the list they act on, Model at the right edge of the model pane. That row is
    /// submitted first, so the widths have to be worked out rather than read back off the panes.
    ///
    /// THE SAME CHAIN THE PANES THEMSELVES WALK, and it has to stay that way: each boundary takes
    /// its share of what is LEFT after the one before it and its grip, so the two only agree while
    /// they are taken in the same order. <see cref="PaneSplit.Would"/> exists for this - asking
    /// <see cref="PaneSplit.Left"/> here instead would hand the drag a width measured somewhere
    /// else, and the boundary would then move at the wrong rate under the mouse.
    ///
    /// A DRAG IS A FRAME AHEAD OF THIS ROW, because a boundary moves while the panes are being
    /// drawn, which is after this. It is the same frame the model pane is behind a turn, and as
    /// invisible.
    ///
    /// WITH THE MODEL FOLDED AWAY the detail pane takes what is left, so its right edge becomes
    /// the window's own - which is what slides the box, the count and the footer out to the edge
    /// without any of them being told that a pane went away.
    /// </remarks>
    private Edges Measured()
    {
        float room = ImGui.GetContentRegionAvail().X;
        float grip = PaneSplit.Grip;

        float rest = room;
        var from = 0f;
        if (_railOpen)
        {
            float rail = _rail.Would(rest);
            rest -= rail + grip;
            from = rail + grip;
        }

        float list = _split.Would(rest);
        rest -= list + grip;

        float detail = _modelOpen ? _model.Would(rest) : rest;

        return new Edges(from + list, from + list + grip + detail, room);
    }

    /// <summary>
    /// The monster's model, filling its own pane.
    /// </summary>
    /// <remarks>
    /// HANDED BOTH SIDES OF THE PANE, and the portrait fits its square to the smaller of them less
    /// its own row and lines: the picture is square and a pane is not, and fitting it to the width
    /// alone would run a tall model off the bottom of a short pane, where there is no scrolling to
    /// rescue it - the drag that turns the model would fight the one that scrolls. Only the
    /// portrait knows how many lines it is about to write under the picture, so the fitting is its.
    /// </remarks>
    private void Portrait(MonsterVarieties all)
    {
        // NOT Possible: the portrait says that itself, and better - it knows whether what is
        // missing is the install or the renderer. Only a viewer that was never wired up at all is
        // this method's to answer for.
        if (Model is not { } model)
        {
            ImGui.TextDisabled("No model viewer attached.");
            return;
        }

        if (_chosen.Length == 0 || all.Find(_chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose a monster on the left.");
            return;
        }

        Vector2 room = ImGui.GetContentRegionAvail();
        model.Draw(one, _chosen, room.X, room.Y);
    }

    /// <summary>
    /// Works the table into columns, once.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE and not by count: the table is loaded at start-up today and read from
    /// the install a moment later, and two different tables can perfectly well hold the same number
    /// of monsters. A count would keep showing the old one.
    ///
    /// THE SENTENCES ARE PART OF THAT KEY because they are part of the searchable text, and they
    /// arrive LATE - the install's own wordings land on a background walk well after start-up. A
    /// book keyed on the monster table alone would keep the text it built from the shipped export
    /// for the rest of the session, so searching for "Block" would find nothing on exactly the
    /// machines that can word it best.
    /// </remarks>
    /// <summary>
    /// Selects a monster by its path, from outside the book.
    /// </summary>
    /// <remarks>
    /// FOR THE TO-DO LIST'S CLICK. Working through the boss pictures means finding one monster
    /// after another in a table of 2733 rows, by a name nobody wants to type twice - so the row
    /// that says which boss a map has opens it here instead. See BossIconRows.
    ///
    /// THE ROW NUMBER IS ALLOWED TO BE HIDDEN. A search or a range may be filtering the row out,
    /// and the grid returns the selection unchanged when it cannot see it (DataGrid.Draw), so the
    /// model pane shows the monster either way - it reads the PATH. The highlight comes back with
    /// the filter that hid it.
    /// </remarks>
    /// <returns>Whether the book has a row for that path.</returns>
    public bool Choose(string path)
    {
        if (path is not { Length: > 0 })
        {
            return false;
        }

        _chosen = path;
        _chosenRow = _page.Row(path);
        return _chosenRow >= 0;
    }

    private void Read(MonsterVarieties all, StatDescriptions said)
    {
        if (ReferenceEquals(_of, all) && ReferenceEquals(_said, said))
        {
            return;
        }

        _of = all;
        _said = said;
        _page = MonsterBook.Of(all, said);

        // THE SELECTION FOLLOWS THE PATH AND NOT THE ROW NUMBER. A new table is a different set of
        // rows in a different order, so the row somebody had chosen is very likely a different
        // monster in it - and the path is the one identity that survives a rebuild.
        _chosenRow = _page.Row(_chosen);

        // THE RANGES DO NOT SURVIVE, deliberately. A range is a pair of numbers that only means
        // anything against the distribution it was dragged out of, and a new table is a new
        // distribution - so keeping them would leave the list filtered by something nobody can see
        // the reason for any more. The columns DO survive: "show me armour" means the same thing
        // whatever the table says about it.
        _ranges.Clear();
        Layout();
    }

    /// <summary>Works out which columns are on, from what was chosen or from the defaults.</summary>
    private void Layout()
    {
        DataColumn[] all = _page.Store.Columns;
        _visible = new bool[all.Length];

        for (var at = 0; at < all.Length; at++)
        {
            _visible[at] = _wanted is null
                ? _page.Shown[at]
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
    /// The caption, the query box, what is wrong with it, and the row of controls under it.
    /// </summary>
    /// <remarks>
    /// STILL A SEARCH BOX TO ANYBODY WHO WANTS ONE. A bare word is a term of the grammar, so typing
    /// "undead" does exactly what it always did; tag:undead and life&gt;200 are there for whoever
    /// wants them. That is deliberate and it is the reason the grammar was written the way it was:
    /// a viewer that has to be learned before it answers anything gets opened once.
    ///
    /// THE CAPTION SAYS WHAT THE BOX IS AND THE HOVER SAYS WHAT IT TAKES, which is the arrangement
    /// the live client drew. The grammar used to be the box's own greyed hint, where it was only
    /// ever visible while the box was EMPTY - so it vanished at the first keystroke, which is
    /// exactly when somebody is wondering what else they may write.
    ///
    /// AND WHAT IS WRONG IS SHOWN WHERE IT IS WRONG. Halfway through typing a query is a broken
    /// query, so the box goes red and a caret points at the character the parser gave up on rather
    /// than the list simply emptying. The rows on screen are left ALONE while it is broken: they
    /// are the last answer somebody got, and blanking them for each keystroke of a longer query is
    /// how a search box starts to feel like it is fighting back.
    ///
    /// THE BOX STOPS WHERE THE MODEL PANE STARTS, and the row under it puts every control over the
    /// pane that control belongs to - see <see cref="Measured"/>, which is where the edges come
    /// from and why they are worked out rather than read back.
    /// </remarks>
    private void Header(MonsterVarieties all, StatDescriptions said, Edges edges)
    {
        ImGui.TextDisabled(Caption);

        bool wrong = _error.Length > 0;
        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search(
                "###monster-find", string.Empty, ref _query, QueryLength, edges.Room - edges.Detail))
        {
            _refilter = true;
        }

        Vector2 box = ImGui.GetItemRectMin();
        float below = ImGui.GetItemRectSize().Y;
        bool typing = ImGui.IsItemActive();

        if (wrong)
        {
            ImGui.PopStyleColor();
        }

        // NOT WHILE IT IS BEING TYPED IN. A tooltip follows the mouse, the mouse is over the box
        // that was just clicked, and a label that then sits there through the whole query is the
        // model pane's tooltip all over again - which is the one thing this window has already
        // been asked to take away.
        if (!typing && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Grammar);
        }

        // HALF A BUTTON'S HEIGHT BETWEEN THE BOX AND THE ROW, asked for from the live client. The
        // two are different kinds of thing - one is typed into, the others are pressed - and at
        // the ordinary item spacing they read as one block of controls.
        //
        // SET AS A POSITION RATHER THAN A SPACER, because a spacer gets the ordinary spacing on
        // BOTH sides of it and the number asked for is then not the number that appears: measured
        // headlessly at this font, Spacing() gives 8, a dummy of the right height gives 9, and
        // this gives the 9.5 that half a frame actually is.
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY()
            + MathF.Max(0f, (ImGui.GetFrameHeight() * 0.5f) - ImGui.GetStyle().ItemSpacing.Y));
        Controls(all, edges);
        Caret(box, below);
    }

    /// <summary>
    /// The row under the box: each control over the pane it acts on, and the count with the query.
    /// </summary>
    /// <remarks>
    /// THE ARRANGEMENT IS THE LIVE CLIENT'S AND SO IS THE RULE BEHIND IT. Facets at the far left,
    /// where the rail is or would be; Columns and Copy list at the right edge of the LIST they act
    /// on; Model at the right edge of the model pane, which is the window's own edge. A control
    /// folded away from its pane keeps the edge the pane left behind, because every edge here is
    /// the end of what is actually drawn rather than a remembered position.
    ///
    /// THE COUNT IS THE ONE THING THAT IS NOT A CONTROL, and it does not follow that rule: it is
    /// the ANSWER TO THE QUERY, so it sits at the query box's own right edge, directly under it.
    /// Put over the list it describes, it read as a label for the list rather than as a result -
    /// which is what sent this row back for a second try.
    /// </remarks>
    private void Controls(MonsterVarieties all, Edges edges)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float start = ImGui.GetCursorPosX();

        string count = $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{all.Count.ToString(CultureInfo.InvariantCulture)} monsters";

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
        Put(start + edges.List - Wide("Copy list", style) - Wide("Columns", style) - style.ItemSpacing.X);
        if (ImGui.Button("Copy list"))
        {
            // WHAT IS ON SCREEN and not the whole table: a filtered list is the answer somebody
            // worked out, and pasting it into a conversation about it is what this is for. In the
            // order the grid has it, so that a sort by life copies out sorted by life.
            DataColumn[] columns = _page.Store.Columns;
            ImGui.SetClipboardText(string.Join(
                '\n',
                _shown.Select(row =>
                    $"{_page.Paths[row]}\t{columns[0].Text[row]}\t{columns[1].Text[row]}")));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"The {_shown.Count} listed rows as path, name and type.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Columns"))
        {
            ImGui.OpenPopup("##monster-columns");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Which of the {_page.Store.Columns.Length} columns the table shows."
                + " Drag across a column's histogram to filter by it; right-click one to undo that.");
        }

        Chooser();

        // THE COUNT YIELDS TO THE BUTTON WHERE BOTH WANT THE SAME EDGE, and folding the model pane
        // away is exactly that: the detail pane then takes the rest, so the box's right edge and
        // the window's become one, and the count sat where the button goes. Put keeps items from
        // overlapping, so it pushed the button PAST THE CLIP RECTANGLE - measured headlessly at
        // 1998 to 2041 against a clip edge of 1990 - and the button was simply gone. That one is
        // worse than it sounds: it is the only way to bring the pane back, so losing it strands
        // whoever folded it. The button keeps the edge because it is a control and the count is
        // a reading; the count backs off by the button's width and no more.
        float model = Wide("Model", style);
        float counted = MathF.Min(edges.Detail, edges.Room - model - style.ItemSpacing.X);

        ImGui.SameLine();
        Put(start + counted - ImGui.CalcTextSize(count).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(count);

        ImGui.SameLine();
        Put(start + edges.Room - model);
        if (ImGui.Button("Model"))
        {
            _modelOpen = !_modelOpen;
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                _modelOpen
                    ? "Fold the model away and give its width back to the rest."
                    : "Show the monster's own 3D model, read out of the game's files.");
        }
    }

    /// <summary>
    /// The line under the panes: what the table holds, and the day it was last built.
    /// </summary>
    /// <remarks>
    /// MOVED OUT OF THE TOP, where it was four facts and a timestamp wedged between the query and
    /// the panes. None of it is read while somebody is searching - it is what the book IS, not
    /// what the query found - and the one number that IS an answer to the query, the monster
    /// count, stayed behind with the box.
    ///
    /// RIGHT-ALIGNED TO THE SAME EDGE AS THE BOX, so it ends where the model pane begins and does
    /// not run under a pane that carries its own lines. With the model folded away that edge is
    /// the window's, which is where this then sits.
    ///
    /// TWO HOVERS, AND BOTH ARE THE SAME KIND OF FACT: which sentences are in force, and which
    /// table. Each source has a shipped export and a live read that look identical on screen -
    /// right up to the handful GGG has reworded, or the league whose monsters have no name.
    /// </remarks>
    private void Footer(MonsterVarieties all, StatDescriptions said, Edges edges)
    {
        const string Between = "  |  ";

        string skills = $"{all.NamedSkills.ToString(CultureInfo.InvariantCulture)} named skills";
        string tags = $"{all.NamedTags.ToString(CultureInfo.InvariantCulture)} named tags";
        string wordings = $"{said.Count.ToString(CultureInfo.InvariantCulture)} stat wordings";
        string day = MonsterVarieties.MadeOn(all.Generated);
        string made = day.Length > 0 ? $"  *last updated: {day}" : string.Empty;

        float wide = ImGui.CalcTextSize(skills).X
            + (ImGui.CalcTextSize(Between).X * 2f)
            + ImGui.CalcTextSize(tags).X
            + ImGui.CalcTextSize(wordings).X
            + ImGui.CalcTextSize(made).X;

        float start = ImGui.GetCursorPosX();
        Put(start + edges.Detail - wide);

        ImGui.TextDisabled(skills);
        Piece(Between);
        Piece(tags);
        Piece(Between);
        Piece(wordings);
        if (ImGui.IsItemHovered())
        {
            Wordings(said);
        }

        if (made.Length > 0)
        {
            Piece(made);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    ImGuiText.Escape(all.Generated)
                    + "\n\nA stale table fails quietly - a new league's monsters simply have no name -"
                    + " so this says whether it is the shipped export or the install's own tables.");
            }
        }

        // THE JOIN COMES BEFORE EACH PIECE AND NOT AFTER IT, so the line ends on a piece rather
        // than on a dangling SameLine - and so nothing has to close it afterwards. Closing it with
        // NewLine was the first attempt and it cost a WHOLE BLANK LINE: ItemSize has already ended
        // the line by then, so NewLine takes the branch that adds a font-size gap, which is enough
        // to push the footer under the bottom of the window. Measured in a headless ImGui.
        //
        // NO SPACING IN THE JOIN, because the separators are already in the text. The pieces are
        // separate items only so that each can answer a hover of its own, and a gap between them
        // would make one line read as five.
        static void Piece(string text)
        {
            ImGui.SameLine(0f, 0f);
            ImGui.TextDisabled(text);
        }
    }

    /// <summary>Puts the next item at this x, and never back over the one before it.</summary>
    /// <remarks>
    /// THE CLAMP IS WHAT A NARROW WINDOW NEEDS. Every position in the row is an edge minus a
    /// width, and on a pane dragged small enough those go negative or run backwards - which in
    /// ImGui is two controls drawn on top of each other, where only the later one can be pressed.
    /// </remarks>
    private static void Put(float x) => ImGui.SetCursorPosX(MathF.Max(x, ImGui.GetCursorPosX()));

    /// <summary>How wide a button with this label is.</summary>
    private static float Wide(string label, ImGuiStylePtr style)
        => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f);

    /// <summary>
    /// How much of this table's modifier text the game can actually word, and why the rest is not.
    /// </summary>
    /// <remarks>
    /// MEASURED HERE BECAUSE THIS IS WHERE SOMEBODY IS LOOKING AT IT. A modifier that shows as
    /// "-50 monster_slain_flask_charges_granted_+%" reads as a gap in the tool, and the useful
    /// question is which KIND of gap: a stat the game words only as part of a group, which a
    /// modifier could fill because it holds every value in that group; or a stat with no sentence
    /// anywhere, which is engine bookkeeping and never had one. The first is work worth doing.
    ///
    /// BOTH SOURCES ANSWER IT - see <see cref="StatDescriptions.Shared"/>, which was the thing
    /// worth checking rather than assuming: the export's fourth column is the group, so a machine
    /// with no install reads the same split as one with it. The line still says which file is in
    /// force, because a reworded sentence is a different question from a missing one.
    /// </remarks>
    private void Wordings(StatDescriptions said)
    {
        if (!ImGui.BeginTooltip())
        {
            return;
        }

        try
        {
            ImGui.TextUnformatted(said.Source);

            MonsterBook.Wordings count = _page.Worded;
            if (count.Lines == 0)
            {
                return;
            }

            ImGui.Separator();
            ImGui.TextUnformatted(
                $"{count.Worded} of {count.Lines} modifier stat lines in this table are worded.");

            if (count.Shared > 0)
            {
                ImGui.TextColored(
                    OverlayInk.Warn,
                    $"{count.Shared} more are worded by the game only as part of a multi-stat"
                    + " group, which this build drops.");
            }

            ImGui.TextDisabled(
                $"{count.Silent} have no sentence anywhere - the engine's own bookkeeping.");
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    /// <summary>Points at the character the query stopped reading at, and says why.</summary>
    private void Caret(Vector2 box, float below)
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

        for (var at = 0; at < Rails.Length; at++)
        {
            (string label, string field) = Rails[at];
            if (!_facets.TryGetValue(field, out List<Facet>? facets) || facets.Count == 0)
            {
                continue;
            }

            // The first two open, the rest shut: five fields of twelve values each is sixty lines,
            // and the ones somebody opens are the ones they are asking about.
            if (!OverlayLayout.Subsection(label, openByDefault: at < 2))
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
                _query = ColumnQuery.Toggle(_query, field, facet.Value);
                _refilter = true;
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
    /// a fact about the monster table and not about this popup.
    /// </remarks>
    private void Chooser()
    {
        if (!ImGui.BeginPopup("##monster-columns"))
        {
            return;
        }

        try
        {
            DataColumn[] all = _page.Store.Columns;
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
                    if (!string.Equals(_page.Groups[at], group, StringComparison.Ordinal))
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
    private IEnumerable<string> Groups()
    {
        var said = new List<string>(8);
        foreach (string group in _page.Groups)
        {
            if (!said.Contains(group))
            {
                said.Add(group);
            }
        }

        return said;
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
        _errorAt = parsed.Column;

        // A BROKEN QUERY LEAVES THE LAST ANSWER ON SCREEN. Every query is broken while it is being
        // typed - "tag:" is halfway to something - and a list that emptied at each of those
        // keystrokes would flicker through nothing on the way to every answer.
        if (_error.Length > 0)
        {
            return;
        }

        _term = parsed.Term;

        // AND A FIELD THE TABLE HAS NOT GOT IS THE TABLE'S ANSWER, not the parser's: "bogus:thing"
        // reads perfectly well and only this table can say there is no such column.
        RowSet? rows = _page.Matching(_term, out string why);
        if (rows is null)
        {
            _error = why;
            _errorAt = 0;
            return;
        }

        _matched = rows;
        rows.CopyTo(_shown);

        // The grid sorts what it is given, and it has just been given a different list.
        _grid.Resort();

        Ranges();
        Counted();
    }

    /// <summary>Reads the query's ranges back out, so the histograms can light what it picked.</summary>
    private void Ranges()
    {
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

    /// <summary>Takes a range a drag picked out, by writing it into the query.</summary>
    /// <remarks>
    /// THIS RUNS EVERY FRAME WHILE THE DRAG IS HAPPENING, which is what makes the list move with
    /// the cursor - so a range that has not changed must cost nothing. Comparing the text it would
    /// produce is the cheapest way to ask, and it is exact.
    /// </remarks>
    private void Range(int column, double least, double most)
    {
        if (column < 0 || column >= _page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Ranged(_query, _page.Store.Columns[column].Name, least, most);
        if (string.Equals(made, _query, StringComparison.Ordinal))
        {
            return;
        }

        _query = made;
        _refilter = true;
    }

    private void Clear(int column)
    {
        if (column < 0 || column >= _page.Store.Columns.Length)
        {
            return;
        }

        string made = ColumnQuery.Drop(_query, _page.Store.Columns[column].Name);
        if (string.Equals(made, _query, StringComparison.Ordinal))
        {
            return;
        }

        _query = made;
        _refilter = true;
    }

    /// <summary>
    /// The list, drawn by a grid that knows nothing about monsters.
    /// </summary>
    /// <remarks>
    /// THE TWO THINGS THE GRID CANNOT WORK OUT FOR ITSELF are handed to it here: which rows are
    /// bosses - a flag the table carries that no column shows - and that hovering a row should
    /// print its path, which is this table's own key and the string somebody came to copy.
    ///
    /// KEPT IN FIELDS rather than written at the call, because a lambda written there is a new
    /// delegate every frame. They read <see cref="_page"/> when they are called rather than
    /// closing over the book that existed when they were made, so a table that arrives later is
    /// picked up without rebuilding them.
    /// </remarks>
    private void Grid()
    {
        _hooks ??= new DataGridHooks(
            Ink: row => _page.Boss[row] ? OverlayInk.Name : null,
            Hover: row => _page.Paths[row],
            Ranged: Range,
            Cleared: Clear);

        int chosen = _grid.Draw(
            "##monsters", _page.Store, _columns, _shown, _chosenRow, _ranges, _hooks);

        if (chosen == _chosenRow)
        {
            return;
        }

        _chosenRow = chosen;
        _chosen = chosen >= 0 && chosen < _page.Paths.Length ? _page.Paths[chosen] : string.Empty;
    }

    private void Detail(MonsterVarieties all, StatDescriptions said)
    {
        if (_chosen.Length == 0 || all.Find(_chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose a monster on the left.");
            return;
        }

        Identity(all, one);
        ImGui.Separator();

        // NOTHING IS PLACED BESIDE ANYTHING HERE ANY MORE, and that is the whole of what the model
        // pane bought. The picture used to sit in this pane's top right, which meant this method
        // had to know how wide the words below would come out BEFORE drawing them - a question
        // ImGui cannot answer in the right order, and every approximation of it was wrong in its
        // own way. The sections are simply a column again.
        Figures(one);
        Type(all, one);
        Words("Tags", all.TagsOf(one));
        Words("Skills", all.Skills(one));
        Mods(all, one, said);
        Words("Built on", one.Inherits ?? []);
    }

    /// <summary>
    /// Draws the monster's model, or null where the overlay did not wire one up.
    /// </summary>
    /// <remarks>
    /// SET FROM OUTSIDE rather than made here, because it needs the install to read and the
    /// renderer to upload to, and this window has neither - see EntityOverlay.AttachMonsterBook.
    /// Null is the ordinary case on a machine with no game installed, and the pane simply has no
    /// picture in it.
    /// </remarks>
    public MonsterPortrait? Model { get; set; }

    private void Identity(MonsterVarieties all, MonsterVariety one)
    {
        OverlayFonts.PushHeading();
        try
        {
            ImGui.TextUnformatted(one.Name is { Length: > 0 } named ? named : Tail(_chosen));
        }
        finally
        {
            OverlayFonts.PopHeading();
        }

        if (one.Name is not { Length: > 0 })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(the game gives this one no name)");
        }

        // The path used to push the monospace for itself - it is the one line here somebody reads
        // out and types back in, where 0/O and 1/l have to be different shapes. The whole window
        // is in that face now (see DrawTab), so the push would only be a second one.
        if (ImGui.Selectable($"{_chosen}###monster-path", false))
        {
            ImGui.SetClipboardText(_chosen);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The path an entity carries - the table's own key. Click to copy.");
        }

        if (one.Boss)
        {
            ImGui.TextColored(OverlayInk.Name, "boss");
        }

        Pair("blood", Named(all.BloodName(one), one.Blood), IdentityEms);

        if (one.Base is { Length: > 0 } built)
        {
            Pair("base", built, IdentityEms);
        }

        // A ROW NUMBER WITH NO NAME HERE, on purpose. The column holds a QuestFlags row and this
        // table carries no copy of that one - the flag's own state is read from the game at
        // runtime, by QuestWatch - so naming it from here would mean inventing the name. What it
        // does say is worth showing: the 68 monsters that carry one are the campaign bosses.
        // GREATER THAN ZERO, not merely non-zero: the install route spells "no quest flag" as -1,
        // because zero is a row of QuestFlags like any other. The export spells it as zero.
        if (one.Quest > 0)
        {
            Pair("quest flag", $"row {one.Quest.ToString(CultureInfo.InvariantCulture)}", IdentityEms);
        }
    }

    /// <summary>
    /// The plain figures, labelled only where the table settles what they mean.
    /// </summary>
    /// <remarks>
    /// See the class remarks: the percentages are percentages because 2733 rows say so, and the
    /// rest stay bare because nothing here can say what they are. Crit is shown as the kind it is.
    /// </remarks>
    private static void Figures(MonsterVariety one)
    {
        if (!OverlayLayout.Subsection("Figures", openByDefault: true))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            Pair("life", Percent(one.Life));
            Pair("damage", Percent(one.Damage));
            Pair("experience", Percent(one.Xp));
            Pair("model size", Percent(one.ModelSize));

            // NO UNIT, and that is the whole point of these four sitting apart from the four
            // above. Nothing measured says what they are counted in.
            Pair("attack speed", Count(one.AttackSpeed));
            Pair("movement speed", Count(one.Speed));
            Pair("size", Count(one.Size));
            Pair("poise", one.Poise.ToString("0.##", CultureInfo.InvariantCulture));
            Pair("attack range", $"{Count(one.MinAttack)} - {Count(one.MaxAttack)}");
            Pair("aggro range", $"{Count(one.MinAggro)} - {Count(one.MaxAggro)}");

            // NOT A PERCENTAGE AND NOT A CHANCE. AttackCrit holds 0, 1 or 2 across the whole
            // table - it names a kind - so it is shown as the number it is, with no unit
            // invented for it and no label that would read as one.
            Pair("crit kind", Count(one.Crit));

            // THE NAME OF AN ANIMATION, NOT A CODE TO BE DECODED, and the column says so itself:
            // beside the 618 rows reading "stance2" through "stance8" sit four reading
            // "TwoHandMace", "left", "right" and "default". It is free text out of the monster's
            // own .ao files, there is no Stances table in the game's schema to join it against,
            // and nothing in the data says what "stance2" looks like. So it is labelled for what
            // it is rather than dressed up as something a reader should be able to work out.
            //
            // AND THE Stance* MODIFIERS ARE NOT IT, which is the join somebody will reach for
            // next: 287 of the 2111 monsters with an EMPTY stance field carry one anyway, and the
            // 468 rows reading "stance2" spread over 53 different Stance* modifiers. They are two
            // unrelated uses of one word - the modifier is a movement-speed multiplier, this is
            // which animation set the model plays.
            if (one.Stance is { Length: > 0 } stance)
            {
                Pair("animation stance", stance);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "What the monster's own animation files call this pose. The game ships no"
                        + " table that translates it, and the column is free text - four monsters"
                        + " carry names like 'TwoHandMace' and 'left' instead of a number."
                        + " Empty on 2111 of 2733 rows.");
                }
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    private static void Type(MonsterVarieties all, MonsterVariety one)
    {
        MonsterKind? kind = all.Kind(one);
        string called = kind?.Id is { Length: > 0 } named ? named : Row(one.Type);

        if (!OverlayLayout.Subsection($"Type - {called}", openByDefault: true))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            if (kind is null)
            {
                ImGui.TextDisabled("This type row is not in the exported table.");
                return;
            }

            // THIS IS WHERE ARMOUR ACTUALLY LIVES - MonsterVarieties' own remark. The monster's
            // MonsterArmour column is filled on 16 rows of 2734; the real values are one join
            // away, on the type.
            Pair("armour", Percent(kind.Armour));
            Pair("evasion", Percent(kind.Evasion));
            Pair("energy shield", $"{Percent(kind.EnergyShield)} of life");
            Pair("damage spread", Percent(kind.Spread));
            Pair("summoned", kind.Summoned ? "yes" : "no");

            Resistances(all, one);
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// What the type resists, and - separately - what it is WEAK to.
    /// </summary>
    /// <remarks>
    /// THE TWO REVERSE WHAT A READER SHOULD DO, so they cannot share a heading. A monster carrying
    /// MinorColdVuln takes MORE cold damage, and listing that beside MajorFireResist under one word
    /// called "resistances" tells somebody the opposite of what is true. The names say which is
    /// which - see <see cref="ResistanceName"/>, and note that four of the nineteen profiles say
    /// neither, so they keep their own spelling under a heading that claims nothing.
    ///
    /// STILL NAMES AND NOT PERCENTAGES. Each profile also holds 32 numeric columns of tiers whose
    /// meaning this data does not settle; reading the NAME is not a step towards reading those.
    /// </remarks>
    private static void Resistances(MonsterVarieties all, MonsterVariety one)
    {
        // REUSED RATHER THAN ALLOCATED: the pane redraws every frame while a row stays picked, and
        // a monster carries at most a handful of profiles, so three lists that never grow again
        // after the first draw cost nothing. ImGui draws on one thread, which is what makes this
        // safe to hold in statics.
        List<string> resists = _resists;
        List<string> weak = _weak;
        List<string> quiet = _quiet;
        resists.Clear();
        weak.Clear();
        quiet.Clear();

        foreach (string name in all.ResistancesOf(one))
        {
            if (ResistanceName.Read(name) is not { } said)
            {
                quiet.Add(name);
                continue;
            }

            (said.Vulnerable ? weak : resists).Add(said.Said);
        }

        if (resists.Count > 0)
        {
            Pair("resists", string.Join(", ", resists));
        }

        if (weak.Count > 0)
        {
            ImGui.TextDisabled("vulnerable to");
            OverlayLayout.ToColumn();
            ImGui.TextColored(OverlayInk.Warn, ImGuiText.Escape(string.Join(", ", weak)));
        }

        if (quiet.Count > 0)
        {
            Pair("resistance profile", string.Join(", ", quiet));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "These profiles carry neither 'Resist' nor 'Vuln' in their name, so which way"
                    + " they point is not something the table says.");
            }
        }
    }

    private static void Words(string what, IEnumerable<string> said)
    {
        string[] all = [.. said];
        if (all.Length == 0)
        {
            return;
        }

        if (!OverlayLayout.Subsection($"{what} ({all.Length.ToString(CultureInfo.InvariantCulture)})"))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            foreach (string one in all)
            {
                ImGui.BulletText(ImGuiText.Escape(one));
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// Every modifier the monster carries, named where it can be and numbered where it cannot.
    /// </summary>
    /// <remarks>
    /// WALKED ROW BY ROW rather than through <see cref="MonsterVarieties.Modifiers"/>, which
    /// yields only the rows that resolved. Against the shipped table that is a distinction with
    /// no difference - all 2530 modifier rows on all 2733 monsters resolve, and the same holds
    /// for the skills, the tags, the types and the blood. It is the NEXT table this is for: the
    /// export is about to be replaced by a read of the install's own files, where a table can
    /// simply be missing, and a book that then showed four of a monster's seven modifiers would
    /// look complete and be wrong. The seven is what somebody comes here to count.
    /// </remarks>
    private static void Mods(MonsterVarieties all, MonsterVariety one, StatDescriptions said)
    {
        int[] rows = [.. Rows(one)];
        if (rows.Length == 0)
        {
            return;
        }

        if (!OverlayLayout.Subsection($"Modifiers ({rows.Length.ToString(CultureInfo.InvariantCulture)})"))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            foreach (int row in rows)
            {
                if (all.Modifier(row) is not { } mod)
                {
                    ImGui.BulletText(Row(row));
                    ImGui.SameLine();
                    ImGui.TextDisabled("(not in the exported table)");
                    continue;
                }

                ImGui.BulletText(ImGuiText.Escape(mod.Id));

                ImGui.Indent();
                try
                {
                    foreach (ModifierStat stat in mod.Stats ?? [])
                    {
                        Stat(stat, said);
                    }
                }
                finally
                {
                    ImGui.Unindent();
                }
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// One stat a modifier sets: the sentence the game words it with, or the raw fact.
    /// </summary>
    /// <remarks>
    /// THE RAW FACT IS NEVER LOST, it moves to the tooltip. A sentence is what somebody reads and
    /// the id is what they search for, cite and check an export against - "30  monster_base_block_%"
    /// is the thing that can be looked up, and dropping it the moment a wording exists would make
    /// this book less useful to the person most likely to open it.
    ///
    /// MONO AND TextUnformatted FOR THE RAW LINE, and that is not a style choice: 207 of these stat
    /// ids carry a PERCENT SIGN - maim_on_hit_%, monster_damage_+%_final_vs_monsters - and ImGui's
    /// Text calls are printf. Drawn with one of those, "%_f" is a conversion that reads an argument
    /// nobody passed and eats the characters behind it. See ImGuiText.
    /// </remarks>
    private static void Stat(ModifierStat stat, StatDescriptions said)
    {
        string raw = $"{stat.Range,10}  {stat.Stat}";

        if (stat.Worded(said) is not { Length: > 0 } sentence)
        {
            ImGuiText.Mono(raw);
            return;
        }

        ImGui.TextUnformatted(sentence);
        if (ImGui.IsItemHovered())
        {
            ImGuiText.MonoTooltip(raw);
        }
    }

    /// <summary>The three modifier columns as one run of rows, in the order the table holds them.</summary>
    private static IEnumerable<int> Rows(MonsterVariety one)
        => (one.Mods ?? []).Concat(one.Mods2 ?? []).Concat(one.SpecialMods ?? []);

    /// <param name="ems">
    /// How wide the label column is, or zero for the one the figures use.
    /// </param>
    private static void Pair(string what, string said, float ems = 0f)
    {
        if (said.Length == 0)
        {
            return;
        }

        ImGui.TextDisabled(what);

        if (ems > 0f)
        {
            OverlayLayout.ToColumn(ems);
        }
        else
        {
            OverlayLayout.ToColumn();
        }

        ImGui.TextUnformatted(said);
    }

    /// <summary>How wide the identity block's label column is, in ems.</summary>
    /// <remarks>
    /// NARROWER THAN THE FIGURES BELOW IT, because its labels are: "blood", "base" and "quest
    /// flag" against "movement speed" and "energy shield". One column for both put a hand's width
    /// of nothing after a five-letter word, which is what was reported. The separator between the
    /// two blocks is what makes two columns read as deliberate rather than as a misalignment.
    ///
    /// ToColumn only ever pushes right, so a label longer than this simply takes the room it
    /// needs - the number is a wish, not a clip.
    /// </remarks>
    private const float IdentityEms = 7f;

    /// <summary>A name with its row number kept beside it, or the bare row when there is no name.</summary>
    /// <remarks>
    /// EVERY FIELD HERE THAT IS A ROW NUMBER STAYS ONE - MonsterVarieties' own rule, and the
    /// reason is that the number is what a capture, a dump or the next export can be checked
    /// against. The name is the part that is easy to lose and easy to re-derive; the number is
    /// the part that is not.
    /// </remarks>
    private static string Named(string name, int row)
        => name.Length > 0
            ? $"{name}  ({Row(row)})"
            : Row(row);

    private static string Row(int row)
        => "#" + row.ToString(CultureInfo.InvariantCulture);

    private static string Percent(int value)
        => value.ToString(CultureInfo.InvariantCulture) + "%";

    private static string Count(int value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The last part of a metadata path, for the many monsters the game never names.</summary>
    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}
