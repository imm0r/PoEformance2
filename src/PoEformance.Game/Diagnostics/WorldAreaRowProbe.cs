using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Reads the WorldAreas row behind an atlas node, to find out whether this tool could stop
/// carrying <c>data/atlas-maps.json</c>.
/// </summary>
/// <remarks>
/// THE QUESTION. That file holds a name, a unique flag and a handful of tags for 440 maps, and
/// three things in this tool depend on it: the atlas groups (keyed by tag), the ritual watch
/// (unique, tower, hideout) and the ratings, which resolve a table of names to ids through it.
/// The name is already known to be readable - the WorldAreas row behind MapRugosa says "Rugosa"
/// at +0x08, measured. What is NOT known is everything the rest of that file would need, and
/// dat-schema says where it would be: IsMapArea 0xDC, IsHideout 0x12A, Tags 0x133,
/// IsUniqueMapArea 0x18B.
///
/// ALL OF THAT IS ARITHMETIC, NOT MEASUREMENT, and one number decides whether any of it is worth
/// believing: the row size. dat-schema computes 0x2E0 for WorldAreas' 80 columns, and a stale
/// comment in this project says 0x300. The game settles it without an opinion - a table states
/// its own rows and the span they occupy, so the row size divides out - and that is the first
/// line this prints. Where the two agree, the column offsets below are as sound as the ones for
/// EndgameMaps and EndgameMapAtlas, which agreed to the byte. Where they disagree, every offset
/// past the disagreement is rubbish and nothing else here is worth reading.
///
/// THEN THE TAGS, which is the half that cannot be settled by arithmetic at all. The curated
/// file calls maps lineage, arbiter, traverse, craft - 39 of its 57 tagged maps hang on words
/// that sound like a player's vocabulary rather than a table's. Whether the game's own Tags
/// column carries those distinctions is a question only the game can answer, so this resolves
/// the array and prints the tag ids as they are, on maps whose answer is already known from the
/// file. The two readings of the array header are both printed, because which of them the game
/// uses - a count and a pointer, or a begin and an end - is not settled in this project either.
///
/// It runs from the atlas panel's debug log beside <see cref="AtlasNodeProbe"/>, one-shot behind
/// a button, and reads nothing on the overlay's own path.
/// </remarks>
public sealed class WorldAreaRowProbe
{
    /// <summary>How many maps get their row read. Enough for a spread, few enough to read.</summary>
    private const int MostMaps = 6;

    /// <summary>How far down the node list to look for them.</summary>
    private const int Scan = 400;

    /// <summary>Most tag entries resolved per map.</summary>
    private const int MostTags = 8;

    /// <summary>Bytes one tag entry takes: a row reference followed by its table.</summary>
    private const int TagEntrySize = 0x10;

    /// <summary>
    /// Id fragments worth looking for when choosing which maps to dump.
    /// </summary>
    /// <remarks>
    /// A SAMPLING AID AND NOTHING ELSE. It decides which rows get printed, never what they
    /// mean - deriving "this is a tower because its id says Tower" is exactly the guess this
    /// probe exists to replace. Each fragment names a map the curated file tags differently,
    /// so whatever the game's Tags column holds, the printed rows are the ones where a
    /// difference would show.
    /// </remarks>
    private static readonly string[] Wanted =
    [
        "Tower",      // curated: tower
        "Citadel",    // curated: arbiter
        "Unique",     // curated: type unique
        "Hideout",    // curated: hideout
        "Expedition", // curated: expedition
        "Quest",      // curated: quest
    ];

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _tables;

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _mapData;
    private readonly int _worldAreaRef;

    private readonly int _id;
    private readonly int _name;
    private readonly int _isMapArea;
    private readonly int _isHideout;
    private readonly int _tagsArray;
    private readonly int _isUnique;
    private readonly long _computedRowSize;

    private readonly int _tagId;
    private readonly int _tagDisplay;

    public WorldAreaRowProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _tables = DatTableShape.From(schema);

        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef endgame = schema.Structs["EndgameMapsRow"];
        StructDef area = schema.Structs["WorldAreaDat"];
        StructDef tag = schema.Structs["TagsRow"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _mapData = data.OffsetOf("MapDataPtr");
        _worldAreaRef = endgame.OffsetOf("WorldAreaRef");

        _id = area.OffsetOf("IdPtr");
        _name = area.OffsetOf("NamePtr");
        _isMapArea = area.OffsetOf("IsMapArea");
        _isHideout = area.OffsetOf("IsHideout");
        _tagsArray = area.OffsetOf("TagsArray");
        _isUnique = area.OffsetOf("IsUniqueMapArea");
        _computedRowSize = area.Constants["ComputedRowSize"];

        _tagId = tag.OffsetOf("IdPtr");
        _tagDisplay = tag.OffsetOf("DisplayStringPtr");
    }

