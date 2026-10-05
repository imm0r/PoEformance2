using System.Numerics;
using System.Text;
using PoEformance.Game.Files;
using Xunit;

namespace PoEformance.Core.Tests;

/// <summary>
/// Shader graphs read, compiled into a material's colour, and drawn.
/// </summary>
/// <remarks>
/// THE GRAPHS ARE WRITTEN THE WAY THE GAME WRITES THEM - nodes by type and index, links naming both
/// ends and their swizzles - cut down to the nodes each claim is about. The drawing claims compare a
/// shaded quad with the same quad painted in a plain texture of the colour the graph should come to,
/// so the renderer's shading cancels out and what is left is the program's arithmetic.
/// </remarks>
public class ShadeProgramTests
{
    private const string Albedo = """{"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}""";

    [Fact]
    public void AGRAPHReadsItsNodesItsLinksAndTheirSwizzles()
    {
        ShaderGraph graph = Graph(
            """
            {"version":3,"nodes":[
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"SampleTexture","index":2,"parameters":[{"path":"Art/rock.dds","srgb":true},{"value":"SamplerDynamicWrap"}],"custom_parameter":"Rock"},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":2,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":2,"variable":"rgba","swizzle":"xyz"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """);

        Assert.True(graph.Ready);
        Assert.Equal(3, graph.Nodes.Count);
        ShaderNode sample = graph.Nodes[1];
        Assert.Equal(("SampleTexture", 2, "Rock"), (sample.Type, sample.Index, sample.Custom));
        Assert.Equal("Art/rock.dds", sample.Parameters[0].Path);
        Assert.True(sample.Parameters[0].Srgb);
        Assert.False(sample.Parameters[1].Said);
        Assert.Equal("xyz", graph.Links[1].Source.Swizzle);
        Assert.Equal("Texturing_Init", graph.Links[1].Target.Stage);
    }

    [Fact]
    public void AMATERIALListsItsInstancesInOrderWithTheParametersItSets()
    {
        IReadOnlyList<ShaderInstance> instances = ShaderGraph.Instances(Encoding.UTF8.GetBytes(
            """
            {"version":4,"graphinstances":[
              {"parent":"Metadata/A.fxgraph","custom_parameters":[{"name":"UVTiling","parameters":[{"value":[2.0,2.0]}]}]},
              {"parent":"Metadata/B.fxgraph"}]}
            """));

        Assert.Equal(["Metadata/A.fxgraph", "Metadata/B.fxgraph"], instances.Select(one => one.Parent));
        Assert.Equal([2f, 2f], instances[0].Custom["UVTiling"][0].Numbers);
        Assert.Empty(instances[1].Custom);
    }

