using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>One row of WorldAreas.dat, as much of it as this tool has a use for.</summary>
/// <param name="Id">The engine id - <c>MapLostTowers</c>. The same on every client, so the key.</param>
/// <param name="Name">The display name, in the CLIENT'S LANGUAGE. A label, never a key.</param>
/// <param name="Tags">The game's own tag ids - "map", "map_tower", "swamp_biome".</param>
public sealed record WorldArea(
    string Id,
    string Name,
    bool IsMapArea,
    bool IsHideout,
    bool IsUnique,
    IReadOnlyList<string> Tags);

/// <summary>
/// Every area the game knows, read out of WorldAreas.dat instead of out of a file this tool ships.
/// </summary>
/// <remarks>
/// WHY THIS CAN EXIST AT ALL. data/atlas-maps.json carries a name, a unique flag and a handful of
/// tags for 440 maps, and the table it was made from turns out to be 442 rows long - it is that
/// table with tags added by hand. The row size divides out of the table itself at 0x2E0 and
/// agrees with the arithmetic dat-schema publishes, so every column is reachable; see
/// WorldAreaDat, where what is measured and what is not is written down.
///
/// THE ROUTE IS THE ONLY ONE THERE IS, and it is not the obvious one. LoadedDatTables walks the
/// game's own file table and WorldAreas IS NOT IN IT - measured, 6913 record names, none of them
/// this table (see that class and DatTableSurveyTests). What does lead here is a dat foreign
/// reference: an atlas node's EndgameMaps row names its WorldAreas row at +0x00 and the TABLE
/// that row belongs to at +0x08, and a table knows its own rows. So this needs one atlas node to
/// have been seen, and then it needs nothing ever again: tables are loaded once and never freed
/// while the game runs, which is why <see cref="Read"/> is a one-shot and the result is kept.
///
/// IT READS THE ROWS IN BLOCKS, not field by field. 442 rows of 0x2E0 is half a megabyte of
/// contiguous memory; taken a slot at a time it would be tens of thousands of calls, and taken
/// whole it would be one enormous read in every recording. A chunk of rows at a time is the
/// middle, and the strings - two per row, plus the tags - are the only per-row reads left. Tag
/// rows repeat across the table (every map carries "map"), so they are resolved once and cached
/// by address.
///
/// WHAT IT DOES NOT DO is replace the file WHOLE, and the walk is what settled that rather than
/// left it open. <see cref="Describe"/> reports what it read beside what data/atlas-maps.json says,
/// and over all 442 rows (tests/fixtures/session-2026-09-catalogue.rec) the two part company in
/// three different ways: the NAMES agree 439 of 440 and the one difference is a trailing space, so
/// names can come from here; the UNIQUE flag disagrees on six ids in both directions, and the
/// disagreement is the file's fault rather than the column's - IsUniqueMapArea is now the flag in
/// force, see AtlasMapNames.LearnUnique; and the TAGS share no vocabulary at all - the game says
/// map, map_tower, dungeon, pinnacle_boss and biomes, while the file says expedition, arbiter,
/// quest, boss, lineage. The curated words are not derivable from this table, so the file shrinks
/// to them rather than disappearing. See WorldAreaDat in the schema, where each of those is
/// written down with its counts.
/// </remarks>
public sealed class WorldAreaCatalogue
{
    /// <summary>Most rows believed. Beyond this the table pointer is not a table.</summary>
    private const int MostRows = 8192;

    /// <summary>Most bytes one row may take, for the same reason.</summary>
    private const int LargestRow = 0x1000;

    /// <summary>Rows read per call. 32 x 0x2E0 is about 23 KB, which is a sane read.</summary>
    private const int RowsPerRead = 32;

    /// <summary>Most tags on one area. A guard on a count that comes from memory.</summary>
    private const int MostTags = 32;

    /// <summary>Longest string taken seriously behind an id or a name.</summary>
    private const int MostChars = 96;

    /// <summary>Bytes one tag entry takes: a row reference followed by its table.</summary>
    private const int TagEntrySize = 0x10;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    private readonly int _id;
    private readonly int _name;
    private readonly int _isMapArea;
    private readonly int _isHideout;
    private readonly int _tagsArray;
    private readonly int _isUnique;
    private readonly long _computedRowSize;
    private readonly int _tagId;

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _mapData;
    private readonly int _worldAreaRef;

    private readonly Dictionary<ulong, string> _tagNames = [];
    private Dictionary<string, WorldArea> _areas = new(StringComparer.OrdinalIgnoreCase);

