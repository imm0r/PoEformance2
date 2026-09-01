using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The two decoded hazards, on the path the overlay actually uses.
/// </summary>
/// <remarks>
/// ComponentSweepTests proves the offsets against the raw component bytes. This proves the other
/// half, which is where a decode usually dies quietly: that the values survive the trip through
/// WorldReader into a WorldSnapshot the layers can read. An offset that is right in a diagnostic
/// and never reaches a snapshot draws nothing, and looks from the screen exactly like an offset
/// that is wrong.
///
/// The tests are written against the same checks that earned the offsets rather than against the
/// numbers - a countdown that predicts the delisting, a beam whose near end is the entity's own
/// position - so they keep their meaning if the capture is ever re-made.
/// </remarks>
public class HazardReadingTests
{
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

    /// <summary>Every snapshot of the sweep capture, with the frame's timestamp in seconds.</summary>
    private static List<(double Seconds, WorldSnapshot Snapshot)> Snapshots(uint step = 5)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-sweep.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var result = new List<(double, WorldSnapshot)>();
        for (uint frame = 0; frame < replay.FrameCount; frame += step)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (snapshot.InGame)
            {
                result.Add((replay.FrameTimes[(int)frame] / 1000.0, snapshot));
            }
        }

        return result;
    }

    [Fact]
    public void GroundEffectsReachTheSnapshotWithTheirCountdown()
    {
        List<(double Seconds, WorldSnapshot Snapshot)> snapshots = Snapshots();
        List<WorldEntity> effects =
            [.. snapshots.SelectMany(s => s.Snapshot.Entities).Where(e => e.GroundSeconds is not null)];

        Assert.True(effects.Count > 200, $"only {effects.Count} readings carried a countdown");

        // Sane on every one. A garbage read must never become a number on screen, which is what
        // the reader's range guard is for - this is that guard, exercised against real memory.
        Assert.All(effects, e => Assert.InRange(e.GroundSeconds!.Value, 0f, 600f));

        // And it is only ever on ground effects. If this ever fires on a monster, the component
        // lookup is matching something else and the rings would land on the wrong things.
        Assert.All(effects, e => Assert.Contains("ground_effects", e.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The countdown still predicts the delisting after the trip through WorldReader.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the check that decoded the field, and the one that matters for
    /// a feature: the number the overlay would print has to be the number of seconds the patch
    /// of ground has left. Asserted through WorldSnapshot rather than through the component, so
    /// a reader change that dropped or stalled the value is caught here rather than on screen.
    /// </remarks>
    [Fact]
    public void TheCountdownInTheSnapshotStillNamesWhenTheGroundGoesOut()
    {
        List<(double Seconds, WorldSnapshot Snapshot)> snapshots = Snapshots();
        double lastSecond = snapshots[^1].Seconds;

        // Per entity, its readings in order.
        var tracks = new Dictionary<uint, List<(double Seconds, float Left)>>();
        foreach ((double seconds, WorldSnapshot snapshot) in snapshots)
        {
            foreach (WorldEntity e in snapshot.Entities.Where(e => e.GroundSeconds is not null))
            {
                tracks.TryAdd(e.Id, []);
                tracks[e.Id].Add((seconds, e.GroundSeconds!.Value));
            }
        }

        var errors = new List<double>();
        int expired = 0;
        foreach ((uint _, List<(double Seconds, float Left)> track) in tracks)
        {
            if (track.Count < 5 || track[^1].Seconds > lastSecond - 0.5)
            {
                continue; // still burning at the end of the capture - nothing to check against
            }

            expired++;
            double gone = track[^1].Seconds;
            errors.AddRange(track.Where(t => t.Left > 0.05).Select(t => t.Seconds + t.Left - gone));
        }

        Assert.True(expired >= 20, $"only {expired} effects burned out inside the capture");
        Assert.True(errors.Count > 100, $"only {errors.Count} predictions to judge by");

        errors.Sort();
        Assert.InRange(errors[errors.Count / 2], -0.7, -0.1);
        Assert.True(errors.Count(e => Math.Abs(e) < 1.5) > errors.Count * 0.9,
            "the countdown stopped predicting the delisting through the snapshot");
    }

    /// <summary>
    /// The radius candidate reaches the snapshot, and this test asserts nothing about what it is.
    /// </summary>
    /// <remarks>
    /// Deliberately weaker than the countdown's test, because the evidence is weaker. All that
    /// is known is the value: a float that reads 18.66-18.67 on every ground effect in the
    /// capture. It is in the right range for a radius in world units and an area needs one, but
    /// nothing has checked it against the burning patch, so this pins the READING and leaves the
    /// meaning alone. When the ring drawn at this size is seen to hug the patch - or not - the
    /// schema field gets renamed and this test gets a real claim to make.
    /// </remarks>
    [Fact]
    public void TheRadiusCandidateReachesTheSnapshot_WithoutAnythingClaimingItIsARadius()
    {
        List<WorldEntity> effects =
            [.. Snapshots().SelectMany(s => s.Snapshot.Entities).Where(e => e.GroundRadius is not null)];

        Assert.True(effects.Count > 200, $"only {effects.Count} readings carried the candidate");

        // One value for one effect type, to within float noise. If a capture ever shows two
        // clearly different sizes for two clearly different patches, that is the moment this
        // stops being a candidate - and this assertion is what will notice.
        Assert.All(effects, e => Assert.InRange(e.GroundRadius!.Value, 18.5f, 18.8f));

        // It does NOT travel with the countdown, and that assumption was wrong when it was
        // first written here: a third of these effects have no timer and every one of them
        // still has this value. Pinned the right way round, because the ring is sized from
        // this and drawn whether or not there is a number to put in it.
        Assert.Contains(effects, e => e.GroundSeconds is null);
        Assert.All(effects, e => Assert.True(e.IsGroundEffect));

        // And the schema's name carries the doubt, so nobody reads a claim into the field list.
        Assert.NotNull(RealSessionTests.Schema().Structs["GroundEffect"].Field("RadiusCandidate"));
        Assert.Null(RealSessionTests.Schema().Structs["GroundEffect"].Field("Radius"));
    }

    /// <summary>
    /// A third of ground effects have no timer, and the ring must not depend on one.
    /// </summary>
    /// <remarks>
    /// The bug this pins was live for one build: the layer skipped any effect whose countdown
    /// came back null, which would have left a third of the burning ground unmarked - the exact
    /// failure the feature exists to remove. The split is a fact about the game rather than
    /// about the read: 33 of 72 effects held NaN for their whole life, 39 held a number, and no
    /// entity ever crossed between the two.
    /// </remarks>
    [Fact]
    public void SomeGroundEffectsNeverCarryATimer_AndAreStillGroundEffects()
    {
        List<(double Seconds, WorldSnapshot Snapshot)> snapshots = Snapshots();

        // Per entity: did it ever show a number, and did it ever not?
        var withNumber = new HashSet<uint>();
        var withoutNumber = new HashSet<uint>();
        int marked = 0;
        foreach ((double _, WorldSnapshot snapshot) in snapshots)
        {
            foreach (WorldEntity e in snapshot.Entities.Where(e => e.IsGroundEffect))
            {
                marked++;
                _ = e.GroundSeconds is null ? withoutNumber.Add(e.Id) : withNumber.Add(e.Id);
            }
        }

        Assert.True(marked > 400, $"only {marked} readings were marked as ground effects");
        Assert.NotEmpty(withNumber);
        Assert.NotEmpty(withoutNumber);

        // NOT ONE entity in both sets. That is what makes an absent timer a kind of effect
        // rather than a flaky read, and it is the whole reason the ring ignores the countdown.
        Assert.Empty(withNumber.Intersect(withoutNumber));
    }

    /// <summary>
    /// The component is narrower than its name, and the rules are not made redundant by it.
    /// </summary>
    /// <remarks>
    /// Worth a test because the opposite is the natural assumption, and acting on it would mean
    /// deleting the path rules as superseded. Across every committed recording only two metadata
    /// paths carry a GroundEffect: the VisibleServerGroundEffect and one Incursion smoke ground.
    /// The GroundOnDeath monster mods - burning, shocked and chilled ground left by a dying
    /// monster - carry none, and are refused by the noise filter's Daemon class before their
    /// components are read at all.
    ///
    /// So a ring is a reliable YES about a hazard, and its absence says nothing either way.
    /// </remarks>
    [Fact]
    public void OnlyAFewPathsCarryTheComponent_SoTheRulesStillHaveAJob()
    {
        var carriers = new HashSet<string>(StringComparer.Ordinal);
        foreach ((double _, WorldSnapshot snapshot) in Snapshots())
        {
            foreach (WorldEntity e in snapshot.Entities.Where(e => e.IsGroundEffect))
            {
                carriers.Add(e.Path);
            }
        }

        Assert.NotEmpty(carriers);

        // A handful, not "everything that burns". If this ever grows a lot, the balance between
        // the two mechanisms has changed and the tab's warning needs revisiting.
        Assert.True(carriers.Count <= 4, $"{carriers.Count} paths carry it: {string.Join(", ", carriers)}");
        Assert.Contains(carriers, p => p.Contains("ground_effects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BeamsReachTheSnapshotAsALineAnchoredOnTheirOwnEntity()
    {
        List<WorldEntity> beams =
            [.. Snapshots().SelectMany(s => s.Snapshot.Entities).Where(e => e.Beam is not null)];

        Assert.True(beams.Count > 100, $"only {beams.Count} readings carried a beam");

        foreach (WorldEntity e in beams)
        {
            BeamLine line = e.Beam!.Value;

            // The near end IS the entity, exactly - the anchor the decode rests on, and the
            // thing that would break first if the offsets drifted.
            Assert.Equal(e.WorldX, line.SourceX);
            Assert.Equal(e.WorldY, line.SourceY);

            // And the far end is somewhere else, or there is no line to draw.
            Assert.True(line.Length > 1f, "a beam with no length reached the snapshot");
        }

        // Only beams. The layer draws a line across the screen for every one of these, so a
        // false positive is the most visible mistake this feature could make.
        Assert.All(beams, e => Assert.Contains("Beam", e.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnARecordingMadeBeforeTheDecode_NeitherHazardAppears()
    {
        // The reads did not happen in those sessions, so a replay cannot serve them - and the
        // reader must come back with null rather than with a zero that would ring the whole
        // screen or draw a line to the origin.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-deployed.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int ground = 0, checkedFrames = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame += 20)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (!snapshot.InGame)
            {
                continue;
            }

            checkedFrames++;
            ground += snapshot.Entities.Count(e => e.Path.Contains("ground_effects", StringComparison.OrdinalIgnoreCase));
            Assert.All(snapshot.Entities, e => Assert.Null(e.GroundSeconds));
            Assert.All(snapshot.Entities, e => Assert.Null(e.Beam));
        }

        Assert.True(checkedFrames > 20, $"only {checkedFrames} frames");

        // And the entities themselves ARE there - so the nulls above are about the missing
        // bytes rather than about the effects having been filtered out, which would make this
        // test pass for the wrong reason.
        Assert.True(ground > 100, $"only {ground} ground-effect entities in a file full of them");
    }

    [Fact]
    public void BothLayersAreOffUntilSomebodyAsksForThem()
    {
        // They draw over the fight. Defaulting either to on would put rings and lines on a
        // screen nobody asked to change, which is the one thing an overlay must not do on
        // upgrade - and the rule list above them keeps its own default untouched.
        Assert.False(TrackerSettings.Default.ShowGroundEffects);
        Assert.False(TrackerSettings.Default.ShowBeams);
        Assert.True(TrackerSettings.Default.ShowGroundDanger);

        // The timer defaults ON, because a ring without it says no more than the old rules did.
        Assert.True(TrackerSettings.Default.ShowGroundEffectTimer);

        // And the ring is NOT sized from the game any more. It was, while GroundEffect+0x38 was
        // believed to be the patch's radius - the point being that a ring at a size somebody
        // picked can never disagree with the patch it is drawn on, so it can never settle
        // anything either. It has since been settled the other way: the slot reads the same
        // 18.67 on every effect where it reads at all, and the reader rejected it on all 54
        // readings of a map capture. A ring sized from it is a ring sized from a constant.
        Assert.False(TrackerSettings.Default.GroundEffectUseGameRadius);
    }
}
