using System.Numerics;
using System.Runtime.InteropServices;
using PoEformance.Game.Files;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PoEformance.Gpu;

/// <summary>
/// The game's light on the graphics card - SceneLight as MeshPicture draws it.
/// </summary>
/// <remarks>
/// THE SUN'S SHADOW MAP IS DRAWN ON THE CARD, on the processor's own texels: ShadowMap.Framed lays the
/// map out from the same vertices, the casters are the same shapes (solid, cut-out through their
/// texture, and shadow-only), and each texel keeps its nearest depth through the blend's minimum
/// into a float target - the compare-and-swap ShadowMap.Build keeps it by, without the threads. Kept
/// while the mesh stands still and the sun and the map's side hold, as the canvas keeps its own.
///
/// WHAT A PIXEL READS IS HANDED OVER AS THE PROCESSOR HOLDS IT: the point lights and the grid they are
/// looked up by, the cube's texels, the grade's table, and the sRGB and gamma tables - each once, by
/// reference, and let go as the meshes are when unused.
/// </remarks>
public sealed partial class ModelGpu
{
    private readonly CardBuffer _tables;
    private readonly Dictionary<SceneLight, LightBuffers> _lit = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, CardBuffer> _read = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Writes the light's numbers, draws the sun's shadow map where it casts, and binds what the lit pixel shader reads.
    /// </summary>
    /// <param name="light">The light.</param>
    /// <param name="buffers">The mesh on the card, already bound for drawing.</param>
    /// <param name="plan">Its runs.</param>
    /// <param name="view">The camera's turn - the way into the picture is its third column.</param>
    /// <param name="placed">The vertices the picture is drawn from, the pose's where it moves.</param>
    /// <param name="still">Whether those are the mesh's own, so a map drawn from them may be kept.</param>
    private void Lit(SceneLight light, MeshBuffers buffers, Plan plan, Matrix4x4 view, Vector3[] placed, bool still)
    {
        ID3D11DeviceContext context = _context;
        ShadowTarget? map = null;
        ShadowFrame frame = default;
        var drawn = false;
        if (light.SunShadows && light.SunColour != Vector3.Zero)
        {
            if (still && buffers.ShadowFor is { } was && was.Direction == light.SunDirection && was.Side == light.ShadowSide
                && buffers.Shadow is not null)
            {
                map = buffers.Shadow;
                frame = was.Frame;
            }
            else if (ShadowMap.Framed(placed, light.SunDirection, light.ShadowSide) is { } framed)
            {
                frame = framed;
                if (buffers.Shadow is not { } kept || kept.Side != framed.Side)
                {
                    buffers.Shadow?.Dispose();
                    buffers.Shadow = new ShadowTarget(_device, framed.Side);
                }

                map = buffers.Shadow;
                drawn = true;
                buffers.ShadowFor = still ? (light.SunDirection, light.ShadowSide, framed) : null;
            }
            else
            {
                buffers.ShadowFor = null;
            }
        }

        // THE VIEW'S THIRD COLUMN IS THE WAY INTO THE PICTURE in model space - MeshPicture.Drawing's _toEye.
        var into = new Vector3(view.M13, view.M23, view.M33);
        Vector3 toEye = into.LengthSquared() > 0f ? -Vector3.Normalize(into) : -Vector3.UnitZ;
        SceneLight.LightCells cells = light.Cells;
        ColourGrade? grade = light.Grade;
        int ambient = light.Ambient == SceneAmbient.Cube && light.Cube is not null ? 2
            : light.Ambient is SceneAmbient.Flat or SceneAmbient.Cube ? 1
            : 0;
        Written(_scene, new SceneConstants
        {
            CubeTurn = light.CubeTurn,
            SunColour = new Vector4(light.SunColour, light.SunColour != Vector3.Zero ? 1f : 0f),
            SunTravels = new Vector4(light.SunDirection, map is null ? 0f : 1f),
            ToEye = new Vector4(toEye, 0f),
            Surround = new Vector4(light.FlatAmbient, light.CubeBrightness, light.DirectLightEnvRatio, light.GiEnvOcclusion),
            Finish = new Vector4(light.Exposure, 0f, 0f, 0f),
            CellLeast = new Vector4(cells.Least, cells.Size),
            ShadowU = new Vector4(frame.U, frame.Least.X),
            ShadowV = new Vector4(frame.V, frame.Least.Y),
            ShadowW = new Vector4(frame.W, frame.Nearest),
            ShadowSize = map is null ? default : new Vector4(frame.PerUnit, 1f / frame.PerUnit, frame.Side, 0f),
            Ambient = new Int4(ambient, grade is null ? 0 : 1, ambient == 2 ? light.Cube!.Size : 0, light.PointCount),
            Cells = new Int4(cells.X, cells.Y, cells.Z, (cells.X * cells.Y * cells.Z) + 1),
            Graded = grade is null ? default : new Int4(grade.Width, grade.Height, grade.Depth, 0),
        });
        context.VSSetConstantBuffer(2, _scene);
        context.PSSetConstantBuffer(2, _scene);

        // TAKEN OFF BEFORE IT IS DRAWN INTO - a texture bound for reading is unbound as a target by the
        // runtime, with a warning, and the other way round.
        context.PSSetShaderResource(6, null!);
        if (drawn)
        {
            Cast(map!, plan);
        }

        LightBuffers? points = light.PointCount > 0 ? Points(light) : null;
        context.PSSetShaderResource(2, points?.Points.View!);
        context.PSSetShaderResource(3, points?.Reach.View!);
        context.PSSetShaderResource(4, ambient == 2 ? Read(light.Cube!, light.Cube!.Texels).View : null!);
        context.PSSetShaderResource(5, grade is null ? null! : Read(grade, MemoryMarshal.Cast<Vector3, float>(grade.Texels)).View);
        context.PSSetShaderResource(6, map?.View!);
    }

