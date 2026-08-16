using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which way an entity is facing, and which way it is turning to face.
/// </summary>
/// <remarks>
/// THE QUESTION BEHIND THIS IS "WHERE IS THAT MONSTER AIMING", and the answer turned out not
/// to be a target pointer. Nothing in GameHelper2 has one - grep its whole source for a target
/// entity on a component and there is nothing - while the game's own stat table carries
/// <c>action_required_target_facing_angle_tolerance_degrees</c> and
/// <c>active_skill_facing_angle_turn_duration_ms</c>. The game aims by ROTATING the actor to
/// an angle and firing once it is within a tolerance, so the aim is a facing.
///
/// The reference knows the pair and cannot place it: <c>Render.cs</c> has RotationCurrent and
/// RotationFuture commented out at 0xC0/0xC4, which are PoE1 offsets. This is where they went
/// in PoE2, found in the dissector by the owner and settled here against the recording that
/// found them - 53 seconds of standing still and turning on the spot.
///
/// WHAT NEITHER FIXTURE SETTLES, said here so nobody reads more into a passing test than it
/// claims: where zero points and which way the angle runs. The first recording has the player
/// standing still, so it holds no independent direction at all - and the second one, made by
/// walking about, turns out not to hold one either. THE CHARACTER FACES THE MOUSE CURSOR, not
/// its own path, which is measured below rather than asserted: it walks a straight line for
/// eleven steps while this angle sweeps a full circle. Fitting the angle against the movement
/// would have produced a confident, wrong convention.
/// </remarks>
public class FacingTests
{
    /// <summary>The turning session: 1528 frames, 53 s, the player standing still.</summary>
    private const string Standing = "session-2026-08-rotation.rec";

    /// <summary>The walking session: 22 s of moving about, facing led by the cursor.</summary>
    private const string Walking = "session-2026-08-rotation-walking.rec";

    private static string FixturePath => Fixture(Standing);

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    /// <summary>One sample of the pair, as the recording served it.</summary>
    private readonly record struct Turn(
        uint Frame, uint Time, float Current, float Future, float X = 0f, float Y = 0f);

    /// <summary>
    /// Every frame in which the recording can answer, read through the SCHEMA's own offsets.
    /// </summary>
    /// <remarks>
    /// Through the schema rather than from constants in the test, which is the point of having
    /// one: if a patch moves the pair, these tests fail with the schema rather than passing
    /// against numbers nobody updated.
    ///
    /// The Render address is resolved through the reader rather than pasted from the dissector,
    /// so the test also asserts that the block somebody dissected is the PLAYER's Render.
    /// </remarks>
    private static List<Turn> Read(string? fixture = null)
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(fixture is null ? FixturePath : Fixture(fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int current = schema.Structs["Render"].OffsetOf("RotationCurrent");
        int future = schema.Structs["Render"].OffsetOf("RotationFuture");

        var samples = new List<Turn>();
        Span<byte> pair = stackalloc byte[8];
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (world.Read(gameStates).Player is not WorldEntity player || player.Render == 0)
            {
                continue;
            }

            if (!replay.TryRead(player.Render + (ulong)current, pair))
            {
                continue; // the dissector was not looking at this block on this frame
            }

            samples.Add(new Turn(
                frame,
                replay.FrameTimes[(int)frame],
                BitConverter.ToSingle(pair),
                BitConverter.ToSingle(pair[(future - current)..]),
                player.WorldX,
                player.WorldY));
        }

        return samples;
    }

    /// <summary>The recording holds enough of the block to be asked anything.</summary>
    [Fact]
    public void TheTurningSessionCoversTheRotationPair()
    {
        List<Turn> samples = Read();

        // 1151 when this was written. A floor rather than the figure, because the assertion is
        // "there is a session in here", not "the recorder produced exactly this many frames".
        Assert.True(samples.Count > 1000, $"only {samples.Count} frames served the rotation pair");
        Assert.True(samples[^1].Time - samples[0].Time > 40_000, "less than 40 s of session");
    }

    /// <summary>
    /// Both fields are angles in 0..2pi, never negative and never past a full turn.
    /// </summary>
    /// <remarks>
    /// The cheapest thing that would catch the pair moving under a patch: whatever lands at
    /// these offsets afterwards is overwhelmingly unlikely to spend a whole session inside one
    /// turn of a circle. It is also the invariant the schema declares, so this checks the
    /// declaration against real memory rather than against itself.
    /// </remarks>
    [Fact]
    public void BothAnglesStayInsideOneTurn()
    {
        List<Turn> samples = Read();

        Assert.All(samples, sample =>
        {
            Assert.InRange(sample.Current, 0f, (float)(2 * Math.PI));
            Assert.InRange(sample.Future, 0f, (float)(2 * Math.PI));
        });

        // And they actually USE the circle - a field frozen at one value would pass the range
        // check while saying nothing. Measured: 155 distinct values of the current angle.
        Assert.True(samples.Select(s => s.Current).Distinct().Count() > 50);
    }

    /// <summary>
    /// A settled facing reads the same twice; a difference means a turn is in progress.
    /// </summary>
    /// <remarks>
    /// 90% of samples in this recording, which is what makes the difference between them worth
    /// drawing at all: it is not noise between two copies of one number, it appears only while
    /// the character is turning.
    /// </remarks>
    [Fact]
    public void TheTwoAgreeWheneverNothingIsTurning()
    {
        List<Turn> samples = Read();
        int settled = samples.Count(s => Math.Abs(Wrapped(s.Future - s.Current)) < 0.01);

        Assert.True(
            settled > samples.Count * 0.8,
            $"only {settled} of {samples.Count} samples had the pair agreeing");
    }

