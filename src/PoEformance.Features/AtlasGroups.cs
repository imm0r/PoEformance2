using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// One kind of map worth telling apart from the rest of the atlas.
/// </summary>
/// <remarks>
/// MATCHED THREE WAYS, and it needs all three. The game's own data tags some maps - "tower",
/// "arbiter", "expedition" - and where a tag says what somebody means, the tag is the better
/// rule: it keeps working when a league adds a map to that kind. But the tags do not always say
/// what somebody means. The maps players call towers are Bluff, Mesa, Sinking Spire, Alpine
/// Ridge and Lost Towers; only the last of those carries the <c>tower</c> tag, while the tag
/// also covers the six Precursor Towers, which are a different thing entirely. So a group can
/// also name its maps outright, and the shipped ones use whichever is faithful.
///
/// BY ID, NEVER BY DISPLAYED NAME. The reference matches "The Copper Citadel", which is the one
/// string the game translates - so on a German client its groups match nothing at all. The ids
/// are the same everywhere.
///
/// FIRST MATCH WINS, so the order of the list is a decision rather than an accident: a citadel
/// is also a unique map, and it is worth seeing as a citadel.
/// </remarks>
/// <param name="Name">What to call this kind, in the settings and on a route's label.</param>
/// <param name="Colour"><c>#RRGGBB</c> or <c>#AARRGGBB</c>, as everywhere else in the settings.</param>
/// <param name="Tag">A tag from the game's data. Empty for a group that names its maps instead.</param>
/// <param name="Maps">Map ids belonging to this group, whatever their tags say.</param>
/// <param name="Unique">Match every unique map. The catch-all the ordinary maps fall past.</param>
/// <param name="Route">Draw the way there from wherever you can start now.</param>
/// <param name="MaxHops">
/// How far away is still worth routing to. Zero means no limit; a route across half an atlas is
/// drawn as readily as one next door, which is a mess rather than a help on a full atlas.
/// </param>
/// <param name="Enabled">Off keeps the group without acting on it. Deleting is the other way.</param>
public sealed record AtlasGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("colour")] string Colour,
    [property: JsonPropertyName("tag")] string Tag = "",
    [property: JsonPropertyName("maps")] IReadOnlyList<string>? Maps = null,
    [property: JsonPropertyName("unique")] bool Unique = false,
    [property: JsonPropertyName("route")] bool Route = false,
    [property: JsonPropertyName("maxHops")] int MaxHops = 0,
    [property: JsonPropertyName("enabled")] bool Enabled = true)
{
    /// <summary>
    /// Whether this group says anything at all about which maps belong to it.
    /// </summary>
    /// <remarks>
    /// A group with no tag, no maps and not the unique catch-all matches nothing - which is
    /// harmless, and worth naming so the settings window can say so instead of showing a group
    /// that quietly never appears.
    /// </remarks>
    public bool SaysNothing => !Unique && string.IsNullOrWhiteSpace(Tag) && (Maps is null || Maps.Count == 0);

    /// <summary>Whether a map belongs to this group.</summary>
    public bool Holds(string mapId, AtlasMapInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!Enabled || string.IsNullOrEmpty(mapId))
        {
            return false;
        }

        if (Maps is not null)
        {
            foreach (string mine in Maps)
            {
                if (string.Equals(mine, mapId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (Tag.Length > 0 && info.Tagged(Tag))
        {
            return true;
        }

        return Unique && info.Unique;
    }
}

/// <summary>
/// The groups a fresh install starts with.
/// </summary>
/// <remarks>
/// The kinds of map somebody actually goes looking for, in the order they should win in. The
/// citadels first because they are the point of an atlas; the unique catch-all last because it
/// would otherwise swallow half the named groups above it.
///
/// NONE OF THEM ROUTE BY DEFAULT. Ten sets of lines across an atlas is not ten times the help -
/// turning one on is what makes it say anything.
/// </remarks>
public static class DefaultAtlasGroups
{
    public static IReadOnlyList<AtlasGroup> Groups { get; } =
    [
        // By tag: the game marks these itself, and marks them right.
        new("Citadels", "#FF4040", Tag: "arbiter"),
        new("Lineage", "#00C853", Tag: "lineage"),
        new("Breach", "#FF33BD", Tag: "breach"),
        new("Expedition", "#5BC1ED", Tag: "expedition"),
        new("Quest", "#00E5E5", Tag: "quest"),

        // By name: the tag exists and means something else. "Tower" in the data covers the six
        // Precursor Towers as well; a tower to run for is one of these five.
        new("Towers", "#DB00E0", Maps: ["MapBluff", "MapMesa", "MapSwampTower", "MapAlpineRidge", "MapLostTowers"]),

        // The way through the endgame: the gateways, the monolith, the origin tower and the
        // chambers. Part tagged "traverse", part not tagged at all, so it is listed.
        new("Progression", "#B4651A", Maps:
        [
            "MapUniqueReactor_01", "MapUniqueReactor_02", "MapUniqueReactor_03",
            "MapUberBoss_Monolith", "MapUberBoss_Divinity",
            "MapUberBoss_IronCitadel_Quest", "MapUberBoss_StoneCitadel_Quest",
        ]),

        // League hubs reached from the atlas rather than from a map device.
        new("Ritual", "#4000F4", Maps: ["MapUberBoss_Ritual", "RitualLeagueBoss"]),
        new("Abyss", "#26FF00", Maps: ["Abyss_Hub", "Abyss_Pinnacle"]),
        new("Temple", "#DEA700", Maps: ["IncursionHub", "IncursionHubEndgame"]),

        // Everything else the game calls unique. Last, so the named groups above keep theirs.
        new("Unique maps", "#FF8F00", Unique: true),
    ];
}

/// <summary>
/// Which group a map belongs to, and what a route to it should look like.
/// </summary>
/// <remarks>
/// Built once per read rather than asked per node per frame: the answer for a map id cannot
/// change while the atlas is open, and an atlas is a few hundred nodes drawn sixty times a
/// second.
/// </remarks>
public sealed class AtlasGrouping
{
    private readonly IReadOnlyList<AtlasGroup> _groups;
    private readonly AtlasMapNames _names;
    private readonly Dictionary<string, AtlasGroup?> _decided = new(StringComparer.OrdinalIgnoreCase);

    private readonly AtlasRatings _ratings;

    public AtlasGrouping(IReadOnlyList<AtlasGroup> groups, AtlasMapNames names, AtlasRatings? ratings = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(names);
        _groups = groups;
        _names = names;
        _ratings = ratings ?? AtlasRatings.Empty;
    }

    /// <summary>Nothing grouped, which is what an atlas with no settings gets.</summary>
    public static AtlasGrouping None { get; } = new([], AtlasMapNames.Empty);

    /// <summary>What is known about a map - its name, and whether it is unique.</summary>
    public AtlasMapInfo About(string mapId) => _names.Of(mapId);

    /// <summary>The name to show for a map, falling back to its raw id.</summary>
    public string Called(string mapId) => _names.Called(mapId);

    /// <summary>What a map is rated, or null when nobody has said.</summary>
    public int? Rated(string mapId) => _ratings.Of(mapId);

    /// <summary>The top of the rating scale, which is what the colours run to.</summary>
    public int BestRating => _ratings.Best;

    /// <summary>The group a map belongs to, or null when it belongs to none.</summary>
    public AtlasGroup? Of(string mapId)
    {
        if (string.IsNullOrEmpty(mapId))
        {
            return null;
        }

        if (_decided.TryGetValue(mapId, out AtlasGroup? remembered))
        {
            return remembered;
        }

        AtlasMapInfo info = _names.Of(mapId);
        AtlasGroup? found = null;
        foreach (AtlasGroup group in _groups)
        {
            if (group.Holds(mapId, info))
            {
                found = group;
                break;
            }
        }

        _decided[mapId] = found;
        return found;
    }

    /// <summary>
    /// Whether a route should be drawn to a map, and how far away it may be.
    /// </summary>
    /// <remarks>
    /// A map already finished is not somewhere to be sent, however interesting its group is,
    /// so the state is part of the question rather than a check the caller has to remember.
    /// </remarks>
    public bool RouteTo(string mapId, AtlasNodeState state, out AtlasGroup? group)
    {
        group = Of(mapId);
        return group is { Route: true } && state != AtlasNodeState.Completed;
    }
}

/// <summary>What the atlas overlay does, and how it sorts the maps on it.</summary>
/// <param name="Enabled">Whether any of it is drawn. Off costs one read of a pointer.</param>
/// <param name="Names">Label each map with its name.</param>
/// <param name="Contents">List what the game says is in it, under the name.</param>
/// <param name="HideCompleted">Leave out the maps already run, which is most of a played atlas.</param>
/// <param name="HideUnreachable">
/// Leave out the maps with no way to them yet. Off by default: those are the ones a route is
/// FOR, and hiding them by default would hide the feature.
/// </param>
/// <param name="Web">Draw every connection, not just the routes. Honest and very busy.</param>
/// <param name="HideOnHover">
/// Draw nothing while the cursor is on a map. The game puts its own panel over the node then -
/// what the map is, its biome, what is in it - and everything here would be drawn across it.
/// </param>
/// <param name="Search">Show only the maps whose name contains this. Empty shows all of them.</param>
/// <param name="TextScale">
/// How big the writing on the atlas is, against the interface's own size. 1 is that size.
/// </param>
/// <param name="RitualWorth">
/// What each ritual reward is worth to this player, which is what orders the planned routes.
/// Only the ones somebody actually set are kept.
/// </param>
/// <param name="Groups">
/// ABSENT means "never chosen" and takes the shipped list; EMPTY means "group nothing" and is
/// honoured - the same rule the preload rules follow, for the same reason.
/// </param>
public sealed record AtlasSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("names")] bool Names = true,
    [property: JsonPropertyName("contents")] bool Contents = true,
    [property: JsonPropertyName("hideCompleted")] bool HideCompleted = true,
    [property: JsonPropertyName("hideUnreachable")] bool HideUnreachable = false,
    [property: JsonPropertyName("web")] bool Web = false,
    [property: JsonPropertyName("hideOnHover")] bool HideOnHover = true,
    [property: JsonPropertyName("ratings")] bool Ratings = true,
    [property: JsonPropertyName("search")] string Search = "",
    [property: JsonPropertyName("textScale")] float TextScale = 0f,
    [property: JsonPropertyName("ritualWorth")] IReadOnlyDictionary<string, int>? RitualWorth = null,
    [property: JsonPropertyName("groups")] IReadOnlyList<AtlasGroup>? Groups = null)
{
    /// <summary>The smallest and largest writing worth allowing.</summary>
    /// <remarks>
    /// A range rather than a free number because this reaches drawing code through a settings
    /// file: zero would draw nothing at all and look like a broken atlas, and a hundred would
    /// fill the screen with one map's name and leave nowhere to put it back.
    /// </remarks>
    public const float SmallestText = 0.5f;

    public const float LargestText = 4f;

    public static AtlasSettings Default { get; } = new();

    /// <summary>
    /// How big to write, with zero meaning "as the interface does".
    /// </summary>
    /// <remarks>
    /// ZERO is the unset value, not a size, for the reason every zero in these settings is:
    /// a record's default, a missing JSON key and a hand-edited file all arrive as zero without
    /// going through a constructor, and a zero taken literally is an atlas with no writing on
    /// it. The atlas is a whole screen of small text and a 4K monitor makes it smaller, so this
    /// exists to be turned up without waiting for a build.
    /// </remarks>
    public float Writing => TextScale <= 0f ? 1f : Math.Clamp(TextScale, SmallestText, LargestText);

    /// <summary>The groups in force - the shipped ones when the file said nothing.</summary>
    public IReadOnlyList<AtlasGroup> Sorting => Groups ?? DefaultAtlasGroups.Groups;

    /// <summary>
    /// What the ritual rewards are worth. Empty until somebody says otherwise.
    /// </summary>
    /// <remarks>
    /// No shipped defaults, unlike the groups: what a reward is worth depends entirely on what
    /// this player needs, and a shipped opinion would sort everybody's routes by mine.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Worth => RitualWorth ?? NothingInParticular;

    private static readonly Dictionary<string, int> NothingInParticular = [];
}

/// <summary>Loads and saves the atlas settings next to the executable.</summary>
/// <remarks>
/// Its own file, like the preload rules: the groups are a long list somebody edits by hand when
/// a league renames half of them, and burying that inside a general settings file makes it
/// harder to find and riskier to correct.
/// </remarks>
public static class AtlasStore
{
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "atlas.json");

    /// <summary>Reads the settings, or the shipped ones when there is no file.</summary>
    /// <remarks>Never throws - a broken file leaves the defaults, exactly like its siblings.</remarks>
    public static AtlasSettings Load(string? path = null)
    {
        string from = path ?? DefaultPath;
        try
        {
            if (!File.Exists(from))
            {
                return AtlasSettings.Default;
            }

            using FileStream stream = File.OpenRead(from);
            return JsonSerializer.Deserialize(stream, AtlasJsonContext.Default.AtlasSettings)
                ?? AtlasSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return AtlasSettings.Default;
        }
    }

    /// <summary>Writes the settings. Returns false rather than throwing into a render loop.</summary>
    public static bool Save(AtlasSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string to = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, JsonSerializer.Serialize(settings, AtlasJsonContext.Default.AtlasSettings));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the atlas settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AtlasSettings))]
public sealed partial class AtlasJsonContext : JsonSerializerContext;
