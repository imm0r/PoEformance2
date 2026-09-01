using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One incoming action that is worth knowing about, in world coordinates.</summary>
/// <param name="Aimed">
/// Whether its landing spot is inside the danger radius of the player - the difference between
/// "something is winding up over there" and "that is going to land on me".
/// </param>
/// <param name="DistanceToPlayer">World units from the landing spot to the player.</param>
/// <param name="Animation">
/// What the animation calls it, or <see cref="AnimationKind.Unknown"/>. Carried so the overlay
/// can say WHAT is coming, and so an id nobody has a name for is still visible as something.
/// </param>
/// <param name="MonsterX">Where the monster is standing NOW - the end of the line the overlay draws.</param>
/// <param name="OriginX">
/// Where the ACTION started, from its own wrapper rather than from the monster.
/// </param>
/// <remarks>
/// THE ORIGIN AND THE MONSTER POSITION ARE TWO DIFFERENT THINGS and both are here, which is worth
/// a sentence because they are nearly equal and the difference only shows in the case that
/// matters. The monster's position is where it is at this instant; the origin is where the action
/// it committed to was aimed FROM, quantised to a grid cell. <see cref="Escape"/> needs the
/// latter: it is what makes origin-to-target a line to get off rather than a pair of points, and
/// a monster that has walked a step since committing would otherwise tilt that line. The overlay
/// draws from the former, because a line that starts anywhere but the monster you can see reads
/// as a bug.
/// </remarks>
public sealed record Threat(
    uint EntityId,
    string Name,
    string Path,
    ItemRarity Rarity,
    ActionKind Kind,
    AnimationKind Animation,
    float MonsterX,
    float MonsterY,
    float OriginX,
    float OriginY,
    float TargetX,
    float TargetY,
    float TargetZ,
    bool Aimed,
    float DistanceToPlayer);

/// <summary>What the planner decided on one tick, including why it did nothing.</summary>
/// <param name="Draw">Threats the WARN gate admitted - what the overlay should show.</param>
/// <param name="Dodge">
/// True when the dodge key should be pressed now. The key itself is not here: the planner is
/// pure and knows nothing about input, exactly as <see cref="AutoFlask"/> returns rules rather
/// than pressing them.
/// </param>
/// <param name="Reason">
/// Human-readable, always set - the same discipline the flask engine keeps. "Why did nothing
/// happen" is the question actually asked of a feature like this, and a silent no-op cannot
/// answer it.
/// </param>
/// <param name="Steer">
/// Which movement keys to hold so the roll goes where it should, or
/// <see cref="MoveDirection.None"/> to leave the direction to the player.
/// </param>
public sealed record EvasionTick(
    IReadOnlyList<Threat> Draw,
    bool Dodge,
    string Reason,
    MoveDirection Steer = MoveDirection.None)
{
    /// <summary>Nothing seen, nothing done.</summary>
    public static EvasionTick Idle { get; } = new([], false, "not started");

    /// <summary>The threats actually aimed at the player, for a status line.</summary>
    public int AimedCount => Draw.Count(t => t.Aimed);
}

/// <summary>
/// Decides what to warn about and when to dodge. Pure: it reads no memory and presses no keys.
/// </summary>
/// <remarks>
/// THE SPLIT IS THE SAME ONE <see cref="AutoFlask"/> MAKES, and for the same reason: the
/// decision is the part worth testing, and it can only be tested without a game if it neither
/// reads memory nor sends input. This takes a snapshot and a clock and returns what to draw and
/// whether to roll; the composition root does the pressing.
///
/// WHAT MAKES A THREAT. A monster's Actor component says what it has committed to and where that
/// lands, settled against the game over 210 arrivals and 1649 aimed casts (see
/// <c>MonsterActionsSettledTests</c>). An action counts here when its landing spot is within the
/// configured radius of the player - which is a question about a PLACE, and so answerable, where
/// "is this attack dangerous" is not.
///
/// THE ANIMATION IS A FILTER, NOT THE SIGNAL. It says what KIND of thing is happening - a slam,
/// a leap, a charge - and the filter asks the question the safe way round, admitting anything
/// that is not plainly quiet rather than trying to list what is dangerous. That matters because
/// the action arrives BEFORE the animation does: the recordings hold frames with a committed
/// skill and a real target while the animation still reads Idle, and those are precisely the
/// frames a warning wants. An animation-only filter would throw them away.
///
/// WHAT DECIDES A ROLL'S DIRECTION, settled by the owner testing it under WASD movement:
/// a held movement key wins, and with none held the roll goes towards the cursor.
///
/// SO THERE ARE TWO MODES HERE AND BOTH ARE WORTH HAVING. Left alone, the tool supplies only the
/// TIMING - the half a person cannot do, since the action fields say an attack is committed and
/// where it lands before any animation shows it - and the player keeps the steering. Two minutes
/// in front of a map boss on those terms cost zero hits. With <c>Steer</c> switched on it also
/// holds a movement key for the length of the roll, because timing alone is not enough for the
/// case that prompted it: a boss channelling a beam AT you is dodged by going ACROSS it, and a
/// player pointing at the boss - which is where you point when you are fighting it - rolls along
/// the beam instead. <see cref="Escape"/> is the geometry that tells those apart.
///
/// DO NOT READ <c>Render.RotationCurrent</c> AS THE ROLL'S DIRECTION. It follows the cursor, so
/// it is right only for the no-key case; on a roll steered by a movement key it points somewhere
/// else entirely, and on a backward one exactly the opposite way. See
/// <c>DodgeRollDirectionTests</c>, where that is measured on real rolls. The steering does not
/// need it: it says which KEYS to hold, and the game resolves those into a direction itself.
/// </remarks>
public sealed class EvasionPlanner
{
    /// <summary>Most threats reported in one tick. A breach fills the screen.</summary>
    private const int MostThreats = 64;

