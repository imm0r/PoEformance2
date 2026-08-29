using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>One monster's move that ran to its end, and where it actually finished.</summary>
/// <param name="Miss">World units between the destination and where the monster came to rest.</param>
public sealed record MonsterArrival(uint EntityId, string Name, float TargetX, float TargetY, float EndX, float EndY, double Miss);

/// <summary>What a recording says about the action fields on MONSTERS.</summary>
/// <param name="IdCounts">Every ActionId seen on a monster, and how often.</param>
/// <param name="Bearings">
/// Degrees between each SKILL action's direction and its actor's facing - the cross-check.
/// </param>
/// <param name="MoveBearings">
/// The same for MOVE actions, kept apart because it is NOT a check.
///
/// A monster faces what it is fighting and walks around whatever is in the way, so its
/// destination is not where it points and there is no reason for these to agree. Measured:
/// 25.9 degrees median over 7597 monster moves, and 32.5 - WORSE - if taken from where the
/// monster currently stands rather than from the run's origin. Averaged in with the skills it
/// drags a 1.6-degree agreement out to 17.7 and makes a corroborated field look shaky, which is
/// the only reason this is a separate list rather than a filter.
/// </param>
/// <param name="ImplausibleTargets">
/// Aimed actions whose target is further from the actor than any skill reaches. The shape a
/// wrong offset takes on an entity nobody has checked.
/// </param>
public sealed record MonsterActionFindings(
    int Frames,
    int MonsterSightings,
    int DistinctMonsters,
    int ActingSightings,
    IReadOnlyDictionary<int, int> IdCounts,
    IReadOnlyList<MonsterArrival> Arrivals,
    IReadOnlyList<double> Bearings,
    int ImplausibleTargets,
    IReadOnlyList<double>? MoveBearings = null)
{
    /// <summary>Median distance between a completed move's destination and its arrival.</summary>
    public double MedianMiss => Median([.. Arrivals.Select(a => a.Miss)]);

    /// <summary>Median disagreement between an AIMED target and the actor's facing, in degrees.</summary>
    public double MedianBearing => Median([.. Bearings]);

    /// <summary>Share of aimed actions whose direction sits within thirty degrees of the facing.</summary>
    /// <remarks>
    /// The headline number, because a median hides the tail and the tail is what a wrong offset
    /// would show up in. Measured at 94% on the monster session.
    /// </remarks>
    public double BearingAgreement => Bearings.Count == 0
        ? double.NaN
        : Bearings.Count(b => b <= 30) / (double)Bearings.Count;

    /// <summary>The same median for MOVE actions - for contrast, never as a check.</summary>
    public double MedianMoveBearing => Median([.. MoveBearings ?? []]);

    /// <summary>
    /// What the median would be if the two were mixed - the misleading number, kept so a test
    /// can assert that keeping them apart still earns its place.
    /// </summary>
    public double MedianBearingIfMixed => Median([.. Bearings, .. MoveBearings ?? []]);

    internal static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return double.NaN;
        }

        double[] sorted = [.. values.Order()];
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}

/// <summary>
/// Asks of a recording the one question every action offset here is still missing an answer to:
/// do these fields say true things about MONSTERS?
/// </summary>
/// <remarks>
/// EVERY OFFSET BEHIND <see cref="ActionReader"/> WAS MEASURED ON THE PLAYER. Monsters carry the
/// same component and the same offsets should hold, and "should" is the word that has cost this
/// project time before - it is exactly what was said about the animation table, and about
/// reading buffs inline. So the claim gets the same treatment the player's did: not a structural
/// argument, but a test the game settles.
///
/// TWO CHECKS, and they fail in different directions, which is why both are here:
///
///  1. THE ARRIVAL. A monster whose move action names a destination must END UP THERE. This is
///     the same measurement that settled the player's grid-to-world conversion (it landed to
///     0.00 world units over four arrivals) and it needs no knowledge of the monster's intent:
///     the game moves it, and either the field predicted where or it did not. A wrong offset
///     cannot pass this by luck.
///  2. THE BEARING. An aimed action's direction must agree with <c>Render.RotationCurrent</c> -
///     a field found independently a month earlier, by a different method, on a different
///     recording. Two unrelated readings agreeing is evidence; one reading looking plausible is
///     not.
///
/// A monster standing still all session yields neither, and that is reported as "the recording
/// cannot say" rather than as a pass - the failure mode this whole file exists to avoid.
/// </remarks>
public static class MonsterActionCheck
{
    /// <summary>
    /// Furthest a target may sit from its actor before it is called implausible.
    /// </summary>
    /// <remarks>
    /// Generous on purpose: the point is to catch a target on the far side of the map (the
    /// shape of a misread pointer), not to police skill ranges nobody here has measured. The
    /// player's own longest action in the settled recording reached 834 units.
    /// </remarks>
    private const double FurthestPlausibleTarget = 20_000;

