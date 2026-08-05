namespace PoEformance.Game.World;

/// <summary>One terrain tile: which piece of scenery it is, and where it sits.</summary>
/// <param name="Path">The tile's own file, e.g. <c>Metadata/Terrain/.../BossArena_01.tdt</c>.</param>
/// <param name="Column">Its column in the tile grid; times 23 gives the grid cell.</param>
/// <param name="SubX">The tile's own id within its template - part of the curated key.</param>
/// <param name="Rotation">Decides which way round the sub-ids are written in that key.</param>
public readonly record struct TerrainTile(
    string Path, int Column, int Row, int SubX, int SubY, byte Rotation);

/// <summary>A place found in the TERRAIN rather than among the entities.</summary>
/// <param name="Id">
/// A synthetic identity, since this is not an entity and has no address. The top bit is set,
/// which keeps it out of the range any real pointer can occupy - so routes can be keyed by
/// entity address and landmark alike without the two ever colliding.
/// </param>
public sealed record TerrainLandmark(
    ulong Id, string Path, string Name, PoiKind Kind, int GridX, int GridY, int Tiles);

/// <summary>
/// Finds places in the shape of the ground itself.
/// </summary>
/// <remarks>
/// THE INSIGHT THIS RESTS ON, and it is the owner's: an endgame map is generated at random,
/// but a BOSS ROOM is not. The arena is a fixed piece of layout, so the tiles it is built from
/// keep their names - HagWitchArena, TombBoss_ArenaFloor, PillarArena - whatever the map
/// around them looks like. Nothing in the entity list says where the boss will be before it
/// exists; the floor says it from the moment the area loads.
///
/// Two ways in, and both are needed:
///
/// - CURATED, by exact key: a tile file plus the sub-ids of one particular instance, which is
///   how the AHK tool's landmark data is written. Exact on purpose - a wall tile is reused all
///   over an area, so matching its path alone would put the same label everywhere. This is
///   what supplies real names ("Beira of the Rotten"), and it only covers what someone has
///   written down: five endgame maps, against hundreds that exist.
/// - GENERIC, by keyword: any tile whose file is named for an arena or a boss. Nobody has to
///   curate it, which is the only way this can work in the endgame, and the name it produces
///   is the tile's own rather than the encounter's.
///
/// Tiles are CLUSTERED before they become landmarks. An arena is a block of tiles, so one
/// landmark per tile would stack a dozen markers on one room - which is exactly the fault the
/// AHK tool hit and fixed the same way.
/// </remarks>
public static class TerrainLandmarks
{
    /// <summary>Grid cells per tile - a tile is 250 world units.</summary>
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>Tiles within this distance of each other are one place.</summary>
    private const int ClusterTiles = 3;

    /// <summary>
    /// Clusters of one tile file past which it is scenery, not a landmark.
    /// </summary>
    /// <remarks>
    /// A boss arena appears once or twice in an area. A tile that turns up in six separate
    /// places is a wall or a floor that happens to be named for one, and marking all of them
    /// buries the real thing - the same lesson the AHK tool learned when a reused wall put
    /// "Boss" across the whole map.
    /// </remarks>
    private const int MaxClusters = 4;

    /// <summary>Tile names that mean "something fights you here".</summary>
    private static readonly string[] ArenaWords = ["arena", "boss"];

    /// <summary>
    /// Finds the landmarks in an area's tiles.
    /// </summary>
    /// <param name="curated">
    /// Names for particular tile instances, keyed as the reference writes them:
    /// <c>path + "x:{a}-y:{b}"</c>. Null when nothing is known about this area. Wanted with a
    /// case-insensitive comparer, which is what <see cref="LandmarkNames"/> supplies: the paths
    /// come out of memory and the keys were written by hand, and the two agree on the letters
    /// rather than always on their case.
    /// </param>
    public static List<TerrainLandmark> Find(
        IReadOnlyList<TerrainTile> tiles, IReadOnlyDictionary<string, string>? curated = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var found = new List<TerrainLandmark>();
        var byPath = new Dictionary<string, List<TerrainTile>>(StringComparer.OrdinalIgnoreCase);

        foreach (TerrainTile tile in tiles)
        {
            if (tile.Path.Length == 0)
            {
                continue;
            }

            // A curated hit is exact and names one tile, so it becomes a landmark on the spot
            // rather than going through the clustering - somebody has already decided this is
            // the place and where it is.
            if (curated is not null && curated.TryGetValue(KeyFor(tile), out string? name))
            {
                found.Add(Make(tile.Path, name, KindFor(tile.Path, curated: true), Centre(tile.Column), Centre(tile.Row), 1));
                continue;
            }

            if (LooksLikeArena(tile.Path))
            {
                if (!byPath.TryGetValue(tile.Path, out List<TerrainTile>? group))
                {
                    group = [];
                    byPath[tile.Path] = group;
                }

                group.Add(tile);
            }
        }

        foreach ((string path, List<TerrainTile> group) in byPath)
        {
            List<List<TerrainTile>> clusters = Cluster(group);
            if (clusters.Count > MaxClusters)
            {
                continue;   // reused scenery wearing an arena's name
            }

            foreach (List<TerrainTile> cluster in clusters)
            {
                (int column, int row) = CentreOf(cluster);
                found.Add(Make(path, NameFor(path), PoiKind.BossArena, column, row, cluster.Count));
            }
        }

        return found;
    }