    // Volatile for the same reason the flask rules are: the config window swaps these from its
    // own thread while the reader thread is evaluating, and a whole-reference assignment means a
    // tick sees the old settings or the new ones, never a half-applied mix.
    private volatile EvasionSettings _settings;

    /// <summary>
    /// When the last dodge was pressed, or null when none has been.
    /// </summary>
    /// <remarks>
    /// NULLABLE RATHER THAN A SENTINEL, and that is a bug fixed rather than a style. The obvious
    /// "never happened" value is <c>long.MinValue</c>, and with it <c>now - last</c> OVERFLOWS
    /// to a negative number - so the very first threat of a session reads as still cooling down
    /// and the feature never acts at all. It looks like a working tool: threats are seen, the
    /// status line says "cooling down", and nothing is ever pressed.
    /// </remarks>
    private long? _lastDodge;

    public EvasionPlanner(EvasionSettings? settings = null)
        => _settings = (settings ?? EvasionSettings.Default).Normalised();

    /// <summary>The configuration in force.</summary>
    public EvasionSettings Settings => _settings;

    /// <summary>The last tick's outcome, for the overlay's status line.</summary>
    public EvasionTick LastTick { get; private set; } = EvasionTick.Idle;

    /// <summary>
    /// The game's own table of ground-effect kinds, which is how HARMFUL is known.
    /// </summary>
    /// <remarks>
    /// Set once at startup, never from a setting - it is a table read out of the install, not a
    /// preference. Null is survivable and the status line says so: without it every ground effect
    /// counts as harmful, which is the safe direction and also the wrong one for the six rows
    /// that grant something. A Consecration nobody can identify becomes a patch to roll out of.
    /// </remarks>
    public GroundEffectTypeTable? GroundTypes { get; set; }

    /// <summary>Swaps in a new configuration while running.</summary>
    /// <remarks>
    /// Does NOT clear the dodge cooldown, on the same argument the flask engine makes: otherwise
    /// every keystroke typed into a settings field would hand the feature a free press.
    /// </remarks>
    public void Configure(EvasionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalised();
    }

    /// <summary>
    /// Decides what to draw and whether to dodge.
    /// </summary>
    /// <param name="snapshot">The world as it was last read.</param>
    /// <param name="animations">The animation table, for the kind filter.</param>
    /// <param name="gameFocused">
    /// Whether the GAME window has focus. A safety property rather than a feature: keystrokes
    /// land wherever focus is, so a dodge press while alt-tabbed types into a browser.
    /// </param>
    /// <param name="nowMs">A monotonic clock, e.g. Environment.TickCount64.</param>
    public EvasionTick Evaluate(
        WorldSnapshot? snapshot, AnimationNames animations, bool gameFocused, long nowMs)
    {
        EvasionTick tick = Decide(snapshot, animations ?? AnimationNames.Empty, gameFocused, nowMs);
        LastTick = tick;
        return tick;
    }

