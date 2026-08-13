using PoEformance.Core.Schema;

namespace PoEformance.Core.Diagnostics;

/// <summary>
/// Where a dat table keeps its path and its rows, taken from the schema rather than written
/// down here.
/// </summary>
/// <remarks>
/// <see cref="PointerPeek"/> is otherwise pure shape - it recognises text, vectors and rows
/// from the bytes alone and hard-codes no offset of this game. Naming a table needs four of
/// them, and offsets in this project are data. So they arrive as this, built once by whoever
/// already holds the schema.
/// </remarks>
public readonly record struct DatTableShape(
    int PathOffset,
    int RowStoreOffset,
    int RowsOffset,
    int ByIdIndexOffset,
    int ByIdEntrySize,
    int ByIdEntryRowAt)
{
    /// <summary>
    /// Reads the shape out of a schema, or null when it does not describe dat tables.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw because the schema hot-reloads: an edit that renames a field
    /// should cost the table label, not the dissector.
    /// </remarks>
    public static DatTableShape? From(OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (!schema.Structs.TryGetValue("DatTable", out StructDef? table)
            || !schema.Structs.TryGetValue("DatRowStore", out StructDef? store)
            || table.Field("Path") is not { } path
            || table.Field("RowStorePtr") is not { } rowStore
            || store.Field("Rows") is not { } rows
            || store.Field("ByIdIndex") is not { } byId
            || !store.Constants.TryGetValue("ByIdEntrySize", out long entrySize)
            || !store.Constants.TryGetValue("ByIdEntryRowAt", out long entryRowAt))
        {
            return null;
        }

        return new DatTableShape(
            path.Offset,
            rowStore.Offset,
            rows.Offset,
            byId.Offset,
            checked((int)entrySize),
            checked((int)entryRowAt));
    }
}
