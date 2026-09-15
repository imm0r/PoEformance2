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

    /// <summary>Longest string taken seriously, across however many steps it takes.</summary>
    private const int MostChars = 512;

    /// <summary>
    /// How much of a string is asked for at once, and the number is not arbitrary.
    /// </summary>
    /// <remarks>
    /// A BIGGER REQUEST CAN RETURN LESS TEXT, which is the trap this exists to avoid.
    /// ReadUnicodeString halves its request until a read succeeds, so against a REPLAY that
    /// recorded 160 characters, asking for 512 fails, 256 fails, and 128 succeeds - the larger ask
    /// yields a string a third shorter than the smaller one would have. Two of the 70 descriptions
    /// on the 0.5.5 capture came back cut mid-markup that way ("Rogue Exiles are [Sp"), which
    /// compares unequal to the shipped file and reads as the GAME disagreeing rather than as the
    /// read falling short.
    ///
    /// So the first step is exactly what the committed recordings hold, and a client with more to
    /// give simply gets asked again. 160 is that size.
    /// </remarks>
    private const int StepChars = 160;

    /// <summary>Most stats one content may grant. A guard on a count that comes from memory.</summary>
    private const int MostStats = 64;

    /// <summary>Bytes of one entry of an array of foreign references: the row, then its table.</summary>
    private const ulong EntrySize = 16;

    /// <summary>How far either way <see cref="Drift"/> looks for a stale stat index.</summary>
    private const int DriftWindow = 8;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    private readonly int _id;
    private readonly int _stats;
    private readonly int _description;
    private readonly int _name;
    private readonly int _visual;
    private readonly int _visualId;
    private readonly int _visualIcon;
    private readonly int _statId;

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
        StatTokenBase = (uint)content.Constants["StatTokenBase"];
        _statId = schema.Structs["StatsRow"].OffsetOf("IdPtr");

        StructDef art = schema.Structs["EndgameMapContentVisualIdentityRow"];
        _visualId = art.OffsetOf("IdPtr");
        _visualIcon = art.OffsetOf("AtlasIconPtr");
    }

    /// <summary>What a row's index is added to before it is a badge id. See AtlasNode.BadgeVectorBegin.</summary>
    public uint BadgeIdBase { get; }

    /// <summary>
    /// What a STAT's row index is added to before it is the token a node carries. One.
    /// </summary>
    /// <remarks>
    /// THE OFF-BY-ONE THAT MADE THE FIRST VERSION OF THIS DO NOTHING, and it was invisible because
    /// everything still read and every number still looked plausible: the learnt effects were filed
    /// under the row index, the nodes ask with the index plus one, and so not one lookup ever hit.
    ///
    /// The six biomes prove the direction rather than merely suggesting it. Those contents grant
    /// stat indices 25861..25866; real nodes carry tokens 25862..25867, and 25861 appears on no
    /// node in any of four captures. Across all 83 stats these contents grant, 61 turn up as token
    /// s+1 and 15 as token s - and those 15 are exactly the ones whose neighbour is also granted,
    /// which is overlap, not counter-evidence.
    /// </remarks>
    public uint StatTokenBase { get; }

    /// <summary>The token a node would carry for a stat this walk resolved to <paramref name="index"/>.</summary>
    public uint TokenFor(long index) => (uint)index + StatTokenBase;

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

    /// <summary>One string, in steps, so a long one is not paid for by a short read. See <see cref="StepChars"/>.</summary>
    private string Text(ulong at)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return string.Empty;
        }

        string first = _reader.ReadUnicodeString(at, StepChars);

        // Short of the step means the terminator was inside it, which is the usual case and the
        // whole string. Only a full step is ambiguous, and only that asks again.
        if (first.Length < StepChars)
        {
            return first;
        }

        var built = new System.Text.StringBuilder(first);
        for (int taken = StepChars; taken < MostChars; taken += StepChars)
        {
            string next = _reader.ReadUnicodeString(at + ((ulong)taken * sizeof(char)), StepChars);
            built.Append(next);
            if (next.Length < StepChars)
            {
                break;
            }
        }

        return built.ToString();
    }

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

        said.Add(file.Revision == 0
            ? "  the shipped file is still in force - nothing has been learnt from this table yet"
            : $"  IN FORCE: {file.LearntBadges} badges and {file.LearntEffects} effects from the game,"
              + " the file behind them. Everything below compares the FILE against the game.");

        said.AddRange(Badges(file));
        said.AddRange(Effects(file));
        said.AddRange(Icons(file));
        return said;
    }

    /// <summary>
    /// The game's wording as the file would have written it: markup out, one space between words.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES ARE NEEDED AND EACH WAS LEARNT THE HARD WAY. The game writes "[Biome|Water]",
    /// so comparing raw text against a file that ships the display half disagrees on every line
    /// that mentions anything - 60 of 67 rows "differed" until this was stripped. And the game
    /// separates a content's clauses with NEWLINES where the published copies joined them with a
    /// space, which is a difference in punctuation rather than in wording.
    /// </remarks>
    public static string AsTheFileWouldWriteIt(string text)
        => string.Join(' ', Files.KeywordGlossary.Plain(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Whether a row plus 100 is the badge the FILE calls by that number.
    /// </summary>
    /// <remarks>
    /// AGAINST file.Badges AND NEVER file.Badge(), which is a distinction that cost a whole report.
    /// The lookup answers with whatever is IN FORCE - which, once the learning has run, is this very
    /// table - so a comparison built on it compares the game against itself and agrees perfectly:
    /// "the file has 69; 70 of them are a row+100, 70 agree on the name and 70 on the sentence", on
    /// a file that has 69 entries and disagrees about six of them. A check a wrong value passes is
    /// worse than no check, and this one passed because it had stopped reading the file at all.
    /// </remarks>
    private IEnumerable<string> Badges(AtlasContentNames file)
    {
        int matched = 0;
        int named = 0;
        int worded = 0;
        var differs = new List<MapContentRow>();
        foreach (MapContentRow row in _rows)
        {
            if (!file.Badges.TryGetValue((uint)row.Index + BadgeIdBase, out AtlasContent badge))
            {
                continue;
            }

            matched++;
            bool sameName = string.Equals(badge.Name, row.Name, StringComparison.Ordinal);
            bool sameWords = string.Equals(
                AsTheFileWouldWriteIt(badge.Description),
                AsTheFileWouldWriteIt(row.Description),
                StringComparison.Ordinal);

            named += sameName ? 1 : 0;
            worded += sameWords ? 1 : 0;
            if (!sameName || !sameWords)
            {
                differs.Add(row);
            }
        }

        var missing = new List<string>();
        foreach ((uint id, AtlasContent _) in file.Badges)
        {
            long index = id - (long)BadgeIdBase;
            if (index < 0 || index >= _rows.Count)
            {
                missing.Add($"0x{id:X}");
            }
        }

        yield return $"  badges: the file has {file.Badges.Count}; {matched} of them are a row+{BadgeIdBase},"
            + $" {named} agree on the name and {worded} on the sentence";
        foreach (MapContentRow row in differs.Take(8))
        {
            AtlasContent badge = file.Badges[(uint)row.Index + BadgeIdBase];
            yield return $"      0x{row.Index + BadgeIdBase:X} {row.Id}";
            yield return $"        file: \"{badge.Name}\" / {badge.Description}";
            yield return $"        game: \"{row.Name}\" / {AsTheFileWouldWriteIt(row.Description)}";
        }

        if (missing.Count > 0)
        {
            yield return $"    {missing.Count} the rule does not reach: {string.Join(", ", missing.Order(StringComparer.Ordinal))}";
        }

        // The rows the file has never heard of are the other direction, and the reason to read the
        // table at all: an id the game knows and the shipped file does not is a gap, not a mismatch.
        var unknown = _rows.Where(row => !file.Badges.ContainsKey((uint)row.Index + BadgeIdBase)).ToList();
        yield return unknown.Count == 0
            ? "    every row is in the file"
            : $"    {unknown.Count} rows the file does not name, first few:";
        foreach (MapContentRow row in unknown.Take(8))
        {
            yield return $"      row {row.Index,3} = 0x{row.Index + BadgeIdBase:X}  {row.Id}  \"{row.Name}\"  {row.Description}";
        }
    }

    /// <summary>Whether the file's effect ids are the stat rows these contents grant.</summary>
    /// <remarks>
    /// COMPARED AS TOKENS, because that is what a lookup is given. The stat's row index plus one is
    /// the number a node carries, and measuring the file against the raw index instead reports a
    /// drift one short of the real one - which is how a +5 came back as a +4.
    /// </remarks>
    private IEnumerable<string> Effects(AtlasContentNames file)
    {
        var granted = new HashSet<long>();
        foreach (MapContentRow row in _rows)
        {
            foreach (long stat in row.Stats)
            {
                granted.Add(TokenFor(stat));
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
        yield return $"    {found} of the file's effect ids are among them as they stand"
            + (found == 0 ? " - so an effect id is NOT one of these stat rows" : string.Empty);

        // THE DRIFT IS FOUND RATHER THAN ASSUMED. A stat index is a POSITION, so inserting rows
        // moves every later one - and a file built against an older client keeps the old numbers.
        // Trying a window of shifts is what turned "9 of 43, could be chance" into a measurement:
        // a wrong shift finds nothing, and the right one finds more than no shift at all.
        (int shift, int hits) = Drift(file, granted);
        if (shift != 0)
        {
            yield return $"    {hits} are, shifted by {shift:+#;-#;0} - which is what a stale index looks like"
                + " after the game inserted rows above them";
        }

        foreach (string line in EveryFileId(file, shift))
        {
            yield return line;
        }
    }

    /// <summary>
    /// What the game calls the stat behind each of the file's effect ids, and behind the drifted one.
    /// </summary>
    /// <remarks>
    /// THE PER-ENTRY ANSWER, where the shift above is only the population's. A shift found over 43
    /// ids says nothing about any ONE of them, and most of the file's entries name stats no map
    /// content grants - so they can neither confirm nor deny it from the contents alone. Their own
    /// Stats row can: if the file's id lands on a stat whose engine name matches its sentence, that
    /// entry is still right; if the name at id+shift matches instead, that one has drifted too.
    /// This is the table somebody reads to correct the file entry by entry rather than in bulk.
    /// </remarks>
    private IEnumerable<string> EveryFileId(AtlasContentNames file, int shift)
    {
        uint[] ids = [.. file.Effects.Keys.Order()];
        if (ids.Length == 0 || StatsTable is null)
        {
            yield break;
        }

        IReadOnlyDictionary<uint, string> here = NameStats(ids);
        IReadOnlyDictionary<uint, string> there = shift == 0
            ? here
            : NameStats(ids.Select(id => (uint)(id + shift)));

        yield return $"    what the game calls each of the file's {ids.Length} effect ids, and the one"
            + $" {shift:+#;-#;0} from it:";
        foreach (uint id in ids)
        {
            string mine = here.GetValueOrDefault(id, "-");
            string drifted = shift == 0 ? string.Empty : $"   {id + shift} = {there.GetValueOrDefault((uint)(id + shift), "-")}";
            yield return $"      {id,6} = {mine}{drifted}"
                + $"   \"{file.Effects[id].Description}\"";
        }
    }

    /// <summary>The shift that makes most of the file's effect ids land on a granted stat.</summary>
    /// <remarks>
    /// A SMALL WINDOW ON PURPOSE. Wide enough to find an insertion of a few rows, narrow enough
    /// that it cannot wander until something lines up: a table of 27281 rows will eventually agree
    /// with anything, and a "best shift" found over hundreds would be numerology rather than drift.
    /// </remarks>
    private static (int Shift, int Hits) Drift(AtlasContentNames file, HashSet<long> granted)
    {
        (int Shift, int Hits) best = (0, file.Effects.Keys.Count(id => granted.Contains(id)));
        for (int shift = -DriftWindow; shift <= DriftWindow; shift++)
        {
            if (shift == 0)
            {
                continue;
            }

            int hits = file.Effects.Keys.Count(id => granted.Contains(id + shift));
            if (hits > best.Hits)
            {
                best = (shift, hits);
            }
        }

        return best;
    }

    /// <summary>
    /// What the game calls the stats behind a set of node tokens - <c>map_extra_shrines</c> and the like.
    /// </summary>
    /// <remarks>
    /// THE ONLY THING THAT CAN SETTLE THE FILE'S REMAINING EFFECT IDS, and it needs no particular
    /// state in the game: an effect id IS a Stats row, so reading that row's own id says which stat
    /// the number means on THIS client. Comparing the id the file expects against the id the game
    /// has at that row is what turns "probably drifted like the others" into a measurement, one
    /// entry at a time - and for the many tokens the file has never heard of, a name beats a number.
    ///
    /// ONE ROW PER TOKEN ASKED FOR, not a walk of the table: Stats has 27281 rows and reading every
    /// id would be twenty-seven thousand string reads for the eighty a session actually sees.
    /// </remarks>
    /// <param name="tokens">The raw tokens, or their masked ids; both are accepted.</param>
    public IReadOnlyDictionary<uint, string> NameStats(IEnumerable<uint> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var named = new Dictionary<uint, string>();
        if (StatsTable is not { } stats || stats.RowSize <= 0)
        {
            return named;
        }

        foreach (uint raw in tokens)
        {
            uint token = AtlasContentNames.IdOf(raw);
            if (named.ContainsKey(token) || token < StatTokenBase)
            {
                continue;
            }

            long index = token - StatTokenBase;
            if (index >= stats.Rows)
            {
                continue;   // a token past the end of the table is not a stat on this client
            }

            string id = Text(_reader.ReadPointer(
                stats.RowsBegin + (ulong)(index * stats.RowSize) + (ulong)_statId));
            if (id.Length > 0)
            {
                named[token] = id;
            }
        }

        return named;
    }

    /// <summary>The last part of an art path, which is the name every published copy kept.</summary>
    public static string ArtName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>What the art rows add to the names the file ships.</summary>
    /// <remarks>
    /// THE FILE'S ICON IS THE PATH'S LAST SEGMENT AND NOT THE ART ROW'S OWN ID, which is worth
    /// saying because the two are different words: the row calls itself "Breach" and its path ends
    /// "AtlasIconContentBreach", and the file ships the second. So this compares the segment.
    /// </remarks>
    private IEnumerable<string> Icons(AtlasContentNames file)
    {
        var pathed = _rows.Where(row => row.IconPath.Length > 0).ToList();
        int agreeing = pathed.Count(row =>
            file.Badges.TryGetValue((uint)row.Index + BadgeIdBase, out AtlasContent badge)
            && string.Equals(badge.Icon, ArtName(row.IconPath), StringComparison.OrdinalIgnoreCase));

        yield return $"  icons: {pathed.Count} of {_rows.Count} rows carry a whole art path,"
            + $" and {agreeing} of those end in the very name the file ships";
        foreach (MapContentRow row in pathed.Take(3))
        {
            yield return $"      {row.Id} -> {row.IconPath}";
        }

        // The other direction is the interesting one: a row the game gives NO picture for, that the
        // file names an icon for anyway, is a name from somewhere other than this table.
        var invented = _rows
            .Where(row => row.IconPath.Length == 0
                && file.Badges.TryGetValue((uint)row.Index + BadgeIdBase, out AtlasContent badge)
                && badge.Icon.Length > 0)
            .ToList();
        if (invented.Count > 0)
        {
            yield return $"    {invented.Count} rows have no art path at all, yet the file names an icon for them:";
            foreach (MapContentRow row in invented.Take(4))
            {
                yield return $"      0x{row.Index + BadgeIdBase:X} {row.Id} -> file says"
                    + $" \"{file.Badges[(uint)row.Index + BadgeIdBase].Icon}\"";
            }
        }
    }
}
