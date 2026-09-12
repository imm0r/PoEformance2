using System.Buffers.Binary;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Diagnostics;

/// <summary>One AreaInstance field found by its fingerprint rather than by its schema offset.</summary>
/// <param name="Field">Schema field name.</param>
/// <param name="SchemaOffset">Where the schema currently says it is.</param>
/// <param name="FoundOffset">Where the fingerprint says it is.</param>
/// <param name="Evidence">What was seen there, in words - the reason to believe the offset.</param>
public sealed record TailCandidate(string Field, int SchemaOffset, int FoundOffset, string Evidence)
{
    /// <summary>Signed distance from the schema offset; the wave delta when every field agrees.</summary>
    public int Delta => FoundOffset - SchemaOffset;
}

/// <summary>What the hunt found in one AreaInstance.</summary>
/// <param name="Found">Every field whose fingerprint matched somewhere in the window.</param>
/// <param name="Consensus">
/// The delta all three strong fingerprints agree on, or null when one is missing or they
/// disagree. A consensus is the "+0x08 wave" answer: the whole tail moved by this much.
/// </param>
public sealed record AreaInstanceHuntResult(IReadOnlyList<TailCandidate> Found, int? Consensus)
{
    public TailCandidate? Candidate(string field)
    {
        foreach (TailCandidate c in Found)
        {
            if (c.Field == field)
            {
                return c;
            }
        }

        return null;
    }
}

/// <summary>
/// Finds the AreaInstance tail fields by what they are, not by where the schema says they
/// are: the player slot, the entity maps and the terrain struct each have a shape that no
/// other slot in the struct has, and a sweep over the struct picks them out wherever a
/// patch has pushed them.
/// </summary>
/// <remarks>
/// Every AreaInstance drift so far has been an insertion: a field appears somewhere in the
/// middle and everything after it moves by the same amount. The schema comment says "when
/// one field drifts, assume the whole tail moved and check them all" - this is that check
/// performed by the tool, on the first attach after a patch, with the answer printed.
///
/// The fingerprints are the ones already proven against real memory, written down in the
/// schema comments and used by the readers every frame:
/// <list type="bullet">
/// <item>PlayerInfo: the schema's own LocalPlayerStruct invariants - a pointer at +0x00 and,
/// at +0x20, an entity whose metadata path names a character. Nothing else in the struct
/// leads to a "Metadata/Characters/..." string.</item>
/// <item>AwakeEntities: an MSVC std::map header - a sentinel node flagged nil whose parent is
/// a real node holding an entity, and a count next to it. SleepingEntities is the same shape
/// 0x10 later, and is checked there rather than hunted separately.</item>
/// <item>TerrainMetadata: an INLINE struct that starts with a vtable and a back-pointer to
/// the AreaInstance that owns it - a slot holding this very struct's own address, which is
/// as self-validating as a fingerprint gets - followed by the tile counts and their
/// plus-one twins.</item>
/// </list>
/// The strong three decide the delta between them. Environments has no shape of its own (a
/// vector of ints looks like any vector), so it is only checked at the consensus delta.
/// </remarks>
public static class AreaInstanceHunt
{
    /// <summary>The window swept: the low fields have never moved, the tail sits below 0x1000.</summary>
    public const int WindowStart = 0x400;

    public const int WindowEnd = 0x1000;

    /// <summary>Largest entity count a live map is believed to hold; the map reader's own bound.</summary>
    private const long MaxEntities = 200_000;

    /// <summary>Tile counts outside this are not an area - the TerrainMetadata invariants' range.</summary>
    private const long MaxTiles = 4096;

    private const string CharacterPath = "Metadata/Characters";

    /// <summary>
    /// Sweeps <paramref name="areaInstance"/> for the tail fields and reports where each
    /// fingerprint matched, with the delta from the schema.
    /// </summary>
    public static AreaInstanceHuntResult Run(IMemoryReader reader, OffsetSchema schema, ulong areaInstance)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        var found = new List<TailCandidate>();
        if (areaInstance == 0)
        {
            return new AreaInstanceHuntResult(found, null);
        }

