using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading the game's packed files, whichever way an install keeps them.
/// </summary>
/// <remarks>
/// These BUILD a GGPK and read it back, which is the only honest way to test a container format
/// without the game: a fixture that writes what the reader expects proves nothing, so the writer
/// here follows LibGGPK3's description of the format rather than my reader's behaviour. Where the
/// two disagree the test fails, which is the point.
///
/// The two details a from-memory version gets wrong are pinned on purpose: the name length counts
/// its null terminator, and a directory entry is twelve bytes rather than sixteen.
/// </remarks>
public class GgpkArchiveTests
{
    private const uint TagGgpk = 0x4B504747;
    private const uint TagPdir = 0x52494450;
    private const uint TagFile = 0x454C4946;

    /// <summary>Writes a GGPK holding one file at Bundles2/&lt;name&gt;.</summary>
    private static string Build(string name, byte[] contents, uint version = 3)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ggpk-{Guid.NewGuid():N}.ggpk");

        using var stream = new FileStream(path, FileMode.Create);
        using var write = new BinaryWriter(stream);

        // The root record, whose offsets are filled in once the rest is placed.
        write.Write(28u);
        write.Write(TagGgpk);
        write.Write(version);
        long rootOffsetAt = stream.Position;
        write.Write(0L);   // where the root folder is
        write.Write(0L);   // where the free list is

        long fileAt = stream.Position;
        WriteFile(write, name, contents, version);

        long bundlesAt = stream.Position;
        WriteFolder(write, "Bundles2", [(name, fileAt)], version);

        long rootAt = stream.Position;
        WriteFolder(write, string.Empty, [("Bundles2", bundlesAt)], version);

        stream.Position = rootOffsetAt;
        write.Write(rootAt);

