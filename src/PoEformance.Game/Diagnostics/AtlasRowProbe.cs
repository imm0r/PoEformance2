using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Sweeps the EndgameMapAtlas row behind EVERY atlas node, instead of the one AtlasNodeProbe
/// happened to reach.
/// </summary>
/// <remarks>
/// WHY A SWEEP AND NOT ANOTHER SAMPLE. AtlasNodeProbe reads AtlasNodeData.AtlasRowPtr on two
/// nodes, so three whole captures between them contain the row for exactly ONE - MapRugosa's -
/// and everything believed about that column rests on it. That one row says the node's Passives
/// column is AtlasOutsideFortressPath72, a PATH node of the Atlas Skills tree whose name is a
/// [DNT] placeholder. Two questions follow and neither can be answered by looking at it again:
/// does +0x2A0 reach this table on most nodes at all, and do different maps get different rows?
/// A recording can only hold reads the build performed, so the sweep has to exist before the
/// capture that answers them.
///
/// WHAT IT READS BESIDES. The Passives link is the puzzle, but it is not the interesting column.
/// MapObjective carries ObjectiveText and CompletionText - the game's own words for what a node
/// asks of a player, localised by the client - and BlockedMessage says why a position cannot be
/// entered. Those are the columns something might one day SHOW, so they are read here first, and
/// the answer to "is there anything worth showing" comes back in the same capture as the answer
/// about Passives rather than needing a second one.
///
/// IT IS CHEAP BECAUSE IT CACHES BY ROW ADDRESS. The sweep proper is two reads per node; the
/// columns behind a row are read ONCE per distinct row however many nodes share it. If positions
/// turn out to be shared the detail cost collapses to almost nothing, and if they do not, that
/// fact is itself the finding. It runs from the atlas panel's debug log, where a person is
/// already waiting, and never on the drawing path.
/// </remarks>
public sealed class AtlasRowProbe
{
    /// <summary>Most distinct rows whose columns get read. The table has 1242.</summary>
    private const int MostRows = 1536;

    /// <summary>How many rows get the full column treatment, with every string followed.</summary>
    /// <remarks>
    /// The distribution wants every row; the detail only wants enough to see the shape. Six is
    /// two more than it takes to notice that they all say the same thing.
    /// </remarks>
    private const int MostDetailed = 6;

    /// <summary>Longest string taken seriously behind any of these columns.</summary>
    private const int MostChars = 160;

    /// <summary>Most stats on one position. A guard on a count that comes from memory.</summary>
    private const int MostStats = 32;

    /// <summary>Bytes one array entry takes: a row reference followed by its table.</summary>
    private const int EntrySize = 0x10;

    /// <summary>Most distinct sentences listed. The vocabularies behind these ids are tiny.</summary>
    private const int MostSentences = 24;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    /// <summary>Resolved string columns, by the row they came from. See <see cref="Id"/>.</summary>
    private readonly Dictionary<(ulong Row, int Column), string> _strings = [];

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _atlasRow;

    private readonly int _passives;
    private readonly int _stats;
    private readonly int _statValues;
    private readonly int _blocked;
    private readonly int _subTree;
    private readonly int _objective;
    private readonly int _quest;
    private readonly int _clientString;
    private readonly long _rowSize;

    private readonly int _passiveId;
    private readonly int _passiveName;
    private readonly int _passiveGraphId;
    private readonly int _objectiveId;
    private readonly int _objectiveText;
    private readonly int _completionText;
    private readonly int _stringId;
    private readonly int _stringText;
    private readonly int _subTreeId;
    private readonly int _statId;

    public AtlasRowProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _tables = DatTableShape.From(schema);

        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef row = schema.Structs["EndgameMapAtlasRow"];
        StructDef passive = schema.Structs["PassiveSkillsRow"];
        StructDef objective = schema.Structs["EndgameMapObjectivesRow"];
        StructDef text = schema.Structs["ClientStrings2Row"];
        StructDef subTree = schema.Structs["AtlasSubTreeRow"];
        StructDef stat = schema.Structs["StatsRow"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _atlasRow = data.OffsetOf("AtlasRowPtr");

        _passives = row.OffsetOf("PassivesRef");
        _stats = row.OffsetOf("StatsRef");
        _statValues = row.OffsetOf("StatValuesArray");
        _blocked = row.OffsetOf("BlockedMessageRef");
        _subTree = row.OffsetOf("SubTreeRef");
        _objective = row.OffsetOf("MapObjectiveRef");
        _quest = row.OffsetOf("QuestRef");
        _clientString = row.OffsetOf("ClientStringRef");
        _rowSize = row.Constants["ComputedRowSize"];

        _passiveId = passive.OffsetOf("IdPtr");
        _passiveName = passive.OffsetOf("NamePtr");
        _passiveGraphId = passive.OffsetOf("GraphId");
        _objectiveId = objective.OffsetOf("IdPtr");
        _objectiveText = objective.OffsetOf("ObjectiveTextPtr");
        _completionText = objective.OffsetOf("CompletionTextPtr");
        _stringId = text.OffsetOf("IdPtr");
        _stringText = text.OffsetOf("TextPtr");
        _subTreeId = subTree.OffsetOf("IdPtr");
        _statId = stat.OffsetOf("IdPtr");
    }

