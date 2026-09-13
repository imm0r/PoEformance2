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
/// up, and the path blob is not touched at all. That is the difference between answering in
/// microseconds and unpacking a list of half a million file names first.
///
/// WHICH LEAVES ONE QUESTION IT CANNOT ANSWER, and that is why the blob is read after all: a
/// hash lookup needs the WHOLE path, and half the data this tool ships knows only what a file is
/// CALLED - "AtlasIconContentBreach" - because the tables that publish it keep only the last
/// part. There is no way to search a table of hashes for a name, and folders cannot be guessed
/// at. <see cref="Look"/> walks the blob once and turns names into paths, which is the only way
/// to be right about where the game keeps its art and to stay right when a patch moves it.
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

    /// <summary>How many bytes one file record takes: hash, bundle, offset, size.</summary>
    /// <remarks>
    /// THE BOUND ON EVERY COUNT IS THE FILE ITSELF, and it used to be a number instead - two
    /// million, which the game outgrew. A count that claims more records than there are bytes
    /// left to hold them is refused, which is exactly as strict against a wrong offset and
    /// cannot go stale: a magic ceiling can only ever become wrong as the game grows, and this
    /// one did, silently, reported as "not an index".
    /// </remarks>
    public const int FileRecord = 20;

    private readonly Dictionary<ulong, FileSpot> _files;
    private readonly bool _murmur;

    // Where each directory's slice of the spelled-out paths begins and how long it is. The hash
    // and the recursive size of each record are dropped: nothing here browses by directory, and
    // half a megabyte of numbers nobody reads is half a megabyte.
    private readonly (int At, int Size)[] _folders;

    // The compressed blob of spelled-out paths - a whole bundle of its own, sitting after the
    // directory array. Kept because it is the only place the game says what its files are CALLED.
    private readonly byte[] _named;

    private BundleIndex(
        string[] bundles,
        Dictionary<ulong, FileSpot> files,
        bool murmur,
        (int At, int Size)[] folders,
        byte[] named,
        int stride)
    {
        Bundles = bundles;
        _files = files;
        _murmur = murmur;
        _folders = folders;
        _named = named;
        Stride = stride;
    }

    /// <summary>The bundles, in the order the index lists them - a file record names one by number.</summary>
    public IReadOnlyList<string> Bundles { get; }

    /// <summary>How many files it knows about.</summary>
    public int Count => _files.Count;

    /// <summary>Which hash this index was built with, for a window to show.</summary>
    public string Hashing => _murmur ? "Murmur2-64A" : "FNV-1a";

    /// <summary>
    /// How many bytes one directory record turned out to be - twenty, or twenty-four padded.
    /// </summary>
    /// <remarks>
    /// MEASURED, NOT ASSUMED, and this project had it wrong in a comment for months. The record
    /// is a 64-bit hash and three 32-bit numbers, which is twenty bytes; a C# struct of those
    /// four fields is twenty-FOUR, because the runtime pads it out to its alignment, so a
    /// reference that reads the array as a raw span of that struct appears to say the file is
    /// padded too. Two independent sources say it is not - the format write-up at
    /// poe-tool-dev/ggpk.discussion, and poe-bundle-lib, which reads real installs with a flat
    /// twenty.
    ///
    /// Rather than pick a side, <see cref="Read"/> tries both and keeps whichever leaves a
    /// readable bundle header at the end - the wrong stride misses by four bytes per directory,
    /// which is megabytes, so the check is decisive rather than merely plausible. This says
    /// which one the install actually used, so the answer is on screen instead of in a comment.
    /// </remarks>
    public int Stride { get; }

    /// <summary>Whether this index can say what its files are CALLED, not just where they are.</summary>
    public bool Named => _named.Length > 0 && _folders.Length > 0;

    /// <summary>
    /// What this index is, in one line: how much is in it, how it is hashed, and whether it can
    /// name what it holds.
    /// </summary>
    /// <remarks>
    /// SAID IN ONE PLACE. The sentence used to be written out twice - once by the reader as it
    /// reported what it had read, once by the install as it described itself - and the two drifted
    /// the moment one of them learned something new, which is a test failure rather than a bug
    /// only because there happens to be a test comparing them.
    /// </remarks>
    public string Says
        => $"{Count} files, {Bundles.Count} bundles, {Hashing}"
           + (Named ? $", names at a {Stride}-byte stride" : ", no names");

    /// <summary>
    /// Reads the index out of the decompressed content of <c>_.index.bin</c>.
    /// </summary>
    /// <returns>Null when the bytes are not an index, or use a hash this does not know.</returns>
    public static BundleIndex? Parse(byte[]? content) => Read(content).Index;

    /// <summary>What reading it came to, and - when it came to nothing - which check refused.</summary>
    /// <param name="Index">The index, or null.</param>
    /// <param name="Why">What happened, in words, whether or not it worked.</param>
    /// <remarks>
    /// EIGHT WAYS TO FAIL AND ONE SENTENCE FOR ALL OF THEM was how this read before, and the
    /// sentence it produced - "the index decompressed to 147897312 bytes but is not an index" -
    /// is true of a count that overran a bound, of a name length out of range, and of a root
    /// hash this does not recognise. Three different problems, three different cures, and
    /// nothing in it to tell them apart.
    /// </remarks>
    public readonly record struct Parsed(BundleIndex? Index, string Why);

    /// <summary>Reads the index, and says which check refused it when one does.</summary>
    public static Parsed Read(byte[]? content)
    {
        if (content is null || content.Length < 12)
        {
            return new Parsed(null, $"only {content?.Length ?? 0} bytes - too short to be one");
        }

        try
        {
            var at = 0;
            int bundleCount = Int(content, ref at);
            if (bundleCount < 0 || (long)bundleCount * 8 > content.Length)
            {
                return new Parsed(null, $"claims {bundleCount} bundles, which will not fit in {content.Length} bytes");
            }

            var bundles = new string[bundleCount];
            for (int i = 0; i < bundleCount; i++)
            {
                int nameLength = Int(content, ref at);
                if (nameLength is < 0 or > 4096 || at + nameLength + 4 > content.Length)
                {
                    return new Parsed(null, $"bundle {i} of {bundleCount} has a name {nameLength} bytes long");
                }

                bundles[i] = System.Text.Encoding.UTF8.GetString(content, at, nameLength);
                at += nameLength;
                _ = Int(content, ref at);   // its uncompressed size, which reading the bundle gives anyway
            }

            int fileCount = Int(content, ref at);
            if (fileCount < 0 || at + ((long)fileCount * FileRecord) > content.Length)
            {
                return new Parsed(
                    null,
                    $"claims {fileCount} files after {bundleCount} bundles, which needs "
                    + $"{(long)fileCount * FileRecord} bytes and has {content.Length - at} left");
            }

            var files = new Dictionary<ulong, FileSpot>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                ulong hash = BitConverter.ToUInt64(content, at);
                int bundle = BitConverter.ToInt32(content, at + 8);
                int spot = BitConverter.ToInt32(content, at + 12);
                int size = BitConverter.ToInt32(content, at + 16);
                at += FileRecord;

                if ((uint)bundle < (uint)bundleCount && spot >= 0 && size >= 0)
                {
                    files[hash] = new FileSpot(bundle, spot, size);
                }
            }

            int directoryCount = Int(content, ref at);
            if (directoryCount <= 0 || at + 8 > content.Length)
            {
                return new Parsed(
                    null, $"claims {directoryCount} directories with {content.Length - at} bytes left");
            }

            // The root's own hash, which is what says how everything else was hashed. It is the
            // first record's, and the record layout is settled after it - see Stride.
            ulong root = BitConverter.ToUInt64(content, at);
            bool murmur;
            switch (root)
            {
                case MurmurMarker: murmur = true; break;
                case FnvMarker: murmur = false; break;
                default:
                    return new Parsed(
                        null,
                        $"{bundleCount} bundles and {files.Count} files read, but the root directory "
                        + $"hashes to 0x{root:X16}, which is neither Murmur2-64A nor FNV-1a - a hash "
                        + "this does not know");
            }

            (int At, int Size)[] folders = [];
            byte[] named = [];
            int stride = 0;

            // The names, if the file still has room for them. Everything above is what a LOOKUP
            // needs; this is what is needed to ask the other question - "what is this file
            // called" - and it is optional in the sense that an index cut short still opens.
            foreach (int guess in Strides)
            {
                long ends = (long)at + ((long)directoryCount * guess);
                if (ends > content.Length)
                {
                    continue;
                }

                // The tail must be a bundle. A stride that is wrong by four bytes per directory
                // is out by megabytes, so its "header" is arbitrary bytes and this refuses it.
                byte[] tail = content[(int)ends..];
                if (BundleFile.Open(tail) is null)
                {
                    continue;
                }

                folders = Folders(content, at, directoryCount, guess);
                named = tail;
                stride = guess;
                break;
            }

            return Held(new BundleIndex(bundles, files, murmur, folders, named, stride));
        }
        catch (ArgumentException)
        {
            // A file cut off part way through, which is what an interrupted patch leaves behind.
            // BitConverter throws ArgumentException rather than the out-of-range one for a read
            // that runs off the end, so catching only that one lets a damaged install throw out
            // of here - at startup, where there is nothing to catch it.
            return new Parsed(null, "it runs off its own end - a patch that did not finish");
        }
    }

    /// <summary>
    /// The directory-record sizes worth trying, in the order the evidence favours.
    /// </summary>
    /// <remarks>Twenty is what the format says; twenty-four is what a padded struct reads. See <see cref="Stride"/>.</remarks>
    private static readonly int[] Strides = [20, 24];

    /// <summary>Where each directory's slice of the path blob is.</summary>
    private static (int At, int Size)[] Folders(byte[] content, int at, int count, int stride)
    {
        var folders = new (int At, int Size)[count];
        for (int i = 0; i < count; i++)
        {
            int record = at + (i * stride);

            // Past the hash, which is the directory's own path and of no use here: this walks
            // every slice, so there is nothing to look one up BY.
            folders[i] = (BitConverter.ToInt32(content, record + 8), BitConverter.ToInt32(content, record + 12));
        }

        return folders;
    }

    private static Parsed Held(BundleIndex index) => new(index, index.Says);

    /// <summary>
    /// The full path of every file whose NAME is asked for, found by walking what the game
    /// calls its own files.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. Half the data this tool ships carries an art NAME and not a path -
    /// "AtlasIconContentBreach" - because the tables that publish it keep only the last part of
    /// what the game's own table holds. The index cannot be asked for a name: it is keyed by a
    /// hash of the WHOLE path, so a name finds nothing and no amount of guessing at folders is
    /// better than a coin toss. What the index does carry, at the very end and compressed, is
    /// every path spelled out - which is this. Walk it once and the guessing is over for good,
    /// and it is right again by itself the next time the game patches.
    ///
    /// EXPENSIVE ONCE, so it takes the whole list rather than one name: unpacking the blob is
    /// tens of megabytes and walking it is half a million paths. Called on a background thread,
    /// asked for everything at once, and then never again for the rest of the session.
    ///
    /// NOTHING IS ALLOCATED FOR A PATH THAT IS NOT WANTED. Half a million paths turned into
    /// strings to compare them would be worse than the walk itself, so a path is assembled into
    /// a reused buffer, its name is hashed where it lies, and only a hit becomes a string.
    ///
    /// The encoding is the reference's: a word of nought flips between collecting prefixes and
    /// emitting paths (and clears the prefixes when it turns collection ON), and any other word
    /// is a one-based index into those prefixes followed by a NUL-terminated tail. An index past
    /// the end of the list means no prefix at all.
    /// </remarks>
    /// <param name="decompress">How to undo Oodle - the same one the bundles are read with.</param>
    /// <param name="wanted">
    /// Names without a folder or an extension, as the data spells them. Case does not matter.
    /// </param>
    /// <returns>Each name that was found, against its whole path. Names not in the install are absent.</returns>
    public Dictionary<string, string> Look(
        Func<ReadOnlyMemory<byte>, int, byte[]?> decompress, IReadOnlyCollection<string>? wanted)
    {
        ArgumentNullException.ThrowIfNull(decompress);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Named || wanted is not { Count: > 0 })
        {
            return found;
        }

        // What is being looked for, by the hash of its name, so a path can be tested without
        // being spelled out. A name appearing twice in the list is one entry, which is why the
        // hit is confirmed against the name itself rather than trusted from the hash.
        var asked = new Dictionary<ulong, List<string>>();
        foreach (string name in wanted)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            ulong stamp = Stamp(System.Text.Encoding.UTF8.GetBytes(name));
            if (!asked.TryGetValue(stamp, out List<string>? same))
            {
                asked[stamp] = same = [];
            }

            if (!same.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                same.Add(name);
            }
        }

        if (asked.Count == 0)
        {
            return found;
        }

        BundleFile? blob = BundleFile.Open(_named);
        byte[]? paths = blob?.Read(decompress);
        if (paths is null)
        {
            return found;
        }

        Walk(paths, asked, found);
        return found;
    }

    /// <summary>How long a path may be while this still assembles it. Anything longer is not one.</summary>
    private const int LongestPath = 1024;

    /// <summary>Walks every spelled-out path, keeping the ones asked for.</summary>
    private void Walk(byte[] paths, Dictionary<ulong, List<string>> asked, Dictionary<string, string> found)
    {
        var whole = new byte[LongestPath];
        var prefixes = new List<byte[]>();

        foreach ((int at, int size) in _folders)
        {
            if (at < 0 || size < 0 || (long)at + size > paths.Length)
            {
                continue;   // a slice that is not in the blob - a truncated or wrong-strided read
            }

            prefixes.Clear();
            bool collecting = false;
            int cursor = at;
            int ends = at + size;

            while (cursor <= ends - 4)
            {
                int word = BitConverter.ToInt32(paths, cursor);
                cursor += 4;

                if (word == 0)
                {
                    collecting = !collecting;
                    if (collecting)
                    {
                        prefixes.Clear();
                    }

                    continue;
                }

                int tail = cursor;
                while (tail < ends && paths[tail] != 0)
                {
                    tail++;
                }

                var rest = new ReadOnlySpan<byte>(paths, cursor, tail - cursor);
                cursor = tail + 1;

                int which = word - 1;
                ReadOnlySpan<byte> start = (uint)which < (uint)prefixes.Count
                    ? prefixes[which]
                    : ReadOnlySpan<byte>.Empty;

                if (start.Length + rest.Length > whole.Length)
                {
                    continue;   // not a path; do not grow a buffer on a number out of the blob
                }

                start.CopyTo(whole);
                rest.CopyTo(whole.AsSpan(start.Length));
                var path = new ReadOnlySpan<byte>(whole, 0, start.Length + rest.Length);

                if (collecting)
                {
                    prefixes.Add(path.ToArray());
                    continue;
                }

                Keep(path, asked, found);
            }
        }
    }

    /// <summary>Keeps a path when its own name is one of the wanted ones.</summary>
    /// <remarks>
    /// A PATH ALREADY FOUND IS NOT REPLACED, which makes the answer the same on every run. The
    /// game has the same name in more than one folder here and there, and taking the last would
    /// hand back whichever one the walk happened to reach second.
    /// </remarks>
    private static void Keep(
        ReadOnlySpan<byte> path, Dictionary<ulong, List<string>> asked, Dictionary<string, string> found)
    {
        int slash = path.LastIndexOf((byte)'/');
        ReadOnlySpan<byte> name = slash >= 0 ? path[(slash + 1)..] : path;

        int dot = name.LastIndexOf((byte)'.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        if (name.IsEmpty || !asked.TryGetValue(Stamp(name), out List<string>? same))
        {
            return;
        }

        foreach (string one in same)
        {
            if (found.ContainsKey(one) || !Same(name, one))
            {
                continue;
            }

            found[one] = System.Text.Encoding.UTF8.GetString(path);
        }
    }

    /// <summary>Whether a name in the blob is the name that was asked for, ignoring case.</summary>
    private static bool Same(ReadOnlySpan<byte> name, string wanted)
    {
        if (name.Length != wanted.Length)
        {
            return false;   // true for anything ASCII, which every art name is
        }

        for (int i = 0; i < name.Length; i++)
        {
            if (Lower(name[i]) != Lower((byte)wanted[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A hash of a file's name, for finding it among the wanted ones.
    /// </summary>
    /// <remarks>
    /// FNV-1a over the lowercased bytes, and deliberately NOT <see cref="Fnv"/>: that one is the
    /// game's PATH hash, which trims a trailing slash and ends with two plus signs. Borrowing it
    /// would tie a private lookup to a format detail that exists for another purpose entirely.
    /// </remarks>
    private static ulong Stamp(ReadOnlySpan<byte> name)
    {
        const ulong prime = 0x100000001B3;
        ulong hash = 0xCBF29CE484222325;

        unchecked
        {
            foreach (byte one in name)
            {
                hash = (hash ^ Lower(one)) * prime;
            }
        }

        return hash;
    }

    private static byte Lower(byte one) => one is >= (byte)'A' and <= (byte)'Z' ? (byte)(one + 32) : one;

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