        return path;
    }

    private static void WriteFolder(BinaryWriter write, string name, (string Name, long At)[] children, uint version)
    {
        byte[] named = Encode(name, version);
        int wide = version == 4 ? 4 : 2;

        // length, tag, name length INCLUDING the terminator, child count, hash, name, entries
        var length = (uint)(4 + 4 + 4 + 4 + 32 + named.Length + wide + (children.Length * 12));
        write.Write(length);
        write.Write(TagPdir);
        write.Write(name.Length + 1);
        write.Write(children.Length);
        write.Write(new byte[32]);
        write.Write(named);
        write.Write(new byte[wide]);

        foreach ((string _, long at) in children)
        {
            write.Write(0u);    // the name hash, which the reader does not use
            write.Write(at);    // TWELVE bytes an entry, not sixteen
        }
    }

    private static void WriteFile(BinaryWriter write, string name, byte[] contents, uint version)
    {
        byte[] named = Encode(name, version);
        int wide = version == 4 ? 4 : 2;

        var length = (uint)(4 + 4 + 4 + 32 + named.Length + wide + contents.Length);
        write.Write(length);
        write.Write(TagFile);
        write.Write(name.Length + 1);
        write.Write(new byte[32]);
        write.Write(named);
        write.Write(new byte[wide]);
        write.Write(contents);
    }

    private static byte[] Encode(string name, uint version)
        => version == 4 ? Encoding.UTF32.GetBytes(name) : Encoding.Unicode.GetBytes(name);

    [Fact]
    public void AFILEComesBackOutOfAGgpk()
    {
        byte[] wanted = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02];
        string path = Build("_.index.bin", wanted);

        try
        {
            GgpkArchive? archive = GgpkArchive.Open(path);

            Assert.NotNull(archive);
            Assert.True(archive!.Ready);
            Assert.Equal(wanted, archive.Read("_.index.bin"));
            Assert.Contains("Content.ggpk", archive.Describe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDTheMacBuildsNamesAreReadAsWideAsItWroteThem()
    {
        // Version 4 writes names as UTF-32 and everything else as UTF-16. Guessing one reads
        // every name in the archive as gibberish, so nothing is ever found.
        byte[] wanted = [1, 2, 3];
        string path = Build("_.index.bin", wanted, version: 4);

        try
        {
            GgpkArchive? archive = GgpkArchive.Open(path);
            Assert.NotNull(archive);
            Assert.Equal(wanted, archive!.Read("_.index.bin"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APARTOfAFileComesBackWithoutTheRestOfIt()
    {
        // What a bundle is read through: it runs to a couple of hundred megabytes and a wanted
        // chunk of it is a few hundred kilobytes.
        var content = new byte[500];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        string path = Build("data.bundle.bin", content);

        try
        {
            GgpkArchive? archive = GgpkArchive.Open(path);
            Assert.NotNull(archive);

            foreach ((int at, int length) in new[] { (0, 60), (60, 4), (100, 200), (499, 1), (0, 500) })
            {
                Assert.Equal(content[at..(at + length)], archive!.Read("data.bundle.bin", at, length));
            }

            // Asked for repeatedly, as reading a file out of a bundle chunk by chunk does. The
            // walk to it is remembered between these, so this is also what says the remembering
            // hands back the same place rather than a stale one.
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(content[64..96], archive!.Read("data.bundle.bin", 64, 32));
                Assert.Equal(content, archive.Read("data.bundle.bin"));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDARangeThatIsNotInsideTheFileIsRefused()
    {
        string path = Build("_.index.bin", [1, 2, 3, 4, 5, 6, 7, 8]);

        try
        {
            GgpkArchive? archive = GgpkArchive.Open(path);

            Assert.Null(archive!.Read("_.index.bin", 0, 9));
            Assert.Null(archive.Read("_.index.bin", 8, 1));
            Assert.Null(archive.Read("_.index.bin", -1, 2));
            Assert.Null(archive.Read("_.index.bin", 2, -2));
            Assert.Null(archive.Read("nothing.bin", 0, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDAFileThatIsNotThereIsNotThereRatherThanAnException()
    {
        string path = Build("_.index.bin", [1]);

        try
        {
            GgpkArchive? archive = GgpkArchive.Open(path);
            Assert.NotNull(archive);
            Assert.Null(archive!.Read("nothing/here.bin"));
            Assert.Null(archive.Read("Art/2DItems/Bow.dds"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SOMETHINGThatIsNotAGgpkIsRefusedRatherThanParsed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"not-{Guid.NewGuid():N}.ggpk");
        try
        {
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x41, 256)]);

            Assert.Null(GgpkArchive.Open(path));
            Assert.Null(GgpkArchive.Open(Path.Combine(Path.GetTempPath(), "no-such.ggpk")));
            Assert.Null(GgpkArchive.Open(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ALOOSEInstallIsReadStraightOffTheDisk()
    {
        // The other shape, and the one a Steam install has: the path under Bundles2 is the path
        // on disk, with nothing to parse.
        string folder = Path.Combine(Path.GetTempPath(), $"game-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
            File.WriteAllBytes(Path.Combine(folder, "Bundles2", "_.index.bin"), [7, 7, 7]);

            var loose = new LooseArchive(folder);

            Assert.True(loose.Ready);
            Assert.Equal(new byte[] { 7, 7, 7 }, loose.Read("_.index.bin"));
            Assert.Null(loose.Read("missing.bin"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDNothingIsReadFromOUTSIDETheGamesFolder()
    {
        // The paths come from an index inside the game's own files, which is not something to
        // trust with a path that walks upwards.
        string folder = Path.Combine(Path.GetTempPath(), $"game-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
            File.WriteAllBytes(Path.Combine(folder, "Bundles2", "_.index.bin"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "secret.txt"), [9]);

            var loose = new LooseArchive(folder);
            Assert.Null(loose.Read("../secret.txt"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANEmptyInstallSaysSoRatherThanFailingLater()
    {
        Assert.False(new LooseArchive(Path.GetTempPath()).Ready);
        Assert.False(new LooseArchive(string.Empty).Ready);
    }
}
