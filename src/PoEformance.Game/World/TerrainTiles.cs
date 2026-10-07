namespace PoEformance.Game.World;

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

    /// <summary>The path id of the tile at a column and row, or -1 outside the area or where it names none.</summary>
    public int IdAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? _ids[(y * Width) + x] : -1;

    /// <summary>The file of the tile at a column and row, or empty.</summary>
    public string PathAt(int x, int y) => IdAt(x, y) is var id and >= 0 && id < Paths.Count ? Paths[id] : string.Empty;

    /// <summary>The tile's own place within its file's template, or (-1, -1) outside the area.</summary>
    public (int X, int Y) SubAt(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height ? (_subX[(y * Width) + x], _subY[(y * Width) + x]) : (-1, -1);

    /// <summary>How the tile at a column and row was laid, 0 to 7, or -1.</summary>
    public int PlacementAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? _placements[(y * Width) + x] : -1;
}
