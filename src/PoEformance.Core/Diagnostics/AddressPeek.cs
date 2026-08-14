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

            ulong address = windowStart + (ulong)(i * 8);
            string where = objectBase != 0 && address >= objectBase
                ? $"+0x{address - objectBase:X}"
                : $"0x{address:X}";

            lines.Add($"  {where,-12} {Slot(before[i])} -> {Slot(after[i])}"
                + (after[i] is { } now ? $"  {Reading(reader, now, tables)}" : string.Empty));
        }

        return lines;
    }

    private static string Slot(ulong? value) => value is { } raw ? raw.ToString("X16", CultureInfo.InvariantCulture) : "unreadable";

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

        if (!MemoryReaderExtensions.IsPlausiblePointer(raw))
        {
            // Not a pointer, so show the readings a person actually checks: the two halves as
            // integers, and the low half as a float, which is how the game stores most of them.
            int low = (int)(uint)raw;
            int high = (int)(uint)(raw >> 32);
            float asFloat = BitConverter.Int32BitsToSingle(low);
            string floatPart = StructureProbe.SensibleFloat(asFloat)
                ? $", {asFloat.ToString("0.###", CultureInfo.InvariantCulture)}f"
                : string.Empty;
            return $"i32 {low} / {high}{floatPart}";
        }

        PeekResult peek = PointerPeek.Peek(reader, raw, tables, following: 0);

        // A pointer into memory this reader cannot serve is the ordinary case in a REPLAY,
        // where only what the tool read is there - so it is described as a pointer whose
        // target is missing, rather than as the reading "unreadable: unreadable".
        return peek.Kind == TargetKind.Unreadable
            ? "pointer, target not readable here"
            : $"{peek.Kind.ToString().ToLowerInvariant()}: {peek.Summary}";
    }
}
