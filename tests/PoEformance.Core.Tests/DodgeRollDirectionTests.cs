using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which way a dodge roll actually goes - measured off the player's own rolls.
/// </summary>
/// <remarks>
/// THE QUESTION THE EVASION FEATURE RESTS ON, and it was answered by assumption until this was
/// written. The feature shipped saying "the roll goes where the character is already pointing",
/// which is close enough to true to be dangerous: it is an AXIS, not a direction.
///
/// <c>tests/fixtures/session-2026-08-monsters.rec</c> holds five rolls - three DodgeRoll
/// (animation 268) and two DodgeRollBack (402). Measured against <c>Render.RotationCurrent</c>,
/// which follows the mouse, taken at the END of each roll:
///
/// <code>
///   anim  travel  angle from facing   turned during the roll
///   402      520              178.6                      49
///   268      391                1.8                      15
///   268      141               32.4                     103
///   402      232              166.9                      61
///   268      509                0.0                      59
/// </code>
///
/// A roll runs along the FACING LINE - forwards for 268, backwards for 402 - and never across
/// it. The separation is clean with nothing near the middle. The two loose readings are the two
/// with the largest turn inside the roll, so the direction tracks the mouse as it moves rather
/// than being fixed when the roll starts; that is a reading of five rolls, not a measurement,
/// and it is written down as the hypothesis it is.
///
/// Two consequences, and both change what the feature can do:
///
///  1. YOU CANNOT ROLL SIDEWAYS. A slam landing to your left cannot be rolled away from while
///     you point north - the character has to turn first, which means the mouse. Any evasion
///     that assumes a free choice of direction is wrong about the game.
///  2. THE COMMON CASE MAY NEED NO STEERING. You are usually facing the monster attacking you,
///     so "away from the threat" is backwards along the axis you already hold.
///
/// WHAT THIS DOES NOT SETTLE, and it is the half the feature needs:
///
///   - WHAT CHOOSES BACKWARDS OVER FORWARDS. Both appear in one session, so it is something the
///     player did; the movement input is the obvious candidate and no recording can see it.
///   - WHAT HAPPENS UNDER WASD. THIS FIXTURE IS NOT WASD. The owner switched the game to
///     click-to-move to make these recordings, and normally plays with WASD - so every roll
///     measured here is a click-to-move roll, and the mode the tool has to work in is the one
///     nothing has been recorded of. Anything below describes click-to-move only.
///
/// The dodge's own ActionWrapper target is NOT the destination, which is worth writing down
/// because it looks like one: it sits within a few cells of where the roll STARTED and reads a
/// reach of 33-34 units against a travel of hundreds.
/// </remarks>
public class DodgeRollDirectionTests
{
    private const string Fixture = "session-2026-08-monsters.rec";

    /// <summary>The animation the game plays for a forward roll.</summary>
    private const int DodgeRoll = 268;

    /// <summary>...and for a backward one.</summary>
    private const int DodgeRollBack = 402;

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

    /// <summary>One roll: the frames it spans, where it went, and where the player pointed.</summary>
    private sealed record Roll(
        int Animation, float FromX, float FromY, float ToX, float ToY, float EndFacing, float StartFacing)
    {
        public double Distance => Math.Sqrt(((ToX - FromX) * (ToX - FromX)) + ((ToY - FromY) * (ToY - FromY)));

        /// <summary>How far the character turned while rolling, in degrees.</summary>
        /// <remarks>
        /// The column that explains the loose readings: the facing follows the mouse, and a
        /// player who swings it mid-roll leaves the travel and the final facing disagreeing.
        /// </remarks>
        public double Turned => Math.Abs(Facing.Between(StartFacing, EndFacing)) * 180.0 / Math.PI;

        /// <summary>Degrees between the way it travelled and the way the character pointed.</summary>
        public double AngleFromFacing
        {
            get
            {
                (float faceX, float faceY) = Facing.Direction(EndFacing);
                double dx = (ToX - FromX) / Distance, dy = (ToY - FromY) / Distance;
                double dot = Math.Clamp((dx * faceX) + (dy * faceY), -1, 1);
                return Math.Acos(dot) * 180.0 / Math.PI;
            }
        }
    }

    /// <summary>Every roll in the fixture, from its first frame to its last.</summary>
    private static readonly Lazy<List<Roll>> Rolls = new(() =>
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var entities = new EntityReader(replay, schema);
        var render = new RenderReader(replay, schema);
        var actions = new ActionReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var rolls = new List<Roll>();
        ulong actor = 0, renderAddress = 0;

        // Open run: the animation, where it began, and the newest position and facing in it.
        (int Animation, float FromX, float FromY, float ToX, float ToY, float Facing, float StartFacing)? open = null;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (!chain.InGame)
            {
                continue;
            }

            if (actor == 0)
            {
                Entity? player = entities.Read(chain.PlayerEntity);
                actor = player?.Component("Actor") ?? 0;
                renderAddress = player?.Component("Render") ?? 0;
                if (actor == 0)
                {
                    continue;
                }
            }

            int animation = actions.Read(actor).AnimationId;
            bool rolling = animation is DodgeRoll or DodgeRollBack;

            if (!rolling)
            {
                if (open is { } finished)
                {
                    rolls.Add(new Roll(
                        finished.Animation, finished.FromX, finished.FromY,
                        finished.ToX, finished.ToY, finished.Facing, finished.StartFacing));
                    open = null;
                }

                continue;
            }

            if (render.Read(renderAddress) is not { } position
                || render.ReadFacing(renderAddress) is not (float angle, _))
            {
                continue;
            }

            // A new roll starts when there was none, or when the animation flipped between the
            // forward and backward one - those are two rolls, not one.
            open = open is { } running && running.Animation == animation
                ? running with { ToX = position.X, ToY = position.Y, Facing = angle }
                : (animation, position.X, position.Y, position.X, position.Y, angle, angle);
        }

