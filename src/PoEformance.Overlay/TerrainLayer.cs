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
/// ONE TEXTURED QUAD, and that is the whole trick. The map transform is
///     screen = centre + ((dx - dy) * cos, (dz - (dx + dy)) * sin)
/// which, with the height term held at the player's own, is LINEAR in the grid deltas -
/// an affine map. So the grid's four corners projected through it define the image exactly,
/// and the GPU interpolates everything between them. The terrain becomes one draw call at
/// any zoom, on any map, and the projection stays the single one used for markers rather
/// than a second copy that can disagree with it.
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
    /// Largest texture edge. A big area's grid can exceed what a GPU will accept, and the
    /// outline stays legible when thinned - it is drawn a few hundred pixels wide either way.
    /// </summary>
    private const int MaxTextureEdge = 4096;

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;

    private TerrainGrid? _built;
    private IntPtr _texture;
    private int _textureWidth;
    private int _textureHeight;
    private uint _colour = 0xFF64C8FF;

    /// <param name="upload">Hands an image to the renderer and returns its handle.</param>
    /// <param name="release">Drops a previously uploaded image.</param>
    public TerrainLayer(Func<string, Image<Rgba32>, bool, IntPtr> upload, Action<string> release)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(release);
        _upload = upload;
        _release = release;
    }

    /// <summary>Outline colour, ABGR as ImGui packs it.</summary>
    public uint Colour
    {
        get => _colour;
        set => _colour = value;
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

        if (!ReferenceEquals(_built, grid))
        {
            Build(grid);
        }

        if (_texture == IntPtr.Zero)
        {
            return;
        }

        // The grid's four corners, in the same world units the markers use.
        Vector2 topLeft = Project(map, 0, 0, player);
        Vector2 topRight = Project(map, grid.Width, 0, player);
        Vector2 bottomRight = Project(map, grid.Width, grid.Height, player);
        Vector2 bottomLeft = Project(map, 0, grid.Height, player);

        // Off the map entirely - every corner outside it and the player not inside either.
        if (!map.Contains(topLeft) && !map.Contains(topRight)
            && !map.Contains(bottomRight) && !map.Contains(bottomLeft)
            && !map.Contains(map.Centre))
        {
            return;
        }

        // Clipped to the map's own rectangle, so a grid larger than the minimap does not
        // spill the level layout across the whole screen.
        draw.PushClipRect(
            new Vector2(map.Left, map.Top),
            new Vector2(map.Left + map.Width, map.Top + map.Height),
            intersect_with_current_clip_rect: true);

        draw.AddImageQuad(
            _texture,
            topLeft, topRight, bottomRight, bottomLeft,
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            _colour);

        draw.PopClipRect();
    }

    /// <summary>A grid cell projected onto the map, at the player's own height.</summary>
    /// <remarks>
    /// The height delta is held at zero on purpose. It is what keeps the transform affine,
    /// and therefore what lets four corners stand in for the whole grid; per-cell height
    /// would need a mesh instead of a quad, to reposition a wall by a few pixels.
    /// </remarks>
    private static Vector2 Project(MapView map, int gridX, int gridY, Vector3 player)
        => map.Project(
            gridX * MapView.WorldToGrid, gridY * MapView.WorldToGrid, player.Z,
            player.X, player.Y, player.Z);

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
        OutlineMask mask = TerrainOutline.Build(grid, MaxTextureEdge);
        using var image = new Image<Rgba32>(mask.Width, mask.Height);

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
    }

    /// <summary>The texture's size, for the diagnostic readout.</summary>
    public string Describe()
        => _texture == IntPtr.Zero ? "none" : $"{_textureWidth}x{_textureHeight}";

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
