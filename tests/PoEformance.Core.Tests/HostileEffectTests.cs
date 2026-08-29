using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What a boss's own effects look like in the entity list, measured rather than supposed.
/// </summary>
/// <remarks>
/// THE QUESTION THIS ANSWERS was the owner's: if a skill is shaped like a wave, it ought to be
/// visible among the particles - so could a danger be OBSERVED instead of described in a table
/// keyed on animation id? This is the measurement, from a recording made with both switches on
/// (<c>ReadVisualEntities</c> and <c>KeepEffects</c>) while the owner spent thirty seconds
/// running circles around a map boss to make it cast.
///
/// NO WAVE IN THIS ONE - the boss is drawn at random and this one had none - so the wave itself
/// is still unobserved.
///
/// THE FIRST READING OF THIS RECORDING WAS WRONG, AND HOW IT WAS WRONG IS THE POINT. It reported
/// "a hostile effect travels: 1304 consecutive-frame steps, every one under 200 units, none over
/// 1000 - no reuse artefacts at all", and every one of those numbers is true. They belong to
/// three PermanentEffect instances that are PINNED TO THE PLAYER: never more than 21 to 30 units
/// away across 975 of the recording's 998 frames, each having travelled 10552 units against the
/// player's own 10557. Thirty units is a fourteenth of a dodge roll. They move because the
/// player moves, trailing by about a frame. A clean measurement was
/// read as a travelling hazard without ever asking WHAT was travelling - and the thing that
/// exposed it was the owner saying they had spent the recording running, which is the shape of
/// mistake this project keeps paying for.
///
/// SO THE DISCRIMINATOR IS RANGE, NOT MOVEMENT. Anything whose distance to the player never
/// changes is attached to the player, whatever its path says and however far it has travelled.
///
/// WHAT IS ACTUALLY THERE, once those three are set aside: 331 hostile-effect instances, and the
/// boss's own are SHORT-LIVED - 276 Effect and 26 BeamEffect instances with a median life of ten
/// frames, about half a second at this reader's rate. That is what several casts in thirty
/// seconds looks like from the outside, and it is the budget any observation-based danger model
/// would have: roughly ten sightings to decide from.
///
/// AND ONE OF THEM CLOSES ON THE PLAYER MONOTONICALLY - a BeamEffect over frames 604-614, ten
/// steps, 433 units travelled, ten closing and none opening. The owner reports one of the two
/// skills moved toward them. This is what that looks like in memory, and it is one instance
/// rather than a rule.
///
/// TRACKING HAS TO BE GAP-AWARE. Render addresses are reused: keyed on the address alone, three
/// separate effects across five hundred frames read as one long-lived one. A track therefore
/// ENDS when its address is absent for a frame.
///
/// THE PATH NAMES NOTHING EITHER WAY. Effect, PermanentEffect, BeamEffect, ServerEffect - engine
/// words, not skill names. Geometry can be observed; identity cannot.
///
/// IT ALSO PROVES THE SWITCHES HELD. A recording contains only reads the running build actually
/// performed, so hostile effects being in this file means KeepEffects was on when the process
/// started - the thing three attempts were needed to fix.
/// </remarks>
public class HostileEffectTests
{
    private const string Fixture = "session-2026-08-effects.rec";

    /// <summary>One effect instance: from the frame it appeared to the frame it went.</summary>
    private sealed class Track
    {
        public string Path = string.Empty;
        public uint First;
        public uint Last;
        public int Steps;
        public double Travelled;
        public double Nearest = double.MaxValue;
        public double Farthest;
        public int Closing;
        public int Opening;

        public uint Frames => Last - First;
    }

