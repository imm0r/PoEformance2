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
/// keyed on animation id? The notes said the plumbing existed and the answer was unmeasured.
/// This is the measurement, from a recording made with both switches on
/// (<c>ReadVisualEntities</c> and <c>KeepEffects</c>) during a real map boss.
///
/// NO WAVE IN THIS ONE - the owner reports the boss is drawn at random and this one had none -
/// so the wave itself is still unobserved. What the recording settles is the mechanism, and it
/// settles it in three parts, two encouraging and one not:
///
/// 1. HOSTILE EFFECTS ARRIVE AND SOME OF THEM MOVE, cleanly. PermanentEffect is up to three at
///    once and produces 1304 consecutive-frame movements, EVERY ONE of them under 200 units and
///    none over 1000. That last figure is what makes it a measurement rather than an artefact:
///    the first pass at this tracked entities by (path, id) and reported ten-thousand-unit
///    "steps", which was the game reusing an id between two different effects. Tracked by RENDER
///    address across consecutive frames only, the noise disappears from this path entirely.
/// 2. STATIONARY DANGER LOOKS DIFFERENT, and that difference is readable. The ground effects
///    (VisibleServerGroundEffect, seven at once) move exactly zero. So "is this hazard coming at
///    me" is answerable from position over time, without knowing what the skill is.
/// 3. THE PATH NAMES NOTHING. Effect, PermanentEffect, SleepableEffect, BeamEffect,
///    ServerEffect - engine words, not skill names. So geometry can be observed and IDENTITY
///    cannot, which splits the original question rather than answering it: a table may still be
///    wanted for what a thing IS, while what it is DOING is readable from the world.
///
/// AND THE BARRIER IS CONFIRMED ON DATA. Every one of the 6864 sightings under
/// Metadata/Projectiles is FRIENDLY - the player's own Spark. Not one monster projectile
/// classified as <see cref="EntityKind.Projectile"/> in a thousand frames of a boss fight, which
/// is what the schema comment predicted from a different session: a monster's projectile is
/// filed under the monster's own path. <c>ProjectileWatch</c> follows your projectiles, not
/// theirs.
///
/// IT ALSO PROVES THE SWITCHES HELD. A recording contains only reads the running build actually
/// performed, so hostile effects being IN this file means KeepEffects was on when the process
/// started - which is the thing three attempts were needed to fix.
/// </remarks>
public class HostileEffectTests
{
    private const string Fixture = "session-2026-08-effects.rec";

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

    /// <summary>One path's behaviour over the recording.</summary>
    private readonly record struct Seen(int MostAtOnce, int Mine, int Theirs, List<double> Steps);

    private static readonly Lazy<Dictionary<string, Seen>> Watched = new(() =>
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
        var found = new Dictionary<string, Seen>();

        // BY RENDER ADDRESS, and only between CONSECUTIVE frames. Both halves matter: the game
        // reuses entity ids, so a (path, id) key silently follows one effect into another and
        // reports the gap between them as a step.
        var before = new Dictionary<ulong, (float X, float Y, uint Frame)>();

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);

            var here = new Dictionary<string, int>();
            foreach (WorldEntity entity in snapshot.Entities)
            {
                if (entity.Kind is not (EntityKind.Effect or EntityKind.Projectile) && !entity.IsEffect)
                {
                    continue;
                }

                here[entity.Path] = here.GetValueOrDefault(entity.Path) + 1;

                Seen was = found.GetValueOrDefault(entity.Path, new Seen(0, 0, 0, []));
                found[entity.Path] = was with
                {
                    Mine = was.Mine + (entity.IsFriendly ? 1 : 0),
                    Theirs = was.Theirs + (entity.IsFriendly ? 0 : 1),
                };

                ulong key = entity.Render != 0 ? entity.Render : entity.Address;
                if (before.TryGetValue(key, out var last) && last.Frame == frame - 1)
                {
                    double moved = Math.Sqrt(
                        ((entity.WorldX - last.X) * (entity.WorldX - last.X))
                        + ((entity.WorldY - last.Y) * (entity.WorldY - last.Y)));

                    if (moved > 0.5)
                    {
                        found[entity.Path].Steps.Add(moved);
                    }
                }

                before[key] = (entity.WorldX, entity.WorldY, frame);
            }

