using System.Globalization;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Items;

namespace PoEformance.Game.Diagnostics;

/// <summary>One inventory, whole, on one frame.</summary>
/// <param name="Id">The game's own id. Bit 31 marks a shop page - see StashInventory.Called.</param>
/// <param name="Entry">Where its slot in the inventory array is.</param>
/// <param name="EntryBytes">That whole 0x18 slot, including the two words nothing reads.</param>
/// <param name="Address">The Inventory the slot points at.</param>
/// <param name="Bytes">Its head, as far as the read got.</param>
/// <param name="Columns">Its grid, so an entry can be recognised without its id.</param>
/// <param name="Items">How many items were in it - 0 for a tab the game has not loaded.</param>
public sealed record InventoryObservation(
    int Id,
    ulong Entry,
    byte[] EntryBytes,
    ulong Address,
    byte[] Bytes,
    int Columns,
    int Rows,
    int Items);

/// <summary>What one frame of the sweep saw.</summary>
public sealed record InventorySweepFrame(int Frame, IReadOnlyList<InventoryObservation> Seen);

/// <summary>
/// Reads every inventory whole, to find what says which SORT of tab it is.
/// </summary>
/// <remarks>
/// THE QUESTION IT EXISTS FOR: a stash tab's own name is the player's and lives in the UI, but
/// its TYPE is the game's - a BreachStash only takes breach items - and a type would survive
/// renaming, would name the specialised tabs without touching the UI tree at all, and is a thing
/// the client must already know, since it enforces what may be put where. Nothing in this tool
/// reads it, and <c>StashReader.KindOf</c> guesses from the id range instead.
///
/// WHY A SWEEP AND NOT A LOOKUP. The id is not it: the ids on a live account run in consecutive
/// stretches across tabs of obviously different sorts, which is what a counter looks like and
/// not what a type field does. The one bit that IS a mark - bit 31, the shop pages - was found
/// by somebody noticing an absurd number in a list, not by reading a struct. So there is no
/// candidate offset to verify, and this is a blind read of the whole head, decoded afterwards
/// against the file.
///
/// WHAT MAKES IT DECIDABLE is that we now have a CONTROL GROUP the game handed over: the two
/// shop pages are an inventory sort we know for certain, confirmed against the Merchant window.
/// So a candidate is not "a plausible small number" - it is a field where the two shop pages
/// AGREE, at least one ordinary tab DIFFERS, and every value fits the 25 rows of StashType. A
/// structural fingerprint that weak has fooled this project before; three conditions the game
/// itself settles have not.
///
/// A RECORDING CAN ONLY HOLD READS THAT WERE PERFORMED, which is the whole reason this exists as
/// its own pass rather than as analysis of what the stash reader already captures. That reader
/// takes an id, a pointer, a grid and an item list, and nothing else - so every byte the question
/// needs is missing from every recording in the repo.
/// </remarks>
public sealed class InventorySweep
{
    /// <summary>
    /// How much of each Inventory to take.
    /// </summary>
    /// <remarks>
    /// Past the last field anybody has decoded - ServerRequestCounter at 0x1E8 - and no further.
    /// The cost is what decides it: a stash of 140 inventories is 76 KB a frame at this size, and
    /// a sweep somebody leaves running while they open tabs takes a frame every couple of
    /// seconds. Doubling the window doubles a recording that has to be uploaded.
    /// </remarks>
    public const int Window = 0x220;

    /// <summary>One slot of the inventory array, whole.</summary>
    public const int EntryWindow = 0x18;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly StashReader _stash;

    private readonly int _playerInfo;
    private readonly int _serverDataPtr;
    private readonly int _inventories;
    private readonly int _entrySize;
    private readonly int _inventoryId;
    private readonly int _inventoryPtr;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _itemList;
    private readonly int _itemListLast;

    private readonly byte[] _buffer = new byte[Window];
    private readonly byte[] _entry = new byte[EntryWindow];

    public InventorySweep(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _stash = new StashReader(reader, schema);

        _playerInfo = schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        _serverDataPtr = schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
        _inventories = schema.Structs["ServerDataStructure"].OffsetOf("PlayerInventories");

        StructDef array = schema.Structs["InventoryArray"];
        _entrySize = (int)array.Constants["EntrySize"];
        _inventoryId = array.OffsetOf("InventoryId");
        _inventoryPtr = array.OffsetOf("InventoryPtr0");

        StructDef inventory = schema.Structs["Inventory"];
        _columns = inventory.OffsetOf("TotalBoxes");
        _rows = inventory.OffsetOf("TotalBoxesY");
        _itemList = inventory.OffsetOf("ItemList");
        _itemListLast = inventory.OffsetOf("ItemListLast");
    }

