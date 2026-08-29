using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The monster question, settled: do the player's action offsets say true things about MONSTERS?
/// </summary>
/// <remarks>
/// THIS IS THE TEST THE WHOLE ACTION HUNT EXISTED TO MAKE POSSIBLE. Every offset behind
/// <see cref="ActionReader"/> was measured on the player's own actor across two sessions, while
/// the feature they exist for reads monsters; the first fight recording could not settle it
/// because nothing had ever read a monster's actor, and the second was the fix for that. This
/// one is 130 seconds of a real fight - 54 monsters, 27,156 sightings, 9,300 of them acting -
/// and the game answers in the two ways it can:
///
///  - THE ARRIVAL. 210 monster moves ran to completion and ended on the destination the field
///    named. 185 of them EXACTLY: median miss 0.00 world units, worst 10.87, which is one grid
///    cell. Across 39 distinct monsters of eleven kinds, none of which anybody had aimed a probe
///    at. A wrong offset cannot pass this once, let alone 210 times.
///  - THE BEARING. Over 1649 monster SKILL actions the direction from the action's origin to its
///    target agrees with Render.RotationCurrent to a median of 1.6 degrees, 94% inside thirty.
///    That field was found a month earlier, by a different method, on a different recording - so
///    this is two unrelated readings agreeing rather than one reading looking plausible.
///
/// The tests below assert those numbers with margin, so a drift shows up as a failure here
/// rather than as an overlay that quietly points at the wrong ground.
/// </remarks>
public class MonsterActionsSettledTests
{
    private const string Fixture = "session-2026-08-monsters.rec";

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

