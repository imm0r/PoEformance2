using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>
/// Which monsters a gate lets through, and what to do about them.
/// </summary>
/// <remarks>
/// TWO GATES RATHER THAN ONE, and they are deliberately separate settings with separate
/// defaults: drawing a marker is free and pressing a key is not. Somebody who wants to SEE
/// everything coming and only have the tool ACT on a rare's slam is the ordinary case, and one
/// shared threshold cannot express it.
/// </remarks>
/// <param name="Enabled">Whether this gate does anything at all.</param>
/// <param name="FromRarity">
/// The least rare monster this gate lets through. <see cref="ItemRarity.Normal"/> is every
/// monster; <see cref="ItemRarity.Unique"/> is bosses only.
/// </param>
/// <param name="OnlyPaths">
/// When non-empty, only monsters whose metadata path contains one of these. The path is the
/// game's own identifier - Metadata/Monsters/Goatman/GoatmanLeaper - so a substring is a
/// monster TYPE without needing a table of names.
/// </param>
/// <param name="IgnorePaths">Monsters whose path contains one of these never pass, whatever else says.</param>
public sealed record EvasionGate(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("fromRarity")] ItemRarity FromRarity = ItemRarity.Normal,
    [property: JsonPropertyName("onlyPaths")] IReadOnlyList<string>? OnlyPaths = null,
    [property: JsonPropertyName("ignorePaths")] IReadOnlyList<string>? IgnorePaths = null)
{
    /// <summary>Whether a monster of this rarity and path gets through.</summary>
    /// <remarks>
    /// An UNKNOWN rarity passes when the floor is Normal and is refused otherwise, which is the
    /// safe way round for each: a monster whose rarity would not read is still worth a marker,
    /// and is not worth pressing a key over.
    /// </remarks>
    public bool Admits(ItemRarity rarity, string path)
    {
        if (!Enabled)
        {
            return false;
        }

        if (rarity < FromRarity && !(rarity == ItemRarity.Unknown && FromRarity == ItemRarity.Normal))
        {
            return false;
        }

        path ??= string.Empty;

        foreach (string ignore in IgnorePaths ?? [])
        {
            if (ignore.Length > 0 && path.Contains(ignore, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        IReadOnlyList<string> only = OnlyPaths ?? [];
        if (only.Count == 0)
        {
            return true;
        }

        foreach (string wanted in only)
        {
            if (wanted.Length > 0 && path.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops empty filter strings, which would otherwise match everything.</summary>
    public EvasionGate Normalised() => this with
    {
        FromRarity = FromRarity < ItemRarity.Normal ? ItemRarity.Normal : FromRarity,
        OnlyPaths = Clean(OnlyPaths),
        IgnorePaths = Clean(IgnorePaths),
    };

    private static IReadOnlyList<string>? Clean(IReadOnlyList<string>? paths)
        => paths is null ? null : [.. paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
}

/// <summary>
/// Everything the user decides about seeing and avoiding incoming attacks.
/// </summary>
/// <remarks>
/// ITS OWN FILE, beside autoflask.json rather than inside the tracker's, because this is the
/// second feature in the tool that can PRESS A KEY. The tracker's settings decide what is drawn;
/// these decide what is drawn AND what is done, and the two halves are worth being able to find
/// in one place when the question is "why did my character just roll".
///
/// EVERYTHING IS OFF BY DEFAULT, including the drawing. A tool that starts acting on its own the
/// first time it is run is not one worth shipping, and the same argument applies a second time
/// over to the half that synthesises input.
/// </remarks>
/// <param name="Warn">Draw a marker where an incoming action will land.</param>
/// <param name="Act">Press the dodge key when one is about to land on the player.</param>
/// <param name="DangerRadius">
/// How close an action's landing spot must be to the player to count as aimed at them, in world
/// units.
/// </param>
/// <param name="CooldownMs">
/// Shortest gap between two dodge presses. Not cosmetic: the planner re-decides on every read,
/// and a slam stays committed for as long as its wind-up lasts, so without this one attack
/// would spend every roll charge the character has.
/// </param>
/// <param name="DodgeKey">
/// Virtual-key code to press. 0 means unset, and with it the tool warns but never acts.
///
/// THIS IS THE ONE KEY THE TOOL DOES NOT READ FROM THE GAME, and the difference from the flask
/// keys is deliberate: their config spelling was established against a real file, and nobody has
/// established what this game calls the dodge roll. Reading a plausible-looking line instead
/// would be a guess dressed as a measurement, and it would send a key the player never bound.
/// The AHK tool settles it the same way - its action hotkeys are always keys the person chose.
/// <see cref="DodgeKeyHints"/> shows the candidates in the config to save somebody opening it.
/// </param>
public sealed record EvasionSettings(
    [property: JsonPropertyName("warn")] EvasionGate? Warn = null,
    [property: JsonPropertyName("act")] EvasionGate? Act = null,
    [property: JsonPropertyName("dangerRadius")] float DangerRadius = 90f,
    [property: JsonPropertyName("cooldownMs")] int CooldownMs = 1200,
    [property: JsonPropertyName("dodgeKey")] int DodgeKey = 0,
    [property: JsonPropertyName("onlyDangerousAnimations")] bool OnlyDangerousAnimations = true,
    [property: JsonPropertyName("markerColour")] string MarkerColour = "#C8FF3C28",
    [property: JsonPropertyName("aimedColour")] string AimedColour = "#FFFF0000",
    [property: JsonPropertyName("markerRadius")] float MarkerRadius = 14f,
    [property: JsonPropertyName("thickness")] float Thickness = 2f,
    [property: JsonPropertyName("showLine")] bool ShowLine = true,
    [property: JsonPropertyName("showName")] bool ShowName = false)
{
    /// <summary>Off, and drawing for everything / acting on rares once switched on.</summary>
    /// <remarks>
    /// The two floors differ on purpose. A marker for an ordinary monster costs a ring on the
    /// screen; a keystroke for one costs a roll charge and a moment of not controlling your own
    /// character, and white monsters are most of what an area contains.
    /// </remarks>
    public static EvasionSettings Default { get; } = new(
        Warn: new EvasionGate(Enabled: false, FromRarity: ItemRarity.Normal),
        Act: new EvasionGate(Enabled: false, FromRarity: ItemRarity.Rare));

    /// <summary>The warn gate, or its default when the file said nothing.</summary>
    public EvasionGate WarnOrDefault => Warn ?? Default.Warn!;

    /// <summary>The act gate, on the same terms.</summary>
    public EvasionGate ActOrDefault => Act ?? Default.Act!;

    /// <summary>
    /// Whether the reader has to read monster ACTIONS for any of this to work.
    /// </summary>
    /// <remarks>
    /// The priced setting, asked here for the same reason the tracker asks its own: the reader
    /// and the feature must not be able to disagree about whether the read is happening. Four
    /// reads per hostile monster per tick buys nothing while both gates are off.
    /// </remarks>
    public bool NeedsActions => WarnOrDefault.Enabled || ActOrDefault.Enabled;

    /// <summary>Keeps every value inside what the planner and the overlay can use.</summary>
    public EvasionSettings Normalised() => this with
    {
        Warn = WarnOrDefault.Normalised(),
        Act = ActOrDefault.Normalised(),

        // A radius of zero would mean nothing is ever aimed at you; the upper bound is about a
        // screen's width, beyond which "near the player" has stopped meaning anything.
        DangerRadius = Math.Clamp(DangerRadius, 10f, 2000f),

        // Zero is legal and means "act on every tick that still sees the threat"; the ceiling
        // stops a stray keystroke parking the feature for the rest of the session.
        CooldownMs = Math.Clamp(CooldownMs, 0, 60_000),
        DodgeKey = Math.Clamp(DodgeKey, 0, 0xFF),
        MarkerRadius = Math.Clamp(MarkerRadius, 2f, 200f),
        Thickness = Math.Clamp(Thickness, 0.5f, 10f),
    };
}

/// <summary>Loads and saves the evasion settings beside the executable.</summary>
public static class EvasionSettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "evasion.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// A corrupt file returns the defaults, which are OFF - the correct way for a settings file
    /// to fail when the setting arms key presses.
    /// </remarks>
    public static EvasionSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return EvasionSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            EvasionSettings? loaded = JsonSerializer.Deserialize(stream, EvasionJsonContext.Default.EvasionSettings);
            return loaded?.Normalised() ?? EvasionSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return EvasionSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(EvasionSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, EvasionJsonContext.Default.EvasionSettings);
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
[JsonSerializable(typeof(EvasionSettings))]
public sealed partial class EvasionJsonContext : JsonSerializerContext;