    /// <summary>Every inventory this frame, or null when the chain did not resolve.</summary>
    public InventorySweepFrame? SampleFrame(ulong gameStatesStatic, int frame)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (chain.AreaInstance == 0)
        {
            return null;
        }

        // The one hop that is easy to get wrong, and the schema says so: PlayerInfo is an INLINE
        // LocalPlayerStruct, so the value AT that address is already ServerDataPtr.
        ulong serverData = _reader.ReadPointer(
            chain.AreaInstance + (ulong)_playerInfo + (ulong)_serverDataPtr);

        // And the second: ServerDataPtr does not hold the inventories - the struct that does is
        // reached through the vector inside it. Resolve knows both shapes.
        ulong holder = _stash.Resolve(serverData);
        if (holder == 0)
        {
            return null;
        }

        ulong first = _reader.ReadPointer(holder + (ulong)_inventories);
        ulong last = _reader.ReadPointer(holder + (ulong)_inventories + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return null;
        }

        long count = Math.Min((long)(last - first) / _entrySize, StashReader.MostInventories);
        var seen = new List<InventoryObservation>();

        for (long i = 0; i < count; i++)
        {
            ulong entry = first + (ulong)(i * _entrySize);
            if (!_reader.TryRead(entry, _entry.AsSpan()))
            {
                continue;
            }

            int id = _reader.Read<int>(entry + (ulong)_inventoryId);
            ulong at = _reader.ReadPointer(entry + (ulong)_inventoryPtr);
            if (!MemoryReaderExtensions.IsPlausiblePointer(at))
            {
                continue;
            }

            // The grid is the test the stash reader already trusts: an inventory always has one,
            // and a pointer that is something else almost never reads as a plausible grid.
            int columns = _reader.Read<int>(at + (ulong)_columns);
            int rows = _reader.Read<int>(at + (ulong)_rows);
            if (columns is < 1 or > StashReader.LargestGrid || rows is < 1 or > StashReader.LargestGrid)
            {
                continue;
            }

            if (!_reader.TryRead(at, _buffer.AsSpan()))
            {
                continue;
            }

            ulong items = _reader.ReadPointer(at + (ulong)_itemList);
            ulong itemsEnd = _reader.ReadPointer(at + (ulong)_itemListLast);
            int held = MemoryReaderExtensions.IsPlausiblePointer(items) && itemsEnd > items
                ? (int)Math.Min((long)(itemsEnd - items) / 8, StashReader.MostItems)
                : 0;

            seen.Add(new InventoryObservation(
                id, entry, _entry[..], at, _buffer[..], columns, rows, held));
        }