    /// <summary>Everything the sweep found, distribution first and samples after.</summary>
    public IReadOnlyList<string> Probe(IReadOnlyList<ProbedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var said = new List<string>();
        if (nodes.Count == 0)
        {
            return said;
        }

        said.Add(string.Empty);
        said.Add("ATLAS ROW SWEEP - the EndgameMapAtlas row behind every node, not just the first:");

        // Which rows exist, and which maps reach them. Both halves are the finding: a column that
        // only reads on a handful of nodes is a different fact from one that reads everywhere.
        var rows = new Dictionary<ulong, List<string>>();
        ulong table = 0;
        int reached = 0;
        foreach (ProbedNode node in nodes)
        {
            ulong storage = _reader.ReadPointer(node.Element + (ulong)_dataStorage);
            ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
                ? _reader.ReadPointer(storage + (ulong)_data)
                : 0;
            if (!MemoryReaderExtensions.IsPlausiblePointer(data))
            {
                continue;
            }

            ulong row = _reader.ReadPointer(data + (ulong)_atlasRow);
            if (!MemoryReaderExtensions.IsPlausiblePointer(row))
            {
                continue;
            }

            reached++;
            if (table == 0)
            {
                // The table half of the foreign reference, read once: it is what NAMES the table
                // and states its row size, and every row after this one belongs to the same one.
                table = _reader.ReadPointer(data + (ulong)_atlasRow + 8);
            }

            (rows.TryGetValue(row, out List<string>? maps) ? maps : rows[row] = []).Add(node.MapId);
        }

        said.Add($"  {nodes.Count} nodes, {reached} with a readable atlas row, {rows.Count} distinct rows");
        if (reached == 0)
        {
            // Worded carefully because the obvious reading is wrong half the time. ON A REPLAY a
            // zero here almost always means the RECORDING lacks those reads - a recording holds
            // only what its build performed, and the build before this probe read +0x2A0 on two
            // nodes - not that the game has nothing there. That exact inference was drawn once in
            // this project and reported as a finding before being caught.
            said.Add("  nothing reached the table. LIVE that means +0x2A0 is not this column on this"
                + " client, or these nodes carry no position row. ON A REPLAY it means the recording"
                + " never read it, which is not a fact about the game - take a fresh capture.");
            return said;
        }

        said.AddRange(Table(table, rows.Count));
        said.AddRange(Spread(rows));
        said.AddRange(Columns(rows));
        return said;
    }

    /// <summary>What the table says about itself, checked against the arithmetic.</summary>
    private List<string> Table(ulong table, int distinct)
    {
        var said = new List<string>();
        if (_tables is not { } shape || PointerPeek.DescribeTable(_reader, table, shape) is not { } facts)
        {
            said.Add($"  the table half at +0x{_atlasRow + 8:X} reads 0x{table:X}, which does not describe a dat table"
                + " - so the rows below are not vouched for by anything");
            return said;
        }

        said.Add($"  table \"{facts.Path}\": {facts.Rows} rows of 0x{facts.RowSize:X}, computed 0x{_rowSize:X}"
            + $" - {(facts.RowSize == _rowSize ? "AGREE" : "DISAGREE, and every column offset below is then wrong")}");
        said.Add($"  {distinct} of those {facts.Rows} rows are reachable from a node on this atlas");
        return said;
    }

    /// <summary>Whether a row belongs to one map or to many - the question a sample cannot answer.</summary>
    private static List<string> Spread(Dictionary<ulong, List<string>> rows)
    {
        int shared = 0, distinctMaps = 0;
        foreach (List<string> maps in rows.Values)
        {
            if (maps.Count > 1)
            {
                shared++;
            }

            if (maps.Distinct(StringComparer.Ordinal).Count() > 1)
            {
                distinctMaps++;
            }
        }

        return
        [
            $"  {shared} rows are reached by more than one node, {distinctMaps} by nodes of DIFFERENT maps"
                + " - a row shared across maps is a position, a row per map is a map property",
        ];
    }

