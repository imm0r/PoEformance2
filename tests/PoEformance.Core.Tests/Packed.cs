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

    /// <summary>
    /// Writes an <c>.ast</c> the way the format lays one out, so the reader can walk it back.
    /// </summary>
    /// <remarks>
    /// FROM THE LAYOUT AND NOT FROM THE READER, like everything else here. The layout itself was
    /// measured off a real rig - see AnimationSkeleton - and what this pins is that the reader
    /// agrees with what was written down, which is the half a synthetic fixture can prove.
    /// </remarks>
    /// <param name="bones">Each bone's name, its sibling and child indices, and its resting height.</param>
    /// <param name="animations">Each animation's header, offsets included.</param>
    /// <param name="tail">The bundle of keyframes, from <see cref="Bundle"/>, or null for none.</param>
    /// <param name="version">The file version. 12 is what PoE 2 ships.</param>
    public static byte[] Skeleton(
        (string Name, int Sibling, int Child, float Z)[] bones,
        (string Name, string Parent, int Tracks, int Rate, int Kind, int At, int Length)[] animations,
        byte[]? tail = null,
        int version = 12)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write((byte)version);
        write.Write((byte)bones.Length);
        write.Write((byte)7);                       // the byte at offset 2 nobody has explained
        write.Write((ushort)animations.Length);
        write.Write((byte)0);
        write.Write((byte)0);
        write.Write((byte)0);                       // lights

        foreach ((string name, int sibling, int child, float z) in bones)
        {
            write.Write((byte)sibling);
            write.Write((byte)child);

            // An identity matrix with the bone's resting place in the last row, which is where a
            // row-major 4x4 keeps its translation.
            for (var cell = 0; cell < 16; cell++)
            {
                write.Write(cell switch { 0 or 5 or 10 or 15 => 1f, 14 => z, _ => 0f });
            }

            byte[] said = Encoding.ASCII.GetBytes(name);
            write.Write((byte)said.Length);
            if (version >= 8)
            {
                write.Write((byte)0);
            }

            write.Write(said);
        }

        foreach ((string name, string parent, int tracks, int rate, int kind, int at, int length) in animations)
        {
            byte[] called = Encoding.ASCII.GetBytes(name);
            byte[] from = version >= 11 ? Encoding.ASCII.GetBytes(parent) : [];

            write.Write((byte)tracks);
            write.Write((byte)0);
            write.Write((byte)rate);
            write.Write((byte)kind);
            if (version >= 10)
            {
                write.Write((byte)0);
            }

            write.Write((byte)called.Length);
            if (version >= 11)
            {
                write.Write((byte)from.Length);
            }

            if (version >= 8)
            {
                write.Write(at);
                write.Write(length);
            }

            write.Write(called);
            write.Write(from);
        }

        if (tail is { Length: > 0 })
        {
            write.Write(tail);
        }

        write.Flush();
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
    /// <param name="paths">
    /// The spelled-out paths, from <see cref="Paths"/>, or null for an index that carries none.
    /// </param>
    /// <param name="stride">
    /// How many bytes one directory record takes. Twenty is what the fields add up to;
    /// twenty-four is what a C# struct of them measures once the runtime has padded it, which is
    /// what a reference reading the array as a raw span appears to say the file uses. Both are
    /// written here because the reader is supposed to tell them apart by itself.
    /// </param>
    public static byte[] Index(
        string[] bundles, Entry[] files, bool murmur = true, byte[]? paths = null, int stride = 20)
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

        // One directory record, the root: a hash, where its slice of the spelled-out paths
        // starts, how long it is, and how long it is with its subdirectories. Twenty bytes of
        // fields - written at either stride, because which one the game uses is the thing the
        // reader works out for itself.
        write.Write(1);
        write.Write(murmur ? 0xF42A94E69CFF42FEul : 0x07E47507B4A92E53ul);
        write.Write(0);
        write.Write(paths?.Length ?? 0);
        write.Write(paths?.Length ?? 0);
        if (stride > 20)
        {
            write.Write(new byte[stride - 20]);
        }

        // The spelled-out paths, as a bundle of their own at the very end. An index without one
        // is what a reader that only ever looked things up by hash leaves behind, and it still
        // has to open.
        if (paths is not null)
        {
            write.Write(Bundle(paths, chunkSize: Math.Max(1, paths.Length)));
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Writes the spelled-out paths the way the index carries them.
    /// </summary>
    /// <remarks>
    /// From the format's own description rather than from the reader: a word of nought flips
    /// between collecting prefixes and emitting paths - and the section starts with one, so the
    /// first flip turns collecting ON - while any other word is a ONE-BASED index into the
    /// prefixes collected so far, followed by a NUL-terminated tail. A word pointing past the
    /// end of that list means the tail stands on its own.
    /// </remarks>
    /// <param name="bases">The prefixes, each one whole (this writer gives them no prefixes of their own).</param>
    /// <param name="made">Each path to emit: which prefix it starts with, or -1 for none, and its tail.</param>
    public static byte[] Paths(string[] bases, (int Base, string Tail)[] made)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);

        write.Write(0);   // flips collecting ON, and clears whatever was collected
        for (int i = 0; i < bases.Length; i++)
        {
            // Past the end of the list as it stands, so the prefix is taken as standing alone.
            write.Write(i + 1);
            write.Write(Encoding.UTF8.GetBytes(bases[i]));
            write.Write((byte)0);
        }

        write.Write(0);   // and OFF again: what follows is paths rather than prefixes
        foreach ((int prefix, string tail) in made)
        {
            write.Write(prefix < 0 ? bases.Length + 1 : prefix + 1);
            write.Write(Encoding.UTF8.GetBytes(tail));
            write.Write((byte)0);
        }

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
