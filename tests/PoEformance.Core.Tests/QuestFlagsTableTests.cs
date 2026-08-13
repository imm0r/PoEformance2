using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// The in-memory QuestFlags.dat table, against the session it was found in
/// (<c>tests/fixtures/session-2026-08-questflags.rec</c>, 191 KB).
/// </summary>
/// <remarks>
/// WHAT THIS PROTECTS. Three numbers in the schema were derived rather than looked up, and
/// each of them is one arithmetic slip away from being silently wrong: the dat column widths
/// (which give NPCs a 0xBF row and QuestFlags a 0x0C one), the half-order of a 16-byte
/// foreign reference (row first, table second - this fixture is what reversed that rule),
/// and DatRowStore's containers being FOUR pointers rather than a std::vector's three. None
/// of those has a test anywhere else, because nothing on the drift report's chain is a dat
/// row.
///
/// The addresses are hard-coded, which is fine and is not the same thing as guessing: a
/// recording is a frozen process, so its addresses are as fixed as its bytes. The table is
/// not reachable from a static in this session - it was reached in the dissector from an
/// NPC standing in a hideout - so the walk starts where the person looking started.
/// </remarks>
public class QuestFlagsTableTests
{
    /// <summary>The NPC component of Metadata/NPC/Hideout/VaalTimeScientist ("Zolin").</summary>
    private const ulong NpcComponent = 0x41ECA2B3AB0UL;

    /// <summary>Its NPCs.dat row, and the row 0xBF further on (BloodPriestFemale, "Zelina").</summary>
    private const ulong NpcsRow = 0x41EC21DCD26UL;
    private const ulong NextNpcsRow = 0x41EC21DCDE5UL;

    /// <summary>How many entries of each index this recording actually holds.</summary>
    private const int RecordedIdEntries = 64;
    private const int RecordedHashEntries = 25;

