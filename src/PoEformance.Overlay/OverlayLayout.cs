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

    /// <summary>The width every value control on every page is drawn at.</summary>
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

    /// <summary>One step in, for a note or for a control that depends on the one above it.</summary>
    public static float Step() => ImGui.GetFontSize() * StepEms;

    /// <summary>
    /// The switch a whole feature hangs off, drawn the same way on every page that has one.
    /// </summary>
    /// <remarks>
    /// FIRST LINE, ALWAYS, AND IT LOOKS LIKE NOTHING ELSE ON THE PAGE. Half the pages in this
    /// tool have one setting that decides whether the other twenty do anything, and it used to
    /// be an ordinary checkbox somewhere among them - so "is this feature even on" was a
    /// question you answered by reading the whole page and hoping you recognised the right
    /// line. Given its own row, in the heading face, with the state spelled out in words beside
    /// it, that question is answered from the top of the page without reading anything.
    ///
    /// The state is written out as well as ticked because a tick is a shape and this is the one
    /// place the answer has to survive a glance at a moving screen. It is also what makes the
    /// row worth the vertical space it costs.
    /// </remarks>
    /// <returns>Whether the switch was just changed.</returns>
    public static bool Master(string label, ref bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        bool changed = false;
        bool state = on;

        // The heading face, like a section title and a tab, because this line outranks
        // everything under it - it decides whether any of it applies.
        OverlayFonts.Heading(() =>
        {
            if (ImGui.Checkbox(label, ref state))
            {
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(state ? OnInk : OffInk, state ? "on" : "off");
        });

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
    public static void Group(string title)
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

        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
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
    public static bool Input(string label, ref string value, uint maxLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ImGui.SetNextItemWidth(FieldWidth());
        return ImGui.InputText(label, ref value, maxLength);
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
