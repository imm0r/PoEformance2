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
/// THE SHELL - the box, the rail, the grid, the chooser - is <see cref="BookWindow{TBook}"/>, shared
/// with the item, tile and effect books. What is here is the monster's own: a detail pane beside
/// the list, a model pane beside that, a row of controls that sit over the panes they act on, and
/// a footer saying which table and which wordings are in force.
///
/// WHAT IS STILL MISSING, so that nobody has to rediscover it: a comparison of several pinned
/// monsters, and the tie to what is on screen in the game right now. Both are in
/// docs/reading-big-tables.md, stages four and five.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonsterBookWindow : BookWindow<MonsterBook>
{
    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// NOT EVERY FIELD A QUERY CAN NAME. A rail is for the questions with a handful of answers
    /// worth clicking - which tag, which type, which skill - and "name" has 994 distinct values
    /// over 2733 rows, which is a list of the table rather than a way into it.
    /// </remarks>
    private static readonly (string Label, string Field)[] Offered =
    [
        ("Tags", "tag"),
        ("Types", "type"),
        ("Skills", "skill"),
        ("Modifiers", "mod"),
        ("Blood", "blood"),
    ];

    /// <summary>Scratch for <see cref="Resistances"/>, held so the pane does not allocate per frame.</summary>
    private static readonly List<string> _resists = [];
    private static readonly List<string> _weak = [];
    private static readonly List<string> _quiet = [];

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

    private readonly Func<MonsterVarieties> _table;
    private readonly Func<StatDescriptions> _sentences;

    /// <summary>The table the book was built from, to notice when a different one arrives.</summary>
    private MonsterVarieties _of = MonsterVarieties.Empty;

    /// <summary>And the sentences it was built with, which arrive later than the table does.</summary>
    private StatDescriptions _said = StatDescriptions.Empty;

    private MonsterBook _page = MonsterBook.Empty;

    /// <summary>
    /// Whether the monster's model window is open - kept in the settings as the model pane's switch was.
    /// </summary>
    /// <remarks>
    /// A WINDOW NOW, NOT A PANE: the picture shared the book's width with the list and the details,
    /// and was asked for in a window of its own from the live client - see MonsterPortrait.Show.
    /// The button at the window's right edge opens and closes it; choosing a monster opens it.
    /// </remarks>
    private bool _modelOpen = true;

    /// <summary>Where this frame's panes will end, worked out before the controls over them are drawn.</summary>
    private Edges _edges;

    public MonsterBookWindow(Func<MonsterVarieties> table, Func<StatDescriptions> sentences)
        : base("monster", MonsterBook.Empty, 0.2f, 0.46f)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sentences);
        _table = table;
        _sentences = sentences;
    }

    /// <summary>Whether the model's window is open, for whoever writes the settings file.</summary>
    public bool ModelOpen => _modelOpen;

    /// <inheritdoc/>
    protected override string Caption => "Search for any monster";

    /// <inheritdoc/>
    protected override string Grammar => "undead  ·  tag:undead life>200  ·  tag:\"beast\"  ·  skill:fire  ·  not boss";

    /// <inheritdoc/>
    protected override string Noun => "monsters";

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Field)> Rails => Offered;

    /// <summary>
    /// The first two open, the rest shut: five fields of twelve values each is sixty lines, and the
    /// ones somebody opens are the ones they are asking about.
    /// </summary>
    protected override int OpenRails => 2;

    /// <summary>One text line held back for the footer.</summary>
    protected override float Reserved => ImGui.GetTextLineHeightWithSpacing();

    /// <summary>Puts back what a settings file remembered, the model pane's own setting included.</summary>
    public void Show(
        IReadOnlyList<string>? columns,
        bool? rail,
        bool? model,
        IReadOnlyDictionary<string, int>? widths = null,
        IReadOnlyDictionary<string, double>? panes = null)
    {
        _modelOpen = model ?? _modelOpen;
        Show(columns, rail, widths, panes);
    }

    /// <summary>
    /// The third boundary, and the pane's own capture settings, which are written to the same file.
    /// </summary>
    /// <remarks>
    /// The greying factor decides what the export puts in a PNG, so a slider moved and not written
    /// down is a set of icons that stops matching after a restart. This book owns those settings;
    /// the other books' panes take the same look and do not write it back.
    /// </remarks>
    protected override void Wired()
    {
        if (Model is not null)
        {
            Model.Changed = Moved;
            Model.WindowOpen = _modelOpen;
            Model.WindowChanged = open =>
            {
                _modelOpen = open;
                Moved();
            };
        }
    }

    /// <summary>
    /// Works the table into columns, once.
    /// </summary>
    /// <remarks>
    /// THE SENTENCES ARE PART OF THE KEY because they are part of the searchable text, and they
    /// arrive LATE - the install's own wordings land on a background walk well after start-up. A
    /// book keyed on the monster table alone would keep the text it built from the shipped export
    /// for the rest of the session, so searching for "Block" would find nothing on exactly the
    /// machines that can word it best.
    /// </remarks>
    protected override MonsterBook Current()
    {
        MonsterVarieties all = _table();
        StatDescriptions said = _sentences();
        if (!ReferenceEquals(_of, all) || !ReferenceEquals(_said, said))
        {
            _of = all;
            _said = said;
            _page = MonsterBook.Of(all, said);
        }

        return _page;
    }

    /// <inheritdoc/>
    protected override string WhyEmpty() => "No monster table loaded - see data/monster-varieties.json.";

    /// <inheritdoc/>
    protected override Vector4? Ink(int row) => Page.Boss[row] ? OverlayInk.Name : null;

    /// <summary>Where the panes below will end, as offsets from the left of the window's content.</summary>
    /// <param name="List">The right edge of the list pane, which Columns and Copy list hang from.</param>
    /// <param name="Detail">The right edge of the detail pane: where the query box stops and the footer ends.</param>
    /// <param name="Room">The whole width, the window's.</param>
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
        if (RailOpen)
        {
            float rail = RailSplit.Would(rest);
            rest -= rail + grip;
            from = rail + grip;
        }

        float list = ListSplit.Would(rest);
        rest -= list + grip;

        return new Edges(from + list, from + list + grip + rest, room);
    }

    /// <summary>
    /// The caption, the query box, what is wrong with it, and the row of controls under it.
    /// </summary>
    /// <remarks>
    /// MEASURED BEFORE ANYTHING IS DRAWN, because the row of controls goes over panes that do not
    /// exist yet - see <see cref="Measured"/>. The panes below then walk the same chain for real.
    ///
    /// THE BOX STOPS WHERE THE MODEL PANE STARTS, and what is wrong is shown where it is wrong: a
    /// caret under the character the parser gave up on rather than the list simply emptying.
    /// </remarks>
    protected override void Header()
    {
        _edges = Measured();

        ImGui.TextDisabled(Caption);
        (Vector2 box, float below) = SearchBox(_edges.Room - _edges.Detail);

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
        Controls(_edges);
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
    private void Controls(Edges edges)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float start = ImGui.GetCursorPosX();
        string count = Counted();

        FacetsButton();

        ImGui.SameLine();
        Put(start + edges.List - Wide("Copy list", style) - Wide("Columns", style) - style.ItemSpacing.X);
        if (ImGui.Button("Copy list"))
        {
            // WHAT IS ON SCREEN and not the whole table: a filtered list is the answer somebody
            // worked out, and pasting it into a conversation about it is what this is for. In the
            // order the grid has it, so that a sort by life copies out sorted by life.
            DataColumn[] columns = Page.Store.Columns;
            ImGui.SetClipboardText(string.Join(
                '\n',
                Shown.Select(row =>
                    $"{Page.Paths[row]}\t{columns[0].Text[row]}\t{columns[1].Text[row]}")));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"The {Shown.Count} listed rows as path, name and type.");
        }

        ImGui.SameLine();
        ColumnsButton();

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
            if (Model is not null)
            {
                Model.WindowOpen = _modelOpen;
            }

            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                _modelOpen
                    ? "Close the model's window."
                    : "Open the monster's own 3D model, read out of the game's files, in a window of its own.");
        }
    }

    /// <summary>
    /// The pane beside the list: the monster's details, with its model's switches under its name - the model itself has a window of its own.
    /// </summary>
    protected override void Pane() => Detail();

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
    protected override void Footer()
    {
        const string Between = "  |  ";
        MonsterVarieties all = _of;
        StatDescriptions said = _said;

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
        Put(start + _edges.Detail - wide);

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

            MonsterBook.Wordings count = Page.Worded;
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

    private void Detail()
    {
        string chosen = Chosen;
        if (chosen.Length == 0 || _of.Find(chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose a monster on the left.");
            return;
        }

        Identity(_of, one, chosen);
        ModelTools(one, chosen, one.Name is { Length: > 0 } named ? named : Tail(chosen));
        ImGui.Separator();

        // NOTHING IS PLACED BESIDE ANYTHING HERE ANY MORE, and that is the whole of what the model
        // pane bought. The picture used to sit in this pane's top right, which meant this method
        // had to know how wide the words below would come out BEFORE drawing them - a question
        // ImGui cannot answer in the right order, and every approximation of it was wrong in its
        // own way. The sections are simply a column again.
        Figures(one);
        Type(_of, one);
        Words("Tags", _of.TagsOf(one));
        Words("Skills", _of.Skills(one));
        Mods(_of, one, _said);
        Words("Built on", one.Inherits ?? []);
    }

    private static void Identity(MonsterVarieties all, MonsterVariety one, string chosen)
    {
        OverlayFonts.PushHeading();
        try
        {
            ImGui.TextUnformatted(one.Name is { Length: > 0 } named ? named : Tail(chosen));
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
        // is in that face now (see BookWindow.DrawTab), so the push would only be a second one.
        if (ImGui.Selectable($"{chosen}###monster-path", false))
        {
            ImGui.SetClipboardText(chosen);
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
