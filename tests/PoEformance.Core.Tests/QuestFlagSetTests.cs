using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The set of quest flags a character has, read out of the session that found it.
/// </summary>
/// <remarks>
/// SIX SESSIONS AND ABOUT 130 MB went looking for this as a reference - pointers to QuestFlags
/// rows, their HASH32 columns, their Id strings, index entries. The set holds none of those.
/// It is a sparse bitset over ROW NUMBERS, nine bytes per record: a u8 chunk index and 64 bits.
/// Nothing that searches for references can find a structure that stores none.
///
/// And the differential missed it for a second, separate reason, which these tests pin down
/// because it is the part that will catch someone again: the vector is exact-sized, so adding
/// a chunk REALLOCATES it. Setting a flag in a new chunk flips no bit in place anywhere - the
/// whole set moves and the old copy stays behind unchanged.
///
/// The fixture is 62 seconds of --peekwatch over the set while playing. Both mechanisms are in
/// it: one flag arrived as a new chunk, another was set inside a chunk that already existed.
/// </remarks>
public class QuestFlagSetTests
{
    /// <summary>The session that found it: 682 samples over 62 seconds, pid 16028.</summary>
    private const string SetFixture = "session-2026-08-questflags-set.rec";

    /// <summary>An older session, different character, that read the QuestFlags table itself.</summary>
    private const string TableFixture = "session-2026-08-questflags.rec";

    /// <summary>
    /// ServerData for the session in the set fixture, off the recorded chain.
    /// </summary>
    /// <remarks>
    /// The VALUE at AreaInstance+0x5A0 (0x314F6EEA800 + 0x5A0), not that address. Getting the
    /// two the wrong way round is the mistake the LocalPlayerStruct comment already records
    /// once, and it costs a chain that resolves to plausible-looking nothing.
    /// </remarks>
    private const ulong ServerData = 0x31517A3F000;

