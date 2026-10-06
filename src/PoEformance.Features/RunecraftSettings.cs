using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>How the price on a recipe row is tinted, so the row worth taking reads at a glance.</summary>
public enum RunecraftColourMode
{
    /// <summary>One neutral colour for every price.</summary>
    Off,

    /// <summary>Green, yellow or red against the MEDIAN of the rows on screen - a relative call.</summary>
    Relative,

    /// <summary>Green, yellow or red against fixed Exalted thresholds - see <see cref="RunecraftSettings.GoodFrom"/>.</summary>
    Absolute,
}

/// <summary>
/// What the Runeshape Combinations overlay draws, and how.
/// </summary>
/// <remarks>
/// PORTED FROM yokkenUA's RunecraftHelper for GameHelper2: while the in-game Runeshape
/// Combinations panel is open, the poe.ninja price of every offered reward is written onto its
/// row, just before the reward's name, in Exalted. The row's own text - the name in the client's
/// language - is left to the game; only the price is added. The plugin's monolith-on-map labels,
/// rune-chain valuation and expedition route planner are NOT here: they are a different feature
/// several times this size, resting on monolith offsets this tool has not measured.
///
/// OFF BY DEFAULT, like everything that needs the network: the prices come from poe.ninja and
/// nowhere else, and the price switch on the Stash tab is the one that lets them be fetched at
/// all. The Runecraft tab says so when this is on and that is off.
/// </remarks>
/// <param name="Enabled">Whether prices are written on the panel at all.</param>
/// <param name="ColourMode">How a price is tinted.</param>
/// <param name="XOffset">
/// Pixels to slide the price sideways from its place before the reward's name. Negative moves
/// it left. For a client language whose names run further left than the room allows, or a
/// letterboxed display whose bars the layout does not know.
/// </param>
/// <param name="TextScale">
/// How big the price is written, against half the row's height. Zero means "as the row" - the
/// unset value every zero in these files is, see <see cref="Writing"/>.
/// </param>
/// <param name="FrameBest">Frame the most valuable row in green, icons to edge, so the answer needs no reading.</param>
/// <param name="GoodFrom">In Absolute mode, the Exalted a reward is green at or above.</param>
/// <param name="BadBelow">In Absolute mode, the Exalted a reward is red below.</param>
/// <param name="ShowUnpriced">Write a mark on the rows nothing could price, rather than nothing.</param>
public sealed record RunecraftSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("colourMode")] RunecraftColourMode ColourMode = RunecraftColourMode.Relative,
    [property: JsonPropertyName("xOffset")] float XOffset = 0f,
    [property: JsonPropertyName("textScale")] float TextScale = 0f,
    [property: JsonPropertyName("frameBest")] bool FrameBest = true,
    [property: JsonPropertyName("goodFrom")] float GoodFrom = 5f,
    [property: JsonPropertyName("badBelow")] float BadBelow = 0.5f,
    [property: JsonPropertyName("showUnpriced")] bool ShowUnpriced = false)
{
    public static RunecraftSettings Default { get; } = new();

    /// <summary>How far the price may be slid either way, in pixels.</summary>
    public const float FurthestOffset = 600f;

    /// <summary>The smallest and largest writing worth allowing, as multiples of the row.</summary>
    public const float SmallestText = 0.5f;

    public const float LargestText = 3f;

    /// <summary>How big to write, with zero meaning "as the row".</summary>
    public float Writing => TextScale <= 0f ? 1f : Math.Clamp(TextScale, SmallestText, LargestText);

    /// <summary>Keeps every value inside what can be drawn - a hand-edited file is the ordinary source.</summary>
    public RunecraftSettings Normalised() => this with
    {
        XOffset = float.IsFinite(XOffset) ? Math.Clamp(XOffset, -FurthestOffset, FurthestOffset) : 0f,
        TextScale = float.IsFinite(TextScale) && TextScale > 0f ? Math.Clamp(TextScale, SmallestText, LargestText) : 0f,
        GoodFrom = float.IsFinite(GoodFrom) && GoodFrom > 0f ? GoodFrom : Default.GoodFrom,
        BadBelow = float.IsFinite(BadBelow) && BadBelow >= 0f ? BadBelow : Default.BadBelow,
    };
}

/// <summary>Loads and saves the Runecraft settings next to the executable.</summary>
public static class RunecraftStore
{
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "runecraft.json");

    /// <summary>Reads the settings, or the defaults when there is no file or it is unreadable.</summary>
    public static RunecraftSettings Load(string? path = null)
    {
        string from = path ?? DefaultPath;
        try
        {
            if (!File.Exists(from))
            {
                return RunecraftSettings.Default;
            }

            using FileStream stream = File.OpenRead(from);
            return JsonSerializer.Deserialize(stream, RunecraftJsonContext.Default.RunecraftSettings)?.Normalised()
                   ?? RunecraftSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return RunecraftSettings.Default;
        }
    }

    /// <summary>Writes the settings. Returns false rather than throwing into a render loop.</summary>
    public static bool Save(RunecraftSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string to = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, JsonSerializer.Serialize(settings, RunecraftJsonContext.Default.RunecraftSettings));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunecraftSettings))]
public sealed partial class RunecraftJsonContext : JsonSerializerContext;
