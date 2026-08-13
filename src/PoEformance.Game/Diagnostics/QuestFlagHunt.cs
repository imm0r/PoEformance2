using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>One flag the sweep matched, and where the match was.</summary>
/// <param name="Row">Which row of QuestFlags.dat it is.</param>
/// <param name="Id">The flag's name, read only for rows that actually matched.</param>
/// <param name="At">Where in memory the match sits.</param>
/// <param name="ByPointer">True when a row POINTER was found, false when the row's HASH32 was.</param>
public readonly record struct FlagSighting(int Row, string Id, ulong At, bool ByPointer);

/// <summary>Something a swept region pointed at, worth reading in its own right.</summary>
/// <param name="At">The field the pointer was found in - what to call the finding if it pays off.</param>
/// <param name="Begin">Where the thing starts.</param>
/// <param name="Bytes">How much of it to take.</param>
public readonly record struct FollowTarget(ulong At, ulong Begin, int Bytes);

/// <summary>What one swept region turned out to hold.</summary>
public sealed record SweptRegion(
    string Name,
    ulong Address,
    int Wanted,
    int Read,
    IReadOnlyList<FlagSighting> Sightings,
    IReadOnlyList<FollowTarget> Targets);

/// <summary>The whole hunt: what was found, and what was merely captured.</summary>
public sealed record QuestFlagHuntResult(
    ulong Table,
    string Path,
    int Rows,
    IReadOnlyList<SweptRegion> Regions,
    string Note,
    int Followed = 0,
    long FollowedBytes = 0,
    int SkippedTargets = 0)
{
    /// <summary>Every sighting across every region, which is the answer when there is one.</summary>
    public IEnumerable<FlagSighting> Sightings => Regions.SelectMany(r => r.Sightings);
}

/// <summary>
/// Looks for the flags a CHARACTER has set, as opposed to the list of flags that exist.
/// </summary>
/// <remarks>
/// WHY THIS IS A READER AND NOT AN ANALYSIS. QuestFlags.dat is static content - 5717 names,
/// identical for every character, with no bit anywhere saying whether one is set. The set a
/// character HAS must exist client-side, because QuestStates.dat gates its text on
/// FlagsPresent/FlagsMissing and something has to evaluate that. Two recordings have now
/// failed to contain it, and neither failure was informative: a recording holds only what the
/// running build READ, and nothing in this tool has ever had a reason to read the regions a
/// per-character blob would live in. The second one - taken deliberately with the quest list
/// open - had 64 bytes of ServerData in it out of the first 32 KB.
///
/// So this reads them. The match below is a bonus; the POINT is that the bytes land in a
/// <c>--record</c> session, which turns a question that needs the game into one that can be
/// answered offline as often as it takes. PlayerProbe exists for the same reason.
///
/// WHAT IT LOOKS FOR, and why both: a row POINTER (16 bytes of foreign reference is how the
/// game's own data refers to a flag) and the row's HASH32 (the table carries an index keyed on
/// exactly that value, so a compact runtime set has an obvious reason to store hashes rather
/// than pointers). A third shape - row indices as u16/u32 - is deliberately not looked for
/// here: an index below 5717 is too ordinary a number to tell from noise, and a sweep for it
/// over a recorded session found only false positives.
///
/// WHAT IT ACTUALLY FINDS, after six sessions and two characters: conditions. Every quest-flag
/// reference in running memory is a rule about when something applies - a map marker that
/// waits for CanSeeAngeInHideout, area logic that checks LogbookQuestRune4Used, the act list
/// gated on Visited-G1_town. The fresh character settles what that means: standing in the
/// first town it holds references to A2PerennialKingSlain and HaveAscendancy, which it cannot
/// possibly have set. So this is a map of the game's RULES, and a useful one; the set of flags
/// a character has is a closed hunt, written up beside QuestFlagsRow in the schema.
///
/// That makes the tool's shape wrong in a way worth stating: the brute-force follow below has
/// never found anything. Both of its deep runs ended with more than half a million targets
/// still queued, so it does not converge and a bigger budget cannot fix that. Every sighting
/// this has ever produced came from a TARGETED read - the structural pass over what the state
/// objects point at, and one list one hop out of ServerData.
/// </remarks>
public sealed class QuestFlagHunt
{
    /// <summary>How much of each region to take, in one chunk.</summary>
    /// <remarks>
    /// A read only succeeds when every byte of it is mapped, so a big region is taken in
    /// pieces: one unmapped page at the end must not cost the whole thing. 4 KB is the page
    /// size, so a chunk either lands entirely in a mapped page or in one that is not there.
    /// </remarks>
    public const int ChunkBytes = 4096;

