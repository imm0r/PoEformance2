using System.Globalization;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>One step of a pointer path and what it resolved to.</summary>
/// <param name="Label">How the step reads, e.g. <c>[0x7FF61929C3A8] + 0x235C</c>.</param>
/// <param name="From">The address this step dereferenced. Zero for the first step.</param>
/// <param name="Pointee">What was stored there. Zero for the first step, or when the read failed.</param>
/// <param name="Result">Where the path stands after this step.</param>
/// <param name="Read">False when the dereference failed - the path stops there.</param>
public readonly record struct PathHop(string Label, ulong From, ulong Pointee, ulong Result, bool Read);

/// <summary>Where a pointer path ended up.</summary>
/// <param name="ObjectBase">
/// The object the last offset was applied to, so the target can be named as
/// <c>object + 0x2358</c> instead of as an address that is gone after a restart. Zero for a
/// path with no offsets, where the address IS the whole answer.
/// </param>
public sealed record PathResolution(IReadOnlyList<PathHop> Hops, ulong Target, ulong ObjectBase, bool Ok);

/// <summary>
/// A Cheat Engine pointer path: a base, then a chain of "dereference and add".
/// </summary>
/// <remarks>
/// This exists so a finding can arrive in the form people actually find things in. A Cheat
/// Engine table hands over <c>PathOfExileSteam.exe+468C3A8</c> with an offset list, and until
/// something here can take that as written it has to be transcribed by hand into an absolute
/// address - which is both a chance to get it wrong and a value that dies with the process
/// that produced it.
///
/// One chain, no branches. Deliberately: the point is to look at ONE address that somebody
/// already found, not to search.
/// </remarks>
public sealed record PointerPath(ulong BaseAddress, bool ModuleRelative, IReadOnlyList<long> Offsets)
{
    /// <summary>
    /// Parses <c>module+RVA[,offset]...</c>, or an absolute <c>0xADDRESS[,offset]...</c>.
    /// </summary>
    /// <remarks>
    /// Everything is hexadecimal, with or without the <c>0x</c>, because that is how both
    /// Cheat Engine and this project write addresses - a path that quietly read <c>235C</c>
    /// as decimal would resolve to a real, readable, wrong address.
    ///
    /// The module NAME is ignored rather than matched. There is exactly one module this tool
    /// attaches to, and the executable is called something different for the Steam build than
    /// for the standalone - so checking the name could only ever reject a path that is right.
    /// </remarks>
    public static bool TryParse(string text, out PointerPath? path, out string error)
    {
        path = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty path";
            return false;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "empty path";
            return false;
        }

        string head = parts[0];
        bool moduleRelative = false;

        // A '+' means "somewhere in the module", whatever sits in front of it. The last one
        // wins so that a path is still parsed when the name itself carries one.
        int plus = head.LastIndexOf('+');
        if (plus >= 0)
        {
            moduleRelative = true;
            head = head[(plus + 1)..].Trim();
        }

        if (!TryParseHex(head, out long baseValue) || baseValue < 0)
        {
            error = $"\"{parts[0]}\" is not an address";
            return false;
        }

        var offsets = new List<long>(parts.Length - 1);
        foreach (string part in parts[1..])
        {
            if (!TryParseHex(part, out long offset))
            {
                error = $"\"{part}\" is not an offset";
                return false;
            }

            offsets.Add(offset);
        }

