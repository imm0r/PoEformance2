using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What the update check is allowed to do.</summary>
/// <remarks>
/// Two settings, because there are only two questions worth asking. Everything else about this
/// feature - how often, from where, what to compare - is a constant with a reason written next
/// to it rather than a knob nobody can answer better than the code can.
/// </remarks>
public sealed record UpdateSettings
{
    public static readonly UpdateSettings Default = new();

    /// <summary>Whether the tool asks GitHub about newer builds at all.</summary>
    /// <remarks>
    /// ON by default, and it is the only outgoing request this tool makes without being asked.
    /// It is two small requests every six hours to a public API, it carries nothing about the
    /// machine it runs on, and what it buys is the difference between running last month's
    /// offsets against this week's patch and knowing that a build exists. The switch is here
    /// because "no unrequested network traffic" is a legitimate position, not because the
    /// default is in doubt.
    /// </remarks>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>A build the user waved away, so it is not offered again.</summary>
    /// <remarks>
    /// The COMMIT, not a flag. "Not now" is about this build; the next one has to be able to
    /// reach the same person, which a boolean would prevent for good.
    /// </remarks>
    [JsonPropertyName("skipped")]
    public string Skipped { get; init; } = string.Empty;

    /// <summary>The settings with anything unusable brought back into range.</summary>
    public UpdateSettings Normalised() => this with { Skipped = Skipped.Trim() };
}

/// <summary>Loads and saves the update settings next to the executable.</summary>
public static class UpdateSettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "update.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    public static UpdateSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return UpdateSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            UpdateSettings? loaded = JsonSerializer.Deserialize(stream, UpdateJsonContext.Default.UpdateSettings);
            return loaded?.Normalised() ?? UpdateSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return UpdateSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(UpdateSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, UpdateJsonContext.Default.UpdateSettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateSettings))]
public sealed partial class UpdateJsonContext : JsonSerializerContext;
