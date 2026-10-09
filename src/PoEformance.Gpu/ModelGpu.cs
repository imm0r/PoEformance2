using System.Numerics;
using System.Runtime.InteropServices;
using PoEformance.Game.Files;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PoEformance.Gpu;

/// <summary>
/// What one picture of a model is drawn with - MeshPicture.Of's arguments, for the graphics card.
/// </summary>
/// <param name="Mesh">What to draw.</param>
/// <param name="Turn">Rotation about the model's up axis, in radians.</param>
/// <param name="Tilt">Rotation towards the viewer, in radians.</param>
/// <param name="Ink">The colour where there is no texture; default for MeshPicture.UsualInk.</param>
/// <param name="Skin">The model's own texture, or null.</param>
/// <param name="Zoom">How much closer than the fitted view.</param>
/// <param name="Pan">Where the model's centre sits, as a share of the picture off its middle.</param>
/// <param name="Skins">One texture per shape, or null - see MeshPicture.Of.</param>
/// <param name="Blends">How each shape is put over what is behind it, or null for all solid.</param>
/// <param name="Positions">The posed vertices, or null to draw the mesh standing still.</param>
/// <param name="Normals">The posed normals, with <paramref name="Positions"/>.</param>
/// <param name="Light">The game's light, or null for the picture's own lamp - see MeshPicture.Canvas.Light.</param>
public readonly record struct ModelScene(
    SkinnedMesh Mesh,
    float Turn = 0f,
    float Tilt = 0f,
    Vector3 Ink = default,
    Mipmaps? Skin = null,
    float Zoom = 1f,
    Vector2 Pan = default,
    IReadOnlyList<Mipmaps?>? Skins = null,
    IReadOnlyList<MaterialBlend>? Blends = null,
    Vector3[]? Positions = null,
    Vector3[]? Normals = null,
    SceneLight? Light = null);

/// <summary>
/// Pictures of models drawn on the graphics card - MeshPicture's pictures, at the card's speed.
/// </summary>
/// <remarks>
/// THE SAME PICTURE, NOT A NEW ONE. The camera is MeshPicture.Camera, mapped onto the card's clip space
/// so that a vertex lands on the very pixel position the processor puts it at; the depth test is
/// "nearer wins, the first drawn on a tie", which is the processor's; the solid and cut-out shapes go
/// first in the mesh's order and the translucent ones after, testing depth and writing none; the light
/// and the texture reads are MeshPicture's (see ModelShaders). What cannot match exactly is said where
/// it shows: the card fills a pixel on an edge two triangles share once rather than twice, and works
/// out a texture's level per block of pixels rather than per triangle - which agree wherever the
/// picture is orthographic, as it always is here.
///
/// ON THE OVERLAY'S OWN DEVICE, and only on its render thread: the immediate context is not
/// thread-safe, and the overlay's frame is where the pane is drawn anyway. Whatever this leaves bound,
/// ImGui sets its own state after the frame's drawing returns (the vendored ClickableTransparentOverlay
/// sets every stage it uses before it draws).
///
/// MESHES AND TEXTURES ARE UPLOADED ONCE and kept while they are drawn, by reference: a drag redraws
/// the same mesh every frame, and a room's textures are hundreds of megabytes the card should be sent
/// once. What has not been drawn for a while is let go - see <see cref="Kept"/>.
///
/// THE GAME'S LIGHT (SceneLight) IS DRAWN AS MeshPicture DRAWS IT - see ModelGpu.Light.cs: the sun's
/// shadow map drawn on the card along the sun, on the processor's own texels, and every pixel lit by
/// the same numbers.
///
/// NOT YET: shade programs and the probe. A picture that needs either is drawn on the processor; the
/// caller asks <see cref="Can"/>.
/// </remarks>
public sealed partial class ModelGpu : IDisposable
{
    /// <summary>How many draws an upload may go unused before it is let go.</summary>
    private const int Kept = 600;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly bool _owns;
    private readonly ID3D11VertexShader _placed;
    private readonly ID3D11PixelShader _solid;
    private readonly ID3D11PixelShader _mixed;
    private readonly ID3D11PixelShader _added;
    private readonly ID3D11PixelShader _scened;
    private readonly ID3D11VertexShader _casting;
    private readonly ID3D11PixelShader _away;
    private readonly ID3D11VertexShader _whole;
    private readonly ID3D11PixelShader _straight;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _frame;
    private readonly ID3D11Buffer _part;
    private readonly ID3D11Buffer _scene;
    private readonly ID3D11SamplerState _wrap;
    private readonly ID3D11BlendState _covers;
    private readonly ID3D11BlendState _mixes;
    private readonly ID3D11BlendState _adds;
    private readonly ID3D11BlendState _least;
    private readonly ID3D11DepthStencilState _writes;
    private readonly ID3D11DepthStencilState _tests;
    private readonly ID3D11DepthStencilState _ignores;
    private readonly ID3D11RasterizerState _raster;
    private readonly int _largest;
    private readonly Dictionary<SkinnedMesh, MeshBuffers> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Mipmaps, SkinTexture> _skins = new(ReferenceEqualityComparer.Instance);
    private long _draws;
    private bool _disposed;

