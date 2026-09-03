using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Looks for the ROOM a tile belongs to, in the bytes nothing has claimed yet.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. The game builds an area in two levels: rooms - files under a
/// <c>Rooms/</c> directory, ending <c>.arm</c> - are assembled from tiles, files under
/// <c>Tiles/</c> ending <c>.tdt</c>. The tile struct's one mapped string is the TILE's, so the
/// room names on the map are currently a materials list: "BuildingWall_OceanEdge_CcMM_02"
/// where the reference tool writes "overlay_bridge_03". The room is one level up and nothing
/// here knows where it lives.
///
/// SO THIS LOOKS RATHER THAN GUESSES, which is the whole rule this project runs on. Two
/// places have room for a pointer nobody has accounted for:
///
/// - the tile struct itself is 0x38 bytes with 0x00, 0x08, 0x30 and 0x34-0x36 mapped, leaving
///   0x10 to 0x2F - four pointer slots - unexplained;
/// - the terrain struct has the tile vector at 0x28 and the grids at 0xD0 and 0xE8, and the
///   span between them is untouched: room for a second vector.
///
/// Both are walked, every plausible pointer is classified by <see cref="PointerPeek"/>, and
/// anything that leads to a structure is followed ONE more hop looking for wide text - because
/// that is the shape the tile's own name has (a pointer to a struct whose +0x08 is a
/// std::wstring), and the room's is likely to be built the same way.
///
/// IT RUNS UNDER --debug AND ITS VALUE IS THE RECORDING. A recording can only contain reads
/// the running build performed, so a question about bytes nothing reads can never be answered
/// offline - which is exactly the position this feature was in. With the probe on, one session
/// in one area captures the whole neighbourhood of both structures, and the answer can be read
/// out of the file afterwards as often as it takes.
/// </remarks>
public sealed class RoomProbe
{
    /// <summary>Bytes in one tile entry. The whole thing is walked, not just the gap.</summary>
    private const int TileEntrySize = 0x38;

    /// <summary>How far past a pointer to look for the string it might carry.</summary>
    /// <remarks>
    /// The tile's own name sits at +0x08 of the struct its pointer leads to. Sixty-four bytes
    /// is eight slots, which covers that shape and its neighbours without turning one probe
    /// into a walk of the heap.
    /// </remarks>
    private const int SecondHopBytes = 0x40;

    /// <summary>How much of a vector's contents to look at. Its head, not the whole of it.</summary>
    private const int ElementBytes = 0x80;

    /// <summary>
    /// How much of an object an element points at to read.
    /// </summary>
    /// <remarks>
    /// 0x60 covers the shape a recording showed these to have - two vtables, a reference, and
    /// an inline vector of its own - and the read is worth making even when this run recognises
    /// nothing in it, because it is what puts the object in the recording.
    /// </remarks>
    private const int ObjectBytes = 0x60;

    /// <summary>
    /// How much of the terrain struct to walk.
    /// </summary>
    /// <remarks>
    /// THE WHOLE OF IT, and it started as the gap between the tile vector at 0x28 and the grids
    /// at 0xD0 - the one span nothing had claimed. That was reasoning from what is already
    /// mapped, and the mapped part stops at 0x134: everything past it is as unexamined as the
    /// gap was, and a rooms vector would be as much at home there. The whole struct costs 128
    /// slots once per area, which is nothing beside reading the grid it describes.
    /// </remarks>
    private const int TerrainFrom = 0x00;

    private const int TerrainTo = 0x400;

    /// <summary>A bound on the report, so a garbled read cannot fill the screen.</summary>
    private const int MostLines = 48;

    /// <summary>
    /// How much of a path to read for the report, rather than take from the peek.
    /// </summary>
    /// <remarks>
    /// NOT COSMETIC. <see cref="PointerPeek"/> trims its summary at sixty characters, which is
    /// the right length for a dissector row and the wrong one here: the paths this is hunting
    /// run past it - "Metadata/Terrain/Maps/Port/Tiles/OceanEdge/BuildingWall_OceanEdge_CcMM_02.tdt"
    /// is seventy-seven - so the extension and often the file name itself fall off the end. A
    /// probe whose whole job is to recognise ".arm" cannot read a string that stops before it,
    /// and the failure would be silent: a correct answer on screen, unmarked.
    /// </remarks>
    private const int MostPathChars = 256;

    private readonly IMemoryReader _reader;

