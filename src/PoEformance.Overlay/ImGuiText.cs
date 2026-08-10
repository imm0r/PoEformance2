using System.Runtime.Versioning;

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
}
