namespace PoEformance.Game.World;

/// <summary>A class of entity that exists in the game's map but is never worth looking at.</summary>
public enum NoiseKind
{
    /// <summary>Cosmetics: attachments, microtransactions, hairstyles, stash skins.</summary>
    Cosmetic,

    /// <summary>Engine assets: effects, materials, audio, environment nodes.</summary>
    Engine,

    /// <summary>Invisible daemons that carry a monster's modifiers rather than being a monster.</summary>
    Daemon,

    /// <summary>Pets, clones, and the base classes player summons are built from.</summary>
    Summoned,

    /// <summary>Decorator markers for things already drawn some other way.</summary>
    Marker,

    /// <summary>Hideout decorations.</summary>
    Hideout,
}

/// <summary>
/// Decides which entities are not worth reading.
/// </summary>
/// <remarks>
/// Plenty of what is in the game's entity map is not a thing. It is effect nodes, material
/// definitions, invisible daemons carrying a rare monster's modifiers, pets, and the cosmetic
/// attachments hanging off every player - all real entities, none of them anything to a person
/// looking at a radar.
///
/// HOW MUCH it removes, measured rather than assumed, because the reference's figure does not
/// transfer: the AHK tool reports a dense fight reading 182 entities to show 96, and on a
/// recorded scene here the same patterns dropped 4 of 123. The difference is not the patterns.
/// It is that this reader already skips anything with no Render component, which is most of
/// what that filter was catching - so the remaining 3% is the noise that has a POSITION and
/// would otherwise be drawn: the pets, the ground-effect daemons, the hideout doodads.
///
/// Which makes this a readability fix that also saves some reading, rather than the other way
/// round. It still runs on the path before the components are walked, because there is no
/// reason to pay for an entity that is about to be thrown away.
///
/// PORTED FROM THE AHK TOOL's JunkFilter, patterns included. They are matched as
/// case-insensitive substrings against the metadata path, which is what makes the list short
/// enough to read: "/fx/" catches every effect there will ever be, without anybody maintaining
/// a list of them.
///
/// Deliberately NOT applied to the player, and deliberately switchable per class. One of these
/// classes is somebody's genuine question - a hideout builder wants the doodads, and reverse
/// engineering wants all of it - so the answer is a switch rather than a decision made here.
/// </remarks>
public sealed class NoiseFilter
{
    /// <summary>
    /// What each class is recognised by. Substrings of the metadata path, matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// From the AHK tool, which arrived at them against the real game. Two are worth a note
    /// because they look too broad and are not: "/ao/" is the model-asset node rather than any
    /// word containing "ao", since the slashes make it a path SEGMENT; and "monstermods" is the
    /// invisible entity a rare monster's modifiers hang off, not the monster.
    /// </remarks>
    private static readonly (NoiseKind Kind, string[] Patterns)[] Definitions =
    [
        (NoiseKind.Cosmetic, ["/attachments", "microtransactions", "/timelines/", "stashskins", "hairstyles", "/outfits/"]),
        (NoiseKind.Engine, ["/fx/", "/mat/", "/ao/", "/epk/", "/graph/", "/audio/", "/environment/"]),
        (NoiseKind.Daemon, ["monstermods", "essencemoddaemons", "tormentedspirits", "/daemon/"]),
        (NoiseKind.Summoned, ["/pet/", "/clone/", "playersummoned"]),
        (NoiseKind.Marker, ["bossroomminimapicon", "/runemarked"]),
        (NoiseKind.Hideout, ["hideoutdoodad"]),
    ];

    private readonly HashSet<NoiseKind> _off = [];

    /// <summary>The whole filter. Off means every entity is read, which is what RE wants.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Extra paths to treat as noise, added by the user.</summary>
    public List<string> Custom { get; } = [];

    /// <summary>Every class this knows, for a settings list.</summary>
    public static IEnumerable<NoiseKind> Kinds => Definitions.Select(d => d.Kind);

    /// <summary>What a class is recognised by, for a settings list.</summary>
    public static IReadOnlyList<string> PatternsFor(NoiseKind kind)
        => Definitions.First(d => d.Kind == kind).Patterns;

    /// <summary>Whether a class is being filtered out.</summary>
    public bool IsOn(NoiseKind kind) => !_off.Contains(kind);

    /// <summary>Turns one class on or off without touching the others.</summary>
    public void Set(NoiseKind kind, bool on)
    {
        if (on)
        {
            _off.Remove(kind);
        }
        else
        {
            _off.Add(kind);
        }
    }

    /// <summary>True when an entity is not worth reading or drawing.</summary>
    /// <remarks>
    /// Called once per entity per tick with only the path in hand, so it stays a handful of
    /// substring checks and nothing else. An empty path is NOT noise - it is an entity that
    /// failed to read, which is a different problem and belongs to whoever asked.
    /// </remarks>
    public bool IsNoise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!Enabled || path.Length == 0)
        {
            return false;
        }

        foreach ((NoiseKind kind, string[] patterns) in Definitions)
        {
            if (_off.Contains(kind))
            {
                continue;
            }

            foreach (string pattern in patterns)
            {
                if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (string pattern in Custom)
        {
            if (pattern.Length > 0 && path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Which class caught a path, for explaining a decision.</summary>
    /// <remarks>
    /// A filter that silently removes things is a filter nobody can debug. When an entity is
    /// missing from the map this answers why in one call, without anybody reading the pattern
    /// list and guessing.
    /// </remarks>
    public NoiseKind? Explain(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach ((NoiseKind kind, string[] patterns) in Definitions)
        {
            foreach (string pattern in patterns)
            {
                if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }
        }

        return null;
    }
}
