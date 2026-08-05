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
