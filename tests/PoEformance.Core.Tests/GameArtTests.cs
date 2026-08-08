using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Getting an item's own picture out of an installed copy of the game.
/// </summary>
/// <remarks>
/// Three things stand between an art path and pixels, and two of them are not guessable: a
/// texture may be stored as a <c>.header</c> file with a short prefix, and a file may be a
/// SIGNPOST whose content is the path of the file that really holds the picture. Then it is a
/// DDS.
///
/// The colour order is checked on purpose. Pfim answers blue-first and everything downstream
/// wants red-first, and getting that backwards produces a perfectly good-looking icon in the
/// wrong colours - which is the sort of thing that ships.
/// </remarks>
public class GameArtTests
{
    /// <summary>An install holding exactly the files a test puts in it.</summary>
    private sealed class Held(Dictionary<string, byte[]> files) : IGameArchive
    {
        public bool Ready => true;

        public string Describe => "a made-up install";

        public byte[]? Read(string path) => files.GetValueOrDefault(path);

        public byte[]? Read(string path, int at, int length)
            => Read(path) is { } bytes && at >= 0 && length >= 0 && at + (long)length <= bytes.Length
                ? bytes[at..(at + length)]
                : null;
    }

    /// <summary>Builds an install whose index holds these paths.</summary>
    private static GameFiles Install(params (string Path, byte[] Content)[] contents)
    {
        // Everything goes in one bundle, back to back, and the index says where each one starts.
        var content = new List<byte>();
        var entries = new List<Packed.Entry>();

        foreach ((string path, byte[] bytes) in contents)
        {
            entries.Add(new Packed.Entry(path, 0, content.Count, bytes.Length));
            content.AddRange(bytes);
        }

        var archive = new Held(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["data.bundle.bin"] = Packed.Bundle([.. content], chunkSize: 64),
            ["_.index.bin"] = Packed.Bundle(Packed.Index(["data"], [.. entries]), chunkSize: 64),
        });

        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);
        Assert.NotNull(files);
        return files!;
    }

    /// <summary>
    /// Writes an uncompressed DDS, so the decoding can be checked without a compressor.
    /// </summary>
    /// <remarks>
    /// The block-compressed forms the game really uses cannot be built here - there is no
    /// encoder - so what these check is everything around the decode: the layout, the stride,
    /// and which byte is which colour. Pfim decodes the compressed ones into the same shape.
    /// </remarks>
    private static byte[] Dds(int width, int height, byte[] bgra)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(Encoding.ASCII.GetBytes("DDS "));
        write.Write(124);                       // the header's own size
        write.Write(0x1007);                    // caps, height, width, pixel format
        write.Write(height);
        write.Write(width);
        write.Write(width * 4);                 // pitch
        write.Write(0);                         // depth
        write.Write(0);                         // mip maps
        write.Write(new byte[44]);              // reserved

        write.Write(32);                        // the pixel format's size
        write.Write(0x41);                      // has colour, has alpha
        write.Write(0);                         // no four-character code, so it is uncompressed
        write.Write(32);                        // bits a pixel
        write.Write(0x00FF0000);                // red
        write.Write(0x0000FF00);                // green
        write.Write(0x000000FF);                // blue
        write.Write(unchecked((int)0xFF000000));// alpha

        write.Write(0x1000);                    // a texture
        write.Write(new byte[16]);              // the rest of the caps, and reserved

        write.Write(bgra);
        return stream.ToArray();
    }

    /// <summary>One pixel, in the blue-first order a DDS stores.</summary>
    private static byte[] Pixel(byte r, byte g, byte b, byte a) => [b, g, r, a];

    [Fact]
    public void APICTUREComesOutOfTheInstall()
    {
        byte[] pixels = [.. Pixel(10, 20, 30, 255), .. Pixel(40, 50, 60, 128)];
        GameFiles files = Install(("Art/2DItems/Weapons/Bow.dds", Dds(2, 1, pixels)));

        GamePicture? picture = GameArt.Read(files, "Art/2DItems/Weapons/Bow.dds");

        Assert.NotNull(picture);
        Assert.True(picture!.Value.Ready);
        Assert.Equal(2, picture.Value.Width);
        Assert.Equal(1, picture.Value.Height);

        // Red first, which is the whole point of this one - the same bytes in the other order
        // is a picture that looks right and is the wrong colour.
        Assert.Equal<byte[]>([10, 20, 30, 255, 40, 50, 60, 128], picture.Value.Rgba);
    }

    [Fact]
    public void ANDTheCaseAndSlashesOfThePathDoNotMatter()
    {
        GameFiles files = Install(("Art/2DItems/Weapons/Bow.dds", Dds(1, 1, Pixel(1, 2, 3, 4))));

        Assert.NotNull(GameArt.Read(files, "art/2ditems/weapons/bow.dds"));
        Assert.NotNull(GameArt.Read(files, @"Art\2DItems\Weapons\Bow.dds"));
    }

    [Fact]
    public void ASPLITTextureIsFoundUnderItsHeaderName()
    {
        // The plain name is often not in the index at all - the picture is under the header one,
        // behind a prefix. Looking only for what the item named finds nothing.
        byte[] picture = Dds(1, 1, Pixel(200, 100, 50, 255));
        GameFiles files = Install(("Art/2DItems/Weapons/Bow.dds.header", [.. new byte[16], .. picture]));

        GamePicture? read = GameArt.Read(files, "Art/2DItems/Weapons/Bow.dds");

        Assert.NotNull(read);
        Assert.Equal<byte[]>([200, 100, 50, 255], read!.Value.Rgba);
    }

    [Fact]
    public void ANDThePrefixIsLongerWhenTheFileStartsWithAThree()
    {
        byte[] picture = Dds(1, 1, Pixel(9, 8, 7, 255));
        byte[] prefix = new byte[28];
        prefix[0] = 3;

        GameFiles files = Install(("Art/Thing.dds.header", [.. prefix, .. picture]));

        Assert.Equal<byte[]>([9, 8, 7, 255], GameArt.Read(files, "Art/Thing.dds")!.Value.Rgba);
    }

    [Fact]
    public void ASIGNPOSTIsFollowedToWhereThePictureReallyIs()
    {
        // A file whose content starts with a star is not a picture: the rest of it says where
        // the picture is. It is how variants share one texture instead of storing it twice.
        byte[] picture = Dds(1, 1, Pixel(77, 88, 99, 255));

        GameFiles files = Install(
            ("Art/2DItems/Variant.dds", [.. "*Art/2DItems/Real.dds"u8]),
            ("Art/2DItems/Real.dds", picture));

        Assert.Equal<byte[]>([77, 88, 99, 255], GameArt.Read(files, "Art/2DItems/Variant.dds")!.Value.Rgba);
    }

    [Fact]
    public void ANDOneSignpostMayPointAtAnother()
    {
        byte[] picture = Dds(1, 1, Pixel(1, 1, 1, 255));

        GameFiles files = Install(
            ("Art/A.dds", [.. "*Art/B.dds"u8]),
            ("Art/B.dds", [.. "*Art/C.dds"u8]),
            ("Art/C.dds", picture));

        Assert.NotNull(GameArt.Read(files, "Art/A.dds"));
    }

    [Fact]
    public void ANDOneThatPointsInACircleGivesUpRatherThanSpinning()
    {
        // This runs while a window is open, so a file that points at itself has to end.
        GameFiles files = Install(
            ("Art/A.dds", [.. "*Art/B.dds"u8]),
            ("Art/B.dds", [.. "*Art/A.dds"u8]),
            ("Art/Self.dds", [.. "*Art/Self.dds"u8]));

        Assert.Null(GameArt.Read(files, "Art/A.dds"));
        Assert.Null(GameArt.Read(files, "Art/Self.dds"));
    }

    [Fact]
    public void ANDASignpostToNowhereIsNothingRatherThanAnError()
    {
        GameFiles files = Install(
            ("Art/A.dds", [.. "*Art/Gone.dds"u8]),
            ("Art/Empty.dds", [.. "*"u8]));

        Assert.Null(GameArt.Read(files, "Art/A.dds"));
        Assert.Null(GameArt.Read(files, "Art/Empty.dds"));
    }

    [Fact]
    public void SOMETHINGThatIsNotAPictureIsNotOne()
    {
        GameFiles files = Install(
            ("Art/Junk.dds", [.. Enumerable.Repeat((byte)0xAB, 500)]),
            ("Art/Short.dds", [1, 2]),
            ("Art/Empty.dds", []));

        Assert.Null(GameArt.Read(files, "Art/Junk.dds"));
        Assert.Null(GameArt.Read(files, "Art/Short.dds"));
        Assert.Null(GameArt.Read(files, "Art/Empty.dds"));
        Assert.Null(GameArt.Read(files, "Art/NotThere.dds"));
        Assert.Null(GameArt.Read(files, null));
        Assert.Null(GameArt.Read(null, "Art/Junk.dds"));
        Assert.Null(GameArt.Decode(null));
        Assert.Null(GameArt.Decode([]));
    }

    [Fact]
    public void ANDAPictureBiggerThanAnyRealOneIsRefused()
    {
        // The size comes out of the file, so a wrong read is an allocation of whatever number
        // happened to be there.
        byte[] absurd = Dds(1, 1, Pixel(1, 2, 3, 4));
        BitConverter.GetBytes(100_000).CopyTo(absurd, 16);   // its width

        Assert.Null(GameArt.Decode(absurd));
    }
}
