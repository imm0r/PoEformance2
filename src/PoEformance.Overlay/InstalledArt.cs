using PoEformance.Game.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// Item pictures out of the installed game, ready for the art store to keep.
/// </summary>
/// <remarks>
/// The last joint of the chain: <see cref="GameFiles"/> gets the bytes out of the install and
/// <see cref="GameArt"/> turns them into pixels, and what the art store wants is an ENCODED
/// picture it can write next to the settings and read back with everything else. That is the
/// only reason this lives here rather than in the layer below - the encoder is here, along with
/// the rest of the image handling, and pushing it down would drag ImageSharp with it.
///
/// The pictures the game ships are block-compressed textures that nothing else reads, so they
/// are re-encoded once each, on the way into the cache. After that a picture is a file like any
/// other and the install is not touched again.
/// </remarks>
public static class InstalledArt
{
    /// <summary>
    /// Opens an install and gives back how to get one picture out of it, or null when there is
    /// no install to read.
    /// </summary>
    /// <param name="gameFolder">Where the game is, or null to go looking for it.</param>
    /// <param name="describe">Told what was opened, or why nothing was.</param>
    public static Func<string, byte[]?>? Source(string? gameFolder = null, Action<string>? describe = null)
    {
        string? folder = GameInstall.Find(gameFolder);
        if (folder is null)
        {
            describe?.Invoke("item art: no Path of Exile 2 install found, so pictures can only come from poe2db");
            return null;
        }

        // The reason comes back with the answer, because "could not read its packed files" is
        // four different failures wearing one sentence and none of them can be acted on.
        GameFiles.OpenedFiles opened = GameFiles.OpenOrSay(folder);
        describe?.Invoke($"item art: {opened.Why}");

        return opened.Files is { } files ? path => Encode(GameArt.Read(files, path)) : null;
    }

    /// <summary>Turns decoded pixels into a picture file, or null when there were none.</summary>
    private static byte[]? Encode(GamePicture? picture)
    {
        if (picture is not { Ready: true } ready)
        {
            return null;
        }

        try
        {
            using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(ready.Rgba, ready.Width, ready.Height);
            using var stream = new MemoryStream();

            // Lossless, and with an alpha channel: an item's picture is cut out against nothing,
            // and a format that drops that draws every icon on a black tile.
            image.Save(stream, new PngEncoder());
            return stream.ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or ImageFormatException or InvalidOperationException)
        {
            // One unreadable texture is one item drawn as its name. The art store remembers it
            // as missing, and poe2db picks it up when that is switched on.
            return null;
        }
    }
}
