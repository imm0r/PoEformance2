using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Keeping an overlay window on screen, anchored by the corner nearest the edge.
/// </summary>
/// <remarks>
/// The pure half of the window chrome, tested because it is the part that can be: WHERE a
/// window may be and WHICH corner is the fixed one are answers about rectangles. The chrome
/// re-asserts these every frame, so an answer that drifts is a window that creeps.
/// </remarks>
public class WindowAnchorTests
{
    private const float ViewW = 1920f;
    private const float ViewH = 1080f;

    [Fact]
    public void AWindowDraggedPastTheEdgeSnapsBackInside()
    {
        (float x, float y) = WindowAnchor.Clamp(1900f, -40f, 300f, 200f, ViewW, ViewH);

        Assert.Equal(ViewW - 300f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void AWindowLargerThanTheScreenKeepsItsTitleBarReachable()
    {
        // Pinned to the top-left rather than centred, so the handle for fixing the situation
        // is the part that stays on screen.
        (float x, float y) = WindowAnchor.Clamp(500f, 500f, 3000f, 2000f, ViewW, ViewH);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void ABottomRightWindowIsAnchoredByItsBottomRightCorner()
    {
        // The point of the whole arrangement: pinned there, a window that grows - more points
        // of interest, another line - extends UP and LEFT into the screen instead of walking
        // its new content off the bottom edge.
        WindowRule settled = WindowAnchor.Settle(
            WindowRule.Free, ViewW - 360f, ViewH - 340f, 360f, 340f, ViewW, ViewH);

        Assert.Equal(1, settled.PivotX);
        Assert.Equal(1, settled.PivotY);
        Assert.Equal(1.0, settled.X!.Value, 3);
        Assert.Equal(1.0, settled.Y!.Value, 3);
    }

    [Fact]
    public void ATopLeftWindowKeepsTheOrdinaryAnchor()
    {
        WindowRule settled = WindowAnchor.Settle(WindowRule.Free, 40f, 320f, 360f, 340f, ViewW, ViewH);

        Assert.Equal(0, settled.PivotX);
        Assert.Equal(0, settled.PivotY);
        Assert.Equal(40f / ViewW, settled.X!.Value, 5);
        Assert.Equal(320f / ViewH, settled.Y!.Value, 5);
    }

    [Fact]
    public void ResolvingASettledRulePutsTheAnchoredCornerWhereItWas()
    {
        // The round trip the chrome runs every frame. If settling and resolving disagreed by
        // a pixel, a resting window would walk that pixel sixty times a second.
        WindowRule settled = WindowAnchor.Settle(
            WindowRule.Free, ViewW - 360f, ViewH - 340f, 360f, 340f, ViewW, ViewH);

        (float x, float y) = WindowAnchor.Resolve(settled, ViewW, ViewH)!.Value;
        Assert.Equal(ViewW, x, 2);
        Assert.Equal(ViewH, y, 2);
    }

    [Fact]
    public void TheAnchorSurvivesAResolutionChange()
    {
        // Shares of the screen, not pixels: the same rule on a smaller monitor puts the window
        // in the same PLACE - the bottom-right corner - not at a pixel count that no longer
        // exists.
        WindowRule settled = WindowAnchor.Settle(
            WindowRule.Free, ViewW - 360f, ViewH - 340f, 360f, 340f, ViewW, ViewH);

        (float x, float y) = WindowAnchor.Resolve(settled, 1280f, 720f)!.Value;
        Assert.Equal(1280f, x, 2);
        Assert.Equal(720f, y, 2);
    }

    [Fact]
    public void AnUnplacedRuleResolvesToNothing()
    {
        Assert.Null(WindowAnchor.Resolve(WindowRule.Free, ViewW, ViewH));
    }

    [Fact]
    public void APlaceIsWorthSaving()
    {
        // Anything gates what the settings file stores; a rule that only remembers a place
        // must count, or every window forgets where it was on the next start.
        WindowRule settled = WindowAnchor.Settle(WindowRule.Free, 40f, 40f, 100f, 100f, ViewW, ViewH);

        Assert.True(settled.Anything);
        Assert.True(settled.Placed);
    }

    [Fact]
    public void TrackingFollowsAWindowPastTheEdgeWithoutPullingItBack()
    {
        // The drag tracker deliberately does NOT clamp: yanking a window back while somebody
        // is still holding it is the fight the whole arrangement exists to avoid. The clamp
        // belongs to the release, which Settle carries.
        (double x, double y, int pivotX, int pivotY) =
            WindowAnchor.Track(ViewW - 100f, ViewH - 50f, 300f, 200f, ViewW, ViewH);

        Assert.Equal(1, pivotX);
        Assert.Equal(1, pivotY);
        Assert.True(x > 1.0);
        Assert.True(y > 1.0);
    }

    [Fact]
    public void TrackingAndSettlingAgreeWhereverBothApply()
    {
        // The release frame asserts what was TRACKED, and Measure then persists what was
        // SETTLED. For a window already inside the screen those must be the same numbers,
        // or letting go of a window would nudge it.
        const float X = ViewW - 360f;
        const float Y = ViewH - 340f;

        (double tx, double ty, int tpx, int tpy) = WindowAnchor.Track(X, Y, 360f, 340f, ViewW, ViewH);
        WindowRule settled = WindowAnchor.Settle(WindowRule.Free, X, Y, 360f, 340f, ViewW, ViewH);

        Assert.Equal(settled.X!.Value, tx, 6);
        Assert.Equal(settled.Y!.Value, ty, 6);
        Assert.Equal(settled.PivotX, tpx);
        Assert.Equal(settled.PivotY, tpy);
    }
}
