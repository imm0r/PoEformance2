using System.Runtime.Versioning;
using PoEformance.Features;
using PoEformance.Game.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// The floor nobody has walked yet, as a sheet under the layout's lines.
/// </summary>
/// <remarks>
/// The game's own map fills the ground behind the player as they go - a dark sheet with a
/// pale edge - and this is the same sheet for the ground AHEAD of them, retreating as the
/// map is walked so that what the game has already drawn is not drawn a second time on top.
/// Where the two meet in the same colour, the layout ahead simply reads as more of the map.
///
/// Its OWN texture, and not part of the outline's, because the two live differently: the
/// outline is fixed for the life of an area and this changes with every step. Baked together,
/// each step would rebuild and re-upload a sixteen-megabyte picture. Apart, this is a small
/// one at MapCoverage's own step - four cells a texel, a quarter of a megabyte for a large
/// area - rebuilt only when the coverage says something changed and at most a few times a
/// second, while the outline is never touched. The second quad it costs per piece is the map's
/// few hundred thousand pixels drawn once more, which no GPU notices.
///
/// The texture holds coverage-times-unseen and nothing else: each texel as solid as the share
/// of its cells that is floor, and clear once the coarse cell its ground came from has been
/// within sight. It is white; the colour and the opacity are the tint it is drawn through, so
/// changing either costs nothing. See TerrainOutline.Floor for why the texel remembers where
/// its ground came from.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TerrainFloor : IDisposable
{
    /// <summary>The key the overlay's texture cache stores this under.</summary>
    private const string TextureKey = "poeformance.terrain.floor";

    /// <summary>
    /// Least time between two rebuilds, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The seen disc is eighty-eight cells across, and its edge moves as far as the player
    /// does: a quarter of a second of walking is a cell or two, which on the map is under a
    /// pixel. Rebuilding faster than that would be uploading the same picture again.
    /// </remarks>
    private const long RefreshMs = 250;

    /// <summary>What <see cref="_version"/> holds before anything has been built.</summary>
    private const int Unbuilt = int.MinValue;

    /// <summary>What <see cref="_version"/> holds when the whole floor was drawn as unseen.</summary>
    private const int Unknown = -1;

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;

    private TerrainGrid? _planned;
    private FloorPlan? _plan;
    private Image<Rgba32>? _image;
    private IntPtr _texture;
    private int _version = Unbuilt;
    private long _builtAt;

    // Set once the floor has given up, so a failure is reported once rather than every frame.
    private string? _failure;

    public TerrainFloor(Func<string, Image<Rgba32>, bool, IntPtr> upload, Action<string> release)
    {
        _upload = upload;
        _release = release;
    }

    /// <summary>The texture to draw, or zero when there is none.</summary>
    public IntPtr Texture => _texture;

    /// <summary>How many grid cells the texture spans across - padding included, see TerrainLayer.Level.</summary>
    public float CoverX => _plan is null ? 0f : _plan.Width * _plan.Step;

    /// <summary>How many grid cells the texture spans down.</summary>
    public float CoverY => _plan is null ? 0f : _plan.Height * _plan.Step;

    /// <summary>
    /// Brings the texture up to date for this grid and what has been seen of it. True when
    /// there is one to draw.
    /// </summary>
    /// <param name="coverage">
    /// What has been walked, or null when nothing is measuring - the whole floor is then
    /// drawn as unseen, which is what it was before anything measured.
    /// </param>
    /// <param name="nowMs">A monotonic clock, for the rebuild pacing.</param>
    public bool Ensure(TerrainGrid grid, MapCoverage? coverage, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (_failure is not null)
        {
            return false;
        }

        // Same reasoning as the outline's: this runs on the render thread, where an escaping
        // exception ends the process, and the upload is the one part not under this code's
        // control. A failed floor turns itself off and says so.
        try
        {
            if (!ReferenceEquals(_planned, grid))
            {
                Plan(grid);
            }

            // The coverage only counts when it is measuring THIS area; between areas it may
            // still hold the last one, and a hole from another map is worse than none.
            bool known = coverage is not null && coverage.Fits(grid);
            int version = known ? coverage!.Version : Unknown;
            if (version != _version && (_version == Unbuilt || nowMs - _builtAt >= RefreshMs))
            {
                Rebuild(known ? coverage : null, version, nowMs);
            }
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
            Release();
            Console.Error.WriteLine($"terrain floor disabled: {exception.Message}");
            return false;
        }

        return _texture != IntPtr.Zero;
    }

    /// <summary>Lays the floor out for a new area: the one pass over every cell.</summary>
    private void Plan(TerrainGrid grid)
    {
        Release();
        _image?.Dispose();
        _image = null;

        _plan = TerrainOutline.Floor(grid, MapCoverage.CoarseStep, isoHeightShift: true);
        _planned = grid;
        _version = Unbuilt;

        // One image for the life of the area, written into in place on every rebuild: the
        // renderer copies it into a texture and keeps nothing of it, so there is no reason to
        // allocate a new one each time. Contiguous, for the reason given in TerrainLayer.Build.
        Configuration configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        _image = new Image<Rgba32>(configuration, _plan.Width, _plan.Height);
    }

    /// <summary>Writes the floor minus what has been seen, and uploads it.</summary>
    private void Rebuild(MapCoverage? coverage, int version, long nowMs)
    {
        FloorPlan plan = _plan!;
        Image<Rgba32> image = _image!;
        if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
        {
            throw new InvalidOperationException("the floor image is not contiguous");
        }

        Span<Rgba32> texels = memory.Span;
        byte[] coverageOfFloor = plan.Coverage;
        int[] source = plan.Source;
        int coarseWidth = coverage?.CoarseWidth ?? 0;
        int coarseHeight = coverage?.CoarseHeight ?? 0;

        for (int i = 0; i < texels.Length; i++)
        {
            byte alpha = coverageOfFloor[i];
            if (alpha != 0 && coverage is not null && source[i] >= 0)
            {
                // The coverage's grid is the floor's, floored where this one is rounded up;
                // the last partial row and column ask the cell beside them.
                int from = source[i];
                int fromY = from / plan.Width;
                int fromX = from - (fromY * plan.Width);
                if (coverage.Seen(Math.Min(fromX, coarseWidth - 1), Math.Min(fromY, coarseHeight - 1)))
                {
                    alpha = 0;
                }
            }

            texels[i] = new Rgba32(255, 255, 255, alpha);
        }

        // Replaced, not updated: the renderer's upload creates a texture and returns the
        // cached one for a key it already holds, so the old one has to go first.
        _release(TextureKey);
        _texture = _upload(TextureKey, image, false);
        _version = version;
        _builtAt = nowMs;
    }

    private void Release()
    {
        if (_texture != IntPtr.Zero)
        {
            _release(TextureKey);
            _texture = IntPtr.Zero;
        }
    }

    /// <summary>The floor's size, or why there is none - for the readouts.</summary>
    public string Describe()
        => _failure is not null ? $"floor failed: {_failure}"
            : _plan is null ? "no floor"
            : $"floor {_plan.Width}x{_plan.Height}";

    public void Dispose()
    {
        Release();
        _image?.Dispose();
        _image = null;
        _plan = null;
        _planned = null;
    }
}
