using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;
using PoEformance.Game.Items;

namespace PoEformance.Game.Components;

/// <summary>One equipped flask and its charge state.</summary>
/// <param name="Slot">Belt slot 1-5.</param>
/// <param name="Path">The item's metadata path - what kind of flask this is.</param>
/// <param name="Charges">Charges currently held.</param>
/// <param name="ChargesPerUse">
/// What one use of THIS flask costs - the base type's cost with the item's own rolls applied,
/// which is the number the game's tooltip prints. Computed, because the game does not store it:
/// see <see cref="FlaskBeltReader"/>.
/// </param>
/// <param name="MaxCharges">How many charges THIS flask holds, on the same footing.</param>
/// <param name="BaseChargesPerUse">
/// What the BASE TYPE costs, before this item's rolls. Kept because it is what memory actually
/// holds, so a diagnostic can show both and a wrong computation is visible rather than silent.
/// </param>
/// <param name="BaseMaxCharges">And the base type's maximum, for the same reason.</param>
/// <param name="Entity">
/// The item entity, for anything that wants to read more off it - the same courtesy
/// <see cref="Items.InspectedItem"/> extends. Zero when it was not recorded.
/// </param>
public readonly record struct EquippedFlask(
    int Slot,
    string Path,
    int Charges,
    int ChargesPerUse,
    ulong Entity = 0,
    int MaxCharges = 0,
    int BaseChargesPerUse = 0,
    int BaseMaxCharges = 0)
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
///
/// THE COST AND THE MAXIMUM ARE COMPUTED, because the game does not store them. ChargesInternal
/// is the base type's descriptor, so a flask that rolled "15% reduced Charges per use" reads its
/// base 10 there while the tooltip says "Consumes 8 of 60 Charges on use". That is not a gap in
/// this reader: the modified numbers are not anywhere on the item.
///
/// HOW THAT WAS ESTABLISHED, since it is a negative and negatives are easy to get wrong. The
/// Charges component is 0x40 bytes - measured, not assumed - and every slot in it is accounted
/// for: vtable, owner entity, ChargesInternal, Current, Regenerating, two zeros and a pointer.
/// FlaskProbe's hunt asked every component of the item, and one level of pointers out of each,
/// for the candidate values and their tenths and hundredths, and found none, WHILE finding a
/// control value it was given - so the negative is the hunt working rather than failing. And a
/// live watch while two flasks refilled moved only Current and Regenerating. Neither GameHelper2
/// nor the AHK tool reads such a field either.
///
/// THE GAME TRUNCATES, which is the part that makes computing safe rather than a guess, and it
/// was settled by a flask that is not this one: a Mana Flask with base maximum 70 and
/// local_max_charges_+% = 27 sits full at 88. 70 x 1.27 = 88.9, so rounding would give 89 and
/// does not. The integer form below is exact for the same reason - 70 * 127 / 100 = 88 with no
/// floating point in it at all.
///
/// WHAT IS STILL CONVENTION RATHER THAN MEASUREMENT: that a flat "+N to Maximum Charges" is
/// added before the percentages rather than after. That is how this game's arithmetic works
/// everywhere else, but no flask in the sample carried one, so it is written down as the
/// assumption it is. Percentages are summed, not compounded - likewise the convention for
/// "increased", and a "more" multiplier would need its own handling if a flask ever grows one.
/// </remarks>
public sealed class FlaskBeltReader
{
    /// <summary>Ids to try first; the content score can still overrule them.</summary>
    private static readonly int[] PreferredInventoryIds = [12, 1];

    private const int MaxInventories = 128;
    private const int MaxItemsScored = 24;

    /// <summary>How far up the stat table the three charge stats are looked for.</summary>
    /// <remarks>
    /// The whole table, because a row number is not predictable - the three wanted here sit at
    /// 20, 381 and 1715 today and a league can move any of them anywhere. Once, at startup.
    /// </remarks>
    private const int StatRowsSearched = 27_000;