    /// <summary>Largest table this will read rows from, as a guard on a length from memory.</summary>
    public const int MostRows = 100_000;

    /// <summary>How much of a list found in a region to take.</summary>
    public const int ListBytes = 256 * 1024;

    /// <summary>And of something that is only a pointer - one bitset over 5717 flags is 715 bytes.</summary>
    public const int LonePointerBytes = 1024;

    /// <summary>
    /// How much to take of an object InGameState points at directly.
    /// </summary>
    /// <remarks>
    /// 16 KB rather than a kilobyte, and rather than the 64 that would be comfortable: there
    /// are 286 of them, so the window is what decides whether this costs 4.6 MB or eighteen.
    /// </remarks>
    public const int StateObjectBytes = 16 * 1024;

    /// <summary>
    /// Ceiling on the whole second pass, so a region full of pointers cannot turn a diagnostic
    /// into a heap dump.
    /// </summary>
    /// <remarks>
    /// RAISED TO 64 MB BECAUSE EVERY RUN SO FAR HIT THE OLD 16, and a sweep that stops early
    /// cannot say "not there" - only "not there, as far as it got", which is a different and
    /// much weaker sentence. Four sessions of this hunt reported nothing found and all four
    /// were truncated, so the negative they carried was worth less than it looked.
    ///
    /// What it costs is bounded and known: 11 MB of reads compressed to 1.5 MB of recording at
    /// the old ceiling, so a completed sweep should land around 6 MB - still a file that can be
    /// sent to somebody. What it BUYS is the difference between a search and an answer, and
    /// SkippedTargets below says which of the two happened.
    /// </remarks>
    public const long FollowBudget = 64 * 1024 * 1024;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly DatTableShape? _tables;

    /// <summary>
    /// What the whole-address-space scan cost, when one was asked for.
    /// </summary>
    /// <remarks>Not named HeapScan: that is the class this calls, and the property would hide it.</remarks>
    public HeapScanResult? LastHeapScan { get; private set; }

    public QuestFlagHunt(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _tables = DatTableShape.From(schema);
    }

