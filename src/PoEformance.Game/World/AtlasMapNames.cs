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
/// WHAT IT IS NOW, AND WHAT IT WAS. It is the 173 maps EndgameMaps.dat says the atlas can actually
/// hold. It used to be 440 entries, ported from GameHelper2 under a name that made a claim the data
/// did not support: that port is WorldAreas.dat with tags added, and WorldAreas is EVERY AREA THE
/// GAME HAS - 83 personal hideouts, 123 campaign zones, 16 Sanctum floors, the login screen, the
/// character-select screen, a row called NULL, and a row called "Atlas", which is the atlas itself.
/// Two hundred and sixty-seven of the 440 were places no atlas node can point at. They are gone.
///
/// NOTHING IN WorldAreas COULD HAVE SEPARATED THEM, which is why this took a second table rather
/// than a filter: measured against 106 ids read off real atlas nodes, the Map* prefix misses every
/// Expedition logbook and the game's own "map" tag misses sixteen of them. See EndgameMapCatalogue.
///
/// WHAT IS LEFT IS WHAT MEMORY DOES NOT SUPPLY: the English name, which the ratings resolve
/// through (the game's own names are translated, so they cannot be that lookup), and the 53
/// entries carrying curated words - expedition, arbiter, tower, lineage, traverse, breach, quest,
/// boss, hideout, craft, ritual - which share no vocabulary with the game's mechanical tags.
///
/// THE NAMES ARE ENGLISH, always, because that is what the table holds. On a German client the
/// atlas will therefore be labelled in English while the game beneath it is not - a real
/// limitation, and the reason to look for the name on the node itself later. Being keyed by id
/// is what makes that a change of one method rather than of every group.
///
/// THE UNIQUE FLAG NO LONGER COMES FROM HERE once the game has been asked. WorldAreas.dat carries
/// IsUniqueMapArea, it was measured against all 442 rows, and it disagrees with this file on six
/// ids in both directions - so the game's column is the one in force and this file's is kept only
/// to be compared against. See <see cref="LearnUnique"/>, and MapDataReport for the comparison.
/// </remarks>
public sealed class AtlasMapNames
{
    /// <summary>What the file says. Never changes, so the report can still show it.</summary>
    private readonly IReadOnlyDictionary<string, AtlasMapInfo> _file;

    /// <summary>What is IN FORCE. The same object as the file until the game corrects it.</summary>
    private IReadOnlyDictionary<string, AtlasMapInfo> _live;

    private int _revision;

    private AtlasMapNames(IReadOnlyDictionary<string, AtlasMapInfo> maps)
    {
        _file = maps;
        _live = maps;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    /// <remarks>
    /// A FRESH ONE EACH TIME, not a singleton, and that stopped being a detail the moment
    /// <see cref="LearnUnique"/> existed: a shared instance that anything can teach is one that
    /// carries a client's table into every other holder of it, tests included. Nothing compares
    /// these by reference, and the allocation is an empty dictionary on a construction path.
    /// </remarks>
    public static AtlasMapNames Empty => new(new Dictionary<string, AtlasMapInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many maps the FILE describes.</summary>
    public int Count => _file.Count;

    /// <summary>
    /// Every map the FILE lists, by id. For anything that has to go the other way - name to id.
    /// </summary>
    /// <remarks>
    /// Exposed rather than offering a name lookup here, because a name is not a key: several
    /// ids share one, and the caller has to decide what that means for it. The ratings want all
    /// of them; something else might want the first.
    ///
    /// THE FILE, deliberately, and not what <see cref="Of"/> answers. The two part company once
    /// the game has spoken, and the callers of this one - the ratings' name lookup, and the report
    /// that compares the two sources - both want the file's word specifically. A reconciliation
    /// built on a table that had already been corrected would report that everything agrees.
    /// </remarks>
    public IReadOnlyDictionary<string, AtlasMapInfo> All => _file;

    /// <summary>
    /// How many times the game has corrected this file. Nought means the file alone is in force.
    /// </summary>
    /// <remarks>
    /// A version rather than a flag, so anything CACHING an answer from <see cref="Of"/> can tell
    /// that its cache is stale without being told. <see cref="AtlasGrouping"/> is the one that
    /// needs it: it decides a map's group once per id and would otherwise keep a decision taken
    /// before the game was asked, for the whole session.
    /// </remarks>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// What is known about a map id, or <see cref="AtlasMapInfo.Unknown"/> when it is new.
    /// </summary>
    /// <remarks>
    /// Never null. A league adds maps this file has not heard of, and the answer to that is a
    /// node drawn with its raw id rather than a node missing from the atlas.
    ///
    /// ONE LOOKUP, not two, which is why <see cref="LearnUnique"/> merges up front rather than
    /// consulting the game's table here: this is asked for every node of a few-hundred-node atlas
    /// on every read, and the merge happens once in a session.
    /// </remarks>
    public AtlasMapInfo Of(string? mapId)
        => mapId is { Length: > 0 } && Volatile.Read(ref _live).TryGetValue(mapId, out AtlasMapInfo? found)
            ? found
            : AtlasMapInfo.Unknown;

    /// <summary>
    /// Takes the game's own IsUniqueMapArea for every area it lists, and returns how many maps
    /// that moved.
    /// </summary>
    /// <remarks>
    /// WHY THE GAME WINS. Both columns were read over the whole 442-row table and they disagree on
    /// six ids in both directions: the game calls ExpeditionLeagueBoss, RitualLeagueBoss,
    /// MapVoidReliquary and Map_HildaCampsite unique where the file does not, and calls
    /// MapUniqueInitialTower and MapUniqueReactor_04 ordinary where the file says unique. The file
    /// is a hand-maintained port; the column is what the client itself decides with. There is no
    /// version of this where the hand-maintained copy is the better source.
    ///
    /// NAMES AND TAGS ARE LEFT ALONE. The names in the table are in the CLIENT'S language and this
    /// file's are English on purpose - the ratings resolve through them - and the tags share no
    /// vocabulary with the curated words at all. Only the flag that was measured to be better moves.
    ///
    /// AN AREA THE FILE HAS NEVER HEARD OF still gets an entry, with no name and no tags, so that
    /// a new league's unique map falls into the unique group instead of past it. Its name stays
    /// empty, so <see cref="Called"/> goes on showing the raw id and it is still visible as new.
    /// </remarks>
    /// <param name="areas">WorldAreas as read from memory, or empty when it has not been.</param>
    public int LearnUnique(IReadOnlyDictionary<string, WorldArea> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);

        if (areas.Count == 0)
        {
            return 0;
        }

        var live = new Dictionary<string, AtlasMapInfo>(_file.Count, StringComparer.OrdinalIgnoreCase);
        int moved = 0;
        foreach ((string id, AtlasMapInfo info) in _file)
        {
            bool unique = areas.TryGetValue(id, out WorldArea? area) ? area.IsUnique : info.Unique;
            live[id] = unique == info.Unique ? info : info with { Unique = unique };
            moved += unique == info.Unique ? 0 : 1;
        }

        foreach ((string id, WorldArea area) in areas)
        {
            if (area.IsUnique && !live.ContainsKey(id))
            {
                live[id] = new AtlasMapInfo(string.Empty, true, []);
                moved++;
            }
        }

        // Published even when nothing moved: the revision is what says the game HAS been asked,
        // and a client where the two happen to agree is not a client where nothing was learned.
        Volatile.Write(ref _live, live);
        Interlocked.Increment(ref _revision);
        return moved;
    }

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
