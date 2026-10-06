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
                  {"type":"DepthDistance","index":0},
                  {"type":"AlbedoColor","index":0,"stage":"PreLighting"}],
                 "links":[
                  {"src":{"type":"InputWorldPos","index":0,"stage":"PreLighting","variable":"output"},"dst":{"type":"DepthDistance","index":0,"variable":"world_pos"}},
                  {"src":{"type":"DepthDistance","index":0,"variable":"distance"},"dst":{"type":"AlbedoColor","index":0,"stage":"PreLighting","variable":"input"}}]}
                """)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(0, compiled.Program.Plain);
        Assert.Equal(["DepthDistance in Dust_simple"], compiled.Skipped);
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

    /// <summary>
    /// A cliff from the same install: its colour map with Dust_simple's dust over it, the whole of it evaluated.
    /// </summary>
    /// <remarks>
    /// THE DUST WAS NAMED FOR THREE THINGS IN TURN - the indirect light, Noise31, the vertex's local
    /// position - and reads the occlusion DielectricSpecGlossBN puts in the indirect light's w, which
    /// is why that graph's normal map is among the textures.
    /// </remarks>
    [Fact]
    public void ANDACLIFFFromTheInstallIsDustedByDustSimple()
    {
        ShadeCompile compiled = Real("Art/Textures/Environment/desert/DesertCliffs/DESERT_Cliff13c.mat");

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(-1, compiled.Program.Plain);
        Assert.Equal(
            ["Metadata/Materials/DielectricSpecGlossBN.fxgraph", "Metadata/Materials/Environment/Dust_simple.fxgraph"],
            compiled.Program.Graphs);
        Assert.Equal(
            [
                new ShadeTexture("Art/Textures/Environment/desert/DesertCliffs/DESERT_Cliff13_colour_BC1.dds", true),
                new ShadeTexture("Art/Textures/Environment/desert/DesertCliffs/DESERT_Cliff13_normal_BC7.dds", false),
            ],
            compiled.Program.Textures);
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

    /// <summary>
    /// Noise31 and PerlinNoise31 are the declarations' own noises, to the hash - the second Perlin cell lies below nought, where the integer cast wraps.
    /// </summary>
    [Fact]
    public void THENOISESAreTheDeclarationsOwn()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat3","index":0,"parameters":[{"value":[1.3,2.7,0.4]}]},
                  {"type":"ConstantFloat3","index":1,"parameters":[{"value":[-3.6,0.25,5.9]}]},
                  {"type":"Noise31","index":0},
                  {"type":"PerlinNoise31","index":0},
                  {"type":"PerlinNoise31","index":1},
                  {"type":"CoordsToFloat3","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Noise31","index":0,"variable":"pos"}},
                  {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"PerlinNoise31","index":0,"variable":"pos"}},
                  {"src":{"type":"ConstantFloat3","index":1,"variable":"output"},"dst":{"type":"PerlinNoise31","index":1,"variable":"pos"}},
                  {"src":{"type":"Noise31","index":0,"variable":"noise"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
                  {"src":{"type":"PerlinNoise31","index":0,"variable":"val"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
                  {"src":{"type":"PerlinNoise31","index":1,"variable":"val"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}}
                """,
                "CoordsToFloat3", "output"),
            0.3105f, 0.5318f, 0.3478f);

    /// <summary>Vibrance lifts its value by a cubic and raises it to one over each colour component.</summary>
    [Fact]
    public void VIBRANCERaisesTheLiftToEachComponent()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat","index":0,"parameters":[{"value":0.6}]},
                  {"type":"ConstantFloat3","index":0,"parameters":[{"value":[1.0,0.5,2.0]}]},
                  {"type":"Vibrance","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"Vibrance","index":0,"variable":"val"}},
                  {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"Vibrance","index":0,"variable":"color"}}
                """,
                "Vibrance", "emission"),
            0.648f, 0.4199f, 0.805f);

    /// <summary>Rotate turns coordinates about nought and RotateUV about its centre - which, left out, is its declared half.</summary>
    [Fact]
    public void ROTATEAndRotateUVTurnTheCoordinates()
    {
        AssertColour(Turning("Rotate", string.Empty), 0.4632f, 0.4307f, 0f);
        AssertColour(Turning("RotateUV", """{"value":[0.5,0.5]}"""), 0.2847f, 0.7316f, 0f);
        AssertColour(Turning("RotateUV", "{}"), 0.2847f, 0.7316f, 0f);
    }

    /// <summary>RadiusToPolarNorm gives the length and the angle as nought to one, by the node's own 3.1415.</summary>
    [Fact]
    public void RADIUSTOPOLARGivesTheLengthAndTheAngle()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat2","index":0,"parameters":[{"value":[0.3,0.4]}]},
                  {"type":"RadiusToPolarNorm","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"RadiusToPolarNorm","index":0,"variable":"radius"}}
                """,
                "RadiusToPolarNorm", "polar", "xy"),
            0.5f, 0.6476f, 0f);

    /// <summary>
    /// A model drawn where it stands has its origin at nought and a scale of one - and a link's swizzle is of the output it names.
    /// </summary>
    [Fact]
    public void THEMODELOriginIsNoughtAndItsScaleOne()
        => AssertColour(
            Colouring(
                """
                  {"type":"ModelOrigin","index":0},
                  {"type":"Half","index":0},
                  {"type":"CoordsToFloat3","index":0}
                """,
                """
                  {"src":{"type":"ModelOrigin","index":0,"variable":"scale"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
                  {"src":{"type":"ModelOrigin","index":0,"variable":"model_origin","swizzle":"y"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
                  {"src":{"type":"Half","index":0,"variable":"output"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}}
                """,
                "CoordsToFloat3", "output"),
            1f, 0f, 0.5f);

    /// <summary>The vertex world position is the model's; its w, which no file says, is refused.</summary>
    [Fact]
    public void THEVERTEXWorldPositionIsTheModelsButNotItsW()
    {
        // THE QUAD STANDS IN y = 0, so its y is black everywhere.
        AssertColour(WorldPositioned("y"), 0f, 0f, 0f);

        ShadeCompile whole = ShadeProgram.Compile([(Instance(), Graph(WorldPositioned(string.Empty)))]);
        Assert.Null(whole.Program);
        Assert.Equal(["FromVertexWorldPos's w, which no file says in Test"], whole.Skipped);
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

    /// <summary>
    /// A value left out is the fragment's declared default: RemapHue's saturation a half, FitRange's range nought to 255 both ways.
    /// </summary>
    /// <remarks>A half mapped from [0, 255] to [0, 255] is a half; as nought the second range would be [0, 0], and the result nought.</remarks>
    [Fact]
    public void ANDAVALUELeftOutIsTheFragmentsDeclaredDefault()
    {
        AssertColour(RemapHue("""[{"value":120.0},{},{"value":0.5}]"""), 0.0998f, 0.79996f, 0.2002f);
        AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat","index":0,"parameters":[{"value":0.5}]},
                  {"type":"FitRange","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"FitRange","index":0,"variable":"value"}}
                """,
                "FitRange", "output"),
            0.5f, 0.5f, 0.5f);
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

        ShadeCompile occlusion = ShadeProgram.Compile([(Instance(), Graph(Reading("InputIndirectColor", "w", into: "xyz")))]);
        Assert.Null(occlusion.Program);
        Assert.Equal(["InputIndirectColor's w, which nothing has set in Test"], occlusion.Skipped);
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
                  {"type":"DepthDistance","index":0},
                  {"type":"IndirectColor","index":0,"stage":"Texturing_Init"}],
                 "links":[
                  {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"DepthDistance","index":0,"variable":"world_pos"}},
                  {"src":{"type":"DepthDistance","index":0,"variable":"distance"},"dst":{"type":"IndirectColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
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

    /// <summary>LookUpTexture reads a row of its texture by the colour's alpha and scales it by its multiplier - declared one, left out.</summary>
    [Fact]
    public void LOOKUPTEXTUREReadsARowByTheAlpha()
    {
        var sheets = new Dictionary<string, Mipmaps> { ["Art/lut.dds"] = Sheet(Srgb(0.6f), Srgb(0.2f), Srgb(0.1f)) };
        AssertColour(LookingUp("{}", "rgb", "xyz"), 0.6f, 0.2f, 0.1f, sheets);
        AssertColour(LookingUp("""{"value":0.5}""", "rgb", "xyz"), 0.3f, 0.1f, 0.05f, sheets);
        AssertColour(LookingUp("{}", "g", string.Empty), 0.2f, 0.2f, 0.2f, sheets);

        ShadeCompile unset = ShadeProgram.Compile([(Instance(), Graph(LookingUp("{}", "a", string.Empty)))]);
        Assert.Null(unset.Program);
        Assert.Equal(["LookUpTexture's a, which the fragment never sets in Test"], unset.Skipped);
    }

    /// <summary>
    /// The vertex position and normal are the mesh's own, the position with a w of one - at either transform stage, and through uv9.
    /// </summary>
    /// <remarks>The quad lies in y = 0 facing -y, so its position's y is nought and its normal's y, made positive, one.</remarks>
    [Fact]
    public void THEVERTEXPositionAndNormalAreTheMeshsOwn()
        => AssertColour(
            Colouring(
                """
                  {"type":"InputVertexPosition","index":0,"stage":"LocalTransform"},
                  {"type":"FromVertexLocalPosition","index":0},
                  {"type":"InputVertexNormal","index":0,"stage":"WorldTransform"},
                  {"type":"Abs3","index":0},
                  {"type":"CoordsToFloat3","index":0}
                """,
                """
                  {"src":{"type":"InputVertexPosition","index":0,"stage":"LocalTransform","variable":"output","swizzle":"w"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"x"}},
                  {"src":{"type":"FromVertexLocalPosition","index":0,"variable":"output","swizzle":"y"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"y"}},
                  {"src":{"type":"InputVertexNormal","index":0,"stage":"WorldTransform","variable":"output"},"dst":{"type":"Abs3","index":0,"variable":"input"}},
                  {"src":{"type":"Abs3","index":0,"variable":"output","swizzle":"y"},"dst":{"type":"CoordsToFloat3","index":0,"variable":"z"}}
                """,
                "CoordsToFloat3", "output"),
            1f, 0f, 1f);

    /// <summary>A graph that moves the vertices at a vertex stage makes every position after it not the mesh's, and those readers are refused.</summary>
    [Fact]
    public void ANDAFTERAGraphMovedTheVerticesTheyAreNotRead()
    {
        const string mover =
            """
            {"nodes":[
              {"type":"InputVertexPosition","index":0,"stage":"WorldTransform"},
              {"type":"ConstantFloat4","index":0,"parameters":[{"value":[0.0,0.0,10.0,0.0]}]},
              {"type":"Add4","index":0},
              {"type":"VertexPosition","index":0,"stage":"WorldTransform"}],
             "links":[
              {"src":{"type":"InputVertexPosition","index":0,"stage":"WorldTransform","variable":"output"},"dst":{"type":"Add4","index":0,"variable":"a"}},
              {"src":{"type":"ConstantFloat4","index":0,"variable":"output"},"dst":{"type":"Add4","index":0,"variable":"b"}},
              {"src":{"type":"Add4","index":0,"variable":"output"},"dst":{"type":"VertexPosition","index":0,"stage":"WorldTransform","variable":"input"}}]}
            """;
        string reader = Colouring(
            """
              {"type":"FromVertexLocalPosition","index":0},
              {"type":"Dummy3","index":0}
            """,
            """
              {"src":{"type":"FromVertexLocalPosition","index":0,"variable":"output","swizzle":"xyz"},"dst":{"type":"Dummy3","index":0,"variable":"in_value"}}
            """,
            "Dummy3", "out_value");

        ShadeCompile compiled = ShadeProgram.Compile([(Instance("Metadata/Mover.fxgraph"), Graph(mover)), (Instance("Metadata/Reader.fxgraph"), Graph(reader))]);

        Assert.Null(compiled.Program);
        Assert.Equal(["FromVertexLocalPosition after Mover moved the vertices in Reader"], compiled.Skipped);
    }

    /// <summary>
    /// A colour's w is compiled apart: a w this cannot work out costs the w alone, a later reader of that w is refused, and a writer with only a w is refused.
    /// </summary>
    [Fact]
    public void ACOLOURSWIsCompiledApartFromItsXyz()
    {
        ShadeCompile faded = ShadeProgram.Compile([(Instance(), Graph(Faded))]);
        Assert.NotNull(faded.Program);
        Assert.Empty(faded.Skipped);

        ShadeCompile read = ShadeProgram.Compile(
        [
            (Instance(), Graph(Faded)),
            (Instance("Metadata/Reader.fxgraph"), Graph(Reading("InputAlbedoColor", "w", "Texturing", "xyz"))),
        ]);
        Assert.NotNull(read.Program);
        Assert.Equal(["InputAlbedoColor's w, which nothing has set in Reader"], read.Skipped);

        ShadeCompile alone = ShadeProgram.Compile([(Instance(), Graph(
            """
            {"nodes":[
              {"type":"DepthDistance","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
             "links":[
              {"src":{"type":"DepthDistance","index":0,"variable":"distance"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"w"}}]}
            """))]);
        Assert.Null(alone.Program);
        Assert.Equal(["AlbedoColor with its w alone, whose xyz are then not written down in Test"], alone.Skipped);
    }

    /// <summary>
    /// Dust_lookup from the install, behind a base that writes the colour and the occlusion: whole, through the vertex's local position and the lookup.
    /// </summary>
    [Fact]
    public void THEDUSTLookupFromTheInstallCompilesWhole()
    {
        const string graph = "Metadata/Materials/Environment/Dust_lookup.fxgraph";
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance("Metadata/Base.fxgraph"), Graph(Occluding("Art/base.dds"))),
            (Instance(graph, ("08_lookupTexture", [new ShaderValue("Art/lut.dds", [], true), ShaderValue.Empty, ShaderValue.Empty, ShaderValue.Empty])), FixtureGraph(graph)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(["Metadata/Base.fxgraph", graph], compiled.Program.Graphs);
        Assert.Contains(new ShadeTexture("Art/lut.dds", true), compiled.Program.Textures);
    }

    /// <summary>
    /// MaterialBlend from the install - the terrain blend the hideout furniture wears - behind a base that writes the colour
    /// and the occlusion: whole, through SampleTriplanar, SampleTexture2, MuddleTex and a variance of nought.
    /// </summary>
    [Fact]
    public void ANDTHEMATERIALBLENDFromTheInstallCompilesWhole()
    {
        const string graph = "Metadata/Materials/Environment/MaterialBlend.fxgraph";
        const string material = "art/models/terrain/doodads/hideouts/atlantis/deepwater/textures/atlantisdeepwaterbootsc.mat";
        ShaderInstance asShipped = FixtureInstance(material, graph);

        // AS THE MATERIAL SHIPS, its SampleTriplanar has no texture: the graph's node names none and
        // the material's Triplanar_noise is not among its parameters. What the engine reads then is
        // not in any file, so the graph is left out and says why.
        ShadeCompile bare = ShadeProgram.Compile(
        [
            (Instance("Metadata/Base.fxgraph"), Graph(Occluding("Art/base.dds"))),
            (asShipped, FixtureGraph(graph)),
        ]);
        Assert.Equal(["SampleTriplanar naming no texture in MaterialBlend"], bare.Skipped);

        // WITH THE NOISE NAMED, as a material that sets it would: whole.
        var custom = new Dictionary<string, ShaderValue[]>(asShipped.Custom)
        {
            ["Triplanar_noise"] = [new ShaderValue("Art/triplanar_noise.dds", [], true), ShaderValue.Empty],
        };
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance("Metadata/Base.fxgraph"), Graph(Occluding("Art/base.dds"))),
            (new ShaderInstance(graph, custom), FixtureGraph(graph)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(["Metadata/Base.fxgraph", graph], compiled.Program.Graphs);
        Assert.Contains(new ShadeTexture("Art/triplanar_noise.dds", true), compiled.Program.Textures);
        Assert.Contains(new ShadeTexture("Art/particles/distortion/muddle_double.dds", false), compiled.Program.Textures);
    }

    /// <summary>The ZProject blend from the install, behind a plain colour: whole, through the vertex normal and position it masks by.</summary>
    [Fact]
    public void ANDTHEZPROJECTBlendFromTheInstallCompilesWhole()
    {
        const string graph = "Metadata/LEGACY/Materials/Environment/MaterialBlends/MaterialBlend_ZProject_V01.fxgraph";
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (Instance(), Graph(Plain("Art/rock.dds"))),
            (Instance(graph), FixtureGraph(graph)),
        ]);

        Assert.NotNull(compiled.Program);
        Assert.Empty(compiled.Skipped);
        Assert.Equal(["Metadata/Test.fxgraph", graph], compiled.Program.Graphs);
        Assert.Contains(new ShadeTexture("Art/Models/Terrain/Jungle/Tiles/Jungle/Textures/JungleMoss01_colour_DXT1.dds", true), compiled.Program.Textures);
    }

    // ---- the graphs ----

    /// <summary>A texture's xyz into the colour, and the depth behind the pixel - which this cannot read - into its w.</summary>
    private const string Faded =
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

    /// <summary>A base graph: a texture as the whole colour, and an occlusion of 0.7 in the indirect light's w.</summary>
    [Fact]
    public void SAMPLETRIPLANAROwnsItsTextureAndBlendsByTheNormalsAbsoluteParts()
    {
        // The normal in parts, as MaterialBlend feeds it - and whichever way it points, three reads of
        // one texture blended by weights that sum to one are that texture.
        string graph = Colouring(
            """
              {"type":"InputWorldPos","index":0,"stage":"Texturing_Init"},
              {"type":"InputWorldNormal","index":0,"stage":"Texturing_Init"},
              {"type":"SampleTriplanar","index":0,"parameters":[{"path":"Art/own.dds","srgb":true},{}]}
            """,
            """
              {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTriplanar","index":0,"variable":"uv"}},
              {"src":{"type":"InputWorldNormal","index":0,"stage":"Texturing_Init","variable":"output","swizzle":"x"},"dst":{"type":"SampleTriplanar","index":0,"variable":"world_normal","swizzle":"x"}},
              {"src":{"type":"InputWorldNormal","index":0,"stage":"Texturing_Init","variable":"output","swizzle":"y"},"dst":{"type":"SampleTriplanar","index":0,"variable":"world_normal","swizzle":"y"}},
              {"src":{"type":"InputWorldNormal","index":0,"stage":"Texturing_Init","variable":"output","swizzle":"z"},"dst":{"type":"SampleTriplanar","index":0,"variable":"world_normal","swizzle":"z"}}
            """,
            "SampleTriplanar", "rgb", "xyz");
        ShadeProgram program = Compile(graph);
        Assert.Equal(["Art/own.dds"], program.Textures.Select(one => one.Path));
        Assert.Equal(3, program.Samples);
        AssertColour(graph, 0.8f, 0.4f, 0.2f, new() { ["Art/own.dds"] = Sheet(Srgb(0.8f), Srgb(0.4f), Srgb(0.2f)) });
    }

    [Fact]
    public void SAMPLETEXTURE2ReadsOnceAtEachCoordinateAndTheReadNobodyTakesIsDropped()
    {
        string first = Owning(
            "SampleTexture2", "rgba0", "xyz",
            ports: """
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture2","index":0,"variable":"uv1"}}
            """).Replace("\"variable\":\"uv\"}", "\"variable\":\"uv0\"}", StringComparison.Ordinal);
        ShadeProgram program = Compile(first);
        Assert.Equal(1, program.Samples);
        AssertColour(first, 0.3f, 0.6f, 0.9f, new() { ["Art/own.dds"] = Sheet(Srgb(0.3f), Srgb(0.6f), Srgb(0.9f)) });

        string second = first.Replace("\"variable\":\"rgba0\"", "\"variable\":\"rgba1\"", StringComparison.Ordinal);
        AssertColour(second, 0.3f, 0.6f, 0.9f, new() { ["Art/own.dds"] = Sheet(Srgb(0.3f), Srgb(0.6f), Srgb(0.9f)) });

        string third = first.Replace("\"variable\":\"rgba0\"", "\"variable\":\"rgba2\"", StringComparison.Ordinal);
        ShadeCompile refused = ShadeProgram.Compile([(Instance(), Graph(third))]);
        Assert.Null(refused.Program);
        Assert.Contains(refused.Skipped, one => one.Contains("SampleTexture2 has no output called rgba2", StringComparison.Ordinal));
    }

    [Fact]
    public void SAMPLETEXTUREATLAS2ReadsTheHalvesOfOneSheet()
    {
        string graph = Owning("SampleTextureAtlas2", "rgba1", "xyz");
        ShadeProgram program = Compile(graph);
        Assert.Equal(1, program.Samples);
        AssertColour(graph, 0.3f, 0.6f, 0.9f, new() { ["Art/own.dds"] = Sheet(Srgb(0.3f), Srgb(0.6f), Srgb(0.9f)) });
    }

    [Fact]
    public void SAMPLETEXTURELODReadsItsOwnTextureAtTheLevelGiven()
    {
        string graph = Owning(
            "SampleTextureLod", "rgba", "xyz",
            ports: """
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"SampleTextureLod","index":0,"variable":"lod"}}
            """,
            nodes: """{"type":"ConstantFloat","index":0,"parameters":[{"value":1.0}]},""");
        ShadeProgram program = Compile(graph);
        Assert.Equal(0, program.Samples);
        AssertColour(graph, 0.3f, 0.6f, 0.9f, new() { ["Art/own.dds"] = Sheet(Srgb(0.3f), Srgb(0.6f), Srgb(0.9f)) });
    }

    [Fact]
    public void SAMPLEDISPERSEDTEXTURETakesEachChannelFromItsOwnRead()
    {
        string graph = Colouring(
            """
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"InputTexture","index":0,"parameters":[{"path":"Art/dust.dds","srgb":true}]},
              {"type":"ConstantFloat2","index":0,"parameters":[{"value":[0.0,0.0]}]},
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.0}]},
              {"type":"SampleDispersedTexture","index":0,"parameters":[{}]}
            """,
            """
              {"src":{"type":"InputTexture","index":0,"variable":"out_texture"},"dst":{"type":"SampleDispersedTexture","index":0,"variable":"tex"}},
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleDispersedTexture","index":0,"variable":"uv"}},
              {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"SampleDispersedTexture","index":0,"variable":"uv_dispersion"}},
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"SampleDispersedTexture","index":0,"variable":"mip_level"}}
            """,
            "SampleDispersedTexture", "dispersed_color", "xyz");
        AssertColour(graph, 0.8f, 0.4f, 0.2f, new() { ["Art/dust.dds"] = Sheet(Srgb(0.8f), Srgb(0.4f), Srgb(0.2f)) });
    }

    [Fact]
    public void MUDDLETEXPushesTheCoordinatesByItsReadAndAVarianceOfNought()
    {
        // A white muddle texture, read linear, is one: uv + (1 - 0.5) * 0.4 on x and y, and z has nothing.
        AssertColour(Muddling(""",{"value":2.0},{},{"value":0.4}"""), 0.7f, 0.7f, 0f, new() { ["Art/muddle.dds"] = Sheet(255, 255, 255) });

        // The declared defaults: a frequency of one, no scroll, no intensity - the coordinates as they were.
        AssertColour(Muddling(string.Empty), 0.5f, 0.5f, 0f, new() { ["Art/muddle.dds"] = Sheet(255, 255, 255) });
    }

    [Fact]
    public void ANDAMuddleThatScrollsWithTimeIsRefused()
    {
        ShadeCompile refused = ShadeProgram.Compile([(Instance(), Graph(Muddling(""",{"value":2.0},{"value":[0.1,0.0]},{"value":0.4}""")))]);
        Assert.Null(refused.Program);
        Assert.Contains(refused.Skipped, one => one.Contains("MuddleTex scrolling with time", StringComparison.Ordinal));

        ShadeCompile still = ShadeProgram.Compile([(Instance(), Graph(Muddling(""",{"value":2.0},{"value":[0.0,0.0]},{"value":0.4}""")))]);
        Assert.NotNull(still.Program);
    }

    [Fact]
    public void MUDDLETEXFROMINPUTTakesItsNumbersFromPortsAndRefusesAScrollThere()
    {
        static string Graph_(string scroll) => Colouring(
            $$$"""
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"Zero","index":0},
              {"type":"ConstantFloat","index":0,"parameters":[{"value":2.0}]},
              {"type":"ConstantFloat2","index":0,"parameters":[{"value":{{{scroll}}}}]},
              {"type":"ConstantFloat","index":1,"parameters":[{"value":0.4}]},
              {"type":"MuddleTexFromInput","index":0,"parameters":[{"path":"Art/muddle.dds","srgb":false},{}]}
            """,
            """
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"MuddleTexFromInput","index":0,"variable":"in_uv"}},
              {"src":{"type":"Zero","index":0,"variable":"output"},"dst":{"type":"MuddleTexFromInput","index":0,"variable":"flow_variance"}},
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"MuddleTexFromInput","index":0,"variable":"flow_frequency"}},
              {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"MuddleTexFromInput","index":0,"variable":"flow_scroll"}},
              {"src":{"type":"ConstantFloat","index":1,"variable":"output"},"dst":{"type":"MuddleTexFromInput","index":0,"variable":"flow_intensity"}}
            """,
            "MuddleTexFromInput", "out_uv", "xy");

        AssertColour(Graph_("[0.0,0.0]"), 0.7f, 0.7f, 0f, new() { ["Art/muddle.dds"] = Sheet(255, 255, 255) });

        ShadeCompile refused = ShadeProgram.Compile([(Instance(), Graph(Graph_("[0.0,0.2]")))]);
        Assert.Null(refused.Program);
        Assert.Contains(refused.Skipped, one => one.Contains("MuddleTexFromInput scrolling with time", StringComparison.Ordinal));
    }

    [Fact]
    public void MUDDLETEX2AddsTwoMuddlesAndRefusesAScrollInEither()
    {
        static string Graph_(string parameters) => Owning("MuddleTex2", "out_uv", "xy", parameters: parameters)
            .Replace("\"variable\":\"uv\"}", "\"variable\":\"in_uv\"}", StringComparison.Ordinal);

        // Both at frequency one and intensity 0.4 over a white, linear texture: uv + 0.2 + 0.2.
        AssertColour(Graph_(""",{"value":[1.0,0.0,0.0,0.4]},{"value":[1.0,0.0,0.0,0.4]}"""), 0.9f, 0.9f, 0f, new() { ["Art/own.dds"] = Sheet(255, 255, 255) });

        // Declared "1 0 0 0" twice: no intensity, so the coordinates as they were.
        AssertColour(Graph_(string.Empty), 0.5f, 0.5f, 0f, new() { ["Art/own.dds"] = Sheet(255, 255, 255) });

        ShadeCompile refused = ShadeProgram.Compile([(Instance(), Graph(Graph_(""",{},{"value":[1.0,0.0,0.3,0.4]}""")))]);
        Assert.Null(refused.Program);
        Assert.Contains(refused.Skipped, one => one.Contains("MuddleTex2 scrolling with time", StringComparison.Ordinal));
    }

    [Fact]
    public void FROMVERTEXVARIANCEIsNoughtOnAMesh()
        => AssertColour(Colouring("""{"type":"FromVertexVariance","index":0}""", string.Empty, "FromVertexVariance", "output"), 0f, 0f, 0f);

    [Fact]
    public void RGBTOTBNUnpacksAColourToAVector()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat3","index":0,"parameters":[{"value":[0.75,0.5,0.25]}]},
                  {"type":"RGBToTbn","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"RGBToTbn","index":0,"variable":"rgb_vec"}}
                """,
                "RGBToTbn", "tbn_vec", "xyz"),
            0.5f, 0f, 0f);

    [Fact]
    public void SCALEUVMAYAScalesAboutAPivotWhoseVRunsTheOtherWay()
        => AssertColour(
            Colouring(
                """
                  {"type":"InputUV","index":0,"stage":"Texturing_Init"},
                  {"type":"ConstantFloat2","index":0,"parameters":[{"value":[2.0,3.0]}]},
                  {"type":"ScaleUVMaya","index":0,"parameters":[{"value":[0.25,0.5]}]}
                """,
                """
                  {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"ScaleUVMaya","index":0,"variable":"in_uv"}},
                  {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"ScaleUVMaya","index":0,"variable":"scale"}}
                """,
                "ScaleUVMaya", "out_uv", "xy"),
            0.75f, 0.5f, 0f);

    [Fact]
    public void HARDLIGHTBLENDMultipliesBelowAHalfAndScreensAbove()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat3","index":0,"parameters":[{"value":[0.5,0.5,0.2]}]},
                  {"type":"ConstantFloat3","index":1,"parameters":[{"value":[0.25,0.75,0.5]}]},
                  {"type":"HardLightBlend","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat3","index":0,"variable":"output"},"dst":{"type":"HardLightBlend","index":0,"variable":"color1"}},
                  {"src":{"type":"ConstantFloat3","index":1,"variable":"output"},"dst":{"type":"HardLightBlend","index":0,"variable":"color2"}}
                """,
                "HardLightBlend", "res_color", "xyz"),
            0.25f, 0.75f, 0.2f);

    [Fact]
    public void SINEIsAFloatSpread()
        => AssertColour(
            Colouring(
                """
                  {"type":"ConstantFloat","index":0,"parameters":[{"value":0.5}]},
                  {"type":"Sine","index":0}
                """,
                """
                  {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"Sine","index":0,"variable":"input"}}
                """,
                "Sine", "output"),
            MathF.Sin(0.5f), MathF.Sin(0.5f), MathF.Sin(0.5f));

    [Fact]
    public void ROTATEUVOLDTurnsAboutTheQuadrantsHalfPointAndRefusesATurnWithTime()
    {
        // (0.2, 0.6) less (0.5, 0.5), turned by half a radian the node's way, and put back.
        const float cos = 0.87758256f, sin = 0.47942554f;
        AssertColour(
            Turning("RotateUVOld", """{"value":[0.0,0.5]}"""),
            (-0.3f * cos) - (0.1f * sin) + 0.5f, (-0.3f * sin) + (0.1f * cos) + 0.5f, 0f);

        ShadeCompile refused = ShadeProgram.Compile([(Instance(), Graph(Turning("RotateUVOld", """{"value":[1.0,0.5]}""")))]);
        Assert.Null(refused.Program);
        Assert.Contains(refused.Skipped, one => one.Contains("RotateUVOld turning with time", StringComparison.Ordinal));
    }

    private static string Occluding(string texture) =>
        $$$"""
        {"nodes":[
          {"type":"InputUV","index":0,"stage":"Texturing_Init"},
          {"type":"SampleTexture","index":0,"parameters":[{"path":"{{{texture}}}","srgb":true},{}]},
          {"type":"ConstantFloat4","index":0,"parameters":[{"value":[0.0,0.0,0.0,0.7]}]},
          {"type":"IndirectColor","index":0,"stage":"Texturing_Init"},
          {{{Albedo}}}],
         "links":[
          {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
          {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}},
          {"src":{"type":"ConstantFloat4","index":0,"variable":"output"},"dst":{"type":"IndirectColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
        """;

    /// <summary>LookUpTexture over a constant colour whose alpha is a half, with the multiplier given, one of its outputs into the colour.</summary>
    private static string LookingUp(string multiplier, string output, string swizzle) =>
        Colouring(
            $$$"""
              {"type":"ConstantFloat4","index":0,"parameters":[{"value":[0.2,0.3,0.4,0.5]}]},
              {"type":"LookUpTexture","index":0,"parameters":[{"path":"Art/lut.dds","srgb":true},{},{{{multiplier}}},{}]}
            """,
            """
              {"src":{"type":"ConstantFloat4","index":0,"variable":"output"},"dst":{"type":"LookUpTexture","index":0,"variable":"source_albedo"}}
            """,
            "LookUpTexture", output, swizzle);

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

    /// <summary>A graph of the nodes and links given, one output of which - whole, or by a swizzle - is the colour's xyz.</summary>
    private static string Colouring(string nodes, string links, string type, string variable, string swizzle = "") =>
        $$$"""
        {"nodes":[
        {{{nodes}}},
          {{{Albedo}}}],
         "links":[
        {{{(links.Trim().Length > 0 ? links + "," : string.Empty)}}}
          {"src":{"type":"{{{type}}}","index":0,"variable":"{{{variable}}}","swizzle":"{{{swizzle}}}"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input","swizzle":"{{{(swizzle.Length > 0 ? swizzle : "xyz")}}}"}}]}
        """;

    /// <summary>(0.2, 0.6) turned by half a radian by Rotate or RotateUV, with the parameters given, into the colour's x and y.</summary>
    private static string Turning(string type, string parameters) =>
        Colouring(
            $$$"""
              {"type":"ConstantFloat","index":0,"parameters":[{"value":0.5}]},
              {"type":"ConstantFloat2","index":0,"parameters":[{"value":[0.2,0.6]}]},
              {"type":"{{{type}}}","index":0{{{(parameters.Length > 0 ? ",\"parameters\":[" + parameters + "]" : string.Empty)}}}}
            """,
            $$$"""
              {"src":{"type":"ConstantFloat","index":0,"variable":"output"},"dst":{"type":"{{{type}}}","index":0,"variable":"angle"}},
              {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"{{{type}}}","index":0,"variable":"in_uv"}}
            """,
            type, "out_uv", "xy");

    /// <summary>The vertex world position, by a swizzle or whole, into the colour.</summary>
    private static string WorldPositioned(string swizzle) =>
        Colouring("""{"type":"FromVertexWorldPos","index":0}""", string.Empty, "FromVertexWorldPos", "output", swizzle);

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

    /// <summary>A reader's components straight into the colour's - the same ones, or those given.</summary>
    private static string Reading(string reader, string swizzle, string stage = "Texturing_Init", string? into = null) =>
        $$$"""
        {"nodes":[
          {"type":"{{{reader}}}","index":0,"stage":"{{{stage}}}"},
          {"type":"AlbedoColor","index":0,"stage":"{{{stage}}}"}],
         "links":[
          {"src":{"type":"{{{reader}}}","index":0,"stage":"{{{stage}}}","variable":"output","swizzle":"{{{swizzle}}}"},"dst":{"type":"AlbedoColor","index":0,"stage":"{{{stage}}}","variable":"input","swizzle":"{{{(into ?? swizzle)}}}"}}]}
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

    /// <summary>A node that owns its texture at parameter 0, read at the mesh's coordinates - with extra ports and parameters - one output into the colour.</summary>
    private static string Owning(string type, string output, string swizzle = "", string ports = "", string parameters = "", string nodes = "") =>
        Colouring(
            $$$"""
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {{{nodes}}}
              {"type":"{{{type}}}","index":0,"parameters":[{"path":"Art/own.dds","srgb":true},{}{{{parameters}}}]}
            """,
            $$$"""
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"{{{type}}}","index":0,"variable":"uv"}}{{{(ports.Length > 0 ? "," + ports : string.Empty)}}}
            """,
            type, output, swizzle);

    /// <summary>
    /// MuddleTex at the mesh's coordinates with the parameters given, its coordinates out straight into the colour's x and y.
    /// </summary>
    private static string Muddling(string parameters, string variance = "FromVertexVariance") =>
        Colouring(
            $$$"""
              {"type":"InputUV","index":0,"stage":"Texturing_Init"},
              {"type":"{{{variance}}}","index":0},
              {"type":"MuddleTex","index":0,"parameters":[{"path":"Art/muddle.dds","srgb":false},{}{{{parameters}}}]}
            """,
            $$$"""
              {"src":{"type":"InputUV","index":0,"stage":"Texturing_Init","variable":"output"},"dst":{"type":"MuddleTex","index":0,"variable":"in_uv"}},
              {"src":{"type":"{{{variance}}}","index":0,"variable":"output"},"dst":{"type":"MuddleTex","index":0,"variable":"variance"}}
            """,
            "MuddleTex", "out_uv", "xy");

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

    private static ShaderInstance Instance(string parent, (string Name, ShaderValue[] Values) custom)
        => new(parent, new Dictionary<string, ShaderValue[]> { [custom.Name] = custom.Values });

    /// <summary>A graph from tests/fixtures/shaders, by its path in the install.</summary>
    private static ShaderGraph FixtureGraph(string path)
    {
        ShaderGraph graph = ShaderGraph.Read(File.ReadAllBytes(Fixture(path)));
        Assert.True(graph.Ready);
        return graph;
    }

    /// <summary>The instance of one graph as a material from tests/fixtures/shaders sets it up - its custom parameters.</summary>
    private static ShaderInstance FixtureInstance(string material, string graph)
    {
        IReadOnlyList<ShaderInstance> instances = ShaderGraph.Instances(File.ReadAllBytes(Fixture(material)));
        return Assert.Single(instances, one => string.Equals(one.Parent, graph, StringComparison.OrdinalIgnoreCase));
    }

    private static string Fixture(string path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", "shaders", path.Replace("/", "__", StringComparison.Ordinal));
    }

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
