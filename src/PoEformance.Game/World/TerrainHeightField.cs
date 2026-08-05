namespace PoEformance.Game.World;

/// <summary>
/// Where the two engine tables live that say how a terrain tile was placed.
/// </summary>
/// <remarks>
/// Addresses rather than the tables themselves, because they are found by pattern scan at
/// startup and the reader is the thing holding a handle to the process. Unresolved is a
/// normal outcome - a scan can legitimately come up empty after a patch - and it costs the
/// within-tile detail rather than the map.
/// </remarks>
public readonly record struct TerrainRotationTables(ulong Selector, ulong Helper)
{
    public bool IsResolved => Selector != 0 && Helper != 0;
}

/// <summary>
/// Ground height per grid cell: the tile's own height, plus the slope inside it.
/// </summary>
/// <remarks>
/// The height of a point is
///     (TileHeight * TileHeightMultiplier + subTileHeight) * 7.8125 * -1
/// and the two terms answer different questions. The tile term is the level a whole 250-unit
/// tile sits at - it carries a hill. The sub-tile term is the slope WITHIN one tile, which is
/// what a staircase is, and it also carries a large common offset: the tile height is not the
/// ground, it is the level the ground is described relative to.
///
/// That last part is why the tile term alone could not be made to line up. Measured against
/// the player's own tile the common offset cancelled and the map still slid, because the
/// player's own slope went with it; measured against the player's real ground height the
/// sliding stopped and the common offset appeared in full, the same error with its
/// accidental compensation removed. Neither is a smaller approximation of the other - they
/// are the same missing term seen from two sides.
///
/// The sub-tile part is expensive to get at and this is why it is worth it: a pointer per
/// tile to a per-template height array, an index into that array that has been rotated to
/// match how the tile was placed, and an array whose LENGTH is the only thing saying how it
/// is packed. A port of GameHelper2's GetSubTerrainHeight, verified against the AHK tool.
/// </remarks>
public sealed class TerrainHeightField
{
    /// <summary>Grid cells along a tile's edge.</summary>
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>Turns a raw height into world units. The sign is the game's, counting down.</summary>
    public const float HeightScale = -7.8125f;

    // Split on purpose: the formula is linear in the two terms, so the tile part can be
    // scaled once up front and the sub-tile part added to it per cell.
    private readonly float[] _tileBase;
    private readonly byte[] _rotation;
    private readonly int[] _subIndex;
    private readonly byte[][] _subHeights;
    private readonly byte[] _selectorTable;
    private readonly byte[] _helperTable;

    private TerrainHeightField(
        float[] tileBase, int tilesX, int tilesY,
        byte[]? rotation = null, int[]? subIndex = null, byte[][]? subHeights = null,
        byte[]? selectorTable = null, byte[]? helperTable = null)
    {
        _tileBase = tileBase;
        TilesX = tilesX;
        TilesY = tilesY;
        _rotation = rotation ?? [];
        _subIndex = subIndex ?? [];
        _subHeights = subHeights ?? [];
        _selectorTable = selectorTable ?? [];
        _helperTable = helperTable ?? [];
    }

    public int TilesX { get; }

    public int TilesY { get; }

    /// <summary>True when the within-tile slope is included, not just the tile's level.</summary>
    public bool HasSubTile => _subIndex.Length > 0 && _selectorTable.Length >= 9 && _helperTable.Length >= 27;

    /// <summary>Tile levels only - the coarse field, and what a failed sub-tile read leaves.</summary>
    public static TerrainHeightField Tiles(float[] tileHeights, int tilesX, int tilesY)
    {
        ArgumentNullException.ThrowIfNull(tileHeights);
        return new TerrainHeightField(tileHeights, tilesX, tilesY);
    }

    /// <summary>The full field: tile levels plus the slope inside each one.</summary>
    public static TerrainHeightField WithSubTile(
        float[] tileHeights, int tilesX, int tilesY,
        byte[] rotation, int[] subIndex, byte[][] subHeights,
        byte[] selectorTable, byte[] helperTable)
        => new(tileHeights, tilesX, tilesY, rotation, subIndex, subHeights, selectorTable, helperTable);