    public WorldAreaCatalogue(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _tables = DatTableShape.From(schema);

        StructDef area = schema.Structs["WorldAreaDat"];
        StructDef tag = schema.Structs["TagsRow"];

        _id = area.OffsetOf("IdPtr");
        _name = area.OffsetOf("NamePtr");
        _isMapArea = area.OffsetOf("IsMapArea");
        _isHideout = area.OffsetOf("IsHideout");
        _tagsArray = area.OffsetOf("TagsArray");
        _isUnique = area.OffsetOf("IsUniqueMapArea");
        _computedRowSize = area.Constants["ComputedRowSize"];
        _tagId = tag.OffsetOf("IdPtr");

        StructDef node = schema.Structs["AtlasNode"];
        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _mapData = schema.Structs["AtlasNodeData"].OffsetOf("MapDataPtr");
        _worldAreaRef = schema.Structs["EndgameMapsRow"].OffsetOf("WorldAreaRef");
    }

    /// <summary>What the table said about itself, or null when it has not been read.</summary>
    public DatTableFacts? Table { get; private set; }

    /// <summary>Every area by id.</summary>
    public IReadOnlyDictionary<string, WorldArea> All => _areas;

    /// <summary>Why the last read found nothing, when it did.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>What one area is, or null when the table does not list it.</summary>
    public WorldArea? Of(string id)
        => id.Length > 0 && _areas.TryGetValue(id, out WorldArea? found) ? found : null;

    /// <summary>
    /// Reads the table an ATLAS NODE leads to, which is the only known way to reach it.
    /// </summary>
    /// <remarks>
    /// The node's own chain gives its EndgameMaps row, and that row's first column is a foreign
    /// reference: the WorldAreas row at +0x00 and the TABLE at +0x08. The row is what the map id
    /// is read from already; the table beside it is what nothing had used.
    /// </remarks>
    public bool ReadFromNode(ulong element)
    {
        if (_areas.Count > 0)
        {
            return true;
        }

        ulong storage = _reader.ReadPointer(element + (ulong)_dataStorage);
        ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
            ? _reader.ReadPointer(storage + (ulong)_data)
            : 0;
        ulong endgameRow = MemoryReaderExtensions.IsPlausiblePointer(data)
            ? _reader.ReadPointer(data + (ulong)_mapData)
            : 0;

        if (!MemoryReaderExtensions.IsPlausiblePointer(endgameRow))
        {
            LastError = "that node has no EndgameMaps row, so it names no table";
            return false;
        }

        return Read(_reader.ReadPointer(endgameRow + (ulong)_worldAreaRef + 8));
    }

    /// <summary>
    /// Reads the whole table, once. Later calls with the same table are ignored.
    /// </summary>
    /// <param name="table">
    /// The table OBJECT - the second half of a dat foreign reference, not a row.
    /// </param>
    public bool Read(ulong table)
    {
        if (_areas.Count > 0)
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
            LastError = $"0x{table:X} does not read as a dat table";
            return false;
        }

