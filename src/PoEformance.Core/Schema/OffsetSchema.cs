namespace PoEformance.Core.Schema;

/// <summary>
/// The complete offset knowledge for one game version: static anchors (byte patterns)
/// and struct layouts (fields with offsets, types and invariants).
/// </summary>
/// <remarks>
/// This is THE central idea of the project. Offsets are data, not code:
///
///   - they load from <c>schema/poe2.offsets.json</c> and can be hot-reloaded while the
///     tool runs, so a reverse-engineering iteration is "edit json, press reload" - no
///     rebuild, no restart, no re-attach;
///   - every field can declare an <see cref="Invariant"/> describing what a correct
///     value looks like, so a game patch that shifts a struct is detected at attach
///     time by the validator instead of weeks later by a subtly broken feature;
///   - the struct viewer annotates raw memory from the same definitions, so the schema
///     is simultaneously the reader's input and the RE workbench's display model.
///
/// The type is immutable after loading. Consumers hold a reference and never see a
/// half-reloaded state - a reload swaps the whole schema atomically.
/// </remarks>
public sealed class OffsetSchema
{
    public OffsetSchema(
        int version,
        string gameVersion,
        IReadOnlyDictionary<string, StaticAnchor> statics,
        IReadOnlyDictionary<string, StructDef> structs)
    {
        Version = version;
        GameVersion = gameVersion;
        Statics = statics;
        Structs = structs;
    }

    /// <summary>Schema format version (not the game version).</summary>
    public int Version { get; }

    /// <summary>Game version these offsets were last verified against, e.g. "0.5.1".</summary>
    public string GameVersion { get; }

    /// <summary>Pattern-scanned static anchors, keyed by name.</summary>
    public IReadOnlyDictionary<string, StaticAnchor> Statics { get; }

    /// <summary>Struct layouts, keyed by struct name.</summary>
    public IReadOnlyDictionary<string, StructDef> Structs { get; }
}

/// <summary>
/// A pattern-scanned static address. The pattern uses the established convention:
/// hex bytes, <c>??</c> wildcards, and a single <c>^</c> marking where the RIP-relative
/// 32-bit displacement starts. The resolved address is
/// <c>match + caretOffset + 4 + disp32</c>.
/// </summary>
public sealed class StaticAnchor
{
    public StaticAnchor(string name, string pattern, string? comment, IReadOnlyList<string>? fallbacks = null)
    {
        Name = name;
        Pattern = pattern;
        Comment = comment;
        Fallbacks = fallbacks ?? [];
    }

    public string Name { get; }

    /// <summary>Pattern text including the <c>^</c> marker.</summary>
    public string Pattern { get; }

    public string? Comment { get; }

    /// <summary>
    /// Looser patterns tried in order when <see cref="Pattern"/> misses or is ambiguous.
    /// The <c>"fallbacks"</c> key.
    /// </summary>
    /// <remarks>
    /// A pattern is a snapshot of one compiler output, and a content patch re-rolls it: the
    /// register the compiler picked for a comparison, the size of an allocation next to it.
    /// Each fallback wildcards one of those, so the same site is still found when only that
    /// detail changed - and because a wildcard also matches other sites, a fallback never
    /// resolves on its hit count alone. Every hit is offered as a candidate with its bytes,
    /// and only a fingerprint check on what the hit resolves to can pick one. A hit that
    /// resolves to the right thing is the answer; the bytes at its site are the new exact
    /// pattern, ready to be written back as the primary.
    /// </remarks>
    public IReadOnlyList<string> Fallbacks { get; }
}

/// <summary>A struct layout: named fields at offsets, plus non-memory constants.</summary>
public sealed class StructDef
{
    public StructDef(
        string name,
        string? comment,
        IReadOnlyList<FieldDef> fields,
        IReadOnlyDictionary<string, long> constants)
    {
        Name = name;
        Comment = comment;
        Fields = fields;
        Constants = constants;
    }

    public string Name { get; }

    public string? Comment { get; }

    /// <summary>Fields in ascending offset order (the loader sorts them).</summary>
    public IReadOnlyList<FieldDef> Fields { get; }

    /// <summary>
    /// Named constants that belong to the struct but are not memory fields - entry
    /// sizes, array strides, well-known indices.
    /// </summary>
    public IReadOnlyDictionary<string, long> Constants { get; }

