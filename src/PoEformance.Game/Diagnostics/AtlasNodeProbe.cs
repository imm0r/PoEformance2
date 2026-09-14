using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Game.Diagnostics;

/// <summary>One node to look at: where it sits and what the tool currently calls it.</summary>
/// <remarks>
/// Deliberately not the reader's own AtlasNode. The probe answers questions about bytes the
/// reader does not decode, so taking its record would tie a diagnostic to the shape of the
/// thing it is meant to check.
/// </remarks>
public readonly record struct ProbedNode(int Index, ulong Element, string MapId);

/// <summary>
/// Reads the parts of an atlas node NOTHING ELSE READS, so that a recording can settle them.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. A recording can only contain reads the running build performed, so a
/// question about bytes nothing reads is unanswerable offline however many sessions are
/// captured - and the atlas is exactly in that position. Three projects read these nodes and
/// they disagree about two fields:
///
/// - the BADGE STRING. This tool and GameHelper2 read the content name at badge+0x278;
///   chickyd3v/POE2Radar reads one at badge+0x2E8, and in the pre-0.5.5 layout the same pair
///   was 0x290 and 0x300. The SAME 0x70 apart in both layouts means two fields rather than one
///   drifted value, and theirs carries the packed "[Code|Display]" form with the boss TIER in
///   it, which the plain one collapses to a generic headline.
/// - the TYPE ROW. POE2Radar reads a row pointer at node+0x300 that names what KIND of node
///   this is ("White Node", "District B"), which is not the rolled map this tool reads through
///   AtlasNodeData.MapDataPtr. Its own note says the name on that row moved to +0x32 and that
///   +0x08 is an art path now - and that disagrees with this tool's measurement of a WorldAreas
///   row on the same client, where +0x08 still reads "Clearfell". One of those is about a
///   different table, and asking the game is cheaper than arguing.
///
/// SO THIS READS RATHER THAN GUESSES, which is the rule the project runs on. Every candidate
/// offset is read, every row pointer is handed to <see cref="PointerPeek"/> - which can name
/// the content table a row belongs to and number the row - and the neighbourhood of each is
/// read as a BLOCK. The block matters twice over: one call instead of eight, and the whole
/// span lands in the recording, so the next question about those bytes needs no new build.
///
/// It runs from the atlas panel's debug log, which is a thing a person opens, so its cost is
/// paid when somebody is looking and never on the overlay's own path.
/// </remarks>
public sealed class AtlasNodeProbe
{
    /// <summary>How many nodes get the full treatment. Two, because the second is the control.</summary>
    /// <remarks>
    /// One node cannot tell a field from a coincidence: a pointer-shaped word at +0x300 on one
    /// node is an observation, the same word leading to a DIFFERENT row on a different map is
    /// evidence that it is the map's row. Beyond two the lines repeat without adding a check.
    /// </remarks>
    private const int MostNodes = 2;

    /// <summary>And how many badge children, for the same reason.</summary>
    private const int MostBadges = 2;

    /// <summary>
    /// How far down the node list to look for a node that HAS badges.
    /// </summary>
    /// <remarks>
    /// Most maps carry no content at all, so the first nodes on the panel are usually bare and
    /// the badge half of this would report nothing on a perfectly healthy atlas. Looking a
    /// little further costs a child walk each and answers the question that was asked.
    /// </remarks>
    private const int BadgeScan = 24;

    /// <summary>Most badge children walked under one node - the reader's own guard, repeated.</summary>
    private const int MostContents = 64;

    /// <summary>Longest string taken seriously behind one of these pointers.</summary>
    /// <remarks>
    /// The packed badge form ("[DeadlyMapBoss|Deadly Map Boss]") is the longest of them and
    /// fits twice over. A cap matters because the pointer may not be one.
    /// </remarks>
    private const int MostChars = 96;

    /// <summary>Bytes of a dat row's head to read. Far enough past 0x32 to show its neighbours.</summary>
    private const int RowHeadBytes = 0x48;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly DatTableShape? _tables;

    private readonly int _dataStorage;
    private readonly int _data;
    private readonly int _biome;
    private readonly int _status;
    private readonly int _mapData;
    private readonly int _completedBit;
    private readonly int _accessibleBit;

    private readonly int _grid;
    private readonly int _mapRow;
    private readonly int[] _badgeChildPath;
    private readonly int _badgeContentId;
    private readonly int _badgeContentName;
    private readonly int _badgeContentStr;

    private readonly int _rowId;
    private readonly int _rowArt;
    private readonly int _rowName;

