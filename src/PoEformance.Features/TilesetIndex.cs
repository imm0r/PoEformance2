using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Which tilesets place which tile - the link nothing in a tile points along.
/// </summary>
/// <remarks>
/// A TILE DOES NOT KNOW ITS AREAS, and what it looks like depends on them: a tileset's
/// MaterialsList says what its ground is and its TileMaterialOverrides swap the tile's own
/// materials - every desert tileset that places <c>cliffcvm_stroma1</c> swaps all nine of its cliff
/// and ledge materials. So the link is built backwards, once: every tileset's tile list read, its
/// includes followed, and each tile it names filed against it.
///
/// BY THE WHOLE PATH, case aside. A tile of the same FILE NAME in another folder is another tile -
/// the hive and the swarm place <c>Desert/AntNest/Stromatolite/CliffCvM_Stroma1.tdt</c>, not the
/// desert's - and is kept apart in <see cref="Alike"/> so the difference can be shown rather than
/// blurred.
/// </remarks>
public sealed class TilesetIndex
{
    /// <summary>How deep includes are followed - a guard against a list that includes itself through another.</summary>
    private const int MostIncludes = 8;

    /// <summary>Nothing indexed.</summary>
    public static TilesetIndex Empty { get; } = new(new Dictionary<string, List<string>>(), new Dictionary<string, List<string>>(), 0);

    private readonly Dictionary<string, List<string>> _byTile;
    private readonly Dictionary<string, List<string>> _byName;

    private TilesetIndex(Dictionary<string, List<string>> byTile, Dictionary<string, List<string>> byName, int searched)
    {
        _byTile = byTile;
        _byName = byName;
        Searched = searched;
    }

    /// <summary>How many tilesets were read.</summary>
    public int Searched { get; }

    /// <summary>The tilesets whose tile list names this tile, in the order they were handed in.</summary>
    public IReadOnlyList<string> Of(string? tile)
        => tile is not null && _byTile.TryGetValue(Key(tile), out List<string>? sets) ? sets : [];

    /// <summary>Tiles of the same file name in another folder, each with the tilesets that place it.</summary>
    public IReadOnlyList<(string Tile, IReadOnlyList<string> Tilesets)> Alike(string? tile)
    {
        if (tile is null)
        {
            return [];
        }

        string key = Key(tile);
        if (!_byName.TryGetValue(Name(key), out List<string>? same))
        {
            return [];
        }

        var alike = new List<(string, IReadOnlyList<string>)>();
        foreach (string other in same)
        {
            if (!string.Equals(other, key, StringComparison.Ordinal))
            {
                alike.Add((other, _byTile[other]));
            }
        }

        return alike;
    }

    /// <summary>
    /// Reads every tileset's tile list and files each tile against it. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="tilesets">Every <c>.tsi</c> the install has.</param>
    public static TilesetIndex Build(Func<string, byte[]?>? read, IReadOnlyList<string>? tilesets)
    {
        if (read is null || tilesets is not { Count: > 0 })
        {
            return Empty;
        }

        var byTile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var searched = 0;
        foreach (string tsi in tilesets)
        {
            string? text = Text(read, tsi);
            if (text is null || TilesetFile.Value(text, "TileSet") is not { Length: > 0 } set)
            {
                continue;
            }

            searched++;
            foreach (string tile in Tiles(read, TilesetFile.Beside(tsi, set), lists, 0))
            {
                if (!byTile.TryGetValue(tile, out List<string>? sets))
                {
                    sets = [];
                    byTile[tile] = sets;
                }

                // ONCE PER TILESET however many of its lists name the tile.
                if (sets.Count == 0 || !ReferenceEquals(sets[^1], tsi))
                {
                    sets.Add(tsi);
                }
            }
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string tile in byTile.Keys)
        {
            string name = Name(tile);
            if (!byName.TryGetValue(name, out List<string>? same))
            {
                same = [];
                byName[name] = same;
            }

            same.Add(tile);
        }

        return new TilesetIndex(byTile, byName, searched);
    }

