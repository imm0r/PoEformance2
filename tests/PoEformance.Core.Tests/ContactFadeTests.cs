using System.Numerics;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Ground layers the game fades into what lies under them - MaskedContactFade, and ParallaxUvSpaceContactFade's march and DepthDistance - drawn over a solid floor.
/// </summary>
/// <remarks>
/// THE SCENE IS BUILT SO THE ANSWER IS KNOWN: a red floor, and over it a blue layer in two halves - one
/// a hair above the floor, one well above it - looked at straight down. A layer that fades by the depth
/// behind it shows the floor through where it lies close and itself where it lies high; one drawn by
/// its texture alone, as every mixed shape was before, covers the floor everywhere it is.
/// </remarks>
public class ContactFadeTests
{
    private const int Size = 96;

    /// <summary>A tilt that looks straight down - see MeshPicture.Camera.</summary>
    private const float Down = MathF.PI / 2f;

    private const string MaskedPath = "Metadata/Effects/Graphs/General/MaskedContactFade.fxgraph";
    private const string ParallaxPath = "Metadata/Effects/Graphs/General/Parallax/ParallaxUvSpaceContactFade.fxgraph";
    private const string HeightPath = "Art/height.dds";

    /// <summary>The layer's own colour: blue, and an alpha of one for the fade to take its share of.</summary>
    private const string BlueLayer =
        """
        {"nodes":[
          {"type":"ConstantPixel4","index":0,"parameters":[{"value":[0.05,0.05,0.9,1.0]}]},
          {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
         "links":[
          {"src":{"type":"ConstantPixel4","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
        """;

    /// <summary>
    /// MaskedContactFade: where the layer lies half a unit over the floor the floor shows through it, where it lies twenty over the layer covers it.
    /// </summary>
    /// <remarks>
    /// A flat layer's fade distance is the parameter times ten - one here, so ten units - and its
    /// alpha the height over the floor over that: a twentieth over the low half, all of it over the high.
    /// </remarks>
    [Fact]
    public void MASKEDCONTACTFadeShowsTheFloorWhereTheLayerLiesCloseAndTheLayerWhereItLiesHigh()
    {
        ShadeProgram program = Layer((Instance(MaskedPath, ("params", [ShaderValue.Empty, ShaderValue.Empty, Numbers(1f)])), Fixture(MaskedPath)));
        Assert.True(program.UsesDepth);
        Assert.True(program.Discards);
        Assert.True(program.HasAlpha);

        SkinnedMesh scene = Scene(low: 0.5f, high: 20f);
        GamePicture picture = Drawn(scene, program);

        (byte Red, byte Blue) low = At(picture, scene, new Vector3(-5f, 0f, -0.5f));
        (byte Red, byte Blue) high = At(picture, scene, new Vector3(5f, 0f, -20f));
        Assert.True(low.Red > low.Blue * 2, $"the floor should show through the low half: {low}");
        Assert.True(high.Blue > high.Red * 2, $"the layer should cover the floor where it lies high: {high}");

        // WITHOUT THE FADE, as before: the texture's alpha alone, one, and the layer covers the floor everywhere.
        GamePicture plain = Drawn(scene, Layer());
        (byte Red, byte Blue) covered = At(plain, scene, new Vector3(-5f, 0f, -0.5f));
        Assert.True(covered.Blue > covered.Red * 2, $"a layer that does not fade covers the floor: {covered}");
    }