    /// <summary>
    /// Finds the QuestFlags table by way of an NPC standing in the world.
    /// </summary>
    /// <remarks>
    /// NPCs.dat declares a QuestFlags column, so every NPC entity carries a foreign reference
    /// to a row of the table - which makes any NPC a route to it. This is how the table was
    /// found in the first place, in a hideout, and a hideout is also where the quest list is
    /// read, so the one place this route fails is a place the hunt is not run.
    ///
    /// The table is CHECKED, not assumed: the pointer's own path must end in QuestFlags.dat.
    /// A route through three offsets that lands on the wrong table would otherwise sweep for
    /// the hashes of something else entirely and report a confident nothing.
    /// </remarks>
    public ulong FindTableViaNpc(ulong areaInstance, int maxEntities = 512)
    {
        if (_tables is not { } shape)
        {
            return 0;
        }

        StructDef ai = _schema.Structs["AreaInstance"];
        StructDef npc = _schema.Structs["Npc"];
        StructDef data = _schema.Structs["NpcData"];
        StructDef row = _schema.Structs["NpcsRow"];

        // The ADDRESS of the map struct, not a pointer read from it - the tree's root sits
        // inside AreaInstance rather than behind it, and reading through the field lands one
        // hop too far in.
        ulong map = areaInstance + (ulong)ai.OffsetOf("AwakeEntities");
        var entities = new EntityReader(_reader, _schema);
        foreach (ulong address in new EntityMapReader(_reader, _schema)
            .ReadEntityPointers(map, maxEntities).Values)
        {
            ulong component = entities.Read(address)?.Component("NPC") ?? 0;
            if (component == 0)
            {
                continue;
            }

            ulong table = _reader.ReadChain(
                component,
                npc.OffsetOf("NpcDataPtr"),
                data.OffsetOf("NpcsRowPtr"),
                row.OffsetOf("QuestFlagsTablePtr"));

            if (PointerPeek.DescribeTable(_reader, table, shape) is { } facts
                && facts.Path.EndsWith("QuestFlags.dat", StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return 0;
    }

    /// <summary>Every row's HASH32, by row index - the needles the sweep looks for.</summary>
    /// <remarks>
    /// Read as one block per chunk rather than row by row: 5717 rows is 68 KB, which is one
    /// read per page and not 5717 of them. The Ids are NOT read here - only the rows that
    /// turn out to match are worth a string read, and that is the difference between a
    /// diagnostic that costs a page and one that costs six thousand.
    /// </remarks>
    public Dictionary<uint, int> HashesIn(ulong table)
    {
        var hashes = new Dictionary<uint, int>();
        if (_tables is not { } shape
            || PointerPeek.DescribeTable(_reader, table, shape) is not { } facts
            || facts.Rows is <= 0 or > MostRows)
        {
            return hashes;
        }

        StructDef flag = _schema.Structs["QuestFlagsRow"];
        int hashAt = flag.OffsetOf("Hash32");
        int size = (int)facts.RowSize;
        var block = new byte[size * (int)facts.Rows];
        var have = new bool[block.Length];

        for (int taken = 0; taken < block.Length; taken += ChunkBytes)
        {
            int got = TakeChunk(facts.RowsBegin + (ulong)taken, block.AsSpan(taken), Math.Min(ChunkBytes, block.Length - taken));
            Array.Fill(have, true, taken, got);
        }

        // Only rows that were wholly read. A chunk that failed leaves zeros behind, and a
        // zero indexed as a hash would put a real row number on a value that is simply
        // missing - which is exactly the kind of confident wrong answer a replayed session
        // (where most pages were never captured) would produce.
        for (int i = 0; i < facts.Rows; i++)
        {
            int at = (i * size) + hashAt;
            if (have[at] && have[at + sizeof(uint) - 1])
            {
                hashes[BitConverter.ToUInt32(block, at)] = i;
            }
        }

        return hashes;
    }

    /// <summary>
    /// Reads a region and reports every flag it refers to.
    /// </summary>
    /// <remarks>
    /// Scanned at four-byte steps for both shapes rather than at their natural alignment. A
    /// pointer is eight-byte aligned in anything the compiler laid out, but the game's own dat
    /// rows are PACKED and land on odd addresses, so alignment is not a safe assumption in
    /// this process - and the cost of dropping it is one extra pass over a few kilobytes.
    /// </remarks>
    public SweptRegion Sweep(
        string name,
        ulong address,
        int bytes,
        IReadOnlyDictionary<uint, int> hashes,
        ulong rowsBegin,
        long rows,
        int rowSize,
        bool collectTargets = true)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        var sightings = new List<FlagSighting>();
        var targets = new List<FollowTarget>();
        var seen = new HashSet<int>();
        var buffer = new byte[ChunkBytes];
        int read = 0;

        for (int taken = 0; taken < bytes; taken += ChunkBytes)
        {
            // As much of the chunk as is mapped. A region walks off the end of its allocation
            // sooner or later - these sizes are guesses about where a blob ends - and a read
            // fails on ALL of its bytes when one page is missing, so asking for less is the
            // difference between half a region and none of it.
            int want = TakeChunk(address + (ulong)taken, buffer, Math.Min(ChunkBytes, bytes - taken));
            if (want == 0)
            {
                continue;
            }

            read += want;

            for (int at = 0; at + sizeof(uint) <= want; at += sizeof(uint))
            {
                ulong here = address + (ulong)(taken + at);

                if (hashes.TryGetValue(BitConverter.ToUInt32(buffer, at), out int row) && seen.Add(row))
                {
                    sightings.Add(new FlagSighting(row, IdOf(rowsBegin, rowSize, row), here, ByPointer: false));
                }

                if (at + sizeof(ulong) > want || rowSize <= 0)
                {
                    continue;
                }

                ulong value = BitConverter.ToUInt64(buffer, at);
                if (value < rowsBegin)
                {
                    continue;
                }

                ulong offset = value - rowsBegin;
                if (offset % (ulong)rowSize != 0)
                {
                    continue;
                }

                long index = (long)(offset / (ulong)rowSize);
                if (index < rows && seen.Add((int)index))
                {
                    sightings.Add(new FlagSighting((int)index, IdOf(rowsBegin, rowSize, (int)index), here, ByPointer: true));
                }
            }

            if (collectTargets)
            {
                // Whether the chunk after this one is still inside the region, which decides
                // what happens to a pointer sitting in its last eight bytes - see below.
                CollectTargets(buffer, want, address + (ulong)taken, targets, taken + want < bytes);
            }
        }

        return new SweptRegion(name, address, bytes, read, sightings, targets);
    }

    /// <summary>
    /// Notes everything in a chunk worth reading in its own right.
    /// </summary>
    /// <remarks>
    /// THE REASON THE FIRST SWEEP FOUND NOTHING, most likely. A set of flags is a CONTAINER,
    /// and a container's contents live wherever it allocated them - so sweeping the bytes of
    /// ServerData looks straight past it and sees only the two pointers bracketing it. The
    /// second recording made that concrete: 128 KB of ServerData read in full, all 5717
    /// hashes tested against every byte of it, zero matches, and 2,326 distinct heap pointers
    /// sitting in it leading somewhere unread.
    ///
    /// Two shapes, and the cheap one is not a mistake:
    ///   - a begin/end PAIR is a list, and its span says how much to take. This is what a
    ///     vector of flags looks like from outside, whatever the elements are.
    ///   - a LONE pointer gets a kilobyte. That is not enough to sweep a list, and it is not
    ///     meant to be: a bitset over 5717 flags is 715 bytes, so one kilobyte is the whole
    ///     of one - and even where the shape is neither, a kilobyte of every target lands in
    ///     the recording, which is what makes the NEXT round of this happen offline.
    /// </remarks>
    private void CollectTargets(byte[] chunk, int length, ulong at, List<FollowTarget> into, bool lookAhead)
    {
        for (int i = 0; i + sizeof(ulong) <= length; i += sizeof(ulong))
        {
            ulong begin = BitConverter.ToUInt64(chunk, i);
            if (!MemoryReaderExtensions.IsPlausiblePointer(begin))
            {
                continue;
            }

            // Not the module. Nearly every object starts with a vtable, so leaving these in
            // would spend a good part of the budget reading the game's own code back.
            if (_reader.ModuleSize > 0
                && begin >= _reader.ModuleBase
                && begin < _reader.ModuleBase + _reader.ModuleSize)
            {
                continue;
            }

            // The second half of a pair can be in the NEXT chunk, and a list whose begin
            // lands in the last eight bytes of this one would then read as a lone pointer -
            // 1 KB instead of its own span, which is how a list gets missed at a seam. One
            // extra read per chunk boundary removes the seam; at the END of the region there
            // is nothing to look ahead into, and reading past it would make the result depend
            // on whatever the allocator put there.
            int take = LonePointerBytes;
            bool paired = i + (2 * sizeof(ulong)) <= length;
            if (paired || lookAhead)
            {
                ulong end = paired
                    ? BitConverter.ToUInt64(chunk, i + sizeof(ulong))
                    : _reader.TryRead(at + (ulong)i + sizeof(ulong), out ulong next) ? next : 0;

                if (end > begin && MemoryReaderExtensions.IsPlausiblePointer(end))
                {
                    ulong span = end - begin;

                    // Divisible by SOMETHING an element could be, or it is two unrelated
                    // pointers into one allocation rather than a list - which is most pairs.
                    if (span is >= 16 and <= 8 * 1024 * 1024
                        && (span % 4 == 0 || span % 12 == 0))
                    {
                        take = (int)Math.Min(span, ListBytes);
                    }
                }
            }

            into.Add(new FollowTarget(at + (ulong)i, begin, take));
        }
    }

    /// <summary>
    /// Reads as much of a chunk as is actually mapped, and says how much that was.
    /// </summary>
    /// <remarks>
    /// Halving rather than giving up, which is what PreloadReader's sweep does for the same
    /// reason: ReadProcessMemory refuses the WHOLE range when any byte of it is unmapped, so
    /// a region that ends mid-page reads as nothing at all. It also matters off the game
    /// entirely - a replayed recording holds only the bytes that were captured, and demanding
    /// a full page from one would report an empty sweep over data that is right there.
    /// </remarks>
    private int TakeChunk(ulong address, Span<byte> destination, int want)
    {
        while (want >= sizeof(uint) && !_reader.TryRead(address, destination[..want]))
        {
            want /= 2;
        }

        return want < sizeof(uint) ? 0 : want;
    }

    /// <summary>
    /// The table's own three arrays, so a whole-address-space scan can leave them alone.
    /// </summary>
    /// <remarks>
    /// Every one of them is full of references to the table BY CONSTRUCTION - the rows point
    /// at their Id strings, the by-Id index holds one entry per row and the by-HASH32 index
    /// another. Scanning them reports 11,434 sightings that say nothing, and would exhaust the
    /// cap before the scan reached memory that might have something to say.
    /// </remarks>
    private IReadOnlyList<MemoryRegion> TableStorage(ulong table, DatTableFacts facts)
    {
        var ranges = new List<MemoryRegion>
        {
            new(facts.RowsBegin, (ulong)(facts.Rows * facts.RowSize)),
        };

        if (_tables is not { } shape)
        {
            return ranges;
        }

        ulong store = _reader.ReadPointer(table + (ulong)shape.RowStoreOffset);
        foreach (int at in (int[])[shape.ByIdIndexOffset, shape.ByIdIndexOffset + 0x38])
        {
            ulong begin = _reader.ReadPointer(store + (ulong)at);
            ulong end = _reader.ReadPointer(store + (ulong)at + sizeof(ulong));
            if (begin != 0 && end > begin)
            {
                ranges.Add(new MemoryRegion(begin, end - begin));
            }
        }

        return ranges;
    }

    /// <summary>Reads one row's Id, which only matters for the rows that matched.</summary>
    private string IdOf(ulong rowsBegin, int rowSize, int row)
    {
        int idAt = _schema.Structs["QuestFlagsRow"].OffsetOf("Id");
        ulong text = _reader.ReadPointer(rowsBegin + (ulong)(row * rowSize) + (ulong)idAt);
        return _reader.ReadUnicodeString(text, 128);
    }

    /// <summary>
    /// Runs the whole hunt: find the table, then sweep the places a character's flags could be.
    /// </summary>
    /// <remarks>
    /// THE REGIONS ARE GUESSES AND THAT IS FINE, because the sweep is not the only thing that
    /// happens here - reading them is what puts them in a recording. ServerData first and
    /// biggest: it is the blob the server sends about this character, it is where the league
    /// name and every inventory already live, and quest state is server-authoritative.
    /// Neither reference decodes anything of it beyond inventories, so there is nothing to
    /// copy and this is new ground.
    /// </remarks>
    public QuestFlagHuntResult Run(ulong gameStatesStatic, int serverDataBytes = 1024 * 1024, bool scanHeap = false)
    {
        if (_tables is null)
        {
            return new QuestFlagHuntResult(0, string.Empty, 0, [], "the schema does not describe dat tables");
        }

        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (chain.AreaInstance == 0)
        {
            return new QuestFlagHuntResult(0, string.Empty, 0, [], "not in game");
        }

        ulong table = FindTableViaNpc(chain.AreaInstance);
        DatTableFacts? facts = table == 0 ? null : PointerPeek.DescribeTable(_reader, table, _tables.Value);
        Dictionary<uint, int> hashes = table == 0 ? [] : HashesIn(table);

        StructDef ai = _schema.Structs["AreaInstance"];
        ulong serverData = _reader.ReadPointer(
            chain.AreaInstance + (ulong)ai.OffsetOf("PlayerInfo")
            + (ulong)_schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr"));

        var regions = new List<(string Name, ulong Address, int Bytes)>
        {
            ("ServerData", serverData, serverDataBytes),
            ("InGameState", chain.InGameState, 32 * 1024),
            ("AreaInstance", chain.AreaInstance, 32 * 1024),
            ("PlayerEntity", chain.PlayerEntity, 8 * 1024),
        };

        // EVERY game state, not just the one being played. A blind spot worth closing before
        // the search widens any further: the tool has only ever looked at InGameState, and a
        // character's quest flags are exactly the kind of thing that would live on a state
        // that OUTLASTS the area - the login or character-select object, which is still in
        // that array while you play. Cheap, since the array is thirteen pointers.
        StructDef gs = _schema.Structs["GameState"];
        ulong states = _reader.ReadPointer(
            _reader.ReadPointer(gameStatesStatic) + (ulong)gs.OffsetOf("States"));
        for (int i = 0; states != 0 && i < gs.Constants["TotalStates"]; i++)
        {
            ulong state = _reader.ReadPointer(states + (ulong)(i * gs.Constants["StateEntrySize"]));
            if (state != 0 && state != chain.InGameState)
            {
                regions.Add(($"GameState[{i}]", state, 16 * 1024));
            }
        }

        var swept = new List<SweptRegion>();
        foreach ((string name, ulong address, int bytes) in regions)
        {
            if (address != 0)
            {
                swept.Add(Sweep(
                    name,
                    address,
                    bytes,
                    hashes,
                    facts?.RowsBegin ?? 0,
                    facts?.Rows ?? 0,
                    (int)(facts?.RowSize ?? 0)));
            }
        }

        var visited = new HashSet<ulong>();
        long spent = 0;

        // WHAT INGAMESTATE POINTS AT IS STRUCTURE, NOT DATA, and a kilobyte is the wrong window
        // for it. Two recordings of the same game - one character each, switched without
        // restarting - differ in 111 bytes of this object, and twelve of those are pointers
        // that lead somewhere else for the other character. Only one of the twelve was ever
        // read properly (AreaInstance); the rest got a kilobyte apiece as ordinary lone
        // pointers, which would miss anything past their first page. So its direct targets get
        // a real window before the general follow starts, and the follow skips them afterwards
        // because they are already visited.
        //
        // Honest about what the diff proves: those two recordings differ in AREA as well as in
        // character, so a pointer that changed is character-scoped OR area-scoped. Either way
        // it belongs to something that is not the same twice, which is what a set of quest
        // flags is.
        var structural = new List<SweptRegion>();
        foreach (SweptRegion region in swept.Where(r => r.Name is "InGameState" or "AreaInstance").ToList())
        {
            foreach (FollowTarget target in region.Targets)
            {
                if (spent >= FollowBudget || !visited.Add(target.Begin))
                {
                    continue;
                }

                spent += StateObjectBytes;
                structural.Add(Sweep(
                    $"{region.Name}+0x{target.At - region.Address:X}",
                    target.Begin,
                    StateObjectBytes,
                    hashes,
                    facts?.RowsBegin ?? 0,
                    facts?.Rows ?? 0,
                    (int)(facts?.RowSize ?? 0)));
            }
        }

        // Then ONE hop of the ordinary kind, and no more. A second hop was tried and dropped:
        // it ended both of its runs with 672,439 and 964,026 targets still queued, which is not
        // a budget that needs raising but a search that does not converge - and in 130 MB it
        // never produced a single sighting. EVERY hit this hunt has ever had came from a
        // targeted read: the area markers from a list one hop out of ServerData, and every
        // quest-flag condition from the structural pass above.
        var sources = new List<SweptRegion>(swept);
        sources.AddRange(structural);
        swept.AddRange(structural.Where(r => r.Sightings.Count > 0));

        int skipped = 0;
        List<SweptRegion> first = Follow(sources, hashes, facts, visited, ref spent, listsOnly: false, ref skipped);
        swept.AddRange(first.Where(r => r.Sightings.Count > 0));
        int followed = visited.Count;
        long followedBytes = spent;

        // THE WHOLE ADDRESS SPACE, when asked. Everything above walks pointers from a root and
        // can only find what hangs off one; this does not, which is the one thing six sessions
        // of pointer-walking could never rule out. Left opt-in because it reads gigabytes.
        if (scanHeap && facts is { } known && _reader is IMemoryRegions space)
        {
            HeapScanResult scan = HeapScan.Run(
                _reader,
                space,
                known.RowsBegin,
                known.Rows,
                (int)known.RowSize,
                hashes,
                TableStorage(table, known),
                anchor: known.RowsBegin);

            LastHeapScan = scan;
            foreach (IGrouping<ulong, RowSighting> group in scan.Sightings.GroupBy(s => s.At >> 20))
            {
                List<FlagSighting> found = [.. group
                    .DistinctBy(g => g.Row)
                    .Select(g => new FlagSighting(
                        g.Row, IdOf(known.RowsBegin, (int)known.RowSize, g.Row), g.At, g.ByPointer))];

                swept.Add(new SweptRegion($"heap 0x{group.Key << 20:X}", group.Key << 20, 0, 0, found, []));
            }
        }

        return new QuestFlagHuntResult(
            table,
            facts?.Path ?? string.Empty,
            hashes.Count,
            swept,
            table == 0
                ? "no NPC in this area carried a QuestFlags reference - the regions were still read, "
                    + "so a --record session can be swept offline once the table is known"
                : string.Empty,
            followed,
            followedBytes,
            skipped);
    }

    /// <summary>
    /// Reads everything the first pass pointed at, and sweeps that too.
    /// </summary>
    /// <remarks>
    /// One level deep, deliberately. Following what THESE turn up would walk the heap, and the
    /// point is not to search all of memory - it is to look inside the containers a character's
    /// state object holds, which is one hop by construction.
    ///
    /// Targets are deduplicated by where they POINT, not by where the pointer was: the same
    /// list is reachable from several fields, and reading it once per field would spend the
    /// budget on one allocation. The field is still what gets named in the report, because a
    /// hit is only useful as an offset somebody can write into the schema.
    /// </remarks>
    /// <summary>
    /// Reads everything a set of swept regions pointed at, and sweeps that too.
    /// </summary>
    /// <remarks>
    /// Targets are deduplicated by where they POINT, not by where the pointer was: the same
    /// list is reachable from several fields, and reading it once per field would spend the
    /// budget on one allocation. The field is still what gets NAMED, because a hit is only
    /// useful as an offset somebody can write into the schema - ServerData+0x2248 is how the
    /// area markers got theirs.
    ///
    /// <paramref name="listsOnly"/> is what keeps a second hop affordable. Everything is worth
    /// following once; at the next depth only the things shaped like lists are, or the walk
    /// turns into a heap dump for the sake of a kilobyte apiece.
    /// </remarks>
    private List<SweptRegion> Follow(
        IReadOnlyList<SweptRegion> from,
        IReadOnlyDictionary<uint, int> hashes,
        DatTableFacts? facts,
        HashSet<ulong> visited,
        ref long spent,
        bool listsOnly,
        ref int skipped)
    {
        var swept = new List<SweptRegion>();

        foreach (SweptRegion region in from)
        {
            foreach (FollowTarget target in region.Targets)
            {
                if (spent >= FollowBudget)
                {
                    // Counted rather than ignored: the number of targets the budget cost is
                    // exactly what says whether "nothing found" is an answer or a timeout.
                    skipped++;
                    continue;
                }

                if ((listsOnly && target.Bytes == LonePointerBytes) || !visited.Add(target.Begin))
                {
                    continue;
                }

                spent += target.Bytes;
                swept.Add(Sweep(
                    $"{region.Name}+0x{target.At - region.Address:X}",
                    target.Begin,
                    target.Bytes,
                    hashes,
                    facts?.RowsBegin ?? 0,
                    facts?.Rows ?? 0,
                    (int)(facts?.RowSize ?? 0)));
            }
        }

        return swept;
    }

    /// <summary>Prints the hunt for a person.</summary>
    public void Report(QuestFlagHuntResult result, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("Quest flags");
        if (result.Table != 0)
        {
            output.WriteLine($"  table    0x{result.Table:X} \"{result.Path}\", {result.Rows} rows");
        }

        if (result.Note.Length > 0)
        {
            output.WriteLine($"  note     {result.Note}");
        }

        if (LastHeapScan is { } scan)
        {
            // POINTERS AND HASHES COUNTED APART, because summing them says the opposite of the
            // truth. A run reported "1790 references" and every one was a 32-bit coincidence -
            // at a rate chance predicts within a factor of two - while the pointer count, the
            // only kind that cannot happen by accident, was zero. One number hid both facts.
            int byPointer = scan.Sightings.Count(s => s.ByPointer);
            output.WriteLine(
                $"  heap scan {scan.RegionsWalked} regions, {scan.BytesScanned / 1024 / 1024} MB, "
                + $"{byPointer} pointers and {scan.Sightings.Count - byPointer} hash matches"
                + (scan.Truncated
                    ? scan.HitSightingCap
                        ? $" - STOPPED AT THE SIGHTING CAP inside {scan.Reach / 1024 / 1024} MB of the table, so there are more"
                        : $" - BUDGET SPENT: everything within {scan.Reach / 1024 / 1024} MB of the table was covered, beyond that nothing"
                    : " - the whole address space"));
        }

        if (result.Followed > 0)
        {
            // Whether the budget ran out MATTERS, and saying nothing about it is how a partial
            // sweep gets read as an exhaustive one. 13 MB of a real session came back empty and
            // the honest description of that is "empty as far as it got".
            string spent = result.SkippedTargets > 0
                ? $" - THE BUDGET RAN OUT with {result.SkippedTargets} targets left, so this is as far as the sweep got"
                : " - the sweep FINISHED, so what is not listed is not there";

            output.WriteLine(
                $"  followed {result.Followed} pointers out of those regions, "
                + $"{result.FollowedBytes / 1024} KB{spent}");
        }

        foreach (SweptRegion region in result.Regions)
        {
            output.WriteLine(
                $"  {region.Name,-13} 0x{region.Address:X}  {region.Read}/{region.Wanted} bytes read, "
                + $"{region.Sightings.Count} flags");

            foreach (FlagSighting sighting in region.Sightings.Take(20))
            {
                output.WriteLine(
                    $"      +0x{sighting.At - region.Address:X6} row {sighting.Row} "
                    + $"{(sighting.ByPointer ? "pointer" : "hash")}  {sighting.Id}");
            }
        }

        if (!result.Sightings.Any())
        {
            // Said plainly, because a silent nothing reads as a broken sweep. It is not: the
            // regions are guesses, and the recording is the deliverable when they are wrong.
            output.WriteLine("  nothing matched - the flags a character has set are not in these regions");
        }
    }
}
