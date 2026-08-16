namespace PoEformance.Game.Components;

/// <summary>What an entity is doing, as coarsely as a name can be trusted to say.</summary>
/// <remarks>
/// Read off the animation's NAME rather than off a list of ids, for the same reason entities
/// are classified by metadata path: a substring rule covers every animation there will ever be,
/// while a list of ids is a list somebody has to maintain against a game that adds monsters
/// every league.
/// </remarks>
public enum AnimationKind
{
    /// <summary>No name for this id, or a name none of the rules recognised.</summary>
    /// <remarks>
    /// NOT harmless. The table has 1084 entries and the game has more; anything that draws a
    /// warning treats this as "something is happening" rather than as nothing, because a
    /// missing marker reads as "there is nothing there".
    /// </remarks>
    Unknown,

    Idle,
    Moving,

    /// <summary>Being hurt, staggered or stunned - happening TO it, not by it.</summary>
    Hurt,

    Dying,
    Attacking,
    Casting,

    /// <summary>A slam, stomp or crash: the ones with a wind-up worth seeing.</summary>
    Slam,

    Leap,
    Charge,
}

/// <summary>
/// Turns the number in an entity's Actor component into the game's own animation name.
/// </summary>
/// <remarks>
/// The Actor component holds one integer for what the entity is doing right now - the game's
/// CastType - and nothing else. The table comes from the AHK tool's hand-maintained
/// <c>ahk/AnimationID.ahk</c>, 1084 entries, extracted to a TSV.
///
/// THE TABLE IS ALIGNED, and that is checked rather than assumed - the stat names in the same
/// folder are off by one, so the question is a real one. The AHK tool's own drift
/// investigation recorded eight ids observed live while playing, and every one of them lands on
/// the right name here: 0 Idle, 4 Run, 195 FixedRun, 268 DodgeRoll, 402 DodgeRollBack,
/// 872 SprintEnd, and the cast types 299 SparkAdditive, 472 Flamewall, 474 OrbOfStorms. Eight
/// independent readings, no shift.
///
/// WHAT IS NOT ESTABLISHED: all eight were read off the PLAYER. That the same table serves
/// MONSTERS follows from it being the game's own CastType enum rather than a per-monster list,
/// and it is not proven here. The tracker tab prints the id beside the name for exactly this
/// reason - a monster whose names read like nonsense would say so at a glance.
///
/// A name is a LABEL and never a fact: an id with no row keeps its number, and the table drifts
/// with the game, so names that stop making sense mean the table needs extracting again.
/// </remarks>
public sealed class AnimationNames
{
    /// <summary>
    /// How a name is recognised. Order matters - the FIRST match wins.
    /// </summary>
    /// <remarks>
    /// The dangerous kinds are tested first, so "LeapSlam" reads as a slam and
    /// "ShapeshiftBearSlamEnraged" does too. Idle and the movements are last because their
    /// words turn up inside other names - "SprintEnd" is a movement, "ChargeEnd" is not.
    /// </remarks>
    private static readonly (AnimationKind Kind, string[] Words)[] Rules =
    [
        (AnimationKind.Slam, ["slam", "stomp", "crash", "smash", "quake"]),
        (AnimationKind.Leap, ["leap", "jump", "vault"]),
        (AnimationKind.Charge, ["charge", "rush", "dash", "chargeend"]),
        (AnimationKind.Casting, ["cast", "spell", "summon", "channel", "conjur", "totem"]),
        (AnimationKind.Attacking, [
            "melee", "attack", "strike", "cleave", "swing", "throw", "shoot", "bow", "impale",
            "slash", "stab", "bite", "claw", "whirl", "spit", "breath", "beam", "fire",
        ]),
        (AnimationKind.Dying, ["death", "dying", "corpse"]),
        (AnimationKind.Hurt, ["takehit", "stagger", "stun", "knockback", "flinch", "recover"]),
        (AnimationKind.Moving, ["run", "walk", "move", "sprint", "dodge", "roll", "step", "flee", "emerge"]),
        (AnimationKind.Idle, ["idle"]),
    ];

    public static AnimationNames Empty { get; } = new([]);

    private readonly Dictionary<int, string> _names;
    private readonly Dictionary<int, AnimationKind> _kinds = [];

    private AnimationNames(Dictionary<int, string> names) => _names = names;

    /// <summary>How many names were loaded.</summary>
    public int Count => _names.Count;

    /// <summary>The game's name for an animation id, or null when the table has none.</summary>
    public string? Of(int id) => _names.TryGetValue(id, out string? name) ? name : null;

    /// <summary>The name, or the bare number when there is none - for showing a person.</summary>
    public string Label(int id) => Of(id) ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// What sort of thing this animation is. <see cref="AnimationKind.Unknown"/> for an id the
    /// table does not have, and for a name no rule recognised.
    /// </summary>
    /// <remarks>
    /// Cached per id: the classification is a handful of substring searches, the answer never
    /// changes, and this is asked for every monster on screen sixty times a second.
    /// </remarks>
    public AnimationKind KindOf(int id)
    {
        if (_kinds.TryGetValue(id, out AnimationKind known))
        {
            return known;
        }

        AnimationKind kind = Classify(Of(id));
        _kinds[id] = kind;
        return kind;
    }

    /// <summary>Whether this is one of the two kinds that mean nothing is coming.</summary>
    /// <remarks>
    /// Stated as "quiet" rather than as "dangerous" ON PURPOSE, and it is the whole safety of
    /// the filter built on it: an id nobody has a name for is not quiet. Asking the question
    /// the other way round - is this dangerous - would make every unrecognised animation
    /// silently harmless, which is the failure a danger overlay cannot have.
    /// </remarks>
    public bool IsQuiet(int id) => KindOf(id) is AnimationKind.Idle or AnimationKind.Moving;

    /// <summary>Classifies a name by the first rule that matches part of it.</summary>
    public static AnimationKind Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return AnimationKind.Unknown;
        }

        foreach ((AnimationKind kind, string[] words) in Rules)
        {
            foreach (string word in words)
            {
                if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }
        }

        return AnimationKind.Unknown;
    }

    /// <summary>Loads the TSV. A missing file is not an error - the ids still read.</summary>
    public static AnimationNames Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        var names = new Dictionary<int, string>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out int id))
            {
                continue;
            }

            names[id] = line[(tab + 1)..].Trim();
        }

        return new AnimationNames(names);
    }
}