    /// <summary>Where the by-Id index of QuestFlags sits in the table fixture.</summary>
    private const ulong ByIdIndex = 0x41ED84A0010;

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")).AsOf(RealSessionTests.PrePatchEra);
    }

    [Fact]
    public void TheChainOffServerDataReachesASetOfRows()
    {
        using ReplayMemoryReader reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));

        IReadOnlyList<int> rows = QuestFlagSet.Rows(reader, Schema(), ServerData);

        Assert.NotEmpty(rows);
        Assert.Equal(rows.OrderBy(r => r), rows);                       // chunks are sorted
        Assert.All(rows, row => Assert.InRange(row, 0, 5716));          // inside QuestFlags.dat
    }

    [Fact]
    public void TheRowsAreRealQuestFlagsAndTheTableSaysTheirNames()
    {
        // THE PROOF, and it takes two recordings because no single one holds both halves: the
        // set comes from the session that found it, the names from an older session of a
        // different character that read the table. Row N is row N in both - a dat table is
        // static content, identical for every character on the patch.
        using ReplayMemoryReader set = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));
        using ReplayMemoryReader table = ReplayMemoryReader.Load(File.OpenRead(Fixture(TableFixture)));

        IReadOnlyList<int> rows = QuestFlagSet.Rows(set, Schema(), ServerData);
        IReadOnlyList<SetQuestFlag> named = QuestFlagSet.Named(table, rows, ByIdIndex);

        Dictionary<int, string> byRow = named
            .Where(flag => flag.Id.Length > 0)
            .ToDictionary(flag => flag.Row, flag => flag.Id);

        // Three names is all the older recording happens to hold for these rows - it read the
        // index entries it needed, not all 5717 - and three is plenty: every one is a
        // per-character PROGRESS flag, which is the thing the hunt said was not in memory.
        Assert.Equal("CompletedSkillGemTutorial", byRow[11]);
        Assert.Equal("CompletedLifeFlaskTutorial", byRow[22]);
        Assert.Equal("EnteredHideout", byRow[46]);
    }

    [Fact]
    public void EveryRecordIsNineBytesAndTheChunkIndexOnlyGoesUp()
    {
        // Read straight from the bytes rather than through the reader above, so the layout is
        // asserted rather than assumed by the thing under test.
        using ReplayMemoryReader reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));
        OffsetSchema schema = Schema();

        ulong hop = reader.ReadPointer(ServerData + (ulong)schema.Structs["ServerData"].OffsetOf("QuestFlagOwnerPtr"));
        ulong flags = reader.ReadPointer(hop + (ulong)schema.Structs["QuestFlagOwner"].OffsetOf("QuestFlagSetPtr"));
        ulong chunks = flags + (ulong)schema.Structs["QuestFlagSet"].OffsetOf("Chunks");

        ulong begin = reader.ReadPointer(chunks);
        ulong end = reader.ReadPointer(chunks + 8);

        Assert.NotEqual(0UL, begin);
        Assert.Equal(0UL, (end - begin) % 9);

        var indices = new List<int>();
        for (ulong at = begin; at + 9 <= end && reader.TryRead(at, out byte chunk); at += 9)
        {
            indices.Add(chunk);
        }

        Assert.NotEmpty(indices);
        Assert.Equal(indices.OrderBy(i => i).Distinct(), indices);
    }

    [Fact]
    public void TheSetOnlyEverGainsBits()
    {
        // A progress set is monotonic, and that is a check the game itself settles: over 682
        // samples of real play, no bit is ever cleared. A drifted offset landing on some other
        // vector would not survive this.
        using ReplayMemoryReader reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));
        OffsetSchema schema = Schema();

        var previous = new HashSet<int>();
        var everGrew = false;

        for (uint frame = 0; frame < reader.FrameCount; frame++)
        {
            reader.Seek(frame);
            var now = QuestFlagSet.Rows(reader, schema, ServerData).ToHashSet();
            if (now.Count == 0)
            {
                continue;   // before the first read of the chain
            }

            // Only what the recording can still SERVE counts: the buffer moves on every insert
            // and the replay holds the first 128 bytes of each, so a row whose chunk fell out
            // of the recorded window is missing from the reading, not cleared in the game.
            Assert.All(previous.Where(row => row < ReadableRows), row => Assert.Contains(row, now));
            everGrew |= now.Count > previous.Count;
            previous = now;
        }

        Assert.True(everGrew, "the set never grew - the fixture should contain flags being set");
    }

    /// <summary>
    /// Rows inside the part of every buffer the recording actually holds.
    /// </summary>
    /// <remarks>
    /// The peek read 128 bytes of each buffer, which is 14 of its 25-30 records, and the
    /// highest chunk in that window is 0x2B. Beyond it the recording has nothing, and treating
    /// silence as "the flag was cleared" would fail this test for a reason that is about the
    /// recording rather than about the game.
    /// </remarks>
    private const int ReadableRows = 0x2B * 64;

    [Fact]
    public void FlagsWereWatchedBeingSetBothWays()
    {
        // The two mechanisms, which is what explains the failed differential: row 644 arrives
        // as a NEW chunk (0x0A), so the whole vector reallocates and nothing flips in place;
        // row 1771 is set INSIDE chunk 0x1B, which already existed, so that one does flip.
        using ReplayMemoryReader reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));
        OffsetSchema schema = Schema();

        reader.Seek(2);
        var before = QuestFlagSet.Rows(reader, schema, ServerData).ToHashSet();
        reader.Seek((uint)(reader.FrameCount - 1));
        var after = QuestFlagSet.Rows(reader, schema, ServerData).ToHashSet();

        Assert.DoesNotContain(644, before);
        Assert.Contains(644, after);
        Assert.Equal(0x0A, 644 / 64);

        Assert.DoesNotContain(1771, before);
        Assert.Contains(1771, after);
        Assert.Equal(0x1B, 1771 / 64);
        Assert.Contains(1759, before);           // chunk 0x1B already held bits 31/32/44/46
    }

    [Fact]
    public void AGarbageChainReadsAsEmptyRatherThanAsFlags()
    {
        using ReplayMemoryReader reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(SetFixture)));

        Assert.Empty(QuestFlagSet.Rows(reader, Schema(), 0));
        Assert.Empty(QuestFlagSet.Rows(reader, Schema(), 0xDEAD_0000_0000));
    }
}
