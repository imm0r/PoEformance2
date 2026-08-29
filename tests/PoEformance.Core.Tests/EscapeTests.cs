using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which way to roll: the geometry, with no game and no keyboard in it.
/// </summary>
/// <remarks>
/// THE CASE THAT PROMPTED ALL OF THIS is the first test below, and it is the owner's own: a boss
/// channelling a beam AT the character. Rolling backwards along that beam is the natural thing to
/// do and it keeps you in it for its whole length, so a scoring that cannot separate backwards
/// from sideways is not a scoring at all - and the obvious one, distance from the point the
/// action is aimed at, cannot: both land the same distance from the same point.
///
/// So the danger is a LINE from where the action starts to where it is aimed, extended past that
/// by the length of a roll, and these tests are what says the line is doing its job.
/// </remarks>
public class EscapeTests
{
    /// <summary>How far a roll goes, in world units. See EvasionSettings.RollDistance.</summary>
    private const double Roll = 400;

    /// <summary>
    /// Screen up is world -Y and screen right is world +X.
    /// </summary>
    /// <remarks>
    /// A stand-in for whatever the camera says, chosen to be legible rather than realistic: what
    /// is being tested here is the scoring, and a plausible-looking isometric basis would make
    /// every expected number a decimal for no gain. <c>ScreenBasisTests</c> is where a real
    /// camera is used, and it is a different question.
    /// </remarks>
    private static readonly ScreenBasis Basis = new(UpX: 0, UpY: -1, RightX: 1, RightY: 0);

    /// <summary>An action running from one point to another - a beam, a slam, anything aimed.</summary>
    private static Threat Aimed(float originX, float originY, float targetX, float targetY, uint id = 1)
        => new(
            id, "Boss", "Metadata/Monsters/Boss", ItemRarity.Unique, ActionKind.Skill,
            AnimationKind.Unknown,
            MonsterX: originX, MonsterY: originY,
            OriginX: originX, OriginY: originY,
            TargetX: targetX, TargetY: targetY,
            TargetZ: 0, Aimed: true, DistanceToPlayer: 0);

    /// <summary>The safety of one direction, so a test can name the numbers it expects.</summary>
    private static double Safety(IReadOnlyList<Threat> threats, MoveDirection direction)
    {
        (float x, float y) = Basis.World(direction);
        return Escape.Worst(threats, x * Roll, y * Roll, Roll);
    }

    [Fact]
    public void ABeamAimedAtYouIsEscapedAcrossItAndNeverAlongIt()
    {
        // THE OWNER'S CASE. A boss up the screen channels at the character, who is standing at
        // the origin. Its target is ON the player, which is what makes a point-distance scoring
        // useless here: backwards and sideways both end 400 units from that point.
        Threat[] beam = [Aimed(originX: 0, originY: -1500, targetX: 0, targetY: 0)];

        // Along the beam, either way, is inside it: towards the boss is on the segment, away
        // from it is on the extension that exists precisely to say so.
        Assert.Equal(0, Safety(beam, MoveDirection.Up), 1);
        Assert.Equal(0, Safety(beam, MoveDirection.Down), 1);

        // Across it is a whole roll clear.
        Assert.Equal(Roll, Safety(beam, MoveDirection.Left), 1);
        Assert.Equal(Roll, Safety(beam, MoveDirection.Right), 1);

        // And that is what gets chosen.
        EscapeChoice choice = Assert.NotNull(
            Escape.Best(beam, Escape.Options(Basis), playerX: 0, playerY: 0, Roll));
        Assert.Contains(
            (MoveDirection)choice.Index,
            new[] { MoveDirection.Left, MoveDirection.Right });
    }

    [Fact]
    public void WithoutTheExtensionBackwardsWouldScoreAsWellAsSideways()
    {
        // The counter-example, kept as a test because it is the mistake this design exists to
        // avoid rather than a property of the code: measured from the TARGET POINT alone, the
        // backward roll and the sideways roll are indistinguishable. Anybody tempted to
        // simplify the scoring back to a point can run this and see what it costs.
        const double PointDistanceBackwards = Roll;
        const double PointDistanceSideways = Roll;
        Assert.Equal(PointDistanceBackwards, PointDistanceSideways);

        // Whereas the line separates them, and by the whole width of a roll.
        Threat[] beam = [Aimed(0, -1500, 0, 0)];
        Assert.True(Safety(beam, MoveDirection.Left) - Safety(beam, MoveDirection.Down) > Roll - 1);
    }

    [Fact]
    public void AThreatNotAimedAtYouRanksAcrossThenAwayThenInto()
    {
        // The ordering for everything that is NOT aimed at the character: a monster off to the
        // right slamming at a spot beside them. Across is still best, but away from it now
        // scores on its own merits instead of collapsing to zero - the extension reaches 400
        // units past the target and no further, so a roll can get out from under it.
        Threat[] slam = [Aimed(originX: 600, originY: 0, targetX: 150, targetY: 0)];

        double across = Safety(slam, MoveDirection.Up);
        double away = Safety(slam, MoveDirection.Left);
        double into = Safety(slam, MoveDirection.Right);

        Assert.True(across > away, $"across scored {across:F0}, away {away:F0}");
        Assert.True(away > into, $"away scored {away:F0}, into {into:F0}");
        Assert.Equal(0, into, 1);
    }