        path = new PointerPath((ulong)baseValue, moduleRelative, offsets);
        return true;
    }

    /// <summary>Walks the path, keeping every intermediate step so a break is visible.</summary>
    /// <remarks>
    /// A failed hop STOPS the walk and is reported, rather than being folded into a zero
    /// result. "The static holds nothing" and "the object has nothing at that offset" are
    /// different problems with different fixes, and a chain that only ever answers 0 cannot
    /// tell them apart.
    /// </remarks>
    public PathResolution Resolve(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        ulong current = ModuleRelative ? reader.ModuleBase + BaseAddress : BaseAddress;
        var hops = new List<PathHop>(Offsets.Count + 1)
        {
            new(
                ModuleRelative ? $"module+0x{BaseAddress:X}" : $"0x{BaseAddress:X}",
                From: 0,
                Pointee: 0,
                Result: current,
                Read: true),
        };

        ulong objectBase = 0;
        foreach (long offset in Offsets)
        {
            if (!reader.TryRead(current, out ulong pointee))
            {
                hops.Add(new PathHop($"[0x{current:X}]", current, 0, 0, Read: false));
                return new PathResolution(hops, 0, objectBase, Ok: false);
            }

            objectBase = pointee;
            current = (ulong)((long)pointee + offset);
            hops.Add(new PathHop(
                $"[0x{hops[^1].Result:X}] + 0x{offset:X}", hops[^1].Result, pointee, current, Read: true));
        }

        return new PathResolution(hops, current, objectBase, Ok: true);
    }

    private static bool TryParseHex(string text, out long value)
    {
        string body = text.Trim();
        bool negative = body.StartsWith('-');
        if (negative || body.StartsWith('+'))
        {
            body = body[1..].Trim();
        }

        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            body = body[2..];
        }

        if (!long.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }
}

/// <summary>
/// Looks at one address somebody already found, and says what is actually there.
/// </summary>
/// <remarks>
/// THE TRAP THIS WAS BUILT FOR, because it cost a session before anything here existed. A
/// Cheat Engine four-byte scan reported a value that was 999 while the cursor sat on an
/// inventory item and 1000 while it did not - a clean, repeatable, two-state signal, and
/// entirely an artefact. 999 is 0x3E7 and 1000 is 0x3E8: they are the TOP THIRTY-TWO BITS of
/// a 64-bit heap pointer, seen through a four-byte window placed four bytes into an eight-byte
/// slot. What actually changed was the whole pointer, and the two "values" were only the two
/// halves of the heap the game happened to be handing out from that run.
///
/// Measured, not assumed. Across one 500,000-read recording, 99.4% of every four-byte 999 in
/// it and 99.99% of every 1000 sat at an address four past an eight-byte boundary, and the
/// two were the commonest non-zero high halves in the whole file at 28% and 9%. Across the
/// committed fixtures the same pair shows up as 1054/1055, 646/647, 940/941 and 1513 - one
/// per process - which is the other half of the lesson: an equality test against 999 is a
/// test against one launch of the game.
///
/// So the first thing this prints about an unaligned address is what the aligned slot around
/// it holds. Everything else - the window, the pointer summaries, the object-relative offsets
/// - is there to answer the question that follows immediately after.
/// </remarks>
public static class AddressPeek
{
    /// <summary>How much of the window sits before the target slot, in bytes.</summary>
    public const int DefaultBefore = 0x20;

    /// <summary>And after it, including the slot itself.</summary>
    public const int DefaultAfter = 0x40;

    /// <summary>
    /// Reads a window of eight-byte slots, one read each.
    /// </summary>
    /// <remarks>
    /// Slot by slot rather than one block read, because the interesting addresses are the
    /// ones nobody has mapped yet and a window that crosses into an unmapped page fails as a
    /// whole. Per slot, the part that IS there still arrives; the rest reads as null, which
    /// is a fact about the process worth showing rather than a failure of the peek.
    /// </remarks>
    public static ulong?[] Sample(IMemoryReader reader, ulong windowStart, int slots)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(slots);

        var window = new ulong?[slots];
        for (int i = 0; i < slots; i++)
        {
            window[i] = reader.TryRead(windowStart + (ulong)(i * 8), out ulong value) ? value : null;
        }