    /// <summary>
    /// The materials a tileset swaps on the tiles it places, by the material swapped. Empty where it swaps none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Overrides(Func<string, byte[]?>? read, string? tsi)
    {
        if (read is null || string.IsNullOrWhiteSpace(tsi)
            || TilesetFile.Value(Text(read, tsi), "TileMaterialOverrides") is not { Length: > 0 } named)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialOverride swap in MaterialOverrides.Read(read(Slashed(TilesetFile.Beside(tsi, named)))))
        {
            // THE FIRST LINE FOR A MATERIAL STANDS - the lists seen never repeat one, and a later line
            // overriding an earlier one would be a rule this has no evidence for.
            swaps.TryAdd(Slashed(swap.From), Slashed(swap.To));
        }

        return swaps;
    }

    /// <summary>A tileset's path without the parts every one shares: <c>maps/swarm</c> for <c>metadata/terrain/maps/swarm/master.tsi</c>.</summary>
    public static string Short(string tsi)
    {
        ArgumentNullException.ThrowIfNull(tsi);
        string path = Slashed(tsi);
        if (path.StartsWith("metadata/terrain/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["metadata/terrain/".Length..];
        }

        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : path;
    }

    /// <summary>Every tile a list names, its includes' tiles among them, as keys - memoised per list.</summary>
    private static List<string> Tiles(Func<string, byte[]?> read, string tst, Dictionary<string, List<string>> lists, int depth)
    {
        if (lists.TryGetValue(tst, out List<string>? known))
        {
            return known;
        }

        // SEEN BEFORE IT IS READ, so a list that comes back round to itself finds itself empty
        // rather than reading forever.
        var tiles = new List<string>();
        lists[tst] = tiles;
        TileList list = TileList.Read(read(Slashed(tst)));
        foreach (string tile in list.Tiles)
        {
            tiles.Add(Key(tile));
        }

        if (depth < MostIncludes)
        {
            foreach (string include in list.Includes)
            {
                tiles.AddRange(Tiles(read, TilesetFile.Beside(tst, include), lists, depth + 1));
            }
        }

        return tiles;
    }

    private static string? Text(Func<string, byte[]?> read, string path)
        => read(Slashed(path)) is { Length: > 0 } bytes ? StatDescriptionFiles.Decode(bytes) : null;

    private static string Key(string tile) => Slashed(tile).ToLowerInvariant();

    private static string Name(string key) => key[(key.LastIndexOf('/') + 1)..];

    private static string Slashed(string path) => path.Replace('\\', '/').Trim();
}

/// <summary>
/// A <see cref="TilesetIndex"/> built once, off the frame, when first asked for.
/// </summary>
/// <remarks>
/// THE LIST OF TILESETS ARRIVES LATE - a background walk of the install sets it - so the build
/// starts on the first ask after it is there, and starts again only if a new list replaces it.
/// The frame asks <see cref="Ready"/> and never waits; a dump, already off the frame, may <see cref="Wait"/>.
/// </remarks>
public sealed class TilesetCatalog(Func<string, byte[]?>? read, Func<IReadOnlyList<string>> tilesets)
{
    private readonly Lock _gate = new();
    private Task<TilesetIndex>? _building;
    private IReadOnlyList<string>? _from;

    /// <summary>The index if it is built, starting the build if it is not; null until then.</summary>
    public TilesetIndex? Ready => Start() is { IsCompletedSuccessfully: true } built ? built.Result : null;

    /// <summary>Whether a build is under way.</summary>
    public bool Reading => Start() is { IsCompleted: false };

    /// <summary>The index, waiting for the build - off the frame only. Empty where there is nothing to build from.</summary>
    public TilesetIndex Wait() => Start() is { } building ? building.GetAwaiter().GetResult() : TilesetIndex.Empty;

    private Task<TilesetIndex>? Start()
    {
        IReadOnlyList<string> sets = tilesets();
        if (read is null || sets.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_building is null || !ReferenceEquals(_from, sets))
            {
                _from = sets;
                _building = Task.Run(() => TilesetIndex.Build(read, sets));
            }

            return _building;
        }
    }
}
