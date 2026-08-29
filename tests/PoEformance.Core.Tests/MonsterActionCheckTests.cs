using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The monster half of the action fields, against sessions whose right answer is planted.
/// </summary>
/// <remarks>
/// WHY SYNTHETIC AND NOT A RECORDING. The obvious way to test this would be a fight fixture,
/// and there is one committed - <c>session-2026-08-fight.rec</c> - which contains no monster
/// actions at all, for the reason <c>FightSessionTests</c> asserts. So the analysis that will
/// judge the NEXT recording is proven here instead, on traces built to be judged: a monster
/// that walks somewhere and arrives, one that stops short because it got in range, one that
/// dies mid-stride, and one aimed at the far side of the map.
///
/// The distinction those cases turn on is the one thing a monster arrival test has to get right
/// and a player one never faces. A player click-moves and walks the whole way; a MONSTER stops
/// when it is in range of what it is chasing, so most of its moves end short BY DESIGN. Counting
/// those as misses would report a median miss of hundreds of units and read exactly like a wrong
/// offset - so an interrupted move is not evidence about the field and is discarded, and this is
/// where that rule is checked.
/// </remarks>
public class MonsterActionCheckTests
{
    /// <summary>A grid cell in world units - the tolerance an "arrival" is judged by.</summary>
    private const float Cell = 250f / 23f;

    private static ActionHuntSample Frame(params MonsterSighting[] monsters)
        => new(0x1000, new byte[ActionHunt.WindowSize], 0, 0, new Dictionary<int, byte[]>(), float.NaN, monsters);

    private static MonsterSighting Walking(
        uint id, string name, float x, float y, float targetX, float targetY, float facing = float.NaN)
        => new(id, name, new ActorAction(ActionKind.Move, 4224, targetX, targetY, 0, 0, 0, 195), x, y, facing);

    /// <summary>A monster walking a straight line to its target, arriving on the last frame.</summary>
    private static List<ActionHuntSample> WalkTo(
        uint id, float fromX, float fromY, float toX, float toY, int steps, bool arrive = true)
    {
        var frames = new List<ActionHuntSample>();
        for (int i = 0; i < steps; i++)
        {
            float share = (float)i / (steps - 1);
            frames.Add(Frame(Walking(id, "Zombie", fromX + ((toX - fromX) * share), fromY + ((toY - fromY) * share), toX, toY)));
        }

        // The run has to END for the arrival to be closed: the monster stands idle where it
        // stopped, which is exactly what the game does when a move completes.
        if (arrive)
        {
            frames.Add(Frame(new MonsterSighting(id, "Zombie", ActorAction.None, toX, toY, float.NaN)));
        }

        return frames;
    }

    [Fact]
    public void AMonsterThatWalksToItsDestinationCountsAsAnArrival()
    {
        MonsterActionFindings f = MonsterActionCheck.Analyze(WalkTo(7, 1000, 1000, 1400, 1300, 10));

        MonsterArrival arrival = Assert.Single(f.Arrivals);
        Assert.Equal(7u, arrival.EntityId);
        Assert.Equal(1400, arrival.TargetX, 1);
        Assert.True(arrival.Miss < 0.01, $"miss {arrival.Miss}");
        Assert.Equal(1, f.DistinctMonsters);
    }

    [Fact]
    public void AMoveThatStopsShortIsNotEvidenceAboutTheField()
    {
        // The monster gets in range and stops 300 units out - the ordinary case, and the one
        // that would poison the median if it were counted.
        var frames = new List<ActionHuntSample>();
        for (int i = 0; i < 10; i++)
        {
            frames.Add(Frame(Walking(9, "Skeleton", 1000 + (i * 30), 1000, 2000, 1000)));
        }

        frames.Add(Frame(new MonsterSighting(9, "Skeleton", ActorAction.None, 1270, 1000, float.NaN)));

        MonsterActionFindings f = MonsterActionCheck.Analyze(frames);
        Assert.Empty(f.Arrivals);

        // It was still SEEN acting - the sighting counts even though the arrival does not,
        // which is what keeps "monsters act" and "the destination is right" separate claims.
        Assert.Equal(10, f.ActingSightings);
    }

    [Fact]
    public void AMonsterThatDiesMidStrideIsDroppedRatherThanCountedAsAMiss()
    {
        // Frames where it walks, then it simply is not in the list any more. Nothing can be
        // concluded about where it would have arrived, so nothing is.
        List<ActionHuntSample> frames = [.. WalkTo(11, 0, 0, 500, 500, 6, arrive: false)];
        frames.Add(Frame()); // gone
        frames.Add(Frame());

        MonsterActionFindings f = MonsterActionCheck.Analyze(frames);
        Assert.Empty(f.Arrivals);
    }

    [Fact]
    public void AVeryShortMoveIsNotCountedEitherWay()
    {
        // Two frames is not a walk; it is a monster that happened to be sampled twice with the
        // same target. The arrival test needs a run long enough to have gone somewhere.
        List<ActionHuntSample> frames = [.. WalkTo(12, 0, 0, 20, 20, 2)];
        Assert.Empty(MonsterActionCheck.Analyze(frames).Arrivals);
    }