        if (open is { } last)
        {
            rolls.Add(new Roll(
                last.Animation, last.FromX, last.FromY, last.ToX, last.ToY, last.Facing, last.StartFacing));
        }

        // Only rolls that actually went somewhere: a single sampled frame of one has no
        // direction to measure.
        return [.. rolls.Where(r => r.Distance > 50)];
    });

    [Fact]
    public void TheFixtureHoldsBothKindsOfRoll()
    {
        // The precondition, asserted first so a fixture that lost its rolls fails here with an
        // obvious message rather than making the measurements below pass vacuously.
        List<Roll> rolls = Rolls.Value;

        Assert.NotEmpty(rolls);
        Assert.Contains(rolls, r => r.Animation == DodgeRoll);
        Assert.Contains(rolls, r => r.Animation == DodgeRollBack);
    }

    [Fact]
    public void EachAnimationPicksOneEndOfTheFacingLine()
    {
        // THE FINDING, asserted at the strength the five rolls actually support: a forward roll
        // travels on the forward half of the facing and a backward one on the back half, with
        // nothing near the middle. That is what makes "roll away from the danger" a claim about
        // where the character POINTS rather than a free choice of direction.
        //
        // NOT asserted: that each is within a few degrees of exactly along it. Three of the five
        // are (0.0, 1.8, 166.9 counting from the far end), and the two that are not are the two
        // in which the player turned most while rolling - so the looseness has an explanation
        // that this fixture cannot test, and a tight bound here would be fitted to the data
        // rather than measured from it.
        foreach (Roll roll in Rolls.Value)
        {
            double angle = roll.AngleFromFacing;
            bool forward = roll.Animation == DodgeRoll;

            Assert.True(
                forward ? angle < 90 : angle > 90,
                $"a {(forward ? "forward" : "backward")} roll travelled {roll.Distance:F0} units at "
                + $"{angle:F1} degrees from the facing - it crossed to the other half, which is new behaviour");
        }
    }

    [Fact]
    public void ARollTakenWithoutTurningRunsAlmostExactlyAlongTheFacing()
    {
        // The tight version of the same claim, on the rolls that can carry it: where the player
        // barely turned during the roll, the travel is within a few degrees of the facing line.
        // This is the evidence that the axis is the facing and not something merely correlated
        // with it - and it is why the looser rolls are read as the mouse moving mid-roll.
        Roll[] steady = [.. Rolls.Value.Where(r => r.Turned < 30)];
        Assert.NotEmpty(steady);

        foreach (Roll roll in steady)
        {
            double offAxis = Math.Min(roll.AngleFromFacing, 180 - roll.AngleFromFacing);
            Assert.True(
                offAxis < 15,
                $"a roll taken while turning only {roll.Turned:F0} degrees ran {offAxis:F1} degrees off the facing line");
        }
    }

    [Fact]
    public void ARollCoversAUsefulDistance()
    {
        // Worth knowing for the feature that will use it: a roll moves hundreds of world units,
        // which is far enough to leave the landing spot of something aimed where you stand.
        Assert.All(Rolls.Value, roll => Assert.InRange(roll.Distance, 100, 1500));
    }

    [Fact]
    public void ADodgesOwnActionTargetIsNotWhereItLands()
    {
        // Said plainly because it looks exactly like a destination and is not one: the wrapper's
        // target for a roll sits within a few cells of where the roll STARTED. Anything reading
        // a player action's target as "where this is going" would be wrong here, and wrong in
        // the direction that matters - it would point back at the danger.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var entities = new EntityReader(replay, schema);
        var render = new RenderReader(replay, schema);
        var actions = new ActionReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int looked = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (!chain.InGame)
            {
                continue;
            }

            Entity? player = entities.Read(chain.PlayerEntity);
            ulong actor = player?.Component("Actor") ?? 0;
            ulong renderAddress = player?.Component("Render") ?? 0;
            if (actor == 0 || renderAddress == 0)
            {
                continue;
            }

            ActorAction action = actions.Read(actor);
            if (action.AnimationId is not (DodgeRoll or DodgeRollBack) || !action.HasTarget)
            {
                continue;
            }

            looked++;

            // The reach - target from the action's own origin - stays tiny while the character
            // crosses hundreds of units.
            Assert.True(action.Reach < 100, $"a roll's action reached {action.Reach:F0} units");
        }

        Assert.True(looked > 20, $"only {looked} roll frames carried a target");
    }
}
