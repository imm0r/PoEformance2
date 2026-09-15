using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// One row of EndgameMapContent.dat - a kind of content a map or a node can carry.
/// </summary>
/// <param name="Index">Where in the table it sits, which is the half the badge ids are built on.</param>
/// <param name="Id">The engine name: <c>Breach</c>.</param>
/// <param name="Name">The short name a player sees, where there is one.</param>
/// <param name="Description">The sentence: "Area contains an Otherworldly Breach".</param>
/// <param name="Icon">The art row's own id - the name data/atlas-content.json ships.</param>
/// <param name="IconPath">The whole art path, which that file does not have.</param>
/// <param name="Stats">Which rows of Stats.dat this content grants, as INDICES.</param>
public sealed record MapContentRow(
    long Index,
    string Id,
    string Name,
    string Description,
    string Icon,
    string IconPath,
    IReadOnlyList<long> Stats);

/// <summary>
/// The contents a map can carry, read out of EndgameMapContent.dat.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR: data/atlas-content.json ships 69 badges and 43 effects by hand, ported from
/// GameHelper2, and this is the table those words would come from if the game can supply them. The
/// file is the last piece of atlas knowledge here that nothing has measured against the client.
///
/// THE BADGE IDS LOOK LIKE ROW INDICES AND SAY SO THEMSELVES. Sixty-seven of the file's 69 badges
/// are 0x64..0xA6 - consecutive, no gaps - and the schema already records why: AtlasNode's badge
/// vector holds one BYTE per badge, and "the content id is the row plus 100". 100 is 0x64. So the
/// prediction is exact rather than suggestive: row 0 is "Powerful Map Boss", row 66 is whatever
/// 0xA6 is, and every name and sentence between them matches the file or the rule is wrong.
///
/// THE EFFECT IDS ARE A DIFFERENT NUMBER ENTIRELY, and this walk tests that in the same pass. They
/// run 1240..26741, which is nothing like this table's height and very like Stats.dat's 27281 rows
/// - and every row here carries a Stats array. Resolving those entries to row INDICES settles it
/// without reading one stat id: either the file's effect numbers turn up among them or they do not.
///
/// RESOLVED BY GRID, NOT BY NAME. A stat entry is a pointer into Stats.dat, and what makes it an
/// index is that it lands exactly on that table's row grid (DatTableFacts.IndexOf). That check
/// costs nothing and cannot be passed by a pointer at something else, which is the property the
/// string-reading version of this would not have had.
///
/// TWO OUTLIERS ARE THE INTERESTING PART. 0x3E8 and 0x6157 are badges in the file and fit no row
/// under the +100 rule - they would be rows 900 and 24819 - and 0x6157 is ALSO one of its effects,
/// with the same sentence and no name. If the effect ids are stat rows, then those two were filed
/// as badges by a port that saw them on a badge element, and the file has been carrying one entry
/// under two headings.
/// </remarks>
public sealed class EndgameMapContentCatalogue
{
    /// <summary>Most rows believed. Beyond this the table pointer is not a table.</summary>
    private const int MostRows = 4096;

    /// <summary>Most bytes one row may take, for the same reason.</summary>
    private const int LargestRow = 0x1000;

    /// <summary>
    /// Smallest row this walks. The columns it reads end at 0x54, so a row that cannot hold them
    /// is not this table however confidently it named itself.
    /// </summary>
    private const int SmallestRow = 0x5C;

    /// <summary>Rows read per call. This table is small, so one block is the whole of it.</summary>
    private const int RowsPerRead = 64;

    /// <summary>Longest string taken seriously. The sentences are long; the paths are longer.</summary>
    private const int MostChars = 160;

    /// <summary>Most stats one content may grant. A guard on a count that comes from memory.</summary>
    private const int MostStats = 64;

    /// <summary>Bytes of one entry of an array of foreign references: the row, then its table.</summary>
    private const ulong EntrySize = 16;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    private readonly int _id;
    private readonly int _stats;
    private readonly int _description;
    private readonly int _name;
    private readonly int _visual;
    private readonly int _visualId;
    private readonly int _visualIcon;

    private List<MapContentRow> _rows = [];

    public EndgameMapContentCatalogue(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _tables = DatTableShape.From(schema);

        StructDef content = schema.Structs["EndgameMapContentRow"];
        _id = content.OffsetOf("IdPtr");
        _stats = content.OffsetOf("StatsArray");
        _description = content.OffsetOf("DescriptionPtr");
        _name = content.OffsetOf("NamePtr");
        _visual = content.OffsetOf("VisualIdentityRef");
        BadgeIdBase = (uint)content.Constants["BadgeIdBase"];

        StructDef art = schema.Structs["EndgameMapContentVisualIdentityRow"];
        _visualId = art.OffsetOf("IdPtr");
        _visualIcon = art.OffsetOf("AtlasIconPtr");
    }