    /// <summary>The columns themselves: the distribution over every row, then a few in full.</summary>
    /// <remarks>
    /// THE TEXTS ARE READ FOR EVERY ROW, not only for the few printed whole, and that is a fix
    /// rather than a flourish. The first capture of this sweep read the ids everywhere and the
    /// TEXT only for six arbitrary rows - which happened to be six path positions carrying no
    /// objective at all - so the recording came back with every objective id and not one of the
    /// sentences behind them. A recording holds only the reads its build performed, so "read it
    /// where it is interesting" has to mean everywhere the column is set.
    /// It costs almost nothing because the referenced rows REPEAT: 113 positions share seven
    /// objectives, so the cache turns that into seven reads.
    /// </remarks>
    private List<string> Columns(Dictionary<ulong, List<string>> rows)
    {
        var said = new List<string>();
        var passives = new Dictionary<string, int>(StringComparer.Ordinal);
        var objectives = new Dictionary<string, int>(StringComparer.Ordinal);
        var subTrees = new Dictionary<string, int>(StringComparer.Ordinal);
        var sentences = new Dictionary<string, int>(StringComparer.Ordinal);
        int withObjective = 0, withBlocked = 0, withStats = 0;

        foreach (ulong row in rows.Keys.Take(MostRows))
        {
            string passive = Id(row + (ulong)_passives, _passiveId);
            if (passive.Length > 0)
            {
                passives[passive] = passives.GetValueOrDefault(passive) + 1;
            }

            string objective = Id(row + (ulong)_objective, _objectiveId);
            if (objective.Length > 0)
            {
                objectives[objective] = objectives.GetValueOrDefault(objective) + 1;
                withObjective++;
                Sentence(sentences, $"{objective} / objective", Id(row + (ulong)_objective, _objectiveText));
                Sentence(sentences, $"{objective} / completion", Id(row + (ulong)_objective, _completionText));
            }

            string subTree = Id(row + (ulong)_subTree, _subTreeId);
            if (subTree.Length > 0)
            {
                subTrees[subTree] = subTrees.GetValueOrDefault(subTree) + 1;
            }

            string blocked = Id(row + (ulong)_blocked, _stringId);
            if (blocked.Length > 0)
            {
                withBlocked++;
                Sentence(sentences, $"{blocked} / blocked", Id(row + (ulong)_blocked, _stringText));
            }

            string extra = Id(row + (ulong)_clientString, _stringId);
            if (extra.Length > 0)
            {
                Sentence(sentences, $"{extra} / clientString", Id(row + (ulong)_clientString, _stringText));
            }

            if (Count(row + (ulong)_stats) > 0)
            {
                withStats++;
            }
        }

        said.Add($"  of the rows read: {withObjective} carry a MapObjective, {withBlocked} a BlockedMessage,"
            + $" {withStats} at least one Stat");
        said.Add($"  {passives.Count} distinct Passives ids, {objectives.Count} distinct objectives,"
            + $" {subTrees.Count} distinct sub-trees");
        said.AddRange(Top("Passives", passives));
        said.AddRange(Top("MapObjective", objectives));
        said.AddRange(Top("SubTree", subTrees));

        // The sentences themselves, which is the half a count can never stand in for: whether
        // these columns hold something a person could read, or only more engine ids.
        if (sentences.Count > 0)
        {
            said.Add($"  {sentences.Count} distinct strings behind those ids:");
            foreach ((string what, int count) in sentences.OrderByDescending(pair => pair.Value).Take(MostSentences))
            {
                said.Add($"      {count,5}x  {what}");
            }
        }

        // And a few rows whole, because a count says how often and never what. The ones carrying
        // something come FIRST - taking whatever the dictionary happened to yield first is what
        // sent the last capture back with six empty path positions and no sentences at all.
        foreach ((ulong row, List<string> maps) in rows
            .OrderByDescending(pair => Id(pair.Key + (ulong)_objective, _objectiveId).Length > 0)
            .ThenByDescending(pair => Id(pair.Key + (ulong)_blocked, _stringId).Length > 0)
            .Take(MostDetailed))
        {
            said.Add($"  row 0x{row:X}  {maps.Count} node(s), e.g. {(maps[0].Length > 0 ? maps[0] : "(no id)")}");
            said.Add($"    Passives     {ProbeBytes.Text(_reader, Ref(row + (ulong)_passives) + (ulong)_passiveId)}"
                + $"  {Named(Ref(row + (ulong)_passives), _passiveName)}"
                + $"  GraphId {(_reader.TryRead(Ref(row + (ulong)_passives) + (ulong)_passiveGraphId, out ushort g) ? g : 0)}");
            said.Add($"    MapObjective {ProbeBytes.Text(_reader, Ref(row + (ulong)_objective) + (ulong)_objectiveId)}");
            said.Add($"      objective  {Named(Ref(row + (ulong)_objective), _objectiveText)}");
            said.Add($"      completion {Named(Ref(row + (ulong)_objective), _completionText)}");
            said.Add($"    Blocked      {ProbeBytes.Text(_reader, Ref(row + (ulong)_blocked) + (ulong)_stringId)}"
                + $"  {Named(Ref(row + (ulong)_blocked), _stringText)}");
            said.Add($"    ClientString {ProbeBytes.Text(_reader, Ref(row + (ulong)_clientString) + (ulong)_stringId)}"
                + $"  {Named(Ref(row + (ulong)_clientString), _stringText)}");
            said.Add($"    SubTree      {ProbeBytes.Text(_reader, Ref(row + (ulong)_subTree) + (ulong)_subTreeId)}");
            said.Add($"    Quest row    0x{Ref(row + (ulong)_quest):X}");
            said.AddRange(Stats(row));
        }

        return said;
    }

