using System.Text;

namespace PoEformance.Game.Files;

/// <summary>A decoded picture: how big it is, and its pixels as red, green, blue, alpha.</summary>
public readonly record struct GamePicture(int Width, int Height, byte[] Rgba)
{
    /// <summary>Whether there is anything to draw.</summary>
    public bool Ready => Width > 0 && Height > 0 && Rgba.Length >= Width * Height * 4;
}

/// <summary>
/// An item's own picture, out of the install.
/// </summary>
/// <remarks>
/// An item in memory names its art - <c>Art/2DItems/Weapons/Bows/Bow1.dds</c> - and that file is
/// in the game's own bundles. What comes out of the bundle is one of THREE things, and only the
/// last is what anybody would guess (owner, who reverse-engineered a PoE2 install; neither
/// LibGGPK3 nor GameHelper2 covers this - the first is a PoE1-era tool and the second does not
/// decode textures at all).
///
/// A SIGNPOST. A file whose content starts with <c>*</c> is not a picture: the rest of it is the
/// path of the file that actually holds one, and THAT one may be compressed or not, or point
/// somewhere else again. It is how variants share one texture without storing it twice.
///
/// BROTLI, behind four bytes that are not part of it - the decompressed size, which is both the
/// buffer to allocate and the check that this really was one.
///
/// OR JUST A DDS, openable as it is.
///
/// Which of the three is worked out from the CONTENT rather than the name, because the name is
/// <c>.dds</c> in all three cases. Then it is a DDS in whichever block format that texture used
/// - BC1 through BC7 all turn up, and Pfim decodes all of them.
/// </remarks>
public static class GameArt
{
    /// <summary>How many signposts will be followed before giving up.</summary>
    /// <remarks>A file that points at itself is a loop, and this runs while a window is open.</remarks>
    public const int MostHops = 8;

    /// <summary>The longest a signpost's path may be, past which it is not one.</summary>
    public const int LongestPath = 4096;

    /// <summary>How big a picture may be, so a wrong read cannot ask for a gigabyte.</summary>
    public const int WidestPicture = 8192;

    /// <summary>The largest a texture may say it decompresses to.</summary>
    /// <remarks>
    /// The size is four bytes off the front of the file, so a file that is not one at all is a
    /// buffer of whatever number happened to be there.
    /// </remarks>
    public const int BiggestTexture = 64 * 1024 * 1024;

    /// <summary>What a DDS starts with.</summary>
    private static ReadOnlySpan<byte> DdsMagic => "DDS "u8;

    /// <summary>
    /// Reads an art path out of an install and decodes it, or returns null when it will not.
    /// </summary>
    /// <param name="files">The install.</param>
    /// <param name="artPath">As the item spells it - the case and slashes do not matter.</param>
    public static GamePicture? Read(GameFiles? files, string? artPath)
    {
        byte[]? dds = ReadRaw(files, artPath);
        return dds is null ? null : Decode(dds);
    }

    /// <summary>
    /// Finds the bytes of the picture a path names, following headers and signposts.
    /// </summary>
    /// <remarks>Separate from decoding so that what the install holds can be checked on its own.</remarks>
    public static byte[]? ReadRaw(GameFiles? files, string? artPath)
    {
        if (files is null || string.IsNullOrWhiteSpace(artPath))
        {
            return null;
        }

        string path = artPath.Replace('\\', '/').Trim();

        for (int hop = 0; hop < MostHops; hop++)
        {
            byte[]? found = files.Read(path);
            if (found is null)
            {
                return null;
            }

            if (Signpost(found) is not { } pointsAt)
            {
                // Not a signpost, so this is the picture - compressed or not.
                return Unpack(found);
            }

            path = pointsAt;
        }

        return null;
    }

