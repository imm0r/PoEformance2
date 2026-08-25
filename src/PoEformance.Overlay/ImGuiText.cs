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
