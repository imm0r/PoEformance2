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

/// <summary>
/// A vector sitting beside PlayerInventories that could be the per-tab list.
/// </summary>
/// <param name="Where">Which struct it was found in - the outer server data, or the inner one.</param>
/// <param name="Offset">Where in that struct the vector's two pointers are.</param>
/// <param name="Stride">
/// The element size that made its length divide into about one record per inventory, or 0 when
/// none did. ZERO IS NOT A REJECTION - a candidate holding text is reported whatever its shape.
/// </param>
/// <param name="Count">How many elements that gives, or 0 with no stride.</param>
/// <param name="Text">
/// Any text found in it, each entry prefixed with the offset it sat at so the gaps between them
/// show the record size. THIS IS THE ANSWER when it holds a tab name: the names are the player's
/// own words, so nothing but the real list can produce them.
/// </param>
public sealed record ParallelList(
    string Where, int Offset, ulong First, int Stride, int Count, IReadOnlyList<string> Text);

/// <summary>Where a tab's own name turned up, and how it was reached.</summary>
/// <param name="Path">
/// The hops from a named root - "inv 42 +0x0F0 +0x018". THIS IS THE DELIVERABLE: a path is a
/// schema entry, and the whole question is whether one exists from the inventories at all.
/// </param>
/// <param name="At">The struct the string header sits in.</param>
/// <param name="Offset">Where in that struct, so the path can be turned into offsets.</param>
/// <param name="Text">What was read, to confirm it is the name and not a coincidence.</param>
public sealed record NameHit(string Path, ulong At, int Offset, string Text);

/// <summary>One entry of the tab array, and the name it leads to.</summary>
/// <param name="Offset">Where in the block it sits, so a run can be turned into a base.</param>
/// <param name="Record">The StashTabRecord it points at.</param>
/// <param name="Name">The player's own word for this tab.</param>
/// <param name="Filled">
/// Whether the entry's vector has anything in it. EVERY ENTRY EVER EXAMINED BY HAND WAS EMPTY,
/// so this column exists to find the first one that is not - what that vector holds is the open
/// question, and guild tab contents have never been readable by any other route.
/// </param>
public sealed record StashTab(int Offset, ulong Record, string Name, bool Filled);

/// <summary>The tab array as a whole.</summary>
/// <param name="Block">What ServerDataStructure.TabRecords points at.</param>
/// <param name="Offset">Where the run of entries starts inside it.</param>
/// <param name="Count">How many entries long the run is, loaded or not.</param>
/// <param name="Reach">
/// How many bytes of the block were ACTUALLY read. Reported rather than assumed: the first
/// version of this printed the window CONSTANT in its "nothing found" line while the read had
/// quietly taken a quarter of it, so the report described a search that never happened.
/// </param>
/// <param name="Read">
/// Whether the block could be read. FALSE AND EMPTY IS NOT NONE - the same distinction the rest
/// of this report insists on, for the same reason.
/// </param>
public sealed record TabScan(
    ulong Block, int Offset, int Count, IReadOnlyList<StashTab> Tabs, int Reach, bool Read);

