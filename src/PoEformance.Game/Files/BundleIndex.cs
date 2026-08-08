namespace PoEformance.Game.Files;

/// <summary>Where one file lives: which bundle, and where in it.</summary>
/// <param name="Bundle">Index into <see cref="BundleIndex.Bundles"/>.</param>
/// <param name="At">Where it starts in that bundle's decompressed content.</param>
/// <param name="Size">How long it is.</param>
public readonly record struct FileSpot(int Bundle, int At, int Size);

/// <summary>
/// The game's table of contents: <c>_.index.bin</c>, which says what is in which bundle.
/// </summary>
/// <remarks>
/// LOOKED UP BY HASH, NOT BY NAME. The index does not store paths next to files - it stores a
/// 64-bit hash of each path, and keeps the spelled-out paths in a separate compressed blob at
/// the end. So finding <c>art/2ditems/weapons/bow.dds</c> means hashing it and looking the hash
/// up, and the path blob never has to be decompressed at all. That is the difference between
/// answering in microseconds and unpacking a list of half a million file names first.
///
/// WHICH HASH IS IN THE FILE. GGG changed it in 3.21.2, from FNV-1a to Murmur2-64A, and picking
/// the wrong one finds nothing at all rather than failing loudly. It does not have to be
/// guessed: the first directory record is the ROOT, whose path is empty, so its stored hash is
/// whichever function's value for the empty string - and those two values are different
/// constants. The file says which hash hashed it.
///
/// Format from LibBundle3, which is the reference for it.
/// </remarks>
public sealed class BundleIndex
{
    /// <summary>What the root directory hashes to under Murmur2-64A, used since patch 3.21.2.</summary>
    public const ulong MurmurMarker = 0xF42A94E69CFF42FE;

    /// <summary>And what it hashes to under FNV-1a, used before it.</summary>
    public const ulong FnvMarker = 0x07E47507B4A92E53;

    /// <summary>A bound on the counts, so a wrong offset is refused rather than allocated from.</summary>
    public const int MostFiles = 2_000_000;

    private readonly Dictionary<ulong, FileSpot> _files;
    private readonly bool _murmur;

    private BundleIndex(string[] bundles, Dictionary<ulong, FileSpot> files, bool murmur)
    {
        Bundles = bundles;
        _files = files;
        _murmur = murmur;
    }

    /// <summary>The bundles, in the order the index lists them - a file record names one by number.</summary>
    public IReadOnlyList<string> Bundles { get; }

    /// <summary>How many files it knows about.</summary>
    public int Count => _files.Count;

    /// <summary>Which hash this index was built with, for a window to show.</summary>
    public string Hashing => _murmur ? "Murmur2-64A" : "FNV-1a";

    /// <summary>
    /// Reads the index out of the decompressed content of <c>_.index.bin</c>.
    /// </summary>
    /// <returns>Null when the bytes are not an index, or use a hash this does not know.</returns>
    public static BundleIndex? Parse(byte[]? content)
    {
        if (content is null || content.Length < 12)
        {
            return null;
        }

        try
        {
            var at = 0;
            int bundleCount = Int(content, ref at);
            if (bundleCount is < 0 or > MostFiles)
            {
                return null;
            }

            var bundles = new string[bundleCount];
            for (int i = 0; i < bundleCount; i++)
            {
                int nameLength = Int(content, ref at);
                if (nameLength is < 0 or > 4096 || at + nameLength + 4 > content.Length)
                {
                    return null;
                }

                bundles[i] = System.Text.Encoding.UTF8.GetString(content, at, nameLength);
                at += nameLength;
                _ = Int(content, ref at);   // its uncompressed size, which reading the bundle gives anyway
            }

            int fileCount = Int(content, ref at);
            if (fileCount is < 0 or > MostFiles || at + ((long)fileCount * 20) > content.Length)
            {
                return null;
            }

            var files = new Dictionary<ulong, FileSpot>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                ulong hash = BitConverter.ToUInt64(content, at);
                int bundle = BitConverter.ToInt32(content, at + 8);
                int spot = BitConverter.ToInt32(content, at + 12);
                int size = BitConverter.ToInt32(content, at + 16);
                at += 20;

                if ((uint)bundle < (uint)bundleCount && spot >= 0 && size >= 0)
                {
                    files[hash] = new FileSpot(bundle, spot, size);
                }
            }

            int directoryCount = Int(content, ref at);
            if (directoryCount <= 0 || at + 8 > content.Length)
            {
                return null;
            }

            // The root's own hash, which is what says how everything else was hashed. Only the
            // FIRST record is read: the rest of the array, and the compressed blob of spelled-out
            // paths after it, are what a file BROWSER needs, and nothing here browses.
            //
            // Should that ever change - a directory record is TWENTY-FOUR bytes, not the twenty
            // its four fields add up to. The reference reads the array as a raw span of a struct
            // the runtime pads out to its eight-byte alignment, and writes it back the same way,
            // so the padding is really in the file. Reading it as twenty walks off the end of the
            // array and the path blob after it comes out as noise.
            ulong root = BitConverter.ToUInt64(content, at);
            return root switch
            {
                MurmurMarker => new BundleIndex(bundles, files, murmur: true),
                FnvMarker => new BundleIndex(bundles, files, murmur: false),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            // A file cut off part way through, which is what an interrupted patch leaves behind.
            // BitConverter throws ArgumentException rather than the out-of-range one for a read
            // that runs off the end, so catching only that one lets a damaged install throw out
            // of here - at startup, where there is nothing to catch it.
            return null;
        }
    }