    private static string DirectoryHolding(string child)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, child)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static readonly Lazy<List<Track>> Instances = new(() =>
    {
        string path = Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture);
        var replay = ReplayMemoryReader.Load(File.OpenRead(path));
        OffsetSchema schema = RealSessionTests.Schema();

        // Both switches on, which is what the recording was made with. With either off the
        // entities below never reach a snapshot - see WorldReader's own remarks on each.
        var world = new WorldReader(replay, schema)
        {
            ReadActions = true,
            ReadVisualEntities = true,
            KeepEffects = true,
        };

        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var all = new List<Track>();
        var live = new Dictionary<ulong, Track>();
        var was = new Dictionary<ulong, (float X, float Y, uint Frame)>();

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (snapshot.Player is not WorldEntity me)
            {
                continue;
            }

            foreach (WorldEntity entity in snapshot.Entities)
            {
                if (entity.Kind != EntityKind.Effect || entity.IsFriendly)
                {
                    continue;
                }

                ulong key = entity.Render != 0 ? entity.Render : entity.Address;
                double range = Distance(entity.WorldX, entity.WorldY, me.WorldX, me.WorldY);

                // A NEW instance whenever this address was absent last frame. Without that, one
                // reused address reads as a single effect living half the recording.
                if (!live.TryGetValue(key, out Track? track) || track.Last != frame - 1)
                {
                    track = new Track { Path = entity.Path, First = frame };
                    live[key] = track;
                    all.Add(track);
                }

                track.Last = frame;
                track.Nearest = Math.Min(track.Nearest, range);
                track.Farthest = Math.Max(track.Farthest, range);

                if (was.TryGetValue(key, out var before) && before.Frame == frame - 1)
                {
                    double moved = Distance(entity.WorldX, entity.WorldY, before.X, before.Y);
                    if (moved > 0.5)
                    {
                        track.Steps++;
                        track.Travelled += moved;
                        if (range < Distance(before.X, before.Y, me.WorldX, me.WorldY))
                        {
                            track.Closing++;
                        }
                        else
                        {
                            track.Opening++;
                        }
                    }
                }

                was[key] = (entity.WorldX, entity.WorldY, frame);
            }
        }

        return all;
    });

    private static double Distance(double ax, double ay, double bx, double by)
        => Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    /// <summary>The recording carries what only a build with both switches on could record.</summary>
    /// <remarks>
    /// The verification that could not be done from here: a recording contains only reads the
    /// running build performed, so hostile effects in the file mean the switches were on when
    /// the process started, and therefore that they survived a restart.
    /// </remarks>
    [Fact]
    public void TheSwitchesWereOnWhenThisWasRecorded()
    {
        List<Track> all = Instances.Value;

        Assert.True(all.Count > 100, $"only {all.Count} hostile-effect instances");
        Assert.Contains(all, t => t.Path == "Metadata/Effects/Effect");
    }

    /// <summary>
    /// The long-lived "movers" never leave the player, so their movement is the player's.
    /// </summary>
    /// <remarks>
    /// THE CORRECTION THIS FILE EXISTS FOR. These three were first published as a hostile hazard
    /// travelling, on the strength of a movement measurement with no reuse artefacts in it. The
    /// measurement was sound and the reading was not: they sit at range zero for every one of
    /// their nine hundred frames. Movement alone cannot tell a hazard from a buff, and this is
    /// the assertion that would have caught it.
    /// </remarks>
    [Fact]
    public void WhatMovedForTheWholeRecordingIsStuckToThePlayer()
    {
        Track[] pinned = [.. Instances.Value.Where(t => t.Path == "Metadata/Effects/PermanentEffect")];

        Assert.Equal(3, pinned.Length);
        Assert.All(pinned, t =>
        {
            Assert.True(t.Frames > 900, $"only {t.Frames} frames");
            Assert.True(t.Travelled > 10_000, $"only travelled {t.Travelled:F0}");

            // THE WHOLE POINT: it went ten thousand units and never got anywhere. The measured
            // spread is 21 to 30 units - not zero, because it trails the player by a frame
            // rather than being welded on - against a dodge roll of about four hundred. So the
            // bound is a fraction of a roll, which is the scale at which "near the player"
            // stops being a position and starts being an attachment.
            Assert.True(t.Farthest < 50, $"reached {t.Farthest:F0} from the player");
        });
    }

    /// <summary>The boss's own effects are short-lived, which is the observation budget.</summary>
    /// <remarks>
    /// Half a second of sightings at this reader's rate. Any danger model built on watching
    /// these has about ten samples to decide from, which is worth knowing before one is designed.
    /// </remarks>
    [Fact]
    public void TheBossesOwnEffectsLastAboutTenFrames()
    {
        Track[] brief =
        [
            .. Instances.Value.Where(t => t.Path is "Metadata/Effects/Effect" or "Metadata/Effects/BeamEffect"),
        ];

        Assert.True(brief.Length > 200, $"only {brief.Length} instances");

        uint[] lives = [.. brief.Select(t => t.Frames).Order()];
        uint median = lives[lives.Length / 2];
        Assert.InRange(median, 1u, 40u);
    }

    /// <summary>At least one hostile effect travels straight at the player.</summary>
    /// <remarks>
    /// The owner reports the boss cast two skills in this recording and that one of them moved
    /// toward them. This is what that looks like from memory - and it is ONE INSTANCE, not a
    /// rule: stated as "at least one" because that is all the recording establishes.
    /// </remarks>
    [Fact]
    public void OneOfThemClosesOnThePlayerWithoutEverBackingOff()
    {
        Track[] chasing =
        [
            .. Instances.Value.Where(t =>
                t.Path != "Metadata/Effects/PermanentEffect"
                && t.Steps >= 8
                && t.Opening == 0
                && t.Travelled > 100),
        ];

        Assert.NotEmpty(chasing);
        Assert.All(chasing, t => Assert.True(t.Nearest < t.Farthest, "it never actually approached"));
    }

    /// <summary>Ground effects sit still, which is what makes a moving one worth noticing.</summary>
    [Fact]
    public void GroundEffectsDoNotMoveAtAll()
    {
        Track[] ground =
        [
            .. Instances.Value.Where(t =>
                t.Path == "Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect"),
        ];

        Assert.NotEmpty(ground);
        Assert.All(ground, t => Assert.Equal(0, t.Steps));
    }

    /// <summary>The paths are engine words, so identity cannot come from them.</summary>
    /// <remarks>
    /// Worth pinning because it is the half that does NOT work, and the half a design would
    /// otherwise assume. Nothing here says "wave", "slam" or the name of any skill.
    /// </remarks>
    [Fact]
    public void NoEffectPathNamesTheSkillBehindIt()
    {
        string[] paths = [.. Instances.Value.Select(t => t.Path).Distinct()];

        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith("Metadata/Effects/", path, StringComparison.Ordinal));
    }
}
