using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// What this build IS: the commit it was compiled from, and when.
/// </summary>
/// <remarks>
/// A FILE BESIDE THE EXE, not an assembly attribute, and the reason is the release this tool
/// actually ships. Every build goes out through the rolling <c>latest-dev</c> tag, so the tag
/// never changes and the assembly version has been <c>1.0.0.0</c> since the first commit -
/// neither can say whether the copy somebody is running is this week's or March's. The commit
/// can, and it is the only identifier the publish pipeline already has.
///
/// It is written by the publish workflow into the publish output, so it travels inside the
/// release zip and lands next to the executable on install. The SAME file is uploaded as a
/// release asset, which is what makes the comparison exact rather than inferred: the updater
/// reads one stamp locally and the other over HTTPS, and both were written by the same step of
/// the same run.
///
/// A build from a developer's own machine has no such file, and that is a state with its own
/// name - <see cref="Known"/> is false, and the updater says "cannot compare" rather than
/// offering to overwrite a working tree's bin folder with a release zip.
/// </remarks>
public sealed record BuildStamp
{
    /// <summary>A build that cannot say what it is - a local build, or a stamp that failed to read.</summary>
    public static readonly BuildStamp Unknown = new();

    /// <summary>The release tag this build went out under ("latest-dev").</summary>
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    /// <summary>The full commit sha this was compiled from.</summary>
    [JsonPropertyName("commit")]
    public string Commit { get; init; } = string.Empty;

    /// <summary>When the publish ran, in UTC.</summary>
    [JsonPropertyName("builtUtc")]
    public DateTimeOffset BuiltUtc { get; init; }

    /// <summary>The workflow run that produced it - the number in the Actions list.</summary>
    [JsonPropertyName("runNumber")]
    public int RunNumber { get; init; }

    /// <summary>Whether this stamp can be compared against another at all.</summary>
    /// <remarks>
    /// BOTH halves are required, because they answer different questions and one without the
    /// other cannot decide anything: the commit says whether two builds are the same, the
    /// timestamp says which of two different ones is newer.
    /// </remarks>
    [JsonIgnore]
    public bool Known => Commit.Length > 0 && BuiltUtc != default;

    /// <summary>The commit as it is written everywhere else - seven characters.</summary>
    [JsonIgnore]
    public string ShortCommit => Commit.Length >= 7 ? Commit[..7] : Commit;

    /// <summary>Where the stamp lives: beside the executable, like the schema and the ui folder.</summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/> rather than the assembly's location, which is
    /// empty in a single-file publish - and a single-file publish is exactly what the release
    /// ships.
    /// </remarks>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "version.json");

    /// <summary>This build, or <see cref="Unknown"/> when there is no stamp to read.</summary>
    public static BuildStamp Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return Unknown;
            }

            using FileStream stream = File.OpenRead(file);
            return JsonSerializer.Deserialize(stream, BuildStampJsonContext.Default.BuildStamp) ?? Unknown;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A stamp that cannot be read is a build that cannot say what it is, which is
            // already a state this handles. Nothing here is worth failing a launch over.
            return Unknown;
        }
    }

    /// <summary>Reads a stamp out of the JSON text the release asset carries.</summary>
    public static BuildStamp Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, BuildStampJsonContext.Default.BuildStamp) ?? Unknown;
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    /// <summary>Writes a stamp, returning false when it could not.</summary>
    /// <remarks>Used by the tests and by anything that wants to fabricate one; the shipped
    /// stamp is written by the workflow, not by the tool.</remarks>
    public static bool Save(BuildStamp stamp, string path)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, stamp, BuildStampJsonContext.Default.BuildStamp);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The build in one line, for a status readout.</summary>
    public string Describe()
        => Known
            ? $"{(Tag.Length > 0 ? Tag + " " : string.Empty)}{ShortCommit}"
                + $", built {BuiltUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                + (RunNumber > 0 ? $" (run {RunNumber})" : string.Empty)
            : "local build - no version.json beside the executable";
}

/// <summary>Source-generated JSON, so the stamp survives Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BuildStamp))]
public sealed partial class BuildStampJsonContext : JsonSerializerContext;
