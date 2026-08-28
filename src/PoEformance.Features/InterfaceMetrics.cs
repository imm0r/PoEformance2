using System.Numerics;

namespace PoEformance.Features;

/// <summary>
/// How much air the interface leaves around things, for a given text size.
/// </summary>
/// <remarks>
/// THE SPACING WAS FIXED WHILE THE TEXT WAS NOT. <see cref="InterfaceStyle.TextSize"/> ranges
/// from 12 to 30 pixels - two and a half times over - and every padding, gap and inset the
/// theme set was a constant that had been tuned by eye at 18. At the top of that range the
/// interface is cramped: the letters are half as big again as the space around them was chosen
/// for, so a button's label very nearly touches its own border and two rows of a table run into
/// each other. At the bottom it is the opposite - twelve-pixel text swimming in padding sized
/// for text half again as large, which is a window twice as tall as its contents need.
///
/// Neither of those is fixable by picking better constants, because the two ends want different
/// ones. So the constants become RATIOS OF THE TEXT SIZE, which is the same move
/// <see cref="InterfaceStyle.HeadingScale"/> makes and for the same reason: "a quarter larger"
/// holds across the whole range where "+4 pixels" is a shout at one end and invisible at the
/// other.
///
/// EVERY RATIO REPRODUCES TODAY'S NUMBER AT 18 PIXELS, which is the default and is where they
/// were tuned. That is deliberate and it is what makes this safe to land: at the setting almost
/// everybody is on, nothing moves at all. What changes is what happens when somebody drags the
/// slider, which is the case that was wrong.
///
/// WHOLE PIXELS, always. ImGui draws a one-pixel border and a one-pixel table rule, and a frame
/// whose padding lands on a half pixel puts those on a half pixel too - where they come out as
/// two dim rows instead of one lit one. Rounding here rather than at the call site means there
/// is one place it can be got wrong.
///
/// IN THIS LAYER for the reason the rest of the appearance arithmetic is: it needs no ImGui
/// context to be reasoned about, so it can be argued with in a test.
/// </remarks>
/// <param name="WindowPadding">Between a window's edge and its contents.</param>
/// <param name="FramePadding">Inside a button, a checkbox, an input box.</param>
/// <param name="ItemSpacing">Between one control and the next.</param>
/// <param name="ItemInnerSpacing">Between a control and its own label.</param>
/// <param name="CellPadding">Inside a table cell.</param>
/// <param name="IndentSpacing">How far a tree node or a hint steps in.</param>
/// <param name="ScrollbarSize">How wide a scrollbar is.</param>
/// <param name="GrabMinSize">The smallest a slider's grab is allowed to get.</param>
public readonly record struct InterfaceMetrics(
    Vector2 WindowPadding,
    Vector2 FramePadding,
    Vector2 ItemSpacing,
    Vector2 ItemInnerSpacing,
    Vector2 CellPadding,
    float IndentSpacing,
    float ScrollbarSize,
    float GrabMinSize)
{
    /// <summary>
    /// The ratios, named by what each one was at the default size.
    /// </summary>
    /// <remarks>
    /// Written as a division rather than as a decimal so the tuning is legible: the numerator is
    /// the pixel value this interface has had since it was tuned, and the denominator is the
    /// text size it was tuned at. Somebody revisiting "is 10 pixels of window padding right"
    /// gets to argue about 10, which is the number they can see on screen, instead of about
    /// 0.5556, which is nobody's idea of anything.
    /// </remarks>
    private const float TunedAt = InterfaceStyle.DefaultTextSize;

    /// <summary>The spacing for a given body text size.</summary>
    /// <remarks>
    /// EVERY RESULT IS AT LEAST ONE PIXEL where the tuned value was not zero. At the small end
    /// the vertical insets - three pixels of cell padding at 18 - would otherwise round to
    /// nothing, and a table whose rows have no padding at all is a table whose rows touch. The
    /// horizontal ones are large enough that the floor never binds; it is written for all of
    /// them anyway, because which one is smallest is a fact about the current tuning rather
    /// than a rule.
    /// </remarks>
    public static InterfaceMetrics For(int body) => new(
        WindowPadding: Pair(body, 10f, 8f),
        FramePadding: Pair(body, 7f, 4f),
        ItemSpacing: Pair(body, 8f, 5f),
        ItemInnerSpacing: Pair(body, 6f, 4f),
        CellPadding: Pair(body, 6f, 3f),

        // A step in is ONE LINE OF TEXT, which is the one ratio here that is a rule rather than
        // a transcription: ImGui's own 21 pixels is a number from a interface set at 13, and at
        // this tool's sizes it indents a hint further than the hint is tall. A line's worth
        // reads as a step at every size because it is measured in the thing being stepped.
        IndentSpacing: Scaled(body, TunedAt),

        ScrollbarSize: Scaled(body, 14f),
        GrabMinSize: Scaled(body, 12f));

    /// <summary>The metrics this style draws at.</summary>
    public static InterfaceMetrics Of(InterfaceStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return For(style.TextSizeOr);
    }

    private static Vector2 Pair(int body, float across, float down)
        => new(Scaled(body, across), Scaled(body, down));

    /// <summary>One tuned pixel value, carried to another text size and rounded to a pixel.</summary>
    private static float Scaled(int body, float tuned)
        => MathF.Max(1f, MathF.Round(tuned * body / TunedAt));
}