        StructDef area = schema.Structs["AreaInstance"];
        byte[] window = ReadWindow(reader, areaInstance);

        TailCandidate? player = FindPlayerInfo(reader, schema, area, areaInstance, window);
        TailCandidate? awake = FindEntityMaps(reader, schema, area, window, found);
        TailCandidate? terrain = FindTerrain(reader, schema, area, areaInstance, window);

        if (player is not null)
        {
            found.Add(player);
        }

        if (terrain is not null)
        {
            found.Add(terrain);
        }

        int? consensus = null;
        if (player is not null && awake is not null && terrain is not null
            && player.Delta == awake.Delta && awake.Delta == terrain.Delta)
        {
            consensus = player.Delta;
            CheckEnvironments(area, areaInstance, window, consensus.Value, found);
        }

        found.Sort((a, b) => a.FoundOffset.CompareTo(b.FoundOffset));
        return new AreaInstanceHuntResult(found, consensus);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is an AreaInstance: it carries a terrain struct
    /// that points back at it, or a player slot. The probe for the parent-pointer scan.
    /// </summary>
    /// <remarks>
    /// The other way a struct "drifts": nothing inside it moved, the pointer leading to it did,
    /// and every field reads misaligned. InGameState.AreaInstanceData has done exactly that
    /// once (0x288 to 0x290). A neighbour slot whose target has a back-pointer to itself at
    /// some 8-byte offset is an AreaInstance and no coincidence.
    /// </remarks>
    public static bool LooksLikeAreaInstance(IMemoryReader reader, OffsetSchema schema, ulong candidate)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        if (!MemoryReaderExtensions.IsPlausiblePointer(candidate))
        {
            return false;
        }

