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

    private readonly PaneSplit _rail = new(0.2f);
    private readonly PaneSplit _split = new(0.46f);
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

    /// <summary>Told when somebody changes the layout, so the choice can be kept.</summary>
    public Action? Changed { get; set; }

    /// <summary>Puts back what a settings file remembered. Nulls leave the defaults.</summary>
    public void Show(IReadOnlyList<string>? columns, bool? rail = null)
    {
        _wanted = columns is { Count: > 0 } ? columns : null;
        _railOpen = rail ?? _railOpen;
        Layout();
    }

    /// <summary>Draws the tab.</summary>
    public void DrawTab()
    {
        MonsterVarieties all = table();
        StatDescriptions said = sentences();
        Read(all, said);

        if (_page.Count == 0)
        {
            ImGui.TextDisabled("No monster table loaded - see data/monster-varieties.json.");
            return;
        }

        Header(all, said);
        Filter();

        if (_railOpen)
        {
            float rail = _rail.Left();
            if (ImGui.BeginChild("##monster-rail", new Vector2(rail, 0f), ImGuiChildFlags.Borders))
            {
                Rail();
            }

            ImGui.EndChild();

            _rail.Bar();
        }

        float left = _split.Left();
        if (ImGui.BeginChild("##monster-list", new Vector2(left, 0f), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar();

        if (ImGui.BeginChild("##monster-detail", new Vector2(0f, 0f), ImGuiChildFlags.Borders))
        {
            Detail(all, said);
        }

        ImGui.EndChild();
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
    /// The query box, what is wrong with it, and the two buttons.
    /// </summary>
    /// <remarks>
    /// STILL A SEARCH BOX TO ANYBODY WHO WANTS ONE. A bare word is a term of the grammar, so typing
    /// "undead" does exactly what it always did; tag:undead and life&gt;200 are there for whoever
    /// wants them. That is deliberate and it is the reason the grammar was written the way it was:
    /// a viewer that has to be learned before it answers anything gets opened once.
    ///
    /// AND WHAT IS WRONG IS SHOWN WHERE IT IS WRONG. Halfway through typing a query is a broken
    /// query, so the box goes red and a caret points at the character the parser gave up on rather
    /// than the list simply emptying. The rows on screen are left ALONE while it is broken: they
    /// are the last answer somebody got, and blanking them for each keystroke of a longer query is
    /// how a search box starts to feel like it is fighting back.
    /// </remarks>
    private void Header(MonsterVarieties all, StatDescriptions said)
    {
        float room = OverlayLayout.ButtonRoom("Copy list", "Columns", "Facets");
        bool wrong = _error.Length > 0;

        if (wrong)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Wrong);
        }

        if (OverlayLayout.Search(
                "###monster-find",
                "undead  ·  tag:undead life>200  ·  skill:fire  ·  not boss",
                ref _query,
                QueryLength,
                room))
        {
            _refilter = true;
        }

        Vector2 box = ImGui.GetItemRectMin();
        float below = ImGui.GetItemRectSize().Y;

        if (wrong)
        {
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();
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
            ImGui.OpenPopup("##monster-columns");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Which of the {_page.Store.Columns.Length} columns the table shows."
                + " Drag across a column's histogram to filter by it; right-click one to undo that.");
        }

        Chooser();

        ImGui.SameLine();
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

        ImGui.TextDisabled(
            $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{all.Count.ToString(CultureInfo.InvariantCulture)} monsters"
            + $"  |  {all.NamedSkills.ToString(CultureInfo.InvariantCulture)} named skills"
            + $"  |  {all.NamedTags.ToString(CultureInfo.InvariantCulture)} named tags");

        // WHERE THE TABLE ITSELF CAME FROM, which is now two possible answers rather than one: the
        // shipped export writes the date it was built, and the install's own tables say so in
        // words. A stale export and a live read look identical on screen otherwise, and the way an
        // export fails is that a league's new monsters simply have no name.
        if (all.Generated is { Length: > 0 } made)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  |  table: {ImGuiText.Escape(made)}");
        }

        // WHICH SENTENCES ARE IN FORCE, on hover rather than on the line, the same fact
        // AtlasWatch.ContentSource reports and for the same reason: the install's own wordings and
        // a six-month-old export look identical on screen right up to the handful GGG has reworded.
        ImGui.SameLine();
        ImGui.TextDisabled($"  |  {said.Count.ToString(CultureInfo.InvariantCulture)} stat wordings");
        if (ImGui.IsItemHovered())
        {
            Wordings(said);
        }

        Caret(box, below);
    }

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

        Portrait(one);

        // MEASURED AS ONE BLOCK, so the picture beside it knows how much room the words actually
        // want. A group is ImGui's own way to ask that: everything between these two calls counts
        // as a single item afterwards, and its rectangle is the bounding box of the lot. Nothing
        // here wraps - every section is BulletText, which simply runs on - so the box is the truth
        // rather than a guess at it.
        ImGui.BeginGroup();
        try
        {
            Figures(one);
            Type(all, one);
            Words("Tags", all.TagsOf(one));
            Words("Skills", all.Skills(one));
            Mods(all, one, said);
            Words("Built on", one.Inherits ?? []);
        }
        finally
        {
            ImGui.EndGroup();
        }

        // A FRAME BEHIND, which is the only order available: the picture is placed before the
        // words are drawn, because it has to sit beside their FIRST line rather than under their
        // last. Opening a section is one frame at the old width and then right - nobody sees it,
        // and the alternative is drawing the words twice.
        _column = ImGui.GetItemRectSize().X;
    }

    /// <summary>
    /// The monster's model, in the top right of the detail pane.
    /// </summary>
    /// <remarks>
    /// PLACED RATHER THAN FLOWED, because ImGui has no text flow: the picture is set at an
    /// absolute spot and the cursor is put back where it was, so everything after it draws down
    /// the left as though the picture were not there. That works because the block beside it is a
    /// column of key and value - Figures is the narrowest thing in the pane - and it is why the
    /// picture goes HERE rather than under the identity block, where the long lists would run
    /// beneath it.
    ///
    /// AND IT GIVES THE WIDTH BACK WHEN THERE IS NOT ENOUGH. Below a pane width where the picture
    /// and a readable column both fit, there is no picture at all - this window is read while
    /// playing, over a game that wants the screen, and a portrait that squeezed the numbers into
    /// two characters would be the wrong trade every time.
    /// </remarks>
    private void Portrait(MonsterVariety one)
    {
        if (Model is not { Possible: true } model)
        {
            return;
        }

        // THE SUM IS NOT DONE HERE, on purpose - see PortraitFit, and the cap of zero that got
        // itself shipped by living on this side of the line where no test could reach it.
        if (PortraitFit.Of(ImGui.GetContentRegionAvail().X, _column, model.Most) is not { Shown: true } fit)
        {
            return;
        }

        Vector2 was = ImGui.GetCursorPos();
        ImGui.SetCursorPos(was with { X = was.X + fit.Left });

        model.Draw(one, _chosen, fit.Side);

        // BACK TO WHERE THE PANE WAS, and to the TOP of it: the picture is taller than the
        // figures beside it, and leaving the cursor under it would push every later section down
        // by the height of a picture that is off to one side.
        ImGui.SetCursorPos(was);
    }

    /// <summary>
    /// How wide the words beside the picture were last frame.
    /// </summary>
    /// <remarks>
    /// MEASURED RATHER THAN RESERVED, which is the whole point. A fixed column is either too wide
    /// for a monster with short names - the gap somebody has to drag the window wider to afford -
    /// or too narrow for one with long ones. What the words want is a question ImGui can answer
    /// exactly, once they have been drawn; see the group in <see cref="Detail"/>.
    /// </remarks>
    private float _column;

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

        OverlayFonts.PushMono();
        try
        {
            if (ImGui.Selectable($"{_chosen}###monster-path", false))
            {
                ImGui.SetClipboardText(_chosen);
            }
        }
        finally
        {
            OverlayFonts.PopMono();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The path an entity carries - the table's own key. Click to copy.");
        }

        if (one.Boss)
        {
            ImGui.TextColored(OverlayInk.Name, "boss");
        }

        Pair("blood", Named(all.BloodName(one), one.Blood));

        if (one.Base is { Length: > 0 } built)
        {
            Pair("base", built);
        }

        // A ROW NUMBER WITH NO NAME HERE, on purpose. The column holds a QuestFlags row and this
        // table carries no copy of that one - the flag's own state is read from the game at
        // runtime, by QuestWatch - so naming it from here would mean inventing the name. What it
        // does say is worth showing: the 68 monsters that carry one are the campaign bosses.
        // GREATER THAN ZERO, not merely non-zero: the install route spells "no quest flag" as -1,
        // because zero is a row of QuestFlags like any other. The export spells it as zero.
        if (one.Quest > 0)
        {
            Pair("quest flag", $"row {one.Quest.ToString(CultureInfo.InvariantCulture)}");
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

    private static void Pair(string what, string said)
    {
        if (said.Length == 0)
        {
            return;
        }

        ImGui.TextDisabled(what);
        OverlayLayout.ToColumn();
        ImGui.TextUnformatted(said);
    }

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
