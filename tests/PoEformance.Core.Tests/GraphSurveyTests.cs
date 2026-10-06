using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The graph survey: what keeps materials from being coloured by their graphs, counted and ranked.
/// </summary>
/// <remarks>
/// Four graphs and seven materials, one of each kind the survey tells apart: a graph the compiler
/// evaluates whole, one reading a vertex colour it does not have, one writing its colour at a stage
/// it does not run through a node it does not know, and one writing no colour at all - plus a
/// material naming no graph and one that is not in the install.
/// </remarks>
public class GraphSurveyTests
{
    private const string Plain =
        """
        {"nodes":[
          {"type":"InputUV","index":0,"stage":"Texturing_Init"},
          {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/a.dds","srgb":true},{}]},
          {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
         "links":[
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
          {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
        """;

    private const string Tinted =
        """
        {"nodes":[
          {"type":"InputVertexColor","index":0,"stage":"VertexInit"},
          {"type":"Saturate","index":0},
          {"type":"AlbedoColor","index":0,"stage":"Texturing"}],
         "links":[
          {"src":{"type":"InputVertexColor","index":0,"stage":"VertexInit","variable":"output"},"dst":{"type":"Saturate","index":0,"variable":"input"}},
          {"src":{"type":"Saturate","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input"}}]}
        """;

    private const string Dusted =
        """
        {"nodes":[
          {"type":"ConstantFloat","index":0,"parameters":[{"value":0.5}]},
          {"type":"Muddle","index":0},
          {"type":"NormalTexToTbn","index":0},
          {"type":"TbnNormal","index":0,"stage":"PixelOutput_Calc"},
          {"type":"AlbedoColor","index":0,"stage":"PixelOutput_Calc"}],
         "links":[
          {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"Muddle","index":0,"variable":"in_value"}},
          {"src":{"type":"Muddle","index":0,"variable":"out_value"},"dst":{"type":"AlbedoColor","index":0,"stage":"PixelOutput_Calc","variable":"input"}},
          {"src":{"type":"NormalTexToTbn","index":0,"variable":"tbn_normal"},"dst":{"type":"TbnNormal","index":0,"stage":"PixelOutput_Calc","variable":"input"}}]}
        """;

    private const string Normals =
        """
        {"nodes":[
          {"type":"NormalTexToTbn","index":0},
          {"type":"TbnNormal","index":0,"stage":"Texturing"}],
         "links":[
          {"src":{"type":"NormalTexToTbn","index":0,"variable":"tbn_normal"},"dst":{"type":"TbnNormal","index":0,"stage":"Texturing","variable":"input"}}]}
        """;

