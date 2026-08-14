using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>
/// What each map is worth running, on somebody's own scale.
/// </summary>
/// <remarks>
/// AN OPINION, not a fact about the game, and the only file here that is. Everything else in
/// <c>data/</c> is extracted from the client or ported from a reference; this is a judgement
/// about which maps are worth the time, and it belongs to whoever is running them. That is
/// exactly why it is a file rather than a table in the code - it will be disagreed with, and
/// disagreeing with it should not need a rebuild.
///
/// WRITTEN BY DISPLAY NAME, RESOLVED TO IDS ONCE. The name is what a person maintaining the
/// file can read; the id is what a node in memory carries. Those are reconciled here, at load,
/// through <see cref="AtlasMapNames"/> - which is OUR table of English names, not the client's
/// translated one. That distinction is the whole reason this is safe: the rule this project
/// keeps hitting is never to MATCH on a string the game translates, and nothing here does. On a
/// German client the ids are the same, our table is the same, and the ratings land the same.
///
/// A name resolves to EVERY id that carries it. Several maps share a name - three different
/// ids are all "Abyssal Depths" - and rating the name means rating all of them, which is what
/// somebody writing one line meant.
///
/// Names that resolve to nothing are KEPT, not dropped, so <see cref="Unmatched"/> can say so.
/// A typo in this file is otherwise a rating that silently never appears.
/// </remarks>
public sealed class AtlasRatings
{
    private readonly IReadOnlyDictionary<string, int> _byId;

    private AtlasRatings(IReadOnlyDictionary<string, int> byId, IReadOnlyList<string> unmatched, int best)
    {
        _byId = byId;
        Unmatched = unmatched;
        Best = best;
    }

    /// <summary>Nothing rated, which is what a missing file leaves.</summary>
    public static AtlasRatings Empty { get; } =
        new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), [], 0);

    /// <summary>How many map ids carry a rating.</summary>
    public int Count => _byId.Count;

    /// <summary>
    /// The highest rating in the file - the top of the scale the colours run to.
    /// </summary>
    /// <remarks>
    /// FROM THE FILE rather than a fixed ten, so any scale works: rate out of five and five is
    /// green, rate out of a hundred and a hundred is. The cost is that this is a RELATIVE
    /// scale - adding one map rated twenty re-colours every other map - and that is the right
    /// way round for an opinion, where "the best there is" is what green should mean.
    /// </remarks>
    public int Best { get; }

    /// <summary>Names in the file that match no map. A typo, or a map that has been renamed.</summary>
    public IReadOnlyList<string> Unmatched { get; }

    /// <summary>What a map is rated, or null when nobody has said.</summary>
    public int? Of(string? mapId)
        => mapId is { Length: > 0 } && _byId.TryGetValue(mapId, out int rating) ? rating : null;

    /// <summary>
    /// Loads the file and resolves its names, or returns <see cref="Empty"/> when it cannot.
    /// </summary>
    /// <remarks>
    /// Never throws, like every other data file here: an atlas without ratings is worth having,
    /// and a tool that will not start because somebody typed a comma is not.
    /// </remarks>
    public static AtlasRatings Load(string? path, AtlasMapNames names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            AtlasRatingFile? file = JsonSerializer.Deserialize(
                stream, AtlasRatingJsonContext.Default.AtlasRatingFile);

            return file?.Maps is { Count: > 0 } wanted ? Resolve(wanted, names) : Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>Turns a table of names into one of ids, and says what did not resolve.</summary>
    public static AtlasRatings Resolve(IReadOnlyDictionary<string, int> byName, AtlasMapNames names)
    {
        ArgumentNullException.ThrowIfNull(byName);
        ArgumentNullException.ThrowIfNull(names);

        // Every id that carries each name, built once - the alternative is a scan of the whole
        // table per rating, and this runs at startup where neither would be felt but only one
        // reads as deliberate.
        var idsByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string mapId, AtlasMapInfo info) in names.All)
        {
            if (info.Name.Length == 0)
            {
                continue;
            }

            if (!idsByName.TryGetValue(info.Name, out List<string>? ids))
            {
                ids = [];
                idsByName[info.Name] = ids;
            }

            ids.Add(mapId);
        }

        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();
        int best = 0;

        foreach ((string name, int rating) in byName)
        {
            if (!idsByName.TryGetValue(name.Trim(), out List<string>? ids))
            {
                unmatched.Add(name);
                continue;
            }

            foreach (string mapId in ids)
            {
                byId[mapId] = rating;
            }

            best = Math.Max(best, rating);
        }

        unmatched.Sort(StringComparer.OrdinalIgnoreCase);
        return new AtlasRatings(byId, unmatched, best);
    }
}

/// <summary>The shape of the file on disk: a flat name-to-rating table under one key.</summary>
/// <remarks>
/// Under a key rather than at the root, so the file has somewhere to grow a note or a scale
/// without every reader of it needing to change.
/// </remarks>
public sealed class AtlasRatingFile
{
    [JsonPropertyName("maps")]
    public Dictionary<string, int>? Maps { get; set; }
}

/// <summary>Source-generated JSON, so the ratings survive Native AOT.</summary>
[JsonSerializable(typeof(AtlasRatingFile))]
public sealed partial class AtlasRatingJsonContext : JsonSerializerContext;
