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

    /// <summary>Where the terrain struct has room for something nothing has named.</summary>
    private const int TerrainGapFrom = 0x30;

    private const int TerrainGapTo = 0xD0;

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
    /// Walks one tile entry and the terrain struct's unclaimed span, and says what is there.
    /// </summary>
    /// <param name="terrainBase">The terrain struct, as a base address - it is inline.</param>
    /// <param name="tileEntry">One tile's 0x38 bytes, by address.</param>
    /// <param name="tileName">What that tile is called, so the report says which one it is.</param>
    public IReadOnlyList<string> Probe(ulong terrainBase, ulong tileEntry, string tileName)
    {
        var lines = new List<string>();

        if (MemoryReaderExtensions.IsPlausiblePointer(tileEntry))
        {
            lines.Add($"tile 0x{tileEntry:X}  {tileName}");
            Walk(lines, tileEntry, 0, TileEntrySize);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(terrainBase))
        {
            lines.Add($"terrain 0x{terrainBase:X}  +0x{TerrainGapFrom:X}..0x{TerrainGapTo:X}");
            Walk(lines, terrainBase, TerrainGapFrom, TerrainGapTo);
        }

        if (lines.Count == 0)
        {
            lines.Add("nothing to probe - no terrain or no tile");
        }

        return lines;
    }

    /// <summary>Classifies every pointer in a span, and follows the promising ones once more.</summary>
    private void Walk(List<string> lines, ulong at, int from, int to)
    {
        var block = new byte[to - from];
        if (!_reader.TryRead(at + (ulong)from, block))
        {
            lines.Add($"  +0x{from:X2}  unreadable ({block.Length} bytes)");
            return;
        }

        for (int offset = 0; offset + 8 <= block.Length && lines.Count < MostLines; offset += 8)
        {
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
            lines.Add($"  +0x{slot:X2}  {Describe(peek, value)}");

            // One hop further, and only for the shape that could be carrying a name: a
            // pointer to a structure. Text found there is reported with BOTH offsets, because
            // "+0x10 then +0x08" is the field this would become.
            if (peek.Kind is TargetKind.Structure or TargetKind.Vector)
            {
                Follow(lines, value, slot);
            }
        }
    }

    /// <summary>Looks one level inside a structure for the text it might be holding.</summary>
    private void Follow(List<string> lines, ulong at, int slot)
    {
        var block = new byte[SecondHopBytes];
        if (!_reader.TryRead(at, block))
        {
            return;
        }

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
            if (peek.Kind is TargetKind.WideText or TargetKind.Text)
            {
                lines.Add($"    +0x{slot:X2}+0x{offset:X2}  {Describe(peek, value)}");
            }
        }
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
