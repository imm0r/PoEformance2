using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading a file out of one of the game's bundles.
/// </summary>
/// <remarks>
/// The arithmetic is what these are about. A bundle is compressed in independent chunks, so
/// reading bytes 300..900 of it means working out which chunks those fall in, where each one
/// starts in the compressed file, and which part of each decompressed chunk was actually asked
/// for. Every one of those is off-by-one country, and getting any of them wrong gives an icon
/// that is subtly the wrong bytes rather than an error.
/// </remarks>
public class BundleFileTests
{
    private static byte[] Counting(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);   // a prime, so the pattern does not line up with the chunks
        }

        return bytes;
    }

    [Fact]
    public void AFILEComesBackOutOfABundle()
    {
        byte[] content = Counting(40);
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(content, chunkSize: 8));

        Assert.NotNull(bundle);
        Assert.Equal(40, bundle!.Uncompressed);
        Assert.Equal(8, bundle.ChunkSize);
        Assert.Equal(5, bundle.Chunks);
        Assert.Equal(content, bundle.Read(Packed.AsIs));
    }

    [Fact]
    public void ANDAReadThatCrossesChunksIsStitchedBackTogether()
    {
        // The one that matters: a file inside a bundle rarely starts on a chunk boundary, so
        // most reads are the end of one chunk, some whole ones, and the start of another.
        byte[] content = Counting(200);
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(content, chunkSize: 16));

        Assert.NotNull(bundle);

        foreach ((int at, int length) in new[] { (0, 200), (5, 3), (15, 2), (16, 16), (7, 100), (31, 65), (199, 1), (0, 1) })
        {
            Assert.Equal(content[at..(at + length)], bundle!.Read(at, length, Packed.AsIs));
        }
    }

    [Fact]
    public void ANDOnlyTheChunksItFallsInAreUnpacked()
    {
        // The whole reason for chunks. Reading one icon out of a two hundred megabyte bundle has
        // to cost one chunk, or a stash of two thousand items unpacks the bundle two thousand times.
        byte[] content = Counting(1000);
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(content, chunkSize: 100));

        var unpacked = new List<int>();
        byte[]? Watch(ReadOnlyMemory<byte> packed, int size)
        {
            unpacked.Add(packed.Length);
            return Packed.AsIs(packed, size);
        }

        Assert.Equal(content[450..470], bundle!.Read(450, 20, Watch));
        Assert.Single(unpacked);

        unpacked.Clear();
        Assert.Equal(content[90..210], bundle.Read(90, 120, Watch));
        Assert.Equal(3, unpacked.Count);
    }

    [Fact]
    public void ANDTheLastChunkIsAsShortAsWhatIsLeft()
    {
        // Told to the decompressor, not asked of it - so a wrong last chunk size is a failed
        // decompression of the last chunk of every bundle, which is where the tail of a file is.
        byte[] content = Counting(35);
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(content, chunkSize: 10));

        Assert.Equal(4, bundle!.Chunks);
        Assert.Equal(10, bundle.SizeOfChunk(0));
        Assert.Equal(10, bundle.SizeOfChunk(2));
        Assert.Equal(5, bundle.SizeOfChunk(3));

        var sizes = new List<int>();
        Assert.Equal(content, bundle.Read(0, 35, (packed, size) =>
        {
            sizes.Add(size);
            return Packed.AsIs(packed, size);
        }));

        Assert.Equal([10, 10, 10, 5], sizes);
    }

    [Fact]
    public void ANDAChunkThatWillNotUnpackIsNotAnIconRatherThanACrash()
    {
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(Counting(40), chunkSize: 8));

        Assert.Null(bundle!.Read((packed, size) => null));
        Assert.Null(bundle.Read(0, 40, (packed, size) => new byte[size - 1]));
    }

    [Fact]
    public void AREADPastTheEndIsRefused()
    {
        BundleFile? bundle = BundleFile.Open(Packed.Bundle(Counting(40), chunkSize: 8));

        Assert.Null(bundle!.Read(0, 41, Packed.AsIs));
        Assert.Null(bundle.Read(39, 2, Packed.AsIs));
        Assert.Null(bundle.Read(-1, 4, Packed.AsIs));
        Assert.Null(bundle.Read(4, -1, Packed.AsIs));
        Assert.Equal([], bundle.Read(4, 0, Packed.AsIs)!);
    }

    [Fact]
    public void SOMETHINGThatIsNotABundleIsRefusedRatherThanParsed()
    {
        Assert.Null(BundleFile.Open((byte[]?)null));
        Assert.Null(BundleFile.Open(new byte[10]));
        Assert.Null(BundleFile.Open(new byte[200]));                             // all zeroes
        Assert.Null(BundleFile.Open([.. Enumerable.Repeat((byte)0xFF, 4096)]));   // all ones

        // A truncated one: the header says more chunks than the file holds.
        byte[] bundle = Packed.Bundle(Counting(40), chunkSize: 8);
        Assert.Null(BundleFile.Open(bundle[..50]));
    }

    [Fact]
    public void ANDAHeaderWhoseNumbersDisagreeIsRefused()
    {
        // The cheapest way to notice a wrong offset: the chunk count and chunk size have to
        // account for the total, so a plausible-looking header that does not is not one.
        byte[] bundle = Packed.Bundle(Counting(40), chunkSize: 8);

        byte[] tooFew = [.. bundle];
        BitConverter.GetBytes(2).CopyTo(tooFew, 36);       // 2 chunks of 8 cannot hold 40
        Assert.Null(BundleFile.Open(tooFew));

        byte[] tooMany = [.. bundle];
        BitConverter.GetBytes(9).CopyTo(tooMany, 36);      // 9 chunks of 8 means the 9th is empty
        Assert.Null(BundleFile.Open(tooMany));
    }
}