    /// <summary>
    /// ParallaxUvSpaceContactFade, the game's own graph: the march goes down into the height texture, and where it ends under the floor the layer is discarded.
    /// </summary>
    /// <remarks>
    /// The layer lies five units over the floor and the march reaches eight below it. A height of nought
    /// sends the ray to the bottom - three units under the floor, so DepthDistance discards it and the
    /// floor shows; a height of one stops it at the layer, five units over the floor, past the fade
    /// distance of one, and the layer covers the floor. Which of the two a pixel shows is the height
    /// texture's to say - which is how a mud layer comes out in patches in the game.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(255, true)]
    public void THEPARALLAXMarchDecidesWhereTheLayerShows(byte height, bool layerShows)
    {
        ShaderInstance parallax = new(ParallaxPath, new Dictionary<string, ShaderValue[]>
        {
            ["Height tex"] = [new ShaderValue(HeightPath, [], false)],
            ["Depth range"] = [Numbers(8f)],
            ["Fade dist"] = [Numbers(1f)],
        });
        ShadeProgram compiled = Layer((parallax, Fixture(ParallaxPath)));
        Assert.True(compiled.UsesTangents);
        Assert.True(compiled.UsesDepth);
        ShadeProgram program = compiled.With([.. compiled.Textures.Select(one => one.Path == HeightPath ? Sheet(height, height, height) : null)]);
        Assert.True(program.Bound);

        SkinnedMesh scene = Scene(low: 5f, high: 5f);
        (byte Red, byte Blue) seen = At(Drawn(scene, program), scene, new Vector3(-5f, 0f, -5f));
        Assert.True(layerShows ? seen.Blue > seen.Red * 2 : seen.Red > seen.Blue * 2, $"height {height}: {seen}");
    }

    /// <summary>
    /// A read is of its own stage: coordinates read at UVSetup_Final are those from before the graph's own write there, wherever they are used.
    /// </summary>
    /// <remarks>
    /// The graph moves the coordinates half a texture across at UVSetup_Final and colours the pixel at
    /// Texturing_Final from a read of InputUV - once placed at UVSetup_Final, once at Texturing_Final.
    /// The texture is red on its left half and blue on its right, and the mesh reads it a quarter in: the
    /// read placed at the earlier stage sees the coordinates unmoved and is red, the later one is blue.
    /// This is what keeps ParallaxUvSpaceContactFade from marching twice.
    /// </remarks>
    [Theory]
    [InlineData("UVSetup_Final", true)]
    [InlineData("Texturing_Final", false)]
    public void AREADIsOfItsOwnStage(string readAt, bool red)
    {
        string graph = $$$"""
            {"nodes":[
              {"type":"InputUV","index":0,"stage":"UVSetup_Final"},
              {"type":"InputUV","index":1,"stage":"{{{readAt}}}"},
              {"type":"ConstantFloat2","index":0,"parameters":[{"value":[0.5,0.0]}]},
              {"type":"Add2","index":0},
              {"type":"UV","index":0,"stage":"UVSetup_Final"},
              {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/halves.dds","srgb":true},{}]},
              {"type":"AlbedoColor","index":0,"stage":"Texturing_Final"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"UVSetup_Final","variable":"output"},"dst":{"type":"Add2","index":0,"variable":"a"}},
              {"src":{"type":"ConstantFloat2","index":0,"variable":"output"},"dst":{"type":"Add2","index":0,"variable":"b"}},
              {"src":{"type":"Add2","index":0,"variable":"output"},"dst":{"type":"UV","index":0,"stage":"UVSetup_Final","variable":"input"}},
              {"src":{"type":"InputUV","index":1,"stage":"{{{readAt}}}","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Final","variable":"input"}}]}
            """;
        ShadeCompile compiled = ShadeProgram.Compile([(Instance("Metadata/Test.fxgraph"), Graph(graph))]);
        Assert.Empty(compiled.Skipped);
        Assert.NotNull(compiled.Program);
        ShadeProgram program = compiled.Program.With([Halves()]);

        SkinnedMesh quarter = Floor(new Vector2(0.25f, 0.5f));
        GamePicture picture = MeshPicture.Of(quarter, Size, tilt: Down, shades: [program]);
        (byte r, byte b) = At(picture, quarter, Vector3.Zero);
        Assert.True(red ? r > b * 2 : b > r * 2, $"read at {readAt}: {r}, {b}");
    }

