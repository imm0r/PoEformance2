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
    [property: JsonPropertyName("terrainThickness")] int TerrainThickness = 1,
    [property: JsonPropertyName("hideNoise")] bool HideNoise = true,
    [property: JsonPropertyName("showPoi")] bool ShowPoi = true,
    [property: JsonPropertyName("poiLabels")] bool PoiLabels = true,
    [property: JsonPropertyName("poiRoutes")] bool PoiRoutes = true,
    [property: JsonPropertyName("poiArrows")] bool PoiArrows = true,
    [property: JsonPropertyName("dotLabels")] bool DotLabels = false,
    [property: JsonPropertyName("healthBarsOnlyWhenHurt")] bool HealthBarsOnlyWhenHurt = false)
{
    /// <summary>
    /// Magic and above, and the layout shown. Path of Exile 2 drops normal-rarity items
    /// faster than they can be read, so marking all of them buries the drops worth walking
    /// to; the layout is the reason to look at the map at all.
    /// </summary>
    public static OverlaySettings Default { get; } = new(ItemRarity.Magic);

    /// <summary>
    /// Takes the fields the configuration page owns, and keeps the rest.
    /// </summary>
    /// <remarks>
    /// TWO THINGS WRITE THIS FILE and only one of them knows every field. The page sends the
    /// four settings it shows; deserialising that into a whole record gives the others their
    /// DEFAULTS, and saving it then quietly resets every switch made on the overlay itself.
    /// Nothing about that failure is visible - the page did what it was asked, the file is
    /// valid, and the user's choices are simply gone the next time they look.
    ///
    /// So the page's values are merged in rather than swapped for. Anything it does not show
    /// is not its to overwrite.
    /// </remarks>
    public OverlaySettings MergeFromPage(OverlaySettings sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        return this with
        {
            MinLootRarity = sent.MinLootRarity,
            ShowTerrain = sent.ShowTerrain,
            TerrainColour = sent.TerrainColour,
            TerrainThickness = sent.TerrainThickness,
        };
    }

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
    /// A colour as ImGui packs it - ABGR, alpha first in the high byte.
    /// </summary>
    /// <remarks>
    /// Takes <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c>. Returns 0 for anything
    /// unparseable, which the caller treats as "use the default" rather than drawing an
    /// invisible line. ImGui's byte order is the reverse of the #RRGGBB a page sends, and
    /// getting that backwards produces a colour that looks deliberate and is wrong.
    ///
    /// A fully transparent colour parses to 0 as well, so it reads as "not chosen" rather
    /// than as a request to draw nothing. Drawing nothing is what the visibility switch is
    /// for, and it says so where somebody looking for a missing marker would look.
    /// </remarks>
    public static uint ParseColour(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        ReadOnlySpan<char> text = value.AsSpan().Trim().TrimStart('#');
        if (text.Length is not (6 or 8)
            || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            return 0;
        }

        uint a = text.Length == 8 ? (argb >> 24) & 0xFF : 0xFF;
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// The same colour with less of it: scales the alpha and leaves the colour alone.
    /// </summary>
    /// <remarks>
    /// HERE rather than in each drawing class, which is where it started - three private
    /// copies of the same bit-shifting, each having to get the byte order right on its own.
    /// It is also the half of a fade a test can reach: the overlay is Windows-only and the
    /// tests are not, so a copy living beside the drawing is a copy nothing checks.
    ///
    /// MULTIPLIES rather than sets, so a colour somebody deliberately made half transparent
    /// stays relatively that way while a banner fades the whole thing out.
    /// </remarks>
    public static uint Fade(uint colour, float by)
    {
        if (by >= 1f)
        {
            return colour;
        }

        uint alpha = (uint)Math.Clamp(((colour >> 24) & 0xFF) * by, 0f, 255f);
        return (colour & 0x00FF_FFFF) | (alpha << 24);
    }

    /// <summary>Writes an ImGui colour back out as <c>#AARRGGBB</c>.</summary>
    /// <remarks>
    /// The other direction, for whatever a colour picker produced. Always eight digits: a
    /// picker that can set alpha needs somewhere to put it, and a file where some entries
    /// carry alpha and some do not is harder to read than one where they all do.
    /// </remarks>
    public static string FormatColour(uint packed)
    {
        uint a = (packed >> 24) & 0xFF;
        uint b = (packed >> 16) & 0xFF;
        uint g = (packed >> 8) & 0xFF;
        uint r = packed & 0xFF;
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
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