    /// <summary>
    /// The key a curated list writes a tile under.
    /// </summary>
    /// <remarks>
    /// The sub-ids swap on an odd rotation, which is how the tile was placed - GameHelper2
    /// writes the key that way and the AHK tool's data follows it, so reading it any other way
    /// misses every rotated instance.
    /// </remarks>
    public static string KeyFor(TerrainTile tile)
        => tile.Rotation % 2 == 0
            ? $"{tile.Path}x:{tile.SubX}-y:{tile.SubY}"
            : $"{tile.Path}x:{tile.SubY}-y:{tile.SubX}";

    /// <summary>True when a tile's file is named for an arena or a boss.</summary>
    public static bool LooksLikeArena(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (string word in ArenaWords)
        {
            if (path.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tile file's own name, tidied - "BossArena_01" reads as "Boss Arena".</summary>
    public static string NameFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int slash = path.LastIndexOf('/');
        string last = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;

        int dot = last.LastIndexOf('.');
        if (dot > 0)
        {
            last = last[..dot];
        }

        return PointsOfInterest.Readable(last.TrimEnd('_', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));
    }

    /// <summary>Groups tiles that touch into one place.</summary>
    private static List<List<TerrainTile>> Cluster(List<TerrainTile> tiles)
    {
        var clusters = new List<List<TerrainTile>>();
        var taken = new bool[tiles.Count];

        for (int i = 0; i < tiles.Count; i++)
        {
            if (taken[i])
            {
                continue;
            }

            var cluster = new List<TerrainTile> { tiles[i] };
            taken[i] = true;

            // Grown rather than compared pairwise: an arena can be a long shape, and every
            // tile of it belongs even when its ends are further apart than the threshold.
            for (int grown = 0; grown < cluster.Count; grown++)
            {
                for (int j = 0; j < tiles.Count; j++)
                {
                    if (taken[j] || !Touches(cluster[grown], tiles[j]))
                    {
                        continue;
                    }

                    taken[j] = true;
                    cluster.Add(tiles[j]);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static bool Touches(TerrainTile a, TerrainTile b)
        => Math.Abs(a.Column - b.Column) <= ClusterTiles && Math.Abs(a.Row - b.Row) <= ClusterTiles;

    private static (int Column, int Row) CentreOf(List<TerrainTile> cluster)
    {
        int column = 0;
        int row = 0;
        foreach (TerrainTile tile in cluster)
        {
            column += tile.Column;
            row += tile.Row;
        }

        return (Centre(column / cluster.Count), Centre(row / cluster.Count));
    }

    /// <summary>
    /// The grid cell at the CENTRE of a tile, not its corner.
    /// </summary>
    /// <remarks>
    /// A tile is 23 cells across, so anchoring at its corner puts every landmark half a tile -
    /// about 125 world units - toward the map's origin. The AHK tool shipped that offset and
    /// then had to correct it.
    /// </remarks>
    private static int Centre(int tileIndex) => (int)((tileIndex + 0.5f) * Cells);

    private static PoiKind KindFor(string path, bool curated)
        => LooksLikeArena(path) ? PoiKind.BossArena : curated ? PoiKind.Marked : PoiKind.None;

    private static TerrainLandmark Make(string path, string name, PoiKind kind, int gridX, int gridY, int tiles)
        => new(IdFor(gridX, gridY, path), path, name, kind, gridX, gridY, tiles);

    /// <summary>
    /// A stable identity for something that has no address.
    /// </summary>
    /// <remarks>
    /// From the place rather than from a counter, so it survives the area being re-scanned and
    /// a route to it stays a route to it. The top bit is set because entity addresses are heap
    /// pointers far below it: routes are keyed by one number, and this guarantees a landmark
    /// can never be mistaken for an entity.
    /// </remarks>
    private static ulong IdFor(int gridX, int gridY, string path)
    {
        ulong hash = 1469598103934665603UL;
        foreach (char c in path)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 1099511628211UL;
        }

        hash = (hash ^ (uint)gridX) * 1099511628211UL;
        hash = (hash ^ (uint)gridY) * 1099511628211UL;
        return hash | 0x8000_0000_0000_0000UL;
    }
}
