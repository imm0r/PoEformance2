using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading a file out of an installed copy of the game, end to end.
/// </summary>
/// <remarks>
/// Four formats stacked on each other - an archive holding bundles, an index saying what is in
/// which, and a hash to find it by - so this builds a small install and asks it for a path, the
/// way drawing an item's icon does.
///
/// What is checked past "it works" is the COST. This runs while a stash window is open, so a
/// read that quietly pulls a couple of hundred megabytes through, or re-opens the same bundle
/// for every icon, is the difference between a stash appearing and the overlay stopping.
/// </remarks>
public class GameFilesTests
{
    /// <summary>An install in memory, which also counts what was read out of it.</summary>
    private sealed class Fake : IGameArchive
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool Ready => true;

        public string Describe => "a made-up install";

        /// <summary>Every range that was asked for, so a test can say how much was moved.</summary>
        public List<(string Path, int At, int Length)> Reads { get; } = [];

        public void Add(string path, byte[] bytes) => _files[path] = bytes;

        public byte[]? Read(string path)
        {
            if (!_files.TryGetValue(path, out byte[]? bytes))
            {
                return null;
            }

            Reads.Add((path, 0, bytes.Length));
            return bytes;
        }

        public byte[]? Read(string path, int at, int length)
        {
            if (!_files.TryGetValue(path, out byte[]? bytes) || at < 0 || length < 0 || at + (long)length > bytes.Length)
            {
                return null;
            }

            Reads.Add((path, at, length));
            return bytes[at..(at + length)];
        }

        /// <summary>How many bytes were moved out of the archive in total.</summary>
        public long Moved => Reads.Sum(read => (long)read.Length);
    }

    private static byte[] Counting(int length, byte from = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i + from) % 251);
        }

        return bytes;
    }

    /// <summary>An install holding one bundle with two files in it.</summary>
    private static (Fake Archive, byte[] Bow, byte[] Ring) Install(int chunkSize = 64)
    {
        byte[] bow = Counting(300, 1);
        byte[] ring = Counting(150, 200);

        // Laid out in the bundle the way the index will say: the bow at 100, the ring at 500,
        // neither on a chunk boundary, which is what a real one looks like.
        var content = new byte[4096];
        bow.CopyTo(content, 100);
        ring.CopyTo(content, 500);

        var archive = new Fake();
        archive.Add("data.bundle.bin", Packed.Bundle(content, chunkSize));
        archive.Add("_.index.bin", Packed.Bundle(Packed.Index(
            ["data"],
            [
                new("Art/2DItems/Weapons/Bow.dds", 0, 100, bow.Length),
                new("Art/2DItems/Rings/Ring1.dds", 0, 500, ring.Length),
            ]), chunkSize));

        return (archive, bow, ring);
    }

    [Fact]
    public void ANICONComesOutOfAnInstall()
    {
        (Fake archive, byte[] bow, byte[] ring) = Install();

        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);

        Assert.NotNull(files);
        Assert.Equal(2, files!.Index.Count);
        Assert.Equal(bow, files.Read("Art/2DItems/Weapons/Bow.dds"));
        Assert.Equal(ring, files.Read(@"Art\2DItems\Rings\Ring1.dds"));
    }

    [Fact]
    public void ANDOneThatIsNotInTheInstallIsNotThere()
    {
        (Fake archive, _, _) = Install();
        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);

        Assert.Null(files!.Read("Art/2DItems/NewThisLeague.dds"));
        Assert.Null(files.Read(null));
        Assert.False(files.Has("Art/2DItems/NewThisLeague.dds"));
        Assert.True(files.Has("art/2ditems/weapons/bow.dds"));
    }

    [Fact]
    public void ANDReadingOneIconDoesNotPullAWholeBundleThrough()
    {
        // The reason the archive has a ranged read at all. A bundle is a couple of hundred
        // megabytes; an icon is a few kilobytes of one chunk. Reading the bundle to slice it
        // works, and reading a stash that way moves gigabytes.
        (Fake archive, byte[] bow, _) = Install();
        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);

        archive.Reads.Clear();
        Assert.Equal(bow, files!.Read("Art/2DItems/Weapons/Bow.dds"));

        long bundle = archive.Reads.Where(read => read.Path == "data.bundle.bin").Sum(read => (long)read.Length);
        Assert.True(bundle < 1200, $"pulled {bundle} bytes through for a 300-byte icon");
        Assert.DoesNotContain(archive.Reads, read => read.At == 0 && read.Length > 4096);
    }

    [Fact]
    public void ANDABundleIsOpenedOnceRatherThanPerIcon()
    {
        // Its header and chunk table are the same every time. A stash draws thousands of items
        // out of a handful of bundles, so re-reading the table per item is thousands of reads
        // for something that has not changed.
        (Fake archive, _, _) = Install();
        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);

        archive.Reads.Clear();
        for (int i = 0; i < 50; i++)
        {
            Assert.NotNull(files!.Read("Art/2DItems/Weapons/Bow.dds"));
            Assert.NotNull(files.Read("Art/2DItems/Rings/Ring1.dds"));
        }

        // Two reads to open the bundle - its header, then its chunk table - and never again.
        Assert.Equal(2, archive.Reads.Count(read => read.At < BundleFile.HeaderBytes + 4));
    }

    [Fact]
    public void ANINSTALLThatIsNotOneIsRefusedRatherThanHalfOpened()
    {
        Assert.Null(GameFiles.Open((string?)null));
        Assert.Null(GameFiles.Open(string.Empty));
        Assert.Null(GameFiles.Open("   "));
        Assert.Null(GameFiles.Open(Path.Combine(Path.GetTempPath(), $"no-game-{Guid.NewGuid():N}")));
        Assert.Null(GameFiles.Open((IGameArchive?)null));

        // One with an index that will not unpack, which is what a version this does not
        // understand looks like.
        var broken = new Fake();
        broken.Add("_.index.bin", [1, 2, 3, 4]);
        Assert.Null(GameFiles.Open(broken, Packed.AsIs));

        // And one whose index unpacks into something that is not an index.
        var wrong = new Fake();
        wrong.Add("_.index.bin", Packed.Bundle([.. Enumerable.Repeat((byte)0xAB, 200)]));
        Assert.Null(GameFiles.Open(wrong, Packed.AsIs));
    }

    [Fact]
    public void ALOOSEInstallOnDiskIsReadTheSameWay()
    {
        // The other shape, all the way through - a folder of files rather than an archive.
        (Fake source, byte[] bow, _) = Install();
        string folder = Path.Combine(Path.GetTempPath(), $"game-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
            foreach (string name in new[] { "_.index.bin", "data.bundle.bin" })
            {
                File.WriteAllBytes(Path.Combine(folder, "Bundles2", name), source.Read(name)!);
            }

            GameFiles? files = GameFiles.Open(folder, Packed.AsIs);

            Assert.NotNull(files);
            Assert.Equal(bow, files!.Read("Art/2DItems/Weapons/Bow.dds"));
            Assert.Contains("loose files", files.Describe);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
