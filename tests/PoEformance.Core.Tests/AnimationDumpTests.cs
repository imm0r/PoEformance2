using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The whole animation table, read out of the game and now the shipped file.
/// </summary>
/// <remarks>
/// WHAT THE FIRST DUMP FOUND, and it corrected an account this project had already written down.
/// The shipped table was NOT riddled with mistakes; it was a faithful table of an older patch.
/// Three rows have since been inserted into Data/Balance/Animation.dat:
///
/// <code>
///   584  AbyssalLivingBomb      +1 from here
///   599  AbyssalPact            +2 from here
///   904  RemidusDive            +3 from here
/// </code>
///
/// Every one of the old file's 1084 rows fits one of those three shifts EXACTLY, with zero
/// leftovers - which is what turns "500 rows are wrong" into "three rows were inserted". The
/// earlier reading of this, from a six-animation sample, was that the file "drifts a row at a
/// time" and that animation 889 was a single bad row. Both were wrong, and the second was worse
/// than wrong: 889 was hand-corrected in the file, which patched a symptom of the shift and left
/// the table internally inconsistent. A whole-table read was the only thing that could have told
/// the difference, and a six-row sample was never going to.
///
/// WHAT IT COST WHILE IT STOOD: 500 of 1084 ids named the wrong animation, 177 of them changing
/// AnimationKind, and 37 classified QUIET when the real animation is not - ElectricSpit read as
/// DodgeRollSprint, an empowered wyvern flame breath read as FixedRunLayerBaseForward. Those are
/// threats the evasion filter dropped in silence. <see cref="AnimationNames.IsQuiet"/> is asked
/// the safe way round precisely so that an unknown animation still counts; a confident WRONG name
/// walks straight past that guard.
/// </remarks>
public class AnimationDumpTests
{
    private const string Fixture = "session-2026-08-animdump.rec";

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

    private static AnimationNames Shipped()
        => AnimationNames.Load(Path.Combine(DirectoryHolding("data"), "data", "animations.tsv"));

    /// <summary>The dump, replayed. Cached - it walks eleven hundred rows.</summary>
    private static readonly Lazy<AnimationDumpResult> Dumped = new(() =>
    {
        string path = Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture);
        var replay = ReplayMemoryReader.Load(File.OpenRead(path));
        OffsetSchema schema = RealSessionTests.Schema();
        var dump = new AnimationDump(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (dump.Sample(gameStates))
            {
                break;
            }
        }

        // The walk's own reads were made at one moment during the live run; the last frame
        // carries the newest recorded value of every address, so it is the position that holds
        // them. Seeking here is not a detail - parked anywhere else the table reads as empty.
        if (replay.FrameCount > 0)
        {
            replay.Seek((uint)(replay.FrameCount - 1));
        }

        return dump.Read(Shipped());
    });

    [Fact]
    public void TheWholeTableIsReadableOffline()
    {
        // THE GAP PR #174 COULD NOT CLOSE. Its fixture held six rows, not the array - a recording
        // contains only the reads its build performed - so the walk had nothing to walk and only
        // the arithmetic was tested. This recording was made by the build that walks, so the walk
        // itself is now a regression rather than something that works until it does not.
        AnimationDumpResult dump = Dumped.Value;

        Assert.True(dump.Confirmed);
        Assert.NotEqual(dump.ConfirmedBy.First, dump.ConfirmedBy.Second);
        Assert.Equal(1087, dump.Names.Count);
        Assert.Equal(1086, dump.HighestId);
    }

    [Fact]
    public void TheShippedTableIsNowExactlyWhatTheGameSays()
    {
        // The shipped file IS this dump, so every row must match and nothing may be left over.
        // A failure here means somebody hand-edited the table, which is the one thing its header
        // asks nobody to do: the next dump would silently revert it.
        AnimationDumpResult dump = Dumped.Value;
        AnimationNames shipped = Shipped();

        Assert.Empty(dump.Changed);
        Assert.Empty(dump.Added);
        Assert.Empty(dump.Missing);

        foreach ((int id, string name) in dump.Names)
        {
            Assert.Equal(name, shipped.Of(id));
        }
    }

    [Fact]
    public void TheThreeInsertedRowsAreWhereTheShiftBegins()
    {
        // The finding itself, pinned. These three ids are the whole difference between the old
        // table and this one - everything else is those rows pushing their successors along.
        AnimationNames shipped = Shipped();

        Assert.Equal("AbyssalLivingBomb", shipped.Of(584));
        Assert.Equal("AbyssalPact", shipped.Of(599));
        Assert.Equal("RemidusDive", shipped.Of(904));

        // And the rows that used to sit at those ids are now one, two and three along.
        Assert.Equal("SigilOfPower", shipped.Of(585));
        Assert.Equal("SpinningInferno", shipped.Of(600));
        Assert.Equal("FloatTeleportForward", shipped.Of(905));
    }

    [Fact]
    public void TheAnimationsThatWereBeingFilteredAsQuietNoLongerAre()
    {
        // The cost of the stale table, as behaviour rather than as a name. Under the old file
        // these ids read as a dodge roll and a run, so IsQuiet dropped them and no warning was
        // ever drawn; they are an attack and a breath weapon.
        AnimationNames shipped = Shipped();

        Assert.Equal("ElectricSpit", shipped.Of(797));
        Assert.False(shipped.IsQuiet(797), "ElectricSpit must not be filtered as a quiet animation");

        Assert.Equal("ShapeshiftWyvernFlameBreathAttackEmpowered", shipped.Of(805));
        Assert.False(shipped.IsQuiet(805));
    }

    [Fact]
    public void TheSixAnimationsFromTheEarlierSessionStillReadTheSame()
    {
        // A cross-check between two independent recordings: the skills session named these by
        // following each cast's own wrapper, one row at a time; this one read the whole array off
        // a base. Two routes to the same six names is what makes either of them a measurement.
        AnimationNames shipped = Shipped();

        Assert.Equal("SparkAdditive", shipped.Of(299));
        Assert.Equal("SummonOffering", shipped.Of(407));
        Assert.Equal("Flamewall", shipped.Of(472));
        Assert.Equal("OrbOfStorms", shipped.Of(474));
        Assert.Equal("PowerSiphon", shipped.Of(501));
        Assert.Equal("ElementalWeakness", shipped.Of(889));
    }
}
