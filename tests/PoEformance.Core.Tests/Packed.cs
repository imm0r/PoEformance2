using System.Text;

namespace PoEformance.Core.Tests;

/// <summary>
/// Builds the game's packed formats, so they can be read back.
/// </summary>
/// <remarks>
/// Written from LibBundle3's description of the formats rather than from the readers, because a
/// fixture that agrees with the reader by construction proves nothing. Where the two disagree
/// the test fails, which is the point of writing it this way round.
///
/// The compression is left out - a chunk is stored as it is. Oodle has no compressor to build a
/// fixture with, so the arithmetic AROUND the compression is what these check, and the readers
/// take the decompression as something handed in for exactly that reason.
/// </remarks>
internal static class Packed
{
    /// <summary>Stands in for Oodle: a chunk that was never compressed comes back as it is.</summary>
    public static byte[]? AsIs(ReadOnlyMemory<byte> packed, int size)
        => packed.Length >= size ? packed.Span[..size].ToArray() : null;

    /// <summary>
    /// Writes a bundle holding some content, cut into chunks of the given size.
    /// </summary>
    public static byte[] Bundle(byte[] content, int chunkSize = 8)
    {
        int chunks = Math.Max(1, (content.Length + chunkSize - 1) / chunkSize);

        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(content.Length);              // 0  uncompressed size
        write.Write(content.Length);              // 4  compressed size, the same with no compression
        write.Write((chunks * 4) + 48);           // 8  head size
        write.Write(9);                           // 12 the compressor, Kraken as a number
        write.Write(1);                           // 16
        write.Write((long)content.Length);        // 20 uncompressed size again, as a long
        write.Write((long)content.Length);        // 28 and the compressed one
        write.Write(chunks);                      // 36 THE CHUNK COUNT, after the longs
        write.Write(chunkSize);                   // 40
        write.Write(new byte[16]);                // 44 four unknowns

        for (int i = 0; i < chunks; i++)
        {
            write.Write(Math.Min(chunkSize, content.Length - (i * chunkSize)));
        }

        write.Write(content);
        return stream.ToArray();
    }

    /// <summary>One file's place in the index.</summary>
    public sealed record Entry(string Path, int Bundle, int At, int Size);

    /// <summary>
    /// Writes the content of <c>_.index.bin</c> - before it is itself put in a bundle.
    /// </summary>
    /// <param name="murmur">
    /// Which hash the paths are hashed with, written into the root directory record the way the
    /// game does it: the root's path is empty, so its stored hash is that function's value for
    /// the empty string, and the two functions have different ones.
    /// </param>
    public static byte[] Index(string[] bundles, Entry[] files, bool murmur = true)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(bundles.Length);
        foreach (string bundle in bundles)
        {
            byte[] named = Encoding.UTF8.GetBytes(bundle);
            write.Write(named.Length);
            write.Write(named);
            write.Write(1024);   // its uncompressed size
        }

        write.Write(files.Length);
        foreach (Entry file in files)
        {
            write.Write(Hash(file.Path, murmur));
            write.Write(file.Bundle);
            write.Write(file.At);
            write.Write(file.Size);
        }

        // One directory record, the root. TWENTY-FOUR bytes: its four fields add up to twenty
        // and the runtime pads the struct out to its alignment, which is really in the file.
        write.Write(1);
        write.Write(murmur ? 0xF42A94E69CFF42FEul : 0x07E47507B4A92E53ul);
        write.Write(0);
        write.Write(0);
        write.Write(0);
        write.Write(0);

        return stream.ToArray();
    }

    /// <summary>Hashes a path the way the game does, written out here rather than called from the reader.</summary>
    public static ulong Hash(string path, bool murmur)
        => murmur ? Murmur(Encoding.UTF8.GetBytes(path.ToLowerInvariant())) : Fnv(Encoding.UTF8.GetBytes(path));

    /// <summary>MurmurHash64A as Appleby wrote it, with a byte-by-byte tail.</summary>
    private static ulong Murmur(byte[] key)
    {
        if (key.Length == 0)
        {
            return 0xF42A94E69CFF42FE;
        }

        const ulong m = 0xC6A4A7935BD1E995;
        const int r = 47;

        unchecked
        {
            ulong h = 0x1337B33F ^ ((ulong)key.Length * m);

            int whole = key.Length / 8;
            for (int i = 0; i < whole; i++)
            {
                ulong k = BitConverter.ToUInt64(key, i * 8);
                k *= m;
                k ^= k >> r;
                k *= m;
                h ^= k;
                h *= m;
            }

            int tail = whole * 8;
            switch (key.Length & 7)
            {
                case 7: h ^= (ulong)key[tail + 6] << 48; goto case 6;
                case 6: h ^= (ulong)key[tail + 5] << 40; goto case 5;
                case 5: h ^= (ulong)key[tail + 4] << 32; goto case 4;
                case 4: h ^= (ulong)key[tail + 3] << 24; goto case 3;
                case 3: h ^= (ulong)key[tail + 2] << 16; goto case 2;
                case 2: h ^= (ulong)key[tail + 1] << 8; goto case 1;
                case 1:
                    h ^= key[tail];
                    h *= m;
                    break;
                default: break;
            }

            h ^= h >> r;
            h *= m;
            h ^= h >> r;
            return h;
        }
    }

    /// <summary>FNV-1a 64, lowercasing as it goes and ending with the two plus signs the game adds.</summary>
    private static ulong Fnv(byte[] key)
    {
        const ulong prime = 0x100000001B3;
        ulong hash = 0xCBF29CE484222325;

        unchecked
        {
            foreach (byte one in key)
            {
                byte lower = one is >= (byte)'A' and <= (byte)'Z' ? (byte)(one + 32) : one;
                hash = (hash ^ lower) * prime;
            }

            return (((hash ^ '+') * prime) ^ '+') * prime;
        }
    }
}
