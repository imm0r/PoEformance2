namespace PoEformance.Game.Files;

/// <summary>
/// The game's own files, opened from an installed copy.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. An item in memory carries the PATH of its picture and not the picture:
/// <c>Art/2DItems/Weapons/Bows/Bow1.dds</c>. That file is in the install, so a stash can be
/// drawn with the game's own art without asking anybody for it - nothing leaves the machine,
/// nothing is out of date, and it works while offline.
///
/// FOUR LAYERS, AND EACH ONE IS SOMEBODY ELSE'S FORMAT. An install keeps its files either loose
/// under <c>Bundles2</c> or inside one <c>Content.ggpk</c> (<see cref="IGameArchive"/>); either
/// way what is in there is bundles (<see cref="BundleFile"/>) plus one index naming what is in
/// which (<see cref="BundleIndex"/>); and the bundles are Oodle-compressed. This is the piece
/// that puts those together and answers "give me this path".
///
/// IT OPENS NOTHING BY ITSELF. <see cref="Open"/> is called once, off the reader thread, and
/// takes a moment: the index decompresses to some tens of megabytes. After that a file is a
/// dictionary lookup and one 256 KB chunk.
/// </remarks>
public sealed class GameFiles
{
    /// <summary>How many bundles' chunk tables are kept open at once.</summary>
    /// <remarks>
    /// Small on purpose. A table is only a few thousand numbers, but art paths cluster into a
    /// handful of bundles, so a handful is all that is ever wanted - and remembering every
    /// bundle an unusual read touched is how a small cache turns into a large one.
    /// </remarks>
    public const int RememberedBundles = 8;

    private readonly IGameArchive _archive;
    private readonly Func<ReadOnlyMemory<byte>, int, byte[]?> _decompress;
    private readonly Dictionary<string, BundleFile> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    private GameFiles(IGameArchive archive, BundleIndex index, Func<ReadOnlyMemory<byte>, int, byte[]?> decompress)
    {
        _archive = archive;
        _decompress = decompress;
        Index = index;
    }

    /// <summary>What is in which bundle.</summary>
    public BundleIndex Index { get; }

    /// <summary>What was opened, for a window to show.</summary>
    public string Describe => $"{_archive.Describe} - {Index.Count} files, {Index.Bundles.Count} bundles, {Index.Hashing}";

    /// <summary>
    /// Opens an install, or returns null when that folder does not hold one.
    /// </summary>
    /// <param name="gameFolder">The folder holding <c>Bundles2</c> or <c>Content.ggpk</c>.</param>
    /// <param name="decompress">How to undo Oodle. Defaults to the one that ships with this.</param>
    public static GameFiles? Open(string? gameFolder, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
    {
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            return null;
        }

        // The loose folder first, because checking for it is one call and it is what a Steam
        // install has. Only a standalone install has the container.
        IGameArchive? archive = new LooseArchive(gameFolder) is { Ready: true } loose
            ? loose
            : GgpkArchive.Open(Path.Combine(gameFolder, "Content.ggpk"));

        return archive is null ? null : Open(archive, decompress);
    }

    /// <summary>Opens one from an archive that is already sorted out.</summary>
    public static GameFiles? Open(IGameArchive? archive, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
    {
        if (archive is not { Ready: true })
        {
            return null;
        }

        Func<ReadOnlyMemory<byte>, int, byte[]?> undo = decompress ?? Oodle.Decompress;

        // The index is itself a bundle, so it is unpacked the same way as everything else -
        // read whole, because it is one file and every lookup wants all of it.
        BundleFile? packed = BundleFile.Open(archive.Read("_.index.bin"));
        BundleIndex? index = packed is null ? null : BundleIndex.Parse(packed.Read(undo));

        return index is null ? null : new GameFiles(archive, index, undo);
    }

    /// <summary>
    /// One file out of the install, or null when it is not there.
    /// </summary>
    /// <param name="path">
    /// From the top, in either slash - <c>Art/2DItems/Weapons/Bows/Bow1.dds</c>. Case does not
    /// matter: paths are hashed lowercased.
    /// </param>
    public byte[]? Read(string? path)
    {
        if (Index.Find(path) is not { } spot)
        {
            return null;
        }

        BundleFile? bundle = Bundle(Index.Bundles[spot.Bundle]);
        return bundle?.Read(spot.At, spot.Size, _decompress);
    }

    /// <summary>Whether a path is in the install at all, without unpacking anything.</summary>
    public bool Has(string? path) => Index.Find(path) is not null;

    /// <summary>
    /// A bundle's chunk table, opened once and kept.
    /// </summary>
    /// <remarks>
    /// The header and table are read through the archive's ranged read, so opening a bundle
    /// costs its first few kilobytes rather than its couple of hundred megabytes.
    /// </remarks>
    private BundleFile? Bundle(string name)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(name, out BundleFile? already))
            {
                return already;
            }
        }

        string file = $"{name}.bundle.bin";
        BundleFile? opened = BundleFile.Open((at, length) => _archive.Read(file, at, length));
        if (opened is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_open.TryGetValue(name, out BundleFile? raced))
            {
                return raced;
            }

            _open[name] = opened;
            _order.Enqueue(name);

            while (_order.Count > RememberedBundles)
            {
                _open.Remove(_order.Dequeue());
            }
        }

        return opened;
    }
}