        return new InventorySweepFrame(frame, seen);
    }

    /// <summary>Whether an id is one of the Merchant's shop pages - the control group.</summary>
    /// <remarks>
    /// Bit 31, confirmed against the game 2026-09: the Shop section holds exactly two pages and
    /// 0x80000001 draws the second one's items. It is the only inventory sort this project can
    /// currently name with certainty, which is what makes it the thing to test candidates against.
    /// </remarks>
    public static bool IsShop(int id) => id < 0;

    /// <summary>Says what the session holds, and runs the check the game can settle.</summary>
    public static void Report(IReadOnlyList<InventorySweepFrame> frames, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(output);

        // THE RICHEST FRAME, not the last. Tabs arrive as the player opens them, so the frame
        // with the most inventories in it is the one where the most had been loaded - and a
        // sweep stopped a moment after opening the last tab would otherwise report the frame
        // before the interesting one.
        InventorySweepFrame? best = null;
        foreach (InventorySweepFrame frame in frames)
        {
            if (best is null || frame.Seen.Count > best.Seen.Count)
            {
                best = frame;
            }
        }

        if (best is null || best.Seen.Count == 0)
        {
            output.WriteLine("inventory sweep: nothing read - was the game in an area?");
            return;
        }

        IReadOnlyList<InventoryObservation> seen = best.Seen;
        int shops = seen.Count(one => IsShop(one.Id));

        output.WriteLine();
        output.WriteLine(
            $"inventory sweep - {frames.Count} frames, richest holds {seen.Count} inventories "
            + $"({shops} shop pages, {seen.Count(one => one.Items > 0)} with items).");

        if (shops < 2)
        {
            // Said out loud, because the whole check below rests on it and its absence looks
            // exactly like "no candidate survived".
            output.WriteLine(
                "  NO CONTROL GROUP: fewer than two shop pages were read, so nothing here can be");
            output.WriteLine(
                "  tested against a sort we know. Open the Merchant window once and sweep again.");
        }

        DrawCandidates("array slot", seen, one => one.EntryBytes, EntryWindow, output);
        DrawCandidates("inventory head", seen, one => one.Bytes, Window, output);
        DrawInventories(seen, output);
    }

    /// <summary>
    /// Every 4-byte field that could be a type, and whether the game agrees it is one.
    /// </summary>
    /// <remarks>
    /// THREE CONDITIONS, ALL OF WHICH THE GAME SETTLES, because a structural fingerprint alone
    /// has fooled this project before - a plausible pointer and a unit-length row look like
    /// anything. A type field must: take FEW values, since StashType has 25 rows; be the SAME on
    /// both shop pages, which are the same sort by construction; and DIFFER on at least one
    /// ordinary tab, or it is saying nothing about sort at all.
    ///
    /// Everything that survives is printed with its values, because the last step is somebody
    /// looking at whether "the currency tab and the essence tab differ" matches the tabs they own.
    /// </remarks>
    private static void DrawCandidates(
        string what,
        IReadOnlyList<InventoryObservation> seen,
        Func<InventoryObservation, byte[]> bytesOf,
        int window,
        TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"  {what} - fields that could carry a sort:");

        var found = 0;

        for (int offset = 0; offset + 4 <= window; offset += 4)
        {
            var values = new Dictionary<uint, int>();
            uint? shop = null;
            var shopsAgree = true;
            var readable = 0;

            foreach (InventoryObservation one in seen)
            {
                byte[] bytes = bytesOf(one);
                if (bytes.Length < offset + 4)
                {
                    continue;
                }

                uint value = BitConverter.ToUInt32(bytes, offset);
                readable++;
                values[value] = values.GetValueOrDefault(value) + 1;

                if (!IsShop(one.Id))
                {
                    continue;
                }

                if (shop is { } already && already != value)
                {
                    shopsAgree = false;
                }

                shop ??= value;
            }

            // A field every inventory agrees on says nothing about sort; one with a value per
            // inventory is an id, an address or a count. StashType has 25 rows, so a type takes
            // at most that many - and at least two, or the first rule would have caught it.
            if (readable == 0 || values.Count < 2 || values.Count > 25)
            {
                continue;
            }

            if (!shopsAgree || shop is not { } theirs)
            {
                continue;
            }

            // And it has to SAY something about the shop pages: a field they share with every
            // other tab is not telling them apart from anything.
            if (values[theirs] == readable)
            {
                continue;
            }

            found++;
            string spread = string.Join(
                ", ",
                values.OrderByDescending(pair => pair.Value)
                    .Take(6)
                    .Select(pair => $"0x{pair.Key:X}x{pair.Value}"));

            output.WriteLine(
                $"    +0x{offset:X3}  {values.Count} values, shops both 0x{theirs:X}  -  {spread}"
                + (values.Count > 6 ? " ..." : string.Empty));
        }

        if (found == 0)
        {
            output.WriteLine("    none survived - no field here is both few-valued and shop-specific.");
        }
    }

    /// <summary>The inventories themselves, so a candidate can be checked against known tabs.</summary>
    private static void DrawInventories(IReadOnlyList<InventoryObservation> seen, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  what was read (loaded ones first - an unopened tab holds nothing here):");

        foreach (InventoryObservation one in seen.OrderByDescending(one => one.Items).Take(40))
        {
            output.WriteLine(
                $"    id {one.Id.ToString(CultureInfo.InvariantCulture),12}  "
                + $"{one.Columns,2}x{one.Rows,-2}  {one.Items,4} items  "
                + $"slot +0x04={BitConverter.ToUInt32(one.EntryBytes, 4):X8}  "
                + $"0x{one.Address:X}"
                + (IsShop(one.Id) ? "   <- shop page" : string.Empty));
        }

        int rest = seen.Count - Math.Min(40, seen.Count);
        if (rest > 0)
        {
            output.WriteLine($"    ...and {rest} more, all holding nothing.");
        }
    }
}
