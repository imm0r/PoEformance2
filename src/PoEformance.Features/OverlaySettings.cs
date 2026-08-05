using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>What the user decides about the overlay.</summary>
/// <remarks>
/// Separate from the flask settings and in its own file, one section per concern: the point
/// of a settings file is that a person can open it, and a single bag of everything stops
/// being that as soon as it has two features in it.
/// </remarks>
public sealed record OverlaySettings(
    [property: JsonPropertyName("minLootRarity")] ItemRarity MinLootRarity,
    [property: JsonPropertyName("showTerrain")] bool ShowTerrain = true)
{
    /// <summary>
    /// Magic and above, and the layout shown. Path of Exile 2 drops normal-rarity items
    /// faster than they can be read, so marking all of them buries the drops worth walking
    /// to; the layout is the reason to look at the map at all.
    /// </summary>
    public static OverlaySettings Default { get; } = new(ItemRarity.Magic);

    /// <summary>Keeps the value inside the range the overlay understands.</summary>
    /// <remarks>
    /// Currency is not a threshold - it is a classification for drops that carry no rarity
    /// at all, and it is always shown. Allowing it to be SELECTED as the minimum would read
    /// as "currency only" while actually meaning "rarity 5 and above", which is nothing.
    /// </remarks>
    public OverlaySettings Normalised() => MinLootRarity switch
    {
        ItemRarity.Normal or ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique => this,
        _ => this with { MinLootRarity = Default.MinLootRarity },
    };
}

/// <summary>Loads and saves the overlay settings next to the executable.</summary>
public static class OverlaySettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "overlay.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    public static OverlaySettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return OverlaySettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            OverlaySettings? loaded = JsonSerializer.Deserialize(stream, OverlayJsonContext.Default.OverlaySettings);
            return loaded?.Normalised() ?? OverlaySettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return OverlaySettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(OverlaySettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, OverlayJsonContext.Default.OverlaySettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OverlaySettings))]
public sealed partial class OverlayJsonContext : JsonSerializerContext;
