using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// How one overlay window behaves once it is where somebody wants it.
/// </summary>
/// <remarks>
/// TWO SEPARATE THINGS, though click-through implies the first. A LOCKED window still takes
/// the mouse - its buttons work, it just cannot be dragged out of place by a stray click,
/// which is what happens to a window parked over the middle of the screen. A CLICK-THROUGH
/// one is not there as far as the mouse is concerned: the click lands on the game behind it,
/// which is what a readout wants and what anything with a button in it does not.
///
/// Per window rather than one switch for the overlay, because the answer differs per window
/// by its nature: a readout somebody glances at wants both, and the window they are picking
/// routes in wants neither.
/// </remarks>
/// <param name="Locked">Cannot be dragged. Still clickable.</param>
/// <param name="ClickThrough">The mouse does not see it at all; clicks reach the game.</param>
/// <param name="X">
/// Where the window's anchored corner sits, as a share of the game window's width - so the
/// position survives a resolution change, where a pixel count would put the window somewhere
/// else on every monitor. Null until the window has been seen once.
/// </param>
/// <param name="Y">The same, of the height.</param>
/// <param name="PivotX">
/// Which corner is anchored: 0 = left/top edge, 1 = right/bottom. The corner NEAREST the
/// screen edge, so a window parked at the bottom right grows UP and LEFT as its content does,
/// instead of walking its extra lines off the screen.
/// </param>
public sealed record WindowRule(
    [property: JsonPropertyName("locked")] bool Locked = false,
    [property: JsonPropertyName("clickThrough")] bool ClickThrough = false,
    [property: JsonPropertyName("x")] double? X = null,
    [property: JsonPropertyName("y")] double? Y = null,
    [property: JsonPropertyName("pivotX")] int PivotX = 0,
    [property: JsonPropertyName("pivotY")] int PivotY = 0)
{
    /// <summary>Draggable and clickable, which is what a window starts as.</summary>
    public static WindowRule Free { get; } = new();

    /// <summary>Whether this says anything at all, so the settings need not store it.</summary>
    public bool Anything => Locked || ClickThrough || X is not null;

    /// <summary>Whether a place has been remembered for this window.</summary>
    public bool Placed => X is not null && Y is not null;
}

/// <summary>
/// The geometry of keeping a window on screen and anchored to its nearest corner.
/// </summary>
/// <remarks>
/// Pure on purpose, because it is the part worth testing: WHERE a window may be and WHICH of
/// its corners is the fixed one are answers about rectangles, and none of it needs ImGui to be
/// wrong in. The chrome asks these questions every frame; this answers them the same way every
/// time.
///
/// THE PIVOT IS THE POINT OF THE WHOLE ARRANGEMENT. A window is normally pinned by its top-left
/// corner, so one that sizes itself to its content grows DOWN and RIGHT - and one parked at the
/// bottom edge grows straight off the screen. Anchoring the corner nearest the screen's edge
/// instead means growth always runs INTO the screen: bottom-right windows extend up and left.
/// </remarks>
public static class WindowAnchor
{
    /// <summary>
    /// Where a window is allowed to be: fully inside the viewport, edges included.
    /// </summary>
    /// <remarks>
    /// A window larger than the viewport pins to the top-left rather than centring, so its
    /// title bar - the handle for fixing the situation - is the part that stays reachable.
    /// </remarks>
    public static (float X, float Y) Clamp(
        float x, float y, float width, float height, float viewWidth, float viewHeight)
    {
        float clampedX = Math.Clamp(x, 0f, Math.Max(0f, viewWidth - width));
        float clampedY = Math.Clamp(y, 0f, Math.Max(0f, viewHeight - height));
        return (clampedX, clampedY);
    }

    /// <summary>
    /// The rule that remembers this window where it stands: clamped, and anchored by the
    /// corner nearest the screen's edge.
    /// </summary>
    public static WindowRule Settle(
        WindowRule rule, float x, float y, float width, float height, float viewWidth, float viewHeight)
    {
        ArgumentNullException.ThrowIfNull(rule);

        (float atX, float atY) = Clamp(x, y, width, height, viewWidth, viewHeight);

        int pivotX = atX + (width / 2f) > viewWidth / 2f ? 1 : 0;
        int pivotY = atY + (height / 2f) > viewHeight / 2f ? 1 : 0;

        return rule with
        {
            X = (atX + (pivotX * width)) / viewWidth,
            Y = (atY + (pivotY * height)) / viewHeight,
            PivotX = pivotX,
            PivotY = pivotY,
        };
    }

    /// <summary>
    /// The anchor as it stands RIGHT NOW, unclamped and unpersisted - the shape a drag is
    /// tracked in.
    /// </summary>
    /// <remarks>
    /// Settle is what a window is REMEMBERED as; this is what it is DOING. The difference is
    /// the release frame: the chrome must not re-assert a position while the button is held,
    /// so the only way the first frame after a drag can assert the DRAGGED position is for
    /// something to have watched the drag as it went. No clamp, because yanking a window back
    /// mid-drag is the fight this exists to avoid - the clamp belongs to the release.
    /// </remarks>
    public static (double X, double Y, int PivotX, int PivotY) Track(
        float x, float y, float width, float height, float viewWidth, float viewHeight)
    {
        int pivotX = x + (width / 2f) > viewWidth / 2f ? 1 : 0;
        int pivotY = y + (height / 2f) > viewHeight / 2f ? 1 : 0;

        return (
            (x + (pivotX * width)) / viewWidth,
            (y + (pivotY * height)) / viewHeight,
            pivotX,
            pivotY);
    }

    /// <summary>The anchored corner's position in pixels, for handing back to the window.</summary>
    public static (float X, float Y)? Resolve(WindowRule rule, float viewWidth, float viewHeight)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule is { X: double x, Y: double y }
            ? ((float)(x * viewWidth), (float)(y * viewHeight))
            : null;
    }
}
