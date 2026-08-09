using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Items;

/// <summary>What an inventory IS, as far as anybody looking at a list of them cares.</summary>
public enum InventoryKind
{
    /// <summary>The backpack.</summary>
    Backpack,

    /// <summary>Something worn - a weapon, a ring, the belt.</summary>
    Equipped,

    /// <summary>A stash tab.</summary>
    Stash,
}

/// <summary>One item, where it sits and what it is.</summary>
/// <param name="Entity">The item entity, for anything that wants to read more off it.</param>
/// <param name="Left">Its left-hand cell in the grid.</param>
/// <param name="Top">And its top one.</param>
/// <param name="Width">How many cells across it is. 1 for most things, 2 for a two-handed weapon.</param>
/// <param name="Height">And down.</param>
public sealed record StashedItem(ulong Entity, int Left, int Top, int Width, int Height);

/// <summary>One inventory and everything in it.</summary>
/// <param name="Id">The game's own id for it. 1 is the backpack; 2-12 are worn; the rest are tabs.</param>
/// <param name="Kind">What it is, worked out from that id.</param>
/// <param name="Columns">How wide the grid is.</param>
/// <param name="Rows">And how deep.</param>
/// <param name="Items">What is in it.</param>
public sealed record StashInventory(
    int Id,
    InventoryKind Kind,
    ulong Address,
    int Columns,
    int Rows,
    IReadOnlyList<StashedItem> Items)
{
    /// <summary>A name to show when the tab's own is not known.</summary>
    public string Called => Kind switch
    {
        InventoryKind.Backpack => "Backpack",
        InventoryKind.Equipped => $"Equipped ({Id})",
        _ => $"Stash tab {Id}",
    };
}

/// <summary>
/// Reads every inventory the player has, stash tabs included.
/// </summary>
/// <remarks>
/// EVERY STASH TAB IS AN INVENTORY, in the same vector as the backpack and the worn gear - the
/// tabs are not a separate structure to be found. That is the whole reason this is possible at
/// all, and it is not obvious from the game: a tab looks like a window, and in memory it is a
/// grid with an id like any other.
///
/// Built on the walk <see cref="Components.FlaskBeltReader"/> already proved, including the trap
/// it documents: there are TWO server-data structs and reading the inventory vector off the
/// outer one silently yields nothing, which looks exactly like a drifted offset.
///
/// A TAB HOLDS NOTHING UNTIL IT HAS BEEN OPENED IN GAME. Confirmed by the owner, 2026-08: a tab
/// the player has never opened this session shows no items here, and starts showing them - with
/// their stats - the moment it is opened once. The server sends a tab's contents when the client
/// asks for them, and nothing here can make it ask.
///
/// So an empty tab and an unopened one look IDENTICAL from this side, and a count is "what has
/// been opened", never "what the account owns". Anything that reports a total has to say so, or
/// it is quietly wrong for every player who has not just clicked through their whole stash.
/// </remarks>
public sealed class StashReader
{
    /// <summary>Most inventories walked. A guard on a count that comes from memory.</summary>
    public const int MostInventories = 256;

    /// <summary>Most items taken from one - a quad tab holds 144 cells.</summary>
    public const int MostItems = 1_024;

    /// <summary>The largest grid worth believing. Beyond this it is not an inventory.</summary>
    public const int LargestGrid = 64;

    private readonly IMemoryReader _reader;

    private readonly int _playerServerData;
    private readonly int _inventories;
    private readonly int _league;
    private readonly int _entrySize;
    private readonly int _inventoryId;
    private readonly int _inventoryPtr;

    private readonly int _columns;
    private readonly int _rows;
    private readonly int _itemList;
    private readonly int _itemListLast;

    private readonly int _itemPtr;
    private readonly int _slotLeft;
    private readonly int _slotTop;
    private readonly int _slotRight;
    private readonly int _slotBottom;

    public StashReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        _playerServerData = schema.Structs["ServerDataOffsets"].OffsetOf("PlayerServerData");
        _inventories = schema.Structs["ServerDataStructure"].OffsetOf("PlayerInventories");
        _league = schema.Structs["ServerDataStructure"].OffsetOf("League");

