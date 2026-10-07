namespace PoEformance.Game.World;

/// <summary>One piece the game laid: a tile file put down once, how, and the area tiles it covers.</summary>
/// <param name="Id">The tile file's id - see <see cref="TerrainTiles.Paths"/>.</param>
/// <param name="Placement">How it was laid, 0 to 7 - see TileOrientation.Placement - or -1 where not known.</param>
/// <param name="MinX">Its lowest area tile across.</param>
/// <param name="MinY">Its lowest area tile down.</param>
/// <param name="MaxX">Its highest area tile across.</param>
/// <param name="MaxY">Its highest area tile down.</param>
/// <param name="Whole">Whether it covers exactly the tiles its file is made of, laid that way round.</param>
public readonly record struct TilePiece(int Id, int Placement, int MinX, int MinY, int MaxX, int MaxY, bool Whole)
{
    /// <summary>Area tiles across.</summary>
    public int Width => MaxX - MinX + 1;

    /// <summary>Area tiles down.</summary>
    public int Height => MaxY - MinY + 1;
}

/// <summary>
/// Every tile of the area as the game laid it: which file, which piece of that file, and which way round.
/// </summary>
/// <remarks>
/// WHAT THE TILE PASS ALREADY READ, kept: the terrain reader looks every tile's file up to group the
/// rooms, and the room search needs the same answer per tile - which definition lies where - to
/// check a room's slots against what was actually put down. A path id per tile rather than a string,
/// for the reason the rooms use one: an area is tens of thousands of tiles built from a few hundred
/// files. Immutable once built, like the grid it rides on.
/// </remarks>
public sealed class TerrainTiles
{
    private readonly int[] _ids;
    private readonly byte[] _subX;
    private readonly byte[] _subY;
    private readonly sbyte[] _placements;

    /// <param name="paths">The distinct tile files, by id.</param>
    /// <param name="ids">A path id per tile, row by row, -1 where the tile names none.</param>
    /// <param name="subX">Each tile's own column within its file's template.</param>
    /// <param name="subY">Each tile's own row within it.</param>
    /// <param name="placements">How each was laid, 0 to 7 - see TileOrientation.Placement - or -1.</param>
    /// <param name="width">Tiles across.</param>
    /// <param name="height">Tiles down.</param>
    public TerrainTiles(IReadOnlyList<string> paths, int[] ids, byte[] subX, byte[] subY, sbyte[] placements, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(subX);
        ArgumentNullException.ThrowIfNull(subY);
        ArgumentNullException.ThrowIfNull(placements);
        long count = (long)Math.Max(0, width) * Math.Max(0, height);
        if (ids.LongLength < count || subX.LongLength < count || subY.LongLength < count || placements.LongLength < count)
        {
            throw new ArgumentException("every per-tile array must cover the area");
        }

        Paths = paths;
        _ids = ids;
        _subX = subX;
        _subY = subY;
        _placements = placements;
        Width = width;
        Height = height;
    }

    /// <summary>The distinct tile files, by id.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Tiles across.</summary>
    public int Width { get; }

    /// <summary>Tiles down.</summary>
    public int Height { get; }

    /// <summary>The path ids row by row, for a scan that has already kept itself inside the area - see RoomPlacements.</summary>
    internal int[] Ids => _ids;

    /// <summary>The path id of the tile at a column and row, or -1 outside the area or where it names none.</summary>
    public int IdAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? _ids[(y * Width) + x] : -1;

    /// <summary>The file of the tile at a column and row, or empty.</summary>
    public string PathAt(int x, int y) => IdAt(x, y) is var id and >= 0 && id < Paths.Count ? Paths[id] : string.Empty;

    /// <summary>The tile's own place within its file's template, or (-1, -1) outside the area.</summary>
    public (int X, int Y) SubAt(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height ? (_subX[(y * Width) + x], _subY[(y * Width) + x]) : (-1, -1);

    /// <summary>How the tile at a column and row was laid, 0 to 7, or -1.</summary>
    public int PlacementAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? _placements[(y * Width) + x] : -1;

    /// <summary>
    /// The pieces laid over a block of the area, each once - a file several tiles across counted as one piece, not one per tile.
    /// </summary>
    /// <param name="x">The block's lowest tile across.</param>
    /// <param name="y">The block's lowest tile down.</param>
    /// <param name="width">Tiles across.</param>
    /// <param name="height">Tiles down.</param>
    /// <param name="size">A tile file's size in tiles as its file has it, by id - one by one where not known.</param>
    /// <remarks>
    /// A PIECE IS FOUND BY ITS SUB-TILES, NOT BY COUNTING THEM: each tile of a piece names its own
    /// place in the file's template, so a piece is the run of side-by-side tiles of one file, laid one
    /// way, that never names the same place twice - whatever frame the game counts those places in,
    /// which nothing here needs to know. It may reach past the block, a piece being laid whole. One
    /// that does not come out the file's size, that way round, is said not whole.
    /// </remarks>
    public List<TilePiece> Pieces(int x, int y, int width, int height, Func<int, (int Width, int Height)> size)
    {
        ArgumentNullException.ThrowIfNull(size);
        var pieces = new List<TilePiece>();
        var taken = new HashSet<int>();
        var group = new List<int>();
        var named = new HashSet<int>();
        var next = new Queue<int>();
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            for (int column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            {
                int at = (row * Width) + column;
                int id = _ids[at];
                if (id < 0 || !taken.Add(at))
                {
                    continue;
                }

                int placement = _placements[at];
                (int wide, int tall) = size(id);
                int cells = Math.Max(1, wide) * Math.Max(1, tall);
                if (cells == 1)
                {
                    pieces.Add(new TilePiece(id, placement, column, row, column, row, Whole: true));
                    continue;
                }

                group.Clear();
                named.Clear();
                next.Clear();
                group.Add(at);
                named.Add(Named(at));
                next.Enqueue(at);
                while (next.Count > 0 && group.Count < cells)
                {
                    int from = next.Dequeue();
                    int fx = from % Width;
                    int fy = from / Width;
                    foreach ((int bx, int by) in (ReadOnlySpan<(int, int)>)[(fx - 1, fy), (fx + 1, fy), (fx, fy - 1), (fx, fy + 1)])
                    {
                        if ((uint)bx >= (uint)Width || (uint)by >= (uint)Height || group.Count >= cells)
                        {
                            continue;
                        }

                        int beside = (by * Width) + bx;
                        if (_ids[beside] == id && _placements[beside] == placement && !taken.Contains(beside) && named.Add(Named(beside)))
                        {
                            taken.Add(beside);
                            group.Add(beside);
                            next.Enqueue(beside);
                        }
                    }
                }

                int minX = column, maxX = column, minY = row, maxY = row;
                foreach (int one in group)
                {
                    minX = Math.Min(minX, one % Width);
                    maxX = Math.Max(maxX, one % Width);
                    minY = Math.Min(minY, one / Width);
                    maxY = Math.Max(maxY, one / Width);
                }

                // LAID A QUARTER ROUND, a piece's columns run down the area: placements 4 to 7 swap the axes.
                bool swapped = placement >= 4;
                (int across, int down) = swapped ? (tall, wide) : (wide, tall);
                bool whole = group.Count == cells && maxX - minX + 1 == across && maxY - minY + 1 == down;
                pieces.Add(new TilePiece(id, placement, minX, minY, maxX, maxY, whole));
            }
        }

        return pieces;
    }

    /// <summary>A tile's place in its file's template as one number.</summary>
    private int Named(int at) => (_subY[at] << 8) | _subX[at];
}
