using System.Text;

namespace PoEformance.Game.Files;

/// <summary>
/// Where the game keeps its packed files, whichever way this install keeps them.
/// </summary>
/// <remarks>
/// TWO SHAPES, ONE SET OF FILES. An install either has a loose <c>Bundles2</c> folder or one
/// <c>Content.ggpk</c> holding the same folder inside it - the contents are the same either way,
/// which is the whole reason this is an interface with two small implementations rather than two
/// features. Assuming the loose folder would simply find nothing on a standalone install.
/// </remarks>
public interface IGameArchive
{
    /// <summary>Whether this install is readable at all.</summary>
    bool Ready { get; }

    /// <summary>Reads one file under <c>Bundles2</c>, or null when it is not there.</summary>
    /// <param name="path">Slash-separated and relative to Bundles2 - <c>_.index.bin</c>.</param>
    byte[]? Read(string path);

    /// <summary>
    /// Reads part of one file.
    /// </summary>
    /// <remarks>
    /// Here because a bundle is a couple of hundred megabytes and a wanted chunk of it is 256 KB.
    /// Reading the whole thing to slice it works and is what the plain <see cref="Read(string)"/>
    /// does, but doing it for every icon reads gigabytes to draw a stash.
    /// </remarks>
    byte[]? Read(string path, int at, int length);

    /// <summary>What this is reading, for a window to show.</summary>
    string Describe { get; }
}

/// <summary>
/// An install whose files sit in ordinary folders.
/// </summary>
/// <remarks>
/// The simpler of the two, and the one a Steam install has. Nothing to parse: the path under
/// Bundles2 is the path on disk.
/// </remarks>
public sealed class LooseArchive : IGameArchive
{
    private readonly string _bundles;

    public LooseArchive(string gameFolder)
        => _bundles = Path.Combine(gameFolder ?? string.Empty, "Bundles2");

    public bool Ready => File.Exists(Path.Combine(_bundles, "_.index.bin"));

    public string Describe => $"loose files at {_bundles}";

