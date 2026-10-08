using System.Text;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The env survey: every key the install's .env files use, how often and with what values, and the files that light their area with a sun.
/// </summary>
public class EnvSurveyTests
{
    /// <summary>Seepage.env as the 2026-10-08 capture printed it, cut to the shapes that matter: a zero sun, a colour, an array of objects.</summary>
    private const string Seepage = """
        {
          "directional_light": { "multiplier": 0.0 },
          "player_light": { "colour": [ 1.0, 0.85869, 0.61868 ], "intensity": 1.23556 },
          "environment_mapping": { "diffuse_cube": "Art/2DArt/Cubemaps/seepagev2_diffuse.dds", "env_brightness": 0.21999 },
          "audio": { "footstep_materials": [ { "material": "a.mat", "audio_id": "RoughStone" }, { "material": "b.mat", "audio_id": "Mud2" } ] }
        }
        """;

    /// <summary>A made-up file with a lit sun - its keys are invented for the test and claim nothing about the game's.</summary>
    private const string Sunny = """
        { "directional_light": { "multiplier": 1.5, "colour": [ 1.0, 0.9, 0.8 ] }, "player_light": { "intensity": 0.5 } }
        """;

    [Fact]
    public void EVERYKeyIsCountedAndTheSunlitFilesAreShownWhole()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Metadata/EnvironmentSettings/Maps/Seepage.env"] = Encoding.UTF8.GetBytes(Seepage),
            ["Metadata/EnvironmentSettings/sunny.env"] = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Sunny)],
            ["Metadata/EnvironmentSettings/broken.env"] = Encoding.UTF8.GetBytes("version 2\nnot json"),
        };

        string said = EnvSurvey.Of(path => files.GetValueOrDefault(path), [.. files.Keys, "Metadata/EnvironmentSettings/gone.env"]);

        Assert.StartsWith("4 .env files in the install, 2 read as JSON, 1 not, 1 could not be read", said, StringComparison.Ordinal);
        Assert.Contains("1 of them light their area with a sun", said, StringComparison.Ordinal);
        Assert.Contains("  not JSON: Metadata/EnvironmentSettings/broken.env: ", said, StringComparison.Ordinal);
        Assert.Contains("directional_light.multiplier  in 2  number  0 .. 1.5  e.g. 0.0 | 1.5", said, StringComparison.Ordinal);
        Assert.Contains("player_light.colour  in 1  array of 3  0.61868 .. 1  e.g. [ 1.0, 0.85869, 0.61868 ]", said, StringComparison.Ordinal);
        Assert.Contains("audio.footstep_materials[].audio_id  in 1  string  e.g. RoughStone | Mud2", said, StringComparison.Ordinal);
        Assert.Contains("environment_mapping.diffuse_cube  in 1  string  e.g. Art/2DArt/Cubemaps/seepagev2_diffuse.dds", said, StringComparison.Ordinal);

        string whole = said[said.IndexOf("=== whole files with a sun", StringComparison.Ordinal)..];
        Assert.Contains("--- Metadata/EnvironmentSettings/sunny.env", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("Seepage.env", whole, StringComparison.Ordinal);
    }

    /// <summary>The cube maps the environments name are tallied by how they are stored - the header is what says whether the picture can read them.</summary>
    [Fact]
    public void THECUBEMapsAreTalliedByTheirHeader()
    {
        const string Cube = "Art/2DArt/Cubemaps/seepagev2_diffuse.dds";
        var files = new Dictionary<string, byte[]>
        {
            ["Metadata/EnvironmentSettings/Maps/Seepage.env"] = Encoding.UTF8.GetBytes(Seepage),
            [Cube] = CubeHeader(32, 6, dxgi: 95),
        };

        string said = EnvSurvey.Of(path => files.GetValueOrDefault(path), ["Metadata/EnvironmentSettings/Maps/Seepage.env"]);

        Assert.Contains("=== the cube maps the environments name, by how they are stored: 1 distinct", said, StringComparison.Ordinal);
        Assert.Contains("1  diffuse_cube: 32x32 · 6 levels · pixel format flags 0x4 · fourCC DX10 · DXGI format 95 (BC6H_UF16) · cube x1  e.g. " + Cube, said, StringComparison.Ordinal);
    }

    /// <summary>A DX10 DDS header of a cube, nothing after it - all a header is asked for.</summary>
    private static byte[] CubeHeader(int size, int levels, int dxgi)
    {
        var dds = new byte[148];
        "DDS "u8.CopyTo(dds);
        BitConverter.TryWriteBytes(dds.AsSpan(4), 124);
        BitConverter.TryWriteBytes(dds.AsSpan(12), size);
        BitConverter.TryWriteBytes(dds.AsSpan(16), size);
        BitConverter.TryWriteBytes(dds.AsSpan(28), levels);
        BitConverter.TryWriteBytes(dds.AsSpan(80), 0x4u);
        "DX10"u8.CopyTo(dds.AsSpan(84));
        BitConverter.TryWriteBytes(dds.AsSpan(112), 0x200u | 0xFC00u);
        BitConverter.TryWriteBytes(dds.AsSpan(128), dxgi);
        BitConverter.TryWriteBytes(dds.AsSpan(132), 3);
        BitConverter.TryWriteBytes(dds.AsSpan(136), 0x4u);
        BitConverter.TryWriteBytes(dds.AsSpan(140), 1);
        return dds;
    }

    [Fact]
    public void WITHNoInstallItSaysSo()
        => Assert.Equal("no install to read the environments from", EnvSurvey.Of(null, ["a.env"]));
}
