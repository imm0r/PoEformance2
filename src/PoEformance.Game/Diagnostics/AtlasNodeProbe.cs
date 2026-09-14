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
/// captured - and the atlas was exactly in that position. Three projects read these nodes and
/// disagreed about two fields; ONE CAPTURE SETTLED BOTH (tests/fixtures/session-2026-09-atlas.rec,
/// 2026-09-14), which is the argument for keeping this rather than deleting it now that it has
/// answered:
///
/// - the BADGE STRING. This tool and GameHelper2 had the content name at badge+0x278;
///   chickyd3v/POE2Radar has it at badge+0x2E8, the same 0x70 apart as their pre-0.5.5 pair
///   (0x290 and 0x300). On the one badge in the capture - content id 0x00020064 - 0x278 held a
///   NULL POINTER and 0x2E8 held "Powerful Map Boss", which is what id 0x64 means. Both slots
///   are still read: one badge is one badge, and the next capture adds samples.
/// - the TYPE ROW. POE2Radar reads a row pointer at node+0x300 and documents it as a WorldAreas
///   row whose name moved to +0x32. IT IS A PassiveSkills.dat ROW - the node's atlas passive -
///   and the table said so itself: the reference's table half at +0x308 names
///   "Data/Balance/PassiveSkills.dat", where Id 0x00, Icon_DDSFile 0x08 and Name 0x32 are what
///   the community dat-schema computes. So their two observations are right about that table and
///   say nothing about WorldAreas, where the name is still at +0x08. See PassiveSkillsRow.
///
/// SO THIS READS RATHER THAN GUESSES, which is the rule the project runs on. Every candidate
/// offset is read, every row pointer is handed to <see cref="PointerPeek"/> - which can name
/// the content table a row belongs to and number the row, and that naming is what settled the
/// type row - and the neighbourhood of each is read as a BLOCK. The block matters twice over:
/// one call instead of eight, and the whole span lands in the recording, so the next question
/// about those bytes needs no new build. The status byte is printed with its undecoded bits
/// named separately for the same reason: 0x10 is set on a fifth of the atlas and nothing knows
/// what it means yet.
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
    private readonly int _atlasRow;
    private readonly int[] _badgeChildPath;
    private readonly int _badgeContentId;
    private readonly int _badgeContentName;
    private readonly int _badgeContentNameGh;

    private readonly int _passivesRef;
    private readonly int _rowId;
    private readonly int _rowIcon;
    private readonly int _rowGraphId;
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
        StructDef atlas = schema.Structs["EndgameMapAtlasRow"];
        StructDef row = schema.Structs["PassiveSkillsRow"];

        _dataStorage = (int)node.Constants["DataStoragePtr"];
        _data = (int)node.Constants["DataPtr"];
        _grid = node.OffsetOf("GridPosition");
        _badgeChildPath = [(int)node.Constants["BadgeChild0"], (int)node.Constants["BadgeChild1"]];
        _badgeContentId = (int)node.Constants["BadgeContentId"];
        _badgeContentName = (int)node.Constants["BadgeContentName"];
        _badgeContentNameGh = (int)node.Constants["BadgeContentNameGameHelper"];

        _biome = data.OffsetOf("BiomeId");
        _status = data.OffsetOf("StatusBits");
        _mapData = data.OffsetOf("MapDataPtr");
        _atlasRow = data.OffsetOf("AtlasRowPtr");
        _completedBit = (int)node.Constants["CompletedBit"];
        _accessibleBit = (int)node.Constants["AccessibleBit"];

        _passivesRef = atlas.OffsetOf("PassivesRef");
        _rowId = row.OffsetOf("IdPtr");
        _rowIcon = row.OffsetOf("IconPtr");
        _rowGraphId = row.OffsetOf("GraphId");
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

        ulong storage = _reader.ReadPointer(node.Element + (ulong)_dataStorage);
        ulong data = MemoryReaderExtensions.IsPlausiblePointer(storage)
            ? _reader.ReadPointer(storage + (ulong)_data)
            : 0;

        if (MemoryReaderExtensions.IsPlausiblePointer(data))
        {
            said.AddRange(Data(data));
            said.AddRange(Row(data));
        }
        else
        {
            said.Add($"  data chain +0x{_dataStorage:X} -> 0x{storage:X} +0x{_data:X} -> 0x{data:X}"
                + " - a zero names the hop that broke");
        }

        // The element's own neighbourhood, kept even though the fields live on the data object:
        // the grid pair is here at +0x310 with the decoy 0x10 past it, and so is the other end of
        // POE2Radar's reading - element+0x300 is data+0x2A0 while the two allocations stay 0x60
        // apart, which this window is where anybody would notice stopped being true.
        said.AddRange(Block("node", node.Element, _grid - 0x30, _grid + 0x28));
        said.AddRange(Badges(node.Element, ref badges));
        return said;
    }

    /// <summary>The bytes at the end of the two-hop chain, read raw.</summary>
    private List<string> Data(ulong data)
    {
        _reader.TryRead(data + (ulong)_biome, out byte biome);
        _reader.TryRead(data + (ulong)_status, out byte status);

        var said = new List<string>
        {
            $"  data 0x{data:X}  +0x{_biome:X} biome {biome}  +0x{_status:X} status 0x{status:X2} {Bits(status)}",
        };

        // Read as a block so the whole tail is in the recording: when these move again they move
        // together, and the answer is then in a file somebody already has.
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

    /// <summary>The node's own atlas row, the passive behind it, and that passive's columns.</summary>
    /// <remarks>
    /// EVERY HOP IS SAID OUT LOUD because the answer came out of the hops rather than the rows:
    /// what settled that this ends in PassiveSkills.dat - and not the WorldAreas row it was
    /// published as - was each TABLE half naming itself. A row on its own looks like any other
    /// row, and a string column full of plausible text is exactly how the wrong table passes.
    /// </remarks>
    private List<string> Row(ulong data)
    {
        var said = new List<string>();

        ulong row = _reader.ReadPointer(data + (ulong)_atlasRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            said.Add($"  +0x{_atlasRow:X} (atlas row) holds 0x{row:X}, which is not a pointer"
                + " - not every node carries one");
            return said;
        }

        // A dat foreign reference is a row followed by the table it belongs to, so the eight
        // bytes after are what let PointerPeek NAME the table instead of showing bytes.
        ulong table = _reader.ReadPointer(data + (ulong)_atlasRow + 8);
        said.Add($"  +0x{_atlasRow:X} (atlas row) -> 0x{row:X}"
            + $"  {PointerPeek.Peek(_reader, row, _tables, table).Summary}");

        ulong inner = _reader.ReadPointer(row + (ulong)_passivesRef);
        if (!MemoryReaderExtensions.IsPlausiblePointer(inner))
        {
            said.Add($"    +0x{_passivesRef:X2} Passives is 0x{inner:X}, so this row references no passive");
            return said;
        }

        ulong innerTable = _reader.ReadPointer(row + (ulong)_passivesRef + 8);
        said.Add($"    +0x{_passivesRef:X2} Passives -> 0x{inner:X}"
            + $"  {PointerPeek.Peek(_reader, inner, _tables, innerTable).Summary}");

        said.Add($"      +0x{_rowId:X2} Id   -> {Text(inner + (ulong)_rowId)}");
        said.Add($"      +0x{_rowIcon:X2} Icon -> {Text(inner + (ulong)_rowIcon)}");
        said.Add(_reader.TryRead(inner + (ulong)_rowGraphId, out ushort graph)
            ? $"      +0x{_rowGraphId:X2} GraphId {graph}"
            : $"      +0x{_rowGraphId:X2} GraphId unreadable");
        said.Add($"      +0x{_rowName:X2} Name -> {Text(inner + (ulong)_rowName)}");
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
            said.Add($"    +0x{_badgeContentName:X} -> {Text(badge + (ulong)_badgeContentName)}  <- holds the name on 0.5.5");
            said.Add($"    +0x{_badgeContentNameGh:X} -> {Text(badge + (ulong)_badgeContentNameGh)}  <- GameHelper2's slot, null on 0.5.5");

            // Both slots in one span, whichever order the schema puts them in - one capture that
            // moved them would otherwise print a window with the interesting half outside it.
            said.AddRange(Block(
                "badge",
                badge,
                Math.Min(_badgeContentName, _badgeContentNameGh) - 0x08,
                Math.Max(_badgeContentName, _badgeContentNameGh) + 0x10));
        }

        return said;
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