    /// <summary>The sun's shadow map drawn: every caster along the light, each texel keeping its nearest depth.</summary>
    private void Cast(ShadowTarget map, Plan plan)
    {
        ID3D11DeviceContext context = _context;
        context.ClearRenderTargetView(map.Target, new Color4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue));
        context.OMSetRenderTargets(map.Target, null);
        context.RSSetViewport(new Viewport(0f, 0f, map.Side, map.Side, 0f, 1f));
        context.OMSetBlendState(_least);
        context.OMSetDepthStencilState(_ignores);
        context.VSSetShader(_casting);
        context.PSSetShader(_away);
        foreach (Run run in plan.Casts)
        {
            // ONLY A CUT-OUT ONE READS ITS TEXTURE - ShadowMap.Build cuts nothing else.
            Mipmaps? worn = run.Blend == MaterialBlend.Cutout ? plan.Palette[run.Wears] : null;
            ID3D11ShaderResourceView? view = worn is null ? null : Skin(worn).View;
            context.PSSetShaderResource(0, view!);
            Written(_part, new Vector4(view is null ? 0f : 1f, view is null ? -1f : MeshPicture.CutoutAlpha, 0f, 0f));
            context.DrawIndexed(run.Count * 3, run.First * 3, 0);
        }

        // TAKEN OFF AS A TARGET BEFORE IT IS BOUND FOR READING: a resource still bound for output is
        // set to a shader slot as NULL by the runtime, and a NULL map reads nought everywhere - every
        // pixel in shadow, on every frame the map was drawn on. The first WARP run caught it on the
        // coarse map; the full one passed only because it was drawn twice, the second from the kept map.
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    /// <summary>
    /// What casts a shadow, in as few draws as the mesh's order allows: every solid and shadow-only run, one after another, as one draw, and each cut-out run that wears a texture on its own.
    /// </summary>
    /// <remarks>
    /// MERGED BECAUSE THE ORDER DOES NOT MATTER - the map keeps the nearest depth whichever is drawn
    /// first - and a solid caster reads no texture, so a room's thousands of runs are a few draws.
    /// </remarks>
    private static Run[] Casting(List<Run> runs, Mipmaps?[] palette)
    {
        var casts = new List<Run>();
        foreach (Run run in runs)
        {
            if (run.Blend is MaterialBlend.Alpha or MaterialBlend.Additive)
            {
                continue;
            }

            if (run.Blend == MaterialBlend.Cutout && palette[run.Wears] is not null)
            {
                casts.Add(run);
                continue;
            }

            if (casts.Count > 0 && casts[^1] is { Blend: MaterialBlend.Opaque } last && last.First + last.Count == run.First)
            {
                casts[^1] = last with { Count = last.Count + run.Count };
            }
            else
            {
                casts.Add(new Run(run.First, run.Count, 0, MaterialBlend.Opaque));
            }
        }

        return [.. casts];
    }