    /// <summary>What the WorldAreas rows behind these nodes hold.</summary>
    public IReadOnlyList<string> Probe(IReadOnlyList<ProbedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var said = new List<string>();
        if (nodes.Count == 0)
        {
            return said;
        }

        said.Add(string.Empty);
        said.Add("WORLDAREAS PROBE - what it would take to drop data/atlas-maps.json:");

        bool sizeSaid = false;
        int done = 0;
        foreach (ProbedNode node in Sample(nodes))
        {
            said.AddRange(One(node, ref sizeSaid));
            if (++done >= MostMaps)
            {
                break;
            }
        }

        if (done == 0)
        {
            said.Add("  no node carried a map id, so there is no row to follow");
        }

        return said;
    }

    /// <summary>
    /// Which maps to read: distinct ids, the ones the curated file disagrees about first.
    /// </summary>
    /// <remarks>
    /// Distinct because the same map appears on the atlas many times and six readings of one
    /// row say no more than one. The preference is what makes the sample worth taking: six
    /// arbitrary maps would very likely all be plain ones, and plain ones cannot show whether
    /// the game's tags separate a tower from a citadel.
    /// </remarks>
    private static IEnumerable<ProbedNode> Sample(IReadOnlyList<ProbedNode> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new ProbedNode?[Wanted.Length];
        var rest = new List<ProbedNode>();

        int looked = 0;
        foreach (ProbedNode node in nodes)
        {
            if (looked++ >= Scan)
            {
                break;
            }

            if (node.MapId.Length == 0 || !seen.Add(node.MapId))
            {
                continue;
            }

            // ONE PER FRAGMENT rather than the first six that match any of them: an atlas holds
            // a dozen uniques and one tower, and six uniques would answer a sixth of the
            // question. The spread is what makes the sample worth taking.
            int which = -1;
            for (int i = 0; i < Wanted.Length; i++)
            {
                if (node.MapId.Contains(Wanted[i], StringComparison.OrdinalIgnoreCase))
                {
                    which = i;
                    break;
                }
            }

            if (which < 0)
            {
                rest.Add(node);
            }
            else if (picked[which] is null)
            {
                picked[which] = node;
            }
        }

        foreach (ProbedNode? node in picked)
        {
            if (node is { } found)
            {
                yield return found;
            }
        }

        // Plain maps after them, so a sample is still taken on an atlas holding none of the
        // fragments - and so the printed rows always include a control.
        foreach (ProbedNode node in rest)
        {
            yield return node;
        }
    }