    /// <summary>What a row's index is added to before it is a badge id. See AtlasNode.BadgeVectorBegin.</summary>
    public uint BadgeIdBase { get; }

    /// <summary>What the table said about itself, or null when it has not been read.</summary>
    public DatTableFacts? Table { get; private set; }

    /// <summary>What Stats.dat said about itself, when a row referenced it. Null means no row did.</summary>
    public DatTableFacts? StatsTable { get; private set; }

    /// <summary>Every row, in table order.</summary>
    public IReadOnlyList<MapContentRow> Rows => _rows;

    /// <summary>Why the last read found nothing, when it did.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Reads the whole table, once. Later calls with the same table are ignored.
    /// </summary>
    /// <param name="table">
    /// The table OBJECT - the second half of a dat foreign reference, not a row.
    /// EndgameMapCatalogue picks one up while striding its own rows.
    /// </param>
    public bool Read(ulong table)
    {
        if (_rows.Count > 0)
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
            // Never over a table already identified: a caller trying a second route must not
            // replace "read, and the rows are empty" with "that address is not a table".
            LastError = Table is null ? $"0x{table:X} does not read as a dat table" : LastError;
            return false;
        }

        Table = facts;
        if (facts.Rows is <= 0 or > MostRows || facts.RowSize is < SmallestRow or > LargestRow)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows of 0x{facts.RowSize:X}, which is not a table this walks";
            return false;
        }

        // The art rows repeat - several contents share one picture - so the two strings behind one
        // are read once per ROW ADDRESS rather than once per content.
        var art = new Dictionary<ulong, (string Id, string Path)>();
        var rows = new List<MapContentRow>((int)facts.Rows);

        var block = new byte[RowsPerRead * (int)facts.RowSize];
        for (long first = 0; first < facts.Rows; first += RowsPerRead)
        {
            int count = (int)Math.Min(RowsPerRead, facts.Rows - first);
            Span<byte> span = block.AsSpan(0, count * (int)facts.RowSize);
            if (!_reader.TryRead(facts.RowsBegin + (ulong)(first * facts.RowSize), span))
            {
                continue; // a page that would not read; the rest of the table still can
            }

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> row = span[(i * (int)facts.RowSize)..];
                ulong visual = BinaryPrimitives.ReadUInt64LittleEndian(row[_visual..]);
                if (!art.TryGetValue(visual, out (string Id, string Path) picture))
                {
                    picture = MemoryReaderExtensions.IsPlausiblePointer(visual)
                        ? (Text(_reader.ReadPointer(visual + (ulong)_visualId)),
                           Text(_reader.ReadPointer(visual + (ulong)_visualIcon)))
                        : (string.Empty, string.Empty);
                    art[visual] = picture;
                }

                rows.Add(new MapContentRow(
                    first + i,
                    Text(BinaryPrimitives.ReadUInt64LittleEndian(row[_id..])),
                    Text(BinaryPrimitives.ReadUInt64LittleEndian(row[_name..])),
                    Text(BinaryPrimitives.ReadUInt64LittleEndian(row[_description..])),
                    picture.Id,
                    picture.Path,
                    Stats(row)));
            }
        }

        _rows = rows;
        if (rows.Count == 0)
        {
            LastError = $"{facts.Label} reports {facts.Rows} rows and none of them read";
        }

        return rows.Count > 0;
    }

    /// <summary>
    /// The Stats column, as row INDICES rather than as addresses.
    /// </summary>
    /// <remarks>
    /// The index is the whole point: the file's effect ids are numbers in Stats.dat's range, so a
    /// number is what has to come back to compare them. An entry that does not land on the table's
    /// grid is dropped rather than rounded - see DatTableFacts.IndexOf.
    /// </remarks>
    private List<long> Stats(ReadOnlySpan<byte> row)
    {
        var stats = new List<long>();
        if (row.Length < _stats + 16)
        {
            return stats;
        }

        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(row[_stats..]);
        ulong entries = BinaryPrimitives.ReadUInt64LittleEndian(row[(_stats + 8)..]);
        if (count is 0 or > MostStats || !MemoryReaderExtensions.IsPlausiblePointer(entries))
        {
            return stats;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong stat = _reader.ReadPointer(entries + (i * EntrySize));
            if (!MemoryReaderExtensions.IsPlausiblePointer(stat))
            {
                continue;
            }

            // The table travels with the first entry that needs it. Describing it once is what
            // turns every later pointer into an index for the price of a division.
            if (StatsTable is null && _tables is { } shape)
            {
                StatsTable = PointerPeek.DescribeTable(_reader, _reader.ReadPointer(entries + (i * EntrySize) + 8), shape);
            }

            long index = StatsTable?.IndexOf(stat) ?? -1;
            if (index >= 0)
            {
                stats.Add(index);
            }
        }

        return stats;
    }

    private string Text(ulong at)
        => MemoryReaderExtensions.IsPlausiblePointer(at) ? _reader.ReadUnicodeString(at, MostChars) : string.Empty;

    /// <summary>
    /// What the table holds, and how much of data/atlas-content.json it could replace.
    /// </summary>
    public IReadOnlyList<string> Describe(AtlasContentNames file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var said = new List<string>();
        if (Table is not { } facts)
        {
            said.Add($"ENDGAMEMAPCONTENT - not read: {(LastError.Length > 0 ? LastError : "no table seen yet")}");
            return said;
        }

        said.Add($"ENDGAMEMAPCONTENT - \"{facts.Path}\", {facts.Rows} rows of 0x{facts.RowSize:X}, {_rows.Count} read");
        if (LastError.Length > 0)
        {
            said.Add($"  {LastError}");
        }

        said.AddRange(Badges(file));
        said.AddRange(Effects(file));
        said.AddRange(Icons(file));
        return said;
    }

    /// <summary>Whether a row plus 100 is the badge the file calls by that number.</summary>
    private IEnumerable<string> Badges(AtlasContentNames file)
    {
        int matched = 0;
        int worded = 0;
        var missing = new List<string>();
        foreach (MapContentRow row in _rows)
        {
            if (file.Badge((uint)row.Index + BadgeIdBase) is not { } badge)
            {
                continue;
            }

            matched++;
            if (string.Equals(badge.Name, row.Name, StringComparison.Ordinal)
                && string.Equals(badge.Description, row.Description, StringComparison.Ordinal))
            {
                worded++;
            }
        }

        foreach ((uint id, AtlasContent _) in file.Badges)
        {
            long index = id - (long)BadgeIdBase;
            if (index < 0 || index >= _rows.Count)
            {
                missing.Add($"0x{id:X}");
            }
        }

        yield return $"  badges: the file has {file.Badges.Count}; {matched} of them are a row+{BadgeIdBase},"
            + $" and {worded} of those agree on BOTH the name and the sentence";
        if (missing.Count > 0)
        {
            yield return $"    {missing.Count} the rule does not reach: {string.Join(", ", missing.Order(StringComparer.Ordinal))}";
        }

        // The rows the file has never heard of are the other direction, and the reason to read the
        // table at all: an id the game knows and the shipped file does not is a gap, not a mismatch.
        var unknown = _rows.Where(row => file.Badge((uint)row.Index + BadgeIdBase) is null).ToList();
        yield return unknown.Count == 0
            ? "    every row is in the file"
            : $"    {unknown.Count} rows the file does not name, first few:";
        foreach (MapContentRow row in unknown.Take(8))
        {
            yield return $"      row {row.Index,3} = 0x{row.Index + BadgeIdBase:X}  {row.Id}  \"{row.Name}\"  {row.Description}";
        }
    }

    /// <summary>Whether the file's effect ids are the stat rows these contents grant.</summary>
    private IEnumerable<string> Effects(AtlasContentNames file)
    {
        var granted = new HashSet<long>();
        foreach (MapContentRow row in _rows)
        {
            foreach (long stat in row.Stats)
            {
                granted.Add(stat);
            }
        }

        if (StatsTable is not { } stats)
        {
            yield return "  effects: no row referenced Stats.dat, so the id hypothesis is untested here";
            yield break;
        }

        int found = file.Effects.Keys.Count(id => granted.Contains(id));
        yield return $"  effects: the file has {file.Effects.Count}; \"{stats.Path}\" holds {stats.Rows} rows"
            + $" and these contents grant {granted.Count} distinct ones";
        yield return $"    {found} of the file's effect ids are among them"
            + (found == 0 ? " - so an effect id is NOT one of these stat rows" : string.Empty);
    }

    /// <summary>What the art rows add to the names the file ships.</summary>
    private IEnumerable<string> Icons(AtlasContentNames file)
    {
        int pathed = _rows.Count(row => row.IconPath.Length > 0);
        int knownToFile = _rows.Count(row => row.Icon.Length > 0 && file.Icons.Contains(row.Icon));

        yield return $"  icons: {pathed} rows carry a whole art path, and {knownToFile} of their art ids"
            + " are names the file already ships";
        foreach (MapContentRow row in _rows.Where(row => row.IconPath.Length > 0).Take(3))
        {
            yield return $"      {row.Icon} -> {row.IconPath}";
        }
    }
}