    private ModelGpu(ID3D11Device device, ID3D11DeviceContext context, bool owns, Compiled compiled)
    {
        _device = device;
        _context = context;
        _owns = owns;
        _placed = compiled.Placed;
        _solid = compiled.Solid;
        _mixed = compiled.Mixed;
        _added = compiled.Added;
        _scened = compiled.Scened;
        _casting = compiled.Casting;
        _away = compiled.Away;
        _whole = compiled.Whole;
        _straight = compiled.Straight;
        _layout = compiled.Layout;
        _frame = device.CreateBuffer(Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _part = device.CreateBuffer(Marshal.SizeOf<Vector4>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _scene = device.CreateBuffer(Marshal.SizeOf<SceneConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _wrap = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap,
            0f, 1, ComparisonFunction.Never, 0f, float.MaxValue));
        _covers = device.CreateBlendState(BlendDescription.Opaque);
        _mixes = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        _adds = device.CreateBlendState(new BlendDescription(Blend.One, Blend.One, Blend.One, Blend.One));
        _least = device.CreateBlendState(Least());
        _writes = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));
        _tests = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Less));
        _ignores = device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        _raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _largest = device.FeatureLevel >= FeatureLevel.Level_11_0 ? 16384 : 8192;
        FeatureLevel = device.FeatureLevel;
        _tables = Tables(device);
    }

    /// <summary>The level the device was made at.</summary>
    public FeatureLevel FeatureLevel { get; }

    /// <summary>
    /// The drawing for a device, or null and why not - a device too old, or shaders that would not compile.
    /// </summary>
    /// <param name="device">The overlay's device.</param>
    /// <param name="context">Its immediate context.</param>
    /// <param name="why">Why there is none, or empty.</param>
    public static ModelGpu? Of(ID3D11Device? device, ID3D11DeviceContext? context, out string why)
    {
        if (device is null || context is null)
        {
            why = "the overlay has no graphics device yet";
            return null;
        }

        return Made(device, context, owns: false, out why);
    }

    /// <summary>
    /// A drawing on WARP, Windows' own processor-run Direct3D - for tests, where there is no card to ask.
    /// </summary>
    public static ModelGpu? Warp(out string why)
    {
        if (!OperatingSystem.IsWindows())
        {
            why = "Direct3D 11 is Windows'";
            return null;
        }

        Result made = D3D11.D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.None,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        if (made.Failure)
        {
            why = $"WARP would not start: {made}";
            return null;
        }

        return Made(device, context, owns: true, out why);
    }

    private static ModelGpu? Made(ID3D11Device device, ID3D11DeviceContext context, bool owns, out string why)
    {
        Compiled? compiled = Compile(device, out why);
        if (compiled is null)
        {
            if (owns)
            {
                context.Dispose();
                device.Dispose();
            }

            return null;
        }

        return new ModelGpu(device, context, owns, compiled);
    }

    /// <summary>A target to draw into, this many pixels each way - the caller keeps it, see <see cref="ModelTarget"/>.</summary>
    public ModelTarget Target(int size) => new(_device, Math.Clamp(size, 1, MeshPicture.Widest));

    /// <summary>
    /// Whether this can draw a picture as the processor would, or why not.
    /// </summary>
    /// <param name="scene">The picture, its light among it.</param>
    /// <param name="shades">The shade programs it would be drawn with, or null.</param>
    /// <param name="why">Why not, or empty.</param>
    public bool Can(in ModelScene scene, IReadOnlyList<ShadeProgram?>? shades, out string why)
    {
        if (scene.Light is { SunShadows: true } light && light.SunColour != Vector3.Zero && light.ShadowSide > _largest)
        {
            why = $"the sun's shadow map is {light.ShadowSide} square, past the {_largest} this card takes";
            return false;
        }

        if (shades is { Count: > 0 } && shades.Any(one => one is { Bound: true }))
        {
            why = "materials' shader graphs are not drawn on the graphics card yet";
            return false;
        }

        foreach (Mipmaps? one in Worn(scene))
        {
            if (one is not null && (one.Top.Width > _largest || one.Top.Height > _largest))
            {
                why = $"a texture is {one.Top.Width} x {one.Top.Height}, past the {_largest} this card takes";
                return false;
            }
        }

        why = string.Empty;
        return true;
    }

    /// <summary>
    /// Draws a picture into a target - on the overlay's render thread only.
    /// </summary>
    /// <param name="target">Where, made by <see cref="Target"/>.</param>
    /// <param name="scene">What.</param>
    /// <param name="why">Why nothing was drawn, or empty.</param>
    /// <returns>False where the picture could not be drawn; the target then holds nothing to show.</returns>
    public bool Draw(ModelTarget target, in ModelScene scene, out string why)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _draws++;
        ID3D11DeviceContext context = _context;
        context.ClearRenderTargetView(target.AccumulatedTarget, new Color4(0f, 0f, 0f, 0f));
        context.ClearDepthStencilView(target.Depth, DepthStencilClearFlags.Depth, 1f, 0);

        SkinnedMesh mesh = scene.Mesh;
        MeshPicture.Camera camera = MeshPicture.Camera.Of(mesh, scene.Turn, scene.Tilt, scene.Zoom, scene.Pan);
        if (mesh is null || !camera.Ready)
        {
            Resolved(target);
            why = string.Empty;
            return true;
        }

        MeshBuffers buffers = Buffers(mesh);
        Vector3[] positions = mesh.Positions;
        bool posed = scene.Positions is { } moved && moved.Length == mesh.Positions.Length
            && scene.Normals is { } turned && turned.Length == mesh.Normals.Length;
        if (posed)
        {
            buffers.Pose(_device, context, scene.Positions!, scene.Normals!);
        }

        Plan plan = buffers.Planned(scene);
        Vector3[] placed = posed ? scene.Positions! : positions;

        // MESHPICTURE'S CAMERA ON THE CARD'S CLIP SPACE: a point the processor puts at pixel
        // (x, y) - its view x and y times the scale, plus the centre, in shares of the side - lands
        // at clip ((2x/side) - 1, 1 - (2y/side)), so a vertex is at the same place to the bit. Depth
        // is the view's z, nearer less, laid into nought to one over four times the box's diagonal so
        // a pose reaching past its box is not clipped.
        float scale = camera.Scale;
        Vector2 centre = camera.Centre;
        float reach = 4f * MathF.Max((mesh.Most - mesh.Least).Length(), 1e-3f);
        var onto = new Matrix4x4(
            2f * scale, 0f, 0f, 0f,
            0f, -2f * scale, 0f, 0f,
            0f, 0f, 0.5f / reach, 0f,
            (2f * centre.X) - 1f, 1f - (2f * centre.Y), 0.5f, 1f);
        Vector3 ink = scene.Ink == default ? MeshPicture.UsualInk : scene.Ink;
        var frame = new FrameConstants
        {
            Clip = camera.View * onto,
            View = camera.View,
            Lamp = new Vector4(MeshPicture.Lamp, 0f),
            Ink = new Vector4(ink, 0.5f),
            Shade = new Vector4(MeshPicture.Ambient, 0f, 0f, 0f),
        };
        Written(_frame, frame);

        context.RSSetState(_raster);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_layout);
        context.IASetVertexBuffer(0, posed ? buffers.PosedPositions! : buffers.Positions, 12, 0);
        context.IASetVertexBuffer(1, posed ? buffers.PosedNormals! : buffers.Normals, 12, 0);
        context.IASetVertexBuffer(2, buffers.Coordinates, 8, 0);
        context.IASetIndexBuffer(buffers.Indices, Format.R32_UInt, 0);
        context.VSSetConstantBuffer(0, _frame);
        context.PSSetConstantBuffer(0, _frame);
        context.PSSetConstantBuffer(1, _part);
        context.PSSetSampler(0, _wrap);

        // THE SUN'S SHADOW MAP BEFORE THE PICTURE, into its own target - see Lit.
        SceneLight? light = scene.Light;
        if (light is not null)
        {
            Lit(light, buffers, plan, camera.View, placed, still: !posed);
        }

        context.OMSetRenderTargets(target.AccumulatedTarget, target.Depth);
        context.RSSetViewport(new Viewport(0f, 0f, target.Size, target.Size, 0f, 1f));
        context.VSSetShader(_placed);

        // THE SOLID AND CUT-OUT SHAPES FIRST, in the mesh's order, writing depth - then the translucent
        // ones in the mesh's order, testing it and writing none, as MeshPicture.Drawing.Band does.
        context.OMSetBlendState(_covers);
        context.OMSetDepthStencilState(_writes);
        context.PSSetShader(light is null ? _solid : _scened);
        foreach (Run run in plan.Runs)
        {
            if (run.Blend is MaterialBlend.Opaque or MaterialBlend.Cutout)
            {
                Drawn(run, plan);
            }
        }

        if (plan.Translucent)
        {
            context.OMSetDepthStencilState(_tests);
            foreach (Run run in plan.Runs)
            {
                if (run.Blend is MaterialBlend.Alpha or MaterialBlend.Additive)
                {
                    context.OMSetBlendState(run.Blend == MaterialBlend.Additive ? _adds : _mixes);
                    context.PSSetShader(run.Blend == MaterialBlend.Additive ? _added : _mixed);
                    Drawn(run, plan);
                }
            }
        }

        if (light is not null)
        {
            Unlit();
        }

        Resolved(target);
        Forget();
        why = string.Empty;
        return true;
    }

    /// <summary>
    /// The finished picture's bytes, read back from the card - for tests and for anything that needs the pixels.
    /// </summary>
    public byte[] Read(ModelTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        int size = target.Size;
        using ID3D11Texture2D staging = _device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm, size, size, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _context.CopyResource(staging, target.ShownTexture);
        MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bytes = new byte[size * size * 4];
        try
        {
            for (var row = 0; row < size; row++)
            {
                Marshal.Copy(mapped.DataPointer + (row * mapped.RowPitch), bytes, row * size * 4, size * 4);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        return bytes;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (MeshBuffers one in _meshes.Values)
        {
            one.Dispose();
        }

        foreach (SkinTexture one in _skins.Values)
        {
            one.Dispose();
        }

        _meshes.Clear();
        _skins.Clear();
        DisposeLight();
        _raster.Dispose();
        _ignores.Dispose();
        _tests.Dispose();
        _writes.Dispose();
        _least.Dispose();
        _adds.Dispose();
        _mixes.Dispose();
        _covers.Dispose();
        _wrap.Dispose();
        _scene.Dispose();
        _part.Dispose();
        _frame.Dispose();
        _layout.Dispose();
        _straight.Dispose();
        _whole.Dispose();
        _away.Dispose();
        _casting.Dispose();
        _scened.Dispose();
        _added.Dispose();
        _mixed.Dispose();
        _solid.Dispose();
        _placed.Dispose();
        if (_owns)
        {
            _context.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>One run of triangles with its texture and the cut-out's threshold.</summary>
    private void Drawn(Run run, Plan plan)
    {
        Mipmaps? worn = plan.Palette[run.Wears];
        ID3D11ShaderResourceView? view = worn is null ? null : Skin(worn).View;
        _context.PSSetShaderResource(0, view!);
        Written(_part, new Vector4(view is null ? 0f : 1f, run.Blend == MaterialBlend.Cutout ? MeshPicture.CutoutAlpha : -1f, 0f, 0f));
        _context.DrawIndexed(run.Count * 3, run.First * 3, 0);
    }

    /// <summary>The float target turned into the straight bytes ImGui shows - see ModelShaders.Resolve.</summary>
    private void Resolved(ModelTarget target)
    {
        ID3D11DeviceContext context = _context;
        context.OMSetRenderTargets(target.ShownTarget, null);
        context.RSSetViewport(new Viewport(0f, 0f, target.Size, target.Size, 0f, 1f));
        context.RSSetState(_raster);
        context.OMSetBlendState(_covers);
        context.OMSetDepthStencilState(_ignores);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_whole);
        context.PSSetShader(_straight);
        context.PSSetShaderResource(0, target.AccumulatedView);
        context.Draw(3, 0);

        // UNBOUND AGAIN, so the float target can be drawn into on the next frame - a texture bound
        // for reading is taken off as a target by the runtime, with a warning, and the other way round.
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    /// <summary>A constant buffer's contents replaced.</summary>
    private unsafe void Written<T>(ID3D11Buffer buffer, T value)
        where T : unmanaged
    {
        MappedSubresource mapped = _context.Map(buffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(T*)mapped.DataPointer = value;
        _context.Unmap(buffer, 0);
    }

    /// <summary>A mesh's buffers, uploaded on first use.</summary>
    private MeshBuffers Buffers(SkinnedMesh mesh)
    {
        if (!_meshes.TryGetValue(mesh, out MeshBuffers? buffers))
        {
            buffers = new MeshBuffers(_device, mesh);
            _meshes[mesh] = buffers;
        }

        buffers.Used = _draws;
        return buffers;
    }

    /// <summary>A texture with all its levels, uploaded on first use.</summary>
    private SkinTexture Skin(Mipmaps skin)
    {
        if (!_skins.TryGetValue(skin, out SkinTexture? texture))
        {
            texture = new SkinTexture(_device, skin);
            _skins[skin] = texture;
        }

        texture.Used = _draws;
        return texture;
    }

    /// <summary>Lets go of what has not been drawn for <see cref="Kept"/> draws.</summary>
    private void Forget()
    {
        if (_draws % 64 != 0)
        {
            return;
        }

        foreach (KeyValuePair<SkinnedMesh, MeshBuffers> one in _meshes.Where(one => _draws - one.Value.Used > Kept).ToArray())
        {
            one.Value.Dispose();
            _meshes.Remove(one.Key);
        }

        foreach (KeyValuePair<Mipmaps, SkinTexture> one in _skins.Where(one => _draws - one.Value.Used > Kept).ToArray())
        {
            one.Value.Dispose();
            _skins.Remove(one.Key);
        }

        ForgetLight();
    }

    /// <summary>Every texture a picture may read.</summary>
    private static IEnumerable<Mipmaps?> Worn(ModelScene scene)
    {
        yield return scene.Skin;
        if (scene.Skins is { } skins)
        {
            foreach (Mipmaps? one in skins)
            {
                yield return one;
            }
        }
    }

    private static Compiled? Compile(ID3D11Device device, out string why)
    {
        (string Source, string Entry, string Profile)[] wanted =
        [
            (ModelShaders.Model, "Placed", "vs_4_0"),
            (ModelShaders.Model, "Solid", "ps_4_0"),
            (ModelShaders.Model, "Mixed", "ps_4_0"),
            (ModelShaders.Model, "Added", "ps_4_0"),
            (ModelShaders.Model, "Scened", "ps_4_0"),
            (ModelShaders.Model, "Casting", "vs_4_0"),
            (ModelShaders.Model, "Away", "ps_4_0"),
            (ModelShaders.Resolve, "Whole", "vs_4_0"),
            (ModelShaders.Resolve, "Straight", "ps_4_0"),
        ];

        var blobs = new Blob?[wanted.Length];
        try
        {
            why = string.Empty;
            for (var at = 0; at < wanted.Length; at++)
            {
                blobs[at] = Compiled.Code(wanted[at].Source, wanted[at].Entry, wanted[at].Profile, out why);
                if (blobs[at] is null)
                {
                    return null;
                }
            }

            InputElementDescription[] corners =
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1, InputClassification.PerVertexData, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2, InputClassification.PerVertexData, 0),
            ];

            return new Compiled(
                device.CreateVertexShader(blobs[0]!),
                device.CreatePixelShader(blobs[1]!),
                device.CreatePixelShader(blobs[2]!),
                device.CreatePixelShader(blobs[3]!),
                device.CreatePixelShader(blobs[4]!),
                device.CreateVertexShader(blobs[5]!),
                device.CreatePixelShader(blobs[6]!),
                device.CreateVertexShader(blobs[7]!),
                device.CreatePixelShader(blobs[8]!),
                device.CreateInputLayout(corners, blobs[0]!));
        }
        finally
        {
            foreach (Blob? one in blobs)
            {
                one?.Dispose();
            }
        }
    }

    /// <summary>The frame's constants - ModelShaders' Frame, field for field, sixteen-byte rows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 Clip;
        public Matrix4x4 View;
        public Vector4 Lamp;
        public Vector4 Ink;
        public Vector4 Shade;
    }

    private sealed record Compiled(
        ID3D11VertexShader Placed,
        ID3D11PixelShader Solid,
        ID3D11PixelShader Mixed,
        ID3D11PixelShader Added,
        ID3D11PixelShader Scened,
        ID3D11VertexShader Casting,
        ID3D11PixelShader Away,
        ID3D11VertexShader Whole,
        ID3D11PixelShader Straight,
        ID3D11InputLayout Layout)
    {
        /// <summary>One entry point compiled, or null and the compiler's words.</summary>
        public static Blob? Code(string source, string entry, string profile, out string why)
        {
            Result result = Compiler.Compile(source, entry, entry, profile, out Blob blob, out Blob errors);
            try
            {
                if (result.Failure || blob is null)
                {
                    why = $"the {entry} shader would not compile: "
                        + (errors is null ? result.ToString() : Marshal.PtrToStringAnsi(errors.BufferPointer) ?? result.ToString());
                    blob?.Dispose();
                    return null;
                }

                why = string.Empty;
                return blob;
            }
            finally
            {
                errors?.Dispose();
            }
        }
    }

    /// <summary>A stretch of triangles in the mesh's order that wear one texture and blend one way.</summary>
    private readonly record struct Run(int First, int Count, int Wears, MaterialBlend Blend);

    /// <summary>A picture's runs and textures, worked out once per mesh, textures and blends - and the runs that cast the sun's shadow.</summary>
    /// <param name="Runs">Every run in the mesh's order, the shadow-only ones among them - the drawing's passes take the blends they draw.</param>
    /// <param name="Casts">What casts a shadow - see <see cref="Casting"/>.</param>
    /// <param name="Palette">The textures, none at nought.</param>
    /// <param name="Translucent">Whether any run is mixed or added.</param>
    private sealed record Plan(Run[] Runs, Run[] Casts, Mipmaps?[] Palette, bool Translucent);

    /// <summary>A mesh on the card: its vertices, normals, coordinates and indices, and a pose's where it moves.</summary>
    private sealed class MeshBuffers : IDisposable
    {
        private (Mipmaps? Skin, IReadOnlyList<Mipmaps?>? Skins, IReadOnlyList<MaterialBlend>? Blends, Plan Plan)? _planned;
        private readonly SkinnedMesh _mesh;
        private readonly bool _coordinated;

        public MeshBuffers(ID3D11Device device, SkinnedMesh mesh)
        {
            _mesh = mesh;
            _coordinated = mesh.Coordinated;
            Positions = device.CreateBuffer<Vector3>(mesh.Positions, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            Normals = device.CreateBuffer<Vector3>(mesh.Normals, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            Coordinates = device.CreateBuffer<Vector2>(mesh.Coordinates, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            Indices = device.CreateBuffer<int>(mesh.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        }

        public long Used { get; set; }

        public ID3D11Buffer Positions { get; }

        public ID3D11Buffer Normals { get; }

        public ID3D11Buffer Coordinates { get; }

        public ID3D11Buffer Indices { get; }

        public ID3D11Buffer? PosedPositions { get; private set; }

        public ID3D11Buffer? PosedNormals { get; private set; }

        /// <summary>The sun's shadow map last drawn for this mesh - see ModelGpu.Lit.</summary>
        public ShadowTarget? Shadow { get; set; }

        /// <summary>What <see cref="Shadow"/> was drawn for, kept only while the mesh stands still - the canvas's rule.</summary>
        public (Vector3 Direction, int Side, ShadowFrame Frame)? ShadowFor { get; set; }

        /// <summary>A pose's vertices and normals written over the moving copies, made the first time.</summary>
        public void Pose(ID3D11Device device, ID3D11DeviceContext context, Vector3[] positions, Vector3[] normals)
        {
            int bytes = positions.Length * 12;
            PosedPositions ??= device.CreateBuffer(bytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            PosedNormals ??= device.CreateBuffer(bytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            Copied(context, PosedPositions, positions);
            Copied(context, PosedNormals, normals);
        }

        /// <summary>
        /// Which texture and blend every triangle has, as MeshPicture's Palette, Worn and Blended work them out, in runs.
        /// </summary>
        /// <remarks>
        /// KEPT WHILE THE LISTS ARE THE SAME, compared by reference as the portrait compares them: a
        /// room is a million triangles, and working this out per frame was per-frame garbage.
        /// </remarks>
        public Plan Planned(in ModelScene scene)
        {
            if (_planned is { } had && ReferenceEquals(had.Skin, scene.Skin) && ReferenceEquals(had.Skins, scene.Skins)
                && ReferenceEquals(had.Blends, scene.Blends))
            {
                return had.Plan;
            }

            Plan plan = Worked(scene);
            _planned = (scene.Skin, scene.Skins, scene.Blends, plan);
            return plan;
        }

        private Plan Worked(in ModelScene scene)
        {
            SkinnedMesh mesh = _mesh;
            int triangles = mesh.Triangles;
            Mipmaps? usable = scene.Skin is not null && _coordinated ? scene.Skin : null;

            // THE PALETTE: none at nought, then the shapes' textures, then the model's own.
            var palette = new List<Mipmaps?> { null };
            if (_coordinated)
            {
                foreach (Mipmaps? one in scene.Skins ?? [])
                {
                    if (one is not null && !palette.Contains(one))
                    {
                        palette.Add(one);
                    }
                }

                if (usable is not null && !palette.Contains(usable))
                {
                    palette.Add(usable);
                }
            }

            var wears = new int[triangles];
            Array.Fill(wears, Math.Max(0, palette.IndexOf(usable)));
            if (scene.Skins is { Count: > 0 } skins && _coordinated)
            {
                for (var shape = 0; shape < mesh.Shapes.Count && shape < skins.Count; shape++)
                {
                    int at = palette.IndexOf(skins[shape]);
                    if (at >= 0)
                    {
                        Fill(wears, mesh.Shapes[shape], at, triangles);
                    }
                }
            }

            var blends = new MaterialBlend[triangles];
            var translucent = false;
            if (scene.Blends is { Count: > 0 } said)
            {
                for (var shape = 0; shape < mesh.Shapes.Count && shape < said.Count; shape++)
                {
                    if (said[shape] != MaterialBlend.Opaque)
                    {
                        Fill(blends, mesh.Shapes[shape], said[shape], triangles);
                        translucent |= said[shape] is MaterialBlend.Alpha or MaterialBlend.Additive && mesh.Shapes[shape].Count > 0;
                    }
                }
            }

            var runs = new List<Run>();
            for (int first = 0, at = 1; first < triangles; at++)
            {
                if (at == triangles || wears[at] != wears[first] || blends[at] != blends[first])
                {
                    runs.Add(new Run(first, at - first, wears[first], blends[first]));
                    first = at;
                }
            }

            Mipmaps?[] worn = [.. palette];
            return new Plan([.. runs], Casting(runs, worn), worn, translucent);
        }

        private static void Fill<T>(T[] into, MeshShape shape, T value, int triangles)
        {
            int from = Math.Clamp(shape.From / 3, 0, triangles);
            int upto = Math.Clamp((shape.From + shape.Count) / 3, from, triangles);
            Array.Fill(into, value, from, upto - from);
        }

        private static unsafe void Copied(ID3D11DeviceContext context, ID3D11Buffer buffer, Vector3[] from)
        {
            MappedSubresource mapped = context.Map(buffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            from.AsSpan().CopyTo(new Span<Vector3>((void*)mapped.DataPointer, from.Length));
            context.Unmap(buffer, 0);
        }

        public void Dispose()
        {
            Shadow?.Dispose();
            PosedNormals?.Dispose();
            PosedPositions?.Dispose();
            Indices.Dispose();
            Coordinates.Dispose();
            Normals.Dispose();
            Positions.Dispose();
        }
    }

    /// <summary>A texture on the card with every level Mipmaps made - the same levels the processor reads.</summary>
    private sealed class SkinTexture : IDisposable
    {
        private readonly ID3D11Texture2D _texture;

        public SkinTexture(ID3D11Device device, Mipmaps skin)
        {
            int levels = skin.Count;
            var pinned = new GCHandle[levels];
            var data = new SubresourceData[levels];
            try
            {
                for (var level = 0; level < levels; level++)
                {
                    GamePicture one = skin[level];
                    pinned[level] = GCHandle.Alloc(one.Rgba, GCHandleType.Pinned);
                    data[level] = new SubresourceData(pinned[level].AddrOfPinnedObject(), one.Width * 4, 0);
                }

                _texture = device.CreateTexture2D(
                    new Texture2DDescription(
                        Format.R8G8B8A8_UNorm, skin.Top.Width, skin.Top.Height, 1, levels, BindFlags.ShaderResource, ResourceUsage.Immutable),
                    data);
            }
            finally
            {
                foreach (GCHandle one in pinned)
                {
                    if (one.IsAllocated)
                    {
                        one.Free();
                    }
                }
            }

            View = device.CreateShaderResourceView(_texture);
        }

        public long Used { get; set; }

        public ID3D11ShaderResourceView View { get; }

        public void Dispose()
        {
            View.Dispose();
            _texture.Dispose();
        }
    }
}