    public byte[]? Read(string path)
    {
        string? file = Inside(path);
        return file is not null && File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    public byte[]? Read(string path, int at, int length)
    {
        if (at < 0 || length < 0 || Inside(path) is not { } file || !File.Exists(file))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(file);
            if (at + (long)length > stream.Length)
            {
                return null;
            }

            stream.Position = at;
            var wanted = new byte[length];
            stream.ReadExactly(wanted);
            return wanted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a path lands on disk, or null when it lands outside the game's folder.
    /// </summary>
    /// <remarks>
    /// These paths come from an index inside the game's own files, which is not something to
    /// trust with one that walks upwards.
    /// </remarks>
    private string? Inside(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string file = Path.GetFullPath(Path.Combine(_bundles, path.Replace('/', Path.DirectorySeparatorChar)));
        return file.StartsWith(Path.GetFullPath(_bundles), StringComparison.OrdinalIgnoreCase) ? file : null;
    }
}

/// <summary>
/// An install whose files are all inside one <c>Content.ggpk</c>.
/// </summary>
/// <remarks>
/// A filesystem in a file. Every record is <c>[length][tag]</c> and then its own payload: GGPK
/// for the root, PDIR for a folder, FILE for a file, FREE for a hole. A folder lists its
/// children as (name hash, offset) pairs, so walking to a path means reading one record per step
/// rather than scanning anything.
///
/// Format from LibGGPK3, which is the reference for it. Two details there that a from-memory
/// version gets wrong: the name length COUNTS the null terminator, and a directory entry is
/// TWELVE bytes rather than sixteen - a four-byte hash and an eight-byte offset, packed to four.
/// Reading it as sixteen walks a folder into nonsense.
/// </remarks>
public sealed class GgpkArchive : IGameArchive
{
    private const uint TagGgpk = 0x4B504747;   // "GGPK"
    private const uint TagPdir = 0x52494450;   // "PDIR"
    private const uint TagFile = 0x454C4946;   // "FILE"

    /// <summary>How deep a walk may go. A guard on a file that says a folder contains itself.</summary>
    public const int Deepest = 32;

    /// <summary>And how many children one folder may have.</summary>
    public const int MostEntries = 100_000;

    /// <summary>How many looked-up files are remembered.</summary>
    /// <remarks>
    /// One entry is a path and two numbers. What it saves is the WALK - see <see cref="_found"/>.
    /// </remarks>
    public const int Remembered = 512;

    private readonly string _path;
    private readonly long _root;
    private readonly uint _version;

    /// <summary>
    /// Where a path's bytes were last found, so the walk to it happens once.
    /// </summary>
    /// <remarks>
    /// NOT a nicety. A bundle is read in chunks, so one icon is several reads of the same file,
    /// and a folder here is walked by comparing the NAME of each child in turn - which for the
    /// few hundred bundles under Bundles2 is a few hundred seeks EVERY time. A stash of two
    /// thousand items turns that into millions of them; with this it is one walk per file.
    /// </remarks>
    private readonly Dictionary<string, (long From, long Size)> _found = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    private GgpkArchive(string path, long root, uint version)
    {
        _path = path;
        _root = root;
        _version = version;
    }

    public bool Ready => _root > 0;

    public string Describe => $"Content.ggpk (version {_version}) at {_path}";

    /// <summary>Opens one, or null when the file is not a GGPK.</summary>
    public static GgpkArchive? Open(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);

            _ = reader.ReadUInt32();                    // the record's length, which the root does not need
            if (reader.ReadUInt32() != TagGgpk)
            {
                return null;
            }

            uint version = reader.ReadUInt32();
            long root = reader.ReadInt64();
            return root > 0 && root < stream.Length ? new GgpkArchive(file, root, version) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    public byte[]? Read(string path) => Read(path, 0, -1);

    public byte[]? Read(string path, int at, int length)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (at < 0 || length < -1)
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            using var reader = new BinaryReader(stream);

            if (Locate(reader, path) is not var (from, size))
            {
                return null;
            }

            int wanted = length < 0 ? checked((int)Math.Min(size, int.MaxValue)) : length;
            if (at + (long)wanted > size)
            {
                return null;
            }

            reader.BaseStream.Position = from + at;
            return reader.ReadBytes(wanted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or EndOfStreamException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Walks to a path's bytes, or remembers where they were the last time.</summary>
    private (long From, long Size)? Locate(BinaryReader reader, string path)
    {
        lock (_gate)
        {
            if (_found.TryGetValue(path, out (long From, long Size) already))
            {
                return already;
            }
        }

        // Everything this wants is under Bundles2, so the walk starts by stepping into it -
        // named here rather than by the caller, because a path relative to Bundles2 is what the
        // loose archive takes too, and the two have to agree.
        long record = _root;
        foreach (string step in Steps(path))
        {
            record = Into(reader, record, step);
            if (record < 0)
            {
                return null;
            }
        }

        if (Where(reader, record) is not var (from, size))
        {
            return null;
        }

        lock (_gate)
        {
            // Only what fits. A GGPK is read-only while this is open, so an entry cannot go
            // stale - but a session that reads its way through the whole archive should not end
            // up holding a note about every file in it.
            if (_found.Count < Remembered)
            {
                _found[path] = (from, size);
            }
        }

        return (from, size);
    }

    /// <summary>The path split into steps, under Bundles2.</summary>
    private static IEnumerable<string> Steps(string path)
    {
        yield return "Bundles2";
        foreach (string step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return step;
        }
    }

    /// <summary>Steps into one child of a folder, or -1 when it has no such child.</summary>
    /// <remarks>
    /// By NAME rather than by the hash the entries are sorted on. The hash is Murmur2 of the
    /// lowercased name, and reimplementing it to save a read per step would be a second thing
    /// to get wrong for no gain - a walk is half a dozen steps.
    /// </remarks>
    private long Into(BinaryReader reader, long folder, string want)
    {
        reader.BaseStream.Position = folder;
        _ = reader.ReadUInt32();
        if (reader.ReadUInt32() != TagPdir)
        {
            return -1;
        }

        int nameLength = reader.ReadInt32() - 1;   // the length counts the null terminator
        int entries = reader.ReadInt32();
        if (nameLength < 0 || entries is < 0 or > MostEntries)
        {
            return -1;
        }

        reader.BaseStream.Position += 32;          // the folder's own hash
        SkipName(reader, nameLength);

        for (int i = 0; i < entries; i++)
        {
            // TWELVE bytes an entry, not sixteen: a four-byte name hash and an eight-byte
            // offset, packed to four.
            _ = reader.ReadUInt32();
            long child = reader.ReadInt64();

            long back = reader.BaseStream.Position;
            if (Named(reader, child) is { } name && string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }

            reader.BaseStream.Position = back;
        }

        return -1;
    }

    /// <summary>What the record at an offset is called, whichever kind it is.</summary>
    private string? Named(BinaryReader reader, long offset)
    {
        if (offset <= 0 || offset >= reader.BaseStream.Length)
        {
            return null;
        }

        reader.BaseStream.Position = offset;
        _ = reader.ReadUInt32();
        uint tag = reader.ReadUInt32();
        if (tag is not (TagPdir or TagFile))
        {
            return null;
        }

        int nameLength = reader.ReadInt32() - 1;
        if (nameLength is < 0 or > 4096)
        {
            return null;
        }

        if (tag == TagPdir)
        {
            _ = reader.ReadInt32();   // the child count, which naming does not need
        }

        reader.BaseStream.Position += 32;
        return ReadName(reader, nameLength);
    }

    /// <summary>Where a file record's bytes start, and how many there are.</summary>
    private (long From, long Size)? Where(BinaryReader reader, long offset)
    {
        reader.BaseStream.Position = offset;
        uint length = reader.ReadUInt32();
        if (reader.ReadUInt32() != TagFile)
        {
            return null;
        }

        int nameLength = reader.ReadInt32() - 1;
        if (nameLength is < 0 or > 4096)
        {
            return null;
        }

        reader.BaseStream.Position += 32;
        SkipName(reader, nameLength);

        // What is left of the record is the file. Worked out from the record's own length
        // rather than from a size field, because there is not one.
        long data = reader.BaseStream.Position;
        long size = offset + length - data;
        return size is > 0 and < int.MaxValue ? (data, size) : null;
    }

    /// <summary>
    /// Reads a name, in whichever encoding this GGPK uses.
    /// </summary>
    /// <remarks>
    /// Version 4 - the Mac build - writes names as UTF-32; everything else uses UTF-16. Guessing
    /// one reads every name as gibberish, so the version is kept from the header for this.
    /// </remarks>
    private string ReadName(BinaryReader reader, int length)
    {
        int wide = _version == 4 ? 4 : 2;
        byte[] bytes = reader.ReadBytes(length * wide);
        reader.BaseStream.Position += wide;   // the null terminator

        return _version == 4
            ? Encoding.UTF32.GetString(bytes)
            : Encoding.Unicode.GetString(bytes);
    }

    private void SkipName(BinaryReader reader, int length)
        => reader.BaseStream.Position += (length + 1) * (_version == 4 ? 4 : 2);
}