    private EvasionTick Decide(
        WorldSnapshot? snapshot, AnimationNames animations, bool gameFocused, long nowMs)
    {
        EvasionSettings settings = _settings;
        EvasionGate warn = settings.WarnOrDefault;
        EvasionGate act = settings.ActOrDefault;

        if (!warn.Enabled && !act.Enabled)
        {
            return new EvasionTick([], false, "disabled");
        }

        if (snapshot is not { InGame: true } world)
        {
            return new EvasionTick([], false, "not in game");
        }

        if (world.Player is not WorldEntity player)
        {
            return new EvasionTick([], false, "no player");
        }

        // EVERY action with a place, not only the ones a gate admits. The gates decide what is
        // worth DRAWING and what is worth REACTING to; the steering has a third question - where
        // is it safe to land - and a white monster's slam makes a spot unsafe whether or not it
        // was ever worth a marker. So the walk collects them all and the gates filter afterwards.
        var seen = new List<Threat>();
        int aimedForAct = 0;

        foreach (WorldEntity entity in world.Entities)
        {
            if (entity.Kind != EntityKind.Monster || entity.IsFriendly)
            {
                continue;
            }

            // Null means the reader was not asked for actions - a different answer from "it is
            // doing nothing", and one that must not be reported as a quiet monster.
            if (entity.Action is not ActorAction action || action.Kind == ActionKind.None)
            {
                continue;
            }

            // An action with no readable target has nowhere to draw and nothing to dodge, but
            // it is NOT nothing - see ActionReader, which keeps the kind and reports zero reach.
            if (action.TargetX == 0 && action.TargetY == 0)
            {
                continue;
            }

            AnimationKind kind = animations.KindOf(action.AnimationId);
            if (settings.OnlyDangerousAnimations && action.AnimationId >= 0 && animations.IsQuiet(action.AnimationId))
            {
                // Asked the safe way round on purpose: "not quiet" rather than "dangerous", so an
                // animation nobody has a name for still counts. See AnimationNames.IsQuiet.
                continue;
            }

            float distance = Distance(action.TargetX, action.TargetY, player.WorldX, player.WorldY);
            bool aimed = distance <= settings.DangerRadius;

            seen.Add(new Threat(
                entity.Id, entity.ShortName, entity.Path, entity.Rarity, action.Kind, kind,
                entity.WorldX, entity.WorldY, action.OriginX, action.OriginY,
                action.TargetX, action.TargetY, entity.WorldZ, aimed, distance));

            if (aimed && act.Admits(entity.Rarity, entity.Path))
            {
                aimedForAct++;
            }

            if (seen.Count >= MostThreats)
            {
                break;
            }
        }

        // Sorted so the closest threat is first: a capped list should keep the ones that matter,
        // and the overlay draws in this order too.
        seen.Sort((a, b) => a.DistanceToPlayer.CompareTo(b.DistanceToPlayer));

        List<Threat> draw = [.. seen.Where(t => warn.Admits(t.Rarity, t.Path))];

        // The ground, which needs no gate: a patch of fire is dangerous to stand in whoever left
        // it, and there is no rarity to admit. Collected before the aimed check because standing
        // in one is a reason to move even when nothing is winding up.
        List<GroundHazard> ground = settings.UsesGround ? Burning(world, settings) : [];
        GroundHazard? standingIn = null;
        foreach (GroundHazard patch in ground)
        {
            if (Escape.SafetyFrom(patch, player.WorldX, player.WorldY) <= 0)
            {
                standingIn = patch;
                break;
            }
        }

        // A SECOND REASON TO ROLL, and the only one in this planner that is not about an incoming
        // action: the danger is already underneath the character and will not announce itself.
        bool escaping = settings.EscapeGroundEffects && standingIn is not null;

        if (aimedForAct == 0 && !escaping)
        {
            return new EvasionTick(draw, false, Describe(draw, act.Enabled, ground.Count));
        }

        string why = escaping && aimedForAct == 0
            ? $"standing in {standingIn!.Value.Name}"
            : $"{aimedForAct} aimed at you";

        // Everything below decides whether to PRESS, and each gate is a reason of its own so the
        // status line can say which one stopped it.
        if (!act.Enabled)
        {
            return new EvasionTick(draw, false, $"{why} (acting is off)");
        }

        // Armed with no key to press. Its own reason because it is the one misconfiguration that
        // otherwise looks exactly like a working tool: everything is switched on, threats are
        // being seen, and nothing ever happens. Unlike the flask keys, this one is NOT read from
        // the game - see DodgeKeyHints for why - so an empty setting is the ordinary first state.
        if (settings.DodgeKey == 0)
        {
            return new EvasionTick(draw, false, $"{why}, but no dodge key is set");
        }

        if (!gameFocused)
        {
            return new EvasionTick(draw, false, "game not focused");
        }

        if (_lastDodge is long last && nowMs - last < settings.CooldownMs)
        {
            return new EvasionTick(draw, false, $"{why}, dodge cooling down");
        }

        // WHICH WAY, decided last and before the cooldown is spent - a tick that ends up not
        // rolling must not have consumed the charge that the next one needs.
        MoveDirection steer = MoveDirection.None;
        string how = string.Empty;

        if (settings.CanSteer)
        {
            if (ScreenBasis.Derive(world.Matrix, player.WorldX, player.WorldY, player.WorldZ)
                is not ScreenBasis basis)
            {
                // Rolls anyway, unsteered, because that is the behaviour this feature had before
                // steering existed and it is a good one - the player is still pointing somewhere.
                // Refusing here would turn "I could not work out the directions" into "you take
                // the hit", which is the wrong way round for the one thing this exists to prevent.
                how = " (unsteered: the camera cannot say which way is which)";
            }
            else if (Escape.Best(
                         seen, ground, Escape.Options(basis), player.WorldX, player.WorldY,
                         settings.RollDistance) is not EscapeChoice choice)
            {
                // Nowhere on offer is better than standing still: every direction lands in
                // something, or the threat is too big to roll out of. Rolling would spend the
                // charge to end up just as exposed, and with the player's own aim overridden
                // on the way - so it does not, and says so. The cooldown is left alone rather
                // than cleared: nothing was spent, and it is already past by this line.
                return new EvasionTick(
                    draw, false, $"{why}, but no direction is any safer");
            }
            else
            {
                steer = (MoveDirection)choice.Index;
                how = $" {steer}, {choice.Safety:F0} units clear";
            }
        }

        _lastDodge = nowMs;
        return new EvasionTick(
            draw, true, $"dodging{how}: {why}", steer);
    }