    /// <summary>A guard on a stat count read out of memory. An item carries a handful.</summary>
    private const int MostStats = 256;

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
    private readonly int _maxCharges;
    private readonly int _localStats;
    private readonly int _statSize;
    private readonly int _statKey;
    private readonly int _statValue;

    // THE STAT ROWS, RESOLVED ONCE. What memory holds is a row number, and the rows move
    // between leagues - so they are looked up by ID in the shipped table at construction and
    // compared as ints afterwards. Data-driven where it matters, and an integer compare in the
    // per-tick path, which is read at the reader's full rate.
    private readonly int _perUseStat;
    private readonly int _maxPercentStat;
    private readonly int _maxFlatStat;

    // The inventory scan is expensive - up to 128 inventories, each costing a full entity
    // read per item - while the answer almost never changes. Charges change constantly, the
    // belt's LOCATION does not, so the search is remembered and only the contents re-read.
    // Without this the scan ran at the reader's full rate and dominated every tick.
    private ulong _cachedInventory;

    public FlaskBeltReader(IMemoryReader reader, OffsetSchema schema, ItemNames? names = null)
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
        StructDef descriptor = schema.Structs["ChargesInternal"];
        _perUseCharges = descriptor.OffsetOf("PerUseCharges");

        // EVERYTHING BELOW IS OPTIONAL, and that is not defensive habit. The scaling needs
        // fields and a stat table that older schemas do not have, and the frozen one the replay
        // fixtures pin is one of them - requiring them here killed 99 replay tests at
        // construction, which is a reader deciding a recording from before a field existed is
        // unreadable. Without them a flask reports its base numbers, which is what it did
        // before any of this and is wrong in a way that is already documented.
        _maxCharges = descriptor.Field("MaxCharges")?.Offset ?? -1;

        _localStats = schema.Structs.TryGetValue("LocalStatsComponent", out StructDef? local)
            ? local.Field("Stats")?.Offset ?? -1
            : -1;

        if (schema.Structs.TryGetValue("StatPair", out StructDef? stat)
            && stat.Field("Key") is { } key
            && stat.Field("Value") is { } value)
        {
            _statSize = (int)stat.Constants["EntrySize"];
            _statKey = key.Offset;
            _statValue = value.Offset;
        }