    /// <summary>A move must run at least this long before its end counts as an arrival.</summary>
    private const int LeastFramesPerMove = 4;

    /// <summary>
    /// A monster must finish at least this close to be counted as having ARRIVED rather than
    /// having been interrupted - stopped early, killed, or knocked back.
    /// </summary>
    /// <remarks>
    /// One grid cell. A monster is not a click-to-move player: it stops when it is in range of
    /// what it is chasing, so most of its moves end SHORT of the named destination by design.
    /// Those are not measurements of the field and must not be averaged in - what is being
    /// asked here is whether the moves that DO run to completion land where the field said.
    /// </remarks>
    private static readonly double ArrivedWithin = Ui.MapView.WorldToGrid;

    /// <summary>Draws every conclusion from the samples. Pure, so a replay re-runs it.</summary>
    public static MonsterActionFindings Analyze(IReadOnlyList<ActionHuntSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var idCounts = new Dictionary<int, int>();
        var bearings = new List<double>();
        var moveBearings = new List<double>();
        var arrivals = new List<MonsterArrival>();
        var distinct = new HashSet<uint>();
        int sightings = 0, acting = 0, implausible = 0;

        // Per monster, the run of frames in which one move action was in force. A run ends when
        // the target changes, the action stops, or the monster leaves the list.
        var open = new Dictionary<uint, (float TargetX, float TargetY, string Name, int Frames, float EndX, float EndY)>();
        var alive = new HashSet<uint>();

        foreach (ActionHuntSample sample in samples)
        {
            alive.Clear();

            foreach (MonsterSighting m in sample.Monsters ?? [])
            {
                sightings++;
                distinct.Add(m.EntityId);
                alive.Add(m.EntityId);

                ActorAction action = m.Action;
                if (action.Kind != ActionKind.None)
                {
                    acting++;
                    idCounts[action.RawId] = idCounts.GetValueOrDefault(action.RawId) + 1;

                    double away = Distance(action.TargetX, action.TargetY, m.X, m.Y);
                    if (away > FurthestPlausibleTarget)
                    {
                        implausible++;
                    }

                    // The cross-check, only where there is a direction to compare: a target on
                    // top of the origin has no bearing, and an unread facing has nothing to
                    // compare against. SKILLS AND MOVES GO IN DIFFERENT LISTS - see the note on
                    // MoveBearings for why mixing them slanders a corroborated field.
                    if (action.Reach > 1f && !float.IsNaN(m.Facing))
                    {
                        float aimed = Facing.FromHeading(
                            action.TargetX - action.OriginX, action.TargetY - action.OriginY);
                        double degrees = Math.Abs(Facing.Between(aimed, m.Facing)) * 180.0 / Math.PI;
                        (action.Kind == ActionKind.Move ? moveBearings : bearings).Add(degrees);
                    }
                }

                bool moving = action.Kind == ActionKind.Move;
                bool sameRun = open.TryGetValue(m.EntityId, out var run)
                    && moving
                    && Math.Abs(run.TargetX - action.TargetX) < 0.01f
                    && Math.Abs(run.TargetY - action.TargetY) < 0.01f;

                if (sameRun)
                {
                    open[m.EntityId] = (run.TargetX, run.TargetY, run.Name, run.Frames + 1, m.X, m.Y);
                    continue;
                }

                // The old run ended HERE, at the position it last held - and this frame's
                // position is that end, because the monster has stopped moving towards it.
                if (open.TryGetValue(m.EntityId, out var finished))
                {
                    Close(arrivals, m.EntityId, finished with { EndX = m.X, EndY = m.Y });
                    open.Remove(m.EntityId);
                }

                if (moving)
                {
                    open[m.EntityId] = (action.TargetX, action.TargetY, m.Name, 1, m.X, m.Y);
                }
            }

            // A monster that left the list mid-move tells us nothing about where it would have
            // arrived - it may have died - so its run is dropped rather than closed.
            foreach (uint gone in open.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                open.Remove(gone);
            }
        }

        return new MonsterActionFindings(
            samples.Count, sightings, distinct.Count, acting, idCounts, arrivals, bearings, implausible,
            moveBearings);
    }

