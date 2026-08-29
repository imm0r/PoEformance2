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
public sealed record Threat(
    uint EntityId,
    string Name,
    string Path,
    ItemRarity Rarity,
    ActionKind Kind,
    AnimationKind Animation,
    float MonsterX,
    float MonsterY,
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
public sealed record EvasionTick(IReadOnlyList<Threat> Draw, bool Dodge, string Reason)
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
/// THE PLAYER STEERS AND THIS SUPPLIES THE TIMING, and that division is the design rather than
/// a gap in it. A dodge roll goes TOWARDS THE MOUSE, so the direction is already in the hand of
/// the person playing - and the thing they cannot do is know that a slam is committed before its
/// animation starts, which is exactly what the action fields give. Two minutes in front of a map
/// boss, the owner steering with the mouse alone and this pressing the key, cost zero hits.
///
/// SO DO NOT READ <c>Render.RotationCurrent</c> AS THE ROLL'S DIRECTION. During a backward roll
/// (animation 402) the model's rotation points OPPOSITE the travel, because that is what rolling
/// backwards is - <c>DodgeRollDirectionTests</c> measures it on real rolls, and anything using it
/// to work out where a roll went is exactly reversed on every one of them.
///
/// WHAT IS NOT ESTABLISHED: how the game behaves under WASD movement. Every recording here was
/// made with the game switched to click-to-move for the purpose, and the owner normally plays
/// WASD - where the mouse still steers the roll, but nothing has been recorded of it.
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

        var draw = new List<Threat>();
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

            if (warn.Admits(entity.Rarity, entity.Path))
            {
                draw.Add(new Threat(
                    entity.Id, entity.ShortName, entity.Path, entity.Rarity, action.Kind, kind,
                    entity.WorldX, entity.WorldY, action.TargetX, action.TargetY, entity.WorldZ,
                    aimed, distance));
            }

            if (aimed && act.Admits(entity.Rarity, entity.Path))
            {
                aimedForAct++;
            }

            if (draw.Count >= MostThreats)
            {
                break;
            }
        }

        // Sorted so the closest threat is first: a capped list should keep the ones that matter,
        // and the overlay draws in this order too.
        draw.Sort((a, b) => a.DistanceToPlayer.CompareTo(b.DistanceToPlayer));

        if (aimedForAct == 0)
        {
            return new EvasionTick(draw, false, Describe(draw, act.Enabled));
        }

        // Everything below decides whether to PRESS, and each gate is a reason of its own so the
        // status line can say which one stopped it.
        if (!act.Enabled)
        {
            return new EvasionTick(draw, false, $"{aimedForAct} aimed at you (acting is off)");
        }

        // Armed with no key to press. Its own reason because it is the one misconfiguration that
        // otherwise looks exactly like a working tool: everything is switched on, threats are
        // being seen, and nothing ever happens. Unlike the flask keys, this one is NOT read from
        // the game - see DodgeKeyHints for why - so an empty setting is the ordinary first state.
        if (settings.DodgeKey == 0)
        {
            return new EvasionTick(draw, false, $"{aimedForAct} aimed at you, but no dodge key is set");
        }

        if (!gameFocused)
        {
            return new EvasionTick(draw, false, "game not focused");
        }

        if (_lastDodge is long last && nowMs - last < settings.CooldownMs)
        {
            return new EvasionTick(draw, false, $"{aimedForAct} aimed at you, dodge cooling down");
        }

        _lastDodge = nowMs;
        return new EvasionTick(draw, true, $"dodging: {aimedForAct} action(s) aimed at you");
    }

    /// <summary>The idle readout, so "nothing happened" is legible.</summary>
    private static string Describe(IReadOnlyList<Threat> draw, bool acting)
    {
        if (draw.Count == 0)
        {
            return acting ? "watching (nothing incoming)" : "watching (nothing incoming, acting is off)";
        }

        Threat nearest = draw[0];
        return $"watching {draw.Count} action(s), nearest {nearest.Name} at {nearest.DistanceToPlayer:F0} units";
    }

    private static float Distance(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