    /// <summary>The ground layers of seepage's offices, from the install: each compiles whole, faded by the depth behind it.</summary>
    /// <remarks>
    /// SloppyMud02_MCFc is the mud that came out as a slab over the floor: DielectricSpecGlossBN's
    /// colour at the march's coordinates, faded by ParallaxUvSpaceContactFade. Its wetness graph reads
    /// the pixel's wetness, which no file says, and only ever touched the specular.
    /// </remarks>
    [Theory]
    [InlineData("Art/Models/Terrain/Jungle/Tiles/Jungle/Textures/SloppyMud02_MCFc.mat", ParallaxPath, true, "ReadPixelWetness in specular_wetness_mult")]
    [InlineData("Art/Models/Terrain/Jungle/Tiles/Jungle/Textures/Dung01c.mat", ParallaxPath, true, "")]
    [InlineData("Art/Models/Terrain/Jungle/Tiles/VaalInterior/Textures/DamagedStone01MCFc.mat", MaskedPath, false, "")]
    public void THEOFFICESGroundLayersFromTheInstallCompileWhole(string material, string fade, bool marches, string skipped)
    {
        IReadOnlyList<ShaderInstance> instances = ShaderGraph.Instances(File.ReadAllBytes(FixturePath(material)));
        ShadeCompile compiled = ShadeProgram.Compile([.. instances.Select(one => (one, ShaderGraph.Read(File.ReadAllBytes(FixturePath(one.Parent)))))]);

        Assert.NotNull(compiled.Program);
        Assert.Equal(skipped.Length == 0 ? [] : [skipped], compiled.Skipped);
        Assert.Equal(["Metadata/Materials/DielectricSpecGlossBN.fxgraph", fade], compiled.Program.Graphs);
        Assert.True(compiled.Program.HasAlpha);
        Assert.True(compiled.Program.UsesDepth);
        Assert.True(compiled.Program.Discards);
        Assert.Equal(marches, compiled.Program.UsesTangents);
    }

    /// <summary>The blue layer, faded by the graph given or by nothing.</summary>
    private static ShadeProgram Layer(params (ShaderInstance Instance, ShaderGraph Graph)[] fade)
    {
        ShadeCompile compiled = ShadeProgram.Compile([(Instance("Metadata/Blue.fxgraph"), Graph(BlueLayer)), .. fade]);
        Assert.Empty(compiled.Skipped);
        Assert.NotNull(compiled.Program);
        return compiled.Program;
    }

    /// <summary>The scene looked at straight down: the floor red by its texture, the layer mixed by its program.</summary>
    private static GamePicture Drawn(SkinnedMesh scene, ShadeProgram layer)
        => MeshPicture.Of(
            scene,
            Size,
            tilt: Down,
            skins: [Sheet(220, 30, 30), null],
            blends: [MaterialBlend.Opaque, MaterialBlend.Alpha],
            shades: [null, layer]);

    /// <summary>The red and blue where a point of the scene lands.</summary>
    private static (byte Red, byte Blue) At(GamePicture picture, SkinnedMesh scene, Vector3 point)
    {
        Vector3 placed = MeshPicture.Camera.Of(scene, 0f, Down, 1f, Vector2.Zero).Place(point);
        int x = Math.Clamp((int)(placed.X * Size), 0, Size - 1);
        int y = Math.Clamp((int)(placed.Y * Size), 0, Size - 1);
        int at = ((y * Size) + x) * 4;
        Assert.True(picture.Rgba[at + 3] > 0, $"nothing drawn at {point}");
        return (picture.Rgba[at], picture.Rgba[at + 2]);
    }

