using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game naming its own animations, and catching the shipped table out on one.
/// </summary>
/// <remarks>
/// WHAT WAS BEING LOOKED FOR AND WHAT WAS FOUND ARE NOT THE SAME THING, and that is the honest
/// summary. <c>--skillhunt</c> went after the SKILL's id so that a threat could be more than a
/// line on the ground. What it reached instead was the ANIMATION's row, and the difference took
/// a measurement to establish rather than a glance: five of the six names it produced match
/// <c>data/animations.tsv</c> word for word.
///
/// THE PROOF THAT IT IS THE ANIMATION TABLE is the stride. Over the six ids the recording
/// contains, the row address is <c>base + id * 106</c> EXACTLY - the same 106 bytes across every
/// gap, including one of 388 ids. A row array indexed by animation id is the animation table, and
/// the companion pointer beside it names the file: <c>Data/Balance/Animation.dat</c>.
///
/// SO THE SKILL ID IS STILL NOT REACHED. Within 0x400 of the wrapper the only dat file referenced
/// is Animation.dat, and two hops out of <c>Actor.CurrentSkillPtr</c> reached no text at all.
/// That is worth stating plainly rather than letting a good-looking name stand in for it.
///
/// WHAT IT IS WORTH ANYWAY, and it is not nothing: the then-shipped table was hand-maintained -
/// its own header said a name is "a LABEL, never a fact" - and the very first session put to it
/// caught a disagreement at animation 889, InteractLeanWell in the file and ElementalWeakness in
/// the game. From six samples that read as one stale row and was patched as one. It was not:
/// reading the whole array afterwards (<c>--animdump</c>, see <c>AnimationDumpTests</c>) showed
/// three rows inserted since, shifting 500 of the file's 1084 rows. SIX SAMPLES CAN SAY THAT
/// SOMETHING IS WRONG AND NEVER WHAT, which is the lesson worth keeping from this file.
///
/// A name is what <see cref="AnimationNames.KindOf"/> classifies, and that classification decides
/// whether an animation is quiet enough to ignore - so a wrong name is a mis-filtered threat, and
/// an id the file has never heard of is one the tool can only report as a number.
/// </remarks>
public class AnimationNamesFromTheGameTests
{
    private const string Fixture = "session-2026-08-skills.rec";

    /// <summary>The stride of a Data/Balance/Animation.dat row, measured.</summary>
    private const int RowStride = 106;

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

