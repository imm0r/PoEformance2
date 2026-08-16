namespace PoEformance.Game.World;

/// <summary>
/// Which way an entity is pointing, and how that angle relates to the world axes.
/// </summary>
/// <remarks>
/// ONE COPY OF THE CONVENTION. The angle comes out of the Render component as a bare float and
/// means nothing without knowing where its zero is; every consumer that works that out for
/// itself is a consumer that can get it wrong in its own way, and a marker drawn ninety degrees
/// off looks like a projection bug rather than like a misread field.
///
/// ZERO POINTS ALONG WORLD -Y, and the angle runs the same way round as
/// <c>atan2(dy, dx)</c> - so it is the ordinary mathematical sense, rotated a quarter turn.
///
/// MEASURED, not assumed, and the measurement needed a particular recording. The character
/// FACES THE MOUSE CURSOR, not its path, so a session played on WASD says nothing about the
/// convention however much walking is in it - in one such recording the player walks a straight
/// line for eleven steps while the angle sweeps a full circle. With movement bound to the mouse
/// the two coincide, and over the 108 settled, moving samples of
/// <c>tests/fixtures/session-2026-08-rotation-clickmove.rec</c> the offset comes out at a
/// median of 89.76 degrees (5th-95th percentile 89.48-91.08), with 94% of steps inside five
/// degrees of a right angle. See <c>FacingTests</c>, which checks this class against that file.
/// </remarks>
public static class Facing
{
    /// <summary>
    /// How far the game's zero sits from the world's +X axis: a quarter turn.
    /// </summary>
    /// <remarks>
    /// Positive because the angle is AHEAD of the heading - a character walking towards +X
    /// reads pi/2, not -pi/2.
    /// </remarks>
    public const float ZeroOffset = MathF.PI / 2f;

    /// <summary>A full turn, for wrapping.</summary>
    private const float FullTurn = MathF.PI * 2f;

    /// <summary>The world-space direction an entity with this facing is pointing.</summary>
    /// <remarks>
    /// A UNIT vector, so a caller that wants a point some distance ahead multiplies rather than
    /// normalising. Z is deliberately absent: the angle says nothing about up and down, and
    /// anything drawn from it belongs at the entity's own height.
    /// </remarks>
    public static (float X, float Y) Direction(float angle)
        => (MathF.Sin(angle), -MathF.Cos(angle));

    /// <summary>A point <paramref name="distance"/> world units ahead of where something is.</summary>
    public static (float X, float Y) Ahead(float x, float y, float angle, float distance)
    {
        (float dx, float dy) = Direction(angle);
        return (x + (dx * distance), y + (dy * distance));
    }

    /// <summary>The facing that would point along a world-space step.</summary>
    /// <remarks>
    /// The inverse of <see cref="Direction"/>, and the form the measurement was made in: a
    /// character walking a heading reads that heading plus a quarter turn. Wrapped into
    /// 0..2pi, which is the range the game itself keeps the field in.
    /// </remarks>
    public static float FromHeading(float dx, float dy)
        => Wrap(MathF.Atan2(dy, dx) + ZeroOffset);

    /// <summary>The shortest way round from one facing to another, in -pi..pi.</summary>
    /// <remarks>
    /// The reason anything here needs a helper at all: subtracting two angles across the wrap
    /// gives nearly a full turn where the real difference is a degree, and a turn-in-progress
    /// test built on the raw difference fires constantly at one particular compass bearing.
    /// </remarks>
    public static float Between(float from, float to)
    {
        float difference = (to - from) % FullTurn;
        if (difference > MathF.PI)
        {
            difference -= FullTurn;
        }
        else if (difference < -MathF.PI)
        {
            difference += FullTurn;
        }

        return difference;
    }

    /// <summary>Brings an angle into the 0..2pi the game keeps these in.</summary>
    public static float Wrap(float angle)
    {
        float wrapped = angle % FullTurn;
        return wrapped < 0 ? wrapped + FullTurn : wrapped;
    }
}
