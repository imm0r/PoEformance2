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
/// in the game's own bundles. Getting from the name to pixels is three steps, and only the last
/// one is what anybody would guess.
///
/// FIRST, THE FILE MAY BE A HEADER. Textures are stored split: <c>x.dds</c> may not be there at
/// all, and <c>x.dds.header</c> is, with a short prefix before the picture. The prefix is 28
/// bytes when the file starts with a 3 and 16 otherwise.
///
/// SECOND, IT MAY BE A SIGNPOST. A file whose content starts with <c>*</c> is not a picture: the
/// rest of it is the path of the file that actually holds one, and that one may point somewhere
/// else again. Following it is how variants share one texture without storing it twice.
///
/// THIRD, IT IS A DDS, in whichever block format that texture was compressed with - BC1 through
/// BC7 all turn up. Pfim decodes all of them, and is what the reference tool uses on exactly
/// this data.
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
            // The header first, because for a split texture the plain name is often not in the
            // index at all - and when both are there the header is the one with the prefix this
            // knows how to strip.
            bool header = true;
            byte[]? found = files.Read(path + ".header");

            if (found is null)
            {
                header = path.EndsWith(".header", StringComparison.OrdinalIgnoreCase);
                found = files.Read(path);
            }

            if (found is null)
            {
                return null;
            }

            ReadOnlySpan<byte> content = found;
            if (header)
            {
                int prefix = content.Length > 0 && content[0] == 3 ? 28 : 16;
                if (content.Length <= prefix)
                {
                    return null;
                }

                content = content[prefix..];
            }

            if (content.Length == 0 || content[0] != (byte)'*')
            {
                return content.ToArray();
            }

            // A signpost. What follows the star is where the picture really is.
            ReadOnlySpan<byte> pointsAt = content[1..];
            if (pointsAt.Length is 0 or > LongestPath)
            {
                return null;
            }

            path = Encoding.UTF8.GetString(pointsAt).TrimEnd('\0').Trim();
            if (path.Length == 0)
            {
                return null;
            }
        }

        return null;
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