    /// <summary>
    /// WHICH OF THE TWO LEADS - the fact the naming rests on.
    /// </summary>
    /// <remarks>
    /// If <c>RotationFuture</c> is where the turn is going, the NEXT sample's
    /// <c>RotationCurrent</c> has to land nearer to it than to where the current angle was:
    /// the follower ends up where the leader already was. Measured on the 115 samples in which
    /// the two differ - 0.070 rad against 0.235 rad, and the future wins 109 of them.
    ///
    /// The control matters as much as the claim, and it is the half a wrong labelling would
    /// pass: asked the other way round, the current angle predicts the next future in 6% of
    /// samples. A pair of numbers that merely track each other would score alike both ways.
    /// </remarks>
    /// <remarks>
    /// Run against BOTH recordings, which is the whole reason the second one is committed. They
    /// share nothing about the situation - one stands still and turns on the spot, the other
    /// never stops moving - so a relation that holds in both is a fact about the fields rather
    /// than about what was being done at the time.
    /// </remarks>
    [Theory]
    [InlineData(Standing)]
    [InlineData(Walking)]
    public void TheFutureAngleIsWhereTheCurrentOneArrivesNext(string fixture)
    {
        List<Turn> samples = Read(fixture);

        int futureLeads = 0, currentLeads = 0, turning = 0;
        double futureError = 0, currentError = 0;

        for (int i = 0; i + 1 < samples.Count; i++)
        {
            Turn now = samples[i];
            if (Math.Abs(Wrapped(now.Future - now.Current)) < 0.01)
            {
                continue; // not turning - it can say nothing about which one leads
            }

            turning++;

            double toFuture = Math.Abs(Wrapped(samples[i + 1].Current - now.Future));
            double toCurrent = Math.Abs(Wrapped(samples[i + 1].Current - now.Current));
            futureError += toFuture;
            currentError += toCurrent;
            if (toFuture < toCurrent)
            {
                futureLeads++;
            }

            // The same question with the roles swapped, which is the control.
            if (Math.Abs(Wrapped(samples[i + 1].Future - now.Current))
                < Math.Abs(Wrapped(samples[i + 1].Future - now.Future)))
            {
                currentLeads++;
            }
        }

        Assert.True(turning > 50, $"only {turning} turning samples to judge from");
        Assert.True(
            futureLeads > turning * 0.85,
            $"the future angle predicted the next current one in only {futureLeads}/{turning}");
        Assert.True(
            futureError < currentError / 2,
            $"future error {futureError / turning:F3} rad was not clearly better than {currentError / turning:F3}");

        // The control: the relation must NOT hold in reverse.
        Assert.True(
            currentLeads < turning * 0.25,
            $"the current angle also predicted the next future one, in {currentLeads}/{turning} - "
            + "the two are not a leader and a follower");
    }

    /// <summary>
    /// THE FACING IS NOT THE DIRECTION OF TRAVEL, which is what makes the convention hard.
    /// </summary>
    /// <remarks>
    /// Here as a test rather than as a note in the schema because it is the mistake somebody
    /// WILL make - it is the first thing anybody tries, it is cheap to compute, and it produces
    /// a confident number instead of an obvious failure. The character faces the mouse cursor:
    /// in this recording it walks a straight line for eleven consecutive steps while the angle
    /// sweeps a full circle.
    ///
    /// So this asserts the negative on purpose. If a future patch ever makes facing follow the
    /// path, this test fails - and that failure is an invitation to settle the convention from
    /// it, not a defect.
    /// </remarks>
    [Fact]
    public void TheFacingDoesNotFollowTheDirectionOfTravel()
    {
        List<Turn> samples = Read(Walking);

        // Circular consistency of (angle - heading) over every step long enough to have a
        // direction. Near 1 would mean the two differ by a constant - a convention. Measured
        // at 0.35 for this reading and 0.08 for the mirrored one: both are noise.
        double sumX = 0, sumY = 0, mirrorX = 0, mirrorY = 0;
        int steps = 0;

        for (int i = 0; i + 1 < samples.Count; i++)
        {
            double dx = samples[i + 1].X - samples[i].X;
            double dy = samples[i + 1].Y - samples[i].Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < 3)
            {
                continue; // standing still says nothing about which way it is facing
            }

            steps++;
            double heading = Math.Atan2(dy, dx);
            sumX += Math.Cos(samples[i + 1].Current - heading);
            sumY += Math.Sin(samples[i + 1].Current - heading);
            mirrorX += Math.Cos(samples[i + 1].Current + heading);
            mirrorY += Math.Sin(samples[i + 1].Current + heading);
        }

        Assert.True(steps > 100, $"only {steps} steps to judge from");

        double sameSense = Math.Sqrt((sumX * sumX) + (sumY * sumY)) / steps;
        double mirrored = Math.Sqrt((mirrorX * mirrorX) + (mirrorY * mirrorY)) / steps;

        Assert.True(
            sameSense < 0.6 && mirrored < 0.6,
            $"the facing tracked the path after all (consistency {sameSense:F2} / {mirrored:F2}) - "
            + "if that is real, the convention can finally be read off it");
    }

    /// <summary>The shortest signed distance between two angles.</summary>
    private static double Wrapped(double radians)
    {
        while (radians > Math.PI)
        {
            radians -= 2 * Math.PI;
        }

        while (radians < -Math.PI)
        {
            radians += 2 * Math.PI;
        }

        return radians;
    }
}
