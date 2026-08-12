using System.Globalization;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Schema;

/// <summary>One declared field, read off live memory and turned into something readable.</summary>
/// <param name="Text">
/// The value as text, already formatted for its type. "unreadable" when the read failed,
/// which is a fact about the offset rather than an error - a struct whose fields all read
/// that way is one whose base address is wrong.
/// </param>
public readonly record struct FieldReading(
    string Name, int Offset, FieldType Type, string Text, string Comment);

/// <summary>
/// Reads every field a schema struct declares, at an address.
/// </summary>
/// <remarks>
/// The other half of "offsets are data". The schema already drives the validator and labels
/// the dissector's rows; this makes it READ, so a component with a decoder can be looked at
/// as what it is - Health 1,532 of 2,410 - rather than as sixteen bytes that need a person to
/// remember which of them is which.
///
/// Which is also why it belongs here and not in the window that wanted it: nothing about this
/// is about the entity browser. Any address with a layout can be shown this way.
/// </remarks>
public static class SchemaFieldReader
{
    private const string Unreadable = "unreadable";

    /// <summary>How much of a string to show before it stops being worth reading.</summary>
    private const int MostChars = 96;

    /// <summary>Reads each of <paramref name="layout"/>'s fields at <paramref name="baseAddress"/>.</summary>
    /// <remarks>
    /// One read per field rather than one span for the struct, because a schema struct is not
    /// a contiguous thing: its declared fields are the handful somebody has identified, and
    /// they are commonly hundreds of bytes apart with nothing known in between. Reading the
    /// span would mean reading all of that to use a fraction of it.
    /// </remarks>
    public static IReadOnlyList<FieldReading> Read(IMemoryReader reader, StructDef layout, ulong baseAddress)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layout);

        var readings = new List<FieldReading>(layout.Fields.Count);
        foreach (FieldDef field in layout.Fields)
        {
            readings.Add(new FieldReading(
                field.Name,
                field.Offset,
                field.Type,
                Format(reader, field, baseAddress + (ulong)field.Offset),
                field.Comment ?? string.Empty));
        }

        return readings;
    }

    private static string Format(IMemoryReader reader, FieldDef field, ulong at)
    {
        switch (field.Type)
        {
            case FieldType.U8:
                return reader.TryRead(at, out byte u8) ? Whole(u8) : Unreadable;
            case FieldType.U16:
                return reader.TryRead(at, out ushort u16) ? Whole(u16) : Unreadable;
            case FieldType.U32:
                return reader.TryRead(at, out uint u32) ? Whole(u32) : Unreadable;
            case FieldType.U64:
                return reader.TryRead(at, out ulong u64) ? Whole((long)u64) : Unreadable;
            case FieldType.I8:
                return reader.TryRead(at, out sbyte i8) ? Whole(i8) : Unreadable;
            case FieldType.I16:
                return reader.TryRead(at, out short i16) ? Whole(i16) : Unreadable;
            case FieldType.I32:
                return reader.TryRead(at, out int i32) ? Whole(i32) : Unreadable;
            case FieldType.I64:
                return reader.TryRead(at, out long i64) ? Whole(i64) : Unreadable;
            case FieldType.F32:
                return reader.TryRead(at, out float f32) ? Real(f32) : Unreadable;
            case FieldType.F64:
                return reader.TryRead(at, out double f64) ? Real(f64) : Unreadable;

            case FieldType.Ptr:
                return reader.TryRead(at, out ulong pointer)
                    ? pointer == 0 ? "null" : $"0x{pointer:X}"
                    : Unreadable;

            // Both string shapes end as text or say why not. A pointer field that leads
            // nowhere readable still shows its address, because "0x0" and "points at bytes
            // that are not a string" are different answers and the second is a lead.
            case FieldType.Utf16Ptr:
                {
                    if (!reader.TryRead(at, out ulong text))
                    {
                        return Unreadable;
                    }

                    string wide = text != 0 ? reader.ReadUnicodeString(text, MostChars) : string.Empty;
                    return wide.Length > 0 ? Quoted(wide) : text == 0 ? "null" : $"0x{text:X}";
                }

            case FieldType.StdWString:
                {
                    string text = reader.ReadStdWString(at, MostChars);
                    return text.Length > 0 ? Quoted(text) : "empty";
                }

            // Begin and end, and what lies between them - which is the only part anybody
            // reads a vector header for. The element size is not in the schema, so the span
            // stays in bytes rather than being divided by a guess.
            case FieldType.StdVector:
                {
                    if (!reader.TryRead(at, out ulong first) || !reader.TryRead(at + 8, out ulong last))
                    {
                        return Unreadable;
                    }

                    if (first == 0 && last == 0)
                    {
                        return "empty";
                    }

                    return last >= first
                        ? $"0x{first:X} .. 0x{last:X}  ({last - first} bytes)"
                        : $"0x{first:X} .. 0x{last:X}  (end before start)";
                }

            case FieldType.Mat4x4:
                return "4x4 matrix";

            default:
                return Unreadable;
        }
    }

    private static string Whole(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Real(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quoted(string text)
        => text.Length <= MostChars ? $"\"{text}\"" : $"\"{text[..(MostChars - 3)]}...\"";
}
