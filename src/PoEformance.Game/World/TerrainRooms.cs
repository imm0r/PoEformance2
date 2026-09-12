namespace PoEformance.Game.World;

/// <summary>
/// One room of the area's layout: the block of tiles the game built from a single file.
/// </summary>
/// <param name="Id">
/// A synthetic identity, since a room is not an entity and has no address. The top bit is set
/// for the same reason <see cref="TerrainLandmark"/> sets it - routes are keyed by one number
/// and no real pointer reaches that high, so a room can never be mistaken for an entity.
/// </param>
/// <param name="Path">The room's own file, as the game stores it on every tile it covers.</param>
/// <param name="Name">Its file name without the extension - what the label shows.</param>
/// <param name="Tiles">How many tiles it covers. The one number that separates a room from a speck.</param>
/// <param name="GridX">Centre of the block in grid cells, which is what the map projects.</param>
/// <param name="WalkableTiles">
/// How many of those tiles hold ground that can be walked on.
///
/// ZERO MEANS SCENERY, and that is the number that makes the names usable. An area's tile grid
/// is a full rectangle while the walkable grid is a subset of it, so most of what the game
/// builds is there to be looked at rather than entered - the buildings along the road, the sea
/// beside them, the wall behind the fence. Those rooms have names like any other
/// (Building_Fill_03, TropicalCoast_Fill_01, BuildingWall_Cv_06) and are most of the labels on
/// the map. Size cannot tell them apart from real rooms, because a scenery block is large.
/// </param>
/// <param name="Placements">
/// How many separate rooms in this area were built from the same file.
///
/// WHAT SEPARATES A PLACE FROM A BUILDING BLOCK, and it is the lesson
/// <see cref="TerrainLandmarks"/> already learned about tiles: a name that turns up in eighty
/// places is a wall module, and labelling all eighty buries the one room worth reading. A name
/// that turns up once or twice is a place - exit_01, an arena, the room a quest object sits in.
/// Size cannot say this either way; an area is built from one module repeated, so nearly every
/// room is the same handful of tiles across.
/// </param>
public sealed record TerrainRoom(
    ulong Id,
    string Path,
    string Name,
    int MinTileX,
    int MinTileY,
    int MaxTileX,
    int MaxTileY,
    int Tiles,
    float GridX,
    float GridY,
    int WalkableTiles = 0,
    int Placements = 1)
{
    /// <summary>True when there is ground in this room somebody could stand on.</summary>
    public bool IsWalkable => WalkableTiles > 0;

    /// <summary>
    /// How a chosen room is written down, so the choice survives leaving the area.
    /// </summary>
    /// <remarks>
    /// The PATH and the room's corner rather than <see cref="Id"/>: a hash is not something a
    /// person can read in their settings file, and the two facts it is made of are exactly
    /// what identifies the room again. Campaign layouts are fixed, so this finds the same room
    /// on every visit; an endgame map is generated per instance, so there it is only good for
    /// as long as that instance lasts - which is all a pick in a map can be.
    /// </remarks>
    public string Key => $"{Path}@{MinTileX},{MinTileY}";
}

/// <summary>
/// Groups the area's tiles into the rooms they were placed as.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. The game builds an area out of room files, and every tile carries the
/// name of the one it belongs to - "overlay_bridge_03", "3open_01", "exit_01". That is the
/// layout in words rather than in walls, and it is known the moment the area loads: which end
/// of the map has the exit, where the bridge is, which blob is the arena. The terrain outline
/// draws the same information as a shape and cannot say what any part of it IS.
///
/// A ROOM IS A CONNECTED BLOCK OF TILES SHARING ONE FILE, and it is found by flood fill rather
/// than by clustering the way <see cref="TerrainLandmarks"/> does. The difference is not a
/// preference: clustering compares tiles pairwise, which is fine for the handful of arena
/// tiles it runs on and quadratic on an area's full tile list. A flood fill over the tile grid
/// is one pass whatever the area's size, and the grid is already in hand.
///
/// The one thing it cannot separate is two placements of the SAME room file that happen to
/// touch - they come out as a single room with twice the tiles. Each tile does carry its
/// sub-ids within its template, which would tell the two apart, but the price is a second
/// pass over data whose only symptom is a merged label on a repeated piece of scenery.
/// </remarks>
public static class TerrainRooms
{
    /// <summary>Grid cells per tile - a tile is 250 world units.</summary>
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>
    /// Rooms kept before the rest are dropped.
    /// </summary>
    /// <remarks>
    /// A guard against a read that went wrong rather than a view about how many rooms an area
    /// has: a garbled tile array turns every tile into its own room, and the cost of that is
    /// paid in the frame that draws them.
    /// </remarks>
    public const int MaxRooms = 8192;