        StructDef area = schema.Structs["AreaInstance"];
        byte[] window = ReadWindow(reader, candidate);
        return FindTerrain(reader, schema, area, candidate, window) is not null
            || FindPlayerInfo(reader, schema, area, candidate, window) is not null;
    }

    /// <summary>
    /// Reads the tail window in one go where it can, and slot by slot where it cannot.
    /// </summary>
    /// <remarks>
    /// The slot-by-slot fallback is for replays: a recording holds only the reads the session
    /// made, so a 256-byte chunk fails there even when the eight bytes at the schema offset
    /// are present. Degrading to single slots lets the hunt run against every existing
    /// fixture and find the tail where those sessions read it - which is the regression test.
    /// Unreadable slots stay zero, and zero matches no fingerprint.
    /// </remarks>
    private static byte[] ReadWindow(IMemoryReader reader, ulong areaInstance)
    {
        var window = new byte[WindowEnd - WindowStart];
        const int chunk = 0x100;
        for (int position = 0; position < window.Length; position += chunk)
        {
            Span<byte> slice = window.AsSpan(position, chunk);
            if (reader.TryRead(areaInstance + (ulong)(WindowStart + position), slice))
            {
                continue;
            }

            for (int slot = 0; slot < chunk; slot += 8)
            {
                reader.TryRead(areaInstance + (ulong)(WindowStart + position + slot), slice.Slice(slot, 8));
            }
        }

        return window;
    }

    private static ulong SlotAt(byte[] window, int offset)
    {
        int index = offset - WindowStart;
        return index >= 0 && index + 8 <= window.Length
            ? BinaryPrimitives.ReadUInt64LittleEndian(window.AsSpan(index))
            : 0;
    }

    private static TailCandidate? FindPlayerInfo(IMemoryReader reader, OffsetSchema schema, StructDef area, ulong areaInstance, byte[] window)
    {
        StructDef local = schema.Structs["LocalPlayerStruct"];
        int playerPtr = local.OffsetOf("LocalPlayerPtr");
        int serverData = local.OffsetOf("ServerDataPtr");
        int entityDetails = schema.Structs["Entity"].OffsetOf("EntityDetailsPtr");
        int path = schema.Structs["EntityDetails"].OffsetOf("Path");
        int schemaOffset = area.OffsetOf("PlayerInfo");

        for (int offset = WindowStart; offset + playerPtr + 8 <= WindowEnd; offset += 8)
        {
            // Both slots are in the window already, so the expensive part - two hops and a
            // string - only runs where the cheap shape fits.
            ulong entity = SlotAt(window, offset + playerPtr);
            if (!MemoryReaderExtensions.IsPlausiblePointer(entity)
                || !MemoryReaderExtensions.IsPlausiblePointer(SlotAt(window, offset + serverData)))
            {
                continue;
            }

            ulong details = reader.ReadPointer(entity + (ulong)entityDetails);
            if (details == 0)
            {
                continue;
            }

            string text = reader.ReadStdWString(details + (ulong)path, 128);
            if (text.Contains(CharacterPath, StringComparison.Ordinal))
            {
                return new TailCandidate("PlayerInfo", schemaOffset, offset, $"+0x{playerPtr:X} leads to entity 0x{entity:X} \"{text}\"");
            }
        }

        return null;
    }

    /// <summary>
    /// Finds AwakeEntities and, at the next header, SleepingEntities. Returns the awake one
    /// (the strong fingerprint) and adds both to <paramref name="found"/>.
    /// </summary>
    private static TailCandidate? FindEntityMaps(IMemoryReader reader, OffsetSchema schema, StructDef area, byte[] window, List<TailCandidate> found)
    {
        StructDef map = schema.Structs["StdMap"];
        StructDef node = schema.Structs["StdMapNode"];
        int head = map.OffsetOf("Head");
        int size = map.OffsetOf("Size");
        int parent = node.OffsetOf("Parent");
        int isNil = node.OffsetOf("IsNil");
        int value = node.OffsetOf("ValueEntityPtr");
        int entityDetails = schema.Structs["Entity"].OffsetOf("EntityDetailsPtr");
        int path = schema.Structs["EntityDetails"].OffsetOf("Path");
        int awakeOffset = area.OffsetOf("AwakeEntities");
        int sleepingOffset = area.OffsetOf("SleepingEntities");
        int mapStride = sleepingOffset - awakeOffset;

        for (int offset = WindowStart; offset + 16 <= WindowEnd; offset += 8)
        {
            ulong sentinel = SlotAt(window, offset + head);
            long count = (long)SlotAt(window, offset + size);
            if (!MemoryReaderExtensions.IsPlausiblePointer(sentinel) || count < 1 || count > MaxEntities)
            {
                continue;
            }

            // The sentinel is the one node flagged nil, and its parent is the tree root -
            // a real node whose value is an entity. An awake map always has at least the
            // player in it, so an empty tree here is not this map.
            if (reader.Read<byte>(sentinel + (ulong)isNil) != 1)
            {
                continue;
            }

            ulong root = reader.ReadPointer(sentinel + (ulong)parent);
            if (root == 0 || root == sentinel || reader.Read<byte>(root + (ulong)isNil) != 0)
            {
                continue;
            }

            ulong entity = reader.ReadPointer(root + (ulong)value);
            ulong details = entity == 0 ? 0 : reader.ReadPointer(entity + (ulong)entityDetails);
            if (details == 0)
            {
                continue;
            }

            string text = reader.ReadStdWString(details + (ulong)path, 128);
            if (!text.StartsWith("Metadata/", StringComparison.Ordinal))
            {
                continue;
            }

            var awake = new TailCandidate("AwakeEntities", awakeOffset, offset, $"std::map of {count} entities, root holds \"{text}\"");
            found.Add(awake);

            // The sleeping map sits one header later and may legitimately be empty, which
            // is why it is verified in place rather than hunted: its own sentinel is the
            // only shape an empty map has.
            int sleepingAt = offset + mapStride;
            ulong sleepingSentinel = SlotAt(window, sleepingAt + head);
            long sleepingCount = (long)SlotAt(window, sleepingAt + size);
            if (MemoryReaderExtensions.IsPlausiblePointer(sleepingSentinel)
                && sleepingCount >= 0 && sleepingCount <= MaxEntities
                && reader.Read<byte>(sleepingSentinel + (ulong)isNil) == 1)
            {
                found.Add(new TailCandidate("SleepingEntities", sleepingOffset, sleepingAt, $"std::map of {sleepingCount} entities, 0x{mapStride:X} after AwakeEntities"));
            }

            return awake;
        }

        return null;
    }

    private static TailCandidate? FindTerrain(IMemoryReader reader, OffsetSchema schema, StructDef area, ulong areaInstance, byte[] window)
    {
        StructDef terrain = schema.Structs["TerrainMetadata"];
        int tilesX = terrain.OffsetOf("TotalTilesX");
        int tilesY = terrain.OffsetOf("TotalTilesY");
        int plusOneX = terrain.OffsetOf("TotalTilesPlusOneX");
        int schemaOffset = area.OffsetOf("TerrainMetadata");
        ulong moduleBase = reader.ModuleBase;
        ulong moduleEnd = moduleBase + reader.ModuleSize;

        for (int offset = WindowStart; offset + 16 <= WindowEnd; offset += 8)
        {
            // A vtable (an address inside the game's own image) followed by this struct's
            // own address. Neither alone is rare; together they are the terrain struct.
            ulong vtable = SlotAt(window, offset);
            if (vtable < moduleBase || vtable >= moduleEnd || SlotAt(window, offset + 8) != areaInstance)
            {
                continue;
            }

            // The tile counts may lie past the window; read them directly.
            ulong terrainBase = areaInstance + (ulong)offset;
            long x = reader.Read<long>(terrainBase + (ulong)tilesX);
            long y = reader.Read<long>(terrainBase + (ulong)tilesY);
            if (x < 1 || x > MaxTiles || y < 1 || y > MaxTiles)
            {
                continue;
            }

            long x1 = reader.Read<long>(terrainBase + (ulong)plusOneX);
            long y1 = reader.Read<long>(terrainBase + (ulong)plusOneX + 8);
            string twins = x1 == x + 1 && y1 == y + 1 ? "plus-one pair agrees" : "plus-one pair NOT read";
            return new TailCandidate("TerrainMetadata", schemaOffset, offset, $"vtable + back-pointer to self, {x} x {y} tiles, {twins}");
        }

        return null;
    }

    /// <summary>
    /// Environments has no fingerprint of its own, so it is only judged at the consensus
    /// delta: does a std::vector of 4-byte keys sit there?
    /// </summary>
    private static void CheckEnvironments(StructDef area, ulong areaInstance, byte[] window, int delta, List<TailCandidate> found)
    {
        if (area.Field("Environments") is not { } environments)
        {
            return;
        }

        // The insertion point decides whether this field moved at all: it sits below the
        // strong three, so a field inserted between it and them leaves it where it was.
        // Both places are judged and both answers are printed, because a vector shape is
        // weak evidence and the person reading the report should see what it rests on.
        int moved = environments.Offset + delta;
        bool shapedAtMoved = VectorShaped(window, moved, out long keysAtMoved);
        bool shapedInPlace = VectorShaped(window, environments.Offset, out long keysInPlace);
        string evidence = (shapedAtMoved, shapedInPlace) switch
        {
            (true, false) => $"vector-shaped only at the consensus delta, {keysAtMoved} keys (weak: any int vector looks like this)",
            (false, true) => $"NOT vector-shaped at the consensus delta but still vector-shaped in place, {keysInPlace} keys - the insertion sits above it, so it did not move",
            (true, true) => $"vector-shaped at BOTH the schema offset ({keysInPlace} keys) and the consensus delta ({keysAtMoved} keys) - undecidable from shape alone",
            _ => "NOT vector-shaped at the schema offset nor at the consensus delta",
        };
        found.Add(new TailCandidate("Environments", environments.Offset, shapedAtMoved && !shapedInPlace ? moved : environments.Offset, evidence));
    }

    private static bool VectorShaped(byte[] window, int at, out long keys)
    {
        ulong begin = SlotAt(window, at);
        ulong end = SlotAt(window, at + 8);
        ulong capacity = SlotAt(window, at + 16);
        keys = 0;
        if (!MemoryReaderExtensions.IsPlausiblePointer(begin)
            || end < begin || capacity < end
            || (end - begin) % 4 != 0 || end - begin > 64 * 4
            || capacity - begin > 4096)
        {
            return false;
        }

        keys = (long)(end - begin) / 4;
        return true;
    }
}
