using PoEformance.Core.Memory;

namespace PoEformance.Core.Schema;

/// <summary>Outcome of checking one field's invariant against live memory.</summary>
public enum CheckOutcome
{
    /// <summary>Invariant holds.</summary>
    Pass,

    /// <summary>Invariant violated - the offset has probably drifted.</summary>
    Fail,

    /// <summary>Field has no invariant, or the check needs data we don't have (e.g. a second sample).</summary>
    Skipped,
}

/// <summary>One row of a validation report.</summary>
public sealed record FieldCheck(
    string StructName,
    string FieldName,
    int Offset,
    CheckOutcome Outcome,
    string Detail);

/// <summary>
/// Evaluates schema invariants against a reader.
/// </summary>
/// <remarks>
/// This is the drift alarm. Run it at attach time against every struct whose base
/// address is known and the report tells you - before any feature misbehaves - which
/// offsets no longer point where they should. The reverse-engineering workbench shows
/// the same report as a green/red list per struct.
///
/// The validator never throws on bad memory: an unreadable field is a
/// <see cref="CheckOutcome.Fail"/> with "unreadable" as the detail, because "we cannot
/// even read it" is exactly as alarming as "it holds a wrong value".
/// </remarks>
public sealed class SchemaValidator
{
    private readonly IMemoryReader _reader;

    public SchemaValidator(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Checks every field of <paramref name="structDef"/> at <paramref name="baseAddress"/>.
    /// <paramref name="previousSample"/> optionally carries values from an earlier call
    /// so <see cref="Invariant.ChangesOverTime"/> can be evaluated; pass null on the
    /// first run.
    /// </summary>
    public List<FieldCheck> ValidateStruct(
        StructDef structDef,
        ulong baseAddress,
        IReadOnlyDictionary<string, ulong>? previousSample = null,
        Dictionary<string, ulong>? sampleOut = null)
    {
        ArgumentNullException.ThrowIfNull(structDef);
        var results = new List<FieldCheck>(structDef.Fields.Count);

        foreach (FieldDef field in structDef.Fields)
        {
            results.Add(CheckField(structDef, field, baseAddress, previousSample, sampleOut));
        }

        return results;
    }

    private FieldCheck CheckField(
        StructDef structDef,
        FieldDef field,
        ulong baseAddress,
        IReadOnlyDictionary<string, ulong>? previousSample,
        Dictionary<string, ulong>? sampleOut)
    {
        ulong fieldAddress = baseAddress + (ulong)field.Offset;

        FieldCheck Result(CheckOutcome outcome, string detail)
            => new(structDef.Name, field.Name, field.Offset, outcome, detail);

        // Record raw bits for change tracking regardless of invariant. Reads exactly
        // the field's size (capped at 8 bytes) - a blind 8-byte read would fail for a
        // 4-byte field at the end of a region and would compare neighbour bytes.
        if (sampleOut is not null && TryReadRawBits(field, fieldAddress, out ulong rawBits))
        {
            sampleOut[field.Name] = rawBits;
        }

        switch (field.Invariant)
        {
            case null:
                return Result(CheckOutcome.Skipped, "no invariant");

            case Invariant.Range range:
            {
                if (!TryReadNumeric(field, fieldAddress, out double value))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                return value >= range.Min && value <= range.Max
                    ? Result(CheckOutcome.Pass, $"{value} in {range.Min}..{range.Max}")
                    : Result(CheckOutcome.Fail, $"{value} outside {range.Min}..{range.Max}");
            }

            case Invariant.PlausiblePtr:
            {
                if (!_reader.TryRead(fieldAddress, out ulong pointer))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                return pointer == 0 || MemoryReaderExtensions.IsPlausiblePointer(pointer)
                    ? Result(CheckOutcome.Pass, pointer == 0 ? "null" : $"0x{pointer:X}")
                    : Result(CheckOutcome.Fail, $"implausible pointer 0x{pointer:X}");
            }

            case Invariant.NonNullPtr:
            {
                if (!_reader.TryRead(fieldAddress, out ulong pointer))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                return MemoryReaderExtensions.IsPlausiblePointer(pointer)
                    ? Result(CheckOutcome.Pass, $"0x{pointer:X}")
                    : Result(CheckOutcome.Fail, pointer == 0 ? "null" : $"implausible pointer 0x{pointer:X}");
            }

            case Invariant.UnitVector3 unit:
            {
                Span<float> v = stackalloc float[3];
                Span<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(v);
                if (!_reader.TryRead(fieldAddress + (ulong)unit.At, bytes))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                double length = Math.Sqrt((double)v[0] * v[0] + (double)v[1] * v[1] + (double)v[2] * v[2]);
                return Math.Abs(length - 1.0) <= 0.05
                    ? Result(CheckOutcome.Pass, $"length {length:F3}")
                    : Result(CheckOutcome.Fail, $"length {length:G4}, expected ~1.0");
            }

            case Invariant.VectorSane sane:
            {
                if (!_reader.TryRead(fieldAddress, out ulong first) ||
                    !_reader.TryRead(fieldAddress + 8, out ulong last))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                if (first == 0 && last == 0)
                {
                    return Result(CheckOutcome.Pass, "empty vector");
                }

                if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first)
                {
                    return Result(CheckOutcome.Fail, $"bad pointers first=0x{first:X} last=0x{last:X}");
                }

                long count = (long)(last - first) / sane.ElementSize;
                return count >= 0 && count <= sane.MaxCount
                    ? Result(CheckOutcome.Pass, $"{count} elements")
                    : Result(CheckOutcome.Fail, $"{count} elements outside 0..{sane.MaxCount}");
            }

            case Invariant.StringContains contains:
            {
                ulong address = fieldAddress;
                foreach (int hop in contains.Hops)
                {
                    address = _reader.ReadPointer(address + (ulong)hop);
                    if (address == 0)
                    {
                        return Result(CheckOutcome.Fail, "pointer chain broke");
                    }
                }

                string text = _reader.ReadStdWString(address + (ulong)contains.StringAt, 256);
                if (text.Length == 0)
                {
                    return Result(CheckOutcome.Fail, "empty or unreadable string");
                }

                return text.Contains(contains.Needle, StringComparison.Ordinal)
                    ? Result(CheckOutcome.Pass, Truncate(text))
                    : Result(CheckOutcome.Fail, $"\"{Truncate(text)}\" lacks \"{contains.Needle}\"");
            }

            case Invariant.ChangesOverTime:
            {
                if (!TryReadRawBits(field, fieldAddress, out ulong current))
                {
                    return Result(CheckOutcome.Fail, "unreadable");
                }

                if (previousSample is null || !previousSample.TryGetValue(field.Name, out ulong previous))
                {
                    return Result(CheckOutcome.Skipped, "needs a second sample");
                }

                return current != previous
                    ? Result(CheckOutcome.Pass, "value changed between samples")
                    : Result(CheckOutcome.Fail, $"frozen at 0x{current:X} across samples");
            }

            default:
                return Result(CheckOutcome.Skipped, "unhandled invariant");
        }
    }

