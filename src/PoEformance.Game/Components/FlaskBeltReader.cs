using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Components;

/// <summary>One equipped flask and its charge state.</summary>
/// <param name="Slot">Belt slot 1-5.</param>
/// <param name="Path">The item's metadata path - what kind of flask this is.</param>
/// <param name="Charges">Charges currently held.</param>
/// <param name="ChargesPerUse">What one use costs.</param>
public readonly record struct EquippedFlask(int Slot, string Path, int Charges, int ChargesPerUse)
{
    /// <summary>
    /// True for a charm rather than a flask.
    /// </summary>
    /// <remarks>
    /// Charms sit in the same belt inventory and under the same Flasks/ metadata path, so
    /// they arrive here looking like flasks - but the GAME triggers them itself on their own
    /// conditions. There is no key to press, so a rule targeting a charm slot would send
    /// keystrokes that can never do anything.
    /// </remarks>
    public bool IsCharm => Path.Contains("Charm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether pressing this flask's key would actually do anything.
    /// </summary>
    /// <remarks>
    /// Too few charges means the flask does not trigger: no charge is spent and no in-game
    /// cooldown starts. So the check is not about protecting the game from us - it is about
    /// not emitting input that cannot do anything. Sending a keystroke thirty times a
    /// second to no effect is work the tool should not be doing, and it would make the
    /// status readout claim uses that never happened. See the gate in AutoFlask.
    /// </remarks>
    public bool CanUse => ChargesPerUse > 0 && Charges >= ChargesPerUse;
}

/// <summary>The flask belt, indexed by slot.</summary>
public sealed class FlaskBelt
{
    public static FlaskBelt Empty { get; } = new([]);

    /// <summary>The flasks found, in slot order.</summary>
    public IReadOnlyList<EquippedFlask> Flasks { get; }

    /// <summary>True when the belt could not be read at all, as opposed to being empty.</summary>
    public bool IsUnknown => Flasks.Count == 0;

    public FlaskBelt(IReadOnlyList<EquippedFlask> flasks)
    {
        ArgumentNullException.ThrowIfNull(flasks);
        Flasks = flasks;
    }

    /// <summary>The flask in a belt slot, or null when that slot is empty or unreadable.</summary>
    public EquippedFlask? InSlot(int slot)
    {
        foreach (EquippedFlask flask in Flasks)
        {
            if (flask.Slot == slot)
            {
                return flask;
            }
        }

        return null;
    }
}

/// <summary>
/// Reads the equipped flasks and their charges out of the player's inventories.
/// </summary>
/// <remarks>
/// FINDING THE BELT IS THE HARD PART, and it is not done by inventory id. The AHK tool
/// carries a preferred-id list (12, then 1) but deliberately lets a CONTENT SCORE override
/// it: it counts how many items in each inventory look like flasks and takes the winner.
/// The ids move between patches; "the inventory that is full of flasks" does not. Same
/// principle as the camera-matrix hunt - identify by what something IS, not by where it
/// was last time.
///
/// Charges then come from each flask item's Charges component, which points at a shared
/// per-base-type descriptor holding the per-use cost.
/// </remarks>
public sealed class FlaskBeltReader
{
    /// <summary>Ids to try first; the content score can still overrule them.</summary>
    private static readonly int[] PreferredInventoryIds = [12, 1];

    private const int MaxInventories = 128;
    private const int MaxItemsScored = 24;

    private readonly IMemoryReader _reader;
    private readonly EntityReader _entities;
    private readonly int _playerServerData;
    private readonly int _playerInventories;
    private readonly int _entrySize;
    private readonly int _inventoryId;
    private readonly int _inventoryPtr;
    private readonly int _itemList;
    private readonly int _itemListLast;
    private readonly int _itemPtr;
    private readonly int _slotStart;
    private readonly int _chargesInternalPtr;
    private readonly int _chargesCurrent;
    private readonly int _perUseCharges;

    // The inventory scan is expensive - up to 128 inventories, each costing a full entity
    // read per item - while the answer almost never changes. Charges change constantly, the
    // belt's LOCATION does not, so the search is remembered and only the contents re-read.
    // Without this the scan ran at the reader's full rate and dominated every tick.
    private ulong _cachedInventory;

    public FlaskBeltReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _entities = new EntityReader(reader, schema);

        _playerServerData = schema.Structs["ServerDataOffsets"].OffsetOf("PlayerServerData");
        _playerInventories = schema.Structs["ServerDataStructure"].OffsetOf("PlayerInventories");

        StructDef array = schema.Structs["InventoryArray"];
        _entrySize = (int)array.Constants["EntrySize"];
        _inventoryId = array.OffsetOf("InventoryId");
        _inventoryPtr = array.OffsetOf("InventoryPtr0");

        StructDef inventory = schema.Structs["Inventory"];
        _itemList = inventory.OffsetOf("ItemList");
        _itemListLast = inventory.OffsetOf("ItemListLast");

        StructDef item = schema.Structs["InventoryItem"];
        _itemPtr = item.OffsetOf("Item");
        _slotStart = item.OffsetOf("SlotStart");

        StructDef charges = schema.Structs["ChargesComponent"];
        _chargesInternalPtr = charges.OffsetOf("ChargesInternalPtr");
        _chargesCurrent = charges.OffsetOf("Current");
        _perUseCharges = schema.Structs["ChargesInternal"].OffsetOf("PerUseCharges");
    }

    /// <summary>Reads the flask belt from the ServerData address.</summary>
    /// <remarks>
    /// The located inventory is cached and revalidated cheaply on each call; the full search
    /// only re-runs when that check fails, which is what makes this affordable at the
    /// reader's rate.
    /// </remarks>
    public FlaskBelt Read(ulong serverData)
    {
        if (_cachedInventory != 0 && HasItems(_cachedInventory))
        {
            FlaskBelt cached = ReadBelt(_cachedInventory);
            if (!cached.IsUnknown)
            {
                return cached;
            }
        }

        _cachedInventory = FindFlaskInventory(ResolveServerDataStructure(serverData));
        return _cachedInventory == 0 ? FlaskBelt.Empty : ReadBelt(_cachedInventory);
    }

    /// <summary>Cheap liveness check on a remembered inventory: does it still hold items?</summary>
    private bool HasItems(ulong inventory)
    {
        ulong first = _reader.ReadPointer(inventory + (ulong)_itemList);
        ulong last = _reader.ReadPointer(inventory + (ulong)_itemListLast);
        return MemoryReaderExtensions.IsPlausiblePointer(first) && last > first;
    }

    /// <summary>
    /// Follows ServerDataPtr to the struct that actually holds the inventories.
    /// </summary>
    /// <remarks>
    /// There are TWO server-data structs, and skipping the hop between them is a silent
    /// failure rather than a loud one: PlayerInventories read off the outer struct is just
    /// zero, which is indistinguishable from a drifted offset. The outer struct holds a
    /// vector of pointers to the inner one; the first entry is the local player's.
    ///
    /// The direct interpretation is kept as a fallback because a build that lays these out
    /// flat would otherwise stop working for no reason - whichever base yields a usable
    /// inventory vector wins.
    /// </remarks>
    public ulong ResolveServerDataStructure(ulong serverData)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(serverData))
        {
            return 0;
        }

        if (HasInventoryVector(serverData))
        {
            return serverData;
        }

        ulong vector = _reader.ReadPointer(serverData + (ulong)_playerServerData);
        if (!MemoryReaderExtensions.IsPlausiblePointer(vector))
        {
            return serverData;
        }

        ulong inner = _reader.ReadPointer(vector);
        return HasInventoryVector(inner) ? inner : serverData;
    }

    /// <summary>True when this base holds a usable inventory vector - the deciding test.</summary>
    private bool HasInventoryVector(ulong candidate)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(candidate))
        {
            return false;
        }

        ulong first = _reader.ReadPointer(candidate + (ulong)_playerInventories);
        ulong last = _reader.ReadPointer(candidate + (ulong)_playerInventories + 8);
        return MemoryReaderExtensions.IsPlausiblePointer(first)
               && last > first
               && (last - first) % (ulong)_entrySize == 0
               && last - first <= 0x4000;
    }

    /// <summary>
    /// Picks the inventory that actually holds flasks.
    /// </summary>
    /// <remarks>
    /// Content first, id second: the best-scoring inventory wins outright, and the id list
    /// only decides when nothing scored - which is what keeps this working across a patch
    /// that renumbers inventories.
    /// </remarks>
    private ulong FindFlaskInventory(ulong serverData)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(serverData))
        {
            return 0;
        }

        ulong first = _reader.ReadPointer(serverData + (ulong)_playerInventories);
        ulong last = _reader.ReadPointer(serverData + (ulong)_playerInventories + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return 0;
        }

        long count = Math.Min((long)(last - first) / _entrySize, MaxInventories);
        ulong byId = 0;
        ulong byContent = 0;
        int bestScore = 0;

        for (long i = 0; i < count; i++)
        {
            ulong entry = first + (ulong)(i * _entrySize);
            int id = _reader.Read<int>(entry + (ulong)_inventoryId);
            ulong inventory = _reader.ReadPointer(entry + (ulong)_inventoryPtr);
            if (!MemoryReaderExtensions.IsPlausiblePointer(inventory))
            {
                continue;
            }

            int score = ScoreFlaskLikelihood(inventory);
            if (score > bestScore)
            {
                bestScore = score;
                byContent = inventory;
            }

            if (byId == 0 && Array.IndexOf(PreferredInventoryIds, id) >= 0)
            {
                byId = inventory;
            }
        }

        return byContent != 0 ? byContent : byId;
    }

    /// <summary>Counts how many of an inventory's items look like flasks.</summary>
    private int ScoreFlaskLikelihood(ulong inventory)
    {
        int score = 0;
        foreach ((ulong _, ulong itemEntity) in EnumerateItems(inventory, MaxItemsScored))
        {
            if (IsFlask(PathOf(itemEntity)))
            {
                score++;
            }
        }

        return score;
    }

    private FlaskBelt ReadBelt(ulong inventory)
    {
        // DEDUPE BY ITEM ENTITY. The list holds one entry per occupied GRID CELL, so an item
        // spanning two cells appears twice - which showed up live as five "flasks" for three
        // items, with slots 1 and 2 listed twice each. The entity pointer is the identity
        // that survives that.
        var flasks = new List<EquippedFlask>(5);
        var seen = new HashSet<ulong>();

        foreach ((ulong itemStruct, ulong itemEntity) in EnumerateItems(inventory, MaxItemsScored))
        {
            if (!seen.Add(itemEntity))
            {
                continue;
            }

            string path = PathOf(itemEntity);
            if (!IsFlask(path))
            {
                continue;
            }

            // Belt slots are the item's grid column, so slot 1 is column 0.
            int slot = _reader.Read<int>(itemStruct + (ulong)_slotStart) + 1;
            if (slot is < 1 or > 5)
            {
                continue;
            }

            (int charges, int perUse) = ReadCharges(itemEntity);
            flasks.Add(new EquippedFlask(slot, path, charges, perUse));
        }

        flasks.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return new FlaskBelt(flasks);
    }

    /// <summary>Reads current charges and the per-use cost from an item's Charges component.</summary>
    private (int Charges, int PerUse) ReadCharges(ulong itemEntity)
    {
        ulong component = _entities.Read(itemEntity)?.Component("Charges") ?? 0;
        if (component == 0)
        {
            return (0, 0);
        }

        int current = _reader.Read<int>(component + (ulong)_chargesCurrent);
        ulong internals = _reader.ReadPointer(component + (ulong)_chargesInternalPtr);
        int perUse = MemoryReaderExtensions.IsPlausiblePointer(internals)
            ? _reader.Read<int>(internals + (ulong)_perUseCharges)
            : 0;

        return (current, perUse);
    }

    /// <summary>Walks an inventory's item list, yielding (item struct, item entity) pairs.</summary>
    private IEnumerable<(ulong ItemStruct, ulong ItemEntity)> EnumerateItems(ulong inventory, int max)
    {
        ulong first = _reader.ReadPointer(inventory + (ulong)_itemList);
        ulong last = _reader.ReadPointer(inventory + (ulong)_itemListLast);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            yield break;
        }

        long count = Math.Min((long)(last - first) / 8, max);
        for (long i = 0; i < count; i++)
        {
            ulong itemStruct = _reader.ReadPointer(first + (ulong)(i * 8));
            if (!MemoryReaderExtensions.IsPlausiblePointer(itemStruct))
            {
                continue;
            }

            ulong itemEntity = _reader.ReadPointer(itemStruct + (ulong)_itemPtr);
            if (MemoryReaderExtensions.IsPlausiblePointer(itemEntity))
            {
                yield return (itemStruct, itemEntity);
            }
        }
    }

    private string PathOf(ulong itemEntity) => _entities.Read(itemEntity)?.Path ?? string.Empty;

    /// <summary>
    /// True for anything living in the flask belt, charms included.
    /// </summary>
    /// <remarks>
    /// Deliberately broad: charms share the Flasks/ path and the same inventory, and knowing
    /// their charges is useful. Whether something is PRESSABLE is a separate question, and
    /// EquippedFlask.IsCharm answers it.
    /// </remarks>
    public static bool IsFlask(string path)
        => path.Contains("/Flask", StringComparison.OrdinalIgnoreCase);
}