    [Fact]
    public void BearingComparesTheAimedTargetWithTheActorsOwnFacing()
    {
        // A monster at the origin aiming due +X. Facing convention: zero points along world
        // -Y and runs as atan2 does, so a heading of +X reads pi/2 - see Facing.
        float facing = PoEformance.Game.World.Facing.FromHeading(1, 0);
        var aimed = new ActorAction(ActionKind.Skill, 2, 500, 0, 0, 0, 0x99, 299);

        MonsterActionFindings agreeing = MonsterActionCheck.Analyze(
            [Frame(new MonsterSighting(3, "Rhoa", aimed, 0, 0, facing))]);
        Assert.Equal(0, Assert.Single(agreeing.Bearings), 1);

        // The same action with the monster facing the other way: a right angle of disagreement,
        // which is what a wrong offset would look like across a column of these.
        MonsterActionFindings disagreeing = MonsterActionCheck.Analyze(
            [Frame(new MonsterSighting(3, "Rhoa", aimed, 0, 0, PoEformance.Game.World.Facing.FromHeading(0, 1)))]);
        Assert.Equal(90, Assert.Single(disagreeing.Bearings), 1);
    }

    [Fact]
    public void ATargetOnTheFarSideOfTheMapIsFlagged()
    {
        var absurd = new ActorAction(ActionKind.Skill, 2, 900_000, 900_000, 0, 0, 0, 299);
        MonsterActionFindings f = MonsterActionCheck.Analyze(
            [Frame(new MonsterSighting(4, "Goatman", absurd, 0, 0, float.NaN))]);

        Assert.Equal(1, f.ImplausibleTargets);
    }

    [Fact]
    public void TwoMonstersAreTrackedApart()
    {
        // Interleaved traces: both walk, both arrive, and the runs must not be confused - the
        // reason sightings are keyed by the game's entity id rather than by list position.
        var frames = new List<ActionHuntSample>();
        for (int i = 0; i < 8; i++)
        {
            float share = i / 7f;
            frames.Add(Frame(
                Walking(21, "A", 100 * share, 0, 100, 0),
                Walking(22, "B", 0, 200 * share, 0, 200)));
        }

        frames.Add(Frame(
            new MonsterSighting(21, "A", ActorAction.None, 100, 0, float.NaN),
            new MonsterSighting(22, "B", ActorAction.None, 0, 200, float.NaN)));

        MonsterActionFindings f = MonsterActionCheck.Analyze(frames);
        Assert.Equal(2, f.DistinctMonsters);
        Assert.Equal(2, f.Arrivals.Count);
        Assert.All(f.Arrivals, a => Assert.True(a.Miss < 0.01, $"miss {a.Miss}"));
    }

    [Fact]
    public void AnArrivalWithinOneCellStillCounts()
    {
        // The player's own destination lands on the CELL CENTRE, so a monster stopping anywhere
        // inside the named cell is the field being right, not nearly right.
        List<ActionHuntSample> frames = [.. WalkTo(31, 0, 0, 500, 0, 8, arrive: false)];
        frames.Add(Frame(new MonsterSighting(31, "Zombie", ActorAction.None, 500 - (Cell * 0.4f), 0, float.NaN)));

        Assert.Single(MonsterActionCheck.Analyze(frames).Arrivals);
    }

    [Fact]
    public void ASessionWithNoMonstersSaysItCannotAnswer()
    {
        // The distinction the whole file turns on: "nothing was asked" is not "the answer is
        // no". This is the exact shape of the first fight recording.
        MonsterActionFindings f = MonsterActionCheck.Analyze([Frame(), Frame(), Frame()]);
        Assert.Equal(0, f.MonsterSightings);

        using var sink = new StringWriter();
        MonsterActionCheck.Report(f, sink);
        Assert.Contains("NOTHING TO SAY", sink.ToString(), StringComparison.Ordinal);
        Assert.Contains("the question was never asked", sink.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MonstersPresentButIdleAlsoSayNothing()
    {
        MonsterActionFindings f = MonsterActionCheck.Analyze(
            [.. Enumerable.Range(0, 5).Select(_ =>
                Frame(new MonsterSighting(5, "Zombie", ActorAction.None, 10, 10, float.NaN)))]);

        Assert.Equal(5, f.MonsterSightings);
        Assert.Equal(0, f.ActingSightings);

        using var sink = new StringWriter();
        MonsterActionCheck.Report(f, sink);
        Assert.Contains("none of them was doing anything", sink.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportSurvivesAFullSetOfFindings()
    {
        List<ActionHuntSample> frames = [.. WalkTo(41, 0, 0, 400, 400, 10)];
        frames.Add(Frame(new MonsterSighting(
            42, "Caster", new ActorAction(ActionKind.Skill, 2, 300, 0, 0, 0, 0x50, 299), 0, 0, 1.5708f)));

        using var sink = new StringWriter();
        MonsterActionCheck.Report(MonsterActionCheck.Analyze(frames), sink);

        string text = sink.ToString();
        Assert.Contains("ARRIVALS:", text, StringComparison.Ordinal);

        // Named "(skills)" since the monster session: a move's bearing is not a check, so the
        // report must never print one number that a reader takes for the cross-check.
        Assert.Contains("BEARINGS (skills):", text, StringComparison.Ordinal);
    }
}
