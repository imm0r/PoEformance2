using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the movement keys mean in world coordinates, measured against a real camera.
/// </summary>
/// <remarks>
/// THE QUESTION IS A REAL ONE AND IT HAS A WRONG ANSWER THAT LOOKS RIGHT. The evasion steering
/// decides which way to roll from monster positions in world units and then has to press W, A, S
/// or D - and those are screen directions. Get the conversion backwards and the tool rolls
/// confidently into the beam it was avoiding, with a status line saying it went the other way.
///
/// SO IT IS CHECKED AGAINST THE GAME'S OWN MATRIX rather than against an isometric constant:
/// <c>session-2026-08-monsters.rec</c> carries the camera as it was in a real fight, and the
/// decisive test below is that a step along the derived "up" direction lands DIRECTLY ABOVE the
/// player on the screen - same pixel column, smaller row. That is the definition of the thing
/// being derived, put to the projection rather than to an argument about it.
///
/// WHAT IT CANNOT CHECK is whether the game's forward key moves the character up the screen.
/// Nothing in a recording answers that; it is a fact about the controls, and it is the reason
/// steering ships switched off with the derived directions on show.
///
/// WHAT THE REAL CAMERA MEASURES, over the 1984 in-game frames of that fixture and recorded here
/// because it is the sort of thing that is expensive to rediscover:
///
///   up the screen    = world ( 0.7071,  0.7071)
///   right, on screen = world ( 0.7071, -0.7071)
///   worst departure from a right angle between them: 0.19 degrees
///
/// So the world axes run DIAGONALLY across the screen, at 45 degrees - the ordinary isometric
/// arrangement, and worth writing down because it means W moves the character along neither world
/// axis. And the two screen axes come back square in the world, which was not guaranteed: the
/// projection tilts and squashes the ground, and a basis that came back at 60 degrees would leave
/// the eight compass directions bunched into a fan rather than evenly spread. The scoring would
/// still work - it measures the vectors it is given - but "sideways" would not mean what it looks
/// like on the screen.
/// </remarks>
public class ScreenBasisTests
{
    private const string Fixture = "session-2026-08-monsters.rec";

