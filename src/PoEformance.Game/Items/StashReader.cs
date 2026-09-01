using PoEformance.Core.Diagnostics;
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
/// <param name="Loaded">
/// Whether the game has ALLOCATED this inventory's item list at all. False means the tab was
/// listed but never filled in - which is not the same as a tab holding nothing, and reading it
/// as such is what let unvisited tabs erase themselves from a total.
/// </param>
public sealed record StashInventory(
    int Id,
    InventoryKind Kind,
    ulong Address,
    int Columns,
    int Rows,
    IReadOnlyList<StashedItem> Items,
    bool Loaded = true)
{
    /// <summary>
    /// A name to show when the tab's own is not known.
    /// </summary>
    /// <remarks>
    /// THE TOP BIT OF AN ID IS A MARK, NOT PART OF A NUMBER. Two entries in the list carry ids
    /// 0x80000000 and 0x80000001, which read as a signed i32 are -2147483648 and -2147483647 -
    /// and were shown as "Stash tab -2147483648", a number that says nothing and looks like a
    /// read gone wrong. Bit 31 marks the category; the low bits are its own index, 0 and 1.
    ///
    /// THEY ARE THE MERCHANT'S SHOP PAGES, and the game settled it rather than a theory did
    /// (2026-09, owner's screenshot): the Merchant window's Shop section holds exactly two pages,
    /// and selecting id 0x80000001 here shows the same 55 items the game draws in the second one.
    /// They also read as EMPTY until that window has been opened once - the same rule as an
    /// unopened stash tab, which is why they first appeared as two empty tabs with absurd numbers.
    ///
    /// Only the NAME is decided here. <see cref="KindOf"/> still calls them stash tabs, which for
    /// counting is right - items listed for sale are still the player's, and these two hold 12 div
    /// worth - and changing it would change what <c>StashInspector.Count</c> treats as having seen
    /// a stash.
    /// </remarks>
    public string Called => Kind switch
    {
        InventoryKind.Backpack => "Backpack",
        InventoryKind.Equipped => $"Equipped ({Id})",
        _ when Id < 0 => $"Shop tab {(Id & int.MaxValue) + 1}",
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
        _league = schema.Structs["ServerDataOffsets"].OffsetOf("League");

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

        (bool loaded, List<StashedItem> items) = Items(inventory);
        return new StashInventory(id, KindOf(id), inventory, columns, rows, items, loaded);
    }

    /// <summary>Everything in one inventory.</summary>
    /// <remarks>
    /// DEDUPLICATED BY ITEM, because the list is one entry per occupied CELL: a two-by-three
    /// piece of armour appears six times, and counting the entries would report six items in a
    /// tab holding one. The first entry for an item carries its whole rectangle, so the extra
    /// ones say nothing the first did not.
    /// </remarks>
    private (bool Loaded, List<StashedItem> Items) Items(ulong inventory)
    {
        var items = new List<StashedItem>();

        ulong first = _reader.ReadPointer(inventory + (ulong)_itemList);
        ulong last = _reader.ReadPointer(inventory + (ulong)_itemListLast);

        // NEVER ALLOCATED IS NOT EMPTY, and the two used to leave here as the same answer.
        //
        // WHAT THE GAME ACTUALLY DOES, from the player rather than from a guess: it keeps the
        // last state you LOOKED AT. A tab opened at some point stays readable; a tab never
        // opened this session has no list to read. So a player with thirty tabs has as many
        // filled in as they have visited, and the rest answer nothing - which was allowed to
        // stand for what those tabs hold.
        //
        // Two guesses were wrong on the way here and are recorded so nobody spends the time
        // again: that only the ACTIVE tab is held (no), and that a tab can fall back OUT of
        // memory once seen (no). Ctrl-clicking an item into the stash opens the destination tab
        // itself, which is why depositing is the moment a tab first becomes readable.
        //
        // A null list pointer is the tab not being there to read. A valid pointer with
        // last == first is an allocated, empty list: a tab somebody has just emptied, which IS
        // a real zero and must still count as one.
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first)
        {
            return (false, items);
        }

        if (last == first)
        {
            return (true, items);
        }

        ulong bytes = last - first;
        if (bytes % 8 != 0)
        {
            return (false, items);
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

        return (true, items);
    }

    /// <summary>
    /// Which league this character is in, or empty when it cannot be read.
    /// </summary>
    /// <remarks>
    /// NOT ON THE STRUCT THE INVENTORIES ARE ON, and getting that wrong is why this read came
    /// back empty and the whole price feature never started. The two server-data structs are
    /// different things: the OUTER one is what ServerDataPtr points at and is where the league
    /// lives; the inner one is reached through the outer's vector and is where the inventories
    /// live. The AHK tool says so in the shape of its own resolve - the pointer it reads the
    /// league from is the one whose +0x48 is a plausible vector, which is the outer by
    /// definition, while it reads inventories from what that vector points AT.
    ///
    /// Confirmed from the other side in a recording, 2026-08-09: on the inner struct this
    /// offset holds two heap pointers interleaved with two pointers into the module image - a
    /// pair of vtables, not a std::wstring - in the same frames where the inner struct's
    /// inventory vector reads 118 inventories perfectly.
    ///
    /// The resolved struct is still tried afterwards, for the layout where both are the same
    /// one. It costs a read that almost always fails, and it is the difference between working
    /// everywhere and working here.
    ///
    /// WHAT IT IS FOR: prices. The value is exactly what poe.ninja spells - "Standard",
    /// "HC Runes of Aldur" - so it can be asked for as it comes, with the hardcore prefix
    /// already telling the two economies apart. Typed in by hand instead, it goes stale at
    /// every league start, silently, and stale prices look exactly like real ones.
    /// </remarks>
    public string League(ulong serverData)
    {
        if (Named(serverData) is { Length: > 0 } outer)
        {
            return outer;
        }

        ulong holder = Resolve(serverData);
        return holder == 0 || holder == serverData ? string.Empty : Named(holder);
    }

    /// <summary>The league name at one candidate base, or empty when it is not one.</summary>
    /// <remarks>
    /// Read SHORT: a league name is a few words. A long read wanders into whatever follows the
    /// string when the base is wrong, and comes back looking like a league nobody has heard of
    /// rather than as nothing - which is the difference between "no prices" and "prices for
    /// somewhere that does not exist".
    /// </remarks>
    private string Named(ulong at)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return string.Empty;
        }

        string named = _reader.ReadStdWString(at + (ulong)_league, 64).Trim();
        return named.Length is > 0 and <= 64 && !named.Any(char.IsControl) ? named : string.Empty;
    }

    /// <summary>Where in the struct a string turned out to be, and what it says.</summary>
    /// <param name="Offset">Its offset from the struct's base - what a schema entry would hold.</param>
    /// <param name="Text">The characters, cut to something a report can show.</param>
    public readonly record struct FoundText(int Offset, string Text);

    /// <summary>How many strings a sweep will fetch the characters of.</summary>
    public const int MostStrings = 64;

    /// <summary>
    /// How much of the window is read at a time.
    /// </summary>
    /// <remarks>
    /// IN PIECES, NOT IN ONE GO, and this is the part that would have made the sweep useless
    /// exactly when it was needed. A read that spans an unmapped page fails ENTIRELY - it does
    /// not return the part that was there - so one read of the whole window comes back with
    /// nothing whenever the struct's tail is not mapped, and a sweep that finds nothing is
    /// indistinguishable from a struct that holds nothing.
    ///
    /// Each piece is read with a header's worth of overlap, so a string starting near the end
    /// of one is still whole in the bytes it is judged from.
    /// </remarks>
    public const int SweepChunk = 0x200;

    /// <summary>
    /// Every string in a window of the struct - for when the league is not where it was.
    /// </summary>
    /// <remarks>
    /// THE ANSWER TO A DRIFTED STRING CANNOT BE GUESSED, and it cannot be found in a recording
    /// either: a replay only holds the reads the running build actually made, so a field nobody
    /// read is simply absent. This is the read that puts the answer IN the next recording.
    ///
    /// It is a sweep rather than a search for a known name, because the name is what is being
    /// looked for - and because "Standard" or "Rise of the Abyssal" is obvious in a list of the
    /// half-dozen strings a struct holds, while a search for one spelling finds nothing on a
    /// league nobody thought of.
    ///
    /// ONE READ of the whole window and then no memory access at all for the rows that are not
    /// strings - the header is already in the bytes. Only a string kept somewhere else costs a
    /// second read, and at most <see cref="MostStrings"/> of those, so a window full of numbers
    /// that happen to look like lengths cannot turn into thousands of reads.
    /// </remarks>
    public IReadOnlyList<FoundText> StringsIn(ulong at, int from, int to)
    {
        var found = new List<FoundText>();

        // The base is handed in rather than resolved here, because WHICH server-data struct is
        // meant is the caller's question - and it is the question this sweep exists to settle.
        if (!MemoryReaderExtensions.IsPlausiblePointer(at) || to <= from || from < 0)
        {
            return found;
        }

        var window = new byte[SweepChunk + TextProbe.HeaderSize];

        for (int start = from; start < to; start += SweepChunk)
        {
            // The overlap first, so a header near the end of a piece is judged whole. Without
            // it, and short of it, the read is retried at the plain size - which is what the
            // last mapped page of the struct looks like.
            int wanted = Math.Min(window.Length, to + TextProbe.HeaderSize - start);
            Span<byte> piece = window.AsSpan(0, wanted);

            if (!_reader.TryRead(at + (ulong)start, piece))
            {
                piece = window.AsSpan(0, Math.Min(SweepChunk, to - start));
                if (!_reader.TryRead(at + (ulong)start, piece))
                {
                    continue;   // not mapped, which is ordinary at the end of a struct
                }
            }

            int last = Math.Min(SweepChunk, piece.Length - TextProbe.HeaderSize);
            for (var row = 0; row <= last; row += 8)
            {
                TextCandidate candidate = TextProbe.At(piece, row);

                string text = candidate.Shape switch
                {
                    TextShape.Inline => candidate.Text,
                    TextShape.Heap when found.Count < MostStrings =>
                        _reader.ReadUnicodeString(candidate.Address, Math.Min(candidate.Length, 128)),
                    _ => string.Empty,
                };

                if (text.Length > 0 && TextProbe.LooksLikeText(text))
                {
                    found.Add(new FoundText(start + row, text));
                }
            }
        }

        return found;
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
