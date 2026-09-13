using System.Runtime.Versioning;
using PoEformance.Features;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PoEformance.Overlay;

/// <summary>
/// Turns a user's own picture into a texture a marker can be drawn with.
/// </summary>
/// <remarks>
/// The decoding half of custom icons. Which file a path means and whether it is worth another
/// look is <see cref="IconFiles"/>' job; this one opens it, shrinks it, and hands it to the
/// renderer.
///
/// Every failure ends the same way: no texture, and whoever asked draws the built-in shape.
/// That is not a nicety - a marker whose file went missing has to still be a marker, because
/// a missing marker on a map reads as "there is nothing there".
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class IconCache : IDisposable
{
    /// <summary>
    /// Largest edge an icon is kept at.
    /// </summary>
    /// <remarks>
    /// A marker is a few pixels across on the map, so detail past this cannot be seen - and
    /// somebody WILL point this at a 4000-pixel photograph, which as a texture is 64 MB of
    /// video memory for something drawn at eight pixels.
    /// </remarks>
    public const int MaxEdge = 128;

    /// <summary>
    /// Largest edge for something drawn big, like a banner across the screen.
    /// </summary>
    /// <remarks>
    /// A marker is eight pixels and a banner is most of the screen's width, so one limit
    /// cannot serve both: at 128 an ornate plate arrives as a 21-pixel-tall smear. The cap
    /// still exists, because the 4000-pixel photograph is still out there.
    /// </remarks>
    public const int MaxWideEdge = 1024;

    /// <summary>
    /// Largest edge for a SHEET, whose tiles are addressed by coordinate.
    /// </summary>
    /// <remarks>
    /// NOT A PREFERENCE ABOUT DETAIL - it has to be larger than the sheet, or the sheet is
    /// wrong. A tile is picked out with texture coordinates derived from a tile size in
    /// PIXELS, so a sheet shrunk on the way in has a grid that no longer matches the number
    /// it is being cut up by, and every icon drawn lands part way between two of them.
    ///
    /// This used to be 4096, which was comfortably clear of the 256x3392 sheet the reference
    /// ships and 832 pixels short of ours: <c>assets/icons.png</c> is 896x4928, so at the old
    /// limit it arrived scaled to 745x4096 and its 64-pixel cells measured 53.2. Raised to the
    /// 8192 that every D3D11 feature level guarantees, which is the next real ceiling rather
    /// than one picked to fit today's file by a margin somebody has to notice again later.
    /// </remarks>
    public const int MaxSheetEdge = 8192;

    /// <summary>
    /// Whether a picture is handed to the renderer as an sRGB texture. It is not, and the
    /// reason is the RENDER TARGET rather than the picture.
    /// </summary>
    /// <remarks>
    /// WHY EVERY ICON CAME OUT DARK. Asking for sRGB picks
    /// <c>R8G8B8A8_UNorm_SRgb</c>, which makes the sampler decode each texel from sRGB into
    /// linear light on the way in - 0.5 arrives at the shader as 0.214. That is correct in a
    /// pipeline that encodes back on the way out, and the overlay's swap chain is plain
    /// <c>R8G8B8A8_UNorm</c> (ClickableTransparentOverlay's <c>Overlay</c> constructor), so
    /// nothing ever does. The decoded value is written to the screen as if it were already
    /// sRGB, and every midtone lands roughly a stop and a half down: item art that is
    /// visibly duller than the same art in the game, most of it in the mid greys and browns
    /// that PoE's icons are made of. Highlights and pure black are unaffected, which is why
    /// it reads as "wrong somehow" rather than as an obvious blackout.
    ///
    /// It was never about where the picture came from - poe2db's PNGs were darkened by
    /// exactly the same conversion as the install's own textures, which is the clue that
    /// pointed here rather than at the DDS decode.
    ///
    /// The reference agrees: GameHelper2 draws over the same overlay and passes false at all
    /// six of its call sites (<c>Radar</c>, <c>HealthBars</c>, <c>Atlas2</c>,
    /// <c>PlayerBuffBar</c>), and <see cref="TerrainLayer"/> here already did.
    /// </remarks>
    public const bool Srgb = false;

    /// <summary>A loaded picture, and the shape it came in.</summary>
    /// <remarks>
    /// The SIZE is the reason this exists. A banner is drawn to a width and has to keep its
    /// proportions, and a caller that cannot ask how tall the picture is can only guess -
    /// which shows up as every custom banner being subtly squashed.
    /// </remarks>
    public readonly record struct Picture(IntPtr Texture, int Width, int Height)
    {
        /// <summary>Whether there is anything to draw.</summary>
        public bool Ready => Texture != IntPtr.Zero && Width > 0 && Height > 0;

        /// <summary>How tall this is when drawn at a given width.</summary>
        public float HeightAt(float width) => Ready ? width * Height / Width : 0f;
    }

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;
    private readonly Dictionary<string, Picture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingBuiltIn = new(StringComparer.OrdinalIgnoreCase);

    // The sheet, once. See Sheet() for why this is a field rather than a lookup.
    private Picture _sheet;
    private bool _sheetAsked;

    public IconCache(Func<string, Image<Rgba32>, bool, IntPtr> upload, Action<string> release)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(release);
        _upload = upload;
        _release = release;
    }

    /// <summary>Where the files are looked for, and what has already been given up on.</summary>
    public IconFiles Files { get; } = new();

    /// <summary>
    /// The picture for a path at a given size limit, or an empty one when there is none.
    /// </summary>
    /// <remarks>
    /// The limit is part of the KEY, not just of the load. The same file is a marker in one
    /// place and a banner in another, and without it in the key whichever asked first decides
    /// how detailed the other one gets - which is a bug that only appears when somebody
    /// happens to point two settings at one file.
    /// </remarks>
    public Picture PictureFor(string? path, int maxEdge)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        string cached = $"{maxEdge}|{path}";
        if (_textures.TryGetValue(cached, out Picture known))
        {
            return known;
        }

        string file = Files.NextToTry(path);
        if (file.Length == 0)
        {
            return default;   // never chosen, or already given up on
        }

        Picture picture = Load(path, file, maxEdge);
        if (picture.Ready)
        {
            _textures[cached] = picture;
        }

        return picture;
    }

    /// <summary>
    /// A picture that ships INSIDE the assembly, by the tail of its resource name.
    /// </summary>
    /// <remarks>
    /// For the things the tool draws by default and does not want to have to install. A file
    /// beside the executable can be deleted, missed by a copy, or lost when somebody unzips a
    /// new build over an old folder - and the failure looks like the feature not working. A
    /// manifest resource cannot be separated from the code that draws it, survives
    /// single-file publishing and AOT alike, and needs no path for anybody to type.
    ///
    /// It does NOT replace the file path. A shipped plate is the default; the style entry's
    /// icon still wins when it is set, which is what keeps "all of it can be changed" true.
    ///
    /// Matched on the END of the resource name, because the full one is built from the root
    /// namespace and folder and would have to be repeated - and silently rewritten - every
    /// time either moved.
    /// </remarks>
    /// <summary>
    /// The sprite sheet every marker and status icon is cut from, or an empty picture.
    /// </summary>
    /// <remarks>
    /// ONE CALL FOR THE WHOLE TOOL, so nothing can load the sheet at a second size limit and
    /// end up with a grid that disagrees with everybody else's - the limit is part of the
    /// cache key, so two callers asking differently get two textures.
    ///
    /// HELD IN A FIELD rather than looked up, and that is not premature. This is asked once
    /// PER MARKER PER FRAME - every entity dot and every landmark on the map - and the lookup
    /// underneath it builds its cache key by interpolating a string. At a hundred markers and
    /// sixty frames that is six thousand throwaway strings a second to arrive at the same
    /// texture handle every time.
    ///
    /// Empty when the resource did not ship, and every caller falls back to the built-in
    /// shape for that, exactly as they did for a missing file. The empty answer is remembered
    /// too - <see cref="BuiltIn"/> already refuses to go looking again, and this saves even
    /// the call.
    /// </remarks>
    public Picture Sheet()
    {
        if (!_sheetAsked)
        {
            _sheetAsked = true;
            _sheet = BuiltIn(IconSheet.Resource, MaxSheetEdge);
        }

        return _sheet;
    }

    public Picture BuiltIn(string name, int maxEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string cached = $"{maxEdge}|resource:{name}";
        if (_textures.TryGetValue(cached, out Picture known))
        {
            return known;
        }

        if (_missingBuiltIn.Contains(cached))
        {
            return default;   // asked once, not there - do not go looking sixty times a second
        }

        Picture picture = LoadBuiltIn(cached, name, maxEdge);
        if (picture.Ready)
        {
            _textures[cached] = picture;
        }
        else
        {
            // Its own list rather than the file problems: a resource that is not there is a
            // build mistake, and putting it among the paths the USER typed would send them
            // looking for a file they never chose.
            _missingBuiltIn.Add(cached);
        }

        return picture;
    }

    private Picture LoadBuiltIn(string cached, string name, int maxEdge)
    {
        try
        {
            System.Reflection.Assembly assembly = typeof(IconCache).Assembly;
            string? resource = Array.Find(
                assembly.GetManifestResourceNames(),
                candidate => candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase));

            if (resource is null)
            {
                return default;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return default;
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(stream);
            return Upload(cached, image, maxEdge);
        }
        catch (Exception exception) when (
            exception is IOException or UnknownImageFormatException or InvalidImageContentException
                or NotSupportedException or ArgumentException)
        {
            // A picture that shipped broken is a build mistake, not something to end a session
            // over. The caller draws its text form, exactly as it does for a missing file.
            return default;
        }
    }

    private Picture Load(string path, string file, int maxEdge)
    {
        try
        {
            if (!File.Exists(file))
            {
                Files.Failed(path, "not found");
                return default;
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(file);
            Files.Worked(path);
            return Upload($"{maxEdge}|{path}", image, maxEdge);
        }
        catch (IOException busy) when (IconFiles.Momentary(busy))
        {
            // NOT a verdict. Somebody else has the file open, which for the item art is this
            // tool itself finishing the download of the very icon being drawn - and remembered
            // as a failure, that picture is gone until the next run. The next frame asks again.
            Files.Busy(path, busy.Message);
            return default;
        }
        catch (Exception exception) when (
            exception is IOException or UnknownImageFormatException or InvalidImageContentException
                or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            // Every one of these means "that file is not a picture we can draw", and none is
            // worth a frame - let alone the session, which is what an escaping exception on
            // the render thread costs.
            Files.Failed(path, exception.Message);
            return default;
        }
    }

    /// <summary>Shrinks a loaded picture to the limit and hands it to the renderer.</summary>
    private Picture Upload(string cached, Image<Rgba32> image, int maxEdge)
    {
        if (image.Width > maxEdge || image.Height > maxEdge)
        {
            // Fits INSIDE the box rather than filling it, so a wide picture is not squashed
            // into a square - a squashed icon is a different picture.
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(maxEdge, maxEdge),
                Mode = ResizeMode.Max,
            }));
        }

        string key = $"poeformance.icon.{_keys.Count}.{maxEdge}";
        _keys[cached] = key;
        return new Picture(_upload(key, image, Srgb), image.Width, image.Height);
    }

    /// <summary>Drops everything, so changed files are picked up on the next ask.</summary>
    public void Forget()
    {
        Release();
        _textures.Clear();
        _keys.Clear();
        _missingBuiltIn.Clear();

        // The held sheet points at a texture that has just been released, so it has to go with
        // them - kept, it would hand every marker a handle the renderer no longer knows.
        _sheet = default;
        _sheetAsked = false;
        Files.Forget();
    }

    private void Release()
    {
        foreach (string key in _keys.Values)
        {
            try
            {
                _release(key);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // Shutting down, or the renderer already let it go. Nothing to do about it,
                // and nothing worth ending the process over.
            }
        }
    }

    public void Dispose() => Release();
}