    /// <summary>A realistic viewport. The derivation does not use it - the check does.</summary>
    private const int Width = 1920;
    private const int Height = 1080;

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", Fixture);
        }
    }

    /// <summary>Every in-game frame of the fixture, as a snapshot. Cached - it replays the lot.</summary>
    private static readonly Lazy<List<WorldSnapshot>> Frames = new(() =>
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var frames = new List<WorldSnapshot>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (snapshot is { InGame: true, Player: not null } && snapshot.Matrix.Length >= 16)
            {
                frames.Add(snapshot);
            }
        }

        return frames;
    });

    /// <summary>Frames whose camera actually yields a basis, with it.</summary>
    private static IEnumerable<(WorldSnapshot Snapshot, ScreenBasis Basis)> Derived()
    {
        foreach (WorldSnapshot snapshot in Frames.Value)
        {
            WorldEntity player = snapshot.Player!;
            if (ScreenBasis.Derive(snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ)
                is ScreenBasis basis)
            {
                yield return (snapshot, basis);
            }
        }
    }

    [Fact]
    public void ARealCameraYieldsABasis()
    {
        // The precondition. A fixture that lost its matrix would otherwise make every
        // measurement below pass vacuously by having nothing to measure.
        Assert.NotEmpty(Frames.Value);

        int derived = Derived().Count();
        Assert.True(
            derived > Frames.Value.Count * 0.9,
            $"only {derived} of {Frames.Value.Count} in-game frames produced a basis");
    }

    [Fact]
    public void TheDerivedDirectionsAreUnitVectors()
    {
        foreach ((_, ScreenBasis basis) in Derived())
        {
            Assert.Equal(1, Math.Sqrt((basis.UpX * basis.UpX) + (basis.UpY * basis.UpY)), 3);
            Assert.Equal(1, Math.Sqrt((basis.RightX * basis.RightX) + (basis.RightY * basis.RightY)), 3);
        }
    }

    [Fact]
    public void AStepAlongUpLandsDirectlyAboveThePlayerOnTheScreen()
    {
        // THE DECISIVE CHECK, and the reason this is worth a fixture rather than a made-up
        // matrix: it puts the derived direction back through the projection and asks the camera
        // whether it points where it claims. A sign error, a swapped column or a row-major
        // reading all fail here, and none of them fails a check on the vector's length.
        const float Far = 400f; // about a roll

        int checkedFrames = 0;
        foreach ((WorldSnapshot snapshot, ScreenBasis basis) in Derived())
        {
            WorldEntity player = snapshot.Player!;
            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, Width, Height);
            if (!at.OnScreen)
            {
                continue;
            }

            ScreenPoint up = WorldToScreen.Project(
                snapshot.Matrix,
                player.WorldX + (basis.UpX * Far),
                player.WorldY + (basis.UpY * Far),
                player.WorldZ,
                Width,
                Height);

            Assert.True(up.Y < at.Y - 10, $"a step 'up' moved the screen point from {at.Y:F0} to {up.Y:F0}");
            Assert.Equal(at.X, up.X, 1f);

            ScreenPoint right = WorldToScreen.Project(
                snapshot.Matrix,
                player.WorldX + (basis.RightX * Far),
                player.WorldY + (basis.RightY * Far),
                player.WorldZ,
                Width,
                Height);

            Assert.True(right.X > at.X + 10, $"a step 'right' moved the screen point from {at.X:F0} to {right.X:F0}");
            Assert.Equal(at.Y, right.Y, 1f);

            checkedFrames++;
        }

        Assert.True(checkedFrames > 100, $"only {checkedFrames} frames could be checked");
    }

    [Fact]
    public void TheTwoScreenAxesAreSquareInTheWorld()
    {
        // Not assumed - measured, and worth measuring because an isometric projection squashes
        // one ground axis against the other and there is no rule that says the inverse comes
        // back at a right angle. It does: 0.19 degrees at worst over the whole fixture, which is
        // what makes the diagonals genuine diagonals rather than a lopsided pair. The bound is
        // set a little above what was measured so a camera tweak is news rather than a failure.
        double worst = 0;
        foreach ((_, ScreenBasis basis) in Derived())
        {
            worst = Math.Max(worst, basis.OutOfSquareDegrees);
        }

        Assert.True(worst < 0.5, $"the screen axes came back {worst:F2} degrees off perpendicular");
    }

    [Fact]
    public void TheEightDirectionsAreEvenlySpread()
    {
        // What the square basis buys: consecutive compass directions sit 45 degrees apart, so
        // "the best of eight" really is a choice between eight and not between four and a
        // bunched-up remainder.
        (WorldSnapshot _, ScreenBasis basis) = Derived().First();
        IReadOnlyList<EscapeOption> options = Escape.Options(basis);

        var angles = options
            .Select(o => Math.Atan2(o.Y, o.X) * 180.0 / Math.PI)
            .Select(a => a < 0 ? a + 360 : a)
            .OrderBy(a => a)
            .ToList();

        for (int i = 1; i < angles.Count; i++)
        {
            Assert.Equal(45, angles[i] - angles[i - 1], 1);
        }
    }

    [Fact]
    public void ACameraThatCannotAnswerReturnsNothing()
    {
        // The guard that matters most, because the caller is about to press a direction key: a
        // matrix that is missing, empty or degenerate has to be tellable from one that says
        // "that way". Anything else presses a key chosen by arithmetic on zeroes.
        Assert.Null(ScreenBasis.Derive(new float[16], 0, 0, 0));
        Assert.Null(ScreenBasis.Derive([], 0, 0, 0));
        Assert.Null(ScreenBasis.Derive(new float[8], 0, 0, 0));
    }
}