    /// <summary>Looks up a field by name, or null.</summary>
    public FieldDef? Field(string name)
    {
        foreach (FieldDef field in Fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the field's offset, throwing with a message that names both struct and
    /// field when it is missing - schema lookups must never fail silently.
    /// </summary>
    public int OffsetOf(string fieldName)
        => Field(fieldName)?.Offset
           ?? throw new KeyNotFoundException($"Schema struct '{Name}' has no field '{fieldName}'.");
}

/// <summary>One field of a struct.</summary>
public sealed class FieldDef
{
    public FieldDef(
        string name,
        int offset,
        FieldType type,
        string? comment,
        Invariant? invariant,
        string? target = null,
        bool inline = false,
        IReadOnlyDictionary<long, string>? values = null)
    {
        Name = name;
        Offset = offset;
        Type = type;
        Comment = comment;
        Invariant = invariant;
        Target = target;
        Inline = inline;
        Values = values;
    }

    public string Name { get; }

    /// <summary>Byte offset from the struct base.</summary>
    public int Offset { get; }

    public FieldType Type { get; }

    /// <summary>
    /// Free-text notes - drift history, verification status, source. This is where the
    /// hard-won RE knowledge lives, right next to the value it explains.
    /// </summary>
    public string? Comment { get; }

    /// <summary>What a correct value looks like, or null when nothing is declared.</summary>
    public Invariant? Invariant { get; }

    /// <summary>
    /// Another struct in this schema that this field leads to. The <c>"struct"</c> key.
    /// </summary>
    /// <remarks>
    /// What turns a wall of addresses into something readable. Life.Health is a Vital, and
    /// saying so is the difference between "0x333C00003335" and a pool with a maximum in it.
    /// The knowledge was already in the schema - in a COMMENT, where only a person could use
    /// it - and this is the same sentence in a form the tool can act on.
    /// </remarks>
    public string? Target { get; }

    /// <summary>
    /// Whether <see cref="Target"/> sits AT this offset rather than behind a pointer.
    /// The <c>"inline"</c> key.
    /// </summary>
    /// <remarks>
    /// Both shapes are everywhere in this game and they look identical from outside: eight
    /// bytes that are either an address or the start of a struct. Reading one as the other
    /// produces a plausible-looking wrong answer rather than a failure, which is why it is
    /// declared rather than guessed.
    /// </remarks>
    public bool Inline { get; }

    /// <summary>
    /// What this field's numbers MEAN, when they are a fixed set. The <c>"values"</c> key.
    /// </summary>
    /// <remarks>
    /// A rarity of 2 tells nobody anything; "Rare" does. Partial maps are fine and expected -
    /// an unlisted value keeps its number, which is how a value nobody has seen before stays
    /// visible instead of being hidden behind a wrong name.
    /// </remarks>
    public IReadOnlyDictionary<long, string>? Values { get; }
}

/// <summary>Wire type of a field. Determines read size and display formatting.</summary>
public enum FieldType
{
    U8,
    U16,
    U32,
    U64,
    I8,
    I16,
    I32,
    I64,
    F32,
    F64,

    /// <summary>64-bit pointer.</summary>
    Ptr,

    /// <summary>4x4 float matrix, row-major, 64 bytes.</summary>
    Mat4x4,

    /// <summary>MSVC std::vector header: begin, end, capacity-end (24 bytes).</summary>
    StdVector,

    /// <summary>MSVC std::wstring with small-string optimisation (32 bytes).</summary>
    StdWString,

    /// <summary>Pointer to a NUL-terminated UTF-16 string.</summary>
    Utf16Ptr,
}

/// <summary>Byte sizes and helpers for <see cref="FieldType"/>.</summary>
public static class FieldTypes
{
    /// <summary>Read size of a field in bytes.</summary>
    public static int SizeOf(FieldType type) => type switch
    {
        FieldType.U8 or FieldType.I8 => 1,
        FieldType.U16 or FieldType.I16 => 2,
        FieldType.U32 or FieldType.I32 or FieldType.F32 => 4,
        FieldType.U64 or FieldType.I64 or FieldType.F64 or FieldType.Ptr or FieldType.Utf16Ptr => 8,
        FieldType.Mat4x4 => 64,
        FieldType.StdVector => 24,
        FieldType.StdWString => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
