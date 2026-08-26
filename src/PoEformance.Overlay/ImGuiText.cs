using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;

namespace PoEformance.Overlay;

/// <summary>
/// Text on its way into ImGui, made safe to print.
/// </summary>
/// <remarks>
/// ImGui's text calls are PRINTF: <c>ImGui.Text</c> and <c>ImGui.TextColored</c> hand the
/// string straight to the C library as a FORMAT string, so a percent sign in it is not a
/// percent sign - it starts a conversion specifier, which then reads an argument that was
/// never passed.
///
/// The damage is not a missing character, which is what makes this worth a named helper. A
/// line reading "48% of the total" came out as "483716076767 0f the total": the "% o" was
/// taken as an octal conversion, printed whatever happened to be in the register, and ATE
/// the "o" of "of" on the way past. Nothing about that output suggests a formatting problem,
/// so the next person reads it as a broken calculation and goes looking in the arithmetic.
///
/// Anything interpolated from data needs this too, not just literals - a monster called
/// "100% Increased" would do the same thing, and that one arrives from the game rather than
/// from a line somebody can see while writing it.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ImGuiText
{
    /// <summary>Doubles every percent sign, which is how printf spells a literal one.</summary>
    public static string Escape(string text) => text.Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>Coloured text that wraps at the window's edge, which TextColored never does.</summary>
    /// <remarks>
    /// The named helper exists because the composition kept being skipped: TextColored never
    /// wraps and TextWrapped takes no colour, so every explainer written with the former ran
    /// off the window as one long line - and reading it meant dragging the window across the
    /// monitor. Some were even broken in two BY HAND at a width somebody's window happened to
    /// have. Wrapping only works where the window has a real width, which every tool page has;
    /// the auto-sized readout and the popups are the places this must not be used.
    ///
    /// Still printf underneath, like everything in this file: interpolated data needs
    /// <see cref="Escape"/> on its way in.
    /// </remarks>
    public static void Wrapped(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>A figure - an address, an offset, a count, a measurement - in the mono face.</summary>
    /// <remarks>
    /// WHAT COUNTS AS A FIGURE is decided at the call site rather than by looking at the string,
    /// and that is on purpose. A rule along the lines of "mono if it is all digits" would have
    /// to answer for "Level 5" and "Act 3", and it would answer differently for the same row
    /// depending on the value that happened to be in it - a table whose face flickers as the
    /// game changes underneath it. The caller knows which of its columns is a measurement and
    /// which is a name, and it knows it once rather than per frame.
    ///
    /// TextUnformatted rather than Text, so this is not printf at all - see the note at the top
    /// of this file for what a stray percent sign does. It means a figure never needs
    /// <see cref="Escape"/>, which matters here more than elsewhere: several of these strings
    /// carry a percent sign as a UNIT.
    /// </remarks>
    public static void Mono(string text)
    {
        OverlayFonts.PushMono();
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            OverlayFonts.PopMono();
        }
    }

    /// <summary>The same, in a colour.</summary>
    /// <remarks>
    /// The colour is PUSHED rather than passed to TextColored, for the printf reason above:
    /// TextColored is a format call and this must not be one.
    /// </remarks>
    public static void Mono(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        try
        {
            Mono(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    /// <summary>A tooltip whose contents are figures, in the mono face.</summary>
    /// <remarks>
    /// Built out rather than SetTooltip, for two reasons that both matter here. SetTooltip is
    /// printf, and it offers no moment at which a font could be pushed - the whole tooltip is
    /// drawn inside the one call.
    ///
    /// What it is for is the several readings of one value stacked in a block, LINED UP BY
    /// SPACES. That is the layout every one of these tooltips uses, and in a proportional face
    /// it does not line up at all: the labels are different words, so the columns after them
    /// start wherever those words happened to end.
    /// </remarks>
    public static void MonoTooltip(string text)
    {
        if (!ImGui.BeginTooltip())
        {
            return;
        }

        try
        {
            Mono(text);
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    /// <summary>An indented, wrapped explainer under the control it belongs to.</summary>
    /// <remarks>
    /// What the four-leading-spaces prefix was trying to be. The prefix only indented the
    /// FIRST line - a wrapped continuation returned to column zero - and it made the text
    /// unequal to itself for searching. A real indent survives the wrap, and scales with the
    /// text like everything else.
    /// </remarks>
    public static void Hint(Vector4 colour, string text)
    {
        float by = ImGui.GetFontSize() * 1.2f;
        ImGui.Indent(by);
        Wrapped(colour, text);
        ImGui.Unindent(by);
    }

    /// <summary>
    /// The last two segments of a metadata path, which is the part that says what it is.
    /// </summary>
    /// <remarks>
    /// For labels drawn IN THE WORLD, where the whole path is a line of text per entity across
    /// a screen that may hold hundreds and the prefix is the same on all of them. The windows
    /// print the full path, because that is the readout somebody copies an id out of and a
    /// shortened one cannot be searched for.
    /// </remarks>
    public static string Tail(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            return "(no path)";
        }

        int last = path.LastIndexOf('/');
        if (last <= 0)
        {
            return path;
        }

        int before = path.LastIndexOf('/', last - 1);
        return before < 0 ? path[(last + 1)..] : path[(before + 1)..];
    }
}