    /// <summary>One map: the two hops to its row, the columns, and the tags.</summary>
    private List<string> One(ProbedNode node, ref bool sizeSaid)
    {
        var said = new List<string>();

        ulong storage = _reader.ReadPointer(node.Element + (ulong)_dataStorage);
        ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
            ? _reader.ReadPointer(storage + (ulong)_data)
            : 0;
        ulong endgameRow = MemoryReaderExtensions.IsPlausiblePointer(data)
            ? _reader.ReadPointer(data + (ulong)_mapData)
            : 0;

        said.Add(string.Empty);
        said.Add($"map {node.MapId} (node [{node.Index}])");
        if (!MemoryReaderExtensions.IsPlausiblePointer(endgameRow))
        {
            said.Add($"  the EndgameMaps reference at data+0x{_mapData:X} is 0x{endgameRow:X}");
            return said;
        }

        ulong row = _reader.ReadPointer(endgameRow + (ulong)_worldAreaRef);
        ulong table = _reader.ReadPointer(endgameRow + (ulong)_worldAreaRef + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            said.Add($"  EndgameMaps row 0x{endgameRow:X} - its WorldArea column is 0x{row:X}");
            return said;
        }

        said.Add($"  WorldAreas row 0x{row:X}  {PointerPeek.Peek(_reader, row, _tables, table).Summary}");

        // The one number everything else here depends on, said once: a table knows its own
        // rows and the span they occupy, so the row size divides out of memory rather than
        // out of a column list.
        if (!sizeSaid && _tables is { } shape && PointerPeek.DescribeTable(_reader, table, shape) is { } facts)
        {
            sizeSaid = true;
            said.Add($"  table \"{facts.Path}\": {facts.Rows} rows of 0x{facts.RowSize:X}"
                + $", computed 0x{_computedRowSize:X}"
                + (facts.RowSize == _computedRowSize
                    ? " - AGREE, so the columns below are arithmetic on a measured size"
                    : " - DISAGREE, so a column width is wrong and every offset past it is rubbish"));
        }

        said.Add($"    +0x{_id:X3} Id   -> {ProbeBytes.Text(_reader, row + (ulong)_id)}");
        said.Add($"    +0x{_name:X3} Name -> {ProbeBytes.Text(_reader, row + (ulong)_name)}");

        said.Add($"    +0x{_isMapArea:X3} IsMapArea {Flag(row, _isMapArea)}"
            + $"   +0x{_isHideout:X3} IsHideout {Flag(row, _isHideout)}"
            + $"   +0x{_isUnique:X3} IsUniqueMapArea {Flag(row, _isUnique)}");

        said.AddRange(Tags(row));

        // The neighbourhoods of the three flags and of the array, so a capture can be re-read
        // when one of them turns out to sit a few bytes off.
        said.AddRange(ProbeBytes.Block(_reader, _tables, "row", row, _isMapArea - 0x10, _tagsArray + 0x18));
        said.AddRange(ProbeBytes.Block(_reader, _tables, "row", row, _isUnique - 0x10, _isUnique + 0x10));
        return said;
    }

    /// <summary>One flag byte, or a question mark when the read itself failed.</summary>
    /// <remarks>
    /// The distinction is the whole value of the line. A byte nobody could read prints as 0
    /// through any careless reader, and a capture that never touched these offsets would then
    /// say "not a map, not a hideout, not unique" about every map on the atlas - a confident
    /// answer built out of nothing, which is exactly the failure this probe exists to avoid.
    /// </remarks>
    private string Flag(ulong row, int offset)
        => _reader.TryRead(row + (ulong)offset, out byte value)
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";

    /// <summary>The Tags column, read both ways round.</summary>
    private List<string> Tags(ulong row)
    {
        var said = new List<string>();

        if (!_reader.TryRead(row + (ulong)_tagsArray, out ulong first)
            || !_reader.TryRead(row + (ulong)_tagsArray + 8, out ulong second))
        {
            // Said apart from "no tags" on purpose - see Flag: a failed read must never read
            // as an answer about the game.
            said.Add($"    +0x{_tagsArray:X3} Tags unread");
            return said;
        }

        said.Add($"    +0x{_tagsArray:X3} Tags {first:X16} {second:X16}");

        // Two readings, because which one this game uses is not settled here. A begin/end pair
        // is two plausible pointers whose difference divides by the entry size; a count and a
        // pointer is a small number followed by one.
        ulong begin;
        long count;
        if (MemoryReaderExtensions.IsPlausiblePointer(first)
            && MemoryReaderExtensions.IsPlausiblePointer(second)
            && second > first
            && (second - first) % TagEntrySize == 0)
        {
            begin = first;
            count = (long)(second - first) / TagEntrySize;
            said.Add($"      as (begin, end): {count} entries of 0x{TagEntrySize:X}");
        }
        else if (first is > 0 and <= MostTags && MemoryReaderExtensions.IsPlausiblePointer(second))
        {
            begin = second;
            count = (long)first;
            said.Add($"      as (count, pointer): {count} entries");
        }
        else
        {
            said.Add("      neither reading fits - no tags, or the column is not here");
            return said;
        }

        for (long i = 0; i < count && i < MostTags; i++)
        {
            ulong entry = begin + (ulong)(i * TagEntrySize);
            ulong tag = _reader.ReadPointer(entry);
            if (!MemoryReaderExtensions.IsPlausiblePointer(tag))
            {
                said.Add($"      tag[{i}] 0x{tag:X} is not a row");
                continue;
            }

            said.Add($"      tag[{i}] {ProbeBytes.Text(_reader, tag + (ulong)_tagId)}"
                + $"  display {ProbeBytes.Text(_reader, tag + (ulong)_tagDisplay)}"
                + $"  {PointerPeek.Peek(_reader, tag, _tables, _reader.Read<ulong>(entry + 8)).Summary}");
        }

        return said;
    }
}
