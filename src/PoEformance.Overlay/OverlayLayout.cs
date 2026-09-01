using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The shapes a settings page is built out of, so every page is built out of the same ones.
/// </summary>
/// <remarks>
/// THE PROBLEM THIS FIXES IS NOT UGLINESS, IT IS THAT NOTHING LINES UP. Every page here was
/// laid out by hand with ImGui's primitives, and the primitives take a width per call - so
/// nineteen different widths were in force across the tool, from <c>GetFontSize() * 4</c> to
/// <c>GetFontSize() * 28</c>, each one chosen by eye on the day its page was written. ImGui
/// puts a control's label to the RIGHT of the control, so the width decides where the label
/// starts: nineteen widths are nineteen label columns, and a page with four settings on it had
/// its four labels starting at four different places. That is what reads as chaos. It is not a
/// taste problem and it cannot be fixed by choosing nicer colours.
///
/// So the width stops being a decision available at the call site. Every value control below
/// takes the SAME width from <see cref="FieldWidth"/>, which means every label on every page
/// starts at the same distance from the left margin, which means the eye can run down a page
/// instead of hunting across it.
///
/// MEASURED IN TEXT, NOT IN THE WINDOW. The obvious alternative - stretch each control to the
/// window's right edge and hang the label off that - is what the tool being copied here does,
/// and it is wrong for this tool: these windows are RESIZED CONSTANTLY, because the entity
/// browser and the dissector want a wide window and nobody wants one over their whole game. A
/// full-width field means every label in the tool moves when the window is dragged, and a
/// 1400-pixel slider for a number between 1 and 10. Fixed in <c>em</c> keeps the column still
/// while the window changes size around it, and it follows the text size - which is a setting
/// somebody can change - because a column measured in pixels is a column that is wrong at
/// every size but one.
///
/// THE THIRD RULE IS THAT PROSE IS NOT A CONTROL. Explanations here used to arrive three ways:
/// baked into the label ("Hide noise  (effects, pets, daemons - off to see everything)"), as a
/// wrapped paragraph in the middle of the controls, or as a paragraph with a button stuck to
/// its last line with <c>SameLine</c> - which puts the button wherever that paragraph happened
/// to wrap. All three make the column of controls ragged, and the first one makes it ragged
/// permanently, since a long label pushes nothing but still has to be read past. <see
/// cref="Note"/> puts an explanation on its own line under what it explains, and <see
/// cref="Hint"/> puts it in a tooltip - which is where an aside about a control belongs when
/// the control's name already says what it does.
///
/// WHAT THIS IS NOT: a widget library. Every method here is ImGui's own control with the
/// layout decided beforehand, and it returns exactly what ImGui returned. Nothing is wrapped
/// that does not need its width setting, and nothing here draws anything ImGui could not.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OverlayLayout
{
    /// <summary>
    /// How wide a value control is, in multiples of the body text size.
    /// </summary>
    /// <remarks>
    /// TWENTY, which is about a dozen digits of the mono face plus a slider worth dragging. The
    /// number itself is a judgement, but it only has to be made once - what matters is that
    /// there is one of it. Wide enough that a percentage slider has travel and a path field
    /// shows a filename; narrow enough that the label column is still on screen when somebody
    /// has the window at its smallest.
    /// </remarks>
    private const float FieldEms = 20f;

    /// <summary>How far a note or a dependent control steps in, in multiples of the text size.</summary>
    /// <remarks>
    /// A LINE'S WORTH, the same step <see cref="PoEformance.Features.InterfaceMetrics"/> settled
    /// on for the same reason: a step measured in the thing being stepped reads as a step at
    /// every text size, where a fixed 26 pixels - which is what this file replaces, hard-coded
    /// in the style rows - is a shove at 12 and invisible at 30.
    /// </remarks>
    private const float StepEms = 1f;

    /// <summary>
    /// How wide a control is when it SHARES its line with others.
    /// </summary>
    /// <remarks>
    /// THE SECOND SORT OF ROW, and the reason one width was not enough. Some rows are a single
    /// setting with its name beside it; others are several controls that only mean anything
    /// together - a rule's "monster count &lt; 5 within 50m", a marker's size and line width,
    /// a colour's four channels. Those cannot all be twenty lines wide, and the tool proved it:
    /// its rule editor alone had 4, 5, 5.5, 6, 6.5, 8.5 and 20 in one file, so no two rows of
    /// the same editor lined up with each other.
    ///
    /// SIX, and the point again is that there is one of it. Two controls at six plus their
    /// labels fit the field column; four still fit an ordinary window. What it buys is that a
    /// stack of these rows is a GRID - the second control of every row starts where the second
    /// control of the row above it started - which is what the colour tables in the tool being
    /// copied here get right and what this tool got wrong every time.
    /// </remarks>
    private const float CompactEms = 6f;

    /// <summary>The width every single-setting control on every page is drawn at.</summary>
    /// <remarks>
    /// Shrinks, and only shrinks, when the window is too narrow to hold the field and its
    /// label. A field that kept its full width in a narrow window would push its own label off
    /// the right edge, which is the one failure worse than a label in the wrong column - the
    /// setting would have no name at all. The floor is eight lines' worth: below that a slider
    /// has no travel and the control is a decoration.
    /// </remarks>
    public static float FieldWidth()
    {
        float em = ImGui.GetFontSize();
        float room = ImGui.GetContentRegionAvail().X - (em * 8f);
        return Math.Max(em * 8f, Math.Min(em * FieldEms, room));
    }

    /// <summary>The width a control takes when several share one line.</summary>
    public static float CompactWidth() => ImGui.GetFontSize() * CompactEms;

    /// <summary>One step in, for a note or for a control that depends on the one above it.</summary>
    public static float Step() => ImGui.GetFontSize() * StepEms;

    /// <summary>
    /// The switch a whole feature hangs off, drawn the same way on every page that has one.
    /// </summary>
    /// <remarks>
    /// FIRST LINE, ALWAYS. Half the pages in this tool have one setting that decides whether the
    /// other twenty do anything, and it used to be an ordinary checkbox somewhere among them -
    /// so "is this feature even on" was a question you answered by reading the whole page and
    /// hoping you recognised the right line. Its own row at the top, with the state spelled out
    /// beside it and a rule under it, answers that without reading anything.
    ///
    /// NOT IN THE HEADING FACE, which is a correction. It was, on the reasoning that this line
    /// outranks everything under it - but a heading is TEXT, and this is a CONTROL. Scaling the
    /// face scales the checkbox with it, so the page opened with one tick box visibly larger
    /// than every other tick box on it, which reads as a rendering fault rather than as
    /// hierarchy. Position, the state beside it and the rule under it carry the rank; they are
    /// what a reader is using anyway.
    ///
    /// The state is written out as well as ticked because a tick is a shape and this is the one
    /// place the answer has to survive a glance at a moving screen.
    /// </remarks>
    /// <returns>Whether the switch was just changed.</returns>
    public static bool Master(string label, ref bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        bool state = on;
        bool changed = ImGui.Checkbox(label, ref state);

        ImGui.SameLine();
        ImGui.TextColored(state ? OnInk : OffInk, state ? "on" : "off");

        on = state;

        // A rule under it rather than a gap, so the switch reads as a lid on the page rather
        // than as its first setting.
        ImGui.Separator();
        ImGui.Spacing();
        return changed;
    }

    /// <summary>What "on" and "off" are written in beside a master switch.</summary>
    private static readonly Vector4 OnInk = OverlayInk.Good;
    private static readonly Vector4 OffInk = OverlayInk.Quiet;

    /// <summary>
    /// A fold INSIDE a section, drawn so it cannot be mistaken for the section around it.
    /// </summary>
    /// <remarks>
    /// THE LEVEL THE TOOL COULD NOT SHOW. A page folds into sections - those are the bars the
    /// tab bar hands out, drawn framed, in the heading face, banded every other one. Several
    /// tools then fold AGAIN inside their own section: the tracker has six of these, the marker
    /// styles one per group, the appearance page three. Every one of them was drawn with the
    /// same call as the bar containing it, so "Tracker" and "Status effects" were the same
    /// widget in the same colour at the same size, and nothing on screen said one was inside
    /// the other. On the combat page that put four levels of hierarchy on screen wearing two
    /// appearances.
    ///
    /// SO IT LOOKS DIFFERENT IN TWO WAYS AT ONCE, because one is not enough to read at a
    /// glance: no frame behind it - an arrow and a label rather than a filled bar - and the
    /// body face rather than the heading one. Either alone could be mistaken for a section that
    /// happened to be styled oddly; together they read as something smaller.
    ///
    /// NoTreePushOnOpen because the caller draws the contents itself and would otherwise owe a
    /// TreePop - a pairing that leaks on whichever branch nobody tested, exactly like the font
    /// stack this file's Heading helper exists to protect.
    /// </remarks>
    /// <param name="openByDefault">
    /// Whether it greets a first-time reader open. Only the FIRST time: after that it is
    /// ImGui's own memory of what they last had open, which is the right answer and not ours
    /// to overrule.
    /// </param>
    /// <returns>Whether it is open, and so whether to draw its contents.</returns>
    public static bool Subsection(string label, bool openByDefault = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        ImGui.SetNextItemOpen(openByDefault, ImGuiCond.FirstUseEver);
        return ImGui.TreeNodeEx(
            label,
            ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
    }

    /// <summary>
    /// Splits one tool's own settings into pages, when it has too many to stack.
    /// </summary>
    /// <remarks>
    /// THE ANSWER TO A TOOL THAT DOES NOT FIT. Folding helps a page with several tools on it;
    /// it does not help a single tool with sixty settings, because those settings are all
    /// ITS - folded away they are just as far off as before, and unfolded they are a scroll.
    /// The tracker is the case: four subjects, and reading any one of them meant scrolling
    /// past the other three.
    ///
    /// Tabs cut that to what is being worked on. They are also the one arrangement here that
    /// cannot be confused with anything else on screen: nothing else in a page's body is a row
    /// of clickable headings.
    ///
    /// WHOLE TABS AS CALLBACKS rather than a Begin/End pair offered to the caller. An
    /// unbalanced pair is a corrupt ImGui stack and an assert that takes the process down, and
    /// the shape that prevents it is the same one <see cref="OverlayFonts.Heading"/> uses. The
    /// array and its closures are built per frame, which is a real cost and not one that
    /// matters here: a settings page is drawn only while somebody is looking at it, and
    /// somebody looking at a settings page is not in a fight.
    /// </remarks>
    /// <param name="id">The bar's ImGui id, unique within its window.</param>
    /// <param name="tabs">Each tab's heading and what to draw inside it.</param>
    public static void Tabs(string id, params (string Label, Action Draw)[] tabs)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(tabs);

        if (!ImGui.BeginTabBar(id, ImGuiTabBarFlags.FittingPolicyScroll))
        {
            return;
        }

        try
        {
            foreach ((string label, Action draw) in tabs)
            {
                // ImGui.NET only exposes the flags overload together with the ref-bool that
                // puts a close button on every tab, and these tabs must not have one - a
                // closed tab needs a list somewhere to reopen it from. BeginTabItem's plain
                // overload is the one without it.
                if (!ImGui.BeginTabItem(label))
                {
                    continue;
                }

                try
                {
                    // Its own id scope per tab, so two tabs holding a control of the same
                    // name - and several hold a "filter" - do not share one ImGui id, which
                    // would be one scroll position and one open state between them.
                    ImGui.PushID(label);
                    try
                    {
                        ImGui.Spacing();
                        draw();
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
                finally
                {
                    ImGui.EndTabItem();
                }
            }
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// A named block of related settings, which is NOT a section and must not look like one.
    /// </summary>
    /// <remarks>
    /// THE TOOL HAD THREE LEVELS OF HEADING AND ONE APPEARANCE FOR ALL OF THEM. A tab, a
    /// collapsing header and a titled rule were the same face at the same size, so a page's
    /// structure was invisible: nothing on screen said which of two headings contained the
    /// other. That is most of what "not logical" means about a page - the hierarchy is real in
    /// the code and absent on screen.
    ///
    /// So the levels are told apart by SHAPE, not by wording. A collapsing header is a bar you
    /// can fold. This is a label with a rule under it that folds nothing - a few settings that
    /// belong together inside a section that is already open. Being unfoldable is the point:
    /// a fold offers a choice, and a choice about four checkboxes is a click asked for nothing.
    /// </remarks>
    /// <param name="hint">
    /// What the block is, when that needs saying. Drawn as a marker beside the title with the
    /// sentence on hover - see <see cref="Hint"/> for why an explanation read once belongs
    /// there rather than as a paragraph the block has to be read past every time.
    /// </param>
    public static void Group(string title, string? hint = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.Accent);
        try
        {
            ImGui.TextUnformatted(title);
        }
        finally
        {
            ImGui.PopStyleColor();
        }

        if (hint is { Length: > 0 })
        {
            // The MARKER carries the hover, not the title. A heading that reacts to the mouse
            // reads as something you can click, and there is nothing here to click; a small
            // quiet mark beside it says "there is more about this" without making the same
            // promise.
            ImGui.SameLine();
            ImGui.TextColored(OverlayInk.Quiet, "(?)");
            Hint(hint);
        }

        ImGui.Separator();
    }

    /// <summary>
    /// An explanation, on its own line, under whatever it explains.
    /// </summary>
    /// <remarks>
    /// INDENTED AND ON ITS OWN LINE, which is the whole difference from what this replaces.
    /// A wrapped paragraph sitting flush in the middle of a column of controls reads as another
    /// control; stepped in and set quiet, it reads as an aside about the line above it, and the
    /// column of controls stays a column.
    ///
    /// It is also the only shape here that may follow a control with <c>SameLine</c> - it may
    /// not. A paragraph wraps at the window's edge, so anything placed after it lands wherever
    /// the last line happened to end, which moves when the window is resized. That is how this
    /// tool ended up with a "reset" button whose position depended on how wide somebody had
    /// dragged the window.
    /// </remarks>
    public static void Note(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        float step = Step();
        ImGui.Indent(step);
        try
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, text);
        }
        finally
        {
            ImGui.Unindent(step);
        }
    }

    /// <summary>
    /// A warning about the line above it: something the feature cannot do, or has not done.
    /// </summary>
    /// <remarks>
    /// THE THIRD KIND OF PROSE, and the one that stops <see cref="Hint"/> from being the answer
    /// to everything. Most explanations belong in a tooltip because they are read once. Some do
    /// not: "IT DOES NOT COVER EVERY HAZARD", "that value is a CANDIDATE, not a measurement",
    /// "that file did not load". Those bound what the switch above can be TRUSTED to do, and a
    /// limit nobody sees is a limit that gets discovered as a bug.
    ///
    /// So the test is not how long the text is, it is what happens if it is never read: an
    /// unread explanation costs a moment of confusion, an unread warning costs a wrong
    /// conclusion about the game.
    ///
    /// SHOW IT ONLY WHEN IT APPLIES. A caveat about what the rings cover is worth a line while
    /// the rings are being drawn and is noise while they are switched off - and a page whose
    /// warnings are always on screen teaches people to read past all of them, which costs the
    /// one that mattered.
    /// </remarks>
    public static void Warning(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        float step = Step();
        ImGui.Indent(step);
        try
        {
            ImGuiText.Wrapped(OverlayInk.Warn, ImGuiText.Escape(text));
        }
        finally
        {
            ImGui.Unindent(step);
        }
    }

    /// <summary>
    /// The same explanation, in a tooltip on the line just drawn.
    /// </summary>
    /// <remarks>
    /// FOR THE ASIDE THAT USED TO LIVE IN THE LABEL. "Hide noise  (effects, pets, daemons - off
    /// to see everything)" is a checkbox called "Hide noise" and a sentence about it, welded
    /// together because there was nowhere else to put the sentence. Welded, the sentence is
    /// read every single time the page is opened, by somebody who learned what the setting does
    /// months ago - and it is what makes a page of eight checkboxes eight paragraphs long.
    ///
    /// A tooltip is read once, by the person who needs it, at the moment they need it.
    ///
    /// IT MUST BE THE LAST THING ASKED ABOUT THE LINE ABOVE IT. A tooltip is a window, and
    /// drawing one leaves ImGui's "last item" pointing at the text inside that window - so any
    /// <c>IsItemDeactivatedAfterEdit</c>, <c>IsItemActive</c> or <c>IsItemHovered</c> placed
    /// after this call is asking about the tooltip's own prose rather than about the control.
    /// The symptom is a control that works until somebody leaves the pointer on it, which is
    /// the one case everybody does.
    /// </remarks>
    public static void Hint(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Nothing to say is not the same as an empty tooltip, and the difference shows: a
        // caller whose sentence is built from what it found - the atlas groups describe what
        // each one matches on, and a group matching nothing describes nothing - would otherwise
        // pop an empty box under the pointer.
        if (text.Length == 0 || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        // A width on the tooltip, because ImGui sizes one to its longest line and a sentence
        // with no wrap point becomes a tooltip wider than the game.
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26f);
        try
        {
            ImGui.SetTooltip(ImGuiText.Escape(text));
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// One headline figure: its name, its value, and its sentence under the pointer.
    /// </summary>
    /// <remarks>
    /// THE SHAPE THAT REPLACES A PARAGRAPH OF NUMBERS. Three pages here open with figures that
    /// only mean something next to each other - the damage split, what a purse is holding against
    /// what it made - and all three wrote them as prose: "credited: 891.4k from ones we were
    /// already hurting", four such lines, pushing the thing they introduce a screen down to
    /// explain something that is read once and then known.
    ///
    /// What belongs on screen is the COMPARISON - which of these is large - and what belongs in a
    /// tooltip is the sentence saying what the number rests on. Set in <see cref="Cell"/> columns
    /// they also line up down the page, which is what makes two of them comparable at a glance.
    ///
    /// THE NAME AND THE VALUE ARE ONE HOVER TARGET. A BeginGroup pair makes them a single item as
    /// far as <c>IsItemHovered</c> is concerned, because a pointer lands on whichever half it
    /// lands on and a tooltip answering to only one of them is a tooltip nobody finds.
    /// </remarks>
    /// <param name="hint">The sentence about it. Pass empty for a figure that needs none.</param>
    public static void Figure(string label, string value, Vector4 ink, string hint = "")
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(hint);

        ImGui.BeginGroup();
        ImGui.TextColored(OverlayInk.Quiet, ImGuiText.Escape(label));
        ImGui.SameLine();
        ImGuiText.Mono(ink, value);
        ImGui.EndGroup();

        if (hint.Length > 0)
        {
            Hint(hint);
        }
    }

    /// <summary>
    /// Puts the next control on the same line as the last, with the gap that means "these go
    /// together".
    /// </summary>
    /// <remarks>
    /// A NAMED GAP rather than bare <c>SameLine()</c>, because the two say different things and
    /// the tool only had one of them. ImGui's default gap is the space between a control and
    /// its own label - use it between two checkboxes and "Map names" runs into the box for
    /// "What is in them", so a row of three switches reads as one long sentence with tick boxes
    /// in it. Two lines' worth is enough to see three groups instead of one run.
    ///
    /// It is also the only sanctioned way to put two controls on one line here: a bare SameLine
    /// after a wrapped paragraph is what put buttons wherever the prose happened to break, and
    /// that call should not be reached for by habit.
    /// </remarks>
    public static void Next() => ImGui.SameLine(0f, ImGui.GetFontSize() * 2f);

    /// <summary>How wide one cell of a switch grid is, in multiples of the text size.</summary>
    /// <remarks>
    /// Sized for the longest switch label that shares a row in this tool - "Every connection",
    /// "Hide maps with no way there" gets a row to itself - plus its tick box. Too narrow and
    /// the labels touch; too wide and three of them do not fit a window somebody has dragged in.
    /// </remarks>
    private const float CellEms = 11f;

    /// <summary>
    /// Puts the next control in a numbered column, so a block of switches is a grid.
    /// </summary>
    /// <remarks>
    /// <see cref="Next"/> puts a control a fixed GAP after the last one, which lines up nothing:
    /// where the second switch starts depends on how long the first one's label was, and where
    /// the third starts depends on the first two. A row of three reads fine on its own and falls
    /// apart the moment there is a SECOND row under it, because the second row's switches land
    /// under the middle of the first row's rather than under their ticks.
    ///
    /// A column index fixes each one to the same x on every row, which is what makes a block of
    /// switches scannable down as well as across - and what lets a switch move between rows
    /// without dragging its neighbours sideways.
    ///
    /// Like <see cref="ToColumn"/> it only ever pushes right: a label too long for its cell
    /// takes the room it needs rather than being overwritten by its neighbour.
    /// </remarks>
    /// <param name="column">Which column, counting the first control on the row as 0.</param>
    /// <param name="ems">
    /// How wide one cell is, for a row whose contents are not switch labels. The headline
    /// figures on the wealth page are the case: "Holding 601 div, 312 ex" does not fit a cell
    /// sized for a tick and two words, and a cell too narrow silently degrades to a fixed gap -
    /// which is what this exists to replace.
    /// </param>
    public static void Cell(int column, float ems = CellEms)
    {
        // Read before the SameLine, while the cursor is on the row's own left margin - see
        // ToColumn for why that is the only moment this can be measured.
        float margin = ImGui.GetCursorPosX();

        ImGui.SameLine();

        float want = margin + (ImGui.GetFontSize() * ems * column);
        if (ImGui.GetCursorPosX() < want)
        {
            ImGui.SetCursorPosX(want);
        }
    }

    /// <summary>
    /// One more fact on a line of them, ruled off from the last.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF <see cref="Figure"/>. A figure is a number somebody compares; a fact is
    /// a short thing that qualifies the figures - which league, how many prices, how old the
    /// stash reading is. Those were paragraphs, one per line, above the thing they qualify.
    ///
    /// The same divider the window's own status strip uses, so a line of facts looks like a line
    /// of facts wherever it is drawn. Not <see cref="StatusBar.Chip(string, Vector4)"/> itself:
    /// that measures against the WINDOW's right edge and keeps a "has one been drawn yet" flag
    /// for the single strip drawn per frame, neither of which is true inside a tab.
    /// </remarks>
    public static void Fact(Vector4 ink, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Divider();
        ImGui.TextColored(ink, ImGuiText.Escape(text));
    }

    /// <summary>The rule between two facts, for a caller putting a CONTROL on that line.</summary>
    /// <remarks>
    /// A line of facts occasionally carries a switch that belongs with them - "hide empty tabs"
    /// beside the count of what was read. Drawing the rule by hand at that one call site is how
    /// two dividers end up looking slightly different.
    /// </remarks>
    public static void Divider()
    {
        ImGui.SameLine();
        ImGui.TextColored(OverlayInk.Edge, "|");
        ImGui.SameLine();
    }

    /// <summary>
    /// How far in a row's controls begin, when the row starts with a name instead.
    /// </summary>
    /// <remarks>
    /// Wide enough for the long names in the lists this is used on - "Where a route starts",
    /// "Unique maps" - without pushing the controls so far right that a narrow window loses
    /// them. Like every other measure here it is in text, so it holds at any text size.
    /// </remarks>
    private const float LabelEms = 14f;

    /// <summary>
    /// Moves to the column where a row's controls start, however long its name was.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF THE GRID, and the half this vocabulary was missing. <see
    /// cref="FieldWidth"/> handles the row that is a control with its name to the RIGHT, which
    /// is ImGui's own arrangement and where most settings live. It does nothing for the other
    /// shape: a list whose rows start with a name and carry controls after it - the atlas
    /// groups, the marker styles, anything with a tick and a label in front.
    ///
    /// Those rows had no column at all. Each one's controls began wherever its own name
    /// happened to end, so eleven atlas groups put eleven number boxes at eleven different
    /// x-positions, and the marker rows put "size" and "icon" wherever the marker's name ran
    /// out. A column of one-word names looks tidy and a column of real ones does not, which is
    /// why this only becomes obvious with live data in it.
    ///
    /// NEVER OVERLAPS: a name longer than the column pushes its controls along instead of
    /// having them drawn over it. A row out of line is untidy; a row on top of itself is
    /// unreadable, and the second is what a plain SetCursorPosX would do.
    /// </remarks>
    public static void ToColumn(float ems = LabelEms)
    {
        // Read BEFORE the SameLine: after an item the cursor has wrapped to the next line, so
        // its x is the row's own left margin - which already includes whatever indent is in
        // force. Measuring the column from there is what makes this work inside an indent.
        float margin = ImGui.GetCursorPosX();

        ImGui.SameLine();

        float want = margin + (ImGui.GetFontSize() * ems);
        if (ImGui.GetCursorPosX() < want)
        {
            ImGui.SetCursorPosX(want);
        }
    }

    /// <summary>A checkbox. The one control whose label ImGui already puts in a fixed place.</summary>
    /// <remarks>
    /// Here despite needing no width set, so that a page can be written entirely in this
    /// vocabulary rather than in this vocabulary plus raw ImGui for the checkboxes - which is
    /// how a page drifts back out of the grid one control at a time.
    /// </remarks>
    public static bool Toggle(string label, ref bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        return ImGui.Checkbox(label, ref on);
    }

    /// <summary>A whole-number slider, at the one field width.</summary>
    public static bool Slider(string label, ref int value, int min, int max, string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.SliderInt(label, ref value, min, max, format);
    }

    /// <summary>A fractional slider, at the one field width.</summary>
    public static bool Slider(string label, ref float value, float min, float max, string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.SliderFloat(label, ref value, min, max, format);
    }

    /// <summary>A drag field, at the one field width.</summary>
    public static bool Drag(string label, ref float value, float speed, float min, float max, string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.DragFloat(label, ref value, speed, min, max, format);
    }

    /// <summary>A text box, at the one field width.</summary>
    public static bool Input(
        string label, ref string value, uint maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.InputText(label, ref value, maxLength, flags);
    }

    /// <summary>A whole-number box, at the one field width.</summary>
    public static bool Number(string label, ref int value, int step = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.InputInt(label, ref value, step);
    }

    /// <summary>A dropdown, at the one field width.</summary>
    public static bool Combo(string label, ref int chosen, string[] options)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(options);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.Combo(label, ref chosen, options, options.Length);
    }

    /// <summary>
    /// A filter box: the width of whatever is left, with its name written inside it.
    /// </summary>
    /// <remarks>
    /// THE ONE CONTROL THAT SHOULD FILL THE WINDOW, and the exception proves the rule the rest
    /// of this file is built on. Fields are fixed so their LABELS stay in a column - a filter
    /// box has no label beside it, because its name is the grey text inside it until somebody
    /// types. There is no column to keep still, so the only question left is how much of the
    /// window is useful for typing into, and the answer is all of it.
    ///
    /// The tool already drew filters this way in five places and at four different widths -
    /// 12, 13.5 and 16.5 lines - each one a guess at how long a search term is. None of them
    /// had to be guessed.
    /// </remarks>
    /// <param name="id">The control's ImGui id, as "##something" - never shown.</param>
    /// <param name="hint">What is written in the empty box, e.g. "filter by name".</param>
    /// <param name="reserve">Room to leave on the right, for a button that follows on the line.</param>
    /// <param name="flags">
    /// ImGui's own input flags. <c>EnterReturnsTrue</c> for the filters that only run a search
    /// when the key is pressed - the tree browser and the preload list both search something
    /// too expensive to redo per keystroke.
    /// </param>
    public static bool Search(
        string id,
        string hint,
        ref string text,
        uint maxLength,
        float reserve = 0f,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(hint);

        // A floor, so a box in a narrow window is still something you can type into rather than
        // a slot showing two characters.
        ImGui.SetNextItemWidth(
            Math.Max(ImGui.GetFontSize() * 8f, ImGui.GetContentRegionAvail().X - reserve));
        return ImGui.InputTextWithHint(id, hint, ref text, maxLength, flags);
    }

    /// <summary>
    /// How much room a run of buttons needs, for a filter that has to leave space for them.
    /// </summary>
    /// <remarks>
    /// MEASURED, not guessed, and that is the point. A filter with a button beside it is the one
    /// arrangement where a full-width field is wrong, and the tool's four instances of it each
    /// picked a field width by eye that happened to leave about enough - so all four were a
    /// different width, and each was wrong at some text size, since the button grows with the
    /// text and a width in lines does not grow at the same rate.
    /// </remarks>
    public static float ButtonRoom(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        ImGuiStylePtr style = ImGui.GetStyle();
        float room = 0f;
        foreach (string label in labels)
        {
            room += ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f) + style.ItemSpacing.X;
        }

        return room;
    }

    /// <summary>
    /// The same controls, for the rows where several of them share one line.
    /// </summary>
    /// <remarks>
    /// A NAMED SORT OF ROW rather than a width passed at the call site, which is the whole
    /// point: "this control shares its line" is a fact about the row, and once it is said the
    /// width follows from it. Said as a number instead, it was said forty times in this tool
    /// and no two of them agreed.
    ///
    /// Nested rather than a separate class so the call site reads as what it is -
    /// <c>OverlayLayout.Narrow.Drag(...)</c> beside <c>OverlayLayout.Drag(...)</c> - and so
    /// there is no second place to look for the vocabulary.
    /// </remarks>
    public static class Narrow
    {
        /// <summary>A fractional slider sharing its line.</summary>
        public static bool Slider(string label, ref float value, float min, float max, string format)
        {
            ArgumentNullException.ThrowIfNull(label);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.SliderFloat(label, ref value, min, max, format);
        }

        /// <summary>A whole-number slider sharing its line.</summary>
        public static bool Slider(string label, ref int value, int min, int max, string format)
        {
            ArgumentNullException.ThrowIfNull(label);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.SliderInt(label, ref value, min, max, format);
        }

        /// <summary>A drag field sharing its line.</summary>
        public static bool Drag(string label, ref float value, float speed, float min, float max, string format)
        {
            ArgumentNullException.ThrowIfNull(label);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.DragFloat(label, ref value, speed, min, max, format);
        }

        /// <summary>A whole-number box sharing its line.</summary>
        public static bool Number(string label, ref int value, int step = 0)
        {
            ArgumentNullException.ThrowIfNull(label);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.InputInt(label, ref value, step);
        }

        /// <summary>A dropdown sharing its line.</summary>
        public static bool Combo(string label, ref int chosen, string[] options)
        {
            ArgumentNullException.ThrowIfNull(label);
            ArgumentNullException.ThrowIfNull(options);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.Combo(label, ref chosen, options, options.Length);
        }

        /// <summary>A text box sharing its line.</summary>
        public static bool Input(string label, ref string value, uint maxLength)
        {
            ArgumentNullException.ThrowIfNull(label);
            ImGui.SetNextItemWidth(CompactWidth());
            return ImGui.InputText(label, ref value, maxLength);
        }
    }

    /// <summary>
    /// The same controls once more, each MEASURED to what it holds - the shape a toolbar wants.
    /// </summary>
    /// <remarks>
    /// THE THIRD SORT OF ROW, and the one the vocabulary was still missing. <see
    /// cref="FieldWidth"/> is for a column of settings read downwards; <see cref="Narrow"/> is
    /// for several controls that only mean anything together. A TOOLBAR is neither: a run of
    /// independent controls that are OPERATED rather than read, above the thing being worked on,
    /// where the cost that matters is the height they take. The dissector is the case - four
    /// controls at the field width filled a line and pushed the rest onto three more, which is
    /// four lines of chrome above a table whose entire value is how many rows of it fit.
    ///
    /// AND THE WIDTH IS ASKED FOR RATHER THAN CHOSEN, which is what makes this different from
    /// adding a fourth number to the file. A dropdown's options are a known list; a hex address
    /// is sixteen digits. Both are things that can be MEASURED, like <see cref="ButtonRoom"/>
    /// already measures buttons - and measured they are also right in the mono face, which is a
    /// different width from the body face at the same size, and right at every text size rather
    /// than at the one it was guessed at.
    /// </remarks>
    public static class Sized
    {
        /// <summary>A dropdown as wide as its longest option, and no wider.</summary>
        /// <remarks>
        /// It measures ITSELF: nothing has to be passed and so nothing can be passed wrongly.
        /// A guess is what puts "AreaInstance" in a box showing "AreaInsta".
        /// </remarks>
        public static bool Combo(string label, ref int chosen, string[] options)
        {
            ArgumentException.ThrowIfNullOrEmpty(label);
            ArgumentNullException.ThrowIfNull(options);

            float widest = 0f;
            foreach (string option in options)
            {
                widest = Math.Max(widest, ImGui.CalcTextSize(option).X);
            }

            // The frame height on top is the arrow, which ImGui draws INSIDE the item width -
            // so a combo sized to its text alone clips the last letter of its longest option.
            ImGui.SetNextItemWidth(
                widest + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetFrameHeight());
            return ImGui.Combo(label, ref chosen, options, options.Length);
        }

        /// <summary>A text box as wide as the longest thing that can go in it.</summary>
        /// <param name="longest">
        /// A sample of the widest content, measured in the face IN FORCE - so a hex box must
        /// call this with the mono face pushed, which is the face its digits are drawn in.
        /// </param>
        public static bool Input(
            string label,
            ref string value,
            uint maxLength,
            string longest,
            ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
        {
            ArgumentException.ThrowIfNullOrEmpty(label);
            ArgumentNullException.ThrowIfNull(longest);

            ImGui.SetNextItemWidth(
                ImGui.CalcTextSize(longest).X + (ImGui.GetStyle().FramePadding.X * 2f));
            return ImGui.InputText(label, ref value, maxLength, flags);
        }
    }

    /// <summary>
    /// A row of buttons, kept off the end of whatever was drawn above it.
    /// </summary>
    /// <remarks>
    /// ON ITS OWN LINE, which is the correction. Buttons in this tool were attached to whatever
    /// preceded them with <c>SameLine</c>, including to wrapped paragraphs - so where the
    /// buttons sat depended on where the prose broke, and dragging the window moved them. A
    /// row of its own puts them at the left margin, at the same place on every page, and it
    /// costs one line.
    /// </remarks>
    /// <param name="buttons">The labels, in order. The index of the one pressed comes back.</param>
    /// <returns>Which button was pressed, or -1.</returns>
    public static int Actions(params string[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        int pressed = -1;
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.Button(buttons[i]))
            {
                pressed = i;
            }
        }

        return pressed;
    }
}