    public RoomProbe(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Windows of the tile array that are read whole, spread evenly across it.
    /// </summary>
    /// <remarks>
    /// THESE READS ARE FOR THE RECORDING, and the size is the whole point. A recording drops any
    /// single read over 64 KiB, and the terrain pass reads the tile array in ONE go - 6075 tiles
    /// of 0x38 is 340 KiB - so the array is exactly the thing a recording never contains. The
    /// first attempt at answering this offline foundered on that: one tile in the file out of
    /// six thousand, which cannot tell "no tile carries a room" from "no tile was looked at".
    ///
    /// Sixteen windows of four kilobytes is 64 KiB in total, each one small enough to be kept,
    /// and each covering 73 consecutive tiles - so a recording gains a thousand tiles spread
    /// across the whole area, and the sampling below draws from what it already read.
    /// </remarks>
    private const int Windows = 16;

    private const int WindowBytes = 4096;

    /// <summary>Tiles walked per window. Two is enough to notice a field every tile has.</summary>
    private const int TilesPerWindow = 2;

    /// <summary>
    /// Walks the tile struct, the terrain struct's unclaimed span, and a sample of the array.
    /// </summary>
    /// <param name="terrainBase">The terrain struct, as a base address - it is inline.</param>
    /// <param name="tileEntry">One tile's 0x38 bytes, by address, walked in full.</param>
    /// <param name="tileName">What that tile is called, so the report says which one it is.</param>
    /// <param name="tileArray">The tile vector's first entry, for the sample. Optional.</param>
    /// <param name="tiles">How many entries that vector holds.</param>
    public IReadOnlyList<string> Probe(
        ulong terrainBase, ulong tileEntry, string tileName, ulong tileArray = 0, long tiles = 0)
    {
        var lines = new List<string>();

        if (MemoryReaderExtensions.IsPlausiblePointer(tileEntry))
        {
            lines.Add($"tile 0x{tileEntry:X}  {tileName}");
            Walk(lines, tileEntry, 0, TileEntrySize, loud: true);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(terrainBase))
        {
            lines.Add($"terrain 0x{terrainBase:X}  +0x{TerrainFrom:X}..0x{TerrainTo:X}");
            Walk(lines, terrainBase, TerrainFrom, TerrainTo, loud: true);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(tileArray) && tiles > 0)
        {
            Sample(lines, tileArray, tiles);
        }

        if (lines.Count == 0)
        {
            lines.Add("nothing to probe - no terrain or no tile");
        }

        return lines;
    }

    /// <summary>
    /// Reads windows of the tile array and walks a few tiles out of each, quietly.
    /// </summary>
    /// <remarks>
    /// QUIETLY, because the point of the sample is the ABSENCE it can establish: one tile
    /// proves nothing, and thirty-two tiles printing seven slots each would be two hundred
    /// lines of the same shape. Only a room path is worth a line here; everything else is a
    /// count at the end - which is the difference between "no tile carries a room" and "no tile
    /// was looked at", and the reason the first recording could not settle anything.
    /// </remarks>
    private void Sample(List<string> lines, ulong tileArray, long tiles)
    {
        long span = tiles * TileEntrySize;
        long step = Math.Max(WindowBytes, span / Windows);
        int walked = 0;
        int found = 0;

        for (int window = 0; window < Windows; window++)
        {
            // Aligned to an entry, or every tile in the window would be read at an offset into
            // its neighbour - which reads as a structure full of garbage rather than as a
            // misalignment.
            long at = window * step / TileEntrySize * TileEntrySize;
            if (at + WindowBytes > span)
            {
                break;
            }

            // The window itself, read whole and thrown away: what it is for is the RECORDING.
            var block = new byte[WindowBytes];
            if (!_reader.TryRead(tileArray + (ulong)at, block))
            {
                continue;
            }

            for (int i = 0; i < TilesPerWindow; i++)
            {
                long inside = i * (WindowBytes / TilesPerWindow) / TileEntrySize * TileEntrySize;
                walked++;
                found += Walk(lines, tileArray + (ulong)(at + inside), 0, TileEntrySize, loud: false);
            }
        }

        lines.Add(found > 0
            ? $"{walked} tiles sampled across {Windows} windows - {found} room paths"
            : $"{walked} tiles sampled across {Windows} windows - no room path in any slot");
    }

    /// <summary>
    /// Classifies every pointer in a span, follows the promising ones, and counts room paths.
    /// </summary>
    /// <param name="loud">
    /// Whether to report everything, or only what looks like a room. False for the sampled
    /// tiles, where the finding is a COUNT and printing every slot of every one would bury it.
    /// </param>
    private int Walk(List<string> lines, ulong at, int from, int to, bool loud)
    {
        var block = new byte[to - from];
        if (!_reader.TryRead(at + (ulong)from, block))
        {
            if (loud)
            {
                lines.Add($"  +0x{from:X2}  unreadable ({block.Length} bytes)");
            }

            return 0;
        }

        int found = 0;

        for (int offset = 0; offset + 8 <= block.Length && lines.Count < MostLines; offset += 8)
        {
            // AN INLINE VECTOR FIRST, because reading one as three separate pointers is what
            // hid a whole level of the tile struct. Slots +0x10, +0x18 and +0x20 of a tile are
            // begin, end and capacity - not three references - so peeking the value at +0x10
            // classifies whatever the ELEMENTS happen to start with, and the array itself is
            // never opened. 889 of 2336 tiles in a recording carry one.
            if (offset + 24 <= block.Length
                && TryInlineVector(block, offset, out ulong begin, out long span))
            {
                int slotAt = from + offset;
                if (loud)
                {
                    lines.Add($"  +0x{slotAt:X2}  Vector inline, 0x{span:X} bytes at 0x{begin:X}");
                }

                // ADDED TO the ordinary walk rather than replacing it: three pointers in the
                // right order are not proof of a vector - a struct holding two references and
                // an array would satisfy it by luck - and skipping the next two slots on a
                // guess would hide whatever they really are. The vector reading is extra, so a
                // wrong guess costs a line and never an offset.
                found += Contents(lines, begin, span, slotAt, loud);
            }

            ulong value = BitConverter.ToUInt64(block, offset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                continue;
            }

            PeekResult peek = PointerPeek.Peek(_reader, value);
            if (peek.Kind == TargetKind.Unreadable)
            {
                continue;
            }

            int slot = from + offset;
            string line = Describe(peek, value);
            bool room = LooksLikeRoom(line);
            found += room ? 1 : 0;

            if (loud || room)
            {
                lines.Add($"  +0x{slot:X2}  {line}");
            }

            // One hop further, and only for the shape that could be carrying a name: a
            // pointer to a structure. Text found there is reported with BOTH offsets, because
            // "+0x10 then +0x08" is the field this would become.
            if (peek.Kind is TargetKind.Structure or TargetKind.Vector)
            {
                found += Follow(lines, value, slot, loud);
            }

            // A VECTOR's own bytes are a begin/end pair, so the thing worth looking at is not
            // there at all - it is behind the begin pointer. The sub-tile details every tile
            // carries are exactly this shape, and the first pass looked straight past their
            // contents at the header holding them.
            if (peek.Kind == TargetKind.Vector)
            {
                found += Elements(lines, value, slot, loud);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether three consecutive slots read as a std::vector laid out INLINE.
    /// </summary>
    /// <remarks>
    /// begin, end, capacity - the shape MSVC gives a vector that is a FIELD rather than a thing
    /// pointed at. The checks are what keep three unrelated pointers from reading as one: the
    /// order has to hold, the span has to be a sane size, and it has to divide by eight, since
    /// an array of anything this game stores is made of whole words.
    /// </remarks>
    private static bool TryInlineVector(byte[] block, int offset, out ulong begin, out long span)
    {
        begin = BitConverter.ToUInt64(block, offset);
        ulong end = BitConverter.ToUInt64(block, offset + 8);
        ulong capacity = BitConverter.ToUInt64(block, offset + 16);
        span = 0;

        if (!MemoryReaderExtensions.IsPlausiblePointer(begin)
            || !MemoryReaderExtensions.IsPlausiblePointer(end)
            || end < begin
            || capacity < end)
        {
            return false;
        }

        span = (long)(end - begin);
        return span > 0 && span <= 0x1_0000 && span % 8 == 0;
    }

    /// <summary>
    /// Opens an array: its elements, and the objects they point at.
    /// </summary>
    /// <remarks>
    /// TWO HOPS, and the second one is as much for the RECORDING as for the report. What a
    /// tile's vector holds is one pointer and one number per 16 bytes, and every one of those
    /// pointers leads to an object of a single type - so the object is the thing that could
    /// carry a room, and it is exactly what a recording never contained, because nothing had
    /// ever read it. Reading 0x60 of it puts it in the file whether or not this run recognises
    /// anything in it.
    /// </remarks>
    private int Contents(List<string> lines, ulong begin, long span, int slot, bool loud)
    {
        var block = new byte[(int)Math.Min(ElementBytes, span)];
        if (!_reader.TryRead(begin, block))
        {
            return 0;
        }

        int found = 0;
        for (int offset = 0; offset + 8 <= block.Length && lines.Count < MostLines; offset += 8)
        {
            ulong value = BitConverter.ToUInt64(block, offset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                continue;
            }

            PeekResult peek = PointerPeek.Peek(_reader, value);
            if (peek.Kind is TargetKind.WideText or TargetKind.Text)
            {
                string text = Describe(peek, value);
                bool room = LooksLikeRoom(text);
                found += room ? 1 : 0;

                if (loud || room)
                {
                    lines.Add($"    +0x{slot:X2}[+0x{offset:X2}]  {text}");
                }

                continue;
            }

            if (peek.Kind != TargetKind.Structure)
            {
                continue;
            }

            // The object an element points at. Read whole - see the remarks above - and then
            // asked whether anything it holds is text.
            var inside = new byte[ObjectBytes];
            if (!_reader.TryRead(value, inside))
            {
                continue;
            }

            for (int deep = 0; deep + 8 <= inside.Length && lines.Count < MostLines; deep += 8)
            {
                ulong held = BitConverter.ToUInt64(inside, deep);
                if (!MemoryReaderExtensions.IsPlausiblePointer(held))
                {
                    continue;
                }

                PeekResult what = PointerPeek.Peek(_reader, held);
                if (what.Kind is not (TargetKind.WideText or TargetKind.Text))
                {
                    continue;
                }

                string text = Describe(what, held);
                bool room = LooksLikeRoom(text);
                found += room ? 1 : 0;

                if (loud || room)
                {
                    lines.Add($"      +0x{slot:X2}[+0x{offset:X2}]+0x{deep:X2}  {text}");
                }
            }
        }

        return found;
    }

    /// <summary>Looks one level inside a structure for the text it might be holding.</summary>
    private int Follow(List<string> lines, ulong at, int slot, bool loud)
    {
        var block = new byte[SecondHopBytes];
        if (!_reader.TryRead(at, block))
        {
            return 0;
        }

        int found = 0;
        for (int offset = 0; offset + 8 <= block.Length && lines.Count < MostLines; offset += 8)
        {
            ulong value = BitConverter.ToUInt64(block, offset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                continue;
            }

            PeekResult peek = PointerPeek.Peek(_reader, value);

            // Only TEXT is reported from this depth. Every structure holds pointers to further
            // structures, and printing those turns one probe into a page of addresses that say
            // nothing - the string is the thing that would identify a room.
            if (peek.Kind is not (TargetKind.WideText or TargetKind.Text))
            {
                continue;
            }

            string line = Describe(peek, value);
            bool room = LooksLikeRoom(line);
            found += room ? 1 : 0;

            if (loud || room)
            {
                lines.Add($"    +0x{slot:X2}+0x{offset:X2}  {line}");
            }
        }

        return found;
    }

    /// <summary>Looks at what a vector actually HOLDS, rather than at its begin/end pair.</summary>
    private int Elements(List<string> lines, ulong vector, int slot, bool loud)
    {
        ulong begin = _reader.Read<ulong>(vector);
        ulong end = _reader.Read<ulong>(vector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || end <= begin)
        {
            return 0;
        }

        // The head of it only. A vector can be an area's whole tile array, and reading one to
        // look for a string in it would be reading the map twice per probe.
        var block = new byte[(int)Math.Min(ElementBytes, (long)(end - begin))];
        if (!_reader.TryRead(begin, block))
        {
            return 0;
        }

        int found = 0;
        for (int offset = 0; offset + 8 <= block.Length && lines.Count < MostLines; offset += 8)
        {
            ulong value = BitConverter.ToUInt64(block, offset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                continue;
            }

            PeekResult peek = PointerPeek.Peek(_reader, value);
            if (peek.Kind is not (TargetKind.WideText or TargetKind.Text))
            {
                continue;
            }

            string line = Describe(peek, value);
            bool room = LooksLikeRoom(line);
            found += room ? 1 : 0;

            if (loud || room)
            {
                lines.Add($"    +0x{slot:X2}[+0x{offset:X2}]  {line}");
            }
        }

        return found;
    }

    /// <summary>
    /// One line about a target, with a room path called out where one appears.
    /// </summary>
    /// <remarks>
    /// The MARKER is the point of the whole probe, and it is deliberately loose: a path under a
    /// Rooms directory, or a file ending .arm. Both come from the reference tool's own tooltip
    /// rather than from a theory about what the game stores, and either one appearing in this
    /// report is the answer to where the room level lives.
    /// </remarks>
    private string Describe(PeekResult peek, ulong target)
    {
        // Text is re-read in full rather than taken from the summary - see MostPathChars for
        // why a trimmed one cannot answer this question.
        string what = peek.Kind switch
        {
            TargetKind.WideText => $"WideText \"{_reader.ReadUnicodeString(target, MostPathChars)}\"",
            TargetKind.Text => $"Text \"{_reader.ReadUtf8(target, MostPathChars)}\"",
            _ => $"{peek.Kind} {peek.Summary}",
        };

        return LooksLikeRoom(what) ? $"<<< ROOM? {what}" : what;
    }

    /// <summary>True when a summary reads like the path of a room file.</summary>
    public static bool LooksLikeRoom(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return summary.Contains("/Rooms/", StringComparison.OrdinalIgnoreCase)
            || summary.Contains(".arm", StringComparison.OrdinalIgnoreCase);
    }
}
