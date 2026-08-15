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
