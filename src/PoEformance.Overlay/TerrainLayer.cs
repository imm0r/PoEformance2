using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Game.Ui;
using PoEformance.Game.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the area's layout on the game's own map - the walls the map has not revealed yet.
/// </summary>
/// <remarks>
/// A TEXTURED MESH, and the shape of it follows from the map transform:
///     screen = centre + ((dx - dy) * cos, (dz - (dx + dy)) * sin)
/// At a FIXED height this is linear in the grid deltas, so any patch of ground is an affine
/// image and four projected corners define it exactly - the GPU interpolates the rest. That
/// made the whole area one quad, until the ground stopped being flat: dz is measured against
/// the PLAYER, so a single height draws every wall at the player's own elevation and the
/// outline slides whenever they walk up a staircase.
///
/// So the surface is a grid of patches, each still affine and still one textured quad, with
/// its corners at their own ground heights. They share one texture and batch into a single
/// draw call; what the mesh costs is projecting the corner grid, not drawing it. The
/// projection stays the same one the markers use rather than a second copy that can
/// disagree with it.
///
/// This is deliberately NOT how the AHK tool does it. That one composites GDI bitmaps with
/// rotated blits and a scroll cache, an effort that took its frame cost from 55 ms to 8 ms -
/// and every bit of which exists because AutoHotkey has no GPU to hand the transform to.
/// Porting that machinery here would be porting the workaround, not the feature.
///
/// The texture is built once per area on the render thread. It is a few megabytes of
/// byte-per-cell work; an area change already costs a loading screen, so the frame it lands
/// on is not one anybody is looking at.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TerrainLayer : IDisposable
{
    /// <summary>The key the overlay's texture cache stores this under.</summary>
    private const string TextureKey = "poeformance.terrain";

    /// <summary>
    /// Largest texture edge.
    /// </summary>
    /// <remarks>
    /// 2048 rather than a GPU's limit, for two reasons. The map it is drawn on is a few
    /// hundred pixels across, so the detail above this cannot be seen; and the image has to
    /// be allocated CONTIGUOUSLY (see Build), which at 4096 means a single 64 MB block for
    /// a picture nobody can tell apart from this one.
    /// </remarks>
    private const int MaxTextureEdge = 2048;

    /// <summary>
    /// Largest number of mesh patches along either axis.
    /// </summary>
    /// <remarks>
    /// A cap rather than one patch per tile: a big area is over a hundred tiles across, and
    /// past this the extra patches move the outline by less than a pixel while multiplying
    /// the corner projections that build them.
    /// </remarks>
    private const int MaxPatches = 96;

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;

    private TerrainGrid? _built;
    private IntPtr _texture;
    private int _textureWidth;
    private int _textureHeight;

    // How much of the grid the texture actually covers. Thinning floors the pixel count, so
    // on a large area this is a cell or two short of the grid - and stretching the image over
    // the full width instead would shift it by that much.
    private int _coverX = 1;
    private int _coverY = 1;
    private uint _colour = 0xFF64C8FF;
    private int _thickness = 1;

    // Set once the layer has given up, so a failure is reported once rather than every frame.
    private string? _failure;

    // Reused between frames: the mesh's corner grid, rebuilt each frame because the
    // projection follows the player, but never reallocated.
    private Vector2[] _corners = [];

    /// <param name="upload">Hands an image to the renderer and returns its handle.</param>
    /// <param name="release">Drops a previously uploaded image.</param>
    public TerrainLayer(Func<string, Image<Rgba32>, bool, IntPtr> upload, Action<string> release)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(release);
        _upload = upload;
        _release = release;
    }

    /// <summary>
    /// Outline colour, ABGR as ImGui packs it.
    /// </summary>
    /// <remarks>
    /// A TINT applied at draw time, not baked into the texture: the image is white and the
    /// quad is drawn through this, so changing the colour costs nothing and rebuilds
    /// nothing. Thickness cannot work that way - it changes the pixels - so that one does
    /// force a rebuild.
    /// </remarks>
    public uint Colour
    {
        get => _colour;
        set => _colour = value;
    }

    /// <summary>Line width in texture pixels. Changing it rebuilds the texture.</summary>
    public int Thickness
    {
        get => _thickness;
        set
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (clamped != _thickness)
            {
                _thickness = clamped;
                _built = null;   // the pixels change, so the texture has to be made again
                _failure = null; // and a previous failure deserves a fresh attempt
            }
        }
    }

    /// <summary>
    /// Draws the layout onto a map, if the terrain has loaded.
    /// </summary>
    /// <param name="player">
    /// The player's world position - the map projects everything relative to it, so the
    /// quad follows the player without the texture ever being rebuilt.
    /// </param>
    public void Draw(ImDrawListPtr draw, MapView map, TerrainGrid grid, Vector3 player)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (_failure is not null)
        {
            return;
        }

        if (!ReferenceEquals(_built, grid))
        {
            // Building uploads a texture through the renderer, which is the one thing here
            // that can fail for reasons this code does not control - a driver, an allocator,
            // an image too large for something downstream. This runs on the RENDER thread,
            // where an escaping exception ends the process: that is how a split image buffer
            // turned a cosmetic layer into a crash on entering a map. A failed layer turns
            // itself off and says so; the overlay keeps drawing everything else.
            try
            {
                Build(grid);
            }
            catch (Exception exception)
            {
                _failure = exception.Message;
                _built = grid;
                _texture = IntPtr.Zero;
                Console.Error.WriteLine($"terrain layer disabled: {exception.Message}");
            }
        }

        if (_texture == IntPtr.Zero)
        {
            return;
        }

        // Clipped to the map's own rectangle, so a grid larger than the minimap does not
        // spill the level layout across the whole screen.
        draw.PushClipRect(
            new Vector2(map.Left, map.Top),
            new Vector2(map.Left + map.Width, map.Top + map.Height),
            intersect_with_current_clip_rect: true);

        DrawMesh(draw, map, grid, player);

        draw.PopClipRect();
    }

    /// <summary>
    /// Draws the layout as a mesh of tile-sized quads, one ground height each.
    /// </summary>
    /// <remarks>
    /// A SINGLE quad would be the affine ideal, and it was - right up until the ground stops
    /// being flat. The map transform is
    ///     screen = centre + ((dx - dy) * cos, (dz - (dx + dy)) * sin)
    /// and dz is the height difference between a point and the PLAYER. Holding it at zero
    /// draws every wall as if it stood at the player's own elevation, so the outline slides
    /// against the game's map whenever the player walks up a staircase or a hill - reported
    /// exactly that way, and the height was the cause.
    ///
    /// So the corners get their own heights and the surface becomes a grid. Each patch is
    /// still affine and still one textured quad; the mesh only lets the height vary between
    /// them. At tile granularity that is a few thousand quads sharing one texture, which
    /// batches into a single draw call - the cost is building the corner grid, not drawing it.
    ///
    /// Which patches exist and where their edges land is <see cref="TerrainMesh"/>, kept out
    /// of here so it can be tested without a GPU. This part is projection and quads.
    /// </remarks>
    private void DrawMesh(ImDrawListPtr draw, MapView map, TerrainGrid grid, Vector3 player)
    {
        TerrainMesh mesh = TerrainMesh.For(grid, MaxPatches, _coverX, _coverY);

        // Measured against the player's OWN TILE, not the render component's terrain height.
        // Both describe the same ground, but by different arithmetic from different fields,
        // and taking both ends of the difference from one source makes any constant
        // disagreement between them cancel. The alternative reference is exact where these
        // two happen to agree and shifts the ENTIRE outline where they do not - including on
        // the flat maps that are correct today, which is not a trade worth making blind.
        //
        // What it costs is the player's own sub-tile height, the term this does not read (see
        // TerrainReader.ReadTileHeights): the outline stays put around the player and the far
        // side of the map moves by that much while they stand on a staircase. DescribeTerrain
        // reports both figures so the size of it can be read off rather than guessed at.
        float reference = grid.HasHeights
            ? grid.HeightAt(
                (int)(player.X / MapView.WorldToGrid),
                (int)(player.Y / MapView.WorldToGrid))
            : player.Z;

        // The corner grid, built once per frame: every patch shares its edges with its
        // neighbours, so projecting per patch would do the same work four times over and
        // let rounding open seams between them.
        int cornersX = mesh.CornersX;
        int cornersY = mesh.CornersY;
        if (_corners.Length < cornersX * cornersY)
        {
            _corners = new Vector2[cornersX * cornersY];
        }

        for (int cy = 0; cy < cornersY; cy++)
        {
            int gy = mesh.EdgeY(cy);
            for (int cx = 0; cx < cornersX; cx++)
            {
                int gx = mesh.EdgeX(cx);

                // The reference height itself when there is none to read: that makes the
                // height term zero everywhere, which is exactly the flat drawing this
                // replaced. A literal 0 would offset the map by the player's own elevation.
                float height = grid.HasHeights ? grid.HeightAt(gx, gy) : reference;
                _corners[(cy * cornersX) + cx] = map.Project(
                    gx * MapView.WorldToGrid, gy * MapView.WorldToGrid, height,
                    player.X, player.Y, reference);
            }
        }

        for (int py = 0; py < mesh.PatchesY; py++)
        {
            float v0 = mesh.V(py);
            float v1 = mesh.V(py + 1);

            for (int px = 0; px < mesh.PatchesX; px++)
            {
                Vector2 a = _corners[(py * cornersX) + px];
                Vector2 b = _corners[(py * cornersX) + px + 1];
                Vector2 c = _corners[((py + 1) * cornersX) + px + 1];
                Vector2 d = _corners[((py + 1) * cornersX) + px];

                // Off the map: skipping here is what keeps a large area cheap, since most
                // of its patches are outside a minimap at any moment.
                if (!map.Contains(a) && !map.Contains(b) && !map.Contains(c) && !map.Contains(d))
                {
                    continue;
                }

                float u0 = mesh.U(px);
                float u1 = mesh.U(px + 1);

                draw.AddImageQuad(
                    _texture, a, b, c, d,
                    new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1),
                    _colour);
            }
        }
    }

    /// <summary>Turns the walkable grid into an outline texture, once per area.</summary>
    private void Build(TerrainGrid grid)
    {
        _built = grid;
        if (_texture != IntPtr.Zero)
        {
            _release(TextureKey);
            _texture = IntPtr.Zero;
        }

        // Thin rather than refuse: a grid wider than a GPU will take still has a layout
        // worth seeing, and it is drawn a few hundred pixels across regardless.
        OutlineMask mask = TerrainOutline.Build(grid, MaxTextureEdge, _thickness);

        // CONTIGUOUS on purpose, and this is not a preference. ImageSharp splits anything
        // past a few megabytes across several buffers, and the renderer uploads a texture
        // by taking the image's SINGLE pixel span - which simply does not exist for a split
        // image. It reports that as "Make sure to initialize MemoryAllocator.Default!",
        // which names neither the cause nor the fix, and it took the whole tool down on the
        // first area whose terrain was large enough to split.
        //
        // A cloned configuration rather than the global default: the renderer loads its own
        // images through that, and this is not the place to change how they are allocated.
        Configuration configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;

        using var image = new Image<Rgba32>(configuration, mask.Width, mask.Height);

        // White where the boundary is, transparent everywhere else. The colour comes from
        // the tint at draw time, so changing it costs nothing and rebuilds nothing.
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < mask.Height; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < mask.Width; x++)
                {
                    row[x] = mask.IsSet(x, y) ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 0);
                }
            }
        });

        _texture = _upload(TextureKey, image, false);
        _textureWidth = mask.Width;
        _textureHeight = mask.Height;
        _coverX = Math.Min(grid.Width, mask.Width * mask.Step);
        _coverY = Math.Min(grid.Height, mask.Height * mask.Step);
    }

    /// <summary>The texture's size, or why there is none - for the readouts.</summary>
    public string Describe()
        => _failure is not null ? $"failed: {_failure}"
            : _texture == IntPtr.Zero ? "none"
            : $"{_textureWidth}x{_textureHeight}";

    public void Dispose()
    {
        if (_texture != IntPtr.Zero)
        {
            _release(TextureKey);
            _texture = IntPtr.Zero;
        }

        _built = null;
    }
}