    /// <summary>Keeps a finished run if it ran long enough AND actually reached its target.</summary>
    private static void Close(
        List<MonsterArrival> arrivals,
        uint id,
        (float TargetX, float TargetY, string Name, int Frames, float EndX, float EndY) run)
    {
        if (run.Frames < LeastFramesPerMove)
        {
            return;
        }

        double miss = Distance(run.TargetX, run.TargetY, run.EndX, run.EndY);
        if (miss <= ArrivedWithin)
        {
            arrivals.Add(new MonsterArrival(id, run.Name, run.TargetX, run.TargetY, run.EndX, run.EndY, miss));
        }
    }

    private static double Distance(double ax, double ay, double bx, double by)
        => Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    /// <summary>Writes the verdict.</summary>
    public static void Report(MonsterActionFindings findings, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("monster action check (do the player's offsets hold on monsters?)");
        output.WriteLine($"  {findings.MonsterSightings} monster sighting(s) over {findings.Frames} frames, "
            + $"{findings.DistinctMonsters} distinct, {findings.ActingSightings} acting");

        if (findings.MonsterSightings == 0)
        {
            output.WriteLine("  NOTHING TO SAY: no hostile monster was in the entity list at any sampled");
            output.WriteLine("  frame. Not a negative result - the question was never asked. Record again");
            output.WriteLine("  standing in a fight.");
            return;
        }

        if (findings.ActingSightings == 0)
        {
            output.WriteLine("  NOTHING TO SAY: monsters were there but none of them was doing anything, so");
            output.WriteLine("  no target was ever written for these offsets to be right or wrong about.");
            return;
        }

        output.WriteLine("  action ids seen: " + string.Join(", ",
            findings.IdCounts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}x{k.Value}")));

        output.WriteLine(findings.ImplausibleTargets == 0
            ? "  every target sits within a plausible distance of its own actor."
            : $"  {findings.ImplausibleTargets} target(s) further from their actor than any skill reaches"
              + " - that is what a wrong offset looks like.");

        if (findings.Arrivals.Count > 0)
        {
            output.WriteLine($"  ARRIVALS: {findings.Arrivals.Count} completed move(s), median miss "
                + $"{findings.MedianMiss:F1} world units, worst {findings.Arrivals.Max(a => a.Miss):F1}");
            foreach (MonsterArrival a in findings.Arrivals.OrderBy(a => a.Miss).Take(6))
            {
                output.WriteLine($"    {a.Name,-24} target ({a.TargetX,8:F0},{a.TargetY,8:F0}) "
                    + $"ended ({a.EndX,8:F0},{a.EndY,8:F0})  miss {a.Miss,6:F1}");
            }
        }
        else
        {
            output.WriteLine("  no completed moves: monsters stop when they are in range rather than on");
            output.WriteLine("  their destination, so the arrival test needs one that ran all the way.");
        }

        if (findings.Bearings.Count > 0)
        {
            output.WriteLine($"  BEARINGS (skills): {findings.Bearings.Count} aimed action(s), median "
                + $"{findings.MedianBearing:F1}°, {findings.BearingAgreement:P0} within 30°");
            output.WriteLine("    (small means the target agrees with the facing - a field found");
            output.WriteLine("     independently, so agreement is corroboration rather than consistency)");
        }
        else
        {
            output.WriteLine("  no skill bearings: nothing was aimed, or the facing did not read.");
        }

        // Printed, but explicitly NOT as a check. A reader who sees one bearing figure assumes
        // it is the cross-check; this session's moves sit at 26 degrees and would read as a
        // failure, when what they actually show is that a monster faces its quarry rather than
        // its path - the same thing the player's own facing turned out to do.
        if (findings.MoveBearings is { Count: > 0 } moves)
        {
            output.WriteLine($"  move bearings, for contrast: {moves.Count}, median "
                + $"{MonsterActionFindings.Median([.. moves]):F1}° - NOT a check. A monster faces what");
            output.WriteLine("    it is fighting and walks around what is in the way, so its destination is");
            output.WriteLine("    not where it points. Only the skill row above is corroboration.");
        }
    }
}
