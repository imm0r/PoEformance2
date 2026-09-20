using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>
/// Which boss the game says stands in an area - WorldAreas' own Bosses column.
/// </summary>
/// <remarks>
/// WHAT THIS CORRECTS, AND IT IS THIS PROJECT'S OWN RULE CATCHING IT OUT. BossIcons was written
/// around the finding that nothing in the game joins a boss to a minimap icon, and that is
/// still true - but it grew a second claim that is NOT: that nothing joins a boss to its AREA
/// either, so the pairing had to be typed in by somebody who had stood in the room. WorldAreas
/// carries a Bosses column, a list of MonsterVarieties references, and it answers for 125 of
/// the atlas's 173 maps. "The reference did not have it" was again not the same as "the game
/// does not have it" - the sentence CLAUDE.md is mostly made of.
///
/// WHAT IT BUYS, measured on the 4.5.5.2 export: 104 distinct bosses over those 125 maps, of
/// which 34 stand in more than one of them - Saphira in Grimhaven and in Epitaph, the Baron in
/// both Iron Citadels, one Chimera in Augury and Bluff. A person filling this in by hand meets
/// that trap 34 times and has no way of knowing it until the second map turns up. The 48 maps
/// with nothing listed are exactly the ones with no boss to name: hideouts, hubs, Expedition
/// logbooks, merchant maps, Delirium towns.
///
/// THE FILE IS A FALLBACK AND THE GAME IS THE SOURCE. data/area-bosses.json is generated from a
/// third-party export by scripts/area-bosses.py, so it is one patch away from being wrong about
/// a new league's maps; <see cref="Learn"/> takes the client's own table over it whenever a read
/// reaches one. The same arrangement <see cref="AtlasMapNames.LearnUnique"/> has, and for the
/// same reason: a shipped copy of a column the client owns is somewhere for it to drift.
///
/// IT IS NOT THE LAST WORD EITHER WAY. The column holds what the area designer filed under
/// "bosses", which on two maps is an NPC (Metadata/Monsters/NPC/DogTrader_). data/boss-icons.json
/// is consulted first and overrides it, because a person who has stood in the room beats a
/// column - that is the one thing this does not change.
/// </remarks>
public sealed class AreaBosses
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _file;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _live;
    private int _revision;

    private AreaBosses(IReadOnlyDictionary<string, IReadOnlyList<string>> areas, string source)
    {
        _file = areas;
        _live = areas;
        Source = source;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    /// <remarks>
    /// A FRESH ONE EACH TIME rather than a singleton, for the reason AtlasMapNames.Empty gives:
    /// this can be taught, and a shared instance would carry one client's table into every
    /// other holder of it, tests included.
    /// </remarks>
    public static AreaBosses Empty => new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        string.Empty);

    /// <summary>Where the file says it came from, for the report. Empty when nothing loaded.</summary>
    public string Source { get; private set; }

    /// <summary>How many areas the file named a boss for.</summary>
    public int Count => _file.Count;

    /// <summary>How many areas are named NOW, which the game may have added to.</summary>
    public int Live => Volatile.Read(ref _live).Count;

    /// <summary>Bumped when the game corrects the file, so a cached answer can tell.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.</summary>
    /// <remarks>
    /// Never throws. Without it every arena still draws, with the name the tile gives it - which
    /// is what it did before this existed. See LandmarkNames for the same argument at length.
    /// </remarks>
    public static AreaBosses Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            AreaBossFile? file = JsonSerializer.Deserialize(stream, AreaBossJson.Default.AreaBossFile);
            if (file?.Areas is null)
            {
                return Empty;
            }

            var built = new Dictionary<string, IReadOnlyList<string>>(
                file.Areas.Count, StringComparer.OrdinalIgnoreCase);

            foreach ((string area, string[] paths) in file.Areas)
            {
                if (paths is { Length: > 0 })
                {
                    built[area] = paths;
                }
            }

            return new AreaBosses(built, file.Source ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>The boss paths of an area, best first, or nothing.</summary>
    public IReadOnlyList<string> Of(string? areaId)
        => areaId is { Length: > 0 } && Volatile.Read(ref _live).TryGetValue(areaId, out IReadOnlyList<string>? found)
            ? found
            : [];

    /// <summary>
    /// Takes the game's own Bosses column for every area it lists, and returns how many moved.
    /// </summary>
    /// <remarks>
    /// MERGED RATHER THAN REPLACED, because a read can be partial - the walk may have reached
    /// fifty areas of four hundred - and an area the game has not been asked about yet is not an
    /// area with no boss. What the game DID say wins outright for the areas it covered, including
    /// saying that an area has none: a map whose boss was removed in a patch is exactly the case
    /// the shipped file gets wrong, and it is answered by an empty list rather than by silence.
    /// </remarks>
    /// <param name="areas">What the client's WorldAreas said, by area id. Empty changes nothing.</param>
    public int Learn(IReadOnlyDictionary<string, IReadOnlyList<string>> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);

        if (areas.Count == 0)
        {
            return 0;
        }

        var live = new Dictionary<string, IReadOnlyList<string>>(
            Volatile.Read(ref _live), StringComparer.OrdinalIgnoreCase);

        int moved = 0;
        foreach ((string id, IReadOnlyList<string> paths) in areas)
        {
            bool had = live.TryGetValue(id, out IReadOnlyList<string>? was);
            if (paths.Count == 0)
            {
                moved += had ? 1 : 0;
                live.Remove(id);
                continue;
            }

            if (!had || !Same(was!, paths))
            {
                moved++;
            }

            live[id] = paths;
        }

        Volatile.Write(ref _live, live);
        Interlocked.Increment(ref _revision);
        Source = "WorldAreas.dat, read from the game";
        return moved;
    }

    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var at = 0; at < left.Count; at++)
        {
            if (!string.Equals(left[at], right[at], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The file's shape. See scripts/area-bosses.py, which writes it.</summary>
internal sealed class AreaBossFile
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("areas")]
    public Dictionary<string, string[]>? Areas { get; init; }
}

/// <summary>Source-generated so the file still loads under Native AOT.</summary>
[JsonSerializable(typeof(AreaBossFile))]
internal sealed partial class AreaBossJson : JsonSerializerContext;
