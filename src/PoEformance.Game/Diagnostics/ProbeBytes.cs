using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// The two readings every layout probe needs: a slot as the string it might point at, and a
/// span of an object as the slots it might be.
/// </summary>
/// <remarks>
/// Shared because both atlas probes want exactly this and a second copy would drift from the
/// first - and because the BLOCK is the part that has to be got right in one place. It reads
/// the whole span in one call rather than a slot at a time, which is cheaper, and it puts the
/// span into a recording CONTIGUOUSLY, so a field nobody has noticed yet can be found in it
/// afterwards without the build that would otherwise be needed to read it.
/// </remarks>
internal static class ProbeBytes
{
    /// <summary>Longest string taken seriously behind one of these pointers.</summary>
    public const int MostChars = 96;

    /// <summary>A pointer slot read as the wide string it might be pointing at.</summary>
    public static string Text(IMemoryReader reader, ulong slot)
    {
        ulong at = reader.ReadPointer(slot);
        if (!MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return $"0x{at:X} (not a pointer)";
        }

        string text = reader.ReadUnicodeString(at, MostChars);
        return text.Length > 0 ? $"0x{at:X} \"{text}\"" : $"0x{at:X} (no text there)";
    }

    /// <summary>
    /// A span of an object, one line per eight-byte slot, and whatever each slot points at.
    /// </summary>
    public static List<string> Block(
        IMemoryReader reader,
        DatTableShape? tables,
        string label,
        ulong at,
        int from,
        int to,
        string indent = "    ")
    {
        var said = new List<string>();

        int start = Math.Max(from, 0) & ~7;
        int length = ((to - start + 7) & ~7) + 8;
        if (length <= 0)
        {
            return said;
        }

        var block = new byte[length];
        if (!reader.TryRead(at + (ulong)start, block))
        {
            said.Add($"{indent}{label} +0x{start:X3}..+0x{start + length:X3} unreadable");
            return said;
        }

        said.Add($"{indent}{label} +0x{start:X3}..+0x{start + length:X3}");
        for (int offset = 0; offset + 8 <= length; offset += 8)
        {
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset));
            string note = string.Empty;
            if (MemoryReaderExtensions.IsPlausiblePointer(raw))
            {
                // A dat foreign reference is a row followed by its table, so the next slot is
                // what lets PointerPeek name the table instead of showing bytes.
                ulong following = offset + 16 <= length
                    ? BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset + 8))
                    : 0;
                note = PointerPeek.Peek(reader, raw, tables, following).Summary;
            }

            said.Add($"{indent}  +0x{start + offset:X3}  {raw:X16}  {note}");
        }

        return said;
    }
}
