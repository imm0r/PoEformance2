using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// What "up the screen" and "right on the screen" are, as world directions.
/// </summary>
/// <remarks>
/// THIS EXISTS BECAUSE MOVEMENT KEYS ARE A SCREEN-SPACE IDEA AND THREATS ARE A WORLD-SPACE ONE.
/// The planner decides which way to roll from monster positions in world units; the thing it can
/// actually press is W, and W means "away from the camera", which is a direction on the screen.
/// One of the two has to be converted, and this is the conversion.
///
/// DERIVED FROM THE GAME'S OWN MATRIX, never from an isometric constant. The tool has the
/// world-to-screen matrix and its inverse-on-a-plane already, so the answer can be measured every
/// tick rather than fitted once: project the player, step a fixed distance up the screen, ask
/// <see cref="WorldToScreen.OnGround"/> where that lands on the player's own ground plane, and
/// the difference is the world direction. A camera that ever changes - a cutscene, a different
/// aspect - is followed for free, and there is no constant to go stale.
///
/// THE NOMINAL VIEWPORT IS SQUARE AND ITS SIZE DOES NOT MATTER, which is worth stating because it
/// looks like a magic number. The projection is run forward and back through the SAME nominal
/// size, so the pixels cancel and only the direction survives; the square keeps the two axes
/// symmetric, so the aspect ratio of the real window cannot tilt the basis. And a straight world
/// line projects to a straight screen line, so the step length does not change the direction it
/// measures either - it is chosen large only to keep the arithmetic well away from rounding.
///
/// WHAT IS ASSUMED, and it is the one thing here that is not measured: that the game's forward
/// movement key moves the character UP THE SCREEN, and its right key to the right. That is how
/// camera-relative movement works everywhere, but nobody has put it to this game, so steering is
/// off by default and the derived directions are reported in the status line where they can be
/// compared against what the character actually does.
/// </remarks>
/// <param name="UpX">World x of a unit step up the screen.</param>
/// <param name="RightX">World x of a unit step right across the screen.</param>
public readonly record struct ScreenBasis(float UpX, float UpY, float RightX, float RightY)
{
    /// <summary>A square stand-in viewport. Cancels out - see the remarks on the type.</summary>
    private const int Nominal = 1000;

    /// <summary>How far to step, in nominal pixels. Direction is independent of this.</summary>
    private const float Step = 200f;

    /// <summary>
    /// Works out the basis at the player's own position, or null when the camera cannot answer.
    /// </summary>
    /// <param name="matrix">The world-to-screen matrix, as the snapshot carries it.</param>
    /// <remarks>
    /// Null rather than a fallback, on purpose. Every caller of this is about to press a
    /// direction key, and a guessed basis presses the WRONG direction key - which in the case
    /// this whole feature exists for means rolling into the beam rather than out of it. "I do not
    /// know which way is which" has to stay tellable from "it is that way".
    /// </remarks>
    public static ScreenBasis? Derive(ReadOnlySpan<float> matrix, float playerX, float playerY, float playerZ)
    {
        ScreenPoint at = WorldToScreen.Project(matrix, playerX, playerY, playerZ, Nominal, Nominal);
        if (!at.OnScreen)
        {
            return null;
        }

        // Screen y counts DOWNWARDS, so up the screen is a SMALLER y.
        if (Offset(matrix, at.X, at.Y - Step, playerX, playerY, playerZ) is not (float ux, float uy)
            || Offset(matrix, at.X + Step, at.Y, playerX, playerY, playerZ) is not (float rx, float ry))
        {
            return null;
        }

        return new ScreenBasis(ux, uy, rx, ry);
    }

    /// <summary>The world direction of a set of movement keys, as a unit vector.</summary>
    /// <remarks>
    /// Diagonals are the SUM OF THE UNIT AXES rather than a screen-space diagonal, because that
    /// is what the game is doing: each key contributes its own direction and the character moves
    /// along the total. Taking a 45-degree line on the screen instead would bring the window's
    /// aspect ratio into an answer it has no business in.
    ///
    /// Opposed keys cancel to nothing, which is correct - holding left and right together is not
    /// a direction - and the caller gets a zero vector it can reject.
    /// </remarks>
    public (float X, float Y) World(MoveDirection direction)
    {
        float x = 0, y = 0;

        if ((direction & MoveDirection.Up) != 0)
        {
            x += UpX;
            y += UpY;
        }

        if ((direction & MoveDirection.Down) != 0)
        {
            x -= UpX;
            y -= UpY;
        }

        if ((direction & MoveDirection.Right) != 0)
        {
            x += RightX;
            y += RightY;
        }

        if ((direction & MoveDirection.Left) != 0)
        {
            x -= RightX;
            y -= RightY;
        }

        float length = MathF.Sqrt((x * x) + (y * y));
        return length < 1e-4f ? (0, 0) : (x / length, y / length);
    }

    /// <summary>How square the basis is, in degrees away from a right angle.</summary>
    /// <remarks>
    /// A property of the camera worth being able to ask about rather than assume: an isometric
    /// view tilts and squashes the ground, and whether the two screen axes still come back
    /// perpendicular IN THE WORLD decides whether "sideways" means what it looks like. Measured
    /// against a real matrix in the tests instead of being taken on faith.
    /// </remarks>
    public double OutOfSquareDegrees
    {
        get
        {
            double dot = (UpX * RightX) + (UpY * RightY);
            return Math.Abs(Math.Acos(Math.Clamp(dot, -1, 1)) * 180.0 / Math.PI - 90.0);
        }
    }

    /// <summary>A unit world step from the player towards a screen pixel, or null.</summary>
    private static (float X, float Y)? Offset(
        ReadOnlySpan<float> matrix, float screenX, float screenY, float playerX, float playerY, float playerZ)
    {
        if (WorldToScreen.OnGround(matrix, screenX, screenY, playerZ, Nominal, Nominal) is not (float wx, float wy))
        {
            return null;
        }

        float dx = wx - playerX, dy = wy - playerY;
        float length = MathF.Sqrt((dx * dx) + (dy * dy));

        // A step that went nowhere means the plane is edge-on to the camera or the inverse
        // disagreed with the projection. Either way there is no direction in it.
        return length < 1e-3f ? null : (dx / length, dy / length);
    }
}
