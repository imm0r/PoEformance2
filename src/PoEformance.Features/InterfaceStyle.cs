using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// How the tool's own windows are drawn - the text size and how solid the panels are.
/// </summary>
/// <remarks>
/// SEPARATE FROM <see cref="OverlayStyle"/>, which is about the things drawn ON the game: a
/// marker's colour, a line's width, an icon. This is the chrome those settings are edited in.
/// The two are confusable by name and never by purpose, so they are kept apart rather than
/// merged into one bag - changing what a chest marker looks like has nothing to do with
/// whether a table of struct bytes can be read.
///
/// IT IS ADJUSTABLE BECAUSE NOBODY CAN CHOOSE IT FOR SOMEBODY ELSE. What this is drawn over
/// is a game with a bright, busy, moving picture, at whatever resolution and display scaling
/// the reader happens to run, and read from wherever they sit. A size and a solidity that are
/// comfortable on one of those is unreadable on the next, and the only person who can settle
/// it is looking at it.
///
/// EVERY VALUE HAS A HARMLESS ZERO, for the same reason <see cref="LayerStyle"/> does: a
/// settings file written before this existed has no key for any of it, and a hand-edited one
/// can hold anything at all. Zero therefore means "as it comes" rather than no text and an
/// invisible panel - see <see cref="Normalised"/>, which is what every path in goes through.
/// </remarks>
/// <param name="TextSize">
/// The interface font's size in pixels. 0 for <see cref="DefaultTextSize"/>.
/// </param>
/// <param name="PanelOpacity">
/// How solid a tool panel is, 0 to 1. 0 for <see cref="DefaultPanelOpacity"/>.
/// </param>
/// <param name="ReadoutOpacity">
/// The same for the live readout, which sits over the game while playing rather than being
/// read like a page. 0 for <see cref="DefaultReadoutOpacity"/>.
/// </param>
public sealed record InterfaceStyle(
    [property: JsonPropertyName("textSize")] int TextSize = InterfaceStyle.DefaultTextSize,
    [property: JsonPropertyName("panelOpacity")] float PanelOpacity = InterfaceStyle.DefaultPanelOpacity,
    [property: JsonPropertyName("readoutOpacity")] float ReadoutOpacity = InterfaceStyle.DefaultReadoutOpacity)
{
    /// <summary>
    /// The interface font's size when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// Well above ImGui's built-in 13, and above the 16 this started at. Both of those are
    /// sizes for text on a quiet background a foot from your face; this is read across a room
    /// from a game that is painting foliage and firelight behind every letter of it.
    /// </remarks>
    public const int DefaultTextSize = 18;

    /// <summary>Smallest and largest the text may be set to.</summary>
    /// <remarks>
    /// The floor is where the serif faces stop resolving their own strokes and the interface
    /// becomes grey mush; the ceiling is where the tool windows stop fitting on a 1080p screen.
    /// Both are limits of the drawing rather than of taste, which is why they are wide.
    /// </remarks>
    public const int MinTextSize = 12;

    /// <inheritdoc cref="MinTextSize"/>
    public const int MaxTextSize = 30;

    /// <summary>
    /// How solid a tool panel is by default: completely.
    /// </summary>
    /// <remarks>
    /// A table of memory addresses with a hideout showing through it cannot be read, and no
    /// amount of contrast elsewhere fixes it - the noise is BEHIND the letters, at the same
    /// spatial frequency, and it moves. A window somebody deliberately opened to read is
    /// allowed to cover what is behind it.
    /// </remarks>
    public const float DefaultPanelOpacity = 1f;

    /// <summary>
    /// How solid the live readout is by default.
    /// </summary>
    /// <remarks>
    /// Not quite solid, because this one is not read like a page: it sits in a corner during
    /// a fight and is glanced at, and what is behind it is the game being played. Still far
    /// more solid than the 0.7 it started at - at that value the readout's own figures were
    /// competing with whatever monster walked under them.
    /// </remarks>
    public const float DefaultReadoutOpacity = 0.9f;

    /// <summary>
    /// The floor for both opacities.
    /// </summary>
    /// <remarks>
    /// Not zero, and this is a guard rather than a preference: a panel at zero is a window
    /// that is still there, still takes the mouse and cannot be seen, and the only way back
    /// would be to find the slider that did it. The lowest setting has to leave the window
    /// visible enough to be found again.
    /// </remarks>
    public const float MinOpacity = 0.3f;

    /// <summary>
    /// How much larger a heading is than the body text.
    /// </summary>
    /// <remarks>
    /// A RATIO rather than a number of pixels, so it holds across the whole range the text size
    /// can be set to: a fixed "+4" is a shout at twelve pixels and invisible at thirty. A
    /// quarter is enough for the eye to rank two lines without the heading becoming a banner.
    /// </remarks>
    public const float HeadingScale = 1.25f;

    /// <summary>Everything as it comes.</summary>
    public static InterfaceStyle Default { get; } = new();

    /// <summary>The size the text is actually drawn at.</summary>
    public int TextSizeOr => TextSize <= 0 ? DefaultTextSize : Math.Clamp(TextSize, MinTextSize, MaxTextSize);

    /// <summary>The size a heading is drawn at, for a given body size.</summary>
    /// <remarks>
    /// Here rather than beside the font loading, because it is arithmetic about the interface's
    /// own sizes and nothing about it needs ImGui to be wrong in - which is the same reason
    /// the window geometry lives in this layer.
    ///
    /// Rounded AWAY from the body size rather than to even, so that every size in the range
    /// gains at least a whole pixel: at the small end the difference between 15 and 15 is no
    /// heading at all.
    /// </remarks>
    public static int HeadingSizeFor(int body)
        => Math.Max(body + 1, (int)((body * HeadingScale) + 0.5f));

    /// <summary>The size this style's headings are drawn at.</summary>
    public int HeadingSizeOr => HeadingSizeFor(TextSizeOr);

    /// <summary>How solid a tool panel is actually drawn.</summary>
    public float PanelOpacityOr => Solidity(PanelOpacity, DefaultPanelOpacity);

    /// <summary>How solid the readout is actually drawn.</summary>
    public float ReadoutOpacityOr => Solidity(ReadoutOpacity, DefaultReadoutOpacity);

    private static float Solidity(float chosen, float fallback)
        => chosen <= 0f ? fallback : Math.Clamp(chosen, MinOpacity, 1f);

    /// <summary>The same settings with every value inside the range the overlay understands.</summary>
    /// <remarks>
    /// Applied on the way IN rather than only when drawing, so that what the editor shows,
    /// what the file holds and what is on screen are the same three numbers. A file saying
    /// <c>textSize: 400</c> otherwise draws at 30 and keeps saying 400 back to whoever opens
    /// the slider, which is the sort of disagreement that gets reported as the slider being
    /// broken.
    /// </remarks>
    public InterfaceStyle Normalised()
        => new(TextSizeOr, PanelOpacityOr, ReadoutOpacityOr);
}
