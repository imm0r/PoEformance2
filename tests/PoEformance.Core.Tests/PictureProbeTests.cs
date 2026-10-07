using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The probe: what one pixel of a picture is made of, and how the translucent layers came out over all of it.
/// </summary>
/// <remarks>
/// THE SCENE IS ContactFadeTests' AND SO IS THE ANSWER: a red floor at height nought and a blue layer
/// over it in two halves, two units over the floor and twenty, faded by MaskedContactFade with a fade
/// distance of ten - so an alpha of a fifth over the low half and a whole one over the high.
/// </remarks>
public class PictureProbeTests
{
    private const int Size = 96;

    /// <summary>A tilt that looks straight down - see MeshPicture.Camera.</summary>
    private const float Down = MathF.PI / 2f;

    private const string MaskedPath = "Metadata/Effects/Graphs/General/MaskedContactFade.fxgraph";

    private const string BlueLayer =
        """
        {"nodes":[
          {"type":"ConstantPixel4","index":0,"parameters":[{"value":[0.05,0.05,0.9,1.0]}]},
          {"type":"AlbedoColor","index":0,"stage":"Texturing_Init"}],
         "links":[
          {"src":{"type":"ConstantPixel4","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing_Init","variable":"input"}}]}
        """;

    /// <summary>A probed pixel over the low half lists the floor, shown, and the layer over it, mixed at a fifth two units in front of it.</summary>
    [Fact]
    public void APROBEDPixelListsTheFloorAndTheLayerMixedOverIt()
    {
        SkinnedMesh scene = Scene(low: 2f, high: 20f);
        (int x, int y) = PixelOf(scene, new Vector3(-5f, 0f, -2f));
        var probe = new PixelProbe(x, y);
        Drawn(scene, new MeshPicture.Canvas(Size) { Probe = probe });

        Assert.True(probe.Drawn);
        Assert.Equal(2, probe.Fragments.Count);

        ProbeFragment floor = probe.Fragments[0];
        Assert.Equal(Seen.Solid, floor.Seen);
        Assert.InRange(floor.Triangle, 0, 1);
        Assert.Equal(float.MaxValue, floor.Behind);
        Assert.Equal(floor.Depth, probe.Final);

        ProbeFragment layer = probe.Fragments[1];
        Assert.Equal(Seen.Mixed, layer.Seen);
        Assert.InRange(layer.Triangle, 2, 3);
        Assert.Equal(floor.Depth, layer.Behind);
        Assert.InRange(layer.Behind - layer.Depth, 1.99f, 2.01f);
        Assert.InRange(layer.Cover, 0.19f, 0.21f);
    }

