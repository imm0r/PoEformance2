using System.Diagnostics;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Gpu.Tests;

/// <summary>
/// Materials' shade programs drawn by the card as their own shaders, held against MeshPicture running the same programs.
/// </summary>
/// <remarks>
/// THE GAME'S OWN MATERIALS, every one tests/fixtures/shaders holds whole - not graphs written for the
/// test - so the ops are the ones the game uses, in the orders it uses them: world-projected tiling,
/// vertex paint, dust laid by occlusion, gloss, contact fades, a parallax march. Their textures are
/// smooth ramps, one per path, so a read at a slightly different level - the card's per pixel, the
/// processor's per triangle - reads nearly the same colour, and what is compared is the arithmetic.
///
/// ONE CARD FOR THE CLASS (<see cref="CardFixture"/>), so a program's shaders are compiled once however
/// many pictures and tests it is in; the first picture of each waits for them as the pane would.
/// </remarks>
public class ProgramGpuTests(CardFixture card) : IClassFixture<CardFixture>
{
    private const int Side = ModelGpuTests.Side;

    /// <summary>
    /// The test sheet's half-width in the game's units - a tile is 250 across.
    /// </summary>
    /// <remarks>
    /// AT THE GAME'S SCALE, NOT ONE UNIT, because the materials are authored for it: Dung01c marches
    /// its parallax 21 units deep, which on a sheet two units wide runs the ray through a dozen
    /// repeats of the height texture between two neighbouring pixels. The march is then chaos, and
    /// which of it a picture shows is decided by the level each read takes - the card's per pixel,
    /// the processor's per triangle - so the first run of these tests failed on exactly the two
    /// materials with a march, each picture right by its own rule. At this size a neighbouring pixel
    /// differs by as little as on every other material (measured on the processor: 52 levels a
    /// step at one unit, 6 at a hundred).
    /// </remarks>
    private const float Scale = 100f;

    /// <summary>How long a picture waits for its shaders before the wait is the failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private static readonly Dictionary<string, Mipmaps> Sheets = new(StringComparer.Ordinal);

    [GpuFact]
    public void EVERYFIXTUREMaterialIsDrawnByTheCardAsTheProcessorDrawsItUnderTheLamp()
        => Every(program => new ModelScene(Hill(layered: false), Turn: 0.4f, Tilt: 0.5f, Blends: [MaterialBlend.Opaque], Shades: [program], Time: 1.25f));

    [GpuFact]
    public void EVERYFIXTUREMaterialIsDrawnByTheCardAsTheProcessorDrawsItUnderTheGamesLight()
    {
        PointLight[] points = [new(new Vector3(0.5f, 0.8f, -0.3f) * Scale, new Vector3(2f, 1f, 0.5f), 1.5f * Scale, 0.4f, "warm")];
        var light = new SceneLight(points, SceneLight.PointShape.Penumbra, null)
        {
            SunColour = new Vector3(1.4f, 1.3f, 1.1f),
            SunDirection = Vector3.Normalize(new Vector3(0.4f, -1f, 0.3f)),
            SunShadows = true,
            ShadowSide = ShadowMap.Coarse,
            FlatAmbient = 0.1f,
            Exposure = 1.2f,
        };
        Every(program => new ModelScene(
            Hill(layered: false), Turn: -0.3f, Tilt: 0.45f, Blends: [MaterialBlend.Opaque], Shades: [program], Light: light, Time: 0.5f));
    }

    /// <summary>A material as a mixed layer over a solid floor: its alpha, its fade by the depth behind, its discards.</summary>
    [GpuFact]
    public void AMIXEDLayerIsLaidOverTheSolidFloorAsTheProcessorLaysIt()
        => Every(
            program => new ModelScene(
                Hill(layered: true),
                Turn: 0.25f,
                Tilt: 0.55f,
                Skins: [Ramp("floor"), null],
                Blends: [MaterialBlend.Opaque, MaterialBlend.Alpha],
                Shades: [null, program],
                Time: 2f),
            program => program.HasAlpha || program.UsesDepth);

