using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Gpu.Tests;

/// <summary>A fact that only runs where Direct3D 11 is.</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Direct3D 11 is Windows'";
        }
    }
}

/// <summary>
/// The graphics card's picture of a model held against MeshPicture's, pixel for pixel.
/// </summary>
/// <remarks>
/// THE PROCESSOR'S PICTURE IS THE REFERENCE - it is the one every other test in the project measures -
/// so what is asked here is only whether the card draws the same one. Not to the byte: the card fills a
/// pixel on an edge two triangles share once where the processor fills it for both, and filters a
/// texture with its own fixed-point weights. So each comparison allows a small share of pixels to
/// differ by more than a few levels, and says which and by how much when it fails.
/// </remarks>
public class ModelGpuTests
{
    private const int Side = 192;

    /// <summary>How far apart a channel may be and still count as the same, in levels of 255.</summary>
    private const int Levels = 4;

    [GpuFact]
    public void EVERYShaderCompilesOnTheCard()
    {
        using ModelGpu? gpu = ModelGpu.Warp(out string why);
        Assert.True(gpu is not null, why);
    }

    [GpuFact]
    public void ANINKBoxTurnedAndTiltedIsTheProcessorsBox()
        => Same(new ModelScene(Box(), Turn: 0.6f, Tilt: 0.4f));

    [GpuFact]
    public void ATEXTUREDQuadIsReadAsTheProcessorReadsItMagnifiedAndShrunk()
    {
        SkinnedMesh quad = Layers(1);
        Same(new ModelScene(quad, Turn: 0.3f, Tilt: 0.2f, Skin: Checker(32, 8)));
        Same(new ModelScene(quad, Turn: 0.3f, Tilt: 0.2f, Skin: Checker(1024, 16)));
    }

    [GpuFact]
    public void CUTOUTMixedAndAddedShapesLieOverTheSolidOneAsTheProcessorLaysThem()
    {
        SkinnedMesh layers = Layers(4);
        Mipmaps solid = Plain(new Vector4(0.6f, 0.3f, 0.2f, 1f));
        Mipmaps holed = Checker(64, 8, alpha: true);
        Mipmaps half = Plain(new Vector4(0.1f, 0.5f, 0.9f, 0.5f));
        Mipmaps glow = Plain(new Vector4(0.2f, 0.4f, 0.1f, 1f));
        Same(new ModelScene(
            layers,
            Turn: 0.2f,
            Tilt: 0.15f,
            Skins: [solid, holed, half, glow],
            Blends: [MaterialBlend.Opaque, MaterialBlend.Cutout, MaterialBlend.Alpha, MaterialBlend.Additive]));
    }

    [GpuFact]
    public void AMIXEDShapeOverNothingIsHalfCoveredAndAShadowOnlyOneIsNotDrawn()
    {
        SkinnedMesh layers = Layers(2);
        Same(new ModelScene(
            layers,
            Skins: [Plain(new Vector4(0.9f, 0.2f, 0.2f, 0.5f)), Plain(Vector4.One)],
            Blends: [MaterialBlend.Alpha, MaterialBlend.ShadowOnly]));
    }

    [GpuFact]
    public void APOSEIsDrawnWhereItStandsInTheBoxOfTheStillMesh()
    {
        SkinnedMesh box = Box();
        Vector3[] moved = [.. box.Positions.Select(one => one + new Vector3(0.4f, 0f, -0.2f))];
        GamePicture expected = MeshPicture.Of(box, new MeshPicture.Canvas(Side), moved, box.Normals, 0.6f, 0.4f);
        Compared(expected.Rgba, Drawn(new ModelScene(box, Turn: 0.6f, Tilt: 0.4f, Positions: moved, Normals: box.Normals)));
    }

    /// <summary>The scene drawn by both, compared.</summary>
    private static void Same(ModelScene scene)
    {
        GamePicture expected = MeshPicture.Of(
            scene.Mesh, Side, scene.Turn, scene.Tilt, scene.Ink, scene.Skin, scene.Zoom, scene.Pan, scene.Skins, scene.Blends);
        Compared(expected.Rgba, Drawn(scene));
    }

    private static byte[] Drawn(ModelScene scene)
    {
        using ModelGpu? gpu = ModelGpu.Warp(out string why);
        Assert.True(gpu is not null, why);
        Assert.True(gpu.Can(scene, null, null, out why), why);
        using ModelTarget target = gpu.Target(Side);
        Assert.True(gpu.Draw(target, scene, out why), why);
        return gpu.Read(target);
    }