    /// <summary>Where a path's file is, or null when the index has no such path.</summary>
    /// <param name="path">Forward slashes, from the top - <c>art/2ditems/weapons/bow.dds</c>.</param>
    public FileSpot? Find(string? path)
        => string.IsNullOrWhiteSpace(path) ? null
            : _files.TryGetValue(Hash(path, _murmur), out FileSpot spot) ? spot
            : null;

    /// <summary>
    /// What a path hashes to.
    /// </summary>
    /// <remarks>
    /// LOWERCASED FIRST, both ways. The game writes art paths in mixed case and hashes them in
    /// lower, so a path used as it was read finds nothing. Murmur is given lowercase bytes;
    /// FNV lowercases as it goes.
    /// </remarks>
    public static ulong Hash(string path, bool murmur)
    {
        ArgumentNullException.ThrowIfNull(path);

        string text = path.Replace('\\', '/').Trim();
        return murmur
            ? Murmur(System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
            : Fnv(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Murmur2-64A, as GGG uses it: seeded oddly, and with a trailing slash trimmed.</summary>
    public static ulong Murmur(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return MurmurMarker;
        }

        if (utf8[^1] == (byte)'/')
        {
            utf8 = utf8[..^1];
        }

        const ulong m = 0xC6A4A7935BD1E995;
        const int r = 47;

        unchecked
        {
            ulong hash = 0x1337B33F ^ ((ulong)utf8.Length * m);

            int whole = utf8.Length / 8;
            for (int i = 0; i < whole; i++)
            {
                ulong k = BitConverter.ToUInt64(utf8[(i * 8)..]) * m;
                k ^= k >> r;
                hash = (hash ^ (k * m)) * m;
            }

            // The leftover bytes, little end first. The reference reads them as one word and
            // masks it, which reads past the string - the same number, one way that is allowed.
            int rest = utf8.Length % 8;
            if (rest != 0)
            {
                ulong tail = 0;
                for (int i = rest - 1; i >= 0; i--)
                {
                    tail = (tail << 8) | utf8[(whole * 8) + i];
                }

                hash = (hash ^ tail) * m;
            }

            hash = (hash ^ (hash >> r)) * m;
            return hash ^ (hash >> r);
        }
    }

    /// <summary>FNV-1a, as GGG used it before 3.21.2: lowercasing as it goes, and ending with two plus signs.</summary>
    public static ulong Fnv(ReadOnlySpan<byte> utf8)
    {
        const ulong prime = 0x100000001B3;
        ulong hash = 0xCBF29CE484222325;

        unchecked
        {
            if (utf8.Length > 0 && utf8[^1] == (byte)'/')
            {
                utf8 = utf8[..^1];
                foreach (byte one in utf8)
                {
                    hash = (hash ^ one) * prime;
                }
            }
            else
            {
                foreach (byte one in utf8)
                {
                    hash = (hash ^ (one is >= (byte)'A' and <= (byte)'Z' ? (ulong)(one + 32) : one)) * prime;
                }
            }

            return (((hash ^ '+') * prime) ^ '+') * prime;
        }
    }

    private static int Int(byte[] from, ref int at)
    {
        int value = BitConverter.ToInt32(from, at);
        at += 4;
        return value;
    }
}
