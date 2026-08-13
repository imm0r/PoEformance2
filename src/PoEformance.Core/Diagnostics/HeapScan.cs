using System.Buffers;
using System.Buffers.Binary;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>Somewhere in the target that refers to a row of a table.</summary>
/// <param name="At">Where the reference sits.</param>
/// <param name="Row">Which row it names.</param>
/// <param name="ByPointer">True for a pointer at the row, false for the row's 32-bit key.</param>
public readonly record struct RowSighting(ulong At, int Row, bool ByPointer);

/// <summary>What a whole-process scan cost and what it turned up.</summary>
/// <param name="Reach">
/// How far from the anchor the scan actually got, in bytes. Only meaningful next to
/// <c>Truncated</c>, where it is the difference between "nothing is out there" and "the budget
/// ran out two terabytes short of the interesting part".
/// </param>
public sealed record HeapScanResult(
    long RegionsWalked,
    long BytesScanned,
    long RegionsSkipped,
    IReadOnlyList<RowSighting> Sightings,
    bool Truncated,
    ulong Reach = 0);

/// <summary>
/// Searches the ENTIRE address space for references to a table's rows, instead of following
/// pointers from a root.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, and it is a confession as much as a feature. Every search this project has
/// made walks pointers from a static: statics to states, states to objects, objects to lists.
/// That can only ever find what is reachable from the roots it knows, and six sessions of
/// hunting a character's quest flags that way swept about 130 MB and found nothing but rules.
/// The explanation that survives all of it is an object hanging off none of those roots - and
/// no amount of following pointers can rule that in or out. This can.
///
/// TWO NEEDLES, both cheap per byte. A pointer that lands EXACTLY on the row grid of a table
/// is how the game's own data refers to a row, and a 32-bit key from the table's own index is
/// how a compact runtime structure would. Both are checked at every four-byte offset rather
/// than at their natural alignment, because this game's packed dat rows sit on odd addresses
/// and an aligned-only scan would miss every reference inside one.
///
/// HOW BIG THE GAP IS, measured once. A debugger scanned the whole address space for pointers
/// to a single QuestFlags row and found eight places holding one - two of them the table's own
/// indices, the rest scattered across the heap. NOT ONE of those eight addresses appears in ANY
/// recording this project has taken, including the six sessions that swept about 130 MB looking
/// for exactly this. That is the blind spot in one number: the sweeps were not unlucky, they
/// were looking somewhere else.
///
/// A HIT IS NOT A LIVE REFERENCE. The game allocates out of pools - a debugger watching one
/// block caught the free itself, three instructions that hang the block off a free list by
/// writing the list head into the block's own first eight bytes and pointing the head at the
/// block. Everything after that first qword is left exactly as the previous tenant wrote it,
/// so a freed block still holds whatever row pointers it held while it was in use, and those
/// are indistinguishable from live ones at the byte level. Two things follow for anything
/// reading these results: a sighting is a place to look, not a finding, and a cluster in one
/// region is worth more than a lone hit, because a live structure is referenced from
/// somewhere while abandoned bytes are not.
///
/// NOT RECORDED, ON PURPOSE. The scan reads in megabyte chunks, which is above the recorder's
/// cap, so a --record session does not swell by the size of the game's heap. The bytes that
/// matter are the HITS, and a caller that wants those in the recording should read their
/// neighbourhoods afterwards in ordinary small reads.
/// </remarks>
public static class HeapScan
{
    /// <summary>Read size. Above the recorder's cap, so a scan does not land in a recording.</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>
    /// Ceiling on one scan, so a walk that goes wrong stops rather than reading forever.
    /// </summary>
    /// <remarks>
    /// A game with everything loaded commits a few gigabytes; eight is room to spare without
    /// being unbounded. Reported as <c>Truncated</c> when it bites, because "found nothing"
    /// and "stopped looking" are different answers - a lesson this project has already paid
    /// for once, in the sweep whose budget quietly ran out four times running.
    /// </remarks>
    public const long ByteBudget = 8L * 1024 * 1024 * 1024;

