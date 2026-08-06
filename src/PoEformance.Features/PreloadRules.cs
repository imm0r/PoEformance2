using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// One thing worth being told an area loaded.
/// </summary>
/// <remarks>
/// THE LIST IS THE FEATURE, and it belongs to whoever is playing rather than to this file. An
/// area loads a few thousand paths and almost all of it is scenery; what makes any of it useful
/// is the short list that means something, and that list grows every league, from whoever is
/// looking at the raw list when something new turns up. Shipping it as code meant it could only
/// grow by a release - so it is data now, added from the window that shows the raw paths.
///
/// MATCHED LOOSELY, on a fragment. The full paths carry variant suffixes and league-specific
/// folders that change between patches, while the fragment that names the thing - "Breach",
/// "Expedition", "Ritual" - does not. A fragment that stops matching is one line missing from a
/// list; a full path that stops matching is the same, with more work to find out why.
/// </remarks>
/// <param name="PathContains">
/// Part of a loaded file's path, matched case-insensitively. A rule with nothing here matches
/// nothing, rather than everything - see <see cref="SaysNothing"/>.
/// </param>
/// <param name="Weight">How loudly it should be said, and what colour it is drawn in.</param>
/// <param name="Enabled">Off keeps the rule without acting on it. Deleting is the other way.</param>
/// <param name="Alert">
/// Whether it also raises the banner on the way in, as opposed to only appearing in the list.
/// Shipped on for the things worth turning around for, off for the ones that are merely worth
/// knowing - a banner that names five things every map is a banner nobody reads.
/// </param>
public sealed record PreloadRule(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pathContains")] string PathContains,
    [property: JsonPropertyName("weight")] PreloadWeight Weight = PreloadWeight.Notable,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("alert")] bool Alert = true)
{
    /// <summary>
    /// Whether this rule says anything at all about what to look for.
    /// </summary>
    /// <remarks>
    /// An empty fragment is contained by EVERY string, so a rule with one would report the
    /// whole file table as a single finding of a few thousand files. That arrives from a
    /// hand-edited file and from an add box somebody hit return in, so it is checked rather
    /// than documented.
    /// </remarks>
    public bool SaysNothing => string.IsNullOrWhiteSpace(PathContains) || string.IsNullOrWhiteSpace(Name);

    /// <summary>Whether a loaded file's path is what this rule is looking for.</summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Enabled
            && !SaysNothing
            && path.Contains(PathContains, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The rules a fresh install starts with.
/// </summary>
/// <remarks>
/// The league mechanics, because knowing an area holds a breach before walking into it is the
/// reason the file list is read at all. Strongboxes and shrines are listed but do not raise the
/// banner: they are in most areas, so announcing them would train the eye to ignore the line
/// that also says "Ultimatum".
/// </remarks>
public static class DefaultPreloadRules
{
    public static IReadOnlyList<PreloadRule> Rules { get; } =
    [
        new("Breach", "/Breach/", PreloadWeight.Valuable),
        new("Expedition", "/Expedition/", PreloadWeight.Valuable),
        new("Ritual", "/Ritual/", PreloadWeight.Valuable),
        new("Delirium", "/Delirium/", PreloadWeight.Valuable),
        new("Essence", "/Essence/", PreloadWeight.Valuable),
        new("Ultimatum", "/Ultimatum/", PreloadWeight.Valuable),
        new("Legion", "/Legion/", PreloadWeight.Valuable),
        new("Abyss", "/Abyss/", PreloadWeight.Valuable),
        new("Harvest", "/Harvest/", PreloadWeight.Valuable),

        // Worth a line, not worth a banner.
        new("Strongbox", "StrongBoxes/", PreloadWeight.Notable, Alert: false),
        new("Shrine", "/Shrines/", PreloadWeight.Notable, Alert: false),

        // Things that make an area harder rather than richer. Worth knowing at the entrance
        // rather than at the moment one lands on you.
        new("Rogue exile", "/ExileLeague", PreloadWeight.Dangerous),
        new("Beyond demon", "/BeyondDemons/", PreloadWeight.Dangerous),
    ];
}

/// <summary>What the preload alerts do, and what they are watching for.</summary>
/// <param name="Banner">Say it across the screen on the way into an area.</param>
/// <param name="List">Keep it in the corner for as long as you are in the area.</param>
/// <param name="MinFiles">
/// How many matching files a finding needs before it is worth announcing.
/// </param>
/// <param name="Rules">
/// What to look for. ABSENT means "never chosen" and takes the shipped list; EMPTY means
/// "look for nothing" and is honoured - deleting every rule has to be something somebody can
/// do, and a list that grows its defaults back cannot be emptied.
/// </param>
public sealed record PreloadSettings(
    [property: JsonPropertyName("banner")] bool Banner = true,
    [property: JsonPropertyName("list")] bool List = true,
    [property: JsonPropertyName("minFiles")] int MinFiles = PreloadSettings.DefaultMinFiles,
    [property: JsonPropertyName("rules")] IReadOnlyList<PreloadRule>? Rules = null)
{
    /// <summary>
    /// The file count below which a finding is a mention rather than an encounter.
    /// </summary>
    /// <remarks>
    /// Two, because one is the number a stray reference has. Something genuinely in an area
    /// drags its whole art set in - dozens of files - so this only ever silences the marginal
    /// case, and it silences the BANNER only: a one-file finding still appears in the window,
    /// marked, because "usually a stray reference" is not "always".
    /// </remarks>
    public const int DefaultMinFiles = 2;

    public static PreloadSettings Default { get; } = new();

    /// <summary>What to look for - the shipped rules when the file said nothing.</summary>
    public IReadOnlyList<PreloadRule> Watching => Rules ?? DefaultPreloadRules.Rules;
}

/// <summary>Loads and saves the preload alerts next to the executable.</summary>
/// <remarks>
/// A file of its own rather than a section of the alert settings. The two answer questions
/// that only sound alike: one watches entities that are in front of you now, the other reads
/// what the area loaded before you got there. Their rules share no fields, and a single file
/// would have to be versioned as one.
/// </remarks>
public static class PreloadStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "preload.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// Rules that say nothing are dropped on the way in: an empty fragment matches every path
    /// in the table, which is the one thing this feature must never do, and a hand-edited file
    /// is exactly where such a rule comes from.
    /// </remarks>
    public static PreloadSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return PreloadSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            PreloadSettings? loaded = JsonSerializer.Deserialize(stream, PreloadJsonContext.Default.PreloadSettings);
            if (loaded is null)
            {
                return PreloadSettings.Default;
            }

            return loaded.Rules is null
                ? loaded
                : loaded with { Rules = [.. loaded.Rules.Where(rule => rule is not null && !rule.SaysNothing)] };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return PreloadSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(PreloadSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, PreloadJsonContext.Default.PreloadSettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the preload rules survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PreloadSettings))]
public sealed partial class PreloadJsonContext : JsonSerializerContext;
