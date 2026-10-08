using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace PoEformance.Game.Files;

/// <summary>
/// The parts of an area's <c>.env</c> that light it: the sun, the player's light, the environment cube and how bright it is, the exposure.
/// </summary>
/// <remarks>
/// THE KEYS ARE THE INSTALL'S, counted over all 861 files by the env survey (2026-10-08) - every
/// name here is one the survey listed, with the section it sits in. What each one DOES in the game's
/// shaders is a separate question, answered where the shaders answer it (SceneLight says which) and
/// left as a candidate reading where they do not: the sun's phi and theta, and the cube's two angles,
/// are turned into vectors on the processor, which no file shows.
///
/// A KEY THE FILE LEAVES OUT IS NULL HERE, not a default: what the engine puts in its place is not
/// written anywhere this tool reads. <see cref="Assumed"/> lists what a missing key was taken as, so
/// the line under a lit picture can say it rather than hide it.
///
/// JSON, read leniently - comments and trailing commas allowed - as the survey reads it; every one of
/// the 861 read that way.
/// </remarks>
/// <param name="Path">The file it was read from.</param>
/// <param name="SunColour">directional_light.colour.</param>
/// <param name="SunMultiplier">directional_light.multiplier - zero in an area with no sun, Seepage among them.</param>
/// <param name="Phi">directional_light.phi, radians.</param>
/// <param name="Theta">directional_light.theta, radians.</param>
/// <param name="SunShadows">directional_light.shadows_enabled.</param>
/// <param name="PlayerColour">player_light.colour.</param>
/// <param name="PlayerIntensity">player_light.intensity.</param>
/// <param name="DiffuseCube">environment_mapping.diffuse_cube, or empty.</param>
/// <param name="SpecularCube">environment_mapping.specular_cube, or empty.</param>
/// <param name="EnvBrightness">environment_mapping.env_brightness.</param>
/// <param name="DirectLightEnvRatio">environment_mapping.direct_light_env_ratio.</param>
/// <param name="GiAdditionalEnvLight">environment_mapping.gi_additional_env_light.</param>
/// <param name="HorAngle">environment_mapping.hor_angle, radians.</param>
/// <param name="VertAngle">environment_mapping.vert_angle, radians.</param>
/// <param name="GiEnvOcclusion">global_illumination.gi_env_occlusion: how much of the cube's diffuse light the game's own GI replaces.</param>
/// <param name="Exposure">camera.exposure.</param>
/// <param name="PostTransform">post_transform.texture - the colour grade, a 3D table - or empty.</param>
/// <param name="Why">Why nothing was read, or empty.</param>
public sealed record EnvironmentSettings(
    string Path,
    Vector3? SunColour,
    float? SunMultiplier,
    float? Phi,
    float? Theta,
    bool? SunShadows,
    Vector3? PlayerColour,
    float? PlayerIntensity,
    string DiffuseCube,
    string SpecularCube,
    float? EnvBrightness,
    float? DirectLightEnvRatio,
    float? GiAdditionalEnvLight,
    float? HorAngle,
    float? VertAngle,
    float? GiEnvOcclusion,
    float? Exposure,
    string PostTransform,
    string Why = "")
{
    /// <summary>Nothing read.</summary>
    public static EnvironmentSettings None { get; } = new(
        string.Empty, null, null, null, null, null, null, null, string.Empty, string.Empty, null, null, null, null, null, null, null, string.Empty, "no environment");

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Whether a file was read.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>The sun's colour as light: its colour times its multiplier, nought where either is missing - see <see cref="Assumed"/>.</summary>
    public Vector3 SunLight => (SunColour ?? Vector3.One) * (SunMultiplier ?? 0f);

    /// <summary>The player's light: its colour times its intensity.</summary>
    public Vector3 PlayerLight => (PlayerColour ?? Vector3.One) * (PlayerIntensity ?? 1f);

    /// <summary>
    /// What each key the file left out was taken as, one line each - the assumptions a lit picture rests on.
    /// </summary>
    public IReadOnlyList<string> Assumed()
    {
        var said = new List<string>();
        void Missing(bool absent, string key, string taken)
        {
            if (absent)
            {
                said.Add(key + " absent, taken as " + taken);
            }
        }

        Missing(SunMultiplier is null, "directional_light.multiplier", "0 (no sun)");
        Missing(SunColour is null && SunMultiplier is > 0f, "directional_light.colour", "white");
        Missing((Phi is null || Theta is null) && SunMultiplier is > 0f, "directional_light.phi/theta", "0");
        Missing(PlayerColour is null, "player_light.colour", "white");
        Missing(PlayerIntensity is null, "player_light.intensity", "1");
        Missing(EnvBrightness is null, "environment_mapping.env_brightness", "1");
        Missing(DirectLightEnvRatio is null, "environment_mapping.direct_light_env_ratio", "0");
        Missing(HorAngle is null, "environment_mapping.hor_angle", "0");
        Missing(VertAngle is null, "environment_mapping.vert_angle", "0");
        Missing(GiEnvOcclusion is null, "global_illumination.gi_env_occlusion", "0");
        Missing(Exposure is null, "camera.exposure", "1");
        return said;
    }

    /// <summary>The settings a file holds, or <see cref="None"/> with why. Never throws.</summary>
    /// <param name="path">The file's path, kept for the line that says what lit the picture.</param>
    /// <param name="content">Its bytes.</param>
    public static EnvironmentSettings Read(string path, byte[]? content)
    {
        if (content is not { Length: > 0 })
        {
            return None with { Path = path ?? string.Empty, Why = "not in the install" };
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(StatDescriptionFiles.Decode(content), Lenient);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return None with { Path = path ?? string.Empty, Why = "not a JSON object" };
            }

            JsonElement sun = Section(root, "directional_light");
            JsonElement player = Section(root, "player_light");
            JsonElement mapping = Section(root, "environment_mapping");
            return new EnvironmentSettings(
                path ?? string.Empty,
                Colour(sun, "colour"),
                Number(sun, "multiplier"),
                Number(sun, "phi"),
                Number(sun, "theta"),
                Flag(sun, "shadows_enabled"),
                Colour(player, "colour"),
                Number(player, "intensity"),
                Text(mapping, "diffuse_cube"),
                Text(mapping, "specular_cube"),
                Number(mapping, "env_brightness"),
                Number(mapping, "direct_light_env_ratio"),
                Number(mapping, "gi_additional_env_light"),
                Number(mapping, "hor_angle"),
                Number(mapping, "vert_angle"),
                Number(Section(root, "global_illumination"), "gi_env_occlusion"),
                Number(Section(root, "camera"), "exposure"),
                Text(Section(root, "post_transform"), "texture"));
        }
        catch (JsonException fault)
        {
            return None with { Path = path ?? string.Empty, Why = "not JSON: " + fault.Message };
        }
    }

    /// <summary>The numbers the line under a lit picture prints.</summary>
    public string Said()
    {
        if (!Ready)
        {
            return $"{Path}: {Why}";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"sun {Say(SunLight)} phi {Say(Phi)} theta {Say(Theta)}{(SunShadows == true ? " shadows" : string.Empty)}; player {Say(PlayerLight)}; "
            + $"cube x{Say(EnvBrightness)} hor {Say(HorAngle)} vert {Say(VertAngle)} gi occlusion {Say(GiEnvOcclusion)}; exposure {Say(Exposure)}");
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement section) && section.ValueKind == JsonValueKind.Object ? section : default;

    private static float? Number(JsonElement section, string key)
        => section.ValueKind == JsonValueKind.Object && section.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : null;

    private static bool? Flag(JsonElement section, string key)
        => section.ValueKind == JsonValueKind.Object && section.TryGetProperty(key, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string Text(JsonElement section, string key)
        => section.ValueKind == JsonValueKind.Object && section.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Vector3? Colour(JsonElement section, string key)
    {
        if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(key, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 3)
        {
            return null;
        }

        Span<float> rgb = stackalloc float[3];
        for (var at = 0; at < 3; at++)
        {
            JsonElement one = value[at];
            if (one.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            rgb[at] = (float)one.GetDouble();
        }

        return new Vector3(rgb[0], rgb[1], rgb[2]);
    }

    private static string Say(float? value) => value is { } one ? one.ToString("0.###", CultureInfo.InvariantCulture) : "-";

    private static string Say(Vector3 value) => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.###} {value.Y:0.###} {value.Z:0.###}");
}
