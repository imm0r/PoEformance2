using PoEformance.Core.Memory;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where a monster is pointing, and what it is doing while it points there.
/// </summary>
/// <remarks>
/// The two halves are useless apart, which is why they travel as one <see cref="Aim"/>: the
/// angle says a monster faces you and a monster walking past faces you too; the animation says
/// a slam is starting and cannot say where it will land.
/// </remarks>
public class AimTests
{
    /// <summary>The animation table as it ships, found the way the app finds it.</summary>
    private static AnimationNames Table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "animations.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return AnimationNames.Load(Path.Combine(dir.FullName, "data", "animations.tsv"));
    }

    /// <summary>
    /// THE TABLE IS ALIGNED, checked against ids somebody watched in a live game.
    /// </summary>
    /// <remarks>
    /// Not a formality: the stat table in the same folder is off by one, so "the numbers line
    /// up" is a real question and not an assumption. These were recorded by the AHK tool while
    /// chasing the Actor offset drift - idling, running, dodging, and three skills whose cast
    /// types it saw go past - and every one lands on the right row here. Independent readings by
    /// another tool are what make this a check rather than a restatement of the file.
    ///
    /// ALL OF THEM SIT BELOW ANIMATION 584, which is not a coincidence and is why the eighth
    /// reading moved out into its own test below. The table was re-read from the game in 2026-08
    /// and three rows have been inserted since the AHK tool saw these: at 584, 599 and 904. Every
    /// id below the first insertion is unmoved, so these still cross two tools AND two game
    /// versions. The one above it is off by exactly two.
    /// </remarks>
    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(4, "Run")]
    [InlineData(195, "FixedRun")]
    [InlineData(268, "DodgeRoll")]
    [InlineData(402, "DodgeRollBack")]
    [InlineData(299, "SparkAdditive")]
    [InlineData(472, "Flamewall")]
    [InlineData(474, "OrbOfStorms")]
    public void TheShippedTableMatchesWhatWasWatchedInGame(int id, string expected)
        => Assert.Equal(expected, Table().Of(id));

    [Fact]
    public void TheOneWatchedIdAboveTheInsertionsMovedByExactlyTwo()
    {
        // THE EIGHTH READING, and it is now evidence rather than a broken assertion. The AHK tool
        // watched a sprint END and saw animation id 872. That was true of the game it was
        // watching; two rows have since been inserted below it (584 AbyssalLivingBomb and 599
        // AbyssalPact), so in the current game the same behaviour reports 874 and 872 is the
        // sprint itself.
        //
        // WHY THIS IS WORTH A TEST RATHER THAN A DELETION: it is a live observation from a
        // DIFFERENT TOOL, at the only one of its eight ids that sits above an insertion point,
        // landing exactly where the insertion story predicts. Nothing about the dump that
        // produced the current table could have arranged that. It is the outside check on a
        // finding that otherwise rests on one recording.
        AnimationNames names = Table();

        Assert.Equal("SprintEnd", names.Of(874));
        Assert.Equal("Sprint", names.Of(872));
    }

    [Fact]
    public void TheShippedTableIsWholeAndUnnumberedIdsKeepTheirNumber()
    {
        AnimationNames names = Table();

        Assert.True(names.Count > 1000, $"only {names.Count} animation names loaded");
        Assert.Null(names.Of(999_999));
        Assert.Equal("999999", names.Label(999_999));
    }

    [Theory]
    [InlineData("GroundSlam", AnimationKind.Slam)]
    [InlineData("ShapeshiftBearSlamEnraged", AnimationKind.Slam)]
    [InlineData("MoltenCrashMoving", AnimationKind.Slam)]
    [InlineData("LeapAttack", AnimationKind.Leap)]
    [InlineData("ChargeStart", AnimationKind.Charge)]
    [InlineData("CastFast", AnimationKind.Casting)]
    [InlineData("MeleeWithStep", AnimationKind.Attacking)]
    [InlineData("SpectralThrow", AnimationKind.Attacking)]
    [InlineData("Death", AnimationKind.Dying)]
    [InlineData("TakeHit", AnimationKind.Hurt)]
    [InlineData("FixedRun", AnimationKind.Moving)]
    [InlineData("DodgeRoll", AnimationKind.Moving)]
    [InlineData("Idle", AnimationKind.Idle)]
    public void NamesAreClassifiedByWhatTheyContain(string name, AnimationKind expected)
        => Assert.Equal(expected, AnimationNames.Classify(name));

    /// <summary>
    /// "LeapSlam" is a slam, because the slam is the part that hurts.
    /// </summary>
    /// <remarks>
    /// Two rules match it, so the ORDER of the table decides - which makes this a test about
    /// the order rather than about either rule. It is here so that reordering them for some
    /// other name has to be a decision rather than an accident.
    /// </remarks>
    [Fact]
    public void ANameThatMatchesTwoRulesTakesTheFirstOne()
        => Assert.Equal(AnimationKind.Slam, AnimationNames.Classify("LeapSlam"));

    /// <summary>
    /// AN ANIMATION NOBODY HAS A NAME FOR IS NOT QUIET, which is the whole safety of the filter.
    /// </summary>
    /// <remarks>
    /// The ray layer hides monsters that are idling or walking. Asked the other way round -
    /// "is this dangerous" - every unrecognised animation would become silently harmless, and
    /// the table has 1084 entries while the game keeps adding monsters. A marker that vanishes
    /// on an unknown id reads as "nothing is happening", which is the one thing a danger
    /// overlay must not say.
    /// </remarks>
    [Fact]
    public void AnUnknownAnimationCountsAsSomethingHappening()
    {
        AnimationNames names = Table();

        Assert.Equal(AnimationKind.Unknown, names.KindOf(999_999));
        Assert.False(names.IsQuiet(999_999));

        Assert.True(names.IsQuiet(0));    // Idle
        Assert.True(names.IsQuiet(195));  // FixedRun
        Assert.False(names.IsQuiet(13));  // GroundSlam
    }

    [Fact]
    public void AnAimKnowsWhetherItIsStillTurningAndWhichWay()
    {
        var settled = new Aim(1.5f, 1.5f);
        Assert.False(settled.IsTurning);
        Assert.Equal(0f, settled.Turn, 4);

        var turning = new Aim(1.0f, 1.6f);
        Assert.True(turning.IsTurning);
        Assert.Equal(0.6f, turning.Turn, 4);

        // ACROSS THE WRAP, which is where a plain subtraction says nearly a full turn and the
        // monster has in fact moved a tenth of a radian.
        var wrapping = new Aim(6.2f, 0.1f);
        Assert.True(Math.Abs(wrapping.Turn) < 0.3f, $"turn across zero read as {wrapping.Turn}");
    }

    /// <summary>The reader only pays for facing while something is drawing it.</summary>
    [Fact]
    public void AimIsReadOnlyWhenSomethingWillDrawIt()
    {
        Assert.False(TrackerSettings.Default.NeedsAim);
        Assert.True((TrackerSettings.Default with
        {
            Aim = AimSettings.Default with { Enabled = true },
        }).NeedsAim);
    }

    [Fact]
    public void AimSettingsAreKeptInsideWhatCanBeDrawn()
    {
        AimSettings wild = AimSettings.Default with { Length = 9000f, Thickness = 0f };
        AimSettings safe = wild.Normalised();

        Assert.InRange(safe.Length, 5f, 400f);
        Assert.InRange(safe.Thickness, 0.5f, 10f);
    }

    /// <summary>
    /// The whole path, against real memory: switch it on and the player has a facing.
    /// </summary>
    /// <remarks>
    /// Through <see cref="WorldReader"/> rather than through <see cref="RenderReader"/> alone,
    /// because the thing being checked is the WIRING - the gate, the entity kinds it applies to,
    /// and the offsets coming out of the schema.
    ///
    /// The animation comes back as -1 here, and that is correct rather than a gap: a replay only
    /// serves reads the running build performed, and the build that made this recording never
    /// read an Actor component. It is the case worth having a test for anyway - an entity that
    /// has a facing and no animation must not read as animation zero, which is Idle, which the
    /// ray filter would hide.
    /// </remarks>
    [Fact]
    public void TheReaderFillsTheFacingFromARealSessionOnlyWhenAsked()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string fixture = Path.Combine(
            dir.FullName, "tests", "fixtures", "session-2026-08-rotation-clickmove.rec");

        var replay = ReplayMemoryReader.Load(File.OpenRead(fixture));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        // Off: nothing pays for it, and nothing carries it.
        replay.Seek(600);
        Assert.Null(world.Read(gameStates).Player?.Aim);

        // On: the player's facing is there, in the range the game keeps these in.
        world.ReadAim = true;
        int found = 0;
        for (uint frame = 500; frame < 900 && found < 20; frame += 5)
        {
            replay.Seek(frame);
            if (world.Read(gameStates).Player?.Aim is not Aim aim)
            {
                continue;
            }

            found++;
            Assert.InRange(aim.Angle, 0f, (float)(2 * Math.PI));
            Assert.InRange(aim.Turning, 0f, (float)(2 * Math.PI));
            Assert.Equal(-1, aim.Animation);
        }

        Assert.True(found >= 20, $"only {found} frames carried a facing");
    }
}