    public AtlasNodeProbe(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;
        _tables = DatTableShape.From(schema);

        StructDef node = schema.Structs["AtlasNode"];
        StructDef data = schema.Structs["AtlasNodeData"];
        StructDef row = schema.Structs["AtlasMapRow"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _grid = node.OffsetOf("GridPosition");
        _mapRow = (int)node.Constants["MapRowPtr"];
        _badgeChildPath = [(int)node.Constants["BadgeChild0"], (int)node.Constants["BadgeChild1"]];
        _badgeContentId = (int)node.Constants["BadgeContentId"];
        _badgeContentName = (int)node.Constants["BadgeContentName"];
        _badgeContentStr = (int)node.Constants["BadgeContentStr"];

        _biome = data.OffsetOf("BiomeId");
        _status = data.OffsetOf("StatusBits");
        _mapData = data.OffsetOf("MapDataPtr");
        _completedBit = (int)node.Constants["CompletedBit"];
        _accessibleBit = (int)node.Constants["AccessibleBit"];

        _rowId = row.OffsetOf("IdPtr");
        _rowArt = row.OffsetOf("ArtPtr");
        _rowName = row.OffsetOf("NamePtr");
    }

    /// <summary>Everything the probe has to say about the nodes it was given.</summary>
    public IReadOnlyList<string> Probe(IReadOnlyList<ProbedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var said = new List<string>();
        if (nodes.Count == 0)
        {
            return said;
        }

        said.Add(string.Empty);
        said.Add("LAYOUT PROBE - the offsets nothing here reads, read so a recording can settle them:");

        int badges = 0;
        for (int i = 0; i < nodes.Count && i < MostNodes; i++)
        {
            said.AddRange(One(nodes[i], ref badges));
        }

        // A bare node says nothing about the badge offsets, and most maps are bare. Keep going
        // until one with content turns up rather than reporting that nothing was found.
        for (int i = MostNodes; i < nodes.Count && i < BadgeScan && badges == 0; i++)
        {
            List<string> found = Badges(nodes[i].Element, ref badges);
            if (badges > 0)
            {
                said.Add(string.Empty);
                said.Add($"node [{nodes[i].Index}] 0x{nodes[i].Element:X} - the first one carrying badges:");
                said.AddRange(found);
            }
        }

        if (badges == 0)
        {
            said.Add($"  no badge child under any of the first {Math.Min(nodes.Count, BadgeScan)} nodes -"
                + " open the atlas somewhere with content on it and look again");
        }

        return said;
    }

    /// <summary>One node, end to end.</summary>
    private List<string> One(ProbedNode node, ref int badges)
    {
        var said = new List<string>
        {
            string.Empty,
            $"node [{node.Index}] 0x{node.Element:X}  {(node.MapId.Length > 0 ? node.MapId : "(no id)")}",
        };

        said.AddRange(Data(node.Element));
        said.AddRange(Row(node.Element));

        // The element's own neighbourhood, because three projects put three different things in
        // it: the row pointer at +0x300, the grid pair at +0x310 (this tool) or +0x320 (the
        // decoy), and POE2Radar's state/biome/flags bytes at +0x32C..+0x32F.
        said.AddRange(Block("node", node.Element, _mapRow - 0x20, _grid + 0x28));
        said.AddRange(Badges(node.Element, ref badges));
        return said;
    }

    /// <summary>The two-hop chain and the bytes at the end of it, read raw.</summary>
    private List<string> Data(ulong element)
    {
        var said = new List<string>();

        ulong storage = _reader.ReadPointer(element + (ulong)_dataStorage);
        ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
            ? _reader.ReadPointer(storage + (ulong)_data)
            : 0;

        if (!MemoryReaderExtensions.IsPlausiblePointer(data))
        {
            said.Add($"  data chain +0x{_dataStorage:X} -> 0x{storage:X} +0x{_data:X} -> 0x{data:X}"
                + " - a zero names the hop that broke");
            return said;
        }

        _reader.TryRead(data + (ulong)_biome, out byte biome);
        _reader.TryRead(data + (ulong)_status, out byte status);
        said.Add($"  data 0x{data:X}  +0x{_biome:X} biome {biome}  +0x{_status:X} status 0x{status:X2} {Bits(status)}");

        // Read as a block so the whole tail is in the recording: when these two move again they
        // move together, and the answer is then in a file somebody already has.
        said.AddRange(Block("data", data, _mapData - 0x10, _status + 0x11));
        return said;
    }

    /// <summary>What the status byte says, and what it says that this tool ignores.</summary>
    /// <remarks>
    /// The unaccounted bits are the point. Both references decode exactly two of them, and a
    /// capture showing a third moving with something in the game is how a third gets decoded -
    /// but only if the raw byte was ever written down.
    /// </remarks>
    private string Bits(byte status)
    {
        string state = (status & _completedBit) != 0 ? "completed"
            : (status & _accessibleBit) != 0 ? "accessible"
            : "locked";

        int rest = status & ~(_completedBit | _accessibleBit);
        return rest == 0 ? $"({state})" : $"({state}, and 0x{rest:X2} nothing decodes)";
    }

    /// <summary>The row POE2Radar reads the node's type off, and the three columns it could be in.</summary>
    private List<string> Row(ulong element)
    {
        var said = new List<string>();

        ulong row = _reader.ReadPointer(element + (ulong)_mapRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            said.Add($"  +0x{_mapRow:X} (POE2Radar MapNodeId) holds 0x{row:X}, which is not a pointer");
            return said;
        }

        // A dat foreign reference is a row followed by the table it belongs to, so the eight
        // bytes after are what let PointerPeek NAME the table instead of showing bytes.
        ulong table = _reader.ReadPointer(element + (ulong)_mapRow + 8);
        said.Add($"  +0x{_mapRow:X} (POE2Radar MapNodeId) -> 0x{row:X}"
            + $"  {PointerPeek.Peek(_reader, row, _tables, table).Summary}");

        ulong inner = _reader.ReadPointer(row);
        if (!MemoryReaderExtensions.IsPlausiblePointer(inner))
        {
            said.Add($"    [row] is 0x{inner:X}, so the row's first column is not a reference");
            return said;
        }

        ulong innerTable = _reader.ReadPointer(row + 8);
        said.Add($"    [row] -> 0x{inner:X}  {PointerPeek.Peek(_reader, inner, _tables, innerTable).Summary}");

        // POE2Radar tries the row itself as text first - on some nodes the hop lands straight on
        // a string buffer rather than on another row. Only said when it READS as text: a row of
        // pointers decodes to something every time, and printing that would be noise dressed as
        // a finding.
        string direct = _reader.ReadUnicodeString(row, MostChars);
        if (Readable(direct))
        {
            said.Add($"    [row] as text: \"{direct}\"");
        }

        said.Add($"      +0x{_rowId:X2} -> {Text(inner + (ulong)_rowId)}  <- the id column every row starts with");
        said.Add($"      +0x{_rowArt:X2} -> {Text(inner + (ulong)_rowArt)}  <- POE2Radar says art path, WorldAreas says name");
        said.Add($"      +0x{_rowName:X2} -> {Text(inner + (ulong)_rowName)}  <- POE2Radar WorldAreaName");
        said.AddRange(Block("row", inner, 0, RowHeadBytes));
        return said;
    }

    /// <summary>The badge children, and both candidate strings on each.</summary>
    private List<string> Badges(ulong element, ref int badges)
    {
        var said = new List<string>();

        ulong holder = element;
        foreach (int step in _badgeChildPath)
        {
            holder = _elements.Child(holder, step);
            if (holder == 0)
            {
                return said;
            }
        }

        foreach (ulong badge in _elements.Children(holder, MostContents))
        {
            if (badges >= MostBadges)
            {
                return said;
            }

            badges++;
            _reader.TryRead(badge + (ulong)_badgeContentId, out uint id);
            said.Add($"  badge 0x{badge:X}  +0x{_badgeContentId:X} id 0x{id:X8}");
            said.Add($"    +0x{_badgeContentName:X} -> {Text(badge + (ulong)_badgeContentName)}  <- read here (GameHelper2)");
            said.Add($"    +0x{_badgeContentStr:X} -> {Text(badge + (ulong)_badgeContentStr)}  <- POE2Radar BadgeContentStr");
            said.AddRange(Block("badge", badge, _badgeContentName - 0x08, _badgeContentStr + 0x10));
        }

        return said;
    }

    /// <summary>Whether a decoded string is text rather than bytes that happened to decode.</summary>
    private static bool Readable(string text)
    {
        if (text.Length < 3)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A pointer slot read as the wide string it might be pointing at.</summary>
    private string Text(ulong slot)
    {
        ulong at = _reader.ReadPointer(slot);
        if (!MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return $"0x{at:X} (not a pointer)";
        }

        string text = _reader.ReadUnicodeString(at, MostChars);
        return text.Length > 0 ? $"0x{at:X} \"{text}\"" : $"0x{at:X} (no text there)";
    }

    /// <summary>
    /// A span of an object, one line per eight-byte slot, and whatever each slot points at.
    /// </summary>
    /// <remarks>
    /// ONE read for the whole span rather than one per slot. That is the cheaper shape, and it
    /// is the useful one: a recording then holds the span CONTIGUOUSLY, so a field nobody has
    /// noticed yet can be found in it later without the build that would otherwise be needed to
    /// read it.
    /// </remarks>
    private List<string> Block(string label, ulong at, int from, int to)
    {
        var said = new List<string>();

        int start = Math.Max(from, 0) & ~7;
        int length = ((to - start + 7) & ~7) + 8;
        var block = new byte[length];
        if (!_reader.TryRead(at + (ulong)start, block))
        {
            said.Add($"    {label} +0x{start:X3}..+0x{start + length:X3} unreadable");
            return said;
        }

        said.Add($"    {label} +0x{start:X3}..+0x{start + length:X3}");
        for (int offset = 0; offset + 8 <= length; offset += 8)
        {
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset));
            string note = string.Empty;
            if (MemoryReaderExtensions.IsPlausiblePointer(raw))
            {
                ulong following = offset + 16 <= length
                    ? BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset + 8))
                    : 0;
                note = PointerPeek.Peek(_reader, raw, _tables, following).Summary;
            }

            said.Add($"      +0x{start + offset:X3}  {raw:X16}  {note}");
        }

        return said;
    }
}
