using System.Buffers;
using System.Diagnostics;
using System.Text;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>A run of bytes to look for in the target.</summary>
/// <param name="Name">What it is, for the list of places - "Texturing_Calc as UTF-16", say.</param>
/// <param name="Bytes">The bytes, in the order they lie in memory.</param>
public sealed record PatternNeedle(string Name, byte[] Bytes)
{
    /// <summary>A text as the game might keep it: one byte a character, and two.</summary>
    public static IReadOnlyList<PatternNeedle> Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return
        [
            new PatternNeedle(text, Encoding.ASCII.GetBytes(text)),
            new PatternNeedle(text + " as UTF-16", Encoding.Unicode.GetBytes(text)),
        ];
    }

    /// <summary>A pointer to an address: its eight bytes, lowest first.</summary>
    public static PatternNeedle Pointer(ulong address)
        => new(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"a pointer to 0x{address:X}"), BitConverter.GetBytes(address));
}

/// <summary>Where a needle was found, and the bytes round it.</summary>
/// <param name="At">The address of its first byte.</param>
/// <param name="Needle">Which needle, by its index in the list asked for.</param>
/// <param name="From">The address of the first byte of <paramref name="Around"/>.</param>
/// <param name="Around">The bytes either side of it, the needle's own among them - empty past <see cref="PatternHunt.MostWithContext"/> places, or where they would not read.</param>
public sealed record PatternSighting(ulong At, int Needle, ulong From, byte[] Around);

/// <summary>What a pattern hunt looked through and what it found.</summary>
/// <param name="RegionsWalked">How many regions were read.</param>
/// <param name="BytesScanned">How many bytes.</param>
/// <param name="Sightings">Every place a needle was found, at most <see cref="PatternHunt.MostPerNeedle"/> a needle, in address order.</param>
/// <param name="Capped">The needles found more often than that - the count is then a floor.</param>
/// <param name="Truncated">True when the byte budget stopped it before the last region.</param>
/// <param name="Took">How long it took.</param>
public sealed record PatternHuntResult(
    long RegionsWalked,
    long BytesScanned,
    IReadOnlyList<PatternSighting> Sightings,
    IReadOnlyList<int> Capped,
    bool Truncated,
    TimeSpan Took);

/// <summary>
/// Searches the whole of the target's memory for runs of bytes - a name as text, or a pointer to an address - and reads back what lies round each place found.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR: a question no file answers but the game's own data does. The order the engine
/// runs a material's shader stages in is written in no shader source (ShadeProgram.Stages), yet the
/// engine must hold the stage names somewhere to match a graph's "Texturing_Calc" against; where it
/// keeps them, and what it keeps beside them, may well be that order. A name found as text says where;
/// a pointer to that text says who refers to it - a table of names is a row of such pointers.
///
/// EACH NEEDLE BY THE RUNTIME'S OWN SEARCH, a chunk at a time: MemoryExtensions.IndexOf is vectorised
/// and runs at memory speed for a rare pattern, which every needle here is. A few needles is a few
/// passes over each chunk, which costs less than reading it from the game did.
///
/// NEAREST THE ANCHOR FIRST, within HeapScan's budget unless the caller asks for more, for the reason
/// its remarks give. Not recorded, like FloatHunt: it reads in megabyte chunks.
/// </remarks>
public static class PatternHunt
{
    /// <summary>Most places kept for one needle; past it the needle is reported as capped.</summary>
    public const int MostPerNeedle = 64;

    /// <summary>The longest needle, in bytes.</summary>
    public const int LongestNeedle = 256;

    /// <summary>How many bytes either side of a place are read back.</summary>
    public const int Context = 128;

    /// <summary>How many places, in all, have their surroundings read back.</summary>
    public const int MostWithContext = 64;

    /// <summary>
    /// Walks every readable region, nearest the anchor first, and reports where each needle lies.
    /// </summary>
    /// <param name="reader">The target's memory.</param>
    /// <param name="regions">Its regions.</param>
    /// <param name="needles">What to look for - none empty, none longer than <see cref="LongestNeedle"/>.</param>
    /// <param name="anchor">An address to start from; nought keeps the enumeration order.</param>
    /// <param name="progress">Where to say how far it has got, or null.</param>
    /// <param name="cancel">Stops it between chunks.</param>
    /// <param name="budget">The most bytes looked through.</param>
    public static PatternHuntResult Run(
        IMemoryReader reader,
        IMemoryRegions regions,
        IReadOnlyList<PatternNeedle> needles,
        ulong anchor = 0,
        FloatHuntProgress? progress = null,
        CancellationToken cancel = default,
        long budget = HeapScan.ByteBudget)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(needles);
        foreach (PatternNeedle needle in needles)
        {
            if (needle.Bytes.Length is 0 or > LongestNeedle)
            {
                throw new ArgumentException($"a needle of {needle.Bytes.Length} bytes: one to {LongestNeedle} are looked for", nameof(needles));
            }
        }

        var clock = Stopwatch.StartNew();
        var places = new List<(ulong At, int Needle)>();
        var found = new int[needles.Count];
        var capped = new HashSet<int>();
        long walked = 0, scanned = 0;
        bool truncated = false;
        if (needles.Count == 0)
        {
            return new PatternHuntResult(0, 0, [], [], false, clock.Elapsed);
        }