        return window;
    }

    /// <summary>Where the window around a target starts, and which slot the target is in.</summary>
    public static (ulong Start, int Slots, int TargetSlot) Window(ulong target, int before, int after)
    {
        ulong aligned = target & ~7UL;
        ulong start = aligned >= (ulong)before ? aligned - (ulong)before : 0;
        int slots = (before + after) / 8;
        return (start, Math.Max(slots, 1), (int)((aligned - start) / 8));
    }

    /// <summary>Everything worth saying about one address, as lines to print.</summary>
    /// <param name="tables">
    /// Passed through to <see cref="PointerPeek"/> so a pointer that lands on a content table
    /// is named instead of shown as anonymous bytes. Optional.
    /// </param>
    public static IReadOnlyList<string> Report(
        IMemoryReader reader,
        PointerPath path,
        int before = DefaultBefore,
        int after = DefaultAfter,
        DatTableShape? tables = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(path);

        var lines = new List<string>();
        PathResolution resolved = path.Resolve(reader);

        foreach (PathHop hop in resolved.Hops)
        {
            lines.Add(hop.Read
                ? $"  {hop.Label,-34} = 0x{hop.Result:X}"
                : $"  {hop.Label,-34} = unreadable - the path stops here");
        }

        if (!resolved.Ok)
        {
            return lines;
        }

        lines.Add(string.Empty);
        lines.AddRange(Describe(reader, resolved.Target, resolved.ObjectBase, before, after, tables));
        return lines;
    }

    /// <summary>The same report for an address that is already known.</summary>
    public static IReadOnlyList<string> Describe(
        IMemoryReader reader,
        ulong target,
        ulong objectBase = 0,
        int before = DefaultBefore,
        int after = DefaultAfter,
        DatTableShape? tables = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var lines = new List<string>();
        (ulong start, int slots, int targetSlot) = Window(target, before, after);
        ulong?[] window = Sample(reader, start, slots);

        ulong aligned = target & ~7UL;
        ulong? slot = targetSlot >= 0 && targetSlot < window.Length ? window[targetSlot] : null;

        if ((target & 7) != 0)
        {
            lines.Add($"  MID-SLOT: 0x{target:X} is {target & 7} bytes into the eight-byte slot at 0x{aligned:X}.");
            if (slot is { } value && MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                // The whole reason this diagnostic exists - see the type's remarks.
                lines.Add($"            That slot holds the POINTER 0x{value:X}, so a four-byte watch here reads");
                lines.Add($"            0x{(uint)(value >> 32):X} ({value >> 32}), its top half. That number names the part of the");
                lines.Add("            heap the pointer landed in and is different every time the game starts;");
                lines.Add("            what carries meaning is the pointer, and whether it moved.");
            }
            else if (slot is { } raw)
            {
                lines.Add($"            That slot holds 0x{raw:X16}, which is not a pointer.");
            }
            else
            {
                lines.Add("            That slot could not be read.");
            }
        }
        else if (slot is { } value2 && MemoryReaderExtensions.IsPlausiblePointer(value2))
        {
            lines.Add($"  This slot holds a POINTER. A four-byte watch at 0x{target + 4:X} would read"
                + $" {value2 >> 32} (0x{(uint)(value2 >> 32):X}) - its top half, not a value of its own.");
        }

        if (objectBase != 0)
        {
            lines.Add(string.Empty);
            lines.Add($"  object 0x{objectBase:X}, target at +0x{aligned - objectBase:X}"
                + $"{VTableNote(reader, objectBase)}");
        }

        lines.Add(string.Empty);
        for (int i = 0; i < window.Length; i++)
        {
            ulong address = start + (ulong)(i * 8);
            string where = objectBase != 0 && address >= objectBase
                ? $"+0x{address - objectBase:X}"
                : $"0x{address:X}";
            string mark = i == targetSlot ? "->" : "  ";

            lines.Add(window[i] is { } raw
                ? $"  {mark} {where,-12} {raw:X16}  {Reading(reader, raw, tables)}"
                : $"  {mark} {where,-12} unreadable");
        }

        return lines;
    }

    /// <summary>
    /// Which slots moved between two samples, as lines to print.
    /// </summary>
    /// <remarks>
    /// The half of this that answers questions. A single reading of an unknown structure says
    /// almost nothing; two readings with something done in the game in between say WHICH slot
    /// is that thing. Hover an item, unhover it, and the slots that moved are the short list.
    /// </remarks>
    public static IReadOnlyList<string> Changes(
        IMemoryReader reader,
        IReadOnlyList<ulong?> before,
        IReadOnlyList<ulong?> after,
        ulong windowStart,
        ulong objectBase = 0,
        DatTableShape? tables = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var lines = new List<string>();
        for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
        {
            if (before[i] == after[i])
            {
                continue;
            }

            lines.Add(Line(reader, windowStart + (ulong)(i * 8), objectBase, before[i], after[i], tables));
        }

        return lines;
    }

    /// <summary>One slot's move, as a line: where it was, what it held, what it holds now.</summary>
    public static string Line(
        IMemoryReader reader,
        ulong address,
        ulong objectBase,
        ulong? before,
        ulong? after,
        DatTableShape? tables = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string where = objectBase != 0 && address >= objectBase
            ? $"+0x{address - objectBase:X}"
            : $"0x{address:X}";

        return $"  {where,-12} {Slot(before)} -> {Slot(after)}"
            + (after is { } now ? $"  {Reading(reader, now, tables)}" : string.Empty);
    }

    private static string Slot(ulong? value) => value is { } raw ? raw.ToString("X16", CultureInfo.InvariantCulture) : "unreadable";

    /// <summary>One slot that moved, and whether it is still worth printing live.</summary>
    public readonly record struct SlotChange(int Slot, ulong? Before, ulong? After, bool Print, bool LastPrint);

    /// <summary>
    /// Keeps score across a watch, so what a slot DOES is legible at the end of it.
    /// </summary>
    /// <remarks>
    /// WHAT THE FIRST REAL RUN OF THIS PRODUCED: ten seconds of watching printed six hundred
    /// lines, and the answer was in none of them. It was in their distribution - one slot took
    /// two values over and over and a handful of others once each, and the slot next to it
    /// never repeated a value at all, because it was a clock. Neither fact is visible one line
    /// at a time, and both are obvious in a tally.
    ///
    /// So a slot that keeps moving goes quiet after a few lines rather than drowning the ones
    /// that moved once, and everything is counted for the summary regardless.
    /// </remarks>
    public sealed class PeekWatchLog
    {
        /// <summary>Lines a single slot may print before it is left to the summary.</summary>
        private const int LoudChanges = 6;

        /// <summary>Distinct values remembered per slot. Beyond this the answer is "lots".</summary>
        private const int MostValuesKept = 64;

        private readonly Dictionary<int, Tally> _slots = [];

        private sealed class Tally
        {
            public int Changes { get; set; }

            public bool Overflowed { get; set; }

            /// <summary>Samples where the slot could not be read - a state of its own.</summary>
            public int Unreadable { get; set; }

            public Dictionary<ulong, int> Values { get; } = [];

            public int Distinct => Values.Count + (Unreadable > 0 ? 1 : 0);
        }

        /// <summary>How many samples have been taken.</summary>
        public int Samples { get; private set; }

        /// <summary>Records a sample against the one before it and reports what moved.</summary>
        public IReadOnlyList<SlotChange> Observe(IReadOnlyList<ulong?> before, IReadOnlyList<ulong?> after)
        {
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(after);

            Samples++;
            var changes = new List<SlotChange>();

            for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
            {
                if (!_slots.TryGetValue(i, out Tally? tally))
                {
                    _slots[i] = tally = new Tally();
                    Count(tally, before[i]);
                }

                Count(tally, after[i]);

                if (before[i] == after[i])
                {
                    continue;
                }

                tally.Changes++;
                changes.Add(new SlotChange(
                    i, before[i], after[i],
                    Print: tally.Changes <= LoudChanges,
                    LastPrint: tally.Changes == LoudChanges));
            }

            return changes;

            static void Count(Tally tally, ulong? value)
            {
                if (value is not { } raw)
                {
                    tally.Unreadable++;
                    return;
                }

                if (tally.Values.Count >= MostValuesKept && !tally.Values.ContainsKey(raw))
                {
                    tally.Overflowed = true;
                    return;
                }

                tally.Values[raw] = tally.Values.GetValueOrDefault(raw) + 1;
            }
        }

        /// <summary>What each slot did over the whole watch.</summary>
        public IReadOnlyList<string> Summary(ulong windowStart, ulong objectBase)
        {
            var lines = new List<string> { $"summary over {Samples} samples" };

            foreach ((int index, Tally tally) in _slots.OrderBy(pair => pair.Key))
            {
                if (tally.Changes == 0)
                {
                    continue;
                }

                ulong address = windowStart + (ulong)(index * 8);
                string where = objectBase != 0 && address >= objectBase
                    ? $"+0x{address - objectBase:X}"
                    : $"0x{address:X}";

                int distinct = tally.Distinct;

                // A slot that never shows the same value twice is not a state anybody can test
                // against - it is a counter, a clock or a handle, and saying so here is what
                // stops the next person building a feature on one.
                if (tally.Overflowed || distinct > tally.Changes)
                {
                    lines.Add($"  {where,-12} {tally.Changes} changes, never the same value twice - a counter or a clock");
                    continue;
                }

                lines.Add($"  {where,-12} {tally.Changes} changes, {distinct} distinct value{(distinct == 1 ? string.Empty : "s")}");

                var once = 0;
                foreach ((ulong value, int seen) in tally.Values.OrderByDescending(pair => pair.Value))
                {
                    if (seen == 1)
                    {
                        once++;
                        continue;
                    }

                    lines.Add($"                 {Slot(value)}  in {seen} samples");
                }

                if (tally.Unreadable > 0)
                {
                    lines.Add($"                 unreadable        in {tally.Unreadable} samples");
                }

                if (once > 0)
                {
                    lines.Add($"                 and {once} value{(once == 1 ? string.Empty : "s")} seen once only");
                }
            }

            return lines;
        }
    }

    /// <summary>The object's first slot, when it is a vtable - the one thing that names a type.</summary>
    /// <remarks>
    /// Reported as a module RVA rather than an address on purpose: the address is gone after
    /// a restart, while the RVA is the same for everyone on the same patch, which makes it
    /// the part worth writing down and comparing against a later run.
    /// </remarks>
    private static string VTableNote(IMemoryReader reader, ulong objectBase)
    {
        if (!reader.TryRead(objectBase, out ulong first)
            || reader.ModuleSize == 0
            || first < reader.ModuleBase
            || first >= reader.ModuleBase + reader.ModuleSize)
        {
            return string.Empty;
        }

        return $", vtable module+0x{first - reader.ModuleBase:X}";
    }

    private static string Reading(IMemoryReader reader, ulong raw, DatTableShape? tables)
    {
        if (raw == 0)
        {
            return "empty";
        }

        // BEFORE the pointer test, which would otherwise swallow it. "Art/Text" is
        // 0x747865542F747241, comfortably above any pointer bound, so eight characters of a
        // path read as a pointer into nowhere - and a path stored as characters in the object
        // rather than behind a pointer is how this game keeps its asset names.
        if (Inline(raw) is { Length: > 0 } inline)
        {
            return $"text \"{inline}\"";
        }

        // StructureProbe's bound rather than the reader's, and the difference is the point.
        // The reader will FOLLOW anything above 0x10000, which is right when a pointer is
        // expected; here nothing is expected, and this game's heap starts above four
        // gigabytes. A float of 7659.73 is 0x45EF5DD2 - comfortably "a pointer" by the loose
        // rule, and reported as one for a whole session, when it was the game's own clock.
        if (!StructureProbe.LooksLikePointer(raw))
        {
            int low = (int)(uint)raw;
            int high = (int)(uint)(raw >> 32);
            return $"i32 {low} / {high}{Floats(low, high)}";
        }

        PeekResult peek = PointerPeek.Peek(reader, raw, tables, following: 0);

        // A pointer into memory this reader cannot serve is the ordinary case in a REPLAY,
        // where only what the tool read is there - so it is described as a pointer whose
        // target is missing, rather than as the reading "unreadable: unreadable".
        if (peek.Kind == TargetKind.Unreadable)
        {
            return "pointer, target not readable here";
        }

        string reading = $"{peek.Kind.ToString().ToLowerInvariant()}: {peek.Summary}";

        // Text INSIDE the target, not at its front. PointerPeek only calls something text when
        // the characters start at offset zero, and this game's records routinely put a vtable
        // and a length in front of them - so the object naming the asset under the cursor read
        // as an anonymous structure while its own path sat eight bytes further in.
        return peek.Kind == TargetKind.Structure && Buried(reader, raw) is { Length: > 0 } buried
            ? $"{reading}   holds \"{buried}\""
            : reading;
    }

    /// <summary>The float readings of a slot, when they are the readings that make sense.</summary>
    private static string Floats(int low, int high)
    {
        float first = BitConverter.Int32BitsToSingle(low);
        float second = BitConverter.Int32BitsToSingle(high);
        bool firstOk = StructureProbe.SensibleFloat(first);
        bool secondOk = StructureProbe.SensibleFloat(second);

        return (firstOk, secondOk) switch
        {
            (true, true) => $", {Show(first)}f / {Show(second)}f",
            (true, false) => $", {Show(first)}f",
            _ => string.Empty,
        };

        static string Show(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Characters a slot must hold, of its eight bytes, before it is called text.</summary>
    /// <remarks>
    /// Six, with the rest NUL. Strict enough that no pointer this game uses can pass: a heap
    /// address carries 0xE7 0x03 0x00 0x00 in its top four bytes and a module address 0xF6
    /// 0x7F 0x00 0x00, and neither 0xE7 nor 0xF6 is a character.
    /// </remarks>
    private const int InlineTextChars = 6;

    /// <summary>The slot read as characters stored in place, or empty when it is not.</summary>
    private static string Inline(ulong raw)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, raw);

        var printable = 0;
        foreach (byte value in bytes)
        {
            if (value is >= 0x20 and < 0x7F)
            {
                printable++;
            }
            else if (value != 0)
            {
                return string.Empty;
            }
        }

        if (printable < InlineTextChars)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder(8);
        foreach (byte value in bytes)
        {
            if (value == 0)
            {
                break;
            }

            text.Append((char)value);
        }

        return text.ToString();
    }

    /// <summary>How far into an object to look for characters.</summary>
    private const int BuriedTextBytes = 64;

    /// <summary>Shortest run of characters worth calling text.</summary>
    /// <remarks>
    /// Strict on purpose: every structure gets offered to this, and a "string" label on four
    /// bytes of coincidence sends somebody looking for a name that was never there.
    /// </remarks>
    private const int ShortestBuriedText = 6;

    /// <summary>The longest run of readable characters inside an object, ASCII or UTF-16.</summary>
    private static string Buried(IMemoryReader reader, ulong address)
    {
        Span<byte> head = stackalloc byte[BuriedTextBytes];
        if (!reader.TryRead(address, head))
        {
            return string.Empty;
        }

        string best = Longest(head, stride: 1);
        string wide = Longest(head, stride: 2);
        return wide.Length > best.Length ? wide : best;

        // stride 1 reads the bytes as ASCII, stride 2 as UTF-16 - the game uses both, and
        // which one an object stores its name in is not knowable in advance.
        static string Longest(ReadOnlySpan<byte> window, int stride)
        {
            int bestStart = 0, bestLength = 0, start = -1, length = 0;
            for (int i = 0; i + stride <= window.Length; i += stride)
            {
                bool printable = window[i] is >= 0x20 and < 0x7F
                    && (stride == 1 || window[i + 1] == 0);

                if (printable)
                {
                    if (start < 0)
                    {
                        start = i;
                        length = 0;
                    }

                    length++;
                    if (length > bestLength)
                    {
                        (bestStart, bestLength) = (start, length);
                    }
                }
                else
                {
                    start = -1;
                }
            }

            if (bestLength < ShortestBuriedText)
            {
                return string.Empty;
            }

            var text = new System.Text.StringBuilder(bestLength);
            for (int i = 0; i < bestLength; i++)
            {
                text.Append((char)window[bestStart + (i * stride)]);
            }

            return text.ToString();
        }
    }
}