    private static string FixturePath => Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture);

    private static AnimationNames Shipped()
        => AnimationNames.Load(Path.Combine(DirectoryHolding("data"), "data", "animations.tsv"));

    /// <summary>Replays the session with learning on, and hands back what it learned.</summary>
    private static readonly Lazy<(AnimationNames Names, Dictionary<int, ulong> Rows)> Session = new(() =>
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var entities = new EntityReader(replay, schema);
        AnimationNames names = Shipped();
        var actions = new ActionReader(replay, schema) { Names = names };
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int wrapperAt = schema.Structs["Actor"].OffsetOf("SkillActionPtr");
        int rowAt = schema.Structs["ActionWrapper"].OffsetOf("AnimationRow");

        var rows = new Dictionary<int, ulong>();
        ulong actor = 0;

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
                actor = entities.Read(chain.PlayerEntity)?.Component("Actor") ?? 0;
                if (actor == 0)
                {
                    continue;
                }
            }

            ActorAction action = actions.Read(actor);

            // The row addresses, kept alongside so the stride can be measured independently of
            // whatever the reader concluded.
            ulong wrapper = replay.ReadPointer(actor + (ulong)wrapperAt);
            if (action.AnimationId > 0 && MemoryReaderExtensions.IsPlausiblePointer(wrapper))
            {
                ulong row = replay.ReadPointer(wrapper + (ulong)rowAt);
                if (MemoryReaderExtensions.IsPlausiblePointer(row))
                {
                    rows.TryAdd(action.AnimationId, row);
                }
            }
        }

        return (names, rows);
    });

    [Fact]
    public void TheGameNamesTheSkillsThatWereCast()
    {
        AnimationNames names = Session.Value.Names;

        // The six the owner deliberately cast. Read out of the running game, not out of the file.
        Assert.Equal("SparkAdditive", names.Of(299));
        Assert.Equal("SummonOffering", names.Of(407));
        Assert.Equal("Flamewall", names.Of(472));
        Assert.Equal("OrbOfStorms", names.Of(474));
        Assert.Equal("PowerSiphon", names.Of(501));
        Assert.Equal("ElementalWeakness", names.Of(889));

        Assert.Equal(6, names.LearnedCount);
    }

    [Fact]
    public void TheRowsAreAnArrayIndexedByAnimationId()
    {
        // THE DECISIVE MEASUREMENT, and the reason the chain is a reading rather than a lucky
        // string: if the address is a linear function of the animation id then this is the
        // animation table, whatever the strings happen to look like. It is - exactly, over a
        // span of 590 ids.
        List<KeyValuePair<int, ulong>> rows = [.. Session.Value.Rows.OrderBy(row => row.Key)];
        Assert.True(rows.Count >= 6, $"only {rows.Count} animations had a row");

        for (int at = 1; at < rows.Count; at++)
        {
            long addresses = (long)rows[at].Value - (long)rows[at - 1].Value;
            int ids = rows[at].Key - rows[at - 1].Key;
            Assert.Equal(ids * (long)RowStride, addresses);
        }

        // ...and the base is consistent, which is the same statement said the other way round.
        long baseAddress = (long)rows[0].Value - (rows[0].Key * (long)RowStride);
        Assert.All(rows, row => Assert.Equal(baseAddress + (row.Key * (long)RowStride), (long)row.Value));
    }

    [Fact]
    public void TheShippedTableNowAgreesWithTheGame()
    {
        // THE PAYOFF, kept as a regression rather than as a live demonstration. When this
        // session was first replayed the game and the file disagreed about animation 889 - the
        // file said InteractLeanWell, the game says ElementalWeakness - and the file has since
        // been corrected. Shipping a row known to be wrong so that a test could keep proving the
        // mechanism would be backwards; the mechanism has its own test below.
        //
        // This is also the reason the reader re-asks about ids the table already "knows" rather
        // than only about ids it lacks: under an only-when-missing rule 889 would never have been
        // looked at, because the file had an answer for it. A wrong one.
        AnimationNames names = Session.Value.Names;

        Assert.Empty(names.Disagreements);
        Assert.Equal("ElementalWeakness", Shipped().Of(889));
    }

    [Fact]
    public void TheRowArrayBaseIsConfirmedFromARealSession()
    {
        // The claim <see cref="AnimationTable"/> rests on, put to the recording rather than to
        // arithmetic: two DIFFERENT animations seen in a real session agree on the same base, so
        // one sighting is enough to address every other row. That is what turns "name what you
        // happen to see" into "regenerate the whole file".
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var dump = new PoEformance.Game.Diagnostics.AnimationDump(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        bool confirmed = false;
        for (uint frame = 0; frame < replay.FrameCount && !confirmed; frame++)
        {
            replay.Seek(frame);
            confirmed = dump.Sample(gameStates);
        }

        Assert.True(confirmed, $"the base was never confirmed over {replay.FrameCount} frames");
        Assert.NotEqual(dump.Table.ConfirmedBy.First, dump.Table.ConfirmedBy.Second);

        // And the base it settled on predicts the rows the other test measured independently.
        foreach ((int animation, ulong row) in Session.Value.Rows)
        {
            Assert.Equal(row, dump.Table.RowOf(animation));
        }

        // WHICH SLOT IT CAME FROM, recorded because it answers a question nobody had asked: the
        // row pointer was found on a SKILL wrapper, and whether a MOVE wrapper carries it at the
        // same offset was never established. This session says what it says.
        Assert.NotEmpty(dump.Slots);
    }

    [Fact]
    public void LearningReplacesTheNameAndTheKindTogether()
    {
        // The kind is CACHED per id and derived from the name, so a learned name that did not
        // evict it would leave the tool classifying by a string it no longer believes.
        AnimationNames names = Shipped();

        Assert.Equal(AnimationKind.Moving, names.KindOf(195));   // FixedRun, via the shipped table
        names.Learn(195, "SomethingSlam");
        Assert.Equal("SomethingSlam", names.Of(195));
        Assert.Equal(AnimationKind.Slam, names.KindOf(195));

        // An empty or whitespace answer is not a name and must not overwrite a good one.
        names.Learn(195, "   ");
        Assert.Equal("SomethingSlam", names.Of(195));
    }

    [Fact]
    public void AnIdTheTableNeverHadBecomesClassifiableRatherThanUnknown()
    {
        // The case that matters most for the evasion filter, and the one the shipped table can
        // never cover: a monster animation nobody has written down. Unnamed, it is Unknown - and
        // the filter admits it, correctly, because "unrecognised" must not read as "harmless".
        // Named by the game, it can be classified, so a walk stops being drawn as a threat.
        AnimationNames names = Shipped();
        const int NeverSeen = 7654;

        Assert.Null(names.Of(NeverSeen));
        Assert.Equal(AnimationKind.Unknown, names.KindOf(NeverSeen));
        Assert.False(names.IsQuiet(NeverSeen));

        names.Learn(NeverSeen, "MonsterWalkSlow");
        Assert.Equal(AnimationKind.Moving, names.KindOf(NeverSeen));
        Assert.True(names.IsQuiet(NeverSeen));

        // A name the game supplied for an id the file lacks is not a DISAGREEMENT - there is
        // nothing to disagree with, and listing it would bury the rows that need attention.
        Assert.DoesNotContain(names.Disagreements, row => row.Id == NeverSeen);
    }

    [Fact]
    public void ADisagreementIsReportedWhenTheGameContradictsTheFile()
    {
        // The mechanism on its own, so correcting the shipped data did not cost the test that
        // finds the next wrong row. A growing list here is what says the file wants re-extracting.
        AnimationNames names = Shipped();
        Assert.Equal("Idle", names.Of(0));

        names.Learn(0, "SomethingElseEntirely");

        (int id, string shipped, string game) = Assert.Single(names.Disagreements);
        Assert.Equal(0, id);
        Assert.Equal("Idle", shipped);
        Assert.Equal("SomethingElseEntirely", game);
    }
}