    /// <summary>The blend that keeps the smaller of what is there and what is drawn - the map's nearest depth.</summary>
    private static BlendDescription Least()
    {
        var least = new BlendDescription(Blend.One, Blend.One, Blend.One, Blend.One);
        least.RenderTarget[0].BlendOperation = BlendOperation.Min;
        least.RenderTarget[0].BlendOperationAlpha = BlendOperation.Min;
        return least;
    }

    /// <summary>
    /// ShadeProgram's sRGB tables, ColourGrade's gamma table and GlossLight's environment, one after another - where ModelShaders.Common's table offsets say.
    /// </summary>
    private static CardBuffer Tables(ID3D11Device device)
    {
        ReadOnlySpan<float> linear = ShadeProgram.LinearTable, srgb = ShadeProgram.SrgbTable, gamma = ColourGrade.Encoding;
        ReadOnlySpan<float> bias = GlossLight.BiasTable, scale = GlossLight.ScaleTable;
        var all = new float[linear.Length + srgb.Length + gamma.Length + bias.Length + scale.Length];
        Span<float> rest = all;
        linear.CopyTo(rest);
        rest = rest[linear.Length..];
        srgb.CopyTo(rest);
        rest = rest[srgb.Length..];
        gamma.CopyTo(rest);
        rest = rest[gamma.Length..];
        bias.CopyTo(rest);
        scale.CopyTo(rest[bias.Length..]);
        return CardBuffer.Of<float>(device, all, Format.R32_Float, all.Length);
    }

    /// <summary>A light's point lights and their grid on the card, uploaded the first time it is drawn.</summary>
    private LightBuffers Points(SceneLight light)
    {
        if (!_lit.TryGetValue(light, out LightBuffers? buffers))
        {
            ReadOnlySpan<SceneLight.Point> points = light.Points;
            var packed = new Vector4[points.Length * 3];
            for (var at = 0; at < points.Length; at++)
            {
                SceneLight.Point one = points[at];
                packed[at * 3] = new Vector4(one.Position, one.Cutoff);
                packed[(at * 3) + 1] = new Vector4(one.Colour, one.Core);
                packed[(at * 3) + 2] = new Vector4(one.Zero, 0f, 0f, 0f);
            }

            ReadOnlySpan<int> starts = light.CellStarts, entries = light.CellEntries;
            var reach = new int[starts.Length + Math.Max(1, entries.Length)];
            starts.CopyTo(reach);
            entries.CopyTo(reach.AsSpan(starts.Length));
            buffers = new LightBuffers(
                CardBuffer.Of<Vector4>(_device, packed, Format.R32G32B32A32_Float, packed.Length),
                CardBuffer.Of<int>(_device, reach, Format.R32_UInt, reach.Length));
            _lit[light] = buffers;
        }

        buffers.Used = _draws;
        return buffers;
    }