    /// <summary>A material cut out on the alpha its graphs leave.</summary>
    [GpuFact]
    public void ACUTOUTMaterialIsCutWhereTheProcessorCutsIt()
        => Every(
            program => new ModelScene(Hill(layered: false), Turn: 0.4f, Tilt: 0.5f, Blends: [MaterialBlend.Cutout], Shades: [program]),
            program => program.HasAlpha);

    /// <summary>A material's compiled shaders are kept in the folder handed in, and a new card reads them back rather than compiling again.</summary>
    [GpuFact]
    public void COMPILEDShadersAreKeptOnDiskAndReadBack()
    {
        string folder = Path.Combine(Path.GetTempPath(), "poeformance-shaders-" + Guid.NewGuid().ToString("N"));
        try
        {
            (string _, ShadeProgram program) = Programs().First();
            var scene = new ModelScene(Hill(layered: false), Turn: 0.4f, Tilt: 0.5f, Blends: [MaterialBlend.Opaque], Shades: [Bound(program)]);
            using (ModelGpu? first = ModelGpu.Warp(out string why, folder))
            {
                Assert.True(first is not null, why);
                Assert.True(Ready(first, scene, out why), why);
            }

            string[] kept = Directory.GetFiles(folder, "*.cso");
            Assert.NotEmpty(kept);

            using ModelGpu? again = ModelGpu.Warp(out string said, folder);
            Assert.True(again is not null, said);
            Assert.True(Ready(again, scene, out said), said);
            Assert.Equal(kept.Length, Directory.GetFiles(folder, "*.cso").Length);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>Every fixture program the filter takes, drawn by both - and every one that differs named, not only the first.</summary>
    private void Every(Func<ShadeProgram, ModelScene> scene, Func<ShadeProgram, bool>? which = null)
    {
        ModelGpu? gpu = card.Gpu;
        Assert.True(gpu is not null, card.Why);
        using ModelTarget target = gpu.Target(Side);
        var failed = new List<string>();
        var drawn = 0;
        foreach ((string name, ShadeProgram program) in Programs())
        {
            if (which is not null && !which(program))
            {
                continue;
            }

            drawn++;
            if (Mismatch(gpu, target, scene(Bound(program))) is { } mismatch)
            {
                failed.Add($"{name}: {mismatch}");
            }
        }

        Assert.True(drawn > 0, "no fixture program was drawn");
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    private static string? Mismatch(ModelGpu gpu, ModelTarget target, ModelScene scene)
    {
        var canvas = new MeshPicture.Canvas(Side) { Light = scene.Light, Time = scene.Time };
        byte[] cpu = [.. MeshPicture.Of(
            scene.Mesh, canvas, scene.Turn, scene.Tilt, scene.Ink, scene.Skin, scene.Zoom, scene.Pan, scene.Skins, scene.Blends, scene.Shades).Rgba];
        if (!Ready(gpu, scene, out string why) || !gpu.Draw(target, scene, out why))
        {
            return why;
        }

        return ModelGpuTests.Mismatch(cpu, gpu.Read(target));
    }

    /// <summary>Waits for a picture's shaders as the pane does, asking again until the card says yes or something other than compiling.</summary>
    private static bool Ready(ModelGpu gpu, in ModelScene scene, out string why)
    {
        var clock = Stopwatch.StartNew();
        while (!gpu.Can(scene, out why))
        {
            if (!why.Contains("compiling", StringComparison.Ordinal) || clock.Elapsed > Patience)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>A program with a ramp for every sheet it reads.</summary>
    private static ShadeProgram Bound(ShadeProgram program) => program.With([.. program.Textures.Select(one => (Mipmaps?)Ramp(one.Path))]);

    /// <summary>
    /// A smooth ramp, its own per path - red across, green down, blue and alpha from the path - so a read at a nearby level is nearly the same colour.
    /// </summary>
    private static Mipmaps Ramp(string path)
    {
        lock (Sheets)
        {
            if (Sheets.TryGetValue(path, out Mipmaps? had))
            {
                return had;
            }

            int seed = 0;
            foreach (char letter in path)
            {
                seed = unchecked((seed * 31) + letter);
            }

            const int side = 64;
            float blue = 0.2f + (0.6f * ((seed & 0xFF) / 255f));
            var rgba = new byte[side * side * 4];
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    int at = ((y * side) + x) * 4;
                    rgba[at] = (byte)(30 + (x * 190 / (side - 1)));
                    rgba[at + 1] = (byte)(40 + (y * 180 / (side - 1)));
                    rgba[at + 2] = (byte)(blue * 255f);
                    rgba[at + 3] = (byte)(60 + ((x + y) * 195 / ((2 * side) - 2)));
                }
            }

            Mipmaps made = Mipmaps.Of(new GamePicture(side, side, rgba))!;
            Sheets[path] = made;
            return made;
        }
    }

    /// <summary>
    /// A rolling sheet <see cref="Scale"/> either way of the middle facing the camera, with coordinates tiled half again, normals that turn every way and a vertex colour that ramps - and where asked a second sheet a little in front of it, a shape of its own.
    /// </summary>
    private static SkinnedMesh Hill(bool layered)
    {
        const int cells = 24;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var spots = new List<Vector2>();
        var colours = new List<byte>();
        var indices = new List<int>();
        var shapes = new List<MeshShape>();
        foreach (float lift in layered ? [0f, 0.12f] : (float[])[0f])
        {
            int start = positions.Count;
            int from = indices.Count;
            for (var j = 0; j <= cells; j++)
            {
                for (var i = 0; i <= cells; i++)
                {
                    float x = -1f + (2f * i / cells), z = -1f + (2f * j / cells);
                    float y = (0.25f * MathF.Sin(3f * x) * MathF.Cos(2f * z)) + lift;
                    positions.Add(new Vector3(x, y, z) * Scale);
                    normals.Add(Vector3.Normalize(new Vector3(-0.75f * MathF.Cos(3f * x) * MathF.Cos(2f * z), 1f, 0.5f * MathF.Sin(3f * x) * MathF.Sin(2f * z))));
                    spots.Add(new Vector2((x * 1.5f) + 0.5f, (z * 1.5f) + 0.5f));
                    colours.AddRange([(byte)(i * 255 / cells), (byte)(j * 255 / cells), 128, (byte)(255 - (i * 4))]);
                }
            }

            for (var j = 0; j < cells; j++)
            {
                for (var i = 0; i < cells; i++)
                {
                    int a = start + (j * (cells + 1)) + i, b = a + cells + 1;
                    indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
                }
            }

            shapes.Add(new MeshShape($"sheet {shapes.Count}", from, indices.Count - from));
        }

        return SkinnedMesh.Of(
            [.. positions], [.. normals], [.. indices], new Vector3(-1f, -0.3f, -1f) * Scale, new Vector3(1f, 0.4f, 1f) * Scale,
            [.. spots], shapes, colours: [.. colours]);
    }

    /// <summary>Every material in tests/fixtures/shaders whose graphs are all there, compiled - its plain and its glossy program.</summary>
    private static IEnumerable<(string Name, ShadeProgram Program)> Programs()
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

/// <summary>The WARP card <see cref="ProgramGpuTests"/> shares, made once for the class.</summary>
public sealed class CardFixture : IDisposable
{
    public CardFixture()
    {
        string why = "Direct3D 11 is Windows'";
        Gpu = OperatingSystem.IsWindows() ? ModelGpu.Warp(out why) : null;
        Why = why;
    }

    /// <summary>The card, or null where it would not start.</summary>
    public ModelGpu? Gpu { get; }

    /// <summary>Why there is none, or empty.</summary>
    public string Why { get; }

    /// <inheritdoc/>
    public void Dispose() => Gpu?.Dispose();
}
