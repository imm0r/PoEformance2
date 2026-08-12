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

    /// <summary>
    /// A named character standing somewhere - which in a campaign zone is usually the goal.
    /// </summary>
    Npc,

    /// <summary>What a quest wants, as the game's own map marking says so.</summary>
    Quest,

    /// <summary>
    /// A boss arena, found in the SHAPE of the ground rather than among the entities.
    /// </summary>
    /// <remarks>
    /// An endgame map is generated at random; the boss room is not. See TerrainLandmarks.
    /// </remarks>
    BossArena,

    /// <summary>
    /// The game marks it, but with an icon this does not recognise.
    /// </summary>
    /// <remarks>
    /// Kept rather than discarded: the icon names run to dozens and change every league, so
    /// an unrecognised one means "not classified", never "not important". The icon's own name
    /// becomes the label, which is usually more informative than a kind would be anyway.
    /// </remarks>
    Marked,
}

/// <summary>
/// Recognises points of interest - from the game's own map marking where there is one.
/// </summary>
/// <remarks>
/// THE GAME ALREADY KNOWS. An entity it puts on its own minimap carries a MinimapIcon
/// component naming the icon it uses ("Waypoint", "QuestObject", "RewardChestExpedition"),
/// which is the authoritative answer to "is this worth marking" and comes with the game's own
/// category attached. Quest objectives in particular are impossible to recognise from a path -
/// a lever is a lever - and trivial from this.
///
/// The path rules below are the FALLBACK, for the things the game does not icon. They are
/// still worth having, and every keyword is taken from a source checked against the running
/// game rather than guessed:
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

    /// <summary>
    /// Classifies an entity, preferring the game's own map icon over its path.
    /// </summary>
    /// <param name="mapIcon">
    /// The icon name from the entity's MinimapIcon component, empty when it has none. When
    /// present it decides, because it is the game saying so rather than this guessing.
    /// </param>
    public static PoiKind Classify(string path, string mapIcon)
    {
        ArgumentNullException.ThrowIfNull(mapIcon);

        if (mapIcon.Length > 0)
        {
            PoiKind fromIcon = FromIcon(mapIcon);
            if (fromIcon != PoiKind.None)
            {
                return fromIcon;
            }
        }

        return Classify(path);
    }

    /// <summary>
    /// Reads the game's own icon name as a kind.
    /// </summary>
    /// <remarks>
    /// Matched loosely on purpose. The names come from MinimapIcons.dat and there are dozens
    /// of them, most naming a specific encounter's reward chest; enumerating them would be a
    /// list that goes stale every league. What matters is the handful of BEHAVIOURS a marker
    /// can have, and an unrecognised icon still counts as a place - the game marked it, so
    /// there is something there - which is what the Marked kind is for.
    /// </remarks>
    private static PoiKind FromIcon(string icon)
    {
        if (Has(icon, "waypoint"))
        {
            return PoiKind.Waypoint;
        }

        if (Has(icon, "checkpoint"))
        {
            return PoiKind.Checkpoint;
        }

        if (Has(icon, "transition") || Has(icon, "portal") || Has(icon, "entrance") || Has(icon, "exit"))
        {
            return PoiKind.AreaTransition;
        }

        if (Has(icon, "quest") || Has(icon, "objective"))
        {
            return PoiKind.Quest;
        }

        if (Has(icon, "npc") || Has(icon, "vendor") || Has(icon, "master"))
        {
            return PoiKind.Npc;
        }

        if (Has(icon, "shrine"))
        {
            return PoiKind.Shrine;
        }

        if (Has(icon, "chest") || Has(icon, "strongbox"))
        {
            return PoiKind.Chest;
        }

        return PoiKind.Marked;
    }

    /// <summary>Classifies by path alone, for entities the game does not mark itself.</summary>
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

        // A place to walk to, and in campaign content usually THE place: what a quest wants
        // is most often an exit or a person. Towns are full of them, which would drown the
        // list - but the overlay is off in towns, so the ones left are the ones that matter.
        if (path.StartsWith("Metadata/NPC", StringComparison.Ordinal))
        {
            return PoiKind.Npc;
        }

        if (path.StartsWith("Metadata/Chests/", StringComparison.Ordinal))
        {
            // Chests are thousands per map; only the ones with their own name are landmarks.
            //
            // "league" and "encounter" are here because a league mechanic's chest is the
            // reward for the mechanic, and the game does not always mark it: the Vaal chest
            // of an Incursion (Metadata/Chests/LeagueIncursion/EncounterChest) carries no
            // MinimapIcon component at all, so nothing else in this file could have caught
            // it, and it was missing from the places list while forty abyss cracks were in it.
            // Both words are the game's own folder convention rather than a league's name -
            // LeagueIncursion, LeagueAbyss, IncursionPedestalEncounter all appear in one
            // recording - so a new league arrives already matched. The junk stays out: every
            // pot and passage chest in the same recording lives under DryRuinPots or straight
            // under Chests/, and carries neither word.
            return Has(path, "strongbox") || Has(path, "vault") || Has(path, "chestepic")
                || Has(path, "league") || Has(path, "encounter")
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

    /// <summary>Turns the game's own icon name into something readable.</summary>
    /// <remarks>
    /// The names are joined words, as everything in the game's data is. Nothing else is done
    /// to them: this is what the GAME calls the marker, and rewording it would be replacing
    /// information with an opinion.
    /// </remarks>
    public static string Readable(string iconName)
    {
        ArgumentNullException.ThrowIfNull(iconName);
        return Space(iconName);
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
        PoiKind.Npc => "NPC",
        PoiKind.Quest => "Quest",
        PoiKind.BossArena => "Boss Arena",
        PoiKind.Marked => "Marked",
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
