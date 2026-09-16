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
    /// <summary>
    /// How to get one picture out of an install that is already open.
    /// </summary>
    /// <remarks>
    /// TAKES THE OPEN INSTALL RATHER THAN OPENING ONE, which it used to do. Opening decompresses
    /// the bundle index - tens of megabytes and a moment of work - and by the time this is wanted
    /// the app has an install open anyway, for the quest tables, the ground-effect names and
    /// (now) for asking what the game calls its own art files. Four features, one open.
    /// </remarks>
    /// <summary>
    /// A PICTURE out of the install, encoded as a PNG - NOT the file's own bytes.
    /// </summary>
    /// <remarks>
    /// NAMED FOR WHAT IT RETURNS, because the name it had - From - said nothing, and the signature
    /// says less: <c>Func&lt;string, byte[]?&gt;</c> is also the shape of "read me that file", and
    /// this is not that. It decodes a .dds and re-encodes it, so an .ao, a .sm or a .mat all come
    /// back null.
    ///
    /// THAT COST A ROUND. The monster portrait was wired to this because the signature matched,
    /// and every monster in the book reported having no model - a true message about an empty read,
    /// for a reason nothing in the type system could show. Anything wanting the file as the bundle
    /// holds it wants <see cref="GameFiles.Read"/>.
    /// </remarks>
    public static Func<string, byte[]?> Pictures(GameFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return path => Encode(GameArt.Read(files, path));
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
