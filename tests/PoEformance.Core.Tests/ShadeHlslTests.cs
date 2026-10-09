using System.Globalization;
using PoEformance.Game.Files;
using Xunit;

namespace PoEformance.Core.Tests;

/// <summary>
/// Shade programs written out as HLSL - every material the fixtures hold, and the numbers in them to the bit.
/// </summary>
/// <remarks>
/// WHETHER THE HLSL DRAWS WHAT THE PROCESSOR DRAWS is the Windows job's question (PoEformance.Gpu.Tests,
/// on WARP); what is asked here is that every program the game's real materials compile to can be
/// written at all - an op the writer does not know throws - and that a constant reads back as the very
/// float it was.
/// </remarks>
public class ShadeHlslTests
{
    [Fact]
    public void EVERYFixtureMaterialsProgramIsWrittenWithEachOfItsSheetsAndARun()
    {
        var written = 0;
        foreach ((string name, ShadeProgram program) in Programs())
        {
            string hlsl = ShadeHlsl.Of(program, 8);
            Assert.Contains("ShadeOut ShadeRun(SamplerState wrap", hlsl, StringComparison.Ordinal);
            for (var sheet = 0; sheet < program.Textures.Count; sheet++)
            {
                Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"Texture2D ShadeSheet{sheet} : register(t{8 + sheet});"), hlsl, StringComparison.Ordinal);
            }

            Assert.True(hlsl.Contains("static const bool ShadeUsesDepth = " + (program.UsesDepth ? "true" : "false"), StringComparison.Ordinal), name);
            written++;
        }

        Assert.True(written >= 30, $"only {written} programs were written");
    }

    [Theory]
    [InlineData(0.04f)]
    [InlineData(1e-7f)]
    [InlineData(-0f)]
    [InlineData(3.40282347e38f)]
    [InlineData(21.055f)]
    [InlineData(1f / 3f)]
    public void ACONSTANTIsWrittenSoItReadsBackToTheBit(float value)
    {
        string said = ShadeHlsl.Number(value);
        Assert.True(said.Contains('.', StringComparison.Ordinal) || said.Contains('E', StringComparison.Ordinal), said);
        Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(float.Parse(said, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void NOTANumberAndTheInfinitiesAreWrittenByTheirBits()
    {
        Assert.Equal("asfloat(0x7F800000u)", ShadeHlsl.Number(float.PositiveInfinity));
        Assert.Equal("asfloat(0xFF800000u)", ShadeHlsl.Number(float.NegativeInfinity));
        Assert.StartsWith("asfloat(0x", ShadeHlsl.Number(float.NaN), StringComparison.Ordinal);
    }

    /// <summary>Every material in tests/fixtures/shaders whose graphs are all there, compiled - its plain and its glossy program.</summary>
    internal static IEnumerable<(string Name, ShadeProgram Program)> Programs()
    {
        string root = Folder();
        foreach (string material in Directory.GetFiles(root, "*.mat").Order(StringComparer.Ordinal))
        {
            IReadOnlyList<ShaderInstance> instances = ShaderGraph.Instances(File.ReadAllBytes(material));
            if (instances.Count == 0 || instances.Any(one => !File.Exists(Path.Combine(root, Flat(one.Parent)))))
            {
                continue;
            }

            ShadeCompile compiled = ShadeProgram.Compile(
                [.. instances.Select(one => (one, ShaderGraph.Read(File.ReadAllBytes(Path.Combine(root, Flat(one.Parent))))))]);
            string name = Path.GetFileNameWithoutExtension(material);
            if (compiled.Program is { } program)
            {
                yield return (name, program);
                if (program.Glossy is { } glossy)
                {
                    yield return (name + " (glossy)", glossy);
                }
            }
        }
    }

    private static string Flat(string path) => path.Replace("/", "__", StringComparison.Ordinal);

    private static string Folder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", "shaders");
    }
}
