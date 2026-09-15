using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// The maps that can actually appear on the atlas, read out of EndgameMaps.dat.
/// </summary>
/// <remarks>
/// WHAT THIS EXISTS TO CORRECT, and it is the oldest wrong assumption in this project.
/// data/atlas-maps.json is named for the atlas and is not about the atlas: it is a copy of
/// WorldAreas.dat with curated tags added, and WorldAreas is EVERY AREA THE GAME HAS. Its 442 rows
/// hold 83 personal hideouts, 123 campaign zones, 16 Sanctum floors, the Expedition sub-areas, the
/// login scene, the character-select screen, a row called NULL, the developers' Design and
/// Programming worlds - and a row called "Atlas", which is the atlas itself. Everything that reads
/// that file as "the atlas maps" has been reading a list of the whole game.
///
/// NEITHER THE NAME NOR THE TAGS SEPARATE THEM. Measured against the 106 ids this project has
/// actually seen as atlas nodes: the Map* prefix misses ExpeditionLogBook_*, Abyss_Pinnacle,
/// G_Endgame_Town and IncursionHubEndgame, and the game's own "map" tag misses sixteen of those
/// 106, every Expedition logbook among them. WorldAreas simply does not know what an atlas is.
///
/// EndgameMaps.dat DOES, because it is the table an atlas node points AT: a node's data block
/// holds an EndgameMaps row at +0x290 (0.5.5; it was +0x2A0, and the old offset now lands on the
/// node's atlas-passive row and yields a real-looking id from the wrong table - see AtlasNodeData).
/// Column 0 of such a row is the WorldAreas reference. So the set of atlas maps is exactly the set
/// of WorldAreas rows that EndgameMaps references, and that is 173 rows rather than 442.
///
/// THE TABLE IS REACHED THE SAME WAY WorldAreas IS, and for free. A dat foreign reference is
/// sixteen bytes - the row at +0x00 and the TABLE at +0x08 - so the node that gives the row also
/// gives the table, and one atlas node is the whole prerequisite. The loaded-file table off
/// FileRoot lists EndgameMaps.dat too and would answer the same question, but it costs a walk of
/// eight thousand records to find one table this route reaches in four reads.
///
/// IT NEEDS NO ARITHMETIC, which is what makes it survive a patch that WorldAreaCatalogue would
/// refuse. That walk reads columns at computed offsets and so has to agree with dat-schema on the
/// row size to the byte; this one reads COLUMN 0 ONLY, and the row size it strides by is the one
/// the table states about itself. The size did move - 0xF0 on the pre-0.5.5 capture against 0xF1
/// on 0.5.5, with the row count 173 both times, the same one-byte growth EndgameMapAtlas shows at
/// 0x11D -> 0x121 - and this walk does not care.
/// </remarks>
public sealed class EndgameMapCatalogue
{
    /// <summary>Most rows believed. Beyond this the table pointer is not a table.</summary>
    private const int MostRows = 8192;

    /// <summary>Most bytes one row may take, for the same reason.</summary>
    private const int LargestRow = 0x1000;

    /// <summary>Rows read per call. 64 x 0xF1 is about 15 KB, which is a sane read.</summary>
    private const int RowsPerRead = 64;

    /// <summary>Longest string taken seriously behind an id.</summary>
    private const int MostChars = 96;

    /// <summary>
    /// Smallest row this walks. Column 0 is a sixteen-byte foreign reference, and a table whose
    /// rows cannot hold one is not this table however confidently it named itself.
    /// </summary>
    private const int SmallestRow = 0x10;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    private readonly int _worldAreaRef;
    private readonly int _areaId;

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _mapData;

    private Dictionary<string, int> _maps = new(StringComparer.OrdinalIgnoreCase);

    public EndgameMapCatalogue(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _tables = DatTableShape.From(schema);

        _worldAreaRef = schema.Structs["EndgameMapsRow"].OffsetOf("WorldAreaRef");
        _areaId = schema.Structs["WorldAreaDat"].OffsetOf("IdPtr");

        StructDef node = schema.Structs["AtlasNode"];
        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _mapData = schema.Structs["AtlasNodeData"].OffsetOf("MapDataPtr");
    }

    /// <summary>What the table said about itself, or null when it has not been read.</summary>
    public DatTableFacts? Table { get; private set; }

    /// <summary>
    /// Every map id the atlas can hold, and how many EndgameMaps rows name it.
    /// </summary>
    /// <remarks>
    /// A COUNT rather than a set, because the rows are not distinct by area: several rows can roll
    /// the same WorldArea, and a walk that silently collapsed them would hide that fact. Anything
    /// asking "is this an atlas map" wants the keys; anything asking how the table is shaped wants
    /// the values.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Maps => _maps;

    /// <summary>How many rows named an area at all. Fewer than the table's rows means gaps.</summary>
    public int RowsNamed { get; private set; }

    /// <summary>Why the last read found nothing, when it did.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Whether a map id is one the atlas can actually hold.</summary>
    /// <remarks>
    /// FALSE IS NOT "NO" UNTIL THE TABLE HAS BEEN READ. With nothing read this answers false for
    /// everything, which is the honest state but not a verdict - callers that would hide a map on
    /// the strength of it have to check <see cref="Table"/> first.
    /// </remarks>
    public bool Holds(string? mapId) => mapId is { Length: > 0 } && _maps.ContainsKey(mapId);

