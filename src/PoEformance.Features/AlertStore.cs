using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>The alert settings as they are written down.</summary>
/// <remarks>
/// The rules travel WITH the switches rather than in a file of their own, because they are
/// one decision: "watch for these things, and be quiet in town". Splitting them would put half
/// a person's configuration in each of two files.
/// </remarks>
public sealed record AlertSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("quietInTown")] bool QuietInTown = true,
    [property: JsonPropertyName("rules")] IReadOnlyList<AlertRule>? Rules = null)
{
    public static AlertSettings Default { get; } = new();

    /// <summary>
    /// The rules to watch for - the shipped ones when the file said nothing.
    /// </summary>
    /// <remarks>
    /// An ABSENT list means "never chosen", and takes the defaults. An EMPTY one means
    /// "watch for nothing", and is honoured: deleting every rule has to be a thing somebody
    /// can do, and a list that grows its defaults back is a list that cannot be emptied.
    /// </remarks>
    public IReadOnlyList<AlertRule> Watching => Rules ?? DefaultAlerts.Rules;
}

/// <summary>Loads and saves the alert settings next to the executable.</summary>
public static class AlertStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "alerts.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// Rules that say nothing are dropped on the way in. One would match every entity in the
    /// area, which is the single worst thing this feature can do - and a hand-edited file, or
    /// one written by a version that had a field this one does not, is exactly where an empty
    /// rule comes from.
    /// </remarks>
    public static AlertSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return AlertSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            AlertSettings? loaded = JsonSerializer.Deserialize(stream, AlertJsonContext.Default.AlertSettings);
            if (loaded is null)
            {
                return AlertSettings.Default;
            }

            return loaded.Rules is null
                ? loaded
                : loaded with { Rules = [.. loaded.Rules.Where(rule => rule is not null && !rule.SaysNothing)] };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return AlertSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(AlertSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, AlertJsonContext.Default.AlertSettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the alerts survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AlertSettings))]
public sealed partial class AlertJsonContext : JsonSerializerContext;