    [Fact]
    public void ADirectionIsScoredByItsWorstThreatAndNotItsAverage()
    {
        // Escaping one attack by rolling into another is not an escape. Averaging would let a
        // direction that is wonderfully clear of a distant threat and standing in a near one
        // outscore a direction that is merely adequate against both.
        Threat[] both =
        [
            Aimed(originX: -2000, originY: 0, targetX: 0, targetY: 0, id: 1),
            Aimed(originX: 0, originY: 2000, targetX: 0, targetY: 380, id: 2),
        ];

        // Down runs across the first beam - 400 clear of it - and straight into the second.
        Assert.Equal(0, Safety(both, MoveDirection.Down), 1);

        // Up is across the first and away from the second, so it is the one taken.
        EscapeChoice choice = Assert.NotNull(Escape.Best(both, Escape.Options(Basis), 0, 0, Roll));
        Assert.Equal(MoveDirection.Up, (MoveDirection)choice.Index);
    }

    [Fact]
    public void ADiagonalWinsWhenEveryAxisIsBlocked()
    {
        // Why the diagonals are in the compass at all. A beam along one axis and slams on the
        // other leave no straight key that helps, and a tool offering four directions would pick
        // the least bad of them without ever saying a better one existed.
        Threat[] boxed =
        [
            Aimed(-1500, 0, 0, 0, id: 1),          // a beam from the left, through the player
            Aimed(0, -1400, 0, -450, id: 2),        // a slam above
            Aimed(0, 1400, 0, 450, id: 3),          // and one below
        ];

        foreach (MoveDirection axis in new[]
                 { MoveDirection.Up, MoveDirection.Down, MoveDirection.Left, MoveDirection.Right })
        {
            Assert.Equal(0, Safety(boxed, axis), 1);
        }

        EscapeChoice choice = Assert.NotNull(Escape.Best(boxed, Escape.Options(Basis), 0, 0, Roll));
        MoveDirection chosen = (MoveDirection)choice.Index;
        Assert.True(
            chosen is (MoveDirection.Up | MoveDirection.Right)
                or (MoveDirection.Up | MoveDirection.Left)
                or (MoveDirection.Down | MoveDirection.Right)
                or (MoveDirection.Down | MoveDirection.Left),
            $"expected a diagonal, got {chosen}");
        Assert.True(choice.Safety > 250, $"the diagonal only cleared {choice.Safety:F0} units");
    }

    [Fact]
    public void NothingSaferThanStandingStillIsNoChoiceAtAll()
    {
        // Surrounded, or the danger is simply too big to roll out of. The question is never
        // "is this spot safe" - nothing here can answer that - but "is rolling better than
        // staying", and when it is not, the roll spends a charge and the player's own aim for
        // nothing. Null rather than a direction, so the caller can decline.
        Threat[] everywhere =
        [
            Aimed(-3000, 0, 3000, 0, id: 1),
            Aimed(0, -3000, 0, 3000, id: 2),
            Aimed(-3000, -3000, 3000, 3000, id: 3),
            Aimed(-3000, 3000, 3000, -3000, id: 4),
        ];

        Assert.Null(Escape.Best(everywhere, Escape.Options(Basis), 0, 0, Roll));
    }

    [Fact]
    public void NoThreatsMeansNoDirectionToPrefer()
    {
        Assert.Null(Escape.Best([], Escape.Options(Basis), 0, 0, Roll));
        Assert.Null(Escape.Best([Aimed(0, -1000, 0, 0)], [], 0, 0, Roll));
    }

    [Fact]
    public void AnActionCastOnItselfIsAPointAndNotALine()
    {
        // A monster whose action starts and ends in the same cell has no direction, and the
        // segment collapses to the point it should rather than dividing by a zero length.
        Threat[] onTheSpot = [Aimed(originX: 100, originY: 0, targetX: 100, targetY: 0)];

        // Every direction is measured from that one point, so rolling away from it is best.
        Assert.True(Safety(onTheSpot, MoveDirection.Left) > Safety(onTheSpot, MoveDirection.Right));
        Assert.Equal(300, Safety(onTheSpot, MoveDirection.Right), 1);
    }

    [Fact]
    public void DistanceToASegmentClampsToItsEnds()
    {
        // The primitive everything above rests on. Beside the middle it is the perpendicular
        // distance; past an end it is the distance to that end, NOT to the infinite line - which
        // is what stops a threat's line reaching backwards forever behind its own origin.
        Assert.Equal(5, Escape.DistanceToSegment(0, 5, -10, 0, 10, 0), 3);
        Assert.Equal(5, Escape.DistanceToSegment(15, 0, -10, 0, 10, 0), 3);
        Assert.Equal(Math.Sqrt(50), Escape.DistanceToSegment(15, 5, -10, 0, 10, 0), 3);

        // A degenerate segment is a point.
        Assert.Equal(5, Escape.DistanceToSegment(0, 5, 0, 0, 0, 0), 3);
    }

    [Fact]
    public void TheCompassIsEightUnitDirections()
    {
        IReadOnlyList<EscapeOption> options = Escape.Options(Basis);
        Assert.Equal(8, options.Count);

        foreach (EscapeOption option in options)
        {
            Assert.Equal(1, Math.Sqrt((option.X * option.X) + (option.Y * option.Y)), 3);
        }

        // Every one maps back to the keys that produce it, which is the whole reason the index
        // is the direction rather than a position in a list.
        Assert.Equal(
            MovementKeys.Compass.OrderBy(d => (int)d),
            options.Select(o => (MoveDirection)o.Index).OrderBy(d => (int)d));
    }

    [Fact]
    public void OpposedKeysAreNotADirection()
    {
        // Holding left and right together goes nowhere, and the basis says so rather than
        // handing back a zero vector dressed as a heading.
        Assert.Equal((0f, 0f), Basis.World(MoveDirection.Left | MoveDirection.Right));
        Assert.Equal((0f, 0f), Basis.World(MoveDirection.None));
    }
}