    /// <summary>
    /// Every patch of harmful ground in the area, as circles to stay out of.
    /// </summary>
    /// <remarks>
    /// HELPFUL GROUND IS LEFT OUT, which is the whole payoff of classifying the table: six of its
    /// 53 rows grant something, and rolling away from a Consecration would be the tool actively
    /// making things worse.
    ///
    /// UNCLEAR COUNTS AS HARMFUL, and so does a row no table could name. The two mistakes are not
    /// symmetric - leaving a neutral patch costs a roll charge, staying in a burning one costs
    /// life - so uncertainty resolves towards moving.
    ///
    /// REMEMBERED SIGHTINGS ARE REFUSED. A remembered ground effect is one the game stopped
    /// listing, which for ground means it burned out; steering around it would be steering around
    /// nothing, and worse, it would keep doing so after the danger had gone.
    ///
    /// NOT FILTERED ON IsFriendly, deliberately, because that flag says nothing here: across both
    /// committed captures not one ground effect is marked friendly. Filtering on it would look
    /// like protection against rolling away from your own ground and provide none.
    /// </remarks>
    private List<GroundHazard> Burning(WorldSnapshot world, EvasionSettings settings)
    {
        var found = new List<GroundHazard>();
        foreach (WorldEntity entity in world.Entities)
        {
            if (!entity.IsGroundEffect || entity.IsRemembered)
            {
                continue;
            }

            GroundEffectType? kind = GroundTypes?.Find(entity.GroundType);
            if (kind is { Harm: GroundHarm.Helpful })
            {
                continue;
            }

            found.Add(new GroundHazard(
                entity.WorldX, entity.WorldY, settings.GroundRadius,
                kind is null ? "unidentified ground" : kind.Caption));

            if (found.Count >= MostThreats)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>The idle readout, so "nothing happened" is legible.</summary>
    private static string Describe(IReadOnlyList<Threat> draw, bool acting, int ground)
    {
        // The ground count rides along on every line: a person who switched this on wants to know
        // it is SEEING patches, and "watching (nothing incoming)" on a screen full of fire is the
        // shape of readout that sends somebody hunting for a bug that is not there.
        string patches = ground > 0 ? $", {ground} patch(es) of ground" : string.Empty;

        if (draw.Count == 0)
        {
            return (acting ? "watching (nothing incoming)" : "watching (nothing incoming, acting is off)")
                + patches;
        }

        Threat nearest = draw[0];
        return $"watching {draw.Count} action(s), nearest {nearest.Name} "
            + $"at {nearest.DistanceToPlayer:F0} units{patches}";
    }

    private static float Distance(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
