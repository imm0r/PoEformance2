using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>What is known about one map on the atlas, beyond what the node itself says.</summary>
/// <param name="Name">The name the game shows for it, in English.</param>
/// <param name="Unique">Whether it is a unique map rather than one of the ordinary ones.</param>
/// <param name="Tags">
/// What KIND of map it is - "tower", "arbiter", "expedition", "lineage", "breach", "ritual",
/// "boss", "traverse", "quest". This is what grouping is built on, and it comes from the game's
/// own data rather than from somebody's list of names.
/// </param>
public sealed record AtlasMapInfo(string Name, bool Unique, IReadOnlyList<string> Tags)
{
    /// <summary>Nothing known about a map - what an unlisted id gets.</summary>
    public static AtlasMapInfo Unknown { get; } = new(string.Empty, false, []);

    /// <summary>Whether one of its tags is this one, however it was capitalised.</summary>
    public bool Tagged(string tag)
    {
        foreach (string mine in Tags)
        {
            if (string.Equals(mine, tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// What each map on the atlas is called and what kind of thing it is.
/// </summary>
/// <remarks>
/// A node in memory carries its id - <c>MapLostTowers</c> - and nothing else worth reading to
/// somebody. This turns that into "Lost Towers", and into the fact that it is a tower, which is
/// the whole basis of grouping the atlas by what its maps are FOR.
///
/// KEYED BY THE INTERNAL ID, which matters more than it looks. The reference groups its maps by
/// DISPLAYED name - "The Copper Citadel" - and that is the one string the game translates, so
/// on a German client every one of those groups matches nothing. The id is the same on every
/// client, so the grouping here holds whatever language the game is in.
///
/// Ported from GameHelper2's WorldAreaTags.json, which is extracted from WorldAreas.dat: 176
/// atlas areas, 23 of them unique, 57 carrying a tag. The bigger WorldAreaNames.tsv beside it
/// adds 264 campaign zones and hubs that cannot appear on an atlas, so this is deliberately the
/// smaller of the two files.
///
/// THE NAMES ARE ENGLISH, always, because that is what the table holds. On a German client the
/// atlas will therefore be labelled in English while the game beneath it is not - a real
/// limitation, and the reason to look for the name on the node itself later. Being keyed by id
/// is what makes that a change of one method rather than of every group.
/// </remarks>
public sealed class AtlasMapNames
{
    private readonly IReadOnlyDictionary<string, AtlasMapInfo> _maps;

    private AtlasMapNames(IReadOnlyDictionary<string, AtlasMapInfo> maps) => _maps = maps;

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static AtlasMapNames Empty { get; } = new(new Dictionary<string, AtlasMapInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many maps are described.</summary>
    public int Count => _maps.Count;

    /// <summary>
    /// Every map, by id. For anything that has to go the other way - name to id.
    /// </summary>
    /// <remarks>
    /// Exposed rather than offering a name lookup here, because a name is not a key: several
    /// ids share one, and the caller has to decide what that means for it. The ratings want all
    /// of them; something else might want the first.
    /// </remarks>
    public IReadOnlyDictionary<string, AtlasMapInfo> All => _maps;

    /// <summary>
    /// What is known about a map id, or <see cref="AtlasMapInfo.Unknown"/> when it is new.
    /// </summary>
    /// <remarks>
    /// Never null. A league adds maps this file has not heard of, and the answer to that is a
    /// node drawn with its raw id rather than a node missing from the atlas.
    /// </remarks>
    public AtlasMapInfo Of(string? mapId)
        => mapId is { Length: > 0 } && _maps.TryGetValue(mapId, out AtlasMapInfo? found) ? found : AtlasMapInfo.Unknown;

    /// <summary>The name to show for a map: the known one, or its raw id when it is new.</summary>
    /// <remarks>
    /// Falling back to the id rather than to nothing is the point. An unrecognised map still
    /// has contents worth seeing and a route worth drawing, and "MapSomethingNew" on the
    /// screen is also how somebody notices this file needs a line adding to it.
    /// </remarks>
    public string Called(string? mapId)
    {
        AtlasMapInfo info = Of(mapId);
        return info.Name.Length > 0 ? info.Name : mapId ?? string.Empty;
    }

    /// <summary>
    /// Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws, for the same reason as its siblings: an atlas labelled with raw map ids
    /// is worth having, and a tool that will not start because a comma moved is not.
    /// </remarks>
    public static AtlasMapNames Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            AtlasMapFile? file = JsonSerializer.Deserialize(stream, AtlasMapJsonContext.Default.AtlasMapFile);
            if (file?.Maps is null)
            {
                return Empty;
            }

            var built = new Dictionary<string, AtlasMapInfo>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, AtlasMapEntry entry) in file.Maps)
            {
                built[id] = new AtlasMapInfo(
                    entry.Name ?? string.Empty,
                    string.Equals(entry.Type, "unique", StringComparison.OrdinalIgnoreCase),
                    entry.Tags ?? []);
            }

            return new AtlasMapNames(built);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }
}

/// <summary>The shape of the file on disk.</summary>
public sealed class AtlasMapFile
{
    [JsonPropertyName("maps")]
    public Dictionary<string, AtlasMapEntry>? Maps { get; set; }
}

/// <summary>One map in it. Type and tags are left out when there is nothing to say.</summary>
public sealed class AtlasMapEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>Source-generated JSON, so the map names survive Native AOT.</summary>
[JsonSerializable(typeof(AtlasMapFile))]
public sealed partial class AtlasMapJsonContext : JsonSerializerContext;