    /// <summary>
    /// The rooms in an area, from one path id per tile.
    /// </summary>
    /// <param name="paths">The distinct room files, indexed by the ids below.</param>
    /// <param name="tilePath">
    /// A path id per tile, row by row, or -1 where the tile names no file. Read only.
    /// </param>
    /// <param name="tileWalkable">
    /// Whether a tile holds ground that can be walked on, by tile coordinates.
    ///
    /// Optional, and what it decides is <see cref="TerrainRoom.WalkableTiles"/>. NOT SUPPLYING
    /// IT COUNTS EVERY TILE AS WALKABLE rather than none: no opinion has to mean "no filter",
    /// or a caller that cannot answer the question - a test, a page drawing the layout from
    /// outside the game - would silently get an empty map.
    /// </param>
    public static List<TerrainRoom> Find(
        IReadOnlyList<string> paths, int[] tilePath, int tilesX, int tilesY,
        Func<int, int, bool>? tileWalkable = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tilePath);

        var rooms = new List<TerrainRoom>();
        long count = (long)tilesX * tilesY;
        if (tilesX <= 0 || tilesY <= 0 || count > tilePath.Length)
        {
            return rooms;
        }

        var taken = new bool[count];

        // The frontier, as an array rather than a Stack<int>: a room can be the whole area, so
        // this is sized for the worst case once instead of growing through it.
        var frontier = new int[count];

        for (int start = 0; start < count; start++)
        {
            int id = tilePath[start];
            if (id < 0 || id >= paths.Count || taken[start])
            {
                continue;
            }

            int top = 0;
            frontier[top++] = start;
            taken[start] = true;

            int tiles = 0;
            int walkable = 0;
            long sumX = 0;
            long sumY = 0;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;

            while (top > 0)
            {
                int at = frontier[--top];
                int y = at / tilesX;
                int x = at - (y * tilesX);

                tiles++;
                sumX += x;
                sumY += y;

                // Counted per tile as the fill visits it, which is the only pass that knows
                // which tiles this room owns - and every tile is visited exactly once.
                if (tileWalkable is null || tileWalkable(x, y))
                {
                    walkable++;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                if (x > 0 && Same(tilePath, taken, at - 1, id))
                {
                    taken[at - 1] = true;
                    frontier[top++] = at - 1;
                }

                if (x + 1 < tilesX && Same(tilePath, taken, at + 1, id))
                {
                    taken[at + 1] = true;
                    frontier[top++] = at + 1;
                }

                if (y > 0 && Same(tilePath, taken, at - tilesX, id))
                {
                    taken[at - tilesX] = true;
                    frontier[top++] = at - tilesX;
                }

                if (y + 1 < tilesY && Same(tilePath, taken, at + tilesX, id))
                {
                    taken[at + tilesX] = true;
                    frontier[top++] = at + tilesX;
                }
            }

            string path = paths[id];
            rooms.Add(new TerrainRoom(
                IdFor(path, minX, minY),
                path,
                NameFor(path),
                minX,
                minY,
                maxX,
                maxY,
                tiles,
                Centre(sumX / (double)tiles),
                Centre(sumY / (double)tiles),
                walkable));

            if (rooms.Count >= MaxRooms)
            {
                break;
            }
        }

        return Ranked(rooms);
    }

