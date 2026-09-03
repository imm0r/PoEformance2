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

    private readonly TerrainHeightField? _heights;
    private readonly IReadOnlyList<TerrainLandmark> _landmarks;
    private readonly IReadOnlyList<TerrainRoom> _rooms;

    public TerrainGrid(
        byte[] cells, int bytesPerRow, int rows,
        long totalTilesX = 0, long totalTilesY = 0, float[]? tileHeights = null,
        string heightNote = "")
        : this(
            cells, bytesPerRow, rows, totalTilesX, totalTilesY,
            tileHeights is { Length: > 0 }
                ? TerrainHeightField.Tiles(tileHeights, (int)totalTilesX, (int)totalTilesY)
                : null,
            heightNote)
    {
    }

    public TerrainGrid(
        byte[] cells, int bytesPerRow, int rows,
        long totalTilesX, long totalTilesY, TerrainHeightField? heights, string heightNote = "",
        IReadOnlyList<TerrainLandmark>? landmarks = null,
        IReadOnlyList<TerrainRoom>? rooms = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = cells;
        _landmarks = landmarks ?? [];
        _rooms = rooms ?? [];
        TilesX = (int)Math.Max(0, totalTilesX);
        TilesY = (int)Math.Max(0, totalTilesY);
        _heights = heights;
        HeightNote = heightNote;
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

    /// <summary>True when heights were read - without them the map is drawn flat.</summary>
    public bool HasHeights => _heights is not null;

    /// <summary>True when the slope INSIDE each tile is included, not just the tile's level.</summary>
    public bool HasSubTileHeights => _heights?.HasSubTile ?? false;

    /// <summary>
    /// Places found in the SHAPE of the ground - boss arenas above all.
    /// </summary>
    /// <remarks>
    /// On the grid because that is where they come from and when: the terrain is read once per
    /// area, and these are known from that moment - long before the boss they belong to
    /// exists as an entity.
    /// </remarks>
    public IReadOnlyList<TerrainLandmark> Landmarks => _landmarks;

    /// <summary>
    /// The area's rooms - the blocks of tiles the game placed, each under its own file name.
    /// </summary>
    /// <remarks>
    /// The layout in WORDS, beside the outline that draws it as a shape: which end holds the
    /// exit, where the bridge is, which blob is the arena. Known from the moment the area
    /// loads, for the same reason the landmarks are - it is read out of the ground rather than
    /// out of the entity list. See <see cref="TerrainRooms"/>.
    /// </remarks>
    public IReadOnlyList<TerrainRoom> Rooms => _rooms;

    /// <summary>
    /// Why the heights are, or are not, here.
    /// </summary>
    /// <remarks>
    /// Carried on the grid rather than left in the reader because "no improvement" and
    /// "improved, still not right" need completely different fixes, and without this the two
    /// are indistinguishable from the screen: a height read that quietly returned nothing
    /// draws exactly the flat map it drew before.
    /// </remarks>
    public string HeightNote { get; }

    /// <summary>
    /// Ground height at a grid cell, in the same world units entities report.
    /// </summary>
    /// <remarks>
    /// Per CELL when the sub-tile arrays were readable, per TILE otherwise - see
    /// <see cref="TerrainHeightField"/> for why the difference matters more than a tile's
    /// 250 units suggests.
    ///
    /// Returns 0 when heights are unavailable, which draws the map flat: exactly what it did
    /// before heights existed, so a failed read costs the correction and nothing else.
    /// </remarks>
    public float HeightAt(int cellX, int cellY) => _heights?.HeightAt(cellX, cellY) ?? 0f;

    /// <summary>
    /// How far to move a cell, in cells, so a FLAT drawing shows it at its real height.
    /// </summary>
    /// <remarks>
    /// GameHelper2's trick, and it is exact rather than an approximation. The map transform
    ///     screen = ((dx - dy) * cos, (dz - (dx + dy)) * sin)
    /// is unchanged in x by moving a point the same distance along BOTH grid axes - the two
    /// cancel - while in y the shift counts twice. So a height can be expressed as a
    /// displacement of 'height / 2' grid units along the diagonal, baked into the picture
    /// once, and the picture then drawn perfectly flat.
    ///
    /// The alternative is a mesh whose corners carry heights, which is what this replaced:
    /// it costs a projection per corner every frame, and it is only exact AT the corners -
    /// between them the height is interpolated, so a cliff edge between two corners is drawn
    /// as a ramp. Displacing each cell is per-CELL exact and costs nothing per frame.
    ///
    /// Whole cells, because the picture has no finer resolution to put it at - but ROUNDED to
    /// the nearest rather than truncated, which is where this parts company with the
    /// reference. Truncation moves every value toward zero, and this game writes raised
    /// ground as a NEGATIVE height, so on real terrain it is not a rounding choice but a
    /// consistent under-shift: the whole map drawn slightly too low. Rounding costs nothing
    /// and halves the average error.
    /// </remarks>
    public int IsoHeightShift(int cellX, int cellY)
        => _heights is null
            ? 0
            : (int)MathF.Round(_heights.HeightAt(cellX, cellY) / (2f * Ui.MapView.HeightToGrid));

    /// <summary>Describes the grid and any padding found, so a mismatch is visible.</summary>
    public string Describe()
        => (Width == StoredWidth && Height == StoredHeight
            ? $"{Width}x{Height}"
            : $"{Width}x{Height} (buffer {StoredWidth}x{StoredHeight})")
           + DescribeRooms();

    /// <summary>
    /// How many rooms were found, and what the biggest one's file is called.
    /// </summary>
    /// <remarks>
    /// The PATH rather than a count alone, and that is the whole reason this line exists: what
    /// the game stores on a tile is a question only the game can answer, and the answer decides
    /// what the room names on the map can ever say. One look at this row in the readout settles
    /// it for an area - which beats reasoning about it from a reference project that reads a
    /// different game's build.
    /// </remarks>
    private string DescribeRooms()
    {
        if (_rooms.Count == 0)
        {
            return string.Empty;
        }

        TerrainRoom biggest = _rooms[0];
        foreach (TerrainRoom room in _rooms)
        {
            if (room.Tiles > biggest.Tiles)
            {
                biggest = room;
            }
        }

        return $", {_rooms.Count} rooms (biggest {biggest.Path} at {biggest.Tiles} tiles)";
    }

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

    /// <summary>Largest sub-tile height array worth believing. A tile is 23x23 = 529 cells.</summary>
    private const int MaxSubHeightBytes = 2048;

    /// <summary>Distinct tile templates read per area, as a guard rather than a real bound.</summary>
    private const int MaxSubTemplates = 4096;

    private readonly IMemoryReader _reader;
    private readonly int _terrainMetadata;
    private readonly int _walkableData;
    private readonly int _bytesPerRow;
    private readonly int _totalTilesX;
    private readonly int _totalTilesY;
    private readonly int _tileDetails;
    private readonly int _heightMultiplier;
    private readonly int _tileHeight;
    private readonly int _subTileDetails;
    private readonly int _rotationSelector;
    private readonly int _tgtFile;
    private readonly int _tgtPath;
    private readonly int _tileIdX;
    private readonly int _tileIdY;
    private readonly int _areaHash;
    private readonly TerrainRotationTables _rotation;

    private TerrainGrid? _grid;
    private uint _gridArea;
    private long _nextAttempt;

    /// <summary>Why the last attempt produced nothing, for the status readout.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <param name="rotation">
    /// Addresses of the two engine tables that say how a tile was placed. Without them the
    /// field stays tile-only: the sub-tile heights are stored per tile TEMPLATE, so reading
    /// one without knowing its orientation returns a real height belonging to the wrong
    /// corner - worse than not reading it.
    /// </param>
    public TerrainReader(IMemoryReader reader, OffsetSchema schema, TerrainRotationTables rotation = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _rotation = rotation;

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
        StructDef tile = schema.Structs["TileStruct"];
        _tileHeight = tile.OffsetOf("TileHeight");
        _subTileDetails = tile.OffsetOf("SubTileDetailsPtr");
        _rotationSelector = tile.OffsetOf("RotationSelector");
        _tgtFile = tile.OffsetOf("TgtFilePtr");
        _tileIdX = tile.OffsetOf("TileIdX");
        _tileIdY = tile.OffsetOf("TileIdY");
        _tgtPath = schema.Structs["TgtFile"].OffsetOf("TgtPath");
    }

    /// <summary>Tile-file paths are static game data; cache them by their pointer.</summary>
    private readonly Dictionary<ulong, string> _tgtPaths = [];

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
        TerrainHeightField? heights = ReadTileHeights(terrainBase, tilesX, tilesY);

        return new TerrainGrid(
            cells, stride, (int)rows, tilesX, tilesY, heights, _heightNote, _landmarks, _rooms);
    }

    /// <summary>Why the last height read produced what it did. See TerrainGrid.HeightNote.</summary>
    private string _heightNote = string.Empty;

    /// <summary>What the last tile read found in the shape of the ground.</summary>
    private IReadOnlyList<TerrainLandmark> _landmarks = [];

    /// <summary>The rooms the same read found. See TerrainGrid.Rooms.</summary>
    private IReadOnlyList<TerrainRoom> _rooms = [];

    /// <summary>
    /// Names for particular tiles of the current area, keyed as the reference writes them.
    /// </summary>
    /// <remarks>
    /// Supplied from outside because it is DATA, not code - the same principle the offsets
    /// follow. Whoever knows which area this is sets it before the terrain is read.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? CuratedLandmarks { get; set; }

    /// <summary>
    /// Reads every tile's file path, and from them the places and the rooms.
    /// </summary>
    /// <remarks>
    /// The tile buffer is already in hand from the heights, so the tile records themselves
    /// cost nothing here. What could cost is the PATHS: an area is tens of thousands of tiles
    /// and reading a string for each would be a read per tile. They are deduplicated by the
    /// file pointer instead - an area is built from a few hundred distinct tiles, each used
    /// hundreds of times - which turns that into a few hundred reads, once per area.
    ///
    /// BOTH ANSWERS FROM ONE PASS, and they want the tiles differently. A landmark is one tile
    /// that could BE something, so those are kept as records and only where the name or a
    /// curated key says to. A ROOM is every tile, because a room is defined by which of its
    /// neighbours carry the same file - so what the rooms need is not the records but an id
    /// per tile, which is an int array rather than tens of thousands of objects.
    ///
    /// The rooms are found whether or not anything is drawing them, and that is deliberate:
    /// the loop already looked up every tile's path, so what they add is an int store per tile
    /// and a flood fill, once per area. Reading them on demand instead would mean the switch
    /// did nothing until the next zone - a setting that appears not to work.
    /// </remarks>
    private void ReadTilePaths(byte[] tiles, long count, long tilesX)
    {
        if (tilesX <= 0)
        {
            _landmarks = [];
            _rooms = [];
            return;
        }

        var found = new List<TerrainTile>();

        // Path ids for the rooms: the file pointer's own dedup gives the STRING, and this
        // gives it a small number the flood fill can compare without touching memory again.
        var paths = new List<string>();
        var ids = new Dictionary<ulong, int>();
        var tilePath = new int[count];

        for (long i = 0; i < count; i++)
        {
            tilePath[i] = -1;

            int at = (int)(i * TileEntrySize);
            ulong file = BitConverter.ToUInt64(tiles, at + _tgtFile);
            if (!MemoryReaderExtensions.IsPlausiblePointer(file))
            {
                continue;
            }

            if (!ids.TryGetValue(file, out int id))
            {
                if (!_tgtPaths.TryGetValue(file, out string? read))
                {
                    read = _reader.ReadStdWString(file + (ulong)_tgtPath, 128);
                    _tgtPaths[file] = read;
                }

                id = -1;
                if (read.Length > 0)
                {
                    id = paths.Count;
                    paths.Add(read);
                }

                ids[file] = id;
            }

            if (id < 0)
            {
                continue;
            }

            tilePath[i] = id;
            string path = paths[id];

            // Only the tiles that could BE something are kept as records. A curated key needs
            // its sub-ids, so the filter has to let anything curated through as well.
            if (!TerrainLandmarks.LooksLikeArena(path) && CuratedLandmarks is null)
            {
                continue;
            }

            found.Add(new TerrainTile(
                path,
                (int)(i % tilesX),
                (int)(i / tilesX),
                tiles[at + _tileIdX],
                tiles[at + _tileIdY],
                tiles[at + _rotationSelector]));
        }

        _landmarks = TerrainLandmarks.Find(found, CuratedLandmarks);
        _rooms = TerrainRooms.Find(paths, tilePath, (int)tilesX, (int)(count / tilesX));
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
    private TerrainHeightField? ReadTileHeights(ulong terrainBase, long tilesX, long tilesY)
    {
        // Cleared here rather than only on success: every early return below leaves the tiles
        // unread, and keeping the last area's answers would put its rooms on this area's map.
        _landmarks = [];
        _rooms = [];

        if (tilesX <= 0 || tilesY <= 0)
        {
            _heightNote = "no tile count";
            return null;
        }

        ulong first = _reader.ReadPointer(terrainBase + (ulong)_tileDetails);
        ulong last = _reader.ReadPointer(terrainBase + (ulong)_tileDetails + 8);
        if (first == 0 || last <= first)
        {
            _heightNote = "tile vector empty";
            return null;
        }

        long count = tilesX * tilesY;
        long available = (long)(last - first) / TileEntrySize;
        if (available < count || count > 4_000_000)
        {
            _heightNote = $"tile vector holds {available}, needs {count}";
            return null;
        }

        short multiplier = _reader.Read<short>(terrainBase + (ulong)_heightMultiplier);
        var tiles = new byte[count * TileEntrySize];
        if (!_reader.TryRead(first, tiles))
        {
            _heightNote = $"tile read failed ({count * TileEntrySize} bytes)";
            return null;
        }

        // The same buffer answers all three questions, so the places in the ground and the
        // rooms cost one pass over memory that has already been read.
        ReadTilePaths(tiles, count, tilesX);

        var heights = new float[count];
        for (long i = 0; i < count; i++)
        {
            short raw = BitConverter.ToInt16(tiles, (int)((i * TileEntrySize) + _tileHeight));
            heights[i] = raw * multiplier * TerrainHeightField.HeightScale;
        }

        string tileNote = $"{tilesX}x{tilesY} tiles, multiplier {multiplier}";

        TerrainHeightField? full = ReadSubTileHeights(tiles, count, heights, (int)tilesX, (int)tilesY, tileNote);
        if (full is not null)
        {
            return full;
        }

        return TerrainHeightField.Tiles(heights, (int)tilesX, (int)tilesY);
    }

    /// <summary>
    /// Adds the slope inside each tile, or returns null and leaves the field tile-only.
    /// </summary>
    /// <remarks>
    /// Three reads deep and every one of them optional: the two engine tables that describe
    /// how a tile was placed come from a pattern scan that can legitimately fail, and each
    /// tile points at a per-TEMPLATE height array that may not be there. Missing any of it
    /// costs the within-tile detail and nothing else.
    ///
    /// The arrays are read once per distinct template rather than once per tile - an area is
    /// thousands of tiles built from a few hundred templates, so this is the difference
    /// between a few hundred small reads and a few thousand.
    /// </remarks>
    private TerrainHeightField? ReadSubTileHeights(
        byte[] tiles, long count, float[] heights, int tilesX, int tilesY, string tileNote)
    {
        if (!_rotation.IsResolved)
        {
            _heightNote = $"{tileNote}; tile-level only (rotation tables not resolved)";
            return null;
        }

        var selectorTable = new byte[9];
        var helperTable = new byte[32];
        if (!_reader.TryRead(_rotation.Selector, selectorTable) || !_reader.TryRead(_rotation.Helper, helperTable))
        {
            _heightNote = $"{tileNote}; tile-level only (rotation tables unreadable)";
            return null;
        }

        var rotation = new byte[count];
        var subIndex = new int[count];
        var arrays = new List<byte[]>();
        var byPointer = new Dictionary<ulong, int>();

        for (long i = 0; i < count; i++)
        {
            int at = (int)(i * TileEntrySize);
            rotation[i] = tiles[at + _rotationSelector];
            subIndex[i] = -1;

            ulong pointer = BitConverter.ToUInt64(tiles, at + _subTileDetails);
            if (!MemoryReaderExtensions.IsPlausiblePointer(pointer))
            {
                continue;
            }

            if (byPointer.TryGetValue(pointer, out int known))
            {
                subIndex[i] = known;
                continue;
            }

            if (arrays.Count >= MaxSubTemplates)
            {
                continue;
            }

            byte[] array = ReadSubHeightArray(pointer);
            int index = arrays.Count;
            arrays.Add(array);
            byPointer[pointer] = index;
            subIndex[i] = index;
        }

        int withHeights = arrays.Count(a => a.Length > 0);
        if (withHeights == 0)
        {
            _heightNote = $"{tileNote}; tile-level only (no sub-tile arrays in {arrays.Count} templates)";
            return null;
        }

        _heightNote = $"{tileNote}, sub-tile from {withHeights}/{arrays.Count} templates";
        return TerrainHeightField.WithSubTile(
            heights, tilesX, tilesY, rotation, subIndex, [.. arrays], selectorTable, helperTable);
    }

    /// <summary>Reads one template's height array - an StdVector of bytes at its start.</summary>
    private byte[] ReadSubHeightArray(ulong subTile)
    {
        ulong begin = _reader.ReadPointer(subTile);
        ulong end = _reader.ReadPointer(subTile + 8);
        if (begin == 0 || end <= begin)
        {
            return [];
        }

        long length = (long)(end - begin);
        if (length > MaxSubHeightBytes)
        {
            return [];
        }

        var bytes = new byte[length];
        return _reader.TryRead(begin, bytes) ? bytes : [];
    }
}
