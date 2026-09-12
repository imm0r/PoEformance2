using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Core.Schema;

/// <summary>
/// Loads an <see cref="OffsetSchema"/> from its JSON file.
/// </summary>
/// <remarks>
/// Parsing is strict on purpose: an unknown type name, a malformed offset or a bad
/// invariant throws with the struct and field named in the message. Offsets are the
/// most safety-critical data in the tool - a silently-skipped typo would surface as
/// unexplainable garbage reads much later.
///
/// Uses source-generated System.Text.Json (see <see cref="SchemaJsonContext"/>) so the
/// loader works under Native AOT, where reflection-based serialisation does not.
/// </remarks>
public static class SchemaJson
{
    public static OffsetSchema Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    public static OffsetSchema Load(Stream json)
    {
        // Read once into memory: the duplicate-name check needs a second pass, and the
        // schema is a few tens of kilobytes.
        using var buffer = new MemoryStream();
        json.CopyTo(buffer);

        RejectDuplicateNames(buffer.ToArray());

        buffer.Position = 0;
        SchemaDto dto = JsonSerializer.Deserialize(buffer, SchemaJsonContext.Default.SchemaDto)
                        ?? throw new InvalidDataException("Schema file is empty.");

        var statics = new Dictionary<string, StaticAnchor>();
        foreach ((string name, StaticDto s) in dto.Statics ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.Pattern))
            {
                throw new InvalidDataException($"Static '{name}' has no pattern.");
            }

            if (!s.Pattern.Contains('^'))
            {
                throw new InvalidDataException($"Static '{name}' pattern has no '^' RIP marker.");
            }

            var fallbacks = new List<string>(s.Fallbacks?.Length ?? 0);
            foreach (string fallback in s.Fallbacks ?? [])
            {
                if (string.IsNullOrWhiteSpace(fallback) || !fallback.Contains('^'))
                {
                    throw new InvalidDataException($"Static '{name}' has a fallback pattern without a '^' RIP marker: \"{fallback}\".");
                }

                fallbacks.Add(fallback);
            }

            statics[name] = new StaticAnchor(name, s.Pattern, s.Comment, fallbacks);
        }

        var structs = new Dictionary<string, StructDef>();
        foreach ((string structName, StructDto s) in dto.Structs ?? [])
        {
            var fields = new List<FieldDef>();
            foreach ((string fieldName, FieldDto f) in s.Fields ?? [])
            {
                fields.Add(ParseField(structName, fieldName, f));
            }

            fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            var constants = new Dictionary<string, long>();
            foreach ((string constName, string raw) in s.Consts ?? [])
            {
                constants[constName] = ParseNumber(raw, structName, constName);
            }

            structs[structName] = new StructDef(structName, s.Comment, fields, constants);
        }

        return new OffsetSchema(dto.Version, dto.GameVersion ?? "unknown", statics, structs);
    }

    /// <summary>
    /// Fails on a struct or static defined twice.
    /// </summary>
    /// <remarks>
    /// JSON deserialisation into a dictionary takes the LAST of two identical keys and says
    /// nothing, so a second definition silently replaces the first - and since both usually
    /// look plausible, the loss only surfaces much later as a missing field or a wrong
    /// offset. That happened: a second "BuffDefinition" block dropped the constants the
    /// first one declared, and only a unit test three layers away noticed.
    ///
    /// JsonDocument preserves duplicate property names when enumerating, which is what
    /// makes detecting this possible at all.
    /// </remarks>
    private static void RejectDuplicateNames(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        foreach (string section in (string[])["structs", "statics"])
        {
            if (!document.RootElement.TryGetProperty(section, out JsonElement element)
                || element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Schema defines '{property.Name}' twice in \"{section}\". The second "
                        + "definition would silently replace the first, including any fields or "
                        + "constants it declares - merge them into one.");
                }
            }
        }
    }

    private static FieldDef ParseField(string structName, string fieldName, FieldDto f)
    {
        string where = $"{structName}.{fieldName}";

        if (string.IsNullOrWhiteSpace(f.Offset))
        {
            throw new InvalidDataException($"Field {where} has no offset.");
        }

        int offset = checked((int)ParseNumber(f.Offset, structName, fieldName));

        FieldType type = f.Type?.ToLowerInvariant() switch
        {
            "u8" => FieldType.U8,
            "u16" => FieldType.U16,
            "u32" => FieldType.U32,
            "u64" => FieldType.U64,
            "i8" => FieldType.I8,
            "i16" => FieldType.I16,
            "i32" => FieldType.I32,
            "i64" => FieldType.I64,
            "f32" => FieldType.F32,
            "f64" => FieldType.F64,
            "ptr" => FieldType.Ptr,
            "mat4x4" => FieldType.Mat4x4,
            "stdvector" => FieldType.StdVector,
            "stdwstring" => FieldType.StdWString,
            "utf16ptr" => FieldType.Utf16Ptr,
            null or "" => throw new InvalidDataException($"Field {where} has no type."),
            _ => throw new InvalidDataException($"Field {where} has unknown type '{f.Type}'."),
        };

        Invariant? invariant = f.Invariant is null ? null : ParseInvariant(where, f.Invariant);

        Dictionary<long, string>? values = null;
        if (f.Values is { Count: > 0 })
        {
            values = new Dictionary<long, string>(f.Values.Count);
            foreach ((string raw, string label) in f.Values)
            {
                values[ParseNumber(raw, structName, fieldName)] = label;
            }
        }

        return new FieldDef(fieldName, offset, type, f.Comment, invariant, f.Struct, f.Inline ?? false, values);
    }

    private static Invariant ParseInvariant(string where, InvariantDto dto) => dto.Kind?.ToLowerInvariant() switch
    {
        "range" => new Invariant.Range(
            dto.Min ?? throw new InvalidDataException($"{where}: range invariant needs 'min'."),
            dto.Max ?? throw new InvalidDataException($"{where}: range invariant needs 'max'.")),
        "plausibleptr" => new Invariant.PlausiblePtr(),
        "nonnullptr" => new Invariant.NonNullPtr(),
        "unitvector3" => new Invariant.UnitVector3(
            dto.At is null ? 0 : checked((int)ParseNumber(dto.At, where, "at")),
            dto.Expect is null ? null
                : dto.Expect.Length == 3 ? dto.Expect
                : throw new InvalidDataException($"{where}: unitVector3 'expect' needs exactly 3 numbers.")),
        "vectorsane" => new Invariant.VectorSane(
            checked((int)(dto.ElementSize ?? throw new InvalidDataException($"{where}: vectorSane needs 'elementSize'."))),
            (long)(dto.MaxCount ?? throw new InvalidDataException($"{where}: vectorSane needs 'maxCount'."))),
        "stringcontains" => new Invariant.StringContains(
            dto.Needle ?? throw new InvalidDataException($"{where}: stringContains needs 'needle'."),
            ParseHops(where, dto.Hops),
            dto.StringAt is null ? 0 : checked((int)ParseNumber(dto.StringAt, where, "stringAt"))),
        "changes" => new Invariant.ChangesOverTime(),
        _ => throw new InvalidDataException($"{where}: unknown invariant kind '{dto.Kind}'."),
    };

    private static int[] ParseHops(string where, string[]? hops)
    {
        if (hops is null || hops.Length == 0)
        {
            return [];
        }

        var result = new int[hops.Length];
        for (int i = 0; i < hops.Length; i++)
        {
            result[i] = checked((int)ParseNumber(hops[i], where, $"hops[{i}]"));
        }

        return result;
    }

    /// <summary>Parses "0x1A0" or plain decimal. Throws naming struct and field on garbage.</summary>
    private static long ParseNumber(string raw, string structName, string fieldName)
    {
        string s = raw.Trim();
        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? s[2..] : s;

        if (long.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        throw new InvalidDataException($"Bad number '{raw}' at {structName}.{fieldName}.");
    }
}

