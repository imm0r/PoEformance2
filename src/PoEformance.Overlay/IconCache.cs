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

    private readonly Func<string, Image<Rgba32>, bool, IntPtr> _upload;
    private readonly Action<string> _release;
    private readonly Dictionary<string, IntPtr> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

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
    /// The texture for a path, or <see cref="IntPtr.Zero"/> when there is none to draw.
    /// </summary>
    public IntPtr TextureFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IntPtr.Zero;
        }

        if (_textures.TryGetValue(path, out IntPtr known))
        {
            return known;
        }

        string file = Files.NextToTry(path);
        if (file.Length == 0)
        {
            return IntPtr.Zero;   // never chosen, or already given up on
        }

        IntPtr texture = Load(path, file);
        if (texture != IntPtr.Zero)
        {
            _textures[path] = texture;
        }

        return texture;
    }

    private IntPtr Load(string path, string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                Files.Failed(path, "not found");
                return IntPtr.Zero;
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(file);

            if (image.Width > MaxEdge || image.Height > MaxEdge)
            {
                // Fits INSIDE the box rather than filling it, so a wide picture is not
                // squashed into a square - a squashed icon is a different picture.
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(MaxEdge, MaxEdge),
                    Mode = ResizeMode.Max,
                }));
            }

            string key = $"poeformance.icon.{_keys.Count}";
            _keys[path] = key;
            return _upload(key, image, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnknownImageFormatException or InvalidImageContentException
                or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            // Every one of these means "that file is not a picture we can draw", and none is
            // worth a frame - let alone the session, which is what an escaping exception on
            // the render thread costs.
            Files.Failed(path, exception.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>Drops everything, so changed files are picked up on the next ask.</summary>
    public void Forget()
    {
        Release();
        _textures.Clear();
        _keys.Clear();
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