        int overlap = needles.Max(one => one.Bytes.Length) - 1;
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
                    // A FEW BYTES PAST THE CHUNK, so a needle that starts in it and ends in the next is
                    // still matched whole; each start is counted in the one chunk it begins in.
                    int want = (int)Math.Min((ulong)HeapScan.ChunkBytes, region.Size - taken);
                    int extra = (int)Math.Min((ulong)overlap, region.Size - taken - (ulong)want);
                    while (want > 0 && !reader.TryRead(region.Address + taken, buffer.AsSpan(0, want + extra)))
                    {
                        want /= 2;
                        extra = 0;
                    }

                    if (want <= 0)
                    {
                        break;
                    }

                    scanned += want;
                    progress?.Add(want);
                    Sweep(buffer.AsSpan(0, want + extra), want, region.Address + taken, needles, found, capped, places);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new PatternHuntResult(walked, scanned, Sighted(reader, needles, places), [.. capped.Order()], truncated, clock.Elapsed);
    }

    /// <summary>
    /// The places as text to paste: each with its needle, and the bytes round it sixteen to a row, hex beside what they read as.
    /// </summary>
    public static string Report(PatternHuntResult result, IReadOnlyList<PatternNeedle> needles)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(needles);
        var said = new StringBuilder();
        said.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"looked for {string.Join(", ", needles.Select(one => one.Name))} through {result.BytesScanned / (1024.0 * 1024 * 1024):0.00} GB in {result.RegionsWalked} regions in {result.Took.TotalSeconds:0.0} s")
            .AppendLine(result.Truncated ? " - stopped at the budget, so what is not found may lie past it" : string.Empty);
        foreach (int capped in result.Capped)
        {
            said.Append(System.Globalization.CultureInfo.InvariantCulture, $"{needles[capped].Name}: more than {MostPerNeedle} places, the first {MostPerNeedle} listed").AppendLine();
        }

        said.Append(System.Globalization.CultureInfo.InvariantCulture, $"{result.Sightings.Count} place{(result.Sightings.Count == 1 ? string.Empty : "s")}").AppendLine();
        foreach (PatternSighting one in result.Sightings)
        {
            said.AppendLine().Append(System.Globalization.CultureInfo.InvariantCulture, $"0x{one.At:X}: {needles[one.Needle].Name}").AppendLine();
            for (var row = 0; row < one.Around.Length; row += 16)
            {
                ReadOnlySpan<byte> bytes = one.Around.AsSpan(row, Math.Min(16, one.Around.Length - row));
                said.Append(System.Globalization.CultureInfo.InvariantCulture, $"  {one.From + (ulong)row:X12}  ");
                for (var at = 0; at < 16; at++)
                {
                    said.Append(at < bytes.Length ? bytes[at].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : "  ").Append(at == 7 ? "  " : " ");
                }

                said.Append(' ').AppendLine(Readable(bytes));
            }
        }

        return said.ToString();
    }

    /// <summary>Bytes as the characters they would be, a dot for what is not a printable one - a name kept two bytes a character reads with a dot between its letters.</summary>
    public static string Readable(ReadOnlySpan<byte> bytes)
    {
        var said = new char[bytes.Length];
        for (var at = 0; at < bytes.Length; at++)
        {
            said[at] = bytes[at] is >= 0x20 and < 0x7F ? (char)bytes[at] : '.';
        }

        return new string(said);
    }

    /// <summary>Every needle's places in one chunk, the ones starting past its own bytes left to the next.</summary>
    private static void Sweep(
        ReadOnlySpan<byte> chunk, int starts, ulong at, IReadOnlyList<PatternNeedle> needles, int[] found, HashSet<int> capped, List<(ulong At, int Needle)> into)
    {
        for (var needle = 0; needle < needles.Count; needle++)
        {
            ReadOnlySpan<byte> pattern = needles[needle].Bytes;
            int from = 0;
            while (from < starts)
            {
                int hit = chunk[from..].IndexOf(pattern);
                if (hit < 0 || from + hit >= starts)
                {
                    break;
                }

                if (found[needle] >= MostPerNeedle)
                {
                    capped.Add(needle);
                    break;
                }

                found[needle]++;
                into.Add((at + (ulong)(from + hit), needle));
                from += hit + 1;
            }
        }
    }

    /// <summary>The places in address order, the first ones with what lies round them.</summary>
    private static List<PatternSighting> Sighted(IMemoryReader reader, IReadOnlyList<PatternNeedle> needles, List<(ulong At, int Needle)> places)
    {
        var sightings = new List<PatternSighting>(places.Count);
        foreach ((ulong at, int needle) in places.OrderBy(one => one.At).ThenBy(one => one.Needle))
        {
            if (sightings.Count >= MostWithContext)
            {
                sightings.Add(new PatternSighting(at, needle, at, []));
                continue;
            }

            ulong from = at > Context ? at - Context : 0;
            var bytes = new byte[(int)(at - from) + needles[needle].Bytes.Length + Context];
            sightings.Add(reader.TryRead(from, bytes) ? new PatternSighting(at, needle, from, bytes) : new PatternSighting(at, needle, at, []));
        }

        return sightings;
    }
}