    /// <summary>
    /// Reads the table an ATLAS NODE names, which is the cheap way to reach it.
    /// </summary>
    /// <remarks>
    /// The node's chain gives its EndgameMaps row at +0x290 and the TABLE at +0x298 - the two
    /// halves of one foreign reference. The row is what the map id is read from already; the table
    /// beside it is what nothing had used.
    /// </remarks>
    public bool ReadFromNode(ulong element)
    {
        if (_maps.Count > 0)
        {
            return true;
        }

        ulong storage = _reader.ReadPointer(element + (ulong)_dataStorage);
        ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
            ? _reader.ReadPointer(storage + (ulong)_data)
            : 0;

        if (!MemoryReaderExtensions.IsPlausiblePointer(data))
        {
            LastError = "that node has no data block, so it names no table";
            return false;
        }

        // NOT HANDED STRAIGHT TO Read, and the difference is a diagnosis this cost once. A caller
        // walks nodes until one works; when the table is found but its ROWS will not read, every
        // later node still gets tried, and a node whose own pointer is missing would then replace
        // "173 rows and none of them names an area" with "0x0 does not read as a dat table" - the
        // useful answer overwritten by a useless one, on the seventh attempt at the same table.
        ulong table = _reader.ReadPointer(data + (ulong)_mapData + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(table))
        {
            LastError = Table is null
                ? "that node names no EndgameMaps table"
                : LastError;
            return false;
        }

        return Read(table);
    }

    /// <summary>
    /// Reads the whole table, once. Later calls with the same table are ignored.
    /// </summary>
    /// <param name="table">
    /// The table OBJECT - the second half of a dat foreign reference, not a row.
    /// </param>
    public bool Read(ulong table)
    {
        if (_maps.Count > 0)
        {
            return true;
        }

        LastError = string.Empty;
        if (_tables is not { } shape)
        {
            LastError = "the schema does not describe dat tables";
            return false;
        }

        if (PointerPeek.DescribeTable(_reader, table, shape) is not { } facts)
        {
            // Only when nothing better is known. See ReadFromNode: a second node offering a worse
            // pointer must not erase the table a first node already identified.
            LastError = Table is null ? $"0x{table:X} does not read as a dat table" : LastError;
            return false;
        }

        Table = facts;
        if (facts.Rows is <= 0 or > MostRows || facts.RowSize is < SmallestRow or > LargestRow)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows of 0x{facts.RowSize:X}, which is not a table this walks";
            return false;
        }

        // Ids repeat across rows - several EndgameMaps rows can roll the same area - so the string
        // behind a WorldAreas row is read once per ROW ADDRESS rather than once per table row.
        var named = new Dictionary<ulong, string>();
        var maps = new Dictionary<string, int>((int)facts.Rows, StringComparer.OrdinalIgnoreCase);
        int rowsNamed = 0;

        var block = new byte[RowsPerRead * (int)facts.RowSize];
        for (long first = 0; first < facts.Rows; first += RowsPerRead)
        {
            int rows = (int)Math.Min(RowsPerRead, facts.Rows - first);
            Span<byte> span = block.AsSpan(0, rows * (int)facts.RowSize);
            if (!_reader.TryRead(facts.RowsBegin + (ulong)(first * facts.RowSize), span))
            {
                continue; // a page that would not read; the rest of the table still can
            }

            for (int i = 0; i < rows; i++)
            {
                ReadOnlySpan<byte> row = span[(i * (int)facts.RowSize)..];
                ulong area = BinaryPrimitives.ReadUInt64LittleEndian(row[_worldAreaRef..]);
                if (!MemoryReaderExtensions.IsPlausiblePointer(area))
                {
                    continue;
                }

                if (!named.TryGetValue(area, out string? id))
                {
                    id = Text(_reader.ReadPointer(area + (ulong)_areaId));
                    named[area] = id;
                }

                if (id.Length == 0)
                {
                    continue;
                }

                maps[id] = maps.GetValueOrDefault(id) + 1;
                rowsNamed++;
            }
        }

        _maps = maps;
        RowsNamed = rowsNamed;
        if (maps.Count == 0)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows and none of them names an area";
        }

        return maps.Count > 0;
    }

    private string Text(ulong at)
        => MemoryReaderExtensions.IsPlausiblePointer(at) ? _reader.ReadUnicodeString(at, MostChars) : string.Empty;

    /// <summary>
    /// What the table holds, and how much of data/atlas-maps.json it says is not about the atlas.
    /// </summary>
    public IReadOnlyList<string> Describe(AtlasMapNames file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var said = new List<string>();
        if (Table is not { } facts)
        {
            said.Add($"ENDGAMEMAPS - not read: {(LastError.Length > 0 ? LastError : "no table seen yet")}");
            return said;
        }

        said.Add($"ENDGAMEMAPS - \"{facts.Path}\", {facts.Rows} rows of 0x{facts.RowSize:X},"
            + $" {RowsNamed} named an area, {_maps.Count} distinct maps");
        if (LastError.Length > 0)
        {
            said.Add($"  {LastError}");
        }

        int shared = 0;
        foreach (string id in _maps.Keys)
        {
            if (file.All.ContainsKey(id))
            {
                shared++;
            }
        }

        said.Add($"  data/atlas-maps.json lists {file.Count}; {shared} of this table's maps are in it,"
            + $" and {file.Count - shared} of its entries are not atlas maps at all");

        // The rows naming the same area twice are worth seeing: they are the difference between
        // "173 maps" and "173 rows", and nothing else says which of the two the number is.
        var repeated = _maps.Where(pair => pair.Value > 1).OrderByDescending(pair => pair.Value).ToList();
        said.Add(repeated.Count == 0
            ? "  every row names a different area"
            : $"  {repeated.Count} areas are named by more than one row:");
        foreach ((string id, int rows) in repeated.Take(12))
        {
            said.Add($"    {rows,3}x  {id}");
        }

        return said;
    }
}