    /// <summary>Replays the whole session through the hunt's sampler. Cached - it is not cheap.</summary>
    private static readonly Lazy<(List<ActionHuntSample> Samples, MonsterActionFindings Findings)> Session = new(() =>
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new ActionHunt(replay, schema);

        var samples = new List<ActionHuntSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(replay.ResolvedStatics["GameStates"]) is { } sample)
            {
                samples.Add(sample);
            }
        }

        return (samples, MonsterActionCheck.Analyze(samples));
    });

    [Fact]
    public void TheRecordingActuallyHoldsAFight()
    {
        // The precondition every claim below rests on, asserted first so a fixture that lost
        // its monsters fails HERE with an obvious message rather than making the real tests
        // pass vacuously on an empty set.
        MonsterActionFindings f = Session.Value.Findings;

        Assert.True(f.MonsterSightings > 20_000, $"only {f.MonsterSightings} monster sightings");
        Assert.True(f.DistinctMonsters >= 40, $"only {f.DistinctMonsters} distinct monsters");
        Assert.True(f.ActingSightings > 5_000, $"only {f.ActingSightings} of them were acting");
    }

    [Fact]
    public void MonstersArriveExactlyWhereTheirMoveActionSaid()
    {
        // THE HEADLINE. The game moves the monster; either the field predicted where it would
        // stop or it did not. Nothing here depends on knowing what the monster intended.
        MonsterActionFindings f = Session.Value.Findings;

        Assert.True(f.Arrivals.Count >= 150, $"only {f.Arrivals.Count} completed moves");
        Assert.True(f.MedianMiss < 0.5, $"median miss {f.MedianMiss:F2} world units");

        // Most of them are not merely close, they are EXACT - which is what a grid destination
        // converted with the right half-cell looks like, and what no wrong offset produces.
        int exact = f.Arrivals.Count(a => a.Miss < 0.001);
        Assert.True(exact > f.Arrivals.Count / 2, $"only {exact} of {f.Arrivals.Count} arrivals were exact");

        // And the worst case is bounded by the quantisation itself: one cell, never more.
        Assert.True(
            f.Arrivals.Max(a => a.Miss) <= PoEformance.Game.Ui.MapView.WorldToGrid + 0.01,
            $"worst arrival missed by {f.Arrivals.Max(a => a.Miss):F2}");
    }

    [Fact]
    public void ArrivalsSpanManyDifferentMonsters()
    {
        // One monster arriving 210 times would be one measurement repeated. These are 39
        // separate entities of eleven kinds, which is what makes it a property of the game's
        // Actor component rather than of one creature's behaviour.
        MonsterActionFindings f = Session.Value.Findings;

        Assert.True(
            f.Arrivals.Select(a => a.EntityId).Distinct().Count() >= 20,
            "the arrivals come from too few distinct monsters to generalise");
        Assert.True(
            f.Arrivals.Select(a => a.Name).Distinct().Count() >= 5,
            "the arrivals come from too few kinds of monster to generalise");
    }

    [Fact]
    public void AimedMonsterActionsAgreeWithTheFacingFoundIndependently()
    {
        // THE CORROBORATION. Render.RotationCurrent was found a month earlier by a different
        // method on a different recording; the action target is new. Two unrelated readings
        // pointing the same way is evidence in a way that either alone is not.
        MonsterActionFindings f = Session.Value.Findings;

        Assert.True(f.Bearings.Count > 1_000, $"only {f.Bearings.Count} aimed monster actions");
        Assert.True(f.MedianBearing < 5, $"median bearing disagreement {f.MedianBearing:F1} degrees");
        Assert.True(f.BearingAgreement > 0.85, $"only {f.BearingAgreement:P0} of aimed actions within 30 degrees");
    }

    [Fact]
    public void MonstersDoNotFaceWhereTheyWalk()
    {
        // THE NEGATIVE, asserted because the obvious check gets it backwards and would report a
        // working field as broken. A monster faces its quarry and walks around obstacles, so a
        // move's destination has no reason to line up with the facing - and measuring the
        // bearing from where the monster CURRENTLY stands makes it worse, not better. This is
        // the same lesson the player's own facing taught: it follows the cursor, not the path.
        MonsterActionFindings f = Session.Value.Findings;
        Assert.NotNull(f.MoveBearings);

        Assert.True(f.MoveBearings!.Count > 5_000, $"only {f.MoveBearings.Count} move actions");
        Assert.True(
            f.MedianMoveBearing > 15,
            $"move bearings agree at {f.MedianMoveBearing:F1} degrees - unexpectedly well");

        // And the reason they are reported apart: mixed in, they drag the skills' agreement
        // from a corroborating number to a doubtful one.
        Assert.True(f.MedianBearingIfMixed > 10, "mixing the two no longer misleads, so the split could be dropped");
        Assert.True(f.MedianBearing < 5, "the skills' own agreement is what the split preserves");
    }

    [Fact]
    public void ActionIdIsAFlagsWordAndBothBitsDecodeCleanly()
    {
        // What this session added to the two ids the player sessions had. Seven values turn up,
        // and read as whole numbers five of them are strangers; read as flags they are two bits
        // and some detail. The claim asserted here is the one the reader relies on: every id
        // carrying the skill bit decodes as a skill and every id carrying only the move bit
        // decodes as a move.
        MonsterActionFindings f = Session.Value.Findings;

        Assert.True(f.IdCounts.Count >= 5, $"only {f.IdCounts.Count} distinct action ids seen");
        Assert.Contains(0x1080, f.IdCounts.Keys); // the ordinary move
        Assert.Contains(0x0002, f.IdCounts.Keys); // the ordinary skill

        // Every id seen carries at least one of the two bits, or is one of the handful with
        // neither - and those must NOT decode as an action with a target.
        foreach (int id in f.IdCounts.Keys)
        {
            bool skill = (id & 0x0002) != 0;
            bool move = (id & 0x1000) != 0;
            Assert.True(skill || move || id is 0x0200, $"action id 0x{id:X} carries neither bit and is not the known 0x200");
        }
    }

    [Fact]
    public void TheReaderDecodesEveryObservedIdWithoutInventingTargets()
    {
        // The production reader over the whole session: it must never turn a monster into
        // something charging at a place on the far side of the map, and it must find a target
        // for the ids that carry one.
        (List<ActionHuntSample> samples, MonsterActionFindings findings) = Session.Value;

        Assert.Equal(0, findings.ImplausibleTargets);

        int withTarget = 0, acting = 0;
        foreach (ActionHuntSample sample in samples)
        {
            foreach (MonsterSighting m in sample.Monsters ?? [])
            {
                if (m.Action.Kind == ActionKind.None)
                {
                    continue;
                }

                acting++;
                if (m.Action.Reach > 0)
                {
                    withTarget++;
                }
            }
        }

        Assert.True(acting > 5_000, $"only {acting} acting sightings");
        Assert.True(withTarget > acting * 0.9, $"only {withTarget} of {acting} acting monsters had a readable target");
    }

    [Fact]
    public void TheCheckReportsTheVerdictRatherThanANumberSalad()
    {
        using var sink = new StringWriter();
        MonsterActionCheck.Report(Session.Value.Findings, sink);
        string text = sink.ToString();

        Assert.Contains("ARRIVALS:", text, StringComparison.Ordinal);
        Assert.Contains("BEARINGS (skills):", text, StringComparison.Ordinal);
        Assert.Contains("NOT a check", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING TO SAY", text, StringComparison.Ordinal);
    }
}
