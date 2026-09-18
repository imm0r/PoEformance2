using System.Text.RegularExpressions;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// That the overlay decodes its pictures into the shape the renderer can actually upload.
/// </summary>
/// <remarks>
/// A TEXT CHECK, for the same reason <see cref="IconColourTests"/> is one: the overlay layer is
/// Windows-only and this suite runs on Linux, so nothing here can reference <c>IconCache</c>, let
/// alone a Direct3D device.
///
/// WHAT IT IS PINNING. The renderer uploads a texture by taking the image's SINGLE pixel span,
/// and ImageSharp's default allocator splits anything past a few megabytes across several
/// buffers - measured: 896x1024 arrives whole, 896x2048 does not. For a split image there is no
/// single span, and ClickableTransparentOverlay answers with a bare
/// <c>Exception("Make sure to initialize MemoryAllocator.Default!")</c>, which names neither the
/// cause nor the fix.
///
/// It has now cost this project three times. The terrain mask hit it first and took the whole tool
/// down on the first area large enough to split; the icon sheet hit it second, and because the icon
/// path falls back to the built-in shape, the symptom was every icon silently going back to how it
/// looks when no icon is chosen - a change that had passed a green build, a green test suite and a
/// green AOT publish. The monster portrait hit it third, UNDER THIS VERY TEST: its upload is spelt
/// <c>_upload!(</c>, the file filter below looked for <c>_upload(</c>, and so the one file that
/// later broke was the one file never opened. Half an hour of animation played fine one rung down,
/// and the Pause button - which goes back up to the rung that covers the pane - killed the viewer
/// until the app was restarted.
/// </remarks>
public class IconDecodeTests
{
    /// <summary>The repository root, found by a file only it has.</summary>
    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    private static string OverlaySources => Path.Combine(RepositoryRoot, "src", "PoEformance.Overlay");

    /// <summary>A decode call, and its arguments.</summary>
    /// <remarks>
    /// No nested brackets, for the reason the sRGB test gives: there are none at these call sites,
    /// and a regex that allowed them would match half the file. If that changes this stops finding
    /// the calls and the count check fails, which is the intended failure.
    /// </remarks>
    private static readonly Regex Decodes =
        new(@"Image\.(?:Load|LoadPixelData)<Rgba32>\((?<args>[^()]*)\)", RegexOptions.Compiled);

    /// <summary>A file that hands a picture to the renderer.</summary>
    /// <remarks>
    /// WITH THE BANG OPTIONAL, because that is the hole the portrait fell through: a delegate
    /// held in a nullable field is called as <c>_upload!(</c>, and a filter on <c>_upload(</c> is
    /// a filter that skips every such file. <see cref="IconColourTests"/> matches the same way.
    /// </remarks>
    private static readonly Regex Uploads = new(@"_upload!?\(", RegexOptions.Compiled);

    [Fact]
    public void EVERYPictureIsDecodedIntoOneBufferTheRendererCanUpload()
    {
        var found = new List<(string File, string Args)>();

        foreach (string file in Directory.EnumerateFiles(OverlaySources, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);

            // ONLY THE FILES THAT UPLOAD. Decoding a picture is not what needs a contiguous
            // buffer - handing it to the renderer is. InstalledArt decodes the game's own
            // textures and re-encodes them straight to a PNG on disk, which never touches a
            // Direct3D device and is under no such constraint; requiring it there would be
            // cargo, and cargo is what a check stops being read for.
            if (!Uploads.IsMatch(source))
            {
                continue;
            }

            foreach (Match call in Decodes.Matches(source))
            {
                found.Add((Path.GetFileName(file), call.Groups["args"].Value));
            }
        }

        // The icon cache's two - a resource and a file - the terrain mask's one and the monster
        // portrait's one. A floor rather than an exact number, so a fifth picture does not fail
        // this; a RENAME, which would leave the check matching nothing and passing, does. (The
        // terrain floor is not among them: it builds its image with new Image and checks the
        // single span itself before writing into it.)
        Assert.True(found.Count >= 4, $"found {found.Count} decode calls in {OverlaySources}, expected at least 4");

        foreach ((string file, string args) in found)
        {
            // The bare overload takes the default configuration, which is the one that splits.
            // Every call has to name a configuration instead - by any of the spellings the two
            // call sites use.
            Assert.True(
                args.Contains("Configuration", StringComparison.Ordinal)
                    || args.Contains("configuration", StringComparison.Ordinal)
                    || args.Contains("Contiguous", StringComparison.Ordinal),
                $"{file} decodes a picture without naming a configuration: Image.Load<Rgba32>({args}). "
                + "The default allocator splits anything past a few megabytes, and the renderer "
                + "uploads from a single pixel span - see IconCache.Contiguous.");
        }
    }

