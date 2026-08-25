namespace PoEformance.Game.World;

/// <summary>A projected screen point plus whether it is in front of the camera.</summary>
/// <param name="X">Screen x in pixels.</param>
/// <param name="Y">Screen y in pixels.</param>
/// <param name="OnScreen">True when the point is in front of the camera and within the viewport.</param>
public readonly record struct ScreenPoint(float X, float Y, bool OnScreen);

/// <summary>
/// Projects a world position to a screen pixel using the world-to-screen matrix.
/// </summary>
/// <remarks>
/// This is the payoff of locating the matrix: it turns a world coordinate into a place on
/// the screen, which is what every overlay dot, every "click here" and the final matrix
/// proof all need.
///
/// The matrix is the game's combined view-projection, row-major, 4x4. For a world point
/// P = (x, y, z, 1) the clip coordinates are M * P; dividing x and y by w gives normalised
/// device coordinates in [-1, 1], which map to the viewport. A w &lt;= 0 means the point is
/// behind the camera and must not be drawn - the historic projection bug was exactly a bad
/// matrix blowing w up so every point collapsed onto the screen centre, which is why the
/// caller is told <see cref="ScreenPoint.OnScreen"/> explicitly rather than getting a
/// silently-wrong pixel.
/// </remarks>
public static class WorldToScreen
{
    /// <summary>
    /// Projects (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) through
    /// the 16-float <paramref name="matrix"/> onto a
    /// <paramref name="width"/> x <paramref name="height"/> viewport.
    /// </summary>
    /// <remarks>
    /// The matrix is stored COLUMN-MAJOR in memory (proven 2026-08 by projecting the real
    /// player position: only the column convention put it at screen centre, NDC (0,0), where
    /// the camera-following-player guarantees it must be; the row-major reading was 0.64 off).
    /// So a clip component is a DOT WITH A COLUMN of the flat array:
    /// element indices {0,4,8,12} for x, {1,5,9,13} for y, {3,7,11,15} for w.
    /// </remarks>
    public static ScreenPoint Project(ReadOnlySpan<float> matrix, float x, float y, float z, int width, int height)
    {
        if (matrix.Length < 16)
        {
            return new ScreenPoint(0, 0, false);
        }

        // Column-major: clip.i = matrix[column i] . (x, y, z, 1).
        double cx = ((double)matrix[0] * x) + ((double)matrix[4] * y) + ((double)matrix[8] * z) + matrix[12];
        double cy = ((double)matrix[1] * x) + ((double)matrix[5] * y) + ((double)matrix[9] * z) + matrix[13];
        double cw = ((double)matrix[3] * x) + ((double)matrix[7] * y) + ((double)matrix[11] * z) + matrix[15];

        if (cw <= 1e-6)
        {
            return new ScreenPoint(0, 0, false); // behind the camera
        }

        double ndcX = cx / cw;
        double ndcY = cy / cw;

        float screenX = (float)((ndcX * 0.5 + 0.5) * width);
        float screenY = (float)((0.5 - ndcY * 0.5) * height);

        bool onScreen = ndcX is >= -1 and <= 1 && ndcY is >= -1 and <= 1;
        return new ScreenPoint(screenX, screenY, onScreen);
    }

    /// <summary>
    /// The world point a screen pixel is pointing at, on the flat plane at a given height.
    /// </summary>
    /// <remarks>
    /// The projection run backwards, and it needs a PLANE because it is not invertible on its
    /// own: a pixel names a ray through the world, and only fixing one coordinate picks a
    /// point on it. The height to fix is the player's, which makes this "where on the ground
    /// the cursor is" - the question a targeted skill asks, and one this cannot answer for a
    /// cursor over a ledge or a staircase, because the ground there is not at that height.
    ///
    /// Fixing z turns the projection into two linear equations in x and y:
    ///
    ///     (m0 - ndcX*m3)x + (m4 - ndcX*m7)y = -((m8  - ndcX*m11)z + (m12 - ndcX*m15))
    ///     (m1 - ndcY*m3)x + (m5 - ndcY*m7)y = -((m9  - ndcY*m11)z + (m13 - ndcY*m15))
    ///
    /// solved by Cramer's rule. Same column-major reading as <see cref="Project"/>, because
    /// the two have to agree exactly - a round trip through both is what pins this, and it
    /// would catch a column swap that neither one alone would show.
    ///
    /// The AHK tool does the same job by INVERTING ITS ISOMETRIC CONSTANT - a fitted scale
    /// and sin(38.7 degrees). That works because its ring is drawn with the same constant. The
    /// matrix is available here and is the game's own answer, so there is nothing to fit.
    /// </remarks>
    /// <returns>The world point, or null when the view is degenerate or the plane is behind the camera.</returns>
    public static (float X, float Y)? OnGround(
        ReadOnlySpan<float> matrix, float screenX, float screenY, float groundZ, int width, int height)
    {
        if (matrix.Length < 16 || width <= 0 || height <= 0)
        {
            return null;
        }

        double ndcX = (2.0 * screenX / width) - 1.0;
        double ndcY = 1.0 - (2.0 * screenY / height);

        double a1 = matrix[0] - (ndcX * matrix[3]);
        double b1 = matrix[4] - (ndcX * matrix[7]);
        double c1 = ((matrix[8] - (ndcX * matrix[11])) * groundZ) + matrix[12] - (ndcX * matrix[15]);

        double a2 = matrix[1] - (ndcY * matrix[3]);
        double b2 = matrix[5] - (ndcY * matrix[7]);
        double c2 = ((matrix[9] - (ndcY * matrix[11])) * groundZ) + matrix[13] - (ndcY * matrix[15]);

        double determinant = (a1 * b2) - (a2 * b1);

        // A camera looking straight along the plane - the ray never meets it, or meets it
        // everywhere. Neither has an answer, and a huge one from a near-zero divisor would be
        // worse than none: it looks like a position.
        if (Math.Abs(determinant) < 1e-9)
        {
            return null;
        }

        double x = ((-c1 * b2) + (b1 * c2)) / determinant;
        double y = ((-a1 * c2) + (c1 * a2)) / determinant;

        // Behind the camera. The algebra is happy to hand back the point where the ray meets
        // the plane BEHIND the viewer, which projects to a perfectly plausible pixel - the
        // same trap Project() reports through OnScreen rather than leaving to the caller.
        double w = (matrix[3] * x) + (matrix[7] * y) + (matrix[11] * groundZ) + matrix[15];
        if (w <= 1e-6)
        {
            return null;
        }

        return ((float)x, (float)y);
    }

    /// <summary>
    /// Distance in pixels from the screen centre, as a fraction of half the smaller
    /// viewport dimension. The camera follows the player, so projecting the PLAYER's world
    /// position should yield a tiny value - this is the decisive, self-contained check that
    /// the matrix and the player-position read are BOTH correct.
    /// </summary>
    public static double OffCentreFraction(ScreenPoint point, int width, int height)
    {
        double half = Math.Min(width, height) / 2.0;
        if (half <= 0)
        {
            return double.PositiveInfinity;
        }

        double dx = point.X - (width / 2.0);
        double dy = point.Y - (height / 2.0);
        return Math.Sqrt((dx * dx) + (dy * dy)) / half;
    }
}
