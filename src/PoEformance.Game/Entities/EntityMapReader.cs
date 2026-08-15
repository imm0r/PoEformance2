using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Entities;

/// <summary>
/// Walks the game's entity std::map and yields each live entity's id and address.
/// </summary>
/// <remarks>
/// The game keeps its entities in an MSVC <c>std::map</c> keyed by entity id - a red-black
/// tree. The struct's first field is the sentinel <c>Head</c> node, whose Parent is the
/// real root; every other node carries Left/Right children plus the id and the entity
/// pointer.
///
/// The walk is breadth-first with a visited set rather than a recursive in-order walk,
/// for one reason: this is a LIVE tree being mutated by the game while we read it. A
/// pointer can be stale by the time we follow it, and a cycle or self-reference would hang
/// a naive recursion. The visited set plus a hard node budget make a corrupted read cost a
/// truncated result, never a freeze.
///
/// Ported from the AHK tool's <c>ScanEntityMapIdsAndPtrs</c>, including its id filter:
/// ids at or above <see cref="MaxRealEntityId"/> are the game's visual/decoration entities
/// and are skipped.
/// </remarks>
public sealed class EntityMapReader
{
    /// <summary>
    /// Entity ids at or above this are visuals and decorations, not gameplay entities.
    /// (The C# reference calls this filter IgnoreVisualsAndDecorations.)
    /// </summary>
    /// <remarks>
    /// AND PROJECTILES ARE AMONG THEM, which cost a day to establish and is written down so
    /// nobody spends a second one. A recording of a Spark build casting for twenty seconds -
    /// a screen solid with lightning - held 68 nodes per frame in this tree and yielded 17
    /// entities: the player, three markers, a drop and the monsters. Everything the skill put
    /// on the screen was in the other 51, thrown away here before a single component was read.
    /// Nothing downstream could see them, so a projectile overlay drew nothing and the effects
    /// list showed three doors.
    ///
    /// The reference filters them by default too, and gives itself a way back in:
    /// <c>ProcessAllRenderableEntities</c>, against <c>EntityFilter.IgnoreVisualsAndDecorations</c>.
    /// Its own note on the neighbouring list says what these are - "Decorations, Effects,
    /// particles and etc." - and a projectile in flight is exactly that sort of object.
    /// </remarks>
    public const uint MaxRealEntityId = 0x40000000;

    /// <summary>Bytes covering Left..ValueEntityPtr, read in one go per node.</summary>
    private const int NodeReadSize = 0x30;

    private readonly IMemoryReader _reader;
    private readonly int _mapHead;
    private readonly int _mapSize;
    private readonly int _nodeLeft;
    private readonly int _nodeParent;
    private readonly int _nodeRight;
    private readonly int _nodeKeyId;
    private readonly int _nodeValue;

    public EntityMapReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef map = schema.Structs["StdMap"];
        StructDef node = schema.Structs["StdMapNode"];
        _mapHead = map.OffsetOf("Head");
        _mapSize = map.OffsetOf("Size");
        _nodeLeft = node.OffsetOf("Left");
        _nodeParent = node.OffsetOf("Parent");
        _nodeRight = node.OffsetOf("Right");
        _nodeKeyId = node.OffsetOf("KeyId");
        _nodeValue = node.OffsetOf("ValueEntityPtr");
    }

    /// <summary>
    /// Reads every gameplay entity in the map at <paramref name="mapStructAddress"/> (the
    /// ADDRESS of the std::map struct - e.g. AreaInstance + AwakeEntities - not a pointer
    /// read from it). Returns id -> entity address.
    /// </summary>
    /// <param name="maxEntities">
    /// Hard cap on GAMEPLAY entities, protecting against a corrupt tree. Visuals do not count
    /// against it - see <paramref name="includeVisuals"/>.
    /// </param>
    /// <param name="includeVisuals">
    /// Keep the high-id entities as well: the effects, the decorations and the projectiles.
    /// </param>
    public Dictionary<uint, ulong> ReadEntityPointers(
        ulong mapStructAddress, int maxEntities = 4096, bool includeVisuals = false)
    {
        var result = new Dictionary<uint, ulong>();
        if (!MemoryReaderExtensions.IsPlausiblePointer(mapStructAddress))
        {
            return result;
        }

        ulong head = _reader.ReadPointer(mapStructAddress + (ulong)_mapHead);
        long size = _reader.Read<long>(mapStructAddress + (ulong)_mapSize);
        if (head == 0 || size < 1 || size > 200_000)
        {
            return result;
        }

        ulong root = _reader.ReadPointer(head + (ulong)_nodeParent);
        if (root == 0 || root == head)
        {
            return result;
        }

        // Breadth-first with a visited set: the tree is live and may mutate mid-read.
        var queue = new Queue<ulong>();
        var visited = new HashSet<ulong>();
        queue.Enqueue(root);

        long visitBudget = Math.Min((size * 2) + 20, 8000);
        var nodeBuffer = new byte[NodeReadSize];

        // Counted apart from the results, and that is the whole point of it. The walk is
        // breadth-first, so which node comes next is a fact about the tree's shape rather than
        // about what matters - and with the visuals let in they outnumber the gameplay
        // entities three to one. Counted together, a cap of 512 would be reached by a screen
        // of sparks and the monsters behind them would silently stop being read. So the budget
        // belongs to the entities the cap was written for; the visuals ride along inside the
        // visit budget, which bounds the walk itself.
        int gameplay = 0;

        while (queue.Count > 0 && visited.Count < visitBudget && gameplay < maxEntities)
        {
            ulong node = queue.Dequeue();
            if (node == head || !MemoryReaderExtensions.IsPlausiblePointer(node) || !visited.Add(node))
            {
                continue;
            }

            // One read per node covers Left, Right, KeyId and the entity pointer.
            if (!_reader.TryRead(node, nodeBuffer))
            {
                continue;
            }

            ReadOnlySpan<byte> span = nodeBuffer;
            ulong left = BitConverter.ToUInt64(span[_nodeLeft..]);
            ulong right = BitConverter.ToUInt64(span[_nodeRight..]);
            uint entityId = BitConverter.ToUInt32(span[_nodeKeyId..]);
            ulong entityPtr = BitConverter.ToUInt64(span[_nodeValue..]);

            bool real = entityId > 0 && entityId < MaxRealEntityId;
            if ((real || (includeVisuals && entityId > 0))
                && MemoryReaderExtensions.IsPlausiblePointer(entityPtr))
            {
                result[entityId] = entityPtr;
                if (real)
                {
                    gameplay++;
                }
            }

            if (left != head && MemoryReaderExtensions.IsPlausiblePointer(left))
            {
                queue.Enqueue(left);
            }

            if (right != head && MemoryReaderExtensions.IsPlausiblePointer(right))
            {
                queue.Enqueue(right);
            }
        }

        return result;
    }
}