    [Fact]
    public void ANDEveryPlaceAsksForAContiguousBuffer()
    {
        // The configurations above are allowed to be named, so what the names MEAN has to be
        // checked too - otherwise a configuration that never sets the flag passes.
        foreach (string file in new[] { "IconCache.cs", "TerrainLayer.cs", "TerrainFloor.cs", "MonsterPortrait.cs" })
        {
            string source = File.ReadAllText(Path.Combine(OverlaySources, file));
            Assert.Contains("PreferContiguousImageBuffers = true", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The portrait rests on rungs past the size where this matters, and plays on one under it.
    /// </summary>
    /// <remarks>
    /// WHICH IS WHY IT SHOWED UP AS A PAUSE BUTTON THAT BROKE THE VIEWER. An animation is drawn
    /// one rung down under the usual cap - 512 px, one megabyte - and half an hour of it uploads
    /// fine. Pause goes back up to the rung that covers the pane, and on a pane wider than 1024 px
    /// that is 1536 or 2048: nine and sixteen megabytes, which ImageSharp 3.1.12 splits into
    /// four-megabyte pool blocks. Measured on every rung rather than read off a diagram: with the
    /// default configuration each rung up to 1024 arrives whole and both above it arrive split,
    /// and with the flag all seven arrive whole. So the flag is load-bearing on exactly the two
    /// rungs a paused portrait can rest on, and on none a playing one is drawn at.
    /// </remarks>
    [Fact]
    public void ThePortraitRestsPastTheSizeWhereItMattersAndPlaysUnderIt()
    {
        const long PoolBlock = 4L * 1024 * 1024;
        var ladder = new PictureLadder();

        // Playing under the usual cap never reaches the threshold, however wide the pane is.
        long playing = Bytes(ladder.Dragging(MeshPicture.Widest));
        Assert.True(playing <= PoolBlock, $"playing draws {playing} bytes, which is past the pool block");

        // Paused on a pane wider than 1024 px it is over it - which is the report exactly.
        Assert.True(Bytes(ladder.For(1025f)) > PoolBlock);
        Assert.True(Bytes(ladder.For(MeshPicture.Widest)) > PoolBlock);

        static long Bytes(int rung) => (long)rung * rung * 4;
    }

    /// <summary>
    /// The sheet really is past the size where this matters, so the flag is not cargo.
    /// </summary>
    /// <remarks>
    /// Read out of the PNG header rather than by decoding it, so this needs no image library:
    /// the IHDR chunk puts width and height as big-endian ints at bytes 16 and 20.
    ///
    /// The threshold is the MEASURED one. 896x1024 (3.5 MB) came back as one buffer and
    /// 896x2048 (7 MB) did not, so four megabytes is inside the band where the answer changes.
    /// A sheet under it would not need the flag; this one is four times over.
    /// </remarks>
    [Fact]
    public void TheSheetIsLargeEnoughToNeedIt()
    {
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(Path.Combine(RepositoryRoot, "assets", "icons.png")))
        {
            Assert.Equal(header.Length, file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false));
        }

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

        // The grid the cells are cut on has to divide the sheet, or every icon lands part way
        // between two of them.
        Assert.Equal(0, width % PoEformance.Features.IconSheet.Tile);
        Assert.Equal(0, height % PoEformance.Features.IconSheet.Tile);

        long bytes = (long)width * height * 4;
        Assert.True(
            bytes > 4L * 1024 * 1024,
            $"the sheet decodes to {bytes / 1024 / 1024} MB, which is small enough to arrive in one "
            + "buffer on its own - so the contiguous flag the tests above pin is no longer load-bearing.");
    }
}
