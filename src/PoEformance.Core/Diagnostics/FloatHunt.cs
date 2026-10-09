using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>A run of floats to look for in the target, side by side.</summary>
/// <param name="Name">What it is, for the report.</param>
/// <param name="Values">The floats in order; a NaN is a place left unchecked - padding in a matrix row, say.</param>
/// <param name="Tolerance">How far each float may be from its value; nought asks for the exact value.</param>
/// <param name="Around">How many bytes either side of its first few places to read back with the result - nought for none. See <see cref="FloatDump"/>.</param>
public sealed record FloatNeedle(string Name, float[] Values, float Tolerance, int Around = 0);

/// <summary>The bytes read round a place a needle was found, for a person to see what the game keeps beside it.</summary>
/// <param name="At">Where the needle was found.</param>
/// <param name="Needle">Which needle.</param>
/// <param name="From">The address of the first byte read.</param>
/// <param name="Bytes">What was there.</param>
public sealed record FloatDump(ulong At, int Needle, ulong From, byte[] Bytes);

/// <summary>Where a needle was found.</summary>
/// <param name="At">The address of its first float.</param>
/// <param name="Needle">Which needle, by its index in the list asked for.</param>
public readonly record struct FloatSighting(ulong At, int Needle);

/// <summary>How far a running hunt has got - read from another thread while it runs.</summary>
public sealed class FloatHuntProgress
{
    private long _bytes;

    /// <summary>How many bytes have been looked through so far.</summary>
    public long Bytes => Volatile.Read(ref _bytes);

    internal void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);
}

/// <summary>What a float hunt looked through and what it found.</summary>
/// <param name="RegionsWalked">How many regions were read.</param>
/// <param name="BytesScanned">How many bytes.</param>
/// <param name="Sightings">Every place a needle was found, at most <see cref="FloatHunt.MostPerNeedle"/> a needle.</param>
/// <param name="Capped">The needles found more often than that - the count is then a floor.</param>
/// <param name="Unsearched">The needles that could not be looked for: nothing in them but nought, one and blanks.</param>
/// <param name="Truncated">True when the byte budget stopped it before the last region.</param>
/// <param name="Took">How long it took.</param>
/// <param name="Dumps">The bytes round the first places of the needles that asked for them, or null.</param>
public sealed record FloatHuntResult(
    long RegionsWalked,
    long BytesScanned,
    IReadOnlyList<FloatSighting> Sightings,
    IReadOnlyList<int> Capped,
    IReadOnlyList<int> Unsearched,
    bool Truncated,
    TimeSpan Took,
    IReadOnlyList<FloatDump>? Dumps = null);

/// <summary>
/// Searches the whole of the target's memory for runs of floats near given values - a vector or a matrix the caller can work out, to learn where and in which form the game keeps it.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR: a value the game computes on its processor and writes in no file - the sun's
/// direction from an environment's two angles, say - can be worked out every way it might be, and
/// the way the game holds is the one found in its memory. The search asks the game rather than a
/// screenshot, which is what this project prefers wherever it can.
///
/// EACH NEEDLE IS LOOKED FOR BY ONE FLOAT FIRST - its most telling one, never a nought or a one,
/// which half of memory is - and only where that one matches are the rest compared. One cheap pass
/// comes before any of that: each word's top sixteen bits - a float's sign, exponent and first
/// mantissa bits - looked up in a bitmap of the buckets the anchors fall in. A few dozen buckets of
/// 65536 are marked, so hardly a word in a thousand goes further. About 2.7 GB a second on one
/// core against a test heap a fifth of it floats, before the cost of reading the game.
///
/// NEAREST THE ANCHOR FIRST, within the same budget as <see cref="HeapScan"/>, for the reason its
/// remarks give: in address order the budget goes on driver mappings far below the game's heap.
///
/// NOT RECORDED: it reads in megabyte chunks, above the recorder's cap, like HeapScan.
/// </remarks>
public static class FloatHunt
{
    /// <summary>Most places kept for one needle; past it the needle is reported as capped.</summary>
    public const int MostPerNeedle = 64;

    /// <summary>The longest needle, in floats - a 4 by 4 matrix.</summary>
    public const int LongestNeedle = 16;

    /// <summary>How many of a needle's places have their surroundings read back, where it asks.</summary>
    public const int MostDumps = 4;

    /// <summary>The most bytes read either side of a place.</summary>
    public const int MostAround = 4096;

    /// <summary>
    /// Walks every readable region, nearest the anchor first, and reports where each needle lies.
    /// </summary>
    /// <param name="reader">The target's memory.</param>
    /// <param name="regions">Its regions.</param>
    /// <param name="needles">What to look for.</param>
    /// <param name="anchor">An address in the game's own heap to start from; nought keeps the enumeration order.</param>
    /// <param name="progress">Where to say how far it has got, or null.</param>
    /// <param name="cancel">Stops it between chunks.</param>
    /// <param name="budget">The most bytes looked through - HeapScan's budget unless the caller wants further.</param>
    public static FloatHuntResult Run(
        IMemoryReader reader,
        IMemoryRegions regions,
        IReadOnlyList<FloatNeedle> needles,
        ulong anchor = 0,
        FloatHuntProgress? progress = null,
        CancellationToken cancel = default,
        long budget = HeapScan.ByteBudget)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(needles);

