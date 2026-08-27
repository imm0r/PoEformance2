using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What the preload alerts do. The list itself lives in its own file.</summary>
/// <remarks>
/// SWITCHES ONLY, since the exact-path rewrite. What to watch for is a curated list of full
/// paths worth handing to somebody else, so it is kept in <see cref="PreloadAlertStore"/> where
/// importing one person's list cannot overwrite another person's window.
/// </remarks>
/// <param name="Card">Say it across the screen on the way into an area.</param>
/// <param name="List">Keep the area's whole finding list in the corner.</param>
/// <param name="Window">
/// The standing window that names what this area holds, for as long as you are in it.
/// </param>
/// <param name="HideInTown">
/// Keep it off screen in town and hideouts. On by default: the file list is not refreshed
/// there, so what it would show is the last real area rather than where you are.
/// </param>
/// <param name="HideWhenEmpty">Take the window away entirely when nothing matched.</param>
/// <param name="Timer">Show how long since the last area loaded.</param>
public sealed record PreloadSettings(
    [property: JsonPropertyName("card")] bool Card = true,
    [property: JsonPropertyName("list")] bool List = true,
    [property: JsonPropertyName("window")] bool Window = true,
    [property: JsonPropertyName("hideInTown")] bool HideInTown = true,
    [property: JsonPropertyName("hideWhenEmpty")] bool HideWhenEmpty = false,
    [property: JsonPropertyName("timer")] bool Timer = true)
{
    public static PreloadSettings Default { get; } = new();
}

/// <summary>
/// How long the card on the way in stays, and how it arrives and leaves.
/// </summary>
/// <remarks>
/// Here rather than in the drawing, because it is the part with edges: a clock that went
/// backwards, the exact moment the fade starts, the frame after it ended. Drawing code cannot
/// be reached by a test in this project - the overlay is Windows-only and the tests are not -
/// so timing that matters lives where it can be checked.
///
/// Both ends fade. Something that appears instantly over a fight reads as a glitch, and
/// something that vanishes instantly is one you are never sure you read.
/// </remarks>
public static class PreloadCard
{
    /// <summary>How long the whole card is on screen.</summary>
    public const long ShownMs = 5_000;

    /// <summary>How long it takes to arrive.</summary>
    public const long FadeInMs = 400;

    /// <summary>And to leave, at the end of its time.</summary>
    public const long FadeOutMs = 800;

    /// <summary>
    /// Whether a card of this age is still on screen at all.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Readability"/>, and the separation is the whole point. The
    /// first version asked "is it readable" and threw the card away when the answer was zero -
    /// which is the answer at age ZERO, because that is where the fade in starts. Announcing
    /// and drawing happen in the same frame off the same clock, so every card was destroyed on
    /// its own first frame and none was ever seen.
    ///
    /// Being invisible and being over are different states, and only time can say which.
    /// </remarks>
    public static bool Showing(long ageMs) => ageMs >= 0 && ageMs < ShownMs;

    /// <summary>
    /// How readable the card is at a given age, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// Zero at both ends. That is the fade, not a verdict on whether the card still exists -
    /// see <see cref="Showing"/>, which is what a caller must ask before deciding to drop it.
    ///
    /// A negative age means the clock went backwards - a restart, or a caller passing
    /// something else - and reads as gone rather than as an exception in a render loop.
    /// </remarks>
    public static float Readability(long ageMs)
    {
        if (ageMs < 0 || ageMs >= ShownMs)
        {
            return 0f;
        }

        if (ageMs < FadeInMs)
        {
            return ageMs / (float)FadeInMs;
        }

        long leaving = ShownMs - FadeOutMs;
        return ageMs <= leaving ? 1f : (ShownMs - ageMs) / (float)FadeOutMs;
    }
}

/// <summary>Loads and saves the preload switches next to the executable.</summary>
public static class PreloadStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "preload.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
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
            return loaded ?? PreloadSettings.Default;
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
[JsonSerializable(typeof(List<PreloadAlertEntry>))]
public sealed partial class PreloadJsonContext : JsonSerializerContext;
