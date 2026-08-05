using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// The area's walkability, one bit of information per half-metre of ground.
/// </summary>
/// <remarks>
/// Two cells per byte: even x in the low nibble, odd x in the high one, and a non-zero
/// nibble means walkable. That packing is why <see cref="Width"/> is twice the row stride
/// and why nothing here indexes the raw array directly.
///
/// Immutable once built. An area's terrain does not change while it is loaded, so this is
/// read once and then shared across threads without a lock.
/// </remarks>
public sealed class TerrainGrid
{
    private readonly byte[] _cells;
    private readonly int _bytesPerRow;

    /// <summary>Grid cells per terrain tile - a tile is 250 world units, a cell is 250/23.</summary>
    public const int CellsPerTile = 23;

    private readonly float[] _tileHeights;

    public TerrainGrid(
        byte[] cells, int bytesPerRow, int rows,
        long totalTilesX = 0, long totalTilesY = 0, float[]? tileHeights = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = cells;
        TilesX = (int)Math.Max(0, totalTilesX);
        TilesY = (int)Math.Max(0, totalTilesY);
        _tileHeights = tileHeights ?? [];
        _bytesPerRow = bytesPerRow;
        StoredWidth = bytesPerRow * 2;
        StoredHeight = rows;

        // The stored buffer can be WIDER than the area. Row stride is a byte count and the
        // game is free to round it up, which leaves a strip of padding cells at the right
        // edge and below - and drawing the buffer's full extent then stretches the whole
        // outline by that strip, an error that is invisible in the middle and grows toward
        // the edges. The tile count is the area's real size, so it wins when it is smaller.
        Width = Fit(StoredWidth, totalTilesX);
        Height = Fit(StoredHeight, totalTilesY);
    }

    /// <summary>Grid cells across, excluding row padding.</summary>
    public int Width { get; }

    /// <summary>Grid cells down, excluding trailing padding rows.</summary>
    public int Height { get; }

    /// <summary>What the buffer holds, padding included - for the diagnostic readout.</summary>
    public int StoredWidth { get; }

    /// <summary>Rows the buffer holds, padding included.</summary>
    public int StoredHeight { get; }

    /// <summary>Terrain tiles across and down. A tile is CellsPerTile cells each way.</summary>
    public int TilesX { get; }

    public int TilesY { get; }

    /// <summary>True when per-tile heights were read - without them the map is drawn flat.</summary>
    public bool HasHeights => _tileHeights.Length > 0;

    /// <summary>
    /// Ground height at a grid cell, in the same world units entities report.
    /// </summary>
    /// <remarks>
    /// Per TILE, not per cell. The finer sub-tile heights exist behind another pointer and
    /// describe variation WITHIN a tile - detail a pathfinder wants and a map outline does
    /// not, since a tile is 250 world units and that is the scale a hill or a staircase
    /// spans anyway.
    ///
    /// Returns 0 when heights are unavailable, which draws the map flat: exactly what it did
    /// before heights existed, so a failed read costs the correction and nothing else.
    /// </remarks>
    public float HeightAt(int cellX, int cellY)
    {
        if (_tileHeights.Length == 0 || TilesX <= 0)
        {
            return 0f;
        }

        int tx = Math.Clamp(cellX / CellsPerTile, 0, TilesX - 1);
        int ty = Math.Clamp(cellY / CellsPerTile, 0, TilesY - 1);
        int index = (ty * TilesX) + tx;
        return (uint)index < (uint)_tileHeights.Length ? _tileHeights[index] : 0f;
    }

    /// <summary>Describes the grid and any padding found, so a mismatch is visible.</summary>
    public string Describe()
        => Width == StoredWidth && Height == StoredHeight
            ? $"{Width}x{Height}"
            : $"{Width}x{Height} (buffer {StoredWidth}x{StoredHeight})";

    /// <summary>Takes the tile-derived size when it is smaller and plausible.</summary>
    private static int Fit(int stored, long tiles)
    {
        long fromTiles = tiles * CellsPerTile;
        return fromTiles > 0 && fromTiles < stored ? (int)fromTiles : stored;
    }

    /// <summary>True when this cell can be walked on. Outside the grid counts as solid.</summary>
    public bool IsWalkable(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return false;
        }

        int index = (y * _bytesPerRow) + (x >> 1);
        if ((uint)index >= (uint)_cells.Length)
        {
            return false;
        }

