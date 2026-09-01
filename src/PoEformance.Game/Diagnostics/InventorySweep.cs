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
/// <param name="Cells">
/// How many slots its item list holds - 0 for a tab the game has not loaded.
/// </param>
/// <remarks>
/// CELLS, NOT ITEMS, and the first run of this sweep called them items and was wrong: every
/// number it printed was exactly columns times rows. The list is one slot per CELL, mostly null
/// - see StashReader.Items, which deduplicates by item because a two-by-three piece of armour
/// occupies six of them. Counting the real items would mean reading every slot, which is 4.6 KB
/// per inventory against this whole window's 544, and it is not what the sweep is for. What the
/// number is good for is telling a loaded tab from an unopened one, which it does exactly.
/// </remarks>
/// <param name="Shared">
/// What the slot's SECOND pointer leads to - the bytes before the Inventory, which nothing has
/// ever read. Empty when that pointer is not one. See InventorySweep.SharedWindow.
/// </param>
public sealed record InventoryObservation(
    int Id,
    ulong Entry,
    byte[] EntryBytes,
    ulong Address,
    byte[] Bytes,
    int Columns,
    int Rows,
    int Cells,
    byte[] Shared);

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
    /// WIDENED FROM 0x220 AFTER THE FIRST CAPTURE ANSWERED WITH IT. A byte-by-byte scan of that
    /// window across 181 inventories, at every width and every alignment, produced exactly one
    /// kind of hit: TotalBoxes and TotalBoxesY, plus the same bytes read misaligned. Nothing
    /// else in it is constant within a grid shape and different across shapes, which a type
    /// must be. So the answer is not there, and the window is worth what a recording costs.
    ///
    /// A stash of 180 inventories is 180 KB a frame at this size. The sweep takes a frame every
    /// couple of seconds and the recorder drops a read whose bytes have not moved, so a capture
    /// pays this once rather than per frame.
    /// </remarks>
    public const int Window = 0x400;

    /// <summary>
    /// How much to take from the slot's SECOND pointer, which nothing has ever read.
    /// </summary>
    /// <remarks>
    /// THE OBJECT STARTS BEFORE WHAT WE CALL THE INVENTORY. Every array slot holds two pointers,
    /// at +0x08 and +0x10, and the second is consistently sixteen bytes BELOW the first - so the
    /// struct the schema calls Inventory begins 0x10 into a larger object, and those sixteen
    /// bytes have never been in a recording.
    ///
    /// Which is exactly where a C++ object keeps its vtable, and a vtable is a far better answer
    /// to "what sort is this" than any small integer: different stash types are different
    /// classes, so the pointer differs by construction rather than by convention. Read from the
    /// second pointer rather than from Ptr0 minus 0x10, because the relationship is an
    /// observation about a handful of rows and not a rule anybody has established.
    /// </remarks>
    public const int SharedWindow = 0x40;

    /// <summary>One slot of the inventory array, whole.</summary>
    public const int EntryWindow = 0x18;

    /// <summary>
    /// The highest row number StashType has, so a type field cannot hold more than this.
    /// </summary>
    /// <remarks>
    /// Twenty-five rows, NormalStash through RelicStash, read off the dat export rather than
    /// guessed. It is the one hard bound the question has, and using it is the difference
    /// between asking what a field HOLDS and asking merely how varied it is.
    /// </remarks>
    public const uint LastStashType = 24;

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

    /// <summary>Where the slot's second, unnamed pointer sits. See SharedWindow.</summary>
    public const int SecondPointer = 0x10;

    private readonly byte[] _buffer = new byte[Window];
    private readonly byte[] _entry = new byte[EntryWindow];
    private readonly byte[] _shared = new byte[SharedWindow];

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

            // A LADDER, the one the component sweep earned: a read is all-or-nothing, so a
            // window that runs past the end of a mapping - or past what an OLDER recording
            // happens to hold - takes nothing at all rather than the part that is there. The
            // steps are the windows this sweep has shipped with, so a capture made before it
            // was widened still replays.
            var got = 0;
            foreach (int size in (int[])[Window, 0x220, 0x160])
            {
                if (_reader.TryRead(at, _buffer.AsSpan(0, size)))
                {
                    got = size;
                    break;
                }
            }

            if (got == 0)
            {
                continue;
            }

            ulong items = _reader.ReadPointer(at + (ulong)_itemList);
            ulong itemsEnd = _reader.ReadPointer(at + (ulong)_itemListLast);
            int cells = MemoryReaderExtensions.IsPlausiblePointer(items) && itemsEnd > items
                ? (int)Math.Min((long)(itemsEnd - items) / 8, StashReader.MostItems)
                : 0;

            // The slot's second pointer, read as a window of its own rather than assumed to be
            // the first minus sixteen - see SharedWindow.
            ulong shared = BitConverter.ToUInt64(_entry, SecondPointer);
            byte[] before = MemoryReaderExtensions.IsPlausiblePointer(shared)
                             && _reader.TryRead(shared, _shared.AsSpan())
                ? _shared[..]
                : [];

            seen.Add(new InventoryObservation(
                id, entry, _entry[..], at, _buffer[..got], columns, rows, cells, before));
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
            + $"({shops} shop pages, {seen.Count(one => one.Cells > 0)} loaded).");

        if (shops < 2)
        {
            // Said out loud, because the whole check below rests on it and its absence looks
            // exactly like "no candidate survived".
            output.WriteLine(
                "  NO CONTROL GROUP: fewer than two shop pages were read, so nothing here can be");
            output.WriteLine(
                "  tested against a sort we know. Open the Merchant window once and sweep again.");
        }

        DrawByShape("array slot", seen, one => one.EntryBytes, output);
        DrawByShape("inventory head", seen, one => one.Bytes, output);
        DrawByShape("before the inventory", seen, one => one.Shared, output);
        DrawCandidates("array slot", seen, one => one.EntryBytes, EntryWindow, output);
        DrawCandidates("inventory head", seen, one => one.Bytes, Window, output);
        DrawCandidates("before the inventory", seen, one => one.Shared, SharedWindow, output);
        DrawInventories(seen, output);
    }

    /// <summary>
    /// Fields that are the same for every tab of one grid shape and differ between shapes.
    /// </summary>
    /// <remarks>
    /// THE BETTER CONTROL, and it replaces an assumption that turned out to be unsupported. The
    /// shop-page test below rests on the two pages being a distinct SORT, which nothing
    /// establishes: their grid is an ordinary twelve by twelve and their type may be ordinary
    /// too. What is not an assumption is that a type DECIDES a layout - a currency stash is
    /// 37x10 and nothing else is - so a type cannot vary among tabs that share a shape, and must
    /// vary between a 37x10 and a 12x12.
    ///
    /// Every width and every ALIGNMENT, because a twenty-five row enum most likely fits a byte,
    /// and a byte at an odd offset is invisible to a scan that steps four at a time. That gap is
    /// how the first pass missed everything it could have found.
    ///
    /// The grid itself passes this test, necessarily. It is left in rather than filtered out,
    /// because seeing TotalBoxes come back is the proof that the scan is looking where it thinks.
    /// </remarks>
    private static void DrawByShape(
        string what,
        IReadOnlyList<InventoryObservation> seen,
        Func<InventoryObservation, byte[]> bytesOf,
        TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"  {what} - constant within a grid shape, different across shapes:");

        List<IGrouping<(int Columns, int Rows), InventoryObservation>> shapes =
            [.. seen.GroupBy(one => (one.Columns, one.Rows))];

        int window = seen.Count == 0 ? 0 : seen.Min(one => bytesOf(one).Length);

        // NOT READ IS NOT THE SAME AS NOTHING FOUND, and this whole line of work has already
        // cost a round to that confusion once - a capture came back without the sweep in it and
        // the report said nothing survived. A window nobody filled says so.
        if (window == 0)
        {
            output.WriteLine("    NOT READ in this capture - so nothing here can be concluded.");
            return;
        }

        var found = 0;

        foreach (int width in (int[])[1, 2, 4, 8])
        {
            for (int offset = 0; offset + width <= window; offset++)
            {
                var perShape = new List<((int Columns, int Rows) Shape, ulong Value)>();
                var uniform = true;

                foreach (IGrouping<(int Columns, int Rows), InventoryObservation> shape in shapes)
                {
                    ulong? agreed = null;
                    foreach (InventoryObservation one in shape)
                    {
                        ulong value = At(bytesOf(one), offset, width);
                        if (agreed is { } already && already != value)
                        {
                            uniform = false;
                            break;
                        }

                        agreed ??= value;
                    }

                    if (!uniform)
                    {
                        break;
                    }

                    perShape.Add((shape.Key, agreed ?? 0));
                }

                if (!uniform || perShape.Select(pair => pair.Value).Distinct().Count() < 2)
                {
                    continue;
                }

                found++;
                output.WriteLine(
                    $"    +0x{offset:X3} w{width}  "
                    + string.Join(
                        "  ",
                        perShape.Take(8).Select(pair =>
                            $"{pair.Shape.Columns}x{pair.Shape.Rows}=0x{pair.Value:X}"))
                    + (perShape.Count > 8 ? " ..." : string.Empty));
            }
        }

        if (found == 0)
        {
            output.WriteLine("    none - nothing here tracks what sort of grid the tab has.");
        }
    }

    /// <summary>A field of the given width at an offset, however it is aligned.</summary>
    private static ulong At(byte[] bytes, int offset, int width) => width switch
    {
        1 => bytes[offset],
        2 => BitConverter.ToUInt16(bytes, offset),
        4 => BitConverter.ToUInt32(bytes, offset),
        _ => BitConverter.ToUInt64(bytes, offset),
    };

    /// <summary>
    /// Every 4-byte field that could be a type, and whether the game agrees it is one.
    /// </summary>
    /// <remarks>
    /// FOUR CONDITIONS, ALL OF WHICH THE GAME SETTLES, because a structural fingerprint alone
    /// has fooled this project before - a plausible pointer and a unit-length row look like
    /// anything. A type field must: hold only values StashType has a row for, so 0 to 24; be the
    /// SAME on both shop pages, which are the same sort by construction; DIFFER on at least one
    /// ordinary tab, or it is saying nothing about sort; and not be half of an address.
    ///
    /// THE FIRST TWO OF THOSE ARE A CORRECTION. The first run tested how MANY distinct values a
    /// field took rather than what those values WERE, which is a different and far weaker
    /// question - and it let through sixty-odd offsets, almost all of them the upper halves of
    /// 64-bit pointers. With two heap regions in play those halves take exactly two values
    /// (0x38F and 0x390), pass a "few values" test perfectly, and agree across any two rows half
    /// the time by chance. That is precisely the weak fingerprint CLAUDE.md warns about, arrived
    /// at by writing a check that did not say what it was described as saying.
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

        // See DrawByShape: a window nobody read cannot be reported on as if it were empty.
        if (seen.Count == 0 || seen.Min(one => bytesOf(one).Length) == 0)
        {
            output.WriteLine("    NOT READ in this capture - so nothing here can be concluded.");
            return;
        }

        var found = 0;

        for (int offset = 0; offset + 4 <= window; offset += 4)
        {
            var values = new Dictionary<uint, int>();
            uint? shop = null;
            var shopsAgree = true;
            var readable = 0;
            var pointerHalf = false;

            foreach (InventoryObservation one in seen)
            {
                byte[] bytes = bytesOf(one);
                if (bytes.Length < offset + 4)
                {
                    continue;
                }

                // THE EIGHT BYTES THIS FIELD SITS INSIDE, on their natural alignment. If those
                // read as an address then this "field" is half of one, and its upper half in
                // particular takes one value per heap region - which is two, on this game.
                int aligned = offset & ~7;
                if (bytes.Length >= aligned + 8
                    && MemoryReaderExtensions.IsPlausiblePointer(BitConverter.ToUInt64(bytes, aligned)))
                {
                    pointerHalf = true;
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

            // A field every inventory agrees on says nothing about sort. And the VALUES have to
            // be ones StashType has a row for - which is the test this used to get wrong, by
            // counting how many distinct values there were instead of looking at them.
            if (readable == 0 || values.Count < 2 || pointerHalf)
            {
                continue;
            }

            if (values.Keys.Any(value => value > LastStashType))
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
    /// <remarks>
    /// The shop pages FIRST whatever their size, because they are the control group and the
    /// reader's eye needs them next to an ordinary tab to judge any candidate at all.
    /// </remarks>
    private static void DrawInventories(IReadOnlyList<InventoryObservation> seen, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  what was read (cells, not items - the list is one slot per cell):");

        List<InventoryObservation> order =
            [.. seen.OrderByDescending(one => IsShop(one.Id)).ThenByDescending(one => one.Cells)];

        foreach (InventoryObservation one in order.Take(40))
        {
            output.WriteLine(
                $"    id {one.Id.ToString(CultureInfo.InvariantCulture),12}  "
                + $"{one.Columns,2}x{one.Rows,-2}  {one.Cells,4} cells  "
                + $"slot +0x04={BitConverter.ToUInt32(one.EntryBytes, 4):X8}  "
                + $"0x{one.Address:X}"
                + (IsShop(one.Id) ? "   <- shop page" : string.Empty));
        }

        // COUNTED, not assumed. This line used to say "all holding nothing" about rows it had
        // never looked at, which is the same fault as the report it belongs to: an assertion
        // written where a measurement was meant.
        List<InventoryObservation> rest = [.. order.Skip(40)];
        if (rest.Count > 0)
        {
            int loaded = rest.Count(one => one.Cells > 0);
            output.WriteLine(
                $"    ...and {rest.Count} more, {loaded} of them loaded.");
        }
    }
}
