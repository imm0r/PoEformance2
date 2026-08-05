using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Entities;

/// <summary>
/// Reads an <see cref="Entity"/> from its address: metadata path plus the component
/// name -> address map.
/// </summary>
/// <remarks>
/// The component lookup is the one genuinely fiddly read in the game, so it is worth
/// stating plainly. An entity holds two parallel things:
///
///   1. a flat VECTOR of component pointers (Entity.ComponentsVec), addressed by index;
///   2. a hash BUCKET (EntityDetails -> ComponentLookup -> Bucket) of
///      (name-pointer, index) entries.
///
/// So to find "the Render component" you walk the bucket, read each entry's name, and
/// when the name matches you use its index to pick the pointer out of the vector. This
/// reader does that once and returns the whole name -> address map, which is far more
/// useful than repeated single lookups.
///
/// Component names are static in the game's memory (they never move or change), so their
/// strings are cached by pointer - the same optimisation the AHK reference makes, and it
/// turns dozens of string reads per entity into a handful after warm-up.
///
/// Ported from the AHK tool's <c>ReadEntityComponentLookupBasic</c>; the offsets all come
/// from the schema so a drift is fixed there, not here.
/// </remarks>
public sealed class EntityReader
{
    private readonly IMemoryReader _reader;

    // Schema offsets, resolved once in the constructor so the hot path does no dictionary
    // lookups. If the schema lacks a field the constructor throws with its name - a
    // configuration error should fail loudly at startup, not mis-read memory later.
    private readonly int _entityDetailsPtr;
    private readonly int _componentsVecBegin;
    private readonly int _componentsVecEnd;
    private readonly int _entityId;
    private readonly int _detailsPath;
    private readonly int _detailsComponentLookupPtr;
    private readonly int _lookupBucket;
    private readonly int _bucketData;
    private readonly int _bucketDataLast;
    private readonly int _bucketCapacity;
    private readonly int _entryNamePtr;
    private readonly int _entryIndex;
    private readonly int _entrySize;

    /// <summary>Component-name strings are static; cache them by their pointer.</summary>
    private readonly Dictionary<ulong, string> _nameCache = [];

    public EntityReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef entity = schema.Structs["Entity"];
        StructDef details = schema.Structs["EntityDetails"];
        StructDef lookup = schema.Structs["ComponentLookup"];
        StructDef bucket = schema.Structs["StdBucket"];
        StructDef entry = schema.Structs["ComponentLookupEntry"];

        _entityDetailsPtr = entity.OffsetOf("EntityDetailsPtr");
        _componentsVecBegin = entity.OffsetOf("ComponentsVec");
        _componentsVecEnd = entity.OffsetOf("ComponentsVecLast");
        _entityId = entity.OffsetOf("Id");
        _detailsPath = details.OffsetOf("Path");
        _detailsComponentLookupPtr = details.OffsetOf("ComponentLookupPtr");
        _lookupBucket = lookup.OffsetOf("Bucket");
        _bucketData = bucket.OffsetOf("Data");
        _bucketDataLast = bucket.OffsetOf("DataLast");
        _bucketCapacity = bucket.OffsetOf("Capacity");
        _entryNamePtr = entry.OffsetOf("NamePtr");
        _entryIndex = entry.OffsetOf("Index");
        _entrySize = checked((int)entry.Constants["Size"]);
    }

    /// <summary>
    /// Reads the entity at <paramref name="address"/>. Returns null only when the address
    /// itself is not a plausible entity (bad details pointer); an entity with an empty
    /// path or no components is still returned, because "it exists but decoded thin" is
    /// useful information, not an error.
    /// </summary>
    public Entity? Read(ulong address)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(address))
        {
            return null;
        }

        ulong details = _reader.ReadPointer(address + (ulong)_entityDetailsPtr);
        if (details == 0)
        {
            return null;
        }

        uint id = _reader.Read<uint>(address + (ulong)_entityId);
        string path = _reader.ReadStdWString(details + (ulong)_detailsPath);
        IReadOnlyDictionary<string, ulong> components = ReadComponents(address, details);

        return new Entity(address, id, path, components);
    }

    /// <summary>
    /// Reads just the component name -> address map for an entity, given its address and
    /// its already-resolved details pointer.
    /// </summary>
    public IReadOnlyDictionary<string, ulong> ReadComponents(ulong entityAddress, ulong detailsAddress)
    {
        var components = new Dictionary<string, ulong>(StringComparer.Ordinal);

        // The component pointer vector: begin/end bracket an array of pointers.
        ulong vecBegin = _reader.ReadPointer(entityAddress + (ulong)_componentsVecBegin);
        ulong vecEnd = _reader.Read<ulong>(entityAddress + (ulong)_componentsVecEnd);
        if (vecBegin == 0 || vecEnd < vecBegin)
        {
            return components;
        }

        long componentCount = (long)(vecEnd - vecBegin) / 8;
        if (componentCount is < 0 or > 512)
        {
            return components;
        }

        // The hash bucket: (name-pointer, index) entries.
        ulong lookup = _reader.ReadPointer(detailsAddress + (ulong)_detailsComponentLookupPtr);
        if (lookup == 0)
        {
            return components;
        }

        ulong bucket = lookup + (ulong)_lookupBucket;
        int capacity = _reader.Read<int>(bucket + (ulong)_bucketCapacity);
        if (capacity is <= 0 or > 4096)
        {
            return components;
        }

        ulong dataFirst = _reader.ReadPointer(bucket + (ulong)_bucketData);
        ulong dataLast = _reader.Read<ulong>(bucket + (ulong)_bucketDataLast);
        if (dataFirst == 0 || dataLast < dataFirst)
        {
            return components;
        }

        long entryCount = (long)(dataLast - dataFirst) / _entrySize;
        if (entryCount <= 0)
        {
            return components;
        }

        int readCount = (int)Math.Min(entryCount, 256);
        for (int i = 0; i < readCount; i++)
        {
            ulong entryAddress = dataFirst + (ulong)(i * _entrySize);
            ulong namePtr = _reader.ReadPointer(entryAddress + (ulong)_entryNamePtr);
            long index = _reader.Read<long>(entryAddress + (ulong)_entryIndex);

            // Empty hash slots carry an out-of-range index; skip them.
            if (index < 0 || index >= componentCount || namePtr == 0)
            {
                continue;
            }

            ulong componentPtr = _reader.ReadPointer(vecBegin + (ulong)(index * 8));
            if (componentPtr == 0)
            {
                continue;
            }

            string name = ReadComponentName(namePtr);
            if (name.Length > 0 && !components.ContainsKey(name))
            {
                components[name] = componentPtr;
            }
        }

        return components;
    }

    /// <summary>Reads (and caches) a component's name string. Names are ASCII in practice.</summary>
    private string ReadComponentName(ulong namePtr)
    {
        if (_nameCache.TryGetValue(namePtr, out string? cached))
        {
            return cached;
        }

        string name = _reader.ReadUtf8(namePtr, 64);
        if (name.Length > 0 && IsPlausibleComponentName(name))
        {
            _nameCache[namePtr] = name;
            return name;
        }

        return string.Empty;
    }

    /// <summary>
    /// A component name is a short run of ASCII letters (Render, Positioned, Life, ...).
    /// This rejects the garbage that a stale pointer would otherwise turn into a bogus
    /// component - the same guard the AHK reference applies.
    /// </summary>
    private static bool IsPlausibleComponentName(string name)
    {
        if (name.Length is < 2 or > 48)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return char.IsAsciiLetterUpper(name[0]);
    }
}