        byte packed = _cells[index];
        return ((x & 1) == 0 ? packed & 0x0F : packed >> 4) != 0;
    }

    /// <summary>
    /// Marks the boundary between walkable ground and everything else.
    /// </summary>
    /// <remarks>
    /// An OUTLINE rather than a filled area, because the result is drawn on top of the
    /// game's own map: filling every walkable cell would cover the map with a solid sheet
    /// and hide what it is drawn over. The boundary is the useful part anyway - it is the
    /// shape of the level, which is the thing a map is being consulted for.
    ///
    /// A cell is on the boundary when it is walkable and at least one of its four
    /// neighbours is not, so the line lands just INSIDE the walkable area and the drawn
    /// shape is the floor rather than the wall.
    /// </remarks>
    public byte[] BuildOutline()
    {
        var mask = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (IsWalkable(x, y)
                    && (!IsWalkable(x - 1, y) || !IsWalkable(x + 1, y)
                        || !IsWalkable(x, y - 1) || !IsWalkable(x, y + 1)))
                {
                    mask[row + x] = 1;
                }
            }
        }

        return mask;
    }
}

/// <summary>
/// Reads the area's walkable grid, once per area.
/// </summary>
/// <remarks>
/// The grid is megabytes, so it is read on an area change and then reused - the alternative
/// is a multi-megabyte copy per frame for data that cannot change.
///
/// It is also NOT there immediately. Terrain populates after the area loads, and on a large
/// map that can take a minute or more; both vector pointers reading null is the normal
/// "still loading" state rather than a failure, so it retries quietly instead of reporting
/// an error nobody can act on.
///
/// The metadata is an INLINE struct at AreaInstance + TerrainMetadata, not a pointer to
/// follow - dereferencing it lands somewhere unrelated and reads plausible nonsense.
/// </remarks>
public sealed class TerrainReader
{
    /// <summary>How long to wait before trying again while terrain is still loading.</summary>
    private const int RetryMs = 1500;

    private const long MinDataSize = 64;
    private const long MaxDataSize = 32 * 1024 * 1024;

    /// <summary>Bytes per TileStruct entry in the tile-details vector.</summary>
    private const int TileEntrySize = 0x38;

    /// <summary>
    /// Turns the raw tile height into world units. GameHelper2's constant, sign included -
    /// the game counts height downward and entity Z counts it up.
    /// </summary>
    private const float HeightScale = -7.8125f;

    private readonly IMemoryReader _reader;
    private readonly int _terrainMetadata;
    private readonly int _walkableData;
    private readonly int _bytesPerRow;
    private readonly int _totalTilesX;
    private readonly int _totalTilesY;
    private readonly int _tileDetails;
    private readonly int _heightMultiplier;
    private readonly int _tileHeight;
    private readonly int _areaHash;

    private TerrainGrid? _grid;
    private uint _gridArea;
    private long _nextAttempt;

    /// <summary>Why the last attempt produced nothing, for the status readout.</summary>
    public string LastError { get; private set; } = string.Empty;

    public TerrainReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef area = schema.Structs["AreaInstance"];
        _terrainMetadata = area.OffsetOf("TerrainMetadata");
        _areaHash = area.OffsetOf("CurrentAreaHash");

