using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the area's layout on the game's own map - the walls the map has not revealed yet,
/// and the floor between them.
/// </summary>
/// <remarks>
/// ONE TEXTURED QUAD, and the shape of it follows from the map transform:
///     screen = centre + ((dx - dy) * cos, (dz - (dx + dy)) * sin)
/// At a FIXED height this is linear in the grid deltas, so the whole area is an affine image
/// and four projected corners define it exactly - the GPU interpolates the rest.
///
/// Height is what threatens that, since dz is measured against the PLAYER and a single height
/// draws every wall at the player's own elevation. It is handled by displacing each cell
/// diagonally in the TEXTURE by half its height, which the transform turns back into exactly
/// that height (see TerrainGrid.IsoHeightShift) - so the geometry stays one flat quad and the
/// correction is exact per cell. A mesh of height-carrying corners was tried first and is
/// strictly worse: thousands of projections a frame, and only exact at the corners.
///
/// The texture is kept at SEVERAL SIZES, and which one is drawn depends on the map. The
/// renderer has no mipmaps, so on a map where a texel is smaller than a pixel the sampler
/// skips texels, and a one-texel line becomes a row of dashes - which is what the minimap
/// showed, and what looked like the line "going under" on some ground. See TerrainPicture:
/// the coarser sizes are the same picture properly averaged down, and the draw picks the one
/// whose texel is nearest a pixel. Still one quad per piece; only the handle changes.
///
/// This is deliberately NOT how the AHK tool does it. That one composites GDI bitmaps with
/// rotated blits and a scroll cache, an effort that took its frame cost from 55 ms to 8 ms -
/// and every bit of which exists because AutoHotkey has no GPU to hand the transform to.
/// Porting that machinery here would be porting the workaround, not the feature.
///
/// The textures are built once per area on the render thread. It is a few megabytes of
/// byte-per-cell work; an area change already costs a loading screen, so the frame it lands
/// on is not one anybody is looking at.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TerrainLayer : IDisposable
{
    /// <summary>The key the overlay's texture cache stores this under, with the level appended.</summary>
    private const string TextureKey = "poeformance.terrain";

    /// <summary>
    /// Largest texture edge of the finest level.
    /// </summary>
    /// <remarks>
    /// 2048 rather than a GPU's limit, for two reasons. The map it is drawn on is a few
    /// hundred pixels across, so the detail above this cannot be seen; and the image has to
    /// be allocated CONTIGUOUSLY (see Build), which at 4096 means a single 64 MB block for
    /// a picture nobody can tell apart from this one.
    /// </remarks>
    private const int MaxTextureEdge = 2048;

    /// <summary>
    /// How many sizes of the picture are kept, the finest included.
    /// </summary>
    /// <remarks>
    /// Four reaches an eighth of the size, which covers a texel of a fifth of a pixel - past
    /// anything a map zoomed all the way out has shown. Each level past the first costs a
    /// quarter of the one before, so all of them together are a third more than the first
    /// alone.
    /// </remarks>
    private const int Levels = 4;

    /// <summary>
    /// How opaque the dark rim around the line is, out of 255 - or the line's own alpha,
    /// whichever is less.
    /// </summary>
    /// <remarks>
    /// Just under half. The rim is there to separate the line from a ground that matches it,
    /// not to draw a second, black outline - on the parts of the map where the pale line
    /// already reads, a solid black rim would be the thing the eye lands on instead.
    /// </remarks>
    private const byte RimAlpha = 127;

    /// <summary>
    /// How far the rim reaches beyond the line, in texture pixels.
    /// </summary>
    /// <remarks>
    /// Two, not one. On the large map a texture pixel is about a screen pixel, and the
    /// renderer samples the texture bilinearly with no mipmaps - so a one-pixel rim was
    /// smeared across its neighbours to a quarter of its opacity and could not be seen
    /// against or beside the line at all. Two survives the filter as a visible edge.
    /// </remarks>
    private const int RimWidth = 2;

    /// <summary>One uploaded size of the picture, and how many grid cells it spans.</summary>
    /// <remarks>
    /// The span is the texel count times the texel's size in cells, padding included - NOT
    /// the grid's size. A coarser level is rounded up to whole texels, and its last texel
    /// really does reach past the grid; drawn over the grid's own extent it would be
    /// squeezed by that much, an error invisible at the near corner and a texel at the far.
    /// </remarks>
    private readonly record struct Level(IntPtr Texture, int Width, int Height, float CoverX, float CoverY);

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;

    private TerrainGrid? _built;
    private Level[] _levels = [];

    // Grid cells per texel of the finest level. Thinning floors the pixel count, so on a
    // large area the finest level is a cell or two short of the grid - and stretching it over
    // the full width instead would shift it by that much.
    private int _step = 1;

    // How far the drawn line sits from the boundary it describes, in grid cells - see
    // OutlineMask.LeanCells. Subtracted from the mesh's own position, which is the only
    // place it can be corrected without changing what the texture's pixels mean.
    private float _lean;

    // The colour the quad is drawn through: the envelope of the line's and the fill's, see
    // TerrainPicture.Split. Opaque; the alphas are in the pixels.
    private uint _tint = 0xFFFF_FFFF;

    private uint _colour = OverlaySettings.ParseColour(OverlaySettings.Default.TerrainColour);
    private uint _fill = OverlaySettings.Default.TerrainFillPacked;
    private int _thickness = 1;
    private bool _rim = true;

    // What the last draw measured, for the readout: it is the one place the question "is a
    // texel smaller than a pixel here" can be answered, since it depends on the map's zoom.
    private float _texelPixels;
    private int _level;
    private bool _onLargeMap;

    // Set once the layer has given up, so a failure is reported once rather than every frame.
    private string? _failure;

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
    /// Outline colour, ABGR as ImGui packs it. Changing it rebuilds the texture.
    /// </summary>
    /// <remarks>
    /// The hue used to be a tint applied at draw time, free to change; the fill is what ended
    /// that. The quad is drawn through ONE colour and the fill has a colour of its own, so
    /// both are baked as ratios of a shared envelope and the envelope is the tint - see
    /// TerrainPicture.Split. A colour change therefore rebuilds, as thickness always has; the
    /// page sends a colour once per pick, not per drag, so that is a rebuild per decision.
    ///
    /// The ALPHA is baked as well, and always was. Tinting multiplies alpha as well as colour,
    /// so a half-transparent line tinted through the quad would take its rim down with it -
    /// to a fifth, on a real style file - and the rim exists precisely for the case where the
    /// line alone does not read. So the line's alpha goes into its own pixels, the rim keeps
    /// its own, and the quad is tinted opaque.
    /// </remarks>
    public uint Colour
    {
        get => _colour;
        set
        {
            if (value != _colour)
            {
                _colour = value;
                _built = null;
                _failure = null;
            }
        }
    }

    /// <summary>
    /// The floor's fill: its colour, with its opacity for alpha. Zero alpha is no fill.
    /// Changing it rebuilds the texture.
    /// </summary>
    /// <remarks>
    /// The floor as a translucent sheet under the line, the way the game's own map draws
    /// the parts it has revealed - a dark sheet with a pale edge. It is what makes the layout
    /// read as ROOMS rather than as a tangle of lines, and it is unmistakably a different
    /// thing from the game's unfilled wall markings on the minimap. It also survives being
    /// drawn small in a way no line can: an area averages down to the same area.
    /// </remarks>
    public uint Fill
    {
        get => _fill;
        set
        {
            if (value != _fill)
            {
                _fill = value;
                _built = null;
                _failure = null;
            }
        }
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
    /// Whether the line gets a dark, half-transparent rim on the side facing the world.
    /// Changing it rebuilds the texture, for the reason thickness does: the rim is pixels.
    /// </summary>
    public bool Rim
    {
        get => _rim;
        set
        {
            if (value != _rim)
            {
                _rim = value;
                _built = null;
                _failure = null;
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
            // Building uploads textures through the renderer, which is the one thing here
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
                _levels = [];
                Console.Error.WriteLine($"terrain layer disabled: {exception.Message}");
            }
        }

        if (_levels.Length == 0)
        {
            return;
        }

        // The size whose texel is nearest a screen pixel on THIS map - the minimap and the
        // large map differ by the map's zoom, and the zoom changes under the mouse wheel.
        _texelPixels = map.PixelsPerCellEdge * _step;
        _level = TerrainPicture.LevelFor(_texelPixels, _levels.Length);
        _onLargeMap = map.IsLargeMap;

        // Clipped to the parts of the map that may be drawn on, so a grid larger than the
        // minimap does not spill the level layout across the whole screen - and so the outline
        // stops at the game's own interface instead of running over the orbs and the skill bar.
        //
        // ONE PASS PER PIECE, because ImGui clips to a single rectangle and the region has
        // holes in it. That is affordable precisely because this layer is ONE quad: a piece
        // costs four projections and an AddImageQuad, and the pieces do not overlap, so no
        // pixel is drawn twice however many there are. The ordinary case is one piece.
        foreach (ScreenRect piece in map.Uncovered)
        {
            draw.PushClipRect(piece.TopLeft, piece.BottomRight, intersect_with_current_clip_rect: true);
            DrawQuad(draw, map, grid, player, _levels[_level]);
            draw.PopClipRect();
        }
    }

    /// <summary>
    /// Draws the layout as ONE quad. The heights are already in the picture.
    /// </summary>
    /// <remarks>
    /// At a fixed height the map transform is affine, so four projected corners define the
    /// whole area exactly and the GPU interpolates the rest. Height is what used to break
    /// that - the transform measures it against the player, so a single height drew every
    /// wall at the player's own elevation - and the answer here is not to bend the surface
    /// but to bake the height into the TEXTURE, as a diagonal displacement per cell (see
    /// TerrainGrid.IsoHeightShift). That is exact per cell rather than per mesh corner, and
    /// it costs four projections a frame instead of thousands.
    ///
    /// So the quad is flat, and it is flat at HEIGHT ZERO measured against the player's own
    /// ground: the displacement supplies each wall's height, and this supplies the "minus the
    /// player's" half of the difference.
    /// </remarks>
    private void DrawQuad(ImDrawListPtr draw, MapView map, TerrainGrid grid, Vector3 player, Level level)
    {
        // Without heights nothing was displaced, so the map is drawn at the player's own
        // elevation - flat, exactly as it was before any of this existed.
        float height = grid.HasHeights ? 0f : player.Z;

        Vector2 Corner(float gx, float gy) => map.Project(
            (gx - _lean) * MapView.WorldToGrid, (gy - _lean) * MapView.WorldToGrid, height,
            player.X, player.Y, player.Z);

        Vector2 a = Corner(0f, 0f);
        Vector2 b = Corner(level.CoverX, 0f);
        Vector2 c = Corner(level.CoverX, level.CoverY);
        Vector2 d = Corner(0f, level.CoverY);

        // Through the envelope of the two colours, opaque: the alphas are in the texture and
        // the colours come back out of the multiplication - see Colour.
        draw.AddImageQuad(
            level.Texture, a, b, c, d,
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            _tint);
    }

    /// <summary>Turns the walkable grid into the outline's textures, once per area.</summary>
    private void Build(TerrainGrid grid)
    {
        _built = grid;
        ReleaseTextures();

        // Thin rather than refuse: a grid wider than a GPU will take still has a layout
        // worth seeing, and it is drawn a few hundred pixels across regardless.
        OutlineMask mask = TerrainOutline.Build(grid, MaxTextureEdge, _thickness, isoHeightShift: true);
        byte[] line = mask.Cells;

        // With the rim off, the line's own pixels stand in for it: the painter then never
        // finds a rim pixel the line does not already cover, and no second buffer is built.
        byte[] rim = _rim ? TerrainOutline.Rim(mask, RimWidth) : line;

        // The floor, only when it is to be drawn: it is the one pass here that visits every
        // cell rather than the boundary.
        byte[]? fill = (_fill >> 24) == 0 ? null : TerrainOutline.Fill(grid, mask, isoHeightShift: true);

        (uint tint, uint lineBaked, uint fillBaked) = TerrainPicture.Split(_colour, _fill);
        TerrainPicture picture = TerrainPicture.Paint(mask, rim, fill, lineBaked, fillBaked, RimAlpha);

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

        var levels = new List<Level>(Levels);
        int cellsPerTexel = mask.Step;
        for (int level = 0; level < Levels; level++)
        {
            using var image = Image.LoadPixelData<Rgba32>(configuration, picture.Pixels, picture.Width, picture.Height);

            // The key in a local, not inline: a test reads every upload call out of the source
            // by regex to check the sRGB flag, and it deliberately stops at a nested bracket.
            string key = KeyOf(level);
            IntPtr texture = _upload(key, image, false);
            levels.Add(new Level(
                texture, picture.Width, picture.Height,
                picture.Width * cellsPerTexel, picture.Height * cellsPerTexel));

            // A picture already down to a texel has nothing left to halve.
            if (level + 1 == Levels || (picture.Width <= 1 && picture.Height <= 1))
            {
                break;
            }

            picture = picture.Halved();
            cellsPerTexel *= 2;
        }

        _levels = [.. levels];
        _step = mask.Step;
        _lean = mask.LeanCells;
        _tint = tint;
    }

    private static string KeyOf(int level) => $"{TextureKey}.{level}";

    /// <summary>
    /// Drops every level's texture, whether or not it was uploaded: the renderer ignores a
    /// key it does not hold, and a build that failed halfway leaves the early levels behind.
    /// </summary>
    private void ReleaseTextures()
    {
        for (int level = 0; level < Levels; level++)
        {
            _release(KeyOf(level));
        }

        _levels = [];
    }

    /// <summary>The textures' sizes and which one the map gets, or why there is none - for the readouts.</summary>
    /// <remarks>
    /// The pixels-per-texel figure is the measurement behind the levels: below one, the
    /// finest picture would be skipping texels on this map. It is only known once the map has
    /// been drawn on, since it depends on that map's zoom.
    /// </remarks>
    public string Describe()
    {
        if (_failure is not null)
        {
            return $"failed: {_failure}";
        }

        if (_levels.Length == 0)
        {
            return "none";
        }

        Level finest = _levels[0];
        string sizes = $"{finest.Width}x{finest.Height} in {_levels.Length} levels";
        return _texelPixels > 0f
            ? $"{sizes}, {_texelPixels:F2} px/texel on the {(_onLargeMap ? "large map" : "minimap")} -> level {_level}"
            : $"{sizes}, not drawn yet";
    }

    public void Dispose()
    {
        ReleaseTextures();
        _built = null;
    }
}