    /// <summary>
    /// Where a file points, or null when it is a picture rather than a signpost.
    /// </summary>
    /// <remarks>
    /// A star and then a path. Checked for being a PLAUSIBLE path rather than just for the star,
    /// because a compressed texture begins with its size and one size in every 256 begins with
    /// the same byte - and reading a picture as a path finds nothing at all.
    /// </remarks>
    public static string? Signpost(byte[]? content)
    {
        if (content is not { Length: > 1 } || content[0] != (byte)'*')
        {
            return null;
        }

        ReadOnlySpan<byte> rest = content.AsSpan(1);
        if (rest.Length > LongestPath)
        {
            return null;
        }

        foreach (byte one in rest)
        {
            // Printable, and nothing a path does not contain. A size read as text is almost
            // always out of this range at the first or second byte.
            if (one is (< 0x20 or > 0x7E) and not 0)
            {
                return null;
            }
        }

        string where = Encoding.UTF8.GetString(rest).TrimEnd('\0').Trim();
        return where.Length > 0 && where.Contains('.') ? where : null;
    }

    /// <summary>
    /// Gets the DDS out of a file, decompressing it when it is compressed.
    /// </summary>
    /// <remarks>
    /// The compressed ones are Brotli behind four bytes that are not part of it: the size it
    /// comes out at. Taken as the buffer to allocate AND as the check - a decompression that
    /// does not fill it exactly was not this.
    /// </remarks>
    public static byte[]? Unpack(byte[]? content)
    {
        if (content is not { Length: > 4 })
        {
            return null;
        }

        if (content.AsSpan().StartsWith(DdsMagic))
        {
            return content;
        }

        uint size = BitConverter.ToUInt32(content, 0);
        if (size is 0 or > BiggestTexture)
        {
            // Not a size, so not the compressed shape. Handed on as it is rather than refused -
            // Pfim may still know what it is, and refusing here would be this deciding that a
            // texture it does not recognise cannot be one.
            return content;
        }

        var plain = new byte[size];
        return System.IO.Compression.BrotliDecoder.TryDecompress(content.AsSpan(4), plain, out int written)
               && written == plain.Length
            ? plain
            : content;
    }

    /// <summary>
    /// Turns a DDS into plain pixels, or returns null when it is not one this can read.
    /// </summary>
    /// <remarks>
    /// Pfim answers in DIRECTX byte order, which is blue first - so handing its bytes straight
    /// to anything expecting red first gives a picture whose colours are swapped, and a swapped
    /// icon looks like a real icon. Only the top mipmap is taken; the smaller copies after it
    /// are the same picture again.
    /// </remarks>
    public static GamePicture? Decode(byte[]? dds)
    {
        if (dds is not { Length: > 4 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(dds, writable: false);
            using Pfim.IImage image = Pfim.Pfimage.FromStream(stream);

            if (image.Width is <= 0 or > WidestPicture || image.Height is <= 0 or > WidestPicture)
            {
                return null;
            }

            var rgba = new byte[image.Width * image.Height * 4];
            return Lay(image, rgba)
                ? new GamePicture(image.Width, image.Height, rgba)
                : null;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
                                              or ArgumentException or IndexOutOfRangeException
                                              or InvalidOperationException or OverflowException)
        {
            // Not a texture this can read, which is an item drawn as its name rather than a
            // stash that fails to open.
            return null;
        }
    }

    /// <summary>Copies Pfim's pixels out as red, green, blue, alpha.</summary>
    private static bool Lay(Pfim.IImage image, byte[] rgba)
    {
        int wide = image.Format switch
        {
            Pfim.ImageFormat.Rgba32 => 4,
            Pfim.ImageFormat.Rgb24 => 3,
            _ => 0,
        };

        if (wide == 0 || image.Stride < image.Width * wide)
        {
            return false;
        }

        byte[] from = image.Data;
        for (int y = 0; y < image.Height; y++)
        {
            int reading = y * image.Stride;
            int writing = y * image.Width * 4;

            if (reading + (image.Width * wide) > image.DataLen)
            {
                return false;
            }

            for (int x = 0; x < image.Width; x++)
            {
                int one = reading + (x * wide);
                int other = writing + (x * 4);

                rgba[other] = from[one + 2];       // blue first out of Pfim, red first into this
                rgba[other + 1] = from[one + 1];
                rgba[other + 2] = from[one];
                rgba[other + 3] = wide == 4 ? from[one + 3] : (byte)255;
            }
        }

        return true;
    }
}
