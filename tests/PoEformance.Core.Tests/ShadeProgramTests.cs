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

    /// <summary>
    /// A Vastiri cliff from the install: its colour map with AGT_DesertDust's dust laid over it, the whole of it evaluated.
    /// </summary>
    /// <remarks>
    /// THE CASE THE NODE BATCH WAS FOR, from the crimsonshores dump: VST_Cliff13c.mat and
    /// AGT_DesertDust.fxgraph verbatim. The dust reads the occlusion DielectricSpecGlossBN put in the
    /// indirect light's w, the surface normal through Transform by the TBN basis, the world position
    /// through GroundScroll, and three textures triplanar or tiled - every texture that reaches the
    /// colour, and none of the normal maps, which only feed channels nothing draws.
    /// </remarks>
    [Fact]
    public void ANDTHEVASTIRICLIFFIsDustedByAGTDesertDust()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/DesertCliffs/Vastiri/VST_Cliff13c.mat");

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Equal(
            ["Metadata/Materials/DielectricSpecGlossBN.fxgraph", "Metadata/Materials/Environment/Act2/Gates/AGT_DesertDust.fxgraph"],
            compiled.Program.Graphs);
        Assert.Equal(
            [
                new ShadeTexture("Art/Textures/Environment/desert/DesertCliffs/Vastiri/VST_Cliff13_colour_BC1.dds", true),
                new ShadeTexture("Art/Textures/Environment/desert/DesertCliffs/Vastiri/VST_Cliff13_normal_BC7.dds", false),
                new ShadeTexture("Art/Textures/Environment/desert/Shore/Ground/VST_RockSandStone_MCF_Height_BC4.dds", false),
                new ShadeTexture("Art/Textures/Environment/desert/Shore/Ground/VST_RockSandStone_MCF_colour_BC1.dds", true),
                new ShadeTexture("Art/Textures/General/Grunge/GrungeMap01_mask_BC4.dds", false),
            ],
            compiled.Program.Textures.OrderBy(one => one.Path, StringComparer.Ordinal));
    }

    /// <summary>SmoothStep is the node's own curve - 0.956 here, where HLSL's smoothstep would say otherwise.</summary>
    [Fact]
    public void SMOOTHSTEPIsTheNodesOwnCurve()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.25}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.75}]},
              {"type":"ConstantFloat","index":2,"parameters":[{"value":0.5}]},
              {"type":"SmoothStep","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"SmoothStep","index":0,"variable":"center"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"SmoothStep","index":0,"variable":"steepness"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"SmoothStep","index":0,"variable":"in_value"}},
              {"src":{"type":"SmoothStep","index":0,"variable":"out_value"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.9561f, 0.9561f, 0.9561f);

    /// <summary>
    /// Divide gives nought for a nought divisor and Power takes the base's size, as the fragments say - assembled by CoordsToFloat3.
    /// </summary>
    /// <remarks>The square root of -0.25 is not a number; of its size, a half.</remarks>
    [Fact]
    public void DIVIDEByNoughtIsNoughtAndPowerTakesTheBasesSize()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.3}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.6}]},
              {"type":"ConstantFloat","index":2,"parameters":[{"value":0.5}]},
              {"type":"Zero","index":0},
              {"type":"ConstantFloat","index":4,"parameters":[{"value":-0.25}]},
              {"type":"Half","index":0},
              {"type":"Divide","index":0},
              {"type":"Divide","index":1},
              {"type":"Power","index":0},
              {"type":"CoordsToFloat3","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"Divide","index":0,"variable":"a"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"Divide","index":0,"variable":"b"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"Divide","index":1,"variable":"a"}},
              {"src":{"type":"Zero","index":0,"variable":"output"},"dst":{"type":"Divide","index":1,"variable":"b"}},
              {"src":{"type":"ConstantFloat","index":4,"variable":"output"},"dst":{"type":"Power","index":0,"variable":"base"}},
              {"src":{"type":"Half","index":0,"variable":"output"},"dst":{"type":"Power","index":0,"variable":"exp"}},
              {"src":{"type":"Divide","index":0,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
              {"src":{"type":"Divide","index":1,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
              {"src":{"type":"Power","index":0,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}},
              {"src":{"type":"CoordsToFloat3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.5f, 0f, 0.5f);

    /// <summary>
    /// CheapSmoothstep is HLSL's smoothstep: the cubic between two edges, and equal edges as a graphics card makes of them.
    /// </summary>
    /// <remarks>Above equal edges one, at them nought - nought by nought is not a number, and saturate makes it nought.</remarks>
    [Fact]
    public void CHEAPSMOOTHSTEPIsHLSLsSmoothstep()
        => AssertColour(Coordinates(
            """
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.2}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.6}]},
              {"type":"ConstantFloat","index":2,"parameters":[{"value":0.5}]},
              {"type":"ConstantFloat","index":3,"parameters":[{"value":0.7}]},
              {"type":"CheapSmoothstep","index":0},
              {"type":"CheapSmoothstep","index":1},
              {"type":"CheapSmoothstep","index":2},
            """,
            """
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":0,"variable":"min_value"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":0,"variable":"max_value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":0,"variable":"value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":1,"variable":"min_value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":1,"variable":"max_value"}},
              {"src":{"type":"ConstantFloat","index":3,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":1,"variable":"value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":2,"variable":"min_value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":2,"variable":"max_value"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"CheapSmoothstep","index":2,"variable":"value"}},
            """,
            "CheapSmoothstep"),
            0.84375f, 1f, 0f);

    /// <summary>If takes its greater, lesser or equal input as a stands to b.</summary>
    [Fact]
    public void IFTakesTheBranchAStandsInToB()
    {
        string Branch(int index, int a) =>
            $$$"""
              {"src":{"type":"ConstantFloat","index":{{{a}}},"variable":"output"},"dst":{"type":"If","index":{{{index}}},"variable":"a"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"If","index":{{{index}}},"variable":"b"}},
              {"src":{"type":"ConstantFloat","index":3,"variable":"output"},"dst":{"type":"If","index":{{{index}}},"variable":"greater"}},
              {"src":{"type":"ConstantFloat","index":4,"variable":"output"},"dst":{"type":"If","index":{{{index}}},"variable":"equals"}},
              {"src":{"type":"ConstantFloat","index":5,"variable":"output"},"dst":{"type":"If","index":{{{index}}},"variable":"lesser"}},
            """;

        AssertColour(Coordinates(
            """
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.7}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.3}]},
              {"type":"ConstantFloat","index":2,"parameters":[{"value":0.5}]},
              {"type":"ConstantFloat","index":3,"parameters":[{"value":0.9}]},
              {"type":"ConstantFloat","index":4,"parameters":[{"value":0.4}]},
              {"type":"ConstantFloat","index":5,"parameters":[{"value":0.2}]},
              {"type":"If","index":0},
              {"type":"If","index":1},
              {"type":"If","index":2},
            """,
            Branch(0, 0) + Branch(1, 1) + Branch(2, 2),
            "If"),
            0.9f, 0.2f, 0.4f);
    }

    /// <summary>FitRangeFromInput divides an empty input range by one, as the node does, rather than giving the bottom of the output.</summary>
    [Fact]
    public void FITRANGEDividesAnEmptyRangeByOne()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.75}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.5}]},
              {"type":"ConstantFloat","index":2,"parameters":[{"value":0.25}]},
              {"type":"FitRangeFromInput","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"FitRangeFromInput","index":0,"variable":"value"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"FitRangeFromInput","index":0,"variable":"in_min"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"FitRangeFromInput","index":0,"variable":"in_max"}},
              {"src":{"type":"ConstantFloat","index":2,"variable":"output"},"dst":{"type":"FitRangeFromInput","index":0,"variable":"out_min"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"FitRangeFromInput","index":0,"variable":"out_max"}},
              {"src":{"type":"FitRangeFromInput","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.3125f, 0.3125f, 0.3125f);

    /// <summary>A link from Float3ToCoords' <c>z</c> carries the z alone - the port names the component.</summary>
    [Fact]
    public void ACOORDINATEOutputIsItsComponent()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat3","index":0,"parameters":[{"value":[0.1,0.2,0.3]}]},
              {"type":"Float3ToCoords","index":0},
              {"type":"CoordsToFloat3","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Float3ToCoords","index":0,"variable":"input"}},
              {"src":{"type":"Float3ToCoords","index":0,"variable":"z"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
              {"src":{"type":"Float3ToCoords","index":0,"variable":"x"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
              {"src":{"type":"Float3ToCoords","index":0,"variable":"y"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}},
              {"src":{"type":"CoordsToFloat3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.3f, 0.1f, 0.2f);

    /// <summary>SampleTexture's <c>g</c> is the read's green, spread like any float.</summary>
    [Fact]
    public void ANDATEXTURESGreenIsItsGreen()
        => AssertColour(
            """
            {"nodes":[
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/mask.dds","srgb":false}]},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"g"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            200f / 255f, 200f / 255f, 200f / 255f,
            new() { ["Art/mask.dds"] = Sheet(40, 200, 10) });

    /// <summary>A vector handed to a float input keeps its x, as HLSL cuts it.</summary>
    [Fact]
    public void AFLOATInputKeepsAVectorsX()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat3","index":0,"parameters":[{"value":[0.2,0.5,0.9]}]},
              {"type":"Saturate","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Saturate","index":0,"variable":"input"}},
              {"src":{"type":"Saturate","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.2f, 0.2f, 0.2f);

    /// <summary>Luminance and the legacy GrayScale weigh the channels as their fragments do.</summary>
    [Fact]
    public void LUMINANCEAndGrayScaleWeighTheChannelsTheirOwnWay()
        => AssertColour(
            """
            {"nodes":[
              {"type":"ConstantFloat3","index":0,"parameters":[{"value":[1.0,0.5,0.0]}]},
              {"type":"Luminance","index":0},
              {"type":"GrayScale","index":0},
              {"type":"Zero","index":0},
              {"type":"CoordsToFloat3","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Luminance","index":0,"variable":"input"}},
              {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"GrayScale","index":0,"variable":"in_color"}},
              {"src":{"type":"Luminance","index":0,"variable":"lum"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
              {"src":{"type":"GrayScale","index":0,"variable":"out_color","swizzle":"y"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
              {"src":{"type":"Zero","index":0,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}},
              {"src":{"type":"CoordsToFloat3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
            """,
            0.5925f, 0.5755f, 0f);

    /// <summary>RemapHue turns a colour by the shader's own helper, its 3.1425 for pi and all.</summary>
    [Fact]
    public void REMAPHUETurnsAColourAsTheShaderDoes()
        => AssertColour(RemapHue("""[{"value":120.0},{"value":0.5},{"value":0.5}]"""), 0.0998f, 0.79996f, 0.2002f);

    /// <summary>A value left out whose declared default is not nought is not taken as nought, nor as the default.</summary>
    [Fact]
    public void ANDAVALUELeftOutWhoseDefaultIsNotNoughtIsNotGuessed()
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance(), Graph(RemapHue("""[{"value":120.0},{},{"value":0.5}]""")))]);

        Assert.Null(compiled.Program);
        Assert.Equal(["RemapHue leaving out a value whose default is not nought in Test"], compiled.Skipped);
    }

    /// <summary>
    /// The indirect light's xyz start at nought in every lighting model and can be read; its w is the material's own and cannot.
    /// </summary>
    [Fact]
    public void ACHANNELSPartsNobodySetAreNotRead()
    {
        ShadeCompile black = ShadeProgram.Compile([(Instance(), Graph(Reading("InputIndirectColor", "xyz")))]);
        Assert.NotNull(black.Program);
        Assert.Empty(black.Skipped);

        ShadeCompile occlusion = ShadeProgram.Compile([(Instance(), Graph(Reading("InputIndirectColor", "w")))]);
        Assert.Null(occlusion.Program);
        Assert.Equal(["InputIndirectColor's w before any graph set it in Test"], occlusion.Skipped);
    }

    /// <summary>A channel whose write was left out is lost, and the graph reading it after is named for it, not handed the old value.</summary>
    [Fact]
    public void ALEFTOUTWriteLosesItsChannel()
    {
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance("Metadata/Occluder.fxgraph"), Graph(
                """
                {"nodes":[
                  {"type":"InputWorldPos","index":0,"stage":"Texturing_Init"},
                  {"type":"Noise31","index":0},
                  {"type":"IndirectColor","index":0,"stage":"Texturing_Init"}],
                 "links":[
                  {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"Noise31","index":0,"variable":"pos"}},
                  {"src":{"type":"Noise31","index":0,"variable":"output"},"dst":{"type":"IndirectColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
                """)),
            (Instance("Metadata/Reader.fxgraph"), Graph(Reading("InputIndirectColor", "xyz", "Texturing"))),
        ]);

        Assert.Null(compiled.Program);
        Assert.Equal(["InputIndirectColor after Occluder's was left out in Reader"], compiled.Skipped);
    }

    /// <summary>
    /// Texturing_Calc runs after Texturing_Init - the dust lays itself over the colour - but its order against plain Texturing is not known.
    /// </summary>
    [Fact]
    public void CALCRunsAfterInitButItsOrderAgainstThePlainStageIsNotGuessed()
    {
        ShadeProgram darkened = Bound(ShadeProgram.Compile(
        [
            (Instance("Metadata/Red.fxgraph"), Graph(Constant(1f, 0f, 0f))),
            (Instance("Metadata/Halved.fxgraph"), Graph(Halving("Texturing_Calc"))),
        ]).Program, []);
        Assert.Equal(["Metadata/Red.fxgraph", "Metadata/Halved.fxgraph"], darkened.Graphs);
        AssertClose(MeshPicture.Of(Quad(), 64, skins: [Sheet(Srgb(0.5f), 0, 0)]), MeshPicture.Of(Quad(), 64, shades: [darkened]));

        ShadeCompile unordered = ShadeProgram.Compile(
        [
            (Instance("Metadata/Red.fxgraph"), Graph(Constant(1f, 0f, 0f).Replace("Texturing_Init", "Texturing", StringComparison.Ordinal))),
            (Instance("Metadata/Halved.fxgraph"), Graph(Halving("Texturing_Calc"))),
        ]);
        Assert.NotNull(unordered.Program);
        Assert.Equal(["Metadata/Red.fxgraph"], unordered.Program.Graphs);
        Assert.Equal(["Halved at Texturing_Calc, whose order against Texturing is not known"], unordered.Skipped);
    }

    /// <summary>
    /// Two unordered stages both touching the normal hold back the normal's writes, not a colour that reads no normal.
    /// </summary>
    /// <remarks>AddDetailMap at Texturing beside AGT_DesertDust at Texturing_Calc, cut down to the part that matters.</remarks>
    [Fact]
    public void ANDWHATAColourDoesNotReadDoesNotHoldItBack()
    {
        ShadeProgram dusted = Bound(Checked(ShadeProgram.Compile(
        [
            (Instance("Metadata/Red.fxgraph"), Graph(Constant(1f, 0f, 0f))),
            (Instance("Metadata/Detail.fxgraph"), Graph(Renormalising("Texturing", string.Empty))),
            (Instance("Metadata/Dust.fxgraph"), Graph(Renormalising("Texturing_Calc", Halving("Texturing_Calc")))),
        ])), []);

        Assert.Equal(["Metadata/Red.fxgraph", "Metadata/Dust.fxgraph"], dusted.Graphs);
        AssertClose(MeshPicture.Of(Quad(), 64, skins: [Sheet(Srgb(0.5f), 0, 0)]), MeshPicture.Of(Quad(), 64, shades: [dusted]));
    }

    /// <summary>
    /// Transform of (0, 0, 1) by the TBN basis is the surface normal, and a triplanar read steered by it is the texture's colour.
    /// </summary>
    [Fact]
    public void THETBNBasisNormalSteersATriplanarRead()
        => AssertColour(Triplanar("[0.0,0.0,1.0]", "SampleInputTriplanar", "world_normal"), 0.8f, 0.4f, 0.2f, new() { ["Art/dust.dds"] = Sheet(Srgb(0.8f), Srgb(0.4f), Srgb(0.2f)) });

    /// <summary>The mesh has no tangents, and the basis's normal has no known length: both are refused where they would matter.</summary>
    [Fact]
    public void ANDWHATTheBasisDoesNotSayIsRefused()
    {
        ShadeCompile tangent = ShadeProgram.Compile([(Instance(), Graph(Triplanar("[1.0,0.0,0.0]", "SampleInputTriplanar", "world_normal")))]);
        Assert.Null(tangent.Program);
        Assert.Equal(["Transform of a vector along the tangent or binormal, which the mesh does not have in Test"], tangent.Skipped);

        ShadeCompile length = ShadeProgram.Compile([(Instance(), Graph(Triplanar("[0.0,0.0,1.0]", "Add3", "a")))]);
        Assert.Null(length.Program);
        Assert.Equal(["Add3 with the TBN basis's normal, whose length is not known in Test"], length.Skipped);
    }

    // ---- the graphs ----

    /// <summary>
    /// Three float nodes of one type, #0, #1 and #2, put into the colour's x, y and z by CoordsToFloat3 - with the graph's other nodes and links.
    /// </summary>
    private static string Coordinates(string nodes, string links, string type) =>
        $$$"""
        {"nodes":[
        {{{nodes}}}
          {"type":"CoordsToFloat3","index":0},
          {{{Albedo}}}],
         "links":[
        {{{links}}}
          {"src":{"type":"{{{type}}}","index":0,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
          {"src":{"type":"{{{type}}}","index":1,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
          {"src":{"type":"{{{type}}}","index":2,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}},
          {"src":{"type":"CoordsToFloat3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
        """;

    /// <summary>RemapHue of (0.8, 0.2, 0.1) with the given parameters, straight into the colour.</summary>
    private static string RemapHue(string parameters) =>
        $$$"""
        {"nodes":[
          {"type":"ConstantFloat3","index":0,"parameters":[{"value":[0.8,0.2,0.1]}]},
          {"type":"RemapHue","index":0,"parameters":{{{parameters}}}},
          {{{Albedo}}}],
         "links":[
          {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"RemapHue","index":0,"variable":"color_map"}},
          {"src":{"type":"RemapHue","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
        """;

    /// <summary>A reader's components straight into the colour.</summary>
    private static string Reading(string reader, string swizzle, string stage = "Texturing_Init") =>
        $$$"""
        {"nodes":[
          {"type":"{{{reader}}}","index":0,"stage":"{{{stage}}}"},
          {"type":"AlbedoColor","index":0,"stage":"{{{stage}}}"}],
         "links":[
          {"src":{"type":"{{{reader}}}","index":0,"stage":"{{{stage}}}","variable":"output","swizzle":"{{{swizzle}}}"},"dst":{"type":"AlbedoColor","index":0,"stage":"{{{stage}}}","variable":"input","swizzle":"{{{swizzle}}}"}}]}
        """;

    /// <summary>The colour so far, halved, at a stage.</summary>
    private static string Halving(string stage) =>
        $$$"""
        {"nodes":[
          {"type":"InputAlbedoColor","index":0,"stage":"{{{stage}}}"},
          {"type":"MultiplyConst3","index":0,"parameters":[{"value":0.5}]},
          {"type":"AlbedoColor","index":0,"stage":"{{{stage}}}"}],
         "links":[
          {"src":{"type":"InputAlbedoColor","index":0,"stage":"{{{stage}}}","variable":"output","swizzle":"xyz"},"dst":{"type":"MultiplyConst3","index":0,"variable":"a"}},
          {"src":{"type":"MultiplyConst3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"{{{stage}}}","variable":"input","swizzle":"xyz"}}]}
        """;

    /// <summary>The normal renormalised at a stage, beside whatever other graph's nodes and links are handed in.</summary>
    private static string Renormalising(string stage, string beside)
    {
        string normals =
            $$$"""
            {"type":"InputTbnNormal","index":0,"stage":"{{{stage}}}"},
            {"type":"Normalize3","index":0},
            {"type":"TbnNormal","index":0,"stage":"{{{stage}}}"}
            """;
        string links =
            $$$"""
            {"src":{"type":"InputTbnNormal","index":0,"stage":"{{{stage}}}","variable":"output"},"dst":{"type":"Normalize3","index":0,"variable":"input"}},
            {"src":{"type":"Normalize3","index":0,"variable":"output"},"dst":{"type":"TbnNormal","index":0,"stage":"{{{stage}}}","variable":"input"}}
            """;
        if (beside.Length == 0)
        {
            return $$"""{"nodes":[{{normals}}],"links":[{{links}}]}""";
        }

        ShaderGraph other = Graph(beside);
        Assert.NotEmpty(other.Nodes);
        return beside
            .Replace("\"nodes\":[", "\"nodes\":[" + normals + ",", StringComparison.Ordinal)
            .Replace("\"links\":[", "\"links\":[" + links + ",", StringComparison.Ordinal);
    }

    /// <summary>
    /// Transform of a constant by the TBN basis, handed to a port - AGT_DesertDust's triplanar read when that port is its world_normal.
    /// </summary>
    /// <remarks>The texture, the coordinates and an Add3's other input are linked whichever node it is; a node reads only its own ports.</remarks>
    private static string Triplanar(string vector, string type, string port) =>
        $$$"""
        {"nodes":[
          {"type":"InputWorldPos","index":0,"stage":"Texturing_Init"},
          {"type":"InputTexture","index":0,"parameters":[{"path":"Art/dust.dds","srgb":true}]},
          {"type":"ConstantFloat3","index":0,"parameters":[{"value":{{{vector}}}}]},
          {"type":"InputTbnBasis","index":0,"stage":"Texturing_Init"},
          {"type":"Transform","index":0},
          {"type":"Dummy3","index":0},
          {"type":"{{{type}}}","index":0},
          {{{Albedo}}}],
         "links":[
          {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Transform","index":0,"variable":"input"}},
          {"src":{"type":"InputTbnBasis","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"Transform","index":0,"variable":"inmatrix"}},
          {"src":{"type":"Transform","index":0,"variable":"output"},"dst":{"type":"Dummy3","index":0,"variable":"in_value"}},
          {"src":{"type":"InputTexture","index":0,"variable":"out_texture"},"dst":{"type":"{{{type}}}","index":0,"variable":"in_texture"}},
          {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"{{{type}}}","index":0,"variable":"uv"}},
          {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"{{{type}}}","index":0,"variable":"b"}},
          {"src":{"type":"Dummy3","index":0,"variable":"out_value"},"dst":{"type":"{{{type}}}","index":0,"variable":"{{{port}}}"}},
          {"src":{"type":"{{{type}}}","index":0,"variable":"{{{(type == "Add3" ? "output" : "color")}}}","swizzle":"xyz"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"xyz"}}]}
        """;

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

    /// <summary>A compile's program, which must exist and have left nothing out.</summary>
    private static ShadeProgram Checked(ShadeCompile compiled)
    {
        Assert.Empty(compiled.Skipped);
        Assert.NotNull(compiled.Program);
        return compiled.Program;
    }

    private static ShadeProgram Compile(string graph)
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance(), Graph(graph))]);
        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        return compiled.Program;
    }

    /// <summary>A graph's colour, drawn, against the plain texture of the linear colour it should come to.</summary>
    private static void AssertColour(string graph, float red, float green, float blue, Dictionary<string, Mipmaps>? sheets = null)
    {
        ShadeProgram program = Bound(Compile(graph), sheets ?? []);
        AssertClose(
            MeshPicture.Of(Quad(), 64, skins: [Sheet(Srgb(red), Srgb(green), Srgb(blue))]),
            MeshPicture.Of(Quad(), 64, shades: [program]));
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