    [Fact]
    public void THESURVEYCountsWhatHoldsMaterialsBackAndOrdersWhatToAdd()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Plain.fxgraph"] = Encoding.UTF8.GetBytes(Plain),
            ["Metadata/Tinted.fxgraph"] = Encoding.UTF8.GetBytes(Tinted),
            ["Metadata/Dusted.fxgraph"] = Encoding.UTF8.GetBytes(Dusted),
            ["Metadata/Normals.fxgraph"] = Encoding.UTF8.GetBytes(Normals),
            ["Art/Textures/Environment/a.mat"] = Material("Metadata/Plain.fxgraph"),
            ["Art/Textures/Environment/b.mat"] = Material("Metadata/Plain.fxgraph", "Metadata/Tinted.fxgraph"),
            ["Art/Models/Monsters/c.mat"] = Material("Metadata/Tinted.fxgraph"),
            ["Art/Textures/Environment/d.mat"] = Material("Metadata/Dusted.fxgraph"),
            ["Art/Textures/Environment/e.mat"] = Material("Metadata/Normals.fxgraph"),
            ["Art/Textures/Environment/f.mat"] = Encoding.UTF8.GetBytes("""{"version":4}"""),
        };
        var told = new List<string>();

        string survey = GraphSurvey.Of(
            path => files.GetValueOrDefault(path),
            [.. files.Keys.Where(one => one.EndsWith(".mat", StringComparison.Ordinal)), "Art/Gone.mat"],
            told.Add);

        Assert.Contains("graph survey of 7 materials in the install", survey, StringComparison.Ordinal);
        Assert.Contains("  1 would not read", survey, StringComparison.Ordinal);
        Assert.Contains("  1 name no graphs", survey, StringComparison.Ordinal);
        Assert.Contains("  1 name graphs, none of which writes a colour or coordinates", survey, StringComparison.Ordinal);
        Assert.Contains("  4 are coloured by their graphs:", survey, StringComparison.Ordinal);
        Assert.Contains("    1 evaluate whole now (25.0%)", survey, StringComparison.Ordinal);
        Assert.Contains("    3 have a graph left out (75.0%)", survey, StringComparison.Ordinal);

        // THE VERTEX COLOUR ALONE holds back two materials, from two folders; the dust's two items
        // hold back one between them. The normal map is off every colour path and counts nowhere.
        Assert.Contains(
            "  InputVertexColor · alone 2 · among 2 · graphs 1 · Art/Models/Monsters 1, Art/Textures/Environment 1",
            survey, StringComparison.Ordinal);
        Assert.Contains("  Muddle · alone 0 · among 1 · graphs 1 · Art/Textures/Environment 1", survey, StringComparison.Ordinal);
        Assert.Contains("  stage PixelOutput_Calc · alone 0 · among 1", survey, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalTexToTbn ·", survey, StringComparison.Ordinal);
        Assert.True(
            survey.IndexOf("  InputVertexColor · alone", StringComparison.Ordinal)
                < survey.IndexOf("  Muddle · alone", StringComparison.Ordinal),
            "what completes the most comes first");

        Assert.Contains("  1. InputVertexColor · completes 2 · 75.0% of the coloured materials evaluate whole after it", survey, StringComparison.Ordinal);
        Assert.Contains("  2. Muddle · completes 0 · 75.0%", survey, StringComparison.Ordinal);
        Assert.Contains("  3. stage PixelOutput_Calc · completes 1 · 100.0%", survey, StringComparison.Ordinal);

        Assert.Contains("  Metadata/Tinted.fxgraph · 2 materials · missing InputVertexColor", survey, StringComparison.Ordinal);
        Assert.Contains("  PixelOutput_Calc · NOT run · graphs 1 · material uses 1", survey, StringComparison.Ordinal);
        Assert.Contains("  Texturing_Init · run · graphs 1 · material uses 2", survey, StringComparison.Ordinal);
        Assert.Equal("0 of 7 materials", told[0]);
    }

    /// <summary>
    /// The install's own materials, from tests/fixtures/shaders: the sand and the dune's tiling evaluate, the dust does not.
    /// </summary>
    /// <remarks>
    /// What the earlier tile dumps said one material at a time, counted: Dust_simple, on seven of the
    /// cliffs, evaluates whole now that the vertex's local position is read, and the vertex colour
    /// BasicColour multiplies by is the only thing standing in the ledge's way.
    /// </remarks>
    [Fact]
    public void ANDOVERTheInstallsOwnMaterialsItNamesWhatTheDumpsNamedOneAtATime()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string root = Path.Combine(dir.FullName, "tests", "fixtures", "shaders");
        byte[]? Read(string path)
        {
            string file = Path.Combine(root, path.Replace("/", "__", StringComparison.Ordinal));
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        string[] materials = [.. Directory.GetFiles(root, "*.mat").Select(one => Path.GetFileName(one).Replace("__", "/", StringComparison.Ordinal))];
        string survey = GraphSurvey.Of(Read, materials);

        Assert.DoesNotContain("Dust_simple.fxgraph ·", survey, StringComparison.Ordinal);
        Assert.Contains("  Metadata/Effects/Graphs/General/BasicColour.fxgraph · 2 materials · missing InputVertexColor", survey, StringComparison.Ordinal);
        Assert.DoesNotContain("PBRGroundBN.fxgraph ·", survey, StringComparison.Ordinal);
        Assert.DoesNotContain("StromatoliteLedge_Blend.fxgraph ·", survey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Beside the survey, a vertex item gets the graph held-back terrain materials name - not the one named most - whole, with one of them.
    /// </summary>
    [Fact]
    public void ANDAVERTEXItemGetsTheTerrainsGraphWhole()
    {
        string Shifted(string mark) =>
            $$$"""
            {"nodes":[
              {"type":"InputVertexColor","index":0,"stage":"VertexInit"},
              {"type":"AlbedoColor","index":0,"stage":"Texturing","custom_parameter":"{{{mark}}}"}],
             "links":[
              {"src":{"type":"InputVertexColor","index":0,"stage":"VertexInit","variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input"}}]}
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Effects/Shifted.fxgraph"] = Encoding.UTF8.GetBytes(Shifted("effect")),
            ["Metadata/Terrain/Shifted.fxgraph"] = Encoding.UTF8.GetBytes(Shifted("terrain")),
            ["Metadata/Effects/a.mat"] = Material("Metadata/Effects/Shifted.fxgraph"),
            ["Metadata/Effects/b.mat"] = Material("Metadata/Effects/Shifted.fxgraph"),
            ["Art/Models/Terrain/Cliff/c.mat"] = Material("Metadata/Terrain/Shifted.fxgraph"),
        };
        var examples = new StringBuilder();

        GraphSurvey.Of(path => files.GetValueOrDefault(path), [.. files.Keys.Where(one => one.EndsWith(".mat", StringComparison.Ordinal))], null, examples);
        string said = examples.ToString();

        Assert.Contains(
            "=== InputVertexColor · graph Metadata/Terrain/Shifted.fxgraph · named by 1 held-back terrain materials, 1 in all",
            said, StringComparison.Ordinal);
        Assert.Contains("--- material Art/Models/Terrain/Cliff/c.mat", said, StringComparison.Ordinal);
        Assert.Contains("\"custom_parameter\":\"terrain\"", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Metadata/Effects/Shifted.fxgraph", said, StringComparison.Ordinal);
    }

    /// <summary>What feeds a colour's w alone - a soft particle's fade by depth - holds nothing back: the w is not drawn.</summary>
    [Fact]
    public void ANDWHATFeedsAColoursWAloneHoldsNothingBack()
    {
        const string faded =
            """
            {"nodes":[
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/a.dds","srgb":true},{}]},
              {"type":"DepthDistance","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"rgba","swizzle":"xyz"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}},
              {"src":{"type":"DepthDistance","index":0,"variable":"distance"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"w"}}]}
            """;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Faded.fxgraph"] = Encoding.UTF8.GetBytes(faded),
            ["Art/Textures/Environment/a.mat"] = Material("Metadata/Faded.fxgraph"),
        };

        string survey = GraphSurvey.Of(path => files.GetValueOrDefault(path), ["Art/Textures/Environment/a.mat"]);

        Assert.Contains("    1 evaluate whole now (100.0%)", survey, StringComparison.Ordinal);
        Assert.DoesNotContain("DepthDistance", survey, StringComparison.Ordinal);
    }

    [Fact]
    public void THECOMPILERSaysWhichNodesItKnows()
    {
        Assert.True(ShadeProgram.Knows("Lerp3"));
        Assert.True(ShadeProgram.Knows("InputUV"));
        Assert.True(ShadeProgram.Knows("InputTexture"));
        Assert.True(ShadeProgram.Knows("One"));
        Assert.True(ShadeProgram.Knows("SmoothStep"));
        Assert.True(ShadeProgram.Knows("InputIndirectColor"));
        Assert.True(ShadeProgram.Knows("SampleInputTriplanar"));
        Assert.True(ShadeProgram.Knows("Noise31"));
        Assert.False(ShadeProgram.Knows("DepthDistance"));
        Assert.False(ShadeProgram.Knows("InputVertexColor"));
        Assert.False(ShadeProgram.Knows("InputVertexUV"));
        Assert.True(ShadeProgram.Knows("FromVertexLocalPosition"));
        Assert.True(ShadeProgram.Knows("LookUpTexture"));
        Assert.True(ShadeProgram.Knows("SampleTriplanar"));
        Assert.True(ShadeProgram.Knows("MuddleTex"));
        Assert.True(ShadeProgram.Knows("FromVertexVariance"));
        Assert.True(ShadeProgram.Knows("RGBToTbn"));
        Assert.False(ShadeProgram.Knows("Muddle"));
        Assert.False(ShadeProgram.Knows("FromVertexColor"));
        Assert.False(ShadeProgram.Knows(string.Empty));
    }

    [Fact]
    public void ANDNOTHINGToSurveyIsSaidRatherThanCounted()
        => Assert.StartsWith("no survey", GraphSurvey.Of(null, ["a.mat"]), StringComparison.Ordinal);

    private static byte[] Material(params string[] graphs)
        => Encoding.UTF8.GetBytes(
            "{\"version\":4,\"graphinstances\":[" + string.Join(',', graphs.Select(one => "{\"parent\":\"" + one + "\"}")) + "]}");
}