    /// <summary>Ground height at a grid cell, in the world units entities report.</summary>
    public float HeightAt(int cellX, int cellY)
    {
        if (_tileBase.Length == 0 || TilesX <= 0 || TilesY <= 0)
        {
            return 0f;
        }

        int tx = Math.Clamp(cellX / Cells, 0, TilesX - 1);
        int ty = Math.Clamp(cellY / Cells, 0, TilesY - 1);
        int tile = (ty * TilesX) + tx;
        if ((uint)tile >= (uint)_tileBase.Length)
        {
            return 0f;
        }

        float height = _tileBase[tile];
        if (!HasSubTile || (uint)tile >= (uint)_subIndex.Length)
        {
            return height;
        }

        int which = _subIndex[tile];
        if ((uint)which >= (uint)_subHeights.Length)
        {
            return height;
        }

        int inTileX = Math.Clamp(cellX, 0, (TilesX * Cells) - 1) % Cells;
        int inTileY = Math.Clamp(cellY, 0, (TilesY * Cells) - 1) % Cells;
        int index = RotatedIndex(_rotation[tile], inTileX, inTileY);

        return index < 0 ? height : height + (SubHeight(_subHeights[which], index) * HeightScale);
    }

    /// <summary>
    /// Turns a position inside a tile into an index into that tile's height array.
    /// </summary>
    /// <remarks>
    /// Tiles are placed rotated and mirrored, and the height array is stored once per tile
    /// TEMPLATE, so reading it needs the position transformed back into the template's own
    /// orientation. The two tables that describe the transform are engine data found by
    /// pattern scan, not constants - which is why an unresolved scan leaves the field
    /// tile-only rather than guessing at an orientation.
    /// </remarks>
    private int RotatedIndex(byte selector, int inTileX, int inTileY)
    {
        int rotation = selector < _selectorTable.Length ? _selectorTable[selector] * 3 : 24;
        if (rotation > 24)
        {
            rotation = 24;
        }

        if (rotation + 2 >= _helperTable.Length)
        {
            return -1;
        }

        // The four candidate coordinates: each axis either straight or mirrored.
        Span<int> candidates =
        [
            Cells - inTileX - 1,
            inTileX,
            Cells - inTileY - 1,
            inTileY,
        ];

        int rx0 = _helperTable[rotation];
        int rx1 = _helperTable[rotation + 1];
        int ry0 = _helperTable[rotation + 2];
        int ry1 = rx0 == 0 ? 2 : 0;

        int ix = (rx0 * 2) + rx1;
        int iy = ry0 + ry1;
        if ((uint)ix > 3 || (uint)iy > 3)
        {
            return -1;
        }

        return (candidates[iy] * Cells) + candidates[ix];
    }

    /// <summary>
    /// Reads one height out of a tile template's array.
    /// </summary>
    /// <remarks>
    /// The array's LENGTH says how it is packed, and nothing else does. A tile is 23x23 = 529
    /// cells, so: 67 bytes of one-bit indices plus 2 values is 0x45; 133 bytes of two-bit
    /// indices plus 4 values is 0x89; 265 bytes of four-bit indices plus 16 values is 0x119.
    /// One byte is a tile at a single height, and anything else is a plain signed byte per
    /// cell. Those three numbers falling exactly out of 529 is the check that this is the
    /// right reading of the format rather than a plausible one.
    /// </remarks>
    public static int SubHeight(byte[] array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length == 0 || index < 0)
        {
            return 0;
        }

        if (array.Length == 1)
        {
            return (sbyte)array[0];
        }

        // 1 bit per cell, indexing 2 values.
        if (array.Length == 0x45)
        {
            int at = (index >> 3) + 2;
            return at >= array.Length ? 0 : (sbyte)array[(array[at] >> (index & 7)) & 0x1];
        }

        // 2 bits per cell, indexing 4 values.
        if (array.Length == 0x89)
        {
            int at = (index >> 2) + 4;
            return at >= array.Length ? 0 : (sbyte)array[(array[at] >> ((index & 3) << 1)) & 0x3];
        }

        // 4 bits per cell, indexing 16 values.
        if (array.Length == 0x119)
        {
            int at = (index >> 1) + 16;
            return at >= array.Length ? 0 : (sbyte)array[(array[at] >> ((index & 1) << 2)) & 0xF];
        }

        return index < array.Length ? (sbyte)array[index] : 0;
    }
}
