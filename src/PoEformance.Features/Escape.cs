namespace PoEformance.Features;

/// <summary>One direction the tool could actually roll in, as a world-space unit vector.</summary>
/// <param name="Index">
/// Which option this is, so the caller can map it back to whatever produces the direction -
/// movement keys, in practice. The scoring neither knows nor cares what that is.
/// </param>
public readonly record struct EscapeOption(int Index, float X, float Y);

/// <summary>How good one escape direction is, and why.</summary>
/// <param name="Safety">
/// World units from the nearest danger after rolling. Larger is better; zero means the roll
/// lands in it.
/// </param>
public readonly record struct EscapeChoice(int Index, float X, float Y, double Safety);

/// <summary>
/// Picks which way to roll out of what is coming.
/// </summary>
/// <remarks>
/// THE DANGER IS A LINE, NOT A POINT, and getting that wrong is the difference between living
/// and dying in the case that prompted this. A boss channelling a beam AT you has its action
/// target ON you: score by distance from that target and rolling BACKWARDS scores exactly as
/// well as rolling sideways - both end the same distance from the point - while backwards keeps
/// you in the beam for its whole length. The owner named this case; the first scoring written
/// here would have failed it.
///
/// So a threat is modelled as the segment from where the action STARTS to where it is AIMED,
/// EXTENDED PAST the target by the length of a roll. Rolling backwards along a beam then stays
/// on that segment and scores zero, while rolling across it scores the full roll.
///
/// WHY THE EXTENSION IS SAFE FOR EVERYTHING ELSE, since nothing here can tell a beam from a
/// slam - that needs the game's skill data, which this tool does not read. The extension makes
/// the scoring prefer PERPENDICULAR escapes over backward ones for every kind of attack. For a
/// beam that is the only survivable direction. For a slam centred on the target, sideways is
/// just as safe as backwards - the roll covers the same distance either way - so the preference
/// costs nothing. A rule that is right for one shape and free for the other is the one to take
/// when the shape is unknown.
///
/// BACKWARDS IS NOT FORBIDDEN, only ranked below across: with a threat to each side it wins on
/// its own merits, because every direction is scored against every threat at once and the one
/// whose WORST case is best is the one taken.
///
/// THE "FREE FOR THE OTHER" CLAIM ABOVE IS TOO STRONG, and the owner named the case that breaks
/// it (2026-08-29): a WAVE ROLLING AT THE PLAYER. It is a wide FRONT, thin along its travel and
/// long across it, and the segment this models runs along its direction of travel - so rolling
/// perpendicular moves ALONG the front and does not leave it. Worse than not helping: rolling
/// forward THROUGH a thin front is the direction that plausibly works, and this scores it zero
/// because it stays on the segment, so the rule ranks the likely answer LAST. Not "less precise
/// on a third shape" - inverted on it.
///
/// AND THE SHAPE IS ONLY HALF OF IT. A wave MOVES, so whether a place is safe depends on WHEN
/// you get there, and there is no time anywhere in this scoring. Two hazards with identical
/// geometry need opposite answers depending on whether the front is advancing, which means a
/// per-animation table of shape and radius - the obvious next step, and the one deferred here -
/// could not express a wave even if somebody wrote it. It would need a travel speed too.
///
/// WHICH POINTS AT NOT WRITING A TABLE AT ALL. The game spawns effect entities for these and the
/// reader already flags them (<c>WorldEntity.IsEffect</c>), and <see cref="ProjectileWatch"/>
/// already derives a direction and a speed from watching a thing MOVE over successive reads. A
/// front that can be observed needs no table and never goes stale - the same "ask the game
/// rather than keep a list" move that answered the steering's hold. Unmeasured, so it is a lead
/// and not a plan.
///
/// NOTHING HERE IS CHANGED ON THE STRENGTH OF THAT. The line model is right for the case it was
/// built for and free for slams, which is two of the three; the third wants a decision about
/// what the danger model IS, and that decision is the owner's and is open.
/// </remarks>
public static class Escape
{
    /// <summary>
    /// The eight directions the movement keys can express, as world vectors.
    /// </summary>
    /// <remarks>
    /// The index of each option is its <see cref="MoveDirection"/> cast to an int, so the winner
    /// comes back knowing which keys produce it and nothing has to keep a parallel table in step.
    /// </remarks>
    public static IReadOnlyList<EscapeOption> Options(ScreenBasis basis)
    {
        var options = new List<EscapeOption>(MovementKeys.Compass.Count);
        foreach (MoveDirection direction in MovementKeys.Compass)
        {
            (float x, float y) = basis.World(direction);
            if (x != 0 || y != 0)
            {
                options.Add(new EscapeOption((int)direction, x, y));
            }
        }

        return options;
    }

