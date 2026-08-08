using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.Items;

/// <summary>What one stat is, and how the game words it.</summary>
/// <param name="Id">Its own name - <c>maximum_life</c>. Always known.</param>
/// <param name="Text">
/// The line the game writes, with <c>{0}</c> where the number goes. Empty for the many stats
/// the game never shows.
/// </param>
/// <param name="Argument">Which placeholder this stat fills, for the lines built from two.</param>
public readonly record struct StatMeaning(string Id, string Text, int Argument)
{
    /// <summary>This stat with its value in it, or the raw id when there is no wording.</summary>
    /// <remarks>
    /// FALLS BACK TO THE ID rather than to nothing. An unworded stat is still a fact about the
    /// item, and "maximum_life 79" is a great deal more use than a blank row - especially to
    /// somebody reverse-engineering, which is what this tool is for.
    /// </remarks>
    public string Say(int value)
    {
        if (Text.Length == 0)
        {
            return $"{Id} {value}";
        }

        // Only this stat's own placeholder is filled. A line built from two stats keeps the
        // other's marker, because guessing at it would put this number in the wrong half.
        //
        // THE PLACEHOLDERS CARRY A FORMAT: "{0:+d}" means show the sign, which is how "+79 to
        // maximum Life" gets its plus. Replacing the bare "{0}" alone leaves a thousand of the
        // game's own lines untouched and reading as though the table were missing them.
        string plain = $"{{{Argument}}}";
        if (Text.Contains(plain, StringComparison.Ordinal))
        {
            return Text.Replace(plain, value.ToString(), StringComparison.Ordinal);
        }

        return Text
            .Replace($"{{{Argument}:+d}}", value >= 0 ? $"+{value}" : value.ToString(), StringComparison.Ordinal)
            .Replace($"{{{Argument}:d}}", value.ToString(), StringComparison.Ordinal)
            .Replace($"{{{Argument}:-d}}", value.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>What one mod is called.</summary>
/// <param name="Name">The affix - "of the Bear". Empty when the table has not heard of it.</param>
/// <param name="Kind">"prefix", "suffix" or "unique". Empty when unknown.</param>
public readonly record struct ModMeaning(string Name, string Kind);

/// <summary>
/// Turns the numbers on an item into what the game says about it.
/// </summary>
/// <remarks>
/// An item in memory is a metadata path, a list of mod ids and a list of (stat row, value)
/// pairs. None of that is readable, and all of it is knowledge about a game that changes every
/// league - so it is DATA, extracted from the game's own tables by the AHK tool's scripts and
/// shipped here.
///
/// THE STAT KEY IS A ROW INDEX, not an id. What memory holds is the row number in Stats.dat,
/// which is why this table is keyed by number and why it has 27,000 entries: it has to cover
/// every row, not just the ones an item can carry.
/// </remarks>
public sealed class ItemNames
{
    private readonly IReadOnlyDictionary<int, StatMeaning> _stats;
    private readonly IReadOnlyDictionary<string, ModMeaning> _mods;
    private readonly IReadOnlyDictionary<string, string> _bases;

    private ItemNames(
        IReadOnlyDictionary<int, StatMeaning> stats,
        IReadOnlyDictionary<string, ModMeaning> mods,
        IReadOnlyDictionary<string, string> bases)
    {
        _stats = stats;
        _mods = mods;
        _bases = bases;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static ItemNames Empty { get; } = new(
        new Dictionary<int, StatMeaning>(),
        new Dictionary<string, ModMeaning>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How many stats, mods and base types are described.</summary>
    public (int Stats, int Mods, int Bases) Counts => (_stats.Count, _mods.Count, _bases.Count);

    /// <summary>What a stat row means. Unknown rows keep their number.</summary>
    public StatMeaning Stat(int key)
        => _stats.TryGetValue(key, out StatMeaning found) ? found : new StatMeaning($"stat #{key}", string.Empty, 0);

    /// <summary>What a mod is called. Unknown mods keep their id, which is readable enough.</summary>
    public ModMeaning Mod(string? id)
        => id is { Length: > 0 } && _mods.TryGetValue(id, out ModMeaning found)
            ? found
            : new ModMeaning(string.Empty, string.Empty);

    /// <summary>What an item's base type is called - "Advanced Dualstring Bow".</summary>
    /// <remarks>
    /// Falls back to the last part of the metadata path, which is close to the name and is
    /// always available - a league adding a base type gets an ugly row rather than a blank one.
    /// </remarks>
    public string Base(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return string.Empty;
        }

        if (_bases.TryGetValue(path, out string? known))
        {
            return known;
        }

        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// Loads both files, or returns <see cref="Empty"/> when they are missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. Without them the items still list - with raw paths, mod ids and stat
    /// numbers - which is worse to read and just as true.
    /// </remarks>
    public static ItemNames Load(string? statsPath, string? namesPath)
    {
        var stats = new Dictionary<int, StatMeaning>();
        var mods = new Dictionary<string, ModMeaning>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (statsPath is { Length: > 0 } && File.Exists(statsPath))
            {
                using FileStream stream = File.OpenRead(statsPath);
                ItemStatFile? file = JsonSerializer.Deserialize(stream, ItemNameJsonContext.Default.ItemStatFile);
                foreach ((string key, ItemStatEntry entry) in file?.Stats ?? [])
                {
                    if (int.TryParse(key, out int row))
                    {
                        stats[row] = new StatMeaning(entry.Id ?? string.Empty, entry.Text ?? string.Empty, entry.Argument);
                    }
                }
            }

            if (namesPath is { Length: > 0 } && File.Exists(namesPath))
            {
                using FileStream stream = File.OpenRead(namesPath);
                ItemNameFile? file = JsonSerializer.Deserialize(stream, ItemNameJsonContext.Default.ItemNameFile);
                foreach ((string id, ItemModEntry entry) in file?.Mods ?? [])
                {
                    mods[id] = new ModMeaning(entry.Name ?? string.Empty, entry.Kind ?? string.Empty);
                }

                foreach ((string path, string name) in file?.Bases ?? [])
                {
                    bases[path] = name;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }

        return new ItemNames(stats, mods, bases);
    }
}

/// <summary>The shape of the stat file.</summary>
public sealed class ItemStatFile
{
    [JsonPropertyName("stats")]
    public Dictionary<string, ItemStatEntry>? Stats { get; set; }
}

/// <summary>One stat in it.</summary>
public sealed class ItemStatEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("arg")]
    public int Argument { get; set; }
}

/// <summary>The shape of the name file.</summary>
public sealed class ItemNameFile
{
    [JsonPropertyName("mods")]
    public Dictionary<string, ItemModEntry>? Mods { get; set; }

    [JsonPropertyName("bases")]
    public Dictionary<string, string>? Bases { get; set; }
}

/// <summary>One mod in it.</summary>
public sealed class ItemModEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

/// <summary>Source-generated JSON, so the item tables survive Native AOT.</summary>
[JsonSerializable(typeof(ItemStatFile))]
[JsonSerializable(typeof(ItemNameFile))]
public sealed partial class ItemNameJsonContext : JsonSerializerContext;