    /// <summary>
    /// Fewer than one pixel in fifty may be covered by one picture and not the other, and fewer than one in fifty of those both cover may differ by more than <see cref="Levels"/>.
    /// </summary>
    private static void Compared(byte[] cpu, byte[] gpu)
    {
        Assert.Equal(cpu.Length, gpu.Length);
        int covered = 0, coverage = 0, colour = 0;
        var worst = new StringBuilder();
        for (var at = 0; at < cpu.Length; at += 4)
        {
            bool inCpu = cpu[at + 3] > 0, inGpu = gpu[at + 3] > 0;
            covered += inCpu || inGpu ? 1 : 0;
            if (inCpu != inGpu)
            {
                coverage++;
                continue;
            }

            if (!inCpu)
            {
                continue;
            }

            int most = 0;
            for (var channel = 0; channel < 4; channel++)
            {
                most = Math.Max(most, Math.Abs(cpu[at + channel] - gpu[at + channel]));
            }

            if (most > Levels)
            {
                colour++;
                if (colour <= 8)
                {
                    int pixel = at / 4;
                    worst.Append(CultureInfo.InvariantCulture,
                        $" ({pixel % Side},{pixel / Side}) cpu {cpu[at]},{cpu[at + 1]},{cpu[at + 2]},{cpu[at + 3]} gpu {gpu[at]},{gpu[at + 1]},{gpu[at + 2]},{gpu[at + 3]};");
                }
            }
        }

        Assert.True(covered > Side * Side / 20, $"too little drawn to compare: {covered} pixels");
        Assert.True(coverage * 50 < covered, $"{coverage} of {covered} pixels covered by one picture only");
        Assert.True(colour * 50 < covered, $"{colour} of {covered} pixels differ by more than {Levels}:{worst}");
    }

    /// <summary>A box two by two by two with a normal per face, twelve triangles - the shape every turn shows three faces of.</summary>
    private static SkinnedMesh Box()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();
        Vector3[] faces = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (Vector3 face in faces)
        {
            Vector3 across = MathF.Abs(face.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 down = Vector3.Cross(face, across);
            int at = positions.Count;
            positions.AddRange([face - across - down, face + across - down, face + across + down, face - across + down]);
            normals.AddRange([face, face, face, face]);
            indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }

        return SkinnedMesh.Of([.. positions], [.. normals], [.. indices], new Vector3(-1f), new Vector3(1f));
    }

    /// <summary>
    /// Quads facing the camera one behind another, a shape each, the first furthest - the camera looks along minus y, so the nearer is the larger y.
    /// </summary>
    private static SkinnedMesh Layers(int count)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var spots = new List<Vector2>();
        var indices = new List<int>();
        var shapes = new List<MeshShape>();
        for (var layer = 0; layer < count; layer++)
        {
            float y = layer * 0.5f, shrink = 1f - (layer * 0.15f);
            int at = positions.Count;
            positions.AddRange(
            [
                new Vector3(-shrink, y, -shrink), new Vector3(shrink, y, -shrink), new Vector3(shrink, y, shrink), new Vector3(-shrink, y, shrink),
            ]);
            normals.AddRange([Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY]);
            spots.AddRange([new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)]);
            shapes.Add(new MeshShape($"layer {layer}", indices.Count, 6));
            indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }

        return SkinnedMesh.Of(
            [.. positions], [.. normals], [.. indices], new Vector3(-1f, 0f, -1f), new Vector3(1f, Math.Max(0.5f, (count - 1) * 0.5f), 1f),
            [.. spots], shapes);
    }

    /// <summary>One colour, alpha and all, a texel four square.</summary>
    private static Mipmaps Plain(Vector4 colour)
    {
        var rgba = new byte[4 * 4 * 4];
        for (var at = 0; at < rgba.Length; at += 4)
        {
            rgba[at] = (byte)(colour.X * 255f);
            rgba[at + 1] = (byte)(colour.Y * 255f);
            rgba[at + 2] = (byte)(colour.Z * 255f);
            rgba[at + 3] = (byte)(colour.W * 255f);
        }

        return Mipmaps.Of(new GamePicture(4, 4, rgba))!;
    }

    /// <summary>A checker of squares, coloured by place so a texel read from the wrong spot shows - with holes where asked.</summary>
    private static Mipmaps Checker(int side, int square, bool alpha = false)
    {
        var rgba = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                int at = ((y * side) + x) * 4;
                bool dark = ((x / square) + (y / square)) % 2 == 0;
                rgba[at] = (byte)(x * 255 / side);
                rgba[at + 1] = (byte)(y * 255 / side);
                rgba[at + 2] = dark ? (byte)40 : (byte)220;
                rgba[at + 3] = alpha && dark ? (byte)0 : (byte)255;
            }
        }

        return Mipmaps.Of(new GamePicture(side, side, rgba))!;
    }
}
