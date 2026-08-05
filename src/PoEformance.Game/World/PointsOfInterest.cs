namespace PoEformance.Game.World;

/// <summary>
/// What a point of interest IS, which decides how it is drawn and whether it is worth a route.
/// </summary>
public enum PoiKind
{
    None = 0,

    /// <summary>A way out of the area. The thing most often being looked for.</summary>
    AreaTransition,

    /// <summary>A waypoint - travel, and a landmark even when already unlocked.</summary>
    Waypoint,

    /// <summary>A checkpoint: where the area is re-entered after a death.</summary>
    Checkpoint,

    /// <summary>A strongbox, chest or other openable worth walking to.</summary>
    Chest,

    /// <summary>A league mechanic's object - ritual altars, expedition markers, and so on.</summary>
    Mechanic,

    /// <summary>A shrine.</summary>
    Shrine,
}

/// <summary>
/// Recognises points of interest from an entity's metadata path.
/// </summary>
/// <remarks>
/// By PATH, because that is how the game itself distinguishes these and it survives patches
/// far better than a component set or an id. Every keyword below is taken from a source that
/// has been checked against the running game rather than guessed:
///
/// - "transition" is the AHK tool's rule, deliberately BROADER than "areatransition". Exits
///   are not all under an AreaTransitions folder - Lightless Passage's real exit is
///   Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition - and matching only
///   the folder silently loses those, which is exactly the bug that rule was written to fix.
/// - checkpoints under MiscellaneousObjects/Checkpoints, per GameHelper2's own path list.
/// - the league objects are GameHelper2's SpecialMiscObjPaths, trimmed to the ones that mark
///   a place worth walking to.
///
/// Ordered, and the order matters: a transition can live under Metadata/Terrain, so a check
/// on the folder prefix has to come after the keyword or every exit built into terrain is
/// classified as scenery.
/// </remarks>
public static class PointsOfInterest
{
    /// <summary>League and encounter objects that mark a place, by path fragment.</summary>
    private static readonly string[] MechanicPaths =
    [
        "/Expedition",
        "/Ritual",
        "/Abyss",
        "/Sanctum",
        "/Breach",
        "/Delirium/",
        "/Legion",
        "/Blight",
        "/Harvest",
        "/Ultimatum",
    ];

    /// <summary>Classifies a path, or <see cref="PoiKind.None"/> when it marks nothing.</summary>
    public static PoiKind Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Living things and drops are never PLACES, and they would otherwise be caught by the
        // keywords below constantly: a Delirium monster's path carries the league's name, and
        // marking every one of them as an encounter would bury the altar that is the point.
        if (path.StartsWith("Metadata/Monsters/", StringComparison.Ordinal)
            || path.StartsWith("Metadata/Characters/", StringComparison.Ordinal)
            || path.StartsWith("Metadata/Pet", StringComparison.Ordinal)
            || path.Contains("/WorldItem", StringComparison.Ordinal))
        {
            return PoiKind.None;
        }

        if (Has(path, "waypoint"))
        {
            return PoiKind.Waypoint;
        }

        if (Has(path, "checkpoint"))
        {
            return PoiKind.Checkpoint;
        }

        // Before the Terrain and Chest checks: exits are built into both.
        if (Has(path, "transition"))
        {
            return PoiKind.AreaTransition;
        }

        if (Has(path, "shrine"))
        {
            return PoiKind.Shrine;
        }

        if (path.StartsWith("Metadata/Chests/", StringComparison.Ordinal))
        {
            // Chests are thousands per map; only the ones with their own name are landmarks.
            return Has(path, "strongbox") || Has(path, "vault") || Has(path, "chestepic")
                ? PoiKind.Chest
                : PoiKind.None;
        }

        foreach (string fragment in MechanicPaths)
        {
            if (Has(path, fragment))
            {
                return PoiKind.Mechanic;
            }
        }

        return PoiKind.None;
    }

    /// <summary>
    /// A readable name for a point of interest.
    /// </summary>
    /// <remarks>
    /// The path's last segment, tidied: the game's own names are joined words with a folder
    /// prefix and a trailing variant number, and "AreaTransition_Animate_2" tells a reader
    /// less than "Area Transition" does. The kind is what actually matters on a map, so a
    /// name that adds nothing to it is dropped rather than repeated.
    /// </remarks>
    public static string Name(string path, PoiKind kind)
    {
        ArgumentNullException.ThrowIfNull(path);

        int slash = path.LastIndexOf('/');
        string last = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;

        // Trailing variant markers: "_01", "_2", "@70".
        int at = last.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            last = last[..at];
        }

        while (last.Length > 1 && (char.IsDigit(last[^1]) || last[^1] == '_'))
        {
            last = last[..^1];
        }

        string spaced = Space(last);
        return spaced.Length == 0 ? Describe(kind) : spaced;
    }

    /// <summary>The plain name of a kind, for a label with nothing better to say.</summary>
    public static string Describe(PoiKind kind) => kind switch
    {
        PoiKind.AreaTransition => "Area Transition",
        PoiKind.Waypoint => "Waypoint",
        PoiKind.Checkpoint => "Checkpoint",
        PoiKind.Chest => "Chest",
        PoiKind.Mechanic => "Encounter",
        PoiKind.Shrine => "Shrine",
        _ => "Point",
    };

    private static bool Has(string path, string fragment)
        => path.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits JoinedWords into separate ones, leaving runs of capitals alone.</summary>
    private static string Space(string text)
    {
        var built = new System.Text.StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '_')
            {
                built.Append(' ');
                continue;
            }

            bool boundary = i > 0
                            && char.IsUpper(c)
                            && (!char.IsUpper(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1])));

            if (boundary && built.Length > 0 && built[^1] != ' ')
            {
                built.Append(' ');
            }

            built.Append(c);
        }

        return built.ToString().Trim();
    }
}