    /// <summary>
    /// Distance from a point to a segment, which is the whole geometry this rests on.
    /// </summary>
    /// <remarks>
    /// Written out rather than reached for from a library because the degenerate case matters:
    /// an action whose origin and target are the same square - a monster casting on itself -
    /// has no direction, and the segment collapses to the point it should.
    /// </remarks>
    public static double DistanceToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double lengthSquared = (abx * abx) + (aby * aby);

        if (lengthSquared < 1e-6)
        {
            return Math.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
        }

        // How far along the segment the nearest point lies, clamped to its ends.
        double t = Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lengthSquared, 0, 1);
        double nx = ax + (t * abx), ny = ay + (t * aby);
        return Math.Sqrt(((px - nx) * (px - nx)) + ((py - ny) * (py - ny)));
    }

    /// <summary>
    /// How far a point sits from one threat, taking the threat as its extended line.
    /// </summary>
    /// <param name="rollDistance">
    /// How far past the target the danger is assumed to reach - the length of a roll, so that a
    /// backward roll along a beam cannot leave it.
    /// </param>
    public static double SafetyFrom(Threat threat, double x, double y, double rollDistance)
    {
        ArgumentNullException.ThrowIfNull(threat);

        double ox = threat.OriginX, oy = threat.OriginY;
        double tx = threat.TargetX, ty = threat.TargetY;

        // Extend past the target, along the direction the action runs. An action with no
        // direction (origin == target) keeps its bare point, which DistanceToSegment handles.
        double dx = tx - ox, dy = ty - oy;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length > 1e-3)
        {
            tx += dx / length * rollDistance;
            ty += dy / length * rollDistance;
        }

        return DistanceToSegment(x, y, ox, oy, tx, ty);
    }

    /// <summary>
    /// The safest of the offered directions, or null when none of them is worth taking.
    /// </summary>
    /// <param name="threats">
    /// What is being escaped. Only the ones aimed at the player belong here - a marker drawn for
    /// something landing elsewhere is not a reason to move.
    /// </param>
    /// <param name="atLeast">
    /// How much safer than STANDING STILL a direction must be before it is worth rolling. Not a
    /// fixed threshold: the question is never "is this position safe" - which nothing here can
    /// answer - but "is rolling there better than staying here", and a roll that improves
    /// nothing is a roll that spends a charge and a moment of control for nothing.
    /// </param>
    public static EscapeChoice? Best(
        IReadOnlyList<Threat> threats,
        IReadOnlyList<EscapeOption> options,
        float playerX,
        float playerY,
        double rollDistance,
        double atLeast = 1.0)
    {
        ArgumentNullException.ThrowIfNull(threats);
        ArgumentNullException.ThrowIfNull(options);

        if (threats.Count == 0 || options.Count == 0)
        {
            return null;
        }

        double standingStill = Worst(threats, playerX, playerY, rollDistance);

        EscapeChoice? best = null;
        foreach (EscapeOption option in options)
        {
            // The options arrive as unit vectors; a zero one would put the candidate on top of
            // the player and quietly score as standing still.
            double length = Math.Sqrt((option.X * option.X) + (option.Y * option.Y));
            if (length < 1e-3)
            {
                continue;
            }

            double x = playerX + (option.X / length * rollDistance);
            double y = playerY + (option.Y / length * rollDistance);

            // THE WORST case over every threat, not the sum: a direction that escapes one attack
            // by rolling into another is not an escape, and averaging would let it look like one.
            double safety = Worst(threats, x, y, rollDistance);
            if (best is null || safety > best.Value.Safety)
            {
                best = new EscapeChoice(option.Index, option.X, option.Y, safety);
            }
        }

        // Nothing on offer improves on where the character already stands - surrounded, or the
        // roll is too short to leave what is coming. Rolling anyway would be motion for its own
        // sake, and the caller is told so rather than handed a direction that does not help.
        return best is { } choice && choice.Safety >= standingStill + atLeast ? choice : null;
    }

    /// <summary>Distance to the nearest of several threats, each taken as its extended line.</summary>
    public static double Worst(
        IReadOnlyList<Threat> threats, double x, double y, double rollDistance)
    {
        ArgumentNullException.ThrowIfNull(threats);

        double worst = double.MaxValue;
        foreach (Threat threat in threats)
        {
            worst = Math.Min(worst, SafetyFrom(threat, x, y, rollDistance));
        }

        return worst == double.MaxValue ? 0 : worst;
    }
}