    /// <summary>
    /// Reads the field's raw bits for change tracking: exactly SizeOf(type) bytes,
    /// capped at 8 (larger types compare by their first 8 bytes, which is enough to
    /// detect "this moved / this froze").
    /// </summary>
    private bool TryReadRawBits(FieldDef field, ulong address, out ulong bits)
    {
        bits = 0;
        int size = Math.Min(FieldTypes.SizeOf(field.Type), 8);
        Span<byte> buffer = stackalloc byte[8];
        if (!_reader.TryRead(address, buffer[..size]))
        {
            return false;
        }

        bits = System.Runtime.InteropServices.MemoryMarshal.Read<ulong>(buffer);
        return true;
    }

    /// <summary>Reads a field as a double for range checks, honouring its declared type.</summary>
    private bool TryReadNumeric(FieldDef field, ulong address, out double value)
    {
        value = 0;
        switch (field.Type)
        {
            case FieldType.U8:
                if (!_reader.TryRead(address, out byte u8)) { return false; }
                value = u8; return true;
            case FieldType.U16:
                if (!_reader.TryRead(address, out ushort u16)) { return false; }
                value = u16; return true;
            case FieldType.U32:
                if (!_reader.TryRead(address, out uint u32)) { return false; }
                value = u32; return true;
            case FieldType.U64 or FieldType.Ptr or FieldType.Utf16Ptr:
                if (!_reader.TryRead(address, out ulong u64)) { return false; }
                value = u64; return true;
            case FieldType.I8:
                if (!_reader.TryRead(address, out sbyte i8)) { return false; }
                value = i8; return true;
            case FieldType.I16:
                if (!_reader.TryRead(address, out short i16)) { return false; }
                value = i16; return true;
            case FieldType.I32:
                if (!_reader.TryRead(address, out int i32)) { return false; }
                value = i32; return true;
            case FieldType.I64:
                if (!_reader.TryRead(address, out long i64)) { return false; }
                value = i64; return true;
            case FieldType.F32:
                if (!_reader.TryRead(address, out float f32)) { return false; }
                value = f32; return true;
            case FieldType.F64:
                return _reader.TryRead(address, out value);
            default:
                return false;
        }
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