        Table = facts;
        if (facts.Rows is <= 0 or > MostRows || facts.RowSize is <= 0 or > LargestRow)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows of 0x{facts.RowSize:X}, which is not a table this walks";
            return false;
        }

        // The size the columns were computed against. Reading the rows anyway would be reading
        // the wrong bytes with great confidence - see WorldAreaDat.
        if (facts.RowSize != _computedRowSize)
        {
            LastError = $"{facts.Label} rows are 0x{facts.RowSize:X} and the columns are arithmetic on 0x{_computedRowSize:X}"
                + " - one of the two is wrong and nothing here is worth reading until that is settled";
            return false;
        }

        var areas = new Dictionary<string, WorldArea>((int)facts.Rows, StringComparer.OrdinalIgnoreCase);
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
                if (Area(row) is { } area && area.Id.Length > 0)
                {
                    areas[area.Id] = area;
                }
            }
        }

        _areas = areas;
        if (areas.Count == 0)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows and none of them read as an area";
        }

        return areas.Count > 0;
    }

    /// <summary>One row, decoded out of the block it arrived in.</summary>
    private WorldArea? Area(ReadOnlySpan<byte> row)
    {
        if (row.Length <= _isUnique)
        {
            return null;
        }

        string id = Text(BinaryPrimitives.ReadUInt64LittleEndian(row[_id..]));
        if (id.Length == 0)
        {
            return null;
        }

        return new WorldArea(
            id,
            Text(BinaryPrimitives.ReadUInt64LittleEndian(row[_name..])),
            row[_isMapArea] != 0,
            row[_isHideout] != 0,
            row[_isUnique] != 0,
            Tags(row));
    }

    /// <summary>The Tags column: a count, then a pointer at the entries.</summary>
    /// <remarks>
    /// Measured, not assumed - see WorldAreaDat.TagsArray. The tag rows repeat heavily across the
    /// table and are worth caching by address: 442 areas resolve to SEVENTEEN distinct tags, and
    /// "map" alone accounts for 153 of the references.
    /// </remarks>
    private List<string> Tags(ReadOnlySpan<byte> row)
    {
        var tags = new List<string>();
        if (row.Length < _tagsArray + 16)
        {
            return tags;
        }

        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row[_tagsArray..]);
        ulong entries = BinaryPrimitives.ReadUInt64LittleEndian(row[(_tagsArray + 8)..]);
        if (count is 0 or > MostTags || !MemoryReaderExtensions.IsPlausiblePointer(entries))
        {
            return tags;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong tag = _reader.ReadPointer(entries + (i * TagEntrySize));
            if (!MemoryReaderExtensions.IsPlausiblePointer(tag))
            {
                continue;
            }

            if (!_tagNames.TryGetValue(tag, out string? name))
            {
                name = Text(_reader.ReadPointer(tag + (ulong)_tagId));
                _tagNames[tag] = name;
            }

            if (name.Length > 0)
            {
                tags.Add(name);
            }
        }

        return tags;
    }

    private string Text(ulong at)
        => MemoryReaderExtensions.IsPlausiblePointer(at) ? _reader.ReadUnicodeString(at, MostChars) : string.Empty;

    /// <summary>
    /// What the table holds, and where it differs from the file this tool ships.
    /// </summary>
    /// <remarks>
    /// The difference is the point rather than the catalogue: whether data/atlas-maps.json can go
    /// away is exactly the question of what it says that the table does not. Names and the unique
    /// flag are compared per id; tags are compared as vocabularies, because the two do not use the
    /// same words and counting per map would only report that.
    /// </remarks>
    public IReadOnlyList<string> Describe(AtlasMapNames file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var said = new List<string>();
        if (Table is not { } facts)
        {
            said.Add($"WORLDAREAS CATALOGUE - not read: {(LastError.Length > 0 ? LastError : "no table seen yet")}");
            return said;
        }

        said.Add($"WORLDAREAS CATALOGUE - \"{facts.Path}\", {facts.Rows} rows of 0x{facts.RowSize:X},"
            + $" {_areas.Count} read");
        if (LastError.Length > 0)
        {
            said.Add($"  {LastError}");
        }

        int maps = 0, hideouts = 0, uniques = 0;
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (WorldArea area in _areas.Values)
        {
            if (area.IsMapArea)
            {
                maps++;
            }

            if (area.IsHideout)
            {
                hideouts++;
            }

            if (area.IsUnique)
            {
                uniques++;
            }

            foreach (string tag in area.Tags)
            {
                vocabulary[tag] = vocabulary.GetValueOrDefault(tag) + 1;
            }
        }

        said.Add($"  IsMapArea {maps}   IsHideout {hideouts}   IsUniqueMapArea {uniques}");
        said.Add($"  {vocabulary.Count} distinct tags, commonest first:");
        foreach ((string tag, int count) in vocabulary.OrderByDescending(pair => pair.Value).Take(24))
        {
            said.Add($"    {count,5}x  {tag}");
        }

        said.AddRange(Against(file));
        return said;
    }

    /// <summary>The comparison proper: the file's claims, checked against the table.</summary>
    private IEnumerable<string> Against(AtlasMapNames file)
    {
        yield return $"  against data/atlas-maps.json ({file.Count} entries):";

        int missing = 0, names = 0, uniques = 0;
        var curated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, AtlasMapInfo info) in file.All)
        {
            if (Of(id) is not { } area)
            {
                missing++;
                continue;
            }

            // The name is expected to differ on a client that is not in English - that is the
            // whole reason to read it - so this counts rather than complains.
            if (info.Name.Length > 0 && !string.Equals(info.Name, area.Name, StringComparison.OrdinalIgnoreCase))
            {
                names++;
            }

            if (info.Unique != area.IsUnique)
            {
                uniques++;
            }

            foreach (string tag in info.Tags)
            {
                if (!area.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    curated[tag] = curated.GetValueOrDefault(tag) + 1;
                }
            }
        }

        yield return $"    {missing} of its ids are not in the table";
        yield return $"    {names} names differ (expected off an English client: 0)";
        // NOT a complaint either. IsUniqueMapArea is the column in force (AtlasMapNames.LearnUnique)
        // and the file's is kept to be compared against, so this counts how many maps the file
        // would have grouped wrongly - six, on the client this was measured on.
        yield return uniques == 0
            ? "    the unique flag agrees everywhere - the file's column costs nothing either way"
            : $"    the unique flag differs on {uniques} - IsUniqueMapArea wins, and those are the maps it moves";

        if (curated.Count == 0)
        {
            yield return "    every tag the file carries is in the table too - the file has nothing left to say";
            yield break;
        }

        yield return "    tags the file carries that the table does not, which is what would have to stay:";
        foreach ((string tag, int count) in curated.OrderByDescending(pair => pair.Value))
        {
            yield return $"      {count,5}x  {tag}";
        }
    }
}