            foreach ((string seenPath, int count) in here)
            {
                Seen was = found[seenPath];
                found[seenPath] = was with { MostAtOnce = Math.Max(was.MostAtOnce, count) };
            }
        }

        return found;
    });

    /// <summary>The recording carries what only a build with both switches on could record.</summary>
    /// <remarks>
    /// The verification that could not be done from here: a recording contains only reads the
    /// running build performed, so hostile effects in the file mean the switches were on when
    /// the process started, and therefore that they survived a restart.
    /// </remarks>
    [Fact]
    public void TheSwitchesWereOnWhenThisWasRecorded()
    {
        Dictionary<string, Seen> watched = Watched.Value;

        Assert.Contains("Metadata/Effects/Effect", watched);
        Assert.Contains("Metadata/Projectiles/Spark", watched);
        Assert.True(watched["Metadata/Effects/Effect"].Theirs > 1000);
    }

    /// <summary>A hostile effect that travels, with no reuse artefacts in the measurement.</summary>
    /// <remarks>
    /// THE ONE THAT MATTERS to the danger model: a hazard's movement is readable from the world
    /// with nothing named and no table consulted. "None over 1000" is the assertion that earns
    /// the rest - a single reused render address would put one there.
    /// </remarks>
    [Fact]
    public void AHostileEffectMovesAndTheMovementIsReal()
    {
        Seen permanent = Watched.Value["Metadata/Effects/PermanentEffect"];

        Assert.Equal(0, permanent.Mine);
        Assert.True(permanent.Theirs > 2000);
        Assert.True(permanent.Steps.Count > 1000, $"only {permanent.Steps.Count} steps");
        Assert.DoesNotContain(permanent.Steps, step => step > 1000);
    }

    /// <summary>Ground effects sit still, which is what makes moving ones worth noticing.</summary>
    [Fact]
    public void GroundEffectsDoNotMoveAtAll()
    {
        Seen ground = Watched.Value["Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect"];

        Assert.True(ground.Theirs > 1000);
        Assert.Empty(ground.Steps);
    }

    /// <summary>
    /// Not one monster projectile arrived as a projectile, over a whole boss fight.
    /// </summary>
    /// <remarks>
    /// The schema predicted this from a different session - a monster's skill spawns its
    /// projectile under the MONSTER's path, which classifies as a Monster and carries no Life,
    /// so the hostile-effect rule drops it. This is the prediction meeting a thousand frames of
    /// a boss fight: every sighting under Metadata/Projectiles is the player's own.
    /// </remarks>
    [Fact]
    public void EveryProjectileHereIsThePlayersOwn()
    {
        foreach ((string path, Seen seen) in Watched.Value)
        {
            if (path.StartsWith("Metadata/Projectiles/", StringComparison.Ordinal))
            {
                Assert.Equal(0, seen.Theirs);
                Assert.True(seen.Mine > 0);
            }
        }

        Assert.Contains("Metadata/Projectiles/Spark", Watched.Value);
    }

    /// <summary>The paths are engine words, so identity cannot come from them.</summary>
    /// <remarks>
    /// Worth pinning because it is the half that does NOT work, and the half a design would
    /// otherwise assume. Nothing here says "wave", "slam" or the name of any skill.
    /// </remarks>
    [Fact]
    public void NoEffectPathNamesTheSkillBehindIt()
    {
        string[] hostile =
        [
            .. Watched.Value.Where(w => w.Value.Theirs > 0 && w.Value.Mine == 0).Select(w => w.Key),
        ];

        Assert.NotEmpty(hostile);
        Assert.All(hostile, path => Assert.StartsWith("Metadata/Effects/", path, StringComparison.Ordinal));
    }
}
