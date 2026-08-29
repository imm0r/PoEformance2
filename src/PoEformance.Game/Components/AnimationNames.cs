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
/// CastType - and nothing else. The table is GENERATED FROM THE GAME by <c>--animdump</c>: 1087
/// rows read straight out of Data/Balance/Animation.dat. It used to be a transcription of the AHK
/// tool's hand-maintained <c>ahk/AnimationID.ahk</c>, and the change was not cosmetic - see below.
///
/// THE TABLE IS ALIGNED, and that is checked rather than assumed - the stat names in the same
/// folder are off by one, so the question is a real one. The AHK tool's own drift investigation
/// recorded eight ids observed live while playing; seven land on the right name here (0 Idle,
/// 4 Run, 195 FixedRun, 268 DodgeRoll, 402 DodgeRollBack, and the cast types 299 SparkAdditive,
/// 472 Flamewall, 474 OrbOfStorms), and the eighth is the check that earns its keep.
///
/// WHY THE EIGHTH MOVED. Three rows have been inserted into the game's file since that
/// transcription - at 584, 599 and 904 - shifting everything after them by one, two and three.
/// All seven ids above land BELOW the first insertion; 872 is the only one above it, and the AHK
/// tool's live reading of it as SprintEnd is off by exactly two against this table, where 874 is
/// SprintEnd. An outside observation landing precisely where the insertions predict is what turns
/// that finding from one recording's word into a measurement. See <c>AimTests</c>.
///
/// WHAT IT COST while the old table stood: 500 of its 1084 rows named the wrong animation, 177
/// changing <see cref="AnimationKind"/>, and 37 classified quiet when the real animation is not -
/// threats the evasion filter dropped in silence. <see cref="IsQuiet"/> is deliberately asked the
/// safe way round so an UNKNOWN animation still counts; a confident WRONG name walks past it.
///
/// WHAT IS NOT ESTABLISHED: those readings were off the PLAYER. That the same table serves
/// MONSTERS follows from it being the game's own CastType enum rather than a per-monster list,
/// and it is not proven here. The tracker tab prints the id beside the name for exactly this
/// reason - a monster whose names read like nonsense would say so at a glance.
///
/// A name is a LABEL and never a fact. The table no longer drifts by being hand-maintained, but
/// it still ages with the game: re-run <c>--animdump</c> after a patch and diff.
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

    /// <summary>
    /// Names read from the RUNNING GAME, which beat the shipped ones.
    /// </summary>
    /// <remarks>
    /// THE SHIPPED TABLE IS HAND-MAINTAINED AND DEMONSTRABLY DRIFTS - its own header says so, and
    /// it says a name is "a LABEL, never a fact". The game carries the truth: an action wrapper
    /// points straight at the animation's own row in Data/Balance/Animation.dat, whose first
    /// field is its id string (see <c>ActionWrapper.AnimationRow</c> in the schema). So an
    /// animation the tool actually SEES can be named by the game rather than by the table.
    ///
    /// The first thing that bought was a correction, and then a bigger one. Six animations read
    /// live disagreed with the file on exactly one: 889, InteractLeanWell in the file,
    /// ElementalWeakness in the game. From six rows that looked like a table drifting one row at
    /// a time, and it was hand-patched as such. It was not. Reading the WHOLE table (--animdump)
    /// showed three rows inserted since - at 584, 599 and 904 - shifting 500 of the file's 1084
    /// rows by one, two or three, with zero rows left over. 889 was a symptom of the shift, and
    /// patching it made the file less consistent rather than more.
    ///
    /// THE LESSON IS ABOUT THE SAMPLE, not about the table: six rows can tell you that something
    /// is wrong and can never tell you what. Only a whole-table read separates "a few bad rows"
    /// from "everything above 584 moved".
    ///
    /// Concurrent because the reader thread learns while the render thread asks.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _learned = new();

    /// <summary>
    /// The classification cache. Concurrent for the same reason, and CLEARED PER ID when that id
    /// is learned - a kind derived from the old name would otherwise outlive it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, AnimationKind> _kinds = new();

    private AnimationNames(Dictionary<int, string> names) => _names = names;

    /// <summary>How many names were loaded.</summary>
    public int Count => _names.Count;

    /// <summary>How many the running game has supplied.</summary>
    public int LearnedCount => _learned.Count;

    /// <summary>
    /// Ids where the game disagrees with the shipped table, as (id, shipped, game).
    /// </summary>
    /// <remarks>
    /// Worth being able to ask rather than merely correcting silently: a growing list means the
    /// file wants re-extracting, and a person reading the table has no other way to find out.
    /// </remarks>
    public IReadOnlyList<(int Id, string Shipped, string Game)> Disagreements =>
        [.. _learned
            .Where(pair => _names.TryGetValue(pair.Key, out string? shipped)
                           && !string.Equals(shipped, pair.Value, StringComparison.Ordinal))
            .Select(pair => (pair.Key, _names[pair.Key], pair.Value))
            .OrderBy(row => row.Key)];

    /// <summary>
    /// Records what the game calls an animation. The game wins over the shipped table.
    /// </summary>
    public void Learn(int id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _learned[id] = name.Trim();
        _kinds.TryRemove(id, out _);
    }

    /// <summary>The game's name for an animation id, or null when nothing has one.</summary>
    public string? Of(int id)
        => _learned.TryGetValue(id, out string? live) ? live
            : _names.TryGetValue(id, out string? name) ? name
            : null;

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
