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

    /// <summary>
    /// A camera that is TILTED and off-axis, so a round trip is a real test.
    /// </summary>
    /// <remarks>
    /// The identity would pass a projection that swapped its columns, mixed up its signs or
    /// dropped the perspective divide, because every one of those is a no-op on it. This one
    /// has a different scale per axis, a z that feeds screen y (the tilt), a translation and a
    /// perspective w - so each of those mistakes moves the answer.
    ///
    /// Column-major, matching the reading Project() uses: element i of the clip vector is a dot
    /// with elements {i, i+4, i+8, i+12}.
    /// </remarks>
    private static float[] Tilted =>
    [
        // column for clip.x        clip.y      clip.z    clip.w
        0.031f, 0.019f, 0f, 0.0004f,
        -0.028f, 0.021f, 0f, -0.0003f,
        0f, -0.041f, 1f, 0f,
        0.05f, -0.12f, 0f, 1.6f,
    ];

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(37f, -18f, 0f)]
    [InlineData(-64f, 51f, 12f)]
    [InlineData(120f, 120f, -8f)]
    public void ProjectingAPointAndUnProjectingItGivesThePointBack(float x, float y, float z)
    {
        // The check that pins the inverse: a wrong column, a sign or a missed divide all
        // survive an identity matrix and none of them survives this.
        ScreenPoint screen = WorldToScreen.Project(Tilted, x, y, z, Width, Height);

        (float X, float Y)? back = WorldToScreen.OnGround(Tilted, screen.X, screen.Y, z, Width, Height);

        Assert.NotNull(back);
        Assert.Equal(x, back!.Value.X, 2);
        Assert.Equal(y, back.Value.Y, 2);
    }

    [Fact]
    public void ACircleOnTheGroundIsNotACircleOnTheScreen()
    {
        // The whole reason a cursor radius is measured in the world rather than in pixels. A
        // round area on the ground projects to an ELLIPSE, so a screen-pixel circle around the
        // cursor covers a ground region stretched away from the camera - a different shape
        // from the one any skill has.
        const float radius = 50f;
        float widest = 0f;
        float tallest = 0f;

        ScreenPoint centre = WorldToScreen.Project(Tilted, 0, 0, 0, Width, Height);
        for (int step = 0; step < 64; step++)
        {
            float angle = step / 64f * MathF.Tau;
            ScreenPoint edge = WorldToScreen.Project(
                Tilted, radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0, Width, Height);

            widest = MathF.Max(widest, MathF.Abs(edge.X - centre.X));
            tallest = MathF.Max(tallest, MathF.Abs(edge.Y - centre.Y));
        }

        Assert.True(widest > tallest * 1.2f,
            $"a ground circle should project wider than it is tall, got {widest:F1} x {tallest:F1}");
    }

    [Fact]
    public void APixelPointingPastTheHorizonHasNoGroundPoint()
    {
        // The ray meets the plane behind the viewer. The algebra hands back that point
        // happily, and it projects to a plausible pixel - so it is refused here rather than
        // left to a caller to notice.
        float[] behind =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, -1,
        ];

        Assert.Null(WorldToScreen.OnGround(behind, Width / 2f, Height / 2f, 0, Width, Height));
    }

    [Fact]
    public void ADegenerateViewHasNoAnswerRatherThanAHugeOne()
    {
        // A matrix that says nothing about y: every pixel names a line, not a point. A
        // near-zero divisor would hand back an enormous coordinate, which looks like a
        // position rather than like a failure.
        float[] flat =
        [
            1, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];

        Assert.Null(WorldToScreen.OnGround(flat, 100, 100, 0, Width, Height));
    }
}