        StructDef terrain = schema.Structs["TerrainMetadata"];
        _walkableData = terrain.OffsetOf("GridWalkableData");
        _bytesPerRow = terrain.OffsetOf("BytesPerRow");
        _totalTilesX = terrain.OffsetOf("TotalTilesX");
        _totalTilesY = terrain.OffsetOf("TotalTilesY");
        _tileDetails = terrain.OffsetOf("TileDetailsPtr");
        _heightMultiplier = terrain.OffsetOf("TileHeightMultiplier");
        _tileHeight = schema.Structs["TileStruct"].OffsetOf("TileHeight");
    }

    /// <summary>
    /// The current area's grid, or null while it is still loading.
    /// </summary>
    /// <param name="nowMs">A monotonic clock, so the retry pause is testable.</param>
    public TerrainGrid? Read(ulong areaInstance, long nowMs)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(areaInstance))
        {
            return null;
        }

        uint area = _reader.Read<uint>(areaInstance + (ulong)_areaHash);
        if (_grid is not null && area == _gridArea)
        {
            return _grid;
        }

        if (nowMs < _nextAttempt)
        {
            return null;
        }

        _nextAttempt = nowMs + RetryMs;
        TerrainGrid? grid = Load(areaInstance + (ulong)_terrainMetadata);
        if (grid is null)
        {
            return null;
        }

        _grid = grid;
        _gridArea = area;
        return grid;
    }

    private TerrainGrid? Load(ulong terrainBase)
    {
        ulong first = _reader.ReadPointer(terrainBase + (ulong)_walkableData);
        ulong last = _reader.ReadPointer(terrainBase + (ulong)_walkableData + 8);
        if (first == 0 || last <= first)
        {
            // The ordinary state right after a zone change, not a fault.
            LastError = "loading";
            return null;
        }

        long dataSize = (long)(last - first);
        if (dataSize is < MinDataSize or > MaxDataSize)
        {
            LastError = $"implausible size {dataSize}";
            return null;
        }

        // The row stride has to DIVIDE the data and leave a sane number of rows. Checking
        // that rather than trusting the field is what turns a drifted offset into a clear
        // "no" instead of a grid with the right area and the wrong shape - which would draw
        // a convincing, completely wrong map.
        int stride = _reader.Read<int>(terrainBase + (ulong)_bytesPerRow);
        if (stride is <= 1 or > 16384 || dataSize % stride != 0)
        {
            LastError = $"bad row stride {stride} for {dataSize} bytes";
            return null;
        }

        long rows = dataSize / stride;
        if (rows is < 16 or > 32768)
        {
            LastError = $"implausible row count {rows}";
            return null;
        }

        var cells = new byte[dataSize];
        if (!_reader.TryRead(first, cells))
        {
            LastError = "grid read failed";
            return null;
        }

        // Sanity-checked rather than trusted: a wrong tile count would CROP the map, which
        // looks like a smaller area rather than a bad read. Zero means "no opinion", and
        // the buffer's own size stands.
        long tilesX = _reader.Read<long>(terrainBase + (ulong)_totalTilesX);
        long tilesY = _reader.Read<long>(terrainBase + (ulong)_totalTilesY);
        if (tilesX is < 1 or > 4096) { tilesX = 0; }
        if (tilesY is < 1 or > 4096) { tilesY = 0; }

        LastError = string.Empty;
        return new TerrainGrid(cells, stride, (int)rows, tilesX, tilesY, ReadTileHeights(terrainBase, tilesX, tilesY));
    }

    /// <summary>
    /// Reads one ground height per terrain tile, in the world units entities report.
    /// </summary>
    /// <remarks>
    /// GameHelper2's formula, minus the sub-tile term:
    ///     height = (TileHeight * TileHeightMultiplier + subTileHeight) * 7.8125 * -1
    /// The sub-tile part describes variation WITHIN a tile and costs a good deal more to
    /// read: a pointer per tile to a per-template vector, a rotation lookup through two more
    /// scanned statics, and a run-length decode whose format is inferred from the array's
    /// length. The tile-level term is the one that moves a hill, which spans many tiles; what
    /// is left out is the slope inside a single one, which is what a staircase is.
    ///
    /// So this is the cheap 90% and it is not the whole correction. The two ground figures in
    /// the overlay's terrain readout measure what remains.
    ///
    /// Returns an empty array on any problem, which leaves the map drawn flat - what it did
    /// before heights existed. The correction is worth having and not worth failing over.
    /// </remarks>
    private float[] ReadTileHeights(ulong terrainBase, long tilesX, long tilesY)
    {
        if (tilesX <= 0 || tilesY <= 0)
        {
            return [];
        }

        ulong first = _reader.ReadPointer(terrainBase + (ulong)_tileDetails);
        ulong last = _reader.ReadPointer(terrainBase + (ulong)_tileDetails + 8);
        if (first == 0 || last <= first)
        {
            return [];
        }

        long count = tilesX * tilesY;
        long available = (long)(last - first) / TileEntrySize;
        if (available < count || count > 4_000_000)
        {
            return [];
        }

        short multiplier = _reader.Read<short>(terrainBase + (ulong)_heightMultiplier);
        var tiles = new byte[count * TileEntrySize];
        if (!_reader.TryRead(first, tiles))
        {
            return [];
        }

        var heights = new float[count];
        for (long i = 0; i < count; i++)
        {
            short raw = BitConverter.ToInt16(tiles, (int)((i * TileEntrySize) + _tileHeight));
            heights[i] = raw * multiplier * HeightScale;
        }

        return heights;
    }
}