// ── JSON wire shapes ─────────────────────────────────────────────────────────
// Plain DTOs the source generator understands. Offsets and constants are strings
// so the file can use hex ("0x1A0"), which is how everyone thinks about offsets.

public sealed class SchemaDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("gameVersion")]
    public string? GameVersion { get; set; }

    [JsonPropertyName("statics")]
    public Dictionary<string, StaticDto>? Statics { get; set; }

    [JsonPropertyName("structs")]
    public Dictionary<string, StructDto>? Structs { get; set; }
}

public sealed class StaticDto
{
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("fallbacks")]
    public string[]? Fallbacks { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed class StructDto
{
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldDto>? Fields { get; set; }

    [JsonPropertyName("consts")]
    public Dictionary<string, string>? Consts { get; set; }
}

public sealed class FieldDto
{
    [JsonPropertyName("offset")]
    public string? Offset { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("invariant")]
    public InvariantDto? Invariant { get; set; }

    /// <summary>Another struct this field leads to, by name.</summary>
    [JsonPropertyName("struct")]
    public string? Struct { get; set; }

    /// <summary>True when that struct sits AT this offset instead of behind a pointer.</summary>
    [JsonPropertyName("inline")]
    public bool? Inline { get; set; }

    /// <summary>What the field's numbers mean, keyed by the number as text ("0", "0x2").</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string>? Values { get; set; }
}

public sealed class InvariantDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }

    [JsonPropertyName("at")]
    public string? At { get; set; }

    [JsonPropertyName("elementSize")]
    public double? ElementSize { get; set; }

    [JsonPropertyName("maxCount")]
    public double? MaxCount { get; set; }

    [JsonPropertyName("needle")]
    public string? Needle { get; set; }

    [JsonPropertyName("hops")]
    public string[]? Hops { get; set; }

    [JsonPropertyName("stringAt")]
    public string? StringAt { get; set; }

    [JsonPropertyName("expect")]
    public double[]? Expect { get; set; }
}

/// <summary>Source-generated serializer context - required for Native AOT.</summary>
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(SchemaDto))]
public sealed partial class SchemaJsonContext : JsonSerializerContext;