    /// <summary>
    /// Fills in how often each file was placed, and puts the rooms worth reading first.
    /// </summary>
    /// <remarks>
    /// A SECOND PASS because the answer is not known until the first one has finished: how many
    /// times a file was placed is a fact about the whole area, and the fill meets its rooms one
    /// at a time.
    ///
    /// The ORDER is the other half, and it exists for what draws these. Labels are packed
    /// against each other - a name that would land on one already written is dropped - so the
    /// order they are offered in decides which of two overlapping names survives. Rarest first,
    /// then largest: a room whose file was placed once says more than one of eighty identical
    /// wall modules, whatever their sizes, and among equals the bigger room is the one somebody
    /// can see they are standing in.
    /// </remarks>
    private static List<TerrainRoom> Ranked(List<TerrainRoom> rooms)
    {
        var placements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TerrainRoom room in rooms)
        {
            placements[room.Path] = placements.GetValueOrDefault(room.Path) + 1;
        }

        for (int i = 0; i < rooms.Count; i++)
        {
            rooms[i] = rooms[i] with { Placements = placements[rooms[i].Path] };
        }

        rooms.Sort(static (left, right) => left.Placements != right.Placements
            ? left.Placements.CompareTo(right.Placements)
            : right.Tiles.CompareTo(left.Tiles));

        return rooms;
    }

    /// <summary>The same, from tile records - which is how a test says what it means.</summary>
    public static List<TerrainRoom> Find(
        IReadOnlyList<TerrainTile> tiles, int tilesX, int tilesY,
        Func<int, int, bool>? tileWalkable = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        if (tilesX <= 0 || tilesY <= 0)
        {
            return [];
        }

        var paths = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var tilePath = new int[tilesX * tilesY];
        Array.Fill(tilePath, -1);

        foreach (TerrainTile tile in tiles)
        {
            if (tile.Path.Length == 0
                || tile.Column < 0 || tile.Column >= tilesX
                || tile.Row < 0 || tile.Row >= tilesY)
            {
                continue;
            }

            if (!ids.TryGetValue(tile.Path, out int id))
            {
                id = paths.Count;
                paths.Add(tile.Path);
                ids[tile.Path] = id;
            }

            tilePath[(tile.Row * tilesX) + tile.Column] = id;
        }

        return Find(paths, tilePath, tilesX, tilesY, tileWalkable);
    }

    /// <summary>
    /// The room file's own name, extension and directories dropped.
    /// </summary>
    /// <remarks>
    /// Left exactly as the game spells it, unlike <see cref="TerrainLandmarks.NameFor"/> which
    /// tidies a tile name into something readable. A landmark's label stands for an encounter
    /// and "Boss Arena" says it better than "BossArena_01"; a room's label IS the file, and
    /// prettifying it would break the one thing it is good for - reading it back against the
    /// game's own data.
    /// </remarks>
    public static string NameFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int slash = path.LastIndexOf('/');
        string last = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;

        int dot = last.LastIndexOf('.');
        return dot > 0 ? last[..dot] : last;
    }

    /// <summary>
    /// A stable identity for a room, from the place rather than from a counter.
    /// </summary>
    /// <remarks>
    /// So a route to a room stays a route to it when the area is read again. The top bit is
    /// set because entity addresses are heap pointers far below it - see
    /// <see cref="TerrainRoom.Id"/>.
    /// </remarks>
    public static ulong IdFor(string path, int minTileX, int minTileY)
    {
        ArgumentNullException.ThrowIfNull(path);

        ulong hash = 1469598103934665603UL;
        foreach (char c in path)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 1099511628211UL;
        }

        hash = (hash ^ (uint)minTileX) * 1099511628211UL;
        hash = (hash ^ (uint)minTileY) * 1099511628211UL;
        return hash | 0x8000_0000_0000_0000UL;
    }

    private static bool Same(int[] tilePath, bool[] taken, int at, int id)
        => !taken[at] && tilePath[at] == id;

    /// <summary>
    /// The grid cell at the CENTRE of a block of tiles, given the mean of their indices.
    /// </summary>
    /// <remarks>
    /// The half-tile is not decoration. A tile is 23 cells - 250 world units - across, and
    /// anchoring on the mean INDEX puts the label half a tile toward the map's origin, which
    /// is the offset the AHK tool shipped and then had to correct. Worth knowing when
    /// comparing against the reference: GameHelper2 reports a room's centroid on the corner
    /// convention, so its number is 11.5 cells short of this one on both axes.
    /// </remarks>
    private static float Centre(double meanTileIndex) => (float)((meanTileIndex + 0.5) * Cells);
}