/// <summary>What one frame of the sweep saw.</summary>
/// <param name="Searched">
/// Whether the server-data window could be read at all. FALSE AND EMPTY IS NOT NONE - a window
/// nobody read has already been reported as "nothing found" once in this line of work, and the
/// round it cost is the reason this flag exists rather than an inference from an empty list.
/// </param>
/// <param name="Hits">
/// Every place a given tab name was reachable from the inventories or the server data. Empty
/// when no name was given to look for - see <paramref name="Hunted"/>.
/// </param>
/// <param name="Hunted">Whether a name was given at all, for the same reason Searched exists.</param>
/// <param name="ServerData">
/// The OUTER server-data struct - what ServerDataPtr points at.
/// </param>
/// <param name="Holder">
/// The INNER one, which actually carries PlayerInventories.
/// </param>
/// <remarks>
/// THE TWO ADDRESSES EXIST TO BE COMPARED AGAINST A CHAIN FOUND ELSEWHERE. A --peek of a real
/// pointer path prints every hop; without these, deciding whether such a chain passes through the
/// stash data means eyeballing whether its hops LOOK like the addresses the sweep printed, and
/// two allocations on one heap page look exactly alike. Adjacency is the weak structural
/// fingerprint this project keeps being burned by; an equality is not.
/// </remarks>
public sealed record InventorySweepFrame(
    int Frame,
    IReadOnlyList<InventoryObservation> Seen,
    IReadOnlyList<ParallelList> Lists,
    bool Searched,
    IReadOnlyList<NameHit> Hits,
    bool Hunted,
    ulong ServerData,
    ulong Holder,
    TabScan? Tabs);

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
    private readonly int _tabRecords;
    private readonly int _tabName;

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
        _tabRecords = schema.Structs["ServerDataStructure"].OffsetOf("TabRecords");
        _tabName = schema.Structs["StashTabRecord"].OffsetOf("Name");

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
    /// <param name="needle">
    /// A tab name to hunt for, or empty. GIVEN BY THE PLAYER, because it has to be: the names are
    /// their own words, so the tool cannot know one and cannot recognise one it has not been told.
    /// </param>
    public InventorySweepFrame? SampleFrame(ulong gameStatesStatic, int frame, string needle = "")
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

        var lists = new List<ParallelList>();
        bool searched = Parallel("inner", holder, seen.Count, lists);
        if (serverData != holder)
        {
            searched |= Parallel("outer", serverData, seen.Count, lists);
        }

        IReadOnlyList<NameHit> hits = Hunt(needle, seen, serverData, holder);
        return new InventorySweepFrame(
            frame, seen, lists, searched, hits, needle.Length > 0, serverData, holder,
            ScanTabs(serverData));
    }

    /// <summary>How much of the tab block to take. The one entry ever seen sat at 0x3A90.</summary>
    public const int TabWindow = 0x8000;

    /// <summary>
    /// How much of the block is asked for at a time.
    /// </summary>
    /// <remarks>
    /// Because a read is all-or-nothing. One request for the whole window comes back empty
    /// whenever the block ends earlier, and a halving ladder in its place lands on a size that
    /// stops short of the data - which is exactly what happened: 0x2000 was taken, the entry at
    /// +0x3A90 was never in it, and the scan reported nothing found in a window it had not read.
    /// </remarks>
    public const int TabChunk = 0x400;

    /// <summary>Bytes per entry - measured across three consecutive named entries.</summary>
    public const int TabStride = 0x18;

    /// <summary>Shortest run of named entries worth believing.</summary>
    /// <remarks>
    /// Four, because three pointers in a row that happen to look like an entry is a coincidence a
    /// 32 KB window will produce several times over, and this project has been burned by exactly
    /// that class of fingerprint. A run also has to be BOUNDED by named entries, so a stretch of
    /// zeroed memory cannot be counted as a very long one.
    /// </remarks>
    public const int ShortestRun = 4;

    /// <summary>How many names to read out of the best run.</summary>
    private const int MostTabs = 512;

    /// <summary>
    /// Walks the whole tab array and reads every name in it.
    /// </summary>
    /// <remarks>
    /// WHERE THE ARRAY STARTS IS NOT KNOWN, which is why this is a scan and not a read. The one
    /// entry a pointer scan ever produced sat at 0x3A90 into the block, and 0x3A90 is not a
    /// multiple of the 0x18 stride, so the base is somewhere else and has to be found.
    ///
    /// The expensive part is deliberately last. Deciding whether an offset LOOKS like an entry
    /// costs nothing - the bytes are already in the window - so the shape filter runs over every
    /// 8-aligned offset first, and only the best surviving run has its records actually read. A
    /// filter that needed a read per candidate would be thousands of reads into a recording.
    ///
    /// Entries whose record pointer is null are kept INSIDE a run rather than ending it: a tab
    /// the game has not loaded reads as zero, and the array does not stop at the first one. What
    /// a run may not do is start or end on one, so a stretch of zeroed memory scores nothing.
    /// </remarks>
    private TabScan? ScanTabs(ulong serverData)
    {
        ulong block = _reader.ReadPointer(serverData + (ulong)_tabRecords);
        if (!MemoryReaderExtensions.IsPlausiblePointer(block))
        {
            return null;
        }

        // IN CHUNKS, NOT ALL AT ONCE, and the first version of this cost a whole live run. A read
        // is all-or-nothing, so asking for 0x8000 of a block that ends earlier returns NOTHING -
        // and the halving ladder that replaced it fell straight past the answer: the one entry
        // known to exist sits at +0x3A90, which 0x2000 does not reach. The scan then reported
        // finding nothing, in a window it had never read.
        //
        // Chunking takes as much as the mapping actually holds, whatever that is, and stops at
        // the first chunk that is not there.
        var window = new byte[TabWindow];
        var got = 0;
        while (got < TabWindow && _reader.TryRead(block + (ulong)got, window.AsSpan(got, TabChunk)))
        {
            got += TabChunk;
        }

        if (got == 0)
        {
            return new TabScan(block, 0, 0, [], 0, Read: false);
        }

        // Entries sit 0x18 apart, and 0x18 is three qwords - so every run lives entirely in one
        // of three alignments, and each can be walked independently.
        var bestStart = -1;
        var bestNamed = 0;
        var bestLength = 0;

        for (var phase = 0; phase < TabStride; phase += 8)
        {
            var runStart = -1;
            var named = 0;
            var lastNamed = -1;

            for (int at = phase; at + TabStride <= got; at += TabStride)
            {
                EntryShape shape = Classify(window, at);

                if (shape == EntryShape.Bad)
                {
                    Keep(runStart, lastNamed, named);
                    runStart = -1;
                    named = 0;
                    lastNamed = -1;
                    continue;
                }

                if (shape == EntryShape.Named)
                {
                    if (runStart < 0)
                    {
                        runStart = at;
                    }

                    named++;
                    lastNamed = at;
                }
            }

            Keep(runStart, lastNamed, named);
        }

        void Keep(int start, int last, int count)
        {
            // Trimmed to the named entries at each end, so trailing zeroes do not inflate it.
            if (start < 0 || count < ShortestRun || count <= bestNamed)
            {
                return;
            }

            bestStart = start;
            bestNamed = count;
            bestLength = ((last - start) / TabStride) + 1;
        }

        if (bestStart < 0)
        {
            return new TabScan(block, 0, 0, [], got, Read: true);
        }

        var tabs = new List<StashTab>();
        for (var i = 0; i < bestLength && tabs.Count < MostTabs; i++)
        {
            int at = bestStart + (i * TabStride);
            ulong record = BitConverter.ToUInt64(window, at);
            if (!MemoryReaderExtensions.IsPlausiblePointer(record))
            {
                continue;
            }

            string name = _reader.ReadStdWString(record + (ulong)_tabName, 64);
            if (name.Length == 0)
            {
                continue;
            }

            ulong first = BitConverter.ToUInt64(window, at + 8);
            ulong last = BitConverter.ToUInt64(window, at + 0x10);
            tabs.Add(new StashTab(at, record, name, Filled: first != 0 && last != first));
        }

        return new TabScan(block, bestStart, bestLength, tabs, got, Read: true);
    }

    /// <summary>What an offset looks like when read as an entry.</summary>
    private enum EntryShape
    {
        /// <summary>Not an entry.</summary>
        Bad,

        /// <summary>All zeroes - a tab the game has not loaded. Allowed inside a run.</summary>
        Blank,

        /// <summary>A record pointer and a sane vector beside it.</summary>
        Named,
    }

    /// <summary>Reads an offset as an entry, from bytes alone - no memory access.</summary>
    private static EntryShape Classify(byte[] window, int at)
    {
        ulong record = BitConverter.ToUInt64(window, at);
        ulong first = BitConverter.ToUInt64(window, at + 8);
        ulong last = BitConverter.ToUInt64(window, at + 0x10);

        if (record == 0 && first == 0 && last == 0)
        {
            return EntryShape.Blank;
        }

        if (!MemoryReaderExtensions.IsPlausiblePointer(record))
        {
            return EntryShape.Bad;
        }

        // The pair is a vector: empty on every entry examined so far, which means first == last,
        // but a full one would have last above it. Both null is equally fine.
        if (first == 0 && last == 0)
        {
            return EntryShape.Named;
        }

        return MemoryReaderExtensions.IsPlausiblePointer(first)
               && MemoryReaderExtensions.IsPlausiblePointer(last)
               && last >= first
            ? EntryShape.Named
            : EntryShape.Bad;
    }

    /// <summary>
    /// How much of each struct the name hunt reads.
    /// </summary>
    /// <remarks>
    /// 0x400 BECAUSE 0x100 WAS MEASURABLY TOO NARROW, and it took a player's pointer scan to show
    /// it. The chain found on a live client is [X + 0x1E0] -> [Y + 0x18] -> the characters: the
    /// hop that reaches the record holding the name sits at 0x1E0, so a hunt reading 0x100 of X
    /// would never see that pointer, never enqueue Y, and would have reported NOT FOUND - a clean,
    /// confident, wrong negative, of exactly the kind the Searched and Hunted flags exist to
    /// prevent and which a window size can produce just as easily.
    ///
    /// The node budget pays for it: 0x100 across 4000 structs and 0x400 across 1500 cost about the
    /// same recording, and the reference the game handed over says the width matters more than the
    /// breadth. Widening also matches the inventory window, so a struct read by both is one entry
    /// in the recording rather than two.
    /// </remarks>
    public const int HuntWindow = 0x400;

    /// <summary>How many pointers deep the hunt follows.</summary>
    /// <remarks>
    /// Three, because a name hanging off an inventory would be at most a container and a record
    /// away, and every extra hop multiplies the reads by the number of pointers in a struct.
    /// </remarks>
    public const int HuntDepth = 3;

    /// <summary>How many structs the hunt may read, so a wide graph cannot fill the recording.</summary>
    /// <remarks>Traded down from 4000 when the window went from 0x100 to 0x400 - see HuntWindow.</remarks>
    private const int MostNodes = 1500;

    /// <summary>
    /// Walks outwards from the inventories looking for a name the player supplied.
    /// </summary>
    /// <remarks>
    /// THE ONE SEARCH THAT CANNOT FOOL ANYBODY, and the reason it exists is that a player scanned
    /// their own tab name in Cheat Engine and found it at exactly two addresses - so the string is
    /// certainly in memory, and the only open question is whether anything ON THE STASH SIDE
    /// points at it. Every other test in this file infers; this one either produces a path from an
    /// inventory to the player's own word, or it does not.
    ///
    /// BOTH ANSWERS ARE WORTH THE CAPTURE. A path is the feature. No path, from the inventories,
    /// from their array slots and from both server-data structs, three pointers deep, is the
    /// evidence that the name is held UI-side only - which is what this project has believed for
    /// several rounds now on the strength of an inference nobody tested.
    ///
    /// The needle is matched case-insensitively and as a SUBSTRING, so a player can pass part of a
    /// name and so that the surrounding storage - a name inside a longer record string - still
    /// registers rather than being missed on an exact-match rule.
    /// </remarks>
    private IReadOnlyList<NameHit> Hunt(
        string needle, IReadOnlyList<InventoryObservation> seen, ulong serverData, ulong holder)
    {
        var hits = new List<NameHit>();
        if (string.IsNullOrEmpty(needle))
        {
            return hits;
        }

        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong At, int Size, string Path, int Depth)>();

        void Push(ulong at, int size, string path, int depth)
        {
            if (depth > HuntDepth
                || !MemoryReaderExtensions.IsPlausiblePointer(at)
                || !visited.Add(at))
            {
                return;
            }

            queue.Enqueue((at, size, path, depth));
        }

        // The inventories FIRST, and their array slots with them. Breadth-first means everything
        // one hop from a stash tab is read before anything one hop from the server struct, so the
        // budget goes on the side of the graph the question is about.
        foreach (InventoryObservation one in seen)
        {
            Push(one.Address, HuntWindow, $"inv {one.Id}", 0);
            Push(one.Entry, EntryWindow, $"slot {one.Id}", 0);
        }

        Push(holder, ListSearch, "inner", 0);
        Push(serverData, ListSearch, "outer", 0);

        var window = new byte[ListSearch + TextProbe.HeaderSize];
        var nodes = 0;

        while (queue.Count > 0 && nodes < MostNodes && hits.Count < 32)
        {
            (ulong at, int size, string path, int depth) = queue.Dequeue();
            nodes++;

            // The ladder again: a read is all-or-nothing, so a window running off the end of a
            // mapping takes nothing rather than the part that is there.
            var got = 0;
            foreach (int want in (int[])[size + TextProbe.HeaderSize, size, 0x80, 0x40])
            {
                if (want <= window.Length && _reader.TryRead(at, window.AsSpan(0, want)))
                {
                    got = want;
                    break;
                }
            }

            if (got == 0)
            {
                continue;
            }

            for (int row = 0; row + TextProbe.HeaderSize <= got; row += 8)
            {
                TextCandidate candidate = TextProbe.At(window.AsSpan(0, got), row);
                string text = candidate.Shape switch
                {
                    TextShape.Inline => candidate.Text,
                    TextShape.Heap =>
                        _reader.ReadUnicodeString(candidate.Address, Math.Min(candidate.Length, 128)),
                    _ => string.Empty,
                };

                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new NameHit($"{path} +0x{row:X3}", at, row, text));
                }
            }

            if (depth >= HuntDepth)
            {
                continue;
            }

            for (int row = 0; row + 8 <= got; row += 8)
            {
                Push(BitConverter.ToUInt64(window, row), HuntWindow, $"{path} +0x{row:X3}", depth + 1);
            }
        }

        return hits;
    }

    /// <summary>How far into a server-data struct to look for a sibling vector.</summary>
    /// <remarks>
    /// PlayerInventories is at 0x320 and a list kept alongside it has no reason to be far off,
    /// but "no reason to be" is a guess and this costs one read of one struct - not one per
    /// inventory. Sixteen kilobytes is cheap enough that guessing narrower would be the
    /// expensive choice.
    /// </remarks>
    public const int ListSearch = 0x4000;

    /// <summary>
    /// How much of a candidate's contents to scan for text.
    /// </summary>
    /// <remarks>
    /// Enough for the first several records whatever their size, and no more: the question is
    /// whether this list holds NAMES at all, which its head answers. Reading all of a 200-entry
    /// vector to confirm what its first entries already said would multiply the capture for
    /// nothing.
    /// </remarks>
    private const int Probe = 0x200;

    /// <summary>
    /// How much of a candidate that ALREADY showed text to read.
    /// </summary>
    /// <remarks>
    /// Enough for a couple of hundred records of any plausible size. Only candidates that already
    /// produced text get this, so it is spent on the handful worth spending it on rather than on
    /// every pointer pair in a 16 KB struct.
    /// </remarks>
    private const int DeepProbe = 0x4000;

    /// <summary>How many distinct targets one struct may probe, so a bad window cannot run away.</summary>
    private const int MostProbes = 512;

    /// <summary>
    /// Vectors beside PlayerInventories, and any TEXT their first entries hold.
    /// </summary>
    /// <remarks>
    /// THE LAST PLACE THE ANSWER CAN BE, and the note at <c>StashReader.Read</c> has pointed at
    /// it since before anybody looked: it says the tab NAMES live in a separate list matched to
    /// the inventories by position. Nothing has ever gone to find that list. Everything else is
    /// ruled out - see the Inventory comment in the schema.
    ///
    /// A COUNT ALONE PROVES NOTHING, WHICH IS WHY IT IS NOT THE FILTER. A struct this size holds
    /// many pointer pairs that happen to divide evenly, and "a vector of about 200 things" is the
    /// weak structural fingerprint this project keeps being burned by. Worse, it can only reject:
    /// a name list covering a different set of tabs than the inventories - the stash ones only,
    /// say - fails a count rule and is thrown away unlooked-at, which is the one outcome this
    /// round cannot afford.
    ///
    /// So EVERY vector-shaped pair gets its contents read, and TEXT decides. The tab names are the
    /// player's own words, so nothing but the real list can produce them; a stride and a count are
    /// worked out afterwards, for reporting, and a candidate that has no plausible stride is still
    /// reported when it holds names. What is bounded instead is the cost: distinct targets only,
    /// <see cref="Probe"/> bytes of each, at most <see cref="MostProbes"/> per struct.
    /// </remarks>
    /// <returns>Whether the window could be read - see InventorySweepFrame.Searched.</returns>
    private bool Parallel(string where, ulong at, int inventories, List<ParallelList> into)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(at) || inventories <= 0)
        {
            return false;
        }

        // The same ladder the inventory read uses, and for the same two reasons: a read is
        // all-or-nothing so a window running past the mapping takes nothing, and an older
        // recording holds whatever the build of the day asked for.
        var window = new byte[ListSearch];
        var reach = 0;
        foreach (int size in (int[])[ListSearch, 0x2000, 0x1000, 0x800])
        {
            if (_reader.TryRead(at, window.AsSpan(0, size)))
            {
                reach = size;
                break;
            }
        }

        if (reach == 0)
        {
            return false;
        }

        // One read per TARGET, not per offset that mentions it. A vector's pointers turn up
        // repeatedly in a struct this size, and probing the same address ten times would spend
        // the budget on one candidate.
        var probed = new HashSet<ulong>();

        for (int offset = 0; offset + 16 <= reach; offset += 8)
        {
            ulong first = BitConverter.ToUInt64(window, offset);
            ulong last = BitConverter.ToUInt64(window, offset + 8);

            if (!MemoryReaderExtensions.IsPlausiblePointer(first)
                || last <= first
                || last - first > 0x100000)
            {
                continue;
            }

            ulong span = last - first;
            (int Stride, int Count) shape = Shape(span, inventories);

            if (probed.Count >= MostProbes && shape.Stride == 0)
            {
                continue;
            }

            IReadOnlyList<string> text = [];
            if (probed.Add(first) && probed.Count <= MostProbes)
            {
                // TWO STAGES, AND THE FIRST CAPTURE IS WHY. A 132-entry list of 0x10 records was
                // found holding tab-like text, and the shallow probe had read 0x200 of it - the
                // first 32 entries - so the name actually being hunted could not have been in
                // what was looked at. A candidate that shows text has earned the whole read; the
                // thousands that show none must never get it, which is what the cheap first pass
                // is for.
                IReadOnlyList<StashReader.FoundText> found =
                    _stash.StringsIn(first, 0, (int)Math.Min(span, Probe));

                if (found.Count > 0 && span > Probe)
                {
                    found = _stash.StringsIn(first, 0, (int)Math.Min(span, DeepProbe));
                }

                // WITH THE OFFSET, because it is what turns a list of words into a structure: the
                // gaps between them are the record size, and reading that off is how the entry
                // holding a name gets described without guessing a stride.
                text =
                [
                    .. found
                        .Select(one => $"+0x{one.Offset:X3}={one.Text}")
                        .Distinct()
                        .Take(24),
                ];
            }

            // Text is the answer; a plausible stride is only a lead. Anything with neither is
            // one of the thousands of pointer pairs a struct this size holds, and saying so
            // would bury the thing being looked for.
            if (text.Count > 0 || shape.Stride != 0)
            {
                into.Add(new ParallelList(
                    where, offset, first, shape.Stride, shape.Count, text));
            }
        }

        return true;
    }

    /// <summary>Element sizes a record list plausibly uses.</summary>
    private static readonly int[] Strides =
        [8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x60, 0x80];

    /// <summary>
    /// The stride that makes a span into about one record per inventory, or nothing.
    /// </summary>
    /// <remarks>
    /// Wide on purpose, and only ever a LEAD: a per-tab list might cover every inventory or only
    /// the stash ones, so the range is generous and a miss here does not stop the candidate being
    /// read - see the note on <c>Parallel</c>.
    /// </remarks>
    private static (int Stride, int Count) Shape(ulong span, int inventories)
    {
        foreach (int stride in Strides)
        {
            if (span % (ulong)stride != 0)
            {
                continue;
            }

            var count = (int)(span / (ulong)stride);
            if (count >= inventories / 4 && count <= inventories * 2)
            {
                return (stride, count);
            }
        }

        return (0, 0);
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

        // WHAT THIS BINARY CAN LOOK FOR, named so a stale build is visible at a glance.
        // A capture was once made with an older exe and read as a result: the two searches that
        // would have answered the question were simply not in it, and their sections were absent
        // rather than empty - which nobody notices, because a missing section looks like nothing
        // more than a report that is shorter than you remembered. This line and the sections
        // below have to agree; when they do not, the exe is older than the report being read.
        output.WriteLine(
            "  searches in this build: grid shapes, sort candidates, parallel lists, NAME hunt.");

        // THE ROOTS, PRINTED, so a chain found in Cheat Engine can be settled against them by
        // equality rather than by how similar its addresses look. --peek lists every hop of a
        // path; if one of them IS one of these, the path runs through the stash data, and if none
        // is, it does not. Two objects a few kilobytes apart on one heap page are not related,
        // however much the leading digits agree.
        output.WriteLine(
            $"  roots: server data (outer) 0x{best.ServerData:X}, inventory holder (inner) "
            + $"0x{best.Holder:X}.");

        if (shops < 2)
        {
            // Said out loud, because the whole check below rests on it and its absence looks
            // exactly like "no candidate survived".
            output.WriteLine(
                "  NO CONTROL GROUP: fewer than two shop pages were read, so nothing here can be");
            output.WriteLine(
                "  tested against a sort we know. Open the Merchant window once and sweep again.");
        }

        DrawTabs(best.Tabs, output);
        DrawHits(best.Hits, best.Hunted, output);
        DrawLists(best.Lists, best.Searched, output);
        DrawByShape("array slot", seen, one => one.EntryBytes, output);
        DrawByShape("inventory head", seen, one => one.Bytes, output);
        DrawByShape("before the inventory", seen, one => one.Shared, output);
        DrawCandidates("array slot", seen, one => one.EntryBytes, EntryWindow, output);
        DrawCandidates("inventory head", seen, one => one.Bytes, Window, output);
        DrawCandidates("before the inventory", seen, one => one.Shared, SharedWindow, output);
        DrawInventories(seen, output);
    }

    /// <summary>
    /// Every stash tab name the array holds - the point of the whole search.
    /// </summary>
    /// <remarks>
    /// FIRST, because it is the only part of this report that is a feature rather than a lead.
    /// The Filled column is the one open question left: every entry examined by hand had an empty
    /// vector, and what a full one holds is unknown - guild tab contents have never been readable
    /// by any route, and this array is the first thing in the project that reaches guild tabs.
    /// </remarks>
    private static void DrawTabs(TabScan? scan, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  stash tab names:");

        if (scan is null)
        {
            output.WriteLine("    NOT REACHED - ServerDataStructure.TabRecords did not resolve.");
            return;
        }

        if (!scan.Read)
        {
            output.WriteLine($"    NOT READ - the block at 0x{scan.Block:X} could not be read in");
            output.WriteLine("    this capture, so nothing here can be concluded.");
            return;
        }

        if (scan.Tabs.Count == 0)
        {
            output.WriteLine($"    none found in the 0x{scan.Reach:X} bytes readable at 0x{scan.Block:X}.");
            output.WriteLine("    Either the");
            output.WriteLine("    array is further in than the window reaches, or its shape is not");
            output.WriteLine($"    {TabStride:X} bytes of record pointer plus a vector.");
            return;
        }

        int filled = scan.Tabs.Count(one => one.Filled);
        output.WriteLine(
            $"    {scan.Tabs.Count} named of {scan.Count} entries, at 0x{scan.Block:X}"
            + $" +0x{scan.Offset:X} ({filled} with a non-empty vector),"
            + $" in 0x{scan.Reach:X} readable bytes.");
        output.WriteLine();

        foreach (StashTab tab in scan.Tabs)
        {
            output.WriteLine(
                $"    +0x{tab.Offset:X4}  {(tab.Filled ? "*" : " ")} {tab.Name}");
        }

        if (filled == 0)
        {
            output.WriteLine();
            output.WriteLine("    NO entry has a non-empty vector, so what that vector holds is still");
            output.WriteLine("    unknown - see StashTabRecord in the schema.");
        }
    }

    /// <summary>
    /// Where the player's own tab name was reachable from - the answer, if there is one.
    /// </summary>
    /// <remarks>
    /// FIRST IN THE REPORT because it is the only part that settles anything. A path from an
    /// inventory to a name is the feature; no path is the evidence for the conclusion this
    /// project has been drawing without it.
    /// </remarks>
    private static void DrawHits(IReadOnlyList<NameHit> hits, bool hunted, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  the tab name, reached from the stash side:");

        // The same distinction the rest of this report insists on: nobody looked is not nothing
        // was there.
        if (!hunted)
        {
            output.WriteLine("    NOT HUNTED - no name was given. Re-run with:");
            output.WriteLine("      PoEformance.App --record inv.rec --inventories --tabname <a tab's name>");
            return;
        }

        if (hits.Count == 0)
        {
            // AND THIS IS NOT "THE NAME IS NOT THERE", which is what this said until a pointer
            // scan showed otherwise. The hunt sees a bounded shape: HuntWindow bytes of each
            // struct, HuntDepth hops, MostNodes structs. A live scan produced a real chain whose
            // last hops are +0x3A90 then +0x8 - and 0x3A90 is past the end of every window this
            // reads, so that chain is invisible here however deep the walk goes. An empty result
            // bounds the search, not the game.
            output.WriteLine(
                $"    NOT FOUND within reach - {HuntWindow:X} bytes per struct, {HuntDepth} hops,");
            output.WriteLine("    from the inventories, their array slots and both server-data structs.");
            output.WriteLine("    THIS IS NOT A NEGATIVE RESULT ABOUT THE GAME: a chain hopping through a");
            output.WriteLine("    far member - a live pointer scan found one at +0x3A90 - is outside this");
            output.WriteLine("    window by construction. Follow such a chain with --peek instead, which");
            output.WriteLine("    takes a Cheat Engine path as written and prints every hop.");
            return;
        }

        foreach (NameHit hit in hits)
        {
            output.WriteLine($"    *** {hit.Path}  =  \"{hit.Text}\"   (0x{hit.At:X} +0x{hit.Offset:X})");
        }
    }

    /// <summary>
    /// The vectors sitting beside PlayerInventories, the ones carrying TEXT first.
    /// </summary>
    /// <remarks>
    /// TEXT FIRST AND LOUDLY, because if the answer is here it is a tab name, and a tab name is
    /// unmistakable: the player wrote it. Everything below the text is a lead at best - "a vector
    /// of about the right length" is the weak structural fingerprint this project keeps being
    /// burned by, and it is printed only so a person can point at one and say what it is.
    /// </remarks>
    private static void DrawLists(
        IReadOnlyList<ParallelList> lists, bool searched, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("  vectors beside PlayerInventories - THE TAB NAMES WOULD BE HERE:");

        // NOT SEARCHED IS NOT NONE. This exact confusion has already cost a round in this line of
        // work - a capture came back without the sweep in it and the report read as if the answer
        // had been looked for and was absent. An empty list from a window nobody could read is
        // silence, not a negative result.
        if (!searched)
        {
            output.WriteLine("    NOT SEARCHED - the server-data struct could not be read in this");
            output.WriteLine("    capture, so nothing here can be concluded either way.");
            return;
        }

        if (lists.Count == 0)
        {
            output.WriteLine("    none - no pointer pair in either server-data struct leads to text");
            output.WriteLine("    or to a plausible number of records. The list may not exist, or");
            output.WriteLine("    may not hang off these two structs at all.");
            return;
        }

        List<ParallelList> named = [.. lists.Where(list => list.Text.Count > 0)];

        if (named.Count == 0)
        {
            output.WriteLine(
                $"    {lists.Count} candidates by shape, NONE of them holding any text.");
        }

        foreach (ParallelList list in named)
        {
            output.WriteLine($"    *** {Line(list)}");
            output.WriteLine($"        text: {string.Join(" | ", list.Text)}");
        }

        // The rest, compactly. A person reading this knows how many tabs they own, which is a
        // fact no rule here has.
        foreach (ParallelList list in lists.Where(list => list.Text.Count == 0).Take(24))
        {
            output.WriteLine($"    {Line(list)}");
        }

        int hidden = lists.Count(list => list.Text.Count == 0) - 24;
        if (hidden > 0)
        {
            output.WriteLine($"    ...and {hidden} more without text.");
        }
    }

    /// <summary>One candidate on one line - stride unknown reads as such rather than as zero.</summary>
    private static string Line(ParallelList list)
    {
        string shape = list.Stride == 0
            ? "no plausible stride"
            : $"{list.Count} x 0x{list.Stride:X}";

        return $"{list.Where} +0x{list.Offset:X4}  {shape}  at 0x{list.First:X}";
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