        StructDef array = schema.Structs["InventoryArray"];
        _entrySize = (int)array.Constants["EntrySize"];
        _inventoryId = array.OffsetOf("InventoryId");
        _inventoryPtr = array.OffsetOf("InventoryPtr0");

        StructDef inventory = schema.Structs["Inventory"];
        _columns = inventory.OffsetOf("TotalBoxes");
        _rows = inventory.OffsetOf("TotalBoxesY");
        _itemList = inventory.OffsetOf("ItemList");
        _itemListLast = inventory.OffsetOf("ItemListLast");

        StructDef item = schema.Structs["InventoryItem"];
        _itemPtr = item.OffsetOf("Item");
        _slotLeft = item.OffsetOf("SlotStart");
        _slotTop = item.OffsetOf("SlotStartY");
        _slotRight = item.OffsetOf("SlotEnd");
        _slotBottom = item.OffsetOf("SlotEndY");
    }

    /// <summary>
    /// What an inventory id means.
    /// </summary>
    /// <remarks>
    /// The player's own inventories occupy a fixed low range - 1 for the backpack, 2 to 12 for
    /// the worn gear - and everything else is a stash tab. Written as a range rather than a
    /// list of known tab ids because tab ids are not knowable: they depend on how many tabs
    /// somebody has bought.
    /// </remarks>
    public static InventoryKind KindOf(int id) => id switch
    {
        1 => InventoryKind.Backpack,
        >= 2 and <= 12 => InventoryKind.Equipped,
        _ => InventoryKind.Stash,
    };

    /// <summary>
    /// Reads every inventory, in the order the game holds them.
    /// </summary>
    /// <remarks>
    /// Order matters and is kept: the tab NAMES live in a separate list that can only be
    /// matched to these by position, so re-ordering here would silently mislabel every tab.
    /// </remarks>
    public IReadOnlyList<StashInventory> Read(ulong serverData)
    {
        var found = new List<StashInventory>();

        ulong holder = Resolve(serverData);
        if (holder == 0)
        {
            return found;
        }

        ulong first = _reader.ReadPointer(holder + (ulong)_inventories);
        ulong last = _reader.ReadPointer(holder + (ulong)_inventories + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return found;
        }

        long count = Math.Min((long)(last - first) / _entrySize, MostInventories);
        for (long i = 0; i < count; i++)
        {
            ulong entry = first + (ulong)(i * _entrySize);
            int id = _reader.Read<int>(entry + (ulong)_inventoryId);
            ulong inventory = _reader.ReadPointer(entry + (ulong)_inventoryPtr);

            if (One(id, inventory) is { } read)
            {
                found.Add(read);
            }
        }

        return found;
    }

    /// <summary>One inventory, or null when the pointer is not one.</summary>
    /// <remarks>
    /// The grid dimensions are the test. An inventory always has one, and a pointer that is
    /// something else almost never reads as a plausible grid - which is what stops a stale
    /// entry being walked as if it held items.
    /// </remarks>
    private StashInventory? One(int id, ulong inventory)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(inventory))
        {
            return null;
        }

        int columns = _reader.Read<int>(inventory + (ulong)_columns);
        int rows = _reader.Read<int>(inventory + (ulong)_rows);
        if (columns is < 1 or > LargestGrid || rows is < 1 or > LargestGrid)
        {
            return null;
        }

        return new StashInventory(id, KindOf(id), inventory, columns, rows, Items(inventory));
    }

    /// <summary>Everything in one inventory.</summary>
    /// <remarks>
    /// DEDUPLICATED BY ITEM, because the list is one entry per occupied CELL: a two-by-three
    /// piece of armour appears six times, and counting the entries would report six items in a
    /// tab holding one. The first entry for an item carries its whole rectangle, so the extra
    /// ones say nothing the first did not.
    /// </remarks>
    private List<StashedItem> Items(ulong inventory)
    {
        var items = new List<StashedItem>();

        ulong first = _reader.ReadPointer(inventory + (ulong)_itemList);
        ulong last = _reader.ReadPointer(inventory + (ulong)_itemListLast);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return items;
        }

        ulong bytes = last - first;
        if (bytes % 8 != 0)
        {
            return items;
        }

        long count = Math.Min((long)(bytes / 8), MostItems);
        var seen = new HashSet<ulong>();

        for (long i = 0; i < count; i++)
        {
            ulong slot = _reader.ReadPointer(first + (ulong)(i * 8));
            if (!MemoryReaderExtensions.IsPlausiblePointer(slot))
            {
                continue;
            }

            ulong entity = _reader.ReadPointer(slot + (ulong)_itemPtr);
            if (!MemoryReaderExtensions.IsPlausiblePointer(entity) || !seen.Add(entity))
            {
                continue;
            }

            int left = _reader.Read<int>(slot + (ulong)_slotLeft);
            int top = _reader.Read<int>(slot + (ulong)_slotTop);
            int right = _reader.Read<int>(slot + (ulong)_slotRight);
            int bottom = _reader.Read<int>(slot + (ulong)_slotBottom);

            items.Add(new StashedItem(
                entity,
                left,
                top,
                Math.Max(1, right - left),
                Math.Max(1, bottom - top)));
        }

        return items;
    }

    /// <summary>
    /// Which league this character is in, or empty when it cannot be read.
    /// </summary>
    /// <remarks>
    /// HERE because it sits on the same struct the inventories do, reached by the same two-hop
    /// resolve below - and that hop is fiddly enough that a second copy of it would be a second
    /// thing to get wrong. It is not an item, but it is server data, and this is the reader for
    /// that.
    ///
    /// WHAT IT IS FOR: prices. The value is exactly what poe.ninja spells - "Standard",
    /// "HC Runes of Aldur" - so it can be asked for as it comes, with the hardcore prefix
    /// already telling the two economies apart. Typed in by hand instead, it goes stale at
    /// every league start, silently, and stale prices look exactly like real ones.
    /// </remarks>
    public string League(ulong serverData)
    {
        ulong holder = Resolve(serverData);
        if (holder == 0)
        {
            return string.Empty;
        }

        // Read SHORT: a league name is a few words. A long read wanders into whatever follows
        // the string once the offset has drifted, and comes back as something that looks like a
        // league nobody has heard of rather than as nothing.
        string named = _reader.ReadStdWString(holder + (ulong)_league, 64).Trim();
        return named.Length is > 0 and <= 64 && !named.Any(char.IsControl) ? named : string.Empty;
    }

    /// <summary>
    /// Follows ServerData to the struct that actually holds the inventories.
    /// </summary>
    /// <remarks>
    /// The same two-struct hop the flask reader documents, and worth repeating because the
    /// failure is silent: the inventory vector read off the OUTER struct is simply zero, which
    /// is indistinguishable from an offset that has drifted. Whichever base yields a usable
    /// vector wins, so a build that laid these out flat would still work.
    /// </remarks>
    public ulong Resolve(ulong serverData)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(serverData))
        {
            return 0;
        }

        if (HasVector(serverData))
        {
            return serverData;
        }

        ulong vector = _reader.ReadPointer(serverData + (ulong)_playerServerData);
        if (!MemoryReaderExtensions.IsPlausiblePointer(vector))
        {
            return 0;
        }

        ulong inner = _reader.ReadPointer(vector);
        return HasVector(inner) ? inner : 0;
    }

    /// <summary>Whether this base holds a usable inventory vector - the deciding test.</summary>
    private bool HasVector(ulong candidate)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(candidate))
        {
            return false;
        }

        ulong first = _reader.ReadPointer(candidate + (ulong)_inventories);
        ulong last = _reader.ReadPointer(candidate + (ulong)_inventories + 8);

        return MemoryReaderExtensions.IsPlausiblePointer(first)
               && last > first
               && (last - first) % (ulong)_entrySize == 0
               && last - first <= (ulong)_entrySize * MostInventories;
    }
}