    /// <summary>Largest single region taken seriously. Beyond this it is a reservation, not data.</summary>
    public const ulong LargestRegion = 2UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Walks every readable region and reports each reference to one of the table's rows.
    /// </summary>
    /// <param name="rowsBegin">First row of the table.</param>
    /// <param name="rows">How many rows it has.</param>
    /// <param name="rowSize">How many bytes one row takes.</param>
    /// <param name="keys">Row key (HASH32) to row index, from the table itself.</param>
    /// <param name="mostSightings">Stop collecting past this, so a pathological hit rate cannot exhaust memory.</param>
    /// <param name="anchor">
    /// Address to scan outward from, nearest region first. Zero keeps the enumeration order.
    /// </param>
    /// <remarks>
    /// THE ANCHOR IS WHAT MAKES THE BUDGET MEAN ANYTHING, and a live run is what showed it.
    /// Scanning in address order, the first 8 GB of the budget went entirely on mappings
    /// between 0x1E0_8030_0000 and 0x2166_3100_0000 - driver and reservation territory - and
    /// the walk stopped two terabytes of address space BELOW the game's own data heap. It
    /// reported 1790 references and not one of them was a pointer: every single one was a
    /// 32-bit hash coincidence, at a rate (1790 against ~2900 expected by chance over that many
    /// four-byte positions) that says noise and nothing else. Meanwhile the structural pass in
    /// the same run was reading real row pointers at 0x41EB_2A11_F5E, which the scan never
    /// reached. A budget spent on the wrong end of a 128 TB address space buys nothing.
    ///
    /// Anchoring on the table's own rows fixes it, and not by luck: whatever holds a reference
    /// to a row is something the game allocated, and this game's allocator keeps its data
    /// together - the table, the marker list, the state objects and the pool arena that
    /// recycles blocks all sit within a few gigabytes of each other. Nearest-first also makes
    /// truncation honest, because what gets cut is then the far and unlikely rather than
    /// whatever happened to sort last.
    /// </remarks>
    public static HeapScanResult Run(
        IMemoryReader reader,
        IMemoryRegions regions,
        ulong rowsBegin,
        long rows,
        int rowSize,
        IReadOnlyDictionary<uint, int> keys,
        IReadOnlyList<MemoryRegion>? exclude = null,
        int mostSightings = 4096,
        ulong anchor = 0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(keys);

        var sightings = new List<RowSighting>();
        long walked = 0, scanned = 0, skipped = 0;
        bool truncated = false;
        ulong reach = 0;
        ulong rowsEnd = rowsBegin + (ulong)(rows * rowSize);

        IEnumerable<MemoryRegion> order = anchor == 0
            ? regions.Regions()
            : [.. regions.Regions().OrderBy(r => Distance(r, anchor))];

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            foreach (MemoryRegion region in order)
            {
                // The TABLE'S OWN STORAGE is not a finding. Its rows point at their own Id
                // strings, and both of its indices hold one entry per row - so scanning them
                // yields 11,434 self-references on QuestFlags alone, which would fill the
                // sighting cap before the scan reached anything anybody wanted.
                if (region.Size > LargestRegion || Overlaps(region, exclude))
                {
                    skipped++;
                    continue;
                }

                if (scanned >= ByteBudget || sightings.Count >= mostSightings)
                {
                    truncated = true;
                    break;
                }

                walked++;
                reach = Math.Max(reach, Distance(region, anchor));
                for (ulong taken = 0; taken < region.Size; taken += ChunkBytes)
                {
                    int want = (int)Math.Min((ulong)ChunkBytes, region.Size - taken);

                    // Halve rather than give up: a region can be readable at its start and
                    // gone by its end - the game is running while this walks, and a page that
                    // was there when VirtualQueryEx answered may not be a moment later.
                    while (want >= sizeof(ulong) && !reader.TryRead(region.Address + taken, buffer.AsSpan(0, want)))
                    {
                        want /= 2;
                    }

                    if (want < sizeof(ulong))
                    {
                        break;
                    }

                    scanned += want;
                    Sweep(buffer.AsSpan(0, want), region.Address + taken, rowsBegin, rowsEnd, rowSize, keys, sightings, mostSightings);

                    if (sightings.Count >= mostSightings)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new HeapScanResult(walked, scanned, skipped, sightings, truncated, reach);
    }

    /// <summary>
    /// How far a region sits from the anchor - zero when it contains it.
    /// </summary>
    /// <remarks>
    /// Measured to the NEAR EDGE rather than to the start, so a large region beginning below
    /// the anchor and ending above it is not pushed to the back of the queue by its own size.
    /// </remarks>
    private static ulong Distance(MemoryRegion region, ulong anchor)
    {
        if (anchor == 0)
        {
            return 0;
        }

        ulong end = region.Address + region.Size;
        if (anchor >= region.Address && anchor < end)
        {
            return 0;
        }

        return anchor < region.Address ? region.Address - anchor : anchor - end;
    }

    /// <summary>True when a region touches any of the ranges the caller wants left alone.</summary>
    private static bool Overlaps(MemoryRegion region, IReadOnlyList<MemoryRegion>? exclude)
    {
        if (exclude is null)
        {
            return false;
        }

        ulong end = region.Address + region.Size;
        foreach (MemoryRegion other in exclude)
        {
            if (region.Address < other.Address + other.Size && other.Address < end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Checks one chunk for both needles at every four-byte offset.</summary>
    private static void Sweep(
        ReadOnlySpan<byte> chunk,
        ulong at,
        ulong rowsBegin,
        ulong rowsEnd,
        int rowSize,
        IReadOnlyDictionary<uint, int> keys,
        List<RowSighting> into,
        int most)
    {
        for (int i = 0; i + sizeof(uint) <= chunk.Length && into.Count < most; i += sizeof(uint))
        {
            if (keys.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(chunk[i..]), out int keyed))
            {
                into.Add(new RowSighting(at + (ulong)i, keyed, ByPointer: false));
            }

            if (i + sizeof(ulong) > chunk.Length)
            {
                continue;
            }

            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(chunk[i..]);
            if (value < rowsBegin || value >= rowsEnd)
            {
                continue;
            }

            ulong offset = value - rowsBegin;
            if (offset % (ulong)rowSize == 0)
            {
                into.Add(new RowSighting(at + (ulong)i, (int)(offset / (ulong)rowSize), ByPointer: true));
            }
        }
    }
}