    /// <summary>
    /// A foreign reference in the wild: a row of another table pointing at the flag
    /// EnteredHideout, with the QuestFlags table in the eight bytes after it.
    /// </summary>
    private const ulong ForeignReference = 0x41EB2A11F8CUL;

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "fixtures", "session-2026-08-questflags.rec");
        }
    }

    private static ReplayMemoryReader LoadSession() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    private static OffsetSchema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    [Fact]
    public void NpcComponent_LeadsToItsOwnNpcsRow()
    {
        // The chain the finding came off: component -> shared data -> the dat row. What makes
        // one sighting worth trusting is that the row names the entity it was reached from.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();

        ulong data = replay.ReadPointer(NpcComponent + (ulong)schema.Structs["Npc"].OffsetOf("NpcDataPtr"));
        ulong row = replay.ReadPointer(data + (ulong)schema.Structs["NpcData"].OffsetOf("NpcsRowPtr"));
        Assert.Equal(NpcsRow, row);

        StructDef npcs = schema.Structs["NpcsRow"];
        Assert.Equal(
            "Metadata/NPC/Hideout/VaalTimeScientist",
            replay.ReadUnicodeString(replay.ReadPointer(row + (ulong)npcs.OffsetOf("Id"))));
        Assert.Equal("Zolin", replay.ReadUnicodeString(replay.ReadPointer(row + (ulong)npcs.OffsetOf("Name"))));

        // Id and Metadata are the SAME pointer, not two copies of one string; same for
        // Name and ShortName. Worth asserting because it is what a wrong row size would
        // break first, and because it is free.
        Assert.Equal(
            replay.ReadPointer(row + (ulong)npcs.OffsetOf("Id")),
            replay.ReadPointer(row + (ulong)npcs.OffsetOf("Metadata")));
        Assert.Equal(
            replay.ReadPointer(row + (ulong)npcs.OffsetOf("Name")),
            replay.ReadPointer(row + (ulong)npcs.OffsetOf("ShortName")));
    }

    [Fact]
    public void NpcsRows_LieOnTheComputedRowSize()
    {
        // The stride check the column-width table lives or dies by, on a third table after
        // MinimapIcons (0x26) and WorldAreas (0x300). 18 columns compute to 0xBF, and the
        // next row starts exactly 0xBF on - at an ODD address, because rows are packed.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        StructDef npcs = schema.Structs["NpcsRow"];

        Assert.Equal(npcs.Constants["RowSize"], (long)(NextNpcsRow - NpcsRow));
        Assert.Equal(
            "Metadata/NPC/Hideout/BloodPriestFemale",
            replay.ReadUnicodeString(replay.ReadPointer(NextNpcsRow + (ulong)npcs.OffsetOf("Id"))));
        Assert.Equal(
            "Zelina",
            replay.ReadUnicodeString(replay.ReadPointer(NextNpcsRow + (ulong)npcs.OffsetOf("Name"))));
    }

    [Fact]
    public void ForeignReference_HoldsTheRowFirstAndTheTableSecond()
    {
        // THE ASSERTION THAT REVERSED THE RULE. The column start holds something that lands
        // exactly on the QuestFlags row grid; +8 holds something that starts with a module
        // pointer, which is a vtable and therefore an engine object. Neither could be the
        // other way round.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        StructDef npcs = schema.Structs["NpcsRow"];

        ulong flagRow = replay.ReadPointer(NpcsRow + (ulong)npcs.OffsetOf("QuestFlagsRowPtr"));
        ulong table = replay.ReadPointer(NpcsRow + (ulong)npcs.OffsetOf("QuestFlagsTablePtr"));

        ulong store = replay.ReadPointer(table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr"));
        ulong rows = replay.ReadPointer(store + (ulong)schema.Structs["DatRowStore"].OffsetOf("Rows"));

        long rowSize = schema.Structs["QuestFlagsRow"].Constants["RowSize"];
        Assert.True(flagRow > rows, $"0x{flagRow:X} is not inside the rows array at 0x{rows:X}");
        Assert.Equal(0UL, (flagRow - rows) % (ulong)rowSize);
        Assert.Equal(2170UL, (flagRow - rows) / (ulong)rowSize);

        // The table half: a vtable, i.e. an address inside the game's own module.
        ulong vtable = replay.ReadPointer(table);
        Assert.InRange(vtable, replay.ModuleBase, replay.ModuleBase + replay.ModuleSize);
    }

    [Fact]
    public void DatTable_NamesItself()
    {
        // The table settles its own identity - and the prediction that came before it is worth
        // keeping in the same test. "Data/Balance/QuestFlags.dat" is 27 characters, and of the
        // 26 PoE2 tables whose columns compute to a 12-byte row it is the only one with a path
        // that long, so the LENGTH alone named the table before the text was ever followed.
        // That is the part that generalises: the next unnamed table may not have its characters
        // in the recording, and two ints beside a pointer are cheap.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        ulong table = replay.ReadPointer(NpcsRow + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));
        ulong path = table + (ulong)schema.Structs["DatTable"].OffsetOf("Path");

        Assert.Equal("Data/Balance/QuestFlags.dat", replay.ReadStdWString(path));

        // std::wstring keeps its size 0x10 into itself - the same layout ReadStdWString reads.
        Assert.Equal(27L, replay.Read<long>(path + 0x10));
        Assert.Equal(31L, replay.Read<long>(path + 0x18));
    }

    [Fact]
    public void PeekingTheTable_NamesItAndSizesItsRows()
    {
        // What the dissector now shows on that pointer instead of a hex preview. The row size
        // is not looked up anywhere: the by-Id index has a fixed 0x18 entry per row, so the
        // table divides out its own 0x0C.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        DatTableShape? shape = DatTableShape.From(schema);
        Assert.NotNull(shape);

        ulong table = replay.ReadPointer(NpcsRow + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));
        PeekResult found = PointerPeek.Peek(replay, table, shape, 0);

        Assert.Equal(TargetKind.DatTable, found.Kind);
        Assert.Equal("dat table \"Data/Balance/QuestFlags.dat\", 5717 rows of 0xC", found.Summary);
    }

    [Fact]
    public void PeekingAForeignReference_NumbersTheRow()
    {
        // A real foreign reference in this session, in a row of some other table: the flag
        // EnteredHideout followed by the table it is a row of. Before, this peeked as
        // "dat row, Id EnteredHideout" and which table it belonged to was unanswerable from
        // the row - the pointer beside it is what answers it.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        DatTableShape? shape = DatTableShape.From(schema);

        ulong row = replay.ReadPointer(ForeignReference);
        ulong table = replay.ReadPointer(ForeignReference + 8);
        PeekResult found = PointerPeek.Peek(replay, row, shape, table);

        Assert.Equal(TargetKind.DatRow, found.Kind);
        Assert.Equal("QuestFlags row 46 of 5717, Id \"EnteredHideout\"", found.Summary);

        // Without the neighbour it falls back to what it always said.
        Assert.Equal("dat row, Id \"EnteredHideout\"", PointerPeek.Peek(replay, row, shape, 0).Summary);
    }

    [Fact]
    public void QuestFlagsTable_HoldsEveryFlagTheClientKnows()
    {
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        StructDef store = schema.Structs["DatRowStore"];
        StructDef flag = schema.Structs["QuestFlagsRow"];

        ulong table = replay.ReadPointer(NpcsRow + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));
        ulong rowStore = replay.ReadPointer(table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr"));

        ulong rows = replay.ReadPointer(rowStore + (ulong)store.OffsetOf("Rows"));
        ulong rowsEnd = replay.ReadPointer(rowStore + (ulong)store.OffsetOf("Rows") + 8);
        long count = (long)(rowsEnd - rows) / flag.Constants["RowSize"];
        Assert.Equal(5717L, count);

        // The first row, which is also the first entry of both indices below.
        Assert.Equal(
            "a1q1-KilledHillock",
            replay.ReadUnicodeString(replay.ReadPointer(rows + (ulong)flag.OffsetOf("Id"))));
        Assert.Equal(0x5085C604U, replay.Read<uint>(rows + (ulong)flag.OffsetOf("Hash32")));

        // Both indices are dense - one entry per row, in row order - which is what says they
        // are not bucket arrays however much the load factor beside them suggests one.
        (string, string)[] indices =
        [
            ("ByIdIndex", "ByIdEntrySize"),
            ("ByHashIndex", "ByHashEntrySize"),
        ];

        foreach ((string index, string entrySize) in indices)
        {
            ulong begin = replay.ReadPointer(rowStore + (ulong)store.OffsetOf(index));
            ulong end = replay.ReadPointer(rowStore + (ulong)store.OffsetOf(index) + 8);
            Assert.Equal(count, (long)(end - begin) / store.Constants[entrySize]);
        }

        Assert.Equal(8192L, replay.Read<long>(rowStore + (ulong)store.OffsetOf("ByIdCapacity")));
        Assert.Equal(6553L, replay.Read<long>(rowStore + (ulong)store.OffsetOf("ByIdGrowAt")));
        Assert.Equal(0.8f, replay.Read<float>(rowStore + (ulong)store.OffsetOf("ByIdLoadFactor")));
        Assert.Equal(8192L, replay.Read<long>(rowStore + (ulong)store.OffsetOf("ByHashCapacity")));
        Assert.Equal(0.8f, replay.Read<float>(rowStore + (ulong)store.OffsetOf("ByHashLoadFactor")));
    }

    [Fact]
    public void BothIndices_PointBackAtTheRowTheyDescribe()
    {
        // Every entry the recording holds has to agree with the row at the same index. An
        // index entry pointing anywhere else would mean the entry size is wrong, which is the
        // one thing this struct can get wrong without looking wrong. The two counts differ
        // because a recording only holds what the tool actually read: the dissector's window
        // sat on the by-Id index and took 1536 bytes of it (64 entries), and reached the
        // by-HASH32 index only through a peek, which fetched 400 (25).
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        StructDef store = schema.Structs["DatRowStore"];
        StructDef flag = schema.Structs["QuestFlagsRow"];

        ulong table = replay.ReadPointer(NpcsRow + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));
        ulong rowStore = replay.ReadPointer(table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr"));
        ulong rows = replay.ReadPointer(rowStore + (ulong)store.OffsetOf("Rows"));
        ulong byId = replay.ReadPointer(rowStore + (ulong)store.OffsetOf("ByIdIndex"));
        ulong byHash = replay.ReadPointer(rowStore + (ulong)store.OffsetOf("ByHashIndex"));

        long rowSize = flag.Constants["RowSize"];
        ulong previousText = 0;
        long previousLength = 0;

        for (int i = 0; i < RecordedIdEntries; i++)
        {
            ulong row = rows + (ulong)(i * rowSize);
            ulong text = replay.ReadPointer(row + (ulong)flag.OffsetOf("Id"));

            ulong entry = byId + (ulong)(i * store.Constants["ByIdEntrySize"]);
            Assert.Equal(text, replay.ReadPointer(entry));
            Assert.Equal(row, replay.ReadPointer(entry + 0x10));

            if (i < RecordedHashEntries)
            {
                ulong hashEntry = byHash + (ulong)(i * store.Constants["ByHashEntrySize"]);
                Assert.Equal(replay.Read<uint>(row + (ulong)flag.OffsetOf("Hash32")), replay.Read<uint>(hashEntry));
                Assert.Equal(row, replay.ReadPointer(hashEntry + 8));
            }

            // The Id blob is packed: each string is followed by four bytes of terminator and
            // padding, so the next one starts at length * 2 + 4. Holding across 63 gaps is
            // what says the length field is a character count and not a byte count.
            long length = replay.Read<long>(entry + 8);
            if (previousText != 0)
            {
                Assert.Equal(previousText + (ulong)((previousLength * 2) + 4), text);
            }

            previousText = text;
            previousLength = length;
        }
    }

    [Fact]
    public void QuestFlagsTable_CarriesNoCharacterState()
    {
        // The negative result, kept because it is the one that stops the next person looking
        // here for progress. A row is 12 bytes and both of them are accounted for - the name
        // and the file's own HASH32 - so there is no bit, count or timestamp in this table
        // saying whether a flag is SET. Whatever holds a character's flags is somewhere else.
        ReplayMemoryReader replay = LoadSession();
        OffsetSchema schema = LoadSchema();
        StructDef flag = schema.Structs["QuestFlagsRow"];

        Assert.Equal(0x0CL, flag.Constants["RowSize"]);
        Assert.Equal(0, flag.OffsetOf("Id"));
        Assert.Equal(8, flag.OffsetOf("Hash32"));

        // And the rows really are that dense: row 1's Id is a pointer, not the tail of
        // row 0's fields.
        ulong table = replay.ReadPointer(NpcsRow + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));
        ulong rowStore = replay.ReadPointer(table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr"));
        ulong rows = replay.ReadPointer(rowStore + (ulong)schema.Structs["DatRowStore"].OffsetOf("Rows"));

        Assert.Equal(
            "Act4HoodedMentorSummonedSolitaryConfinement",
            replay.ReadUnicodeString(replay.ReadPointer(rows + 0x0C)));
    }
}