        ItemNames table = names ?? ItemNames.Empty;
        if (_localStats >= 0 && _statSize > 0)
        {
            _perUseStat = Row(table, "local_charges_used_+%");
            _maxPercentStat = Row(table, "local_max_charges_+%");
            _maxFlatStat = Row(table, "local_extra_max_charges");
        }
    }

    /// <summary>
    /// The memory key of a stat, found by its id, or 0 when the table cannot say.
    /// </summary>
    /// <remarks>
    /// A LINEAR SCAN, ONCE. The shipped table is keyed by row and this needs the reverse, and
    /// building a whole reverse index to answer three questions at startup would cost more than
    /// it saves. Zero when the table is missing, which disables the computation rather than
    /// applying a wrong row - a flask then reports its base cost, which is what it did before.
    /// </remarks>
    private static int Row(ItemNames table, string id)
    {
        for (int key = 1; key <= StatRowsSearched; key++)
        {
            if (table.Stat(key).Id == id)
            {
                return key;
            }
        }

        return 0;
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

            ChargeState charges = ReadCharges(itemEntity);
            flasks.Add(new EquippedFlask(
                slot,
                path,
                charges.Current,
                charges.PerUse,
                itemEntity,
                charges.Max,
                charges.BasePerUse,
                charges.BaseMax));
        }

        flasks.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return new FlaskBelt(flasks);
    }

    /// <summary>Reads current charges and the per-use cost from an item's Charges component.</summary>
    private ChargeState ReadCharges(ulong itemEntity)
    {
        Entity? item = _entities.Read(itemEntity);
        ulong component = item?.Component("Charges") ?? 0;
        if (component == 0)
        {
            return default;
        }

        int current = _reader.Read<int>(component + (ulong)_chargesCurrent);
        ulong internals = _reader.ReadPointer(component + (ulong)_chargesInternalPtr);
        if (!MemoryReaderExtensions.IsPlausiblePointer(internals))
        {
            return new ChargeState(current, 0, 0, 0, 0);
        }

        int basePerUse = _reader.Read<int>(internals + (ulong)_perUseCharges);
        int baseMax = _maxCharges >= 0 ? _reader.Read<int>(internals + (ulong)_maxCharges) : 0;

        (int perUsePercent, int maxPercent, int maxFlat) = Rolls(item);

        return new ChargeState(
            current,
            Scaled(basePerUse, perUsePercent),
            Scaled(baseMax + maxFlat, maxPercent),
            basePerUse,
            baseMax);
    }

    /// <summary>
    /// A base value with its percentages applied, the way the game does it.
    /// </summary>
    /// <remarks>
    /// INTEGER THROUGHOUT, and that is the whole point rather than a micro-optimisation: the
    /// game truncates, and 70 * 127 / 100 = 88 exactly where 70 * 1.27 in floating point is
    /// 88.899999999999991 and one careless rounding away from 89. See the type's remarks for
    /// how the truncation was established.
    ///
    /// A result of zero or less falls back to the base. Nothing observed produces one, but a
    /// flask that costs nothing to use would make CanUse say yes forever, and the base is the
    /// answer this reader gave before any of this - wrong, but wrong in a way somebody has
    /// already seen.
    /// </remarks>
    private static int Scaled(int baseValue, int percent)
    {
        if (baseValue <= 0)
        {
            return baseValue;
        }

        int scaled = baseValue * (100 + percent) / 100;
        return scaled > 0 ? scaled : baseValue;
    }

    /// <summary>What this item rolled that changes its charges, summed.</summary>
    /// <remarks>
    /// FROM LOCALSTATS rather than through the Mods component: it is the same resolved pairs
    /// the game's own tooltip is built from, one pointer closer, and this runs at the reader's
    /// rate. Empty when the stat table was not supplied, in which case nothing scales and every
    /// flask reports its base numbers.
    /// </remarks>
    private (int PerUsePercent, int MaxPercent, int MaxFlat) Rolls(Entity? item)
    {
        if (_perUseStat == 0 || _localStats < 0 || item?.Component("LocalStats") is not { } stats
            || !MemoryReaderExtensions.IsPlausiblePointer(stats))
        {
            return (0, 0, 0);
        }

        ulong vector = stats + (ulong)_localStats;
        ulong first = _reader.ReadPointer(vector);
        ulong last = _reader.ReadPointer(vector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return (0, 0, 0);
        }

        ulong bytes = last - first;
        if (bytes % (ulong)_statSize != 0 || bytes > (ulong)_statSize * MostStats)
        {
            return (0, 0, 0);
        }

        var perUse = 0;
        var maxPercent = 0;
        var maxFlat = 0;
        for (ulong i = 0; i < bytes / (ulong)_statSize; i++)
        {
            ulong at = first + (i * (ulong)_statSize);
            int key = _reader.Read<int>(at + (ulong)_statKey);

            // Summed rather than taken once: two mods can carry the same stat, and the game
            // adds "increased" together before applying it.
            if (key == _perUseStat)
            {
                perUse += _reader.Read<int>(at + (ulong)_statValue);
            }
            else if (key == _maxPercentStat)
            {
                maxPercent += _reader.Read<int>(at + (ulong)_statValue);
            }
            else if (key == _maxFlatStat)
            {
                maxFlat += _reader.Read<int>(at + (ulong)_statValue);
            }
        }

        return (perUse, maxPercent, maxFlat);
    }

    /// <summary>Everything the Charges component and the item's rolls come to.</summary>
    /// <param name="Current">Charges held right now.</param>
    /// <param name="PerUse">What one use costs THIS flask.</param>
    /// <param name="Max">What THIS flask holds when full.</param>
    /// <param name="BasePerUse">And what the base type says, for both, so a diagnostic can</param>
    /// <param name="BaseMax">show the computation's input beside its output.</param>
    private readonly record struct ChargeState(int Current, int PerUse, int Max, int BasePerUse, int BaseMax);

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
