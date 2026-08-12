using System.Globalization;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Schema;

/// <summary>One declared field, read off live memory and turned into something readable.</summary>
/// <param name="Text">
/// The value as text, already formatted for its type. "unreadable" when the read failed,
/// which is a fact about the offset rather than an error - a struct whose fields all read
/// that way is one whose base address is wrong.
/// </param>
/// <param name="Children">
/// The fields of the struct this one leads to, where the schema names one. Null when it
/// names none, or when the pointer led nowhere.
/// </param>
public readonly record struct FieldReading(
    string Name, int Offset, FieldType Type, string Text, string Comment,
    IReadOnlyList<FieldReading>? Children = null);

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
    /// <param name="schema">
    /// Supplied to follow the fields that name another struct - Life.Health into a Vital, and
    /// so on. Left out for the nested read itself, which is what stops this at one level: a
    /// struct that names itself, directly or round a ring, would otherwise never finish.
    /// </param>
    public static IReadOnlyList<FieldReading> Read(
        IMemoryReader reader, StructDef layout, ulong baseAddress, OffsetSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layout);

        var readings = new List<FieldReading>(layout.Fields.Count);
        foreach (FieldDef field in layout.Fields)
        {
            ulong at = baseAddress + (ulong)field.Offset;
            IReadOnlyList<FieldReading>? children = ChildrenOf(reader, field, at, schema);
            string text = Format(reader, field, at);

            if (children is not null)
            {
                // An inline struct's own eight bytes are not a value at all - they are the
                // start of the thing below - so showing them as one is worse than showing
                // nothing. Behind a pointer the address still means something, and it is
                // what somebody copies into the dissector.
                text = field.Inline ? field.Target! : $"{text} -> {field.Target}";
            }

            readings.Add(new FieldReading(
                field.Name, field.Offset, field.Type, text, field.Comment ?? string.Empty, children));
        }

        return readings;
    }

    /// <summary>Reads the struct a field leads to, when the schema names one.</summary>
    private static IReadOnlyList<FieldReading>? ChildrenOf(
        IMemoryReader reader, FieldDef field, ulong at, OffsetSchema? schema)
    {
        if (schema is null
            || field.Target is null
            || !schema.Structs.TryGetValue(field.Target, out StructDef? target)
            || target.Fields.Count == 0)
        {
            return null;
        }

        ulong childBase = at;
        if (!field.Inline)
        {
            if (!reader.TryRead(at, out ulong pointer) || pointer == 0)
            {
                return null;
            }

            childBase = pointer;
        }

        return Read(reader, target, childBase);
    }

    private static string Format(IMemoryReader reader, FieldDef field, ulong at)
    {
        switch (field.Type)
        {
            case FieldType.U8:
                return reader.TryRead(at, out byte u8) ? Whole(field,u8) : Unreadable;
            case FieldType.U16:
                return reader.TryRead(at, out ushort u16) ? Whole(field,u16) : Unreadable;
            case FieldType.U32:
                return reader.TryRead(at, out uint u32) ? Whole(field,u32) : Unreadable;
            case FieldType.U64:
                return reader.TryRead(at, out ulong u64) ? Whole(field,(long)u64) : Unreadable;
            case FieldType.I8:
                return reader.TryRead(at, out sbyte i8) ? Whole(field,i8) : Unreadable;
            case FieldType.I16:
                return reader.TryRead(at, out short i16) ? Whole(field,i16) : Unreadable;
            case FieldType.I32:
                return reader.TryRead(at, out int i32) ? Whole(field,i32) : Unreadable;
            case FieldType.I64:
                return reader.TryRead(at, out long i64) ? Whole(field,i64) : Unreadable;
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

                    // Only when BOTH ends are real. One null end is not a vector of two
                    // hundred and eighty gigabytes, it is a vector that is not there - and
                    // subtracting the two produces exactly that number, which reads like a
                    // measurement instead of like nonsense. Seen on Actor.DeployedEntities.
                    if (first == 0 || last == 0)
                    {
                        return $"0x{first:X} .. 0x{last:X}  (one end missing)";
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

    /// <summary>A whole number, as whatever the schema says it means.</summary>
    /// <remarks>
    /// Two ways of saying more than the digits do. A declared value map wins - "Rare" beats
    /// 2, and an unlisted number keeps its digits rather than being hidden behind a guess.
    ///
    /// Failing that, a field declaring a range of 0..1 has already said it is a yes or a no;
    /// that is what this schema's flags look like, and reading "yes" is faster than deciding
    /// what 1 meant on this particular field. The number stays for anything wider.
    /// </remarks>
    private static string Whole(FieldDef field, long value)
    {
        if (field.Values is not null && field.Values.TryGetValue(value, out string? label))
        {
            return $"{label} ({value})";
        }

        if (field.Invariant is Invariant.Range { Min: 0, Max: 1 })
        {
            return value == 0 ? "no" : "yes";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Real(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quoted(string text)
        => text.Length <= MostChars ? $"\"{text}\"" : $"\"{text[..(MostChars - 3)]}...\"";
}
