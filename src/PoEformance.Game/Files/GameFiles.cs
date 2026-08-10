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
    /// What opening an install came to - and when it did not, how far it got.
    /// </summary>
    /// <param name="Files">The opened install, or null.</param>
    /// <param name="Why">What happened, in words, whether or not it worked.</param>
    /// <remarks>
    /// FOUR STACKED FORMATS FAIL IN FOUR DIFFERENT WAYS and used to be reported as one sentence:
    /// "found the game but could not read its packed files". That is true of a folder with no
    /// bundles in it, of an archive version this does not understand, of an index that will not
    /// decompress, and of one that decompresses into something that is not an index - and there
    /// is nothing anybody can do with it, including the person who wrote it. Which layer it was
    /// is the whole of the diagnosis, and it is free to say.
    /// </remarks>
    public readonly record struct OpenedFiles(GameFiles? Files, string Why);

    /// <summary>
    /// Opens an install, or returns null when that folder does not hold one.
    /// </summary>
    /// <param name="gameFolder">The folder holding <c>Bundles2</c> or <c>Content.ggpk</c>.</param>
    /// <param name="decompress">How to undo Oodle. Defaults to the one that ships with this.</param>
    public static GameFiles? Open(string? gameFolder, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
        => OpenOrSay(gameFolder, decompress).Files;

    /// <summary>The same, and says which layer gave up when one did.</summary>
    public static OpenedFiles OpenOrSay(
        string? gameFolder, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
    {
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            return new OpenedFiles(null, "no game folder to look in");
        }

        // THE INSTALL'S OWN OODLE, when it has one. The game cannot run without that library,
        // so it is always beside the bundles it packed - and a decoder that came with the data
        // decodes it by construction, where a reimplementation only usually does.
        Oodle.Unpacker unpacker = Oodle.For(gameFolder);
        Func<ReadOnlyMemory<byte>, int, byte[]?> undo = decompress ?? unpacker.Decompress;
        string decoder = decompress is null ? unpacker.Which : "a decoder handed in";

        // The loose folder first, because checking for it is one call and it is what a Steam
        // install has. Only a standalone install has the container.
        var loose = new LooseArchive(gameFolder);
        if (loose.Ready)
        {
            return Said(OpenOrSay(loose, undo), decoder);
        }

        string container = Path.Combine(gameFolder, "Content.ggpk");
        if (GgpkArchive.Open(container) is { } ggpk)
        {
            return Said(OpenOrSay(ggpk, undo), decoder);
        }

        return new OpenedFiles(
            null,
            File.Exists(container)
                ? $"{container} is there but did not open as a GGPK - a version this does not understand"
                : $@"no Bundles2\_.index.bin and no Content.ggpk in {gameFolder}");
    }

    /// <summary>Adds which decoder was used to whatever the open came to.</summary>
    private static OpenedFiles Said(OpenedFiles opened, string decoder)
        => opened with { Why = $"{opened.Why} [{decoder}]" };

    /// <summary>Opens one from an archive that is already sorted out.</summary>
    public static GameFiles? Open(IGameArchive? archive, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
        => OpenOrSay(archive, decompress).Files;

    /// <summary>The same, and says which layer gave up when one did.</summary>
    public static OpenedFiles OpenOrSay(
        IGameArchive? archive, Func<ReadOnlyMemory<byte>, int, byte[]?>? decompress = null)
    {
        if (archive is not { Ready: true })
        {
            return new OpenedFiles(null, "the archive did not open");
        }

        Func<ReadOnlyMemory<byte>, int, byte[]?> undo = decompress ?? Oodle.Decompress;
        string where = archive.Describe;

        byte[]? raw = archive.Read("_.index.bin");
        if (raw is not { Length: > 0 })
        {
            return new OpenedFiles(null, $"{where}: _.index.bin is not in there");
        }

        // The index is itself a bundle, so it is unpacked the same way as everything else -
        // read whole, because it is one file and every lookup wants all of it.
        BundleFile? packed = BundleFile.Open(raw);
        if (packed is null)
        {
            return new OpenedFiles(
                null, $"{where}: _.index.bin is {raw.Length} bytes but its bundle header did not read");
        }

        byte[]? content = packed.Read(undo);
        if (content is null)
        {
            // THE ONE LAYER NOTHING HERE CAN TEST without a real install: there is no Oodle
            // compressor to build a fixture with. So this message is the first news that the
            // shipped decoder does not handle what this install packs with.
            return new OpenedFiles(
                null,
                $"{where}: _.index.bin will not decompress - {packed.Chunks} chunks, "
                + $"{packed.Compressed} bytes in, {packed.Uncompressed} expected out. "
                + $"The decoder said: {(Oodle.LastRefusal.Length > 0 ? Oodle.LastRefusal : "nothing")}");
        }

        BundleIndex.Parsed read = BundleIndex.Read(content);
        return read.Index is null
            ? new OpenedFiles(null, $"{where}: the index decompressed to {content.Length} bytes, and then: {read.Why}")
            : new OpenedFiles(new GameFiles(archive, read.Index, undo), $"{archive.Describe} - {read.Why}");
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
