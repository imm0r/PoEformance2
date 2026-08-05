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
    [property: JsonPropertyName("showTerrain")] bool ShowTerrain = true,
    [property: JsonPropertyName("terrainColour")] string TerrainColour = "#96C8FF",
    [property: JsonPropertyName("terrainThickness")] int TerrainThickness = 1)
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
    public OverlaySettings Normalised()
    {
        ItemRarity rarity = MinLootRarity is ItemRarity.Normal or ItemRarity.Magic
            or ItemRarity.Rare or ItemRarity.Unique
            ? MinLootRarity
            : Default.MinLootRarity;

        return this with
        {
            MinLootRarity = rarity,
            TerrainColour = ParseColour(TerrainColour) == 0 ? Default.TerrainColour : TerrainColour,

            // Capped low: this thickens the line in TEXTURE pixels, and past a few the
            // outline stops being a boundary and becomes a filled shape.
            TerrainThickness = Math.Clamp(TerrainThickness, 1, 6),
        };
    }

    /// <summary>
    /// The outline colour as ImGui packs it - ABGR, alpha first in the high byte.
    /// </summary>
    /// <remarks>
    /// Returns 0 for anything unparseable, which the caller treats as "use the default"
    /// rather than drawing an invisible line. ImGui's byte order is the reverse of the
    /// #RRGGBB the page sends, and getting that backwards produces a colour that looks
    /// deliberate and is wrong.
    /// </remarks>
    public static uint ParseColour(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        ReadOnlySpan<char> text = value.AsSpan().Trim().TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return 0;
        }

        uint r = (rgb >> 16) & 0xFF;
        uint g = (rgb >> 8) & 0xFF;
        uint b = rgb & 0xFF;
        return 0xFF000000u | (b << 16) | (g << 8) | r;
    }
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
