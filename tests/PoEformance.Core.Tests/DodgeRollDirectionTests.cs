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
/// WHERE A ROLL GOES IS THE CURSOR, AND THIS FILE MEASURES SOMETHING ELSE - which is the whole
/// reason it is worth keeping. The owner, who plays this game, states it plainly: the roll goes
/// towards the mouse. Two minutes spent in front of a map boss steering with the mouse alone,
/// while the tool supplied only the timing, cost zero hits.
///
/// What the five rolls in <c>tests/fixtures/session-2026-08-monsters.rec</c> measure is the
/// angle between where the character TRAVELLED and <c>Render.RotationCurrent</c>, which is the
/// model's own rotation:
///
/// <code>
///   anim  travel  angle from RotationCurrent   turned during the roll
///   402      520                        178.6                      49
///   268      391                          1.8                      15
///   268      141                         32.4                     103
///   402      232                        166.9                      61
///   268      509                          0.0                      59
/// </code>
///
/// THE FINDING IS A WARNING, NOT A STEERING RULE. For the forward animation the rotation points
/// along the travel; for the BACKWARD one it points the opposite way, because that is what a
/// backward roll is - the body keeps its orientation while the character moves the other way. So
/// <c>RotationCurrent</c> DOES NOT SAY WHERE A ROLL IS GOING, and anything reading it during one
/// gets the direction exactly reversed half the time. That is the trap this file exists to mark.
///
/// AN EARLIER READING OF THE SAME NUMBERS WAS WRONG, and how it was wrong is worth keeping. It
/// took the rotation for the cursor direction, concluded that a roll can only run along the line
/// the character already faces, and wrote down that sideways was unavailable and that arming the
/// dodge was "a coin toss on the axis you already hold". None of that survives contact with the
/// game: the cursor steers, so any direction is available, and the player chooses it. A second
/// guess - that the facing locks onto a target during a backward roll - was checked against this
/// same fixture and does not hold either: the nearest monster during those frames is 1100 world
/// units away and 45 to 124 degrees off the rotation. Two wrong explanations from one set of
/// correct measurements, which is what happens when a number is asked what it means instead of a
/// person who can see the screen.
///
/// WHAT THE TOOL TAKES FROM THIS. The evasion feature supplies TIMING and the player supplies
/// DIRECTION, and that division is not a limitation to be engineered away - it is why the thing
/// works. The action fields say when something is committed and where it lands, before the
/// animation shows it; the hand on the mouse says where to go.
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
    public void TheModelsRotationIsReversedForABackwardRoll()
    {
        // THE WARNING. For the forward animation the rotation runs with the travel and for the
        // backward one against it, cleanly, with nothing near the middle. So a reader that takes
        // RotationCurrent for the roll's direction is exactly reversed on every 402 - and that
        // is not a rare case, it is two of the five rolls here.
        //
        // NOT asserted: that each sits within a few degrees of the line. Three of the five do;
        // the two that do not are the two in which the player turned most while rolling, and a
        // tight bound over all five would be fitted to the data rather than measured from it.
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
    public void ARollTakenWithoutTurningLinesUpWithTheModel()
    {
        // The tight version, on the rolls that can carry it: where the player barely turned, the
        // travel is within a few degrees of the model's own axis. That is what makes the
        // reversal above a property of the animation rather than noise.
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
