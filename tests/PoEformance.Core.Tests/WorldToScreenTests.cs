using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The world-to-screen projection. These pin the algebra with matrices whose result is
/// known by hand, so the only unknown left for the live game is whether the matrix bytes
/// and the player position are read correctly - which the real session then confirms.
/// </summary>
public class WorldToScreenTests
{
    private const int Width = 2560;
    private const int Height = 1440;

    /// <summary>An identity matrix maps the origin to the exact screen centre.</summary>
    [Fact]
    public void Identity_MapsOriginToScreenCentre()
    {
        float[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];

        ScreenPoint p = WorldToScreen.Project(identity, 0, 0, 0, Width, Height);

        Assert.True(p.OnScreen);
        Assert.Equal(Width / 2f, p.X, 3);
        Assert.Equal(Height / 2f, p.Y, 3);
        Assert.Equal(0.0, WorldToScreen.OffCentreFraction(p, Width, Height), 3);
    }

    /// <summary>
    /// NDC +1 in x is the right edge, +1 in y is the TOP (screen y grows downward, so the
    /// projection flips it). This locks the axis convention.
    /// </summary>
    [Fact]
    public void NdcCorners_MapToTheRightViewportEdges()
    {
        float[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];

        // World (1, 1, 0) -> NDC (1, 1) -> screen (right edge, top).
        ScreenPoint corner = WorldToScreen.Project(identity, 1, 1, 0, Width, Height);
        Assert.Equal(Width, corner.X, 3);
        Assert.Equal(0f, corner.Y, 3); // top
    }

    /// <summary>A point behind the camera (w &lt;= 0) is reported off-screen, never drawn.</summary>
    [Fact]
    public void BehindCamera_IsRejected()
    {
        // w-row = (0,0,0,-1) so w = -1 for any point: everything is behind the camera.
        float[] behind =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, -1,
        ];

        ScreenPoint p = WorldToScreen.Project(behind, 5, 5, 5, Width, Height);
        Assert.False(p.OnScreen);
    }

    /// <summary>
    /// The historic failure: a matrix whose w-row holds a huge value makes w enormous, so
    /// every world point collapses onto the screen centre. The projection stays finite and
    /// on-screen, but OffCentreFraction is ~0 for a point that is really far away - which is
    /// why the drift is caught by the schema invariant on the matrix, not by the projection.
    /// This test documents the failure mode so it is never mistaken for correct behaviour.
    /// </summary>
    [Fact]
    public void HugeWRow_CollapsesEverythingToCentre_TheDocumentedBug()
    {
        float[] broken =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 24951, // the real garbage value from issue #158
        ];
        float[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];

        // A point a correct matrix places OFF-screen (NDC 2,2)...
        Assert.False(WorldToScreen.Project(identity, 2, 2, 0, Width, Height).OnScreen);

        // ...the broken matrix collapses to near the centre and calls it on-screen.
        ScreenPoint collapsed = WorldToScreen.Project(broken, 2, 2, 0, Width, Height);
        Assert.True(collapsed.OnScreen);
        Assert.True(WorldToScreen.OffCentreFraction(collapsed, Width, Height) < 0.01);
    }

    [Fact]
    public void ShortMatrix_IsRejectedNotCrashed()
    {
        Assert.False(WorldToScreen.Project([1, 2, 3], 0, 0, 0, Width, Height).OnScreen);
    }
}