    /// <summary>A layer under the floor is listed as hidden under it, and a pixel the model does not reach lists nothing.</summary>
    [Fact]
    public void ALAYERUnderTheFloorIsHiddenAndAnEmptyPixelListsNothing()
    {
        SkinnedMesh scene = Scene(low: -2f, high: 20f);
        (int x, int y) = PixelOf(scene, new Vector3(-5f, 0f, 2f));
        var under = new PixelProbe(x, y);
        var canvas = new MeshPicture.Canvas(Size) { Probe = under };
        Drawn(scene, canvas);
        Assert.Equal([Seen.Solid, Seen.Under], under.Fragments.Select(one => one.Seen));

        var empty = new PixelProbe(0, 0);
        canvas.Probe = empty;
        Drawn(scene, canvas);
        Assert.True(empty.Drawn);
        Assert.Empty(empty.Fragments);
        Assert.Contains("nothing of the model reaches", PictureProbe.Lines(empty, Model(scene), null)[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The tally counts every pixel of the layer: all with the floor behind, the low half two units in front and at a fifth, the high half twenty in front and whole - on one thread or several.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void THETALLYCountsEveryPixelOfTheLayerByGapAndAlpha(int threads)
    {
        SkinnedMesh scene = Scene(low: 2f, high: 20f);
        var tally = new LayerTally();
        Drawn(scene, new MeshPicture.Canvas(Size, threads) { Tally = tally });

        LayerCount layer = tally.Of(1);
        Assert.True(layer.Fragments > Size * Size / 4, $"{layer.Fragments} pixels");
        Assert.Equal(layer.Fragments, layer.Behind);
        Assert.Equal(0, layer.Discarded);

        // [1, 3) for the low half, [10, 30) for the high.
        Assert.Equal(layer.Behind, layer.Gaps[1] + layer.Gaps[3]);
        Assert.True(layer.Gaps[1] > 0 && layer.Gaps[3] > 0);

        // Under a quarter for the low half, whole for the high.
        Assert.Equal(layer.Fragments, layer.Cover[1] + layer.Cover[5]);
        Assert.Equal(layer.Gaps[1], layer.Cover[1]);

        // THE FLOOR IS SOLID, and only translucent shapes are counted.
        Assert.Equal(0, tally.Of(0).Fragments);
    }

    /// <summary>The same counts whatever the threads, and nothing counted where no tally is asked for.</summary>
    [Fact]
    public void THETALLYIsTheSameOnOneThreadAndOnSeveral()
    {
        SkinnedMesh scene = Scene(low: 2f, high: 20f);
        var one = new LayerTally();
        var several = new LayerTally();
        GamePicture first = Drawn(scene, new MeshPicture.Canvas(Size, 1) { Tally = one });
        byte[] pixels = [.. first.Rgba];
        Drawn(scene, new MeshPicture.Canvas(Size, 4) { Tally = several });

        Assert.Equal(one.Of(1).Fragments, several.Of(1).Fragments);
        Assert.Equal(one.Of(1).Gaps, several.Of(1).Gaps);
        Assert.Equal(one.Of(1).Cover, several.Of(1).Cover);

        // AND COUNTING CHANGES NOT ONE PIXEL of the picture.
        var plain = new MeshPicture.Canvas(Size, 1);
        Assert.Equal(pixels, Drawn(scene, plain).Rgba);
    }

    /// <summary>The probe's lines name each surface's material, blend, what drew it and where it came from, nearest first.</summary>
    [Fact]
    public void THELINESNameEachSurfaceNearestFirst()
    {
        SkinnedMesh scene = Scene(low: 2f, high: 20f);
        (int x, int y) = PixelOf(scene, new Vector3(-5f, 0f, -2f));
        var probe = new PixelProbe(x, y);
        var tally = new LayerTally();
        ShadeProgram layer = Layer();
        MonsterModel model = Model(scene);
        Drawn(scene, new MeshPicture.Canvas(Size) { Probe = probe, Tally = tally }, layer);

        IReadOnlyList<string> lines = PictureProbe.Lines(probe, model, [null, layer]);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith($"probe at {x}, {y}: 2 fragments, nearest first", lines[0], StringComparison.Ordinal);
        Assert.Contains("mixed, alpha 0.20 · 2.0 in front of the solid behind it · Mud_MCFc.mat (AlphaBlend -> alpha, program) · shape 1 · from Metadata/Test/Mud.ao", lines[1], StringComparison.Ordinal);
        Assert.Contains("shown · Floor.mat (- -> opaque, texture) · shape 0 · from ground of Metadata/Test/tile.tdt at tile 1, 2", lines[2], StringComparison.Ordinal);

        IReadOnlyList<string> layers = PictureProbe.Layers(tally, model, [null, layer]);
        Assert.Equal(2, layers.Count);
        Assert.Contains("Mud_MCFc.mat · program · ", layers[1], StringComparison.Ordinal);
        Assert.Contains(" · solid behind 100% · gap <1 0%, 1-3 ", layers[1], StringComparison.Ordinal);
        Assert.Contains(" · dropped 0%", layers[1], StringComparison.Ordinal);
    }

    /// <summary>The room's model of the scene: the floor a tile's ground, the layer a doodad's mud.</summary>
    private static MonsterModel Model(SkinnedMesh scene) => new(scene, null, "scene", string.Empty, string.Empty)
    {
        Skins = [Sheet(220, 30, 30), null],
        Modes = [string.Empty, "AlphaBlend"],
        ShapeMaterials = ["Art/Floor.mat", "Art/Mud_MCFc.mat"],
        ShapeSources = ["ground of Metadata/Test/tile.tdt at tile 1, 2", "Metadata/Test/Mud.ao"],
    };

    private static GamePicture Drawn(SkinnedMesh scene, MeshPicture.Canvas canvas, ShadeProgram? layer = null)
        => MeshPicture.Of(
            scene,
            canvas,
            tilt: Down,
            skins: [Sheet(220, 30, 30), null],
            blends: [MaterialBlend.Opaque, MaterialBlend.Alpha],
            shades: [null, layer ?? Layer()]);

    /// <summary>The blue layer faded by MaskedContactFade with a fade distance of ten.</summary>
    private static ShadeProgram Layer()
    {
        var masked = new ShaderInstance(MaskedPath, new Dictionary<string, ShaderValue[]>
        {
            ["params"] = [ShaderValue.Empty, ShaderValue.Empty, new ShaderValue(string.Empty, [1f], null)],
        });
        ShadeCompile compiled = ShadeProgram.Compile(
        [
            (new ShaderInstance("Metadata/Blue.fxgraph", new Dictionary<string, ShaderValue[]>()), ShaderGraph.Read(System.Text.Encoding.UTF8.GetBytes(BlueLayer))),
            (masked, ShaderGraph.Read(File.ReadAllBytes(FixturePath(MaskedPath)))),
        ]);
        Assert.Empty(compiled.Skipped);
        Assert.NotNull(compiled.Program);
        return compiled.Program;
    }

    private static (int X, int Y) PixelOf(SkinnedMesh scene, Vector3 point)
    {
        Vector3 placed = MeshPicture.Camera.Of(scene, 0f, Down, 1f, Vector2.Zero).Place(point);
        return (Math.Clamp((int)(placed.X * Size), 0, Size - 1), Math.Clamp((int)(placed.Y * Size), 0, Size - 1));
    }

    /// <summary>A floor twenty across at height nought, and a layer over it in two halves - see ContactFadeTests.Scene.</summary>
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
            places, normals, indices, new Vector3(-10f, -10f, -Math.Max(Math.Max(low, high), 0f)), new Vector3(10f, 10f, Math.Max(0f, -Math.Min(low, high))),
            spots, [new MeshShape("Floor", 0, 6), new MeshShape("Layer", 6, 12)]);
    }

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