    /// <summary>The Stats/StatValues pair, which is what a position actually grants.</summary>
    private List<string> Stats(ulong row)
    {
        long count = Count(row + (ulong)_stats);
        if (count <= 0)
        {
            return ["    Stats        none"];
        }

        ulong entries = _reader.ReadPointer(row + (ulong)_stats + 8);
        ulong values = _reader.ReadPointer(row + (ulong)_statValues + 8);
        var said = new List<string> { $"    Stats        {count}" };
        for (long i = 0; i < count; i++)
        {
            ulong statRow = _reader.ReadPointer(entries + (ulong)(i * EntrySize));
            string id = MemoryReaderExtensions.IsPlausiblePointer(statRow)
                ? _reader.ReadUnicodeString(_reader.ReadPointer(statRow + (ulong)_statId), MostChars)
                : string.Empty;

            said.Add(MemoryReaderExtensions.IsPlausiblePointer(values) && _reader.TryRead(values + (ulong)(i * 4), out int value)
                ? $"      {(id.Length > 0 ? id : "(no id)")} = {value}"
                : $"      {(id.Length > 0 ? id : "(no id)")} = (no value)");
        }

        return said;
    }

    /// <summary>The commonest few of a distribution, which is where a pattern shows.</summary>
    private static List<string> Top(string what, Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return [$"    {what}: nothing read"];
        }

        var said = new List<string> { $"    {what}, commonest first:" };
        foreach ((string id, int count) in counts.OrderByDescending(pair => pair.Value).Take(8))
        {
            said.Add($"      {count,5}x  {id}");
        }

        return said;
    }

    /// <summary>The row half of a dat foreign reference.</summary>
    private ulong Ref(ulong at) => _reader.ReadPointer(at);

    /// <summary>
    /// A string column of the row a reference points at, empty when it points nowhere.
    /// </summary>
    /// <remarks>
    /// CACHED BY (ROW, COLUMN) because the referenced rows repeat heavily - hundreds of positions
    /// resolve to seven objectives and six blocked messages - and a dat row never changes while
    /// the game runs. Without it, reading the sentences everywhere would cost a read per position
    /// instead of a read per distinct string.
    /// </remarks>
    private string Id(ulong reference, int column)
    {
        ulong row = _reader.ReadPointer(reference);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return string.Empty;
        }

        if (_strings.TryGetValue((row, column), out string? found))
        {
            return found;
        }

        string text = _reader.ReadUnicodeString(_reader.ReadPointer(row + (ulong)column), MostChars);
        _strings[(row, column)] = text;
        return text;
    }

    /// <summary>Records one sentence under the id it belongs to, when there is one.</summary>
    private static void Sentence(Dictionary<string, int> into, string what, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        string key = $"{what}: \"{text}\"";
        into[key] = into.GetValueOrDefault(key) + 1;
    }

    /// <summary>The same, printed the way the other probes print a string slot.</summary>
    private string Named(ulong row, int column)
        => MemoryReaderExtensions.IsPlausiblePointer(row)
            ? ProbeBytes.Text(_reader, row + (ulong)column)
            : "(no row)";

    /// <summary>The count half of a dat array column, guarded against a number out of memory.</summary>
    private long Count(ulong at)
    {
        ulong count = _reader.ReadPointer(at);
        return count is 0 or > MostStats ? 0 : (long)count;
    }
}