    /// <summary>A cube's or a grade's texels on the card, uploaded the first time they are read.</summary>
    private CardBuffer Read(object owner, ReadOnlySpan<float> texels)
    {
        if (!_read.TryGetValue(owner, out CardBuffer? buffer))
        {
            buffer = CardBuffer.Of(_device, texels, Format.R32_Float, texels.Length);
            _read[owner] = buffer;
        }

        buffer.Used = _draws;
        return buffer;
    }

    /// <summary>Lets go of the lights, cubes and grades that have not been drawn for <see cref="Kept"/> draws.</summary>
    private void ForgetLight()
    {
        foreach (KeyValuePair<SceneLight, LightBuffers> one in _lit.Where(one => _draws - one.Value.Used > Kept).ToArray())
        {
            one.Value.Dispose();
            _lit.Remove(one.Key);
        }

        foreach (KeyValuePair<object, CardBuffer> one in _read.Where(one => _draws - one.Value.Used > Kept).ToArray())
        {
            one.Value.Dispose();
            _read.Remove(one.Key);
        }
    }

    private void DisposeLight()
    {
        foreach (LightBuffers one in _lit.Values)
        {
            one.Dispose();
        }

        foreach (CardBuffer one in _read.Values)
        {
            one.Dispose();
        }

        _lit.Clear();
        _read.Clear();
        _tables.Dispose();
    }

    /// <summary>The light's constants - ModelShaders' Scene, field for field, sixteen-byte rows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix4x4 CubeTurn;
        public Vector4 SunColour;
        public Vector4 SunTravels;
        public Vector4 ToEye;
        public Vector4 Surround;
        public Vector4 Finish;
        public Vector4 CellLeast;
        public Vector4 ShadowU;
        public Vector4 ShadowV;
        public Vector4 ShadowW;
        public Vector4 ShadowSize;
        public Int4 Ambient;
        public Int4 Cells;
        public Int4 Graded;
    }

    /// <summary>An HLSL int4.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Int4(int X, int Y, int Z, int W);

    /// <summary>A light's point lights - three rows each - and its grid's starts and entries, one after the other.</summary>
    private sealed class LightBuffers(CardBuffer points, CardBuffer reach) : IDisposable
    {
        public CardBuffer Points { get; } = points;

        public CardBuffer Reach { get; } = reach;

        public long Used { get; set; }

        public void Dispose()
        {
            Reach.Dispose();
            Points.Dispose();
        }
    }

    /// <summary>A buffer a shader reads element by element, and the view it reads through.</summary>
    private sealed class CardBuffer : IDisposable
    {
        private readonly ID3D11Buffer _buffer;

        private CardBuffer(ID3D11Buffer buffer, ID3D11ShaderResourceView view)
        {
            _buffer = buffer;
            View = view;
        }

        public ID3D11ShaderResourceView View { get; }

        public long Used { get; set; }

        public static CardBuffer Of<T>(ID3D11Device device, ReadOnlySpan<T> data, Format format, int elements)
            where T : unmanaged
        {
            ID3D11Buffer buffer = device.CreateBuffer(data, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var described = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = elements },
            };
            return new CardBuffer(buffer, device.CreateShaderResourceView(buffer, described));
        }

        public void Dispose()
        {
            View.Dispose();
            _buffer.Dispose();
        }
    }

    /// <summary>The sun's shadow map on the card: one float a texel, the nearest depth.</summary>
    private sealed class ShadowTarget : IDisposable
    {
        private readonly ID3D11Texture2D _texture;

        public ShadowTarget(ID3D11Device device, int side)
        {
            Side = side;
            _texture = device.CreateTexture2D(new Texture2DDescription(
                Format.R32_Float, side, side, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            Target = device.CreateRenderTargetView(_texture);
            View = device.CreateShaderResourceView(_texture);
        }

        public int Side { get; }

        public ID3D11RenderTargetView Target { get; }

        public ID3D11ShaderResourceView View { get; }

        public void Dispose()
        {
            View.Dispose();
            Target.Dispose();
            _texture.Dispose();
        }
    }
}