    /// <summary>The ordinary material - one sRGB read at the mesh's coordinates - is recognised as its texture.</summary>
    [Fact]
    public void ACOLOURThatIsOnePlainReadIsSaidToBeThatTexture()
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance(), Graph(Plain("Art/rock.dds")))]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(0, compiled.Program.Plain);
        Assert.Equal(new ShadeTexture("Art/rock.dds", true), compiled.Program.Textures[0]);
        Assert.Empty(compiled.Skipped);
    }

    /// <summary>
    /// Coordinates set up at UVSetup reach a read at Texturing_Init, though the material lists that graph second.
    /// </summary>
    /// <remarks>TallDune1c lists PBRGround before OffsetUVTiling, and the tiling is still what PBRGround reads with.</remarks>
    [Fact]
    public void ANDCOORDINATESSetUpAtUVSetupReachTheReadEvenListedAfterIt()
    {
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance(), Graph(Plain("Art/rock.dds"))),
            (Instance(("UVTiling", [Numbers(2f, 2f)])), Graph(Tiling)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Empty(compiled.Skipped);
    }

    /// <summary>Within one graph at one stage, a read sees the coordinates from before that graph wrote them.</summary>
    [Fact]
    public void ANDAGRAPHReadsTheValuesFromBeforeItsOwnWrites()
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance(), Graph(
            """
            {"nodes":[
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"MultiplyConst2","index":0,"parameters":[{"value":3.0}]},
              {"type":"UV","index":0,"stage":"Texturing_Init"},
              {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/rock.dds","srgb":true}]},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"MultiplyConst2","index":0,"variable":"a"}},
              {"src":{"type":"MultiplyConst2","index":0,"variable":"output"},"dst":{"type":"UV","index":0,"stage":"Texturing_Init","variable":"input"}},
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
            """))]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(0, compiled.Program.Plain);
    }

    /// <summary>
    /// A graph whose colour needs a node this does not know is left out whole and named; the colour before it stands.
    /// </summary>
    [Fact]
    public void ANUNKNOWNNodeCostsItsGraphsColourAndIsNamed()
    {
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance(), Graph(Plain("Art/rock.dds"))),
            (Instance("Metadata/Materials/Environment/Dust_simple.fxgraph"), Graph(
                """
                {"nodes":[
                  {"type":"InputWorldPos","index":0,"stage":"PreLighting"},
                  {"type":"Noise31","index":0},
                  {"type":"AlbedoColor","index":0,"stage":"PreLighting"}],
                 "links":[
                  {"src":{"type":"InputWorldPos","index":0,"stage":"PreLighting","variable":"output"},"dst":{"type":"Noise31","index":0,"variable":"pos"}},
                  {"src":{"type":"Noise31","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"PreLighting","variable":"input"}}]}
                """)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(0, compiled.Program.Plain);
        Assert.Equal(["Noise31 in Dust_simple"], compiled.Skipped);
    }

    [Fact]
    public void ANDAMaterialNoneOfWhoseColourCouldBeEvaluatedHasNoProgram()
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance("Metadata/BasicColour.fxgraph"), Graph(
            """
            {"nodes":[
              {"type":"InputVertexColor","index":0,"stage":"VertexInit"},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"InputVertexColor","index":0,"stage":"VertexInit","variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
            """))]);

        Assert.Null(compiled.Program);
        Assert.Equal(["InputVertexColor in BasicColour"], compiled.Skipped);
    }

    /// <summary>A constant colour, worked in linear light, comes out as the sRGB texture of that colour would.</summary>
    [Fact]
    public void ACONSTANTColourIsDrawnAsThePlainTextureOfItsSrgbValue()
    {
        ShadeProgram program = Bound(Compile(Constant(0.5f, 0.2f, 0.05f)), []);
        Assert.Equal(-1, program.Plain);
        GamePicture shaded = MeshPicture.Of(Quad(), 64, shades: [program]);
        GamePicture plain = MeshPicture.Of(Quad(), 64, skins: [Sheet(Srgb(0.5f), Srgb(0.2f), Srgb(0.05f))]);

        AssertClose(plain, shaded);
    }

    /// <summary>
    /// The stromatolite ledge's own arithmetic: two textures mixed by a mask's green channel.
    /// </summary>
    /// <remarks>
    /// THE MASK IS NOT sRGB and the two textures are, which is the case the linear pipeline has to get
    /// right: a full green picks the second texture and none the first, each back exactly as painted.
    /// </remarks>
    [Fact]
    public void ATEXTUREMixIsSteeredByTheMasksChannel()
    {
        Mipmaps edge = Sheet(200, 40, 40);
        Mipmaps inner = Sheet(40, 40, 200);
        ShadeProgram Mixed(byte green) => Bound(Compile(Mix), new()
        {
            ["Art/mask.dds"] = Sheet(0, green, 0),
            ["Art/edge.dds"] = edge,
            ["Art/inner.dds"] = inner,
        });

        AssertClose(MeshPicture.Of(Quad(), 64, skins: [inner]), MeshPicture.Of(Quad(), 64, shades: [Mixed(255)]));
        AssertClose(MeshPicture.Of(Quad(), 64, skins: [edge]), MeshPicture.Of(Quad(), 64, shades: [Mixed(0)]));
    }

    /// <summary>A material's custom parameter overrides the node's own, and the later graph at a stage wins.</summary>
    [Fact]
    public void ACUSTOMParameterOverridesTheNodeAndTheLaterGraphWins()
    {
        ShadeProgram program = Bound(ShadeProgram.Compile(
        [
            (Instance(), Graph(Constant(1f, 0f, 0f))),
            (Instance(("Tint", [Numbers(0f, 0.5f, 0f)])), Graph(Constant(1f, 0f, 0f, custom: "Tint"))),
        ]).Program, []);

        GamePicture shaded = MeshPicture.Of(Quad(), 64, shades: [program]);
        AssertClose(MeshPicture.Of(Quad(), 64, skins: [Sheet(0, Srgb(0.5f), 0)]), shaded);
    }

    /// <summary>A program missing a texture is not drawn with; the shape keeps its skin.</summary>
    [Fact]
    public void APROGRAMWithoutItsTexturesLeavesTheSkin()
    {
        ShadeProgram unbound = Compile(Mix);
        Assert.False(unbound.Bound);

        Mipmaps skin = Sheet(10, 200, 10);
        AssertClose(MeshPicture.Of(Quad(), 64, skins: [skin]), MeshPicture.Of(Quad(), 64, skins: [skin], shades: [unbound]));
    }

    /// <summary>
    /// The stromatolite ledge from the install's own files: its colour is the blend graph's, not the Meshmap texture.
    /// </summary>
    /// <remarks>
    /// THE CASE THIS WAS WRITTEN FOR, from a live 0.5 install's dump - RockyLedgec.mat and the graphs
    /// it names, verbatim, under tests/fixtures/shaders. BasicColour multiplies by a vertex colour
    /// this reader does not have and is named; StromatoliteLedge_Blend compiles whole, reading the
    /// Meshmap the material hands it and the three textures the graph names itself.
    /// </remarks>
    [Fact]
    public void THELEDGEFromTheInstallIsColouredByItsBlendGraph()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/Shore/RockyLedgec.mat");

        Assert.NotNull(compiled.Program);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Equal(["Metadata/Materials/Environment/StromatoliteLedge_Blend.fxgraph"], compiled.Program.Graphs);
        Assert.Equal(["InputVertexColor in BasicColour"], compiled.Skipped);
        // THE MESHMAP IS READ AS THE MATERIAL SAYS - sRGB - over the node's own "srgb": false: an
        // instance's parameters override the graph's defaults, and the material writes true.
        Assert.Equal(
            [
                new ShadeTexture("Art/Textures/Environment/desert/Shore/Rocks_tmdlcgao_colour.dds", true),
                new ShadeTexture("Art/Textures/Environment/desert/Shore/RockyLedge_colour.dds", true),
                new ShadeTexture("Art/Textures/Environment/desert/Shore/StoneSurface_sbhi3ip0_colour.dds", true),
                new ShadeTexture("Art/Textures/masks/GrundgeMask02_Uncompressed.dds", false),
            ],
            compiled.Program.Textures.OrderBy(one => one.Path, StringComparer.Ordinal));
    }

    /// <summary>A cliff from the same install is its colour map, and the dust graph after it is named.</summary>
    [Fact]
    public void ANDACLIFFFromTheInstallIsItsColourMapWithTheDustNamed()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/DesertCliffs/DESERT_Cliff13c.mat");

        Assert.NotNull(compiled.Program);
        Assert.Equal(
            "Art/Textures/Environment/desert/DesertCliffs/DESERT_Cliff13_colour_BC1.dds",
            compiled.Program.Textures[compiled.Program.Plain].Path);
        Assert.Single(compiled.Skipped);
        Assert.EndsWith(" in Dust_simple", compiled.Skipped[0], StringComparison.Ordinal);
    }

    /// <summary>The tall dune: PBRGround's colour at OffsetUVTiling's coordinates, the contact fade named.</summary>
    [Fact]
    public void ANDTHEDUNEFromTheInstallTilesItsColourAndNamesTheFade()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/GroundMaterials/TallDune1c.mat");

        Assert.NotNull(compiled.Program);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Equal(["Metadata/Materials/Ground/PBRGround.fxgraph"], compiled.Program.Graphs);
        Assert.Equal(["InputVertexColor in BasicColour", "MaskedContactFade in MaskedContactFade"], compiled.Skipped);
    }

    /// <summary>
    /// A ground material from the install: PBRGroundBN's colour at half the coordinates, its alpha the One node.
    /// </summary>
    /// <remarks>
    /// EVERY GROUND MATERIAL THE DESERT TILESETS OFFER is an instance of PBRGroundBN, and before
    /// <c>One</c> was known not one of them compiled - the dump said "One in PBRGroundBN" seven times.
    /// The height and normal halves of the graph write channels this reader does not follow, so
    /// nothing is left out of the colour.
    /// </remarks>
    [Fact]
    public void ANDTHESANDFromTheInstallIsItsColourAtHalfTheCoordinates()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01.mat");

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Equal(["Metadata/Materials/Ground/PBRGroundBN.fxgraph"], compiled.Program.Graphs);
        Assert.Equal(
            [new ShadeTexture("Art/Textures/Environment/desert/Shore/Ground/G_VST_Sand01_colour_BC7.dds", true)],
            compiled.Program.Textures);
    }

    // ---- the graphs ----

    /// <summary>OffsetUVTiling, as the game ships it.</summary>
    private const string Tiling =
        """
        {"nodes":[
          {"type":"InputUV","index":0,"stage":"UVSetup"},
          {"type":"ConstantPixel2","index":0,"parameters":[{"value":[1.0,1.0]}],"custom_parameter":"UVTiling"},
          {"type":"Multiply2","index":0},
          {"type":"UV","index":0,"stage":"UVSetup"}],
         "links":[
          {"src":{"type":"ConstantPixel2","index":0,"variable":"output"},"dst":{"type":"Multiply2","index":0,"variable":"b"}},
          {"src":{"type":"InputUV","index":0,"stage":"UVSetup","variable":"output"},"dst":{"type":"Multiply2","index":0,"variable":"a"}},
          {"src":{"type":"Multiply2","index":0,"variable":"output"},"dst":{"type":"UV","index":0,"stage":"UVSetup","variable":"input"}}]}
        """;

    /// <summary>The ledge's mix: lerp(edge, inner, saturate(mask.y)), the mask read without sRGB.</summary>
    private const string Mix =
        """
        {"nodes":[
          {"type":"InputUV","index":0,"stage":"Texturing_Init"},
          {"type":"SampleTexture","index":6,"parameters":[{"path":"Art/mask.dds","srgb":false}]},
          {"type":"SampleTexture","index":3,"parameters":[{"path":"Art/edge.dds","srgb":true}]},
          {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/inner.dds","srgb":true}]},
          {"type":"Saturate","index":0},
          {"type":"Lerp3","index":0},
          {"type":"ConstantBool","index":0,"parameters":[{"value":true}]},
          {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
         "links":[
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":6,"variable":"uv"}},
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":3,"variable":"uv"}},
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
          {"src":{"type":"SampleTexture","index":6,"variable":"rgba","swizzle":"y"},"dst":{"type":"Saturate","index":0,"variable":"input"}},
          {"src":{"type":"SampleTexture","index":3,"variable":"rgba","swizzle":"xyz"},"dst":{"type":"Lerp3","index":0,"variable":"a"}},
          {"src":{"type":"SampleTexture","index":0,"variable":"rgba","swizzle":"xyz"},"dst":{"type":"Lerp3","index":0,"variable":"b"}},
          {"src":{"type":"Saturate","index":0,"variable":"output"},"dst":{"type":"Lerp3","index":0,"variable":"alpha"}},
          {"src":{"type":"ConstantBool","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"w"}},
          {"src":{"type":"Lerp3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
        """;

    /// <summary>DielectricSpecGlossBN's colour: one texture handed in and read at the mesh's coordinates.</summary>
    private static string Plain(string texture) =>
        $$$"""
        {"nodes":[
          {"type":"InputTexture","index":0,"parameters":[{"format":"DXT1","srgb":true,"path":"{{{texture}}}"}],"custom_parameter":"AlbedoTransparency_TEX"},
          {"type":"InputUV","index":0,"stage":"Texturing_Init"},
          {"type":"SampleInputTexture","index":0,"parameters":[{"value":"SamplerDynamicWrap"}]},
          {{{Albedo}}}],
         "links":[
          {"src":{"type":"InputTexture","index":0,"variable":"out_texture"},"dst":{"type":"SampleInputTexture","index":0,"variable":"in_texture"}},
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleInputTexture","index":0,"variable":"uv"}},
          {"src":{"type":"SampleInputTexture","index":0,"variable":"color","swizzle":"w"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"w"}},
          {"src":{"type":"SampleInputTexture","index":0,"variable":"color","swizzle":"xyz"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
        """;

    /// <summary>A constant colour, in linear light, straight into the colour.</summary>
    private static string Constant(float red, float green, float blue, string custom = "") =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$$"""
            {"nodes":[
              {"type":"ConstantPixel3","index":0,"parameters":[{"value":[{{{red}}},{{{green}}},{{{blue}}}]}],"custom_parameter":"{{{custom}}}"},
              {{{Albedo}}}],
             "links":[
              {"src":{"type":"ConstantPixel3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """);

    // ---- helpers ----

    /// <summary>A material and the graphs it names, from tests/fixtures/shaders, compiled.</summary>
    private static ShadeCompile Real(string material)
    {
        string Fixture(string path)
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "shaders", path.Replace("/", "__", StringComparison.Ordinal));
        }

        IReadOnlyList<ShaderInstance> instances = ShaderGraph.Instances(File.ReadAllBytes(Fixture(material)));
        Assert.NotEmpty(instances);
        return ShadeProgram.Compile(
            [.. instances.Select(one => (one, ShaderGraph.Read(File.ReadAllBytes(Fixture(one.Parent)))))]);
    }

    private static ShaderGraph Graph(string json)
    {
        ShaderGraph graph = ShaderGraph.Read(Encoding.UTF8.GetBytes(json));
        Assert.True(graph.Ready);
        return graph;
    }

    private static ShaderInstance Instance(string parent = "Metadata/Test.fxgraph")
        => new(parent, new Dictionary<string, ShaderValue[]>());

    private static ShaderInstance Instance((string Name, ShaderValue[] Values) custom)
        => new("Metadata/Test.fxgraph", new Dictionary<string, ShaderValue[]> { [custom.Name] = custom.Values });

    private static ShaderValue Numbers(params float[] numbers) => new(string.Empty, numbers, null);

    private static ShadeProgram Compile(string graph)
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance(), Graph(graph))]);
        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        return compiled.Program;
    }

    private static ShadeProgram Bound(ShadeProgram? program, Dictionary<string, Mipmaps> sheets)
    {
        Assert.NotNull(program);
        ShadeProgram bound = program.With([.. program.Textures.Select(one => sheets[one.Path])]);
        Assert.True(bound.Bound);
        return bound;
    }

    /// <summary>The sRGB byte for a linear value - what a plain texture would hold.</summary>
    private static byte Srgb(float linear)
    {
        float encoded = linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    /// <summary>One quad facing the camera, its coordinates all on the middle of the texture.</summary>
    private static SkinnedMesh Quad()
    {
        Vector3[] places =
        [
            new(-10f, 0f, -10f),
            new(10f, 0f, -10f),
            new(10f, 0f, 10f),
            new(-10f, 0f, 10f),
        ];
        var normals = new Vector3[4];
        Array.Fill(normals, new Vector3(0f, -1f, 0f));
        var spots = new Vector2[4];
        Array.Fill(spots, new Vector2(0.5f, 0.5f));
        return SkinnedMesh.Of(
            places, normals, [0, 1, 2, 0, 2, 3], new Vector3(-10f, -1f, -10f), new Vector3(10f, 1f, 10f),
            spots, [new MeshShape("Quad", 0, 6)]);
    }

    /// <summary>A texture of one colour. Whether it is read as sRGB is the graph's to say, not the texture's.</summary>
    private static Mipmaps Sheet(byte red, byte green, byte blue)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var one = 0; one < 8 * 8; one++)
        {
            pixels[(one * 4) + 0] = red;
            pixels[(one * 4) + 1] = green;
            pixels[(one * 4) + 2] = blue;
            pixels[(one * 4) + 3] = 255;
        }

        Mipmaps? levels = Mipmaps.Of(new GamePicture(8, 8, pixels));
        Assert.NotNull(levels);
        return levels;
    }

    /// <summary>The two pictures alike within the sRGB table's step, and both actually covered.</summary>
    private static void AssertClose(GamePicture expected, GamePicture actual)
    {
        Assert.Equal(expected.Rgba.Length, actual.Rgba.Length);
        var covered = 0;
        for (var at = 0; at < expected.Rgba.Length; at += 4)
        {
            if (expected.Rgba[at + 3] == 0 && actual.Rgba[at + 3] == 0)
            {
                continue;
            }

            covered++;
            for (var part = 0; part < 4; part++)
            {
                int apart = Math.Abs(expected.Rgba[at + part] - actual.Rgba[at + part]);
                Assert.True(apart <= 2, $"pixel {at / 4} channel {part}: {expected.Rgba[at + part]} expected, {actual.Rgba[at + part]} drawn");
            }
        }

        Assert.True(covered > 100, $"only {covered} pixels covered");
    }
}