    /// <summary>
    /// A floor twenty across at height nought, and a layer over it in two halves: the one over negative x <paramref name="low"/> above it, the other <paramref name="high"/>.
    /// </summary>
    /// <remarks>Up is minus z. The layer's coordinates run with x and y across the whole twenty, so its ∂p/∂u and ∂p/∂v are whole.</remarks>
    private static SkinnedMesh Scene(float low, float high)
    {
        Vector3[] places =
        [
            new(-10f, -10f, 0f), new(10f, -10f, 0f), new(10f, 10f, 0f), new(-10f, 10f, 0f),
            new(-10f, -10f, -low), new(0f, -10f, -low), new(0f, 10f, -low), new(-10f, 10f, -low),
            new(0f, -10f, -high), new(10f, -10f, -high), new(10f, 10f, -high), new(0f, 10f, -high),
        ];
        var normals = new Vector3[places.Length];
        Array.Fill(normals, new Vector3(0f, 0f, -1f));
        Vector2[] spots = [.. places.Select(one => new Vector2((one.X + 10f) / 20f, (one.Y + 10f) / 20f))];
        int[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11];
        return SkinnedMesh.Of(
            places, normals, indices, new Vector3(-10f, -10f, -Math.Max(low, high)), new Vector3(10f, 10f, 0f),
            spots, [new MeshShape("Floor", 0, 6), new MeshShape("Layer", 6, 12)]);
    }

    /// <summary>A floor twenty across, every corner reading the texture at one spot.</summary>
    private static SkinnedMesh Floor(Vector2 spot)
    {
        Vector3[] places = [new(-10f, -10f, 0f), new(10f, -10f, 0f), new(10f, 10f, 0f), new(-10f, 10f, 0f)];
        var normals = new Vector3[4];
        Array.Fill(normals, new Vector3(0f, 0f, -1f));
        var spots = new Vector2[4];
        Array.Fill(spots, spot);
        return SkinnedMesh.Of(places, normals, [0, 1, 2, 0, 2, 3], new Vector3(-10f, -10f, -1f), new Vector3(10f, 10f, 0f), spots, [new MeshShape("Floor", 0, 6)]);
    }

    /// <summary>A texture of one colour.</summary>
    private static Mipmaps Sheet(byte red, byte green, byte blue)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var one = 0; one < 8 * 8; one++)
        {
            pixels[one * 4] = red;
            pixels[(one * 4) + 1] = green;
            pixels[(one * 4) + 2] = blue;
            pixels[(one * 4) + 3] = 255;
        }

        Mipmaps? levels = Mipmaps.Of(new GamePicture(8, 8, pixels));
        Assert.NotNull(levels);
        return levels;
    }

    /// <summary>A texture red on its left half and blue on its right.</summary>
    private static Mipmaps Halves()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var one = 0; one < 8 * 8; one++)
        {
            bool left = one % 8 < 4;
            pixels[one * 4] = left ? (byte)220 : (byte)20;
            pixels[(one * 4) + 2] = left ? (byte)20 : (byte)220;
            pixels[(one * 4) + 3] = 255;
        }

        Mipmaps? levels = Mipmaps.Of(new GamePicture(8, 8, pixels));
        Assert.NotNull(levels);
        return levels;
    }

    private static ShaderValue Numbers(params float[] numbers) => new(string.Empty, numbers, null);

    private static ShaderInstance Instance(string parent, (string Name, ShaderValue[] Values) custom)
        => new(parent, new Dictionary<string, ShaderValue[]> { [custom.Name] = custom.Values });

    private static ShaderInstance Instance(string parent) => new(parent, new Dictionary<string, ShaderValue[]>());

    private static ShaderGraph Graph(string json)
    {
        ShaderGraph graph = ShaderGraph.Read(Encoding.UTF8.GetBytes(json));
        Assert.True(graph.Ready);
        return graph;
    }

    private static ShaderGraph Fixture(string path)
    {
        ShaderGraph graph = ShaderGraph.Read(File.ReadAllBytes(FixturePath(path)));
        Assert.True(graph.Ready);
        return graph;
    }

    private static string FixturePath(string path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", "shaders", path.Replace("/", "__", StringComparison.Ordinal));
    }
}