        var clock = Stopwatch.StartNew();
        var unsearched = new List<int>();
        var anchors = new List<Anchor>(needles.Count);
        int longest = 1;
        for (var at = 0; at < needles.Count; at++)
        {
            FloatNeedle needle = needles[at];
            if (needle.Values.Length is 0 or > LongestNeedle || AnchorOf(needle.Values) is not { } first)
            {
                unsearched.Add(at);
                continue;
            }

            float value = needle.Values[first];
            float tolerance = MathF.Max(0f, needle.Tolerance);
            anchors.Add(new Anchor(at, first, value - tolerance, value + tolerance));
            longest = Math.Max(longest, needle.Values.Length);
        }

        var sightings = new List<FloatSighting>();
        var found = new int[needles.Count];
        var capped = new HashSet<int>();
        long walked = 0, scanned = 0;
        bool truncated = false;
        if (anchors.Count == 0)
        {
            return new FloatHuntResult(0, 0, sightings, [], unsearched, false, clock.Elapsed);
        }

        Anchor[] sorted = [.. anchors.OrderBy(one => one.Low)];
        float widest = sorted.Max(one => one.High - one.Low);
        ulong[] buckets = Buckets(sorted);
        int overlap = longest * sizeof(float);
        IEnumerable<MemoryRegion> order = anchor == 0
            ? regions.Regions()
            : [.. regions.Regions().OrderBy(one => HeapScan.Distance(one, anchor))];

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeapScan.ChunkBytes + overlap);
        try
        {
            foreach (MemoryRegion region in order)
            {
                if (region.Size > HeapScan.LargestRegion)
                {
                    continue;
                }

                if (scanned >= budget)
                {
                    truncated = true;
                    break;
                }

                cancel.ThrowIfCancellationRequested();
                walked++;
                for (ulong taken = 0; taken < region.Size; taken += HeapScan.ChunkBytes)
                {
                    // A FEW FLOATS PAST THE CHUNK, so a needle that starts in it and ends in the next
                    // is still compared whole; each start is tried in the one chunk it begins in.
                    int want = (int)Math.Min((ulong)HeapScan.ChunkBytes, region.Size - taken);
                    int extra = (int)Math.Min((ulong)overlap, region.Size - taken - (ulong)want);
                    while (want >= sizeof(float) && !reader.TryRead(region.Address + taken, buffer.AsSpan(0, want + extra)))
                    {
                        want /= 2;
                        extra = 0;
                    }

                    if (want < sizeof(float))
                    {
                        break;
                    }

                    scanned += want;
                    progress?.Add(want);
                    Sweep(buffer.AsSpan(0, (want + extra) & ~3), want, region.Address + taken, needles, sorted, widest, buckets, found, capped, sightings);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new FloatHuntResult(walked, scanned, sightings, [.. capped.Order()], unsearched, truncated, clock.Elapsed, Dumps(reader, needles, sightings));
    }

    /// <summary>The bytes round the first places of each needle that asked for them, windows that overlap one already read left out.</summary>
    /// <remarks>
    /// THE NARROWEST WINDOWS FIRST, then by address: a wide window asked for to read one value well
    /// past its needle - the light hunt's dust colour, 0x360 bytes after phi - would otherwise swallow
    /// a narrow one asked for to be printed whole, the sun's vector among them.
    /// </remarks>
    private static List<FloatDump> Dumps(IMemoryReader reader, IReadOnlyList<FloatNeedle> needles, List<FloatSighting> sightings)
    {
        var dumps = new List<FloatDump>();
        var taken = new int[needles.Count];
        foreach (FloatSighting one in sightings.OrderBy(sighting => Math.Clamp(needles[sighting.Needle].Around, 0, MostAround)).ThenBy(sighting => sighting.At))
        {
            int around = Math.Clamp(needles[one.Needle].Around, 0, MostAround);
            if (around == 0 || taken[one.Needle] >= MostDumps || dumps.Exists(dump => one.At >= dump.From && one.At < dump.From + (ulong)dump.Bytes.Length))
            {
                continue;
            }

            ulong from = one.At > (ulong)around ? one.At - (ulong)around : 0;
            var bytes = new byte[(2 * around) + (needles[one.Needle].Values.Length * sizeof(float))];
            if (reader.TryRead(from, bytes))
            {
                taken[one.Needle]++;
                dumps.Add(new FloatDump(one.At, one.Needle, from, bytes));
            }
        }

        return dumps;
    }

    /// <summary>The float a needle is first looked for by: its largest that is not nought, one or a blank - null where it has none.</summary>
    internal static int? AnchorOf(float[] values)
    {
        int? best = null;
        for (var at = 0; at < values.Length; at++)
        {
            float value = values[at];
            if (!float.IsFinite(value) || value == 0f || MathF.Abs(value) == 1f)
            {
                continue;
            }

            if (best is not { } was || MathF.Abs(value) > MathF.Abs(values[was]))
            {
                best = at;
            }
        }

        return best;
    }

    /// <summary>
    /// The buckets the anchors fall in, one bit each: a float's bucket is its top sixteen bits, which order the floats of one sign by size.
    /// </summary>
    private static ulong[] Buckets(Anchor[] anchors)
    {
        var marks = new ulong[(1 << 16) / 64];
        foreach (Anchor one in anchors)
        {
            // ACROSS NOUGHT the two signs are two runs of buckets, each from nought outward.
            if (one.Low < 0f && one.High > 0f)
            {
                Mark(marks, one.Low, -0f);
                Mark(marks, 0f, one.High);
            }
            else
            {
                Mark(marks, one.Low, one.High);
            }
        }

        return marks;
    }

    /// <summary>Marks every bucket from one float's to another's, both of one sign.</summary>
    private static void Mark(ulong[] marks, float from, float to)
    {
        uint a = Bucket(from), b = Bucket(to);
        for (uint bucket = Math.Min(a, b); bucket <= Math.Max(a, b); bucket++)
        {
            marks[bucket >> 6] |= 1UL << (int)(bucket & 63);
        }
    }

    private static uint Bucket(float value) => (uint)BitConverter.SingleToInt32Bits(value) >> 16;

    /// <summary>Checks one chunk: each word by its bucket, and the few in a marked one against the anchors.</summary>
    /// <param name="chunk">The chunk and the few floats read past it.</param>
    /// <param name="starts">How many of its bytes are this chunk's own - a needle starting past them is the next chunk's.</param>
    /// <remarks>
    /// ONE SHIFT AND ONE BIT TEST A WORD, and that is all nearly every word costs: a pass over the
    /// floats' size a vector at a time came first once, and it was slower - in memory full of floats
    /// most vectors hold one of the right size, and picking the lanes back out cost more than it saved.
    /// </remarks>
    private static void Sweep(
        ReadOnlySpan<byte> chunk, int starts, ulong at, IReadOnlyList<FloatNeedle> needles, Anchor[] anchors, float widest, ulong[] buckets,
        int[] found, HashSet<int> capped, List<FloatSighting> into)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(chunk);
        ReadOnlySpan<ulong> marks = buckets;
        for (var index = 0; index < words.Length; index++)
        {
            uint bucket = words[index] >> 16;
            if ((marks[(int)(bucket >> 6)] & (1UL << (int)(bucket & 63))) != 0)
            {
                Check(MemoryMarshal.Cast<byte, float>(chunk), index, starts, at, needles, anchors, widest, found, capped, into);
            }
        }
    }

    /// <summary>
    /// Compares the needles whose first float this one may be, found by a binary search on the anchors' lower bounds - there are hundreds of them when every turn of a matrix is looked for.
    /// </summary>
    private static void Check(
        ReadOnlySpan<float> floats, int index, int starts, ulong at, IReadOnlyList<FloatNeedle> needles, Anchor[] anchors, float widest,
        int[] found, HashSet<int> capped, List<FloatSighting> into)
    {
        float value = floats[index];
        int low = 0, high = anchors.Length;
        float least = value - widest;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (anchors[middle].Low < least)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (int place = low; place < anchors.Length; place++)
        {
            Anchor one = anchors[place];
            if (one.Low > value)
            {
                break;
            }

            if (one.High < value)
            {
                continue;
            }

            FloatNeedle needle = needles[one.Needle];
            int start = index - one.Index;
            if (start < 0 || start * sizeof(float) >= starts || start + needle.Values.Length > floats.Length)
            {
                continue;
            }

            if (!Matches(floats.Slice(start, needle.Values.Length), needle))
            {
                continue;
            }

            if (found[one.Needle] >= MostPerNeedle)
            {
                capped.Add(one.Needle);
                continue;
            }

            found[one.Needle]++;
            into.Add(new FloatSighting(at + (ulong)(start * sizeof(float)), one.Needle));
        }
    }

    private static bool Matches(ReadOnlySpan<float> floats, FloatNeedle needle)
    {
        float tolerance = MathF.Max(0f, needle.Tolerance);
        for (var at = 0; at < floats.Length; at++)
        {
            float wanted = needle.Values[at];
            if (float.IsNaN(wanted))
            {
                continue;
            }

            if (!(MathF.Abs(floats[at] - wanted) <= tolerance))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A needle's first float and the values it may take.</summary>
    private readonly record struct Anchor(int Needle, int Index, float Low, float High);
}
