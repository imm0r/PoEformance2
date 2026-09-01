using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reaching one of the game's content tables from a static, which used to be impossible.
/// </summary>
/// <remarks>
/// WHAT THIS PROTECTS is a single claim: a dat table IS a loaded-file record. Everything the
/// route does rests on it - if the two were merely similar objects, walking the file table
/// would hand back file records with nothing at RowStorePtr and the glossary would read
/// gibberish out of whatever sat there.
///
/// The claim is checked against a recording rather than against this code's own idea of the
/// layout, and from both directions at once: the object an NPCs row calls "the table" is also
/// one of the records the FileRoot walk enumerates. The addresses are hard-coded, which is not
/// the same thing as guessing - a recording is a frozen process, so its addresses are as fixed
/// as its bytes.
///
/// WHAT NO FIXTURE CAN CHECK, and it is written here rather than left to be discovered: the
/// last hop. The preload walk reads a 32-byte string header at record+0x08 and stops one byte
/// short of RowStorePtr at +0x28, so no recording in this repo holds those bytes. That hop is
/// the same RowStorePtr already confirmed on the same kind of object from the MinimapIcon and
/// NPCs side, and on THIS route it is exercised synthetically below until somebody runs
/// `--glossary --record`.
/// </remarks>
public class LoadedDatTablesTests
{
    /// <summary>The QuestFlags table in session-2026-08-sweep.rec, as the file table holds it.</summary>
    private const ulong QuestFlagsTable = 0x3182_6080AD0UL;

    /// <summary>An NPCs row in the same session, whose fourth column is a QuestFlags reference.</summary>
    private const ulong NpcsRowWithAFlag = 0x3182_220FDBAUL;

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

    [Fact]
    public void ADatTableIsALoadedFileRecord()
    {
        using ReplayMemoryReader replay =
            ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-sweep.rec")));
        OffsetSchema schema = RealSessionTests.Schema();

        // From the row: the table half of a foreign reference, which is the only way this
        // project could name a table before.
        StructDef npcs = schema.Structs["NpcsRow"];
        ulong fromTheRow = replay.ReadPointer(NpcsRowWithAFlag + (ulong)npcs.OffsetOf("QuestFlagsTablePtr"));
        Assert.Equal(QuestFlagsTable, fromTheRow);

        StructDef table = schema.Structs["DatTable"];
        Assert.Equal(
            "Data/Balance/QuestFlags.dat",
            replay.ReadStdWString(QuestFlagsTable + (ulong)table.OffsetOf("Path")));

        // From the static: the same object, reached bucket -> slot -> record with nothing
        // assumed about what it is. THIS is the new part - the two sides meeting.
        var files = new PreloadReader(replay, schema);
        ulong fileRoot = replay.ResolvedStatics["FileRoot"];
        Assert.Contains(QuestFlagsTable, files.Records(fileRoot));

        // And the record calls it what the table calls it, because it is one field: the
        // std::wstring's size sits at Path+0x10, and 27 is the length of that path.
        Assert.Equal(27, replay.Read<int>(QuestFlagsTable + (ulong)table.OffsetOf("Path") + 0x10));
    }

    [Fact]
    public void TheWalkFindsATableByNameAndReadsItsRows()
    {
        // The whole route in synthetic memory, which is the only place the last hop can be
        // exercised at all - see the type comment. A wrong bucket stride, slot size or row
        // store offset all fail here as "found nothing", which is also what a wrong static
        // returns, so the assertions say WHICH.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.PlainFile("Metadata/Terrain/Leagues/Breach/BreachObject");
        game.Table("Data/Balance/KeywordPopups.dat", 0x48, Rows);

        var tables = new LoadedDatTables(game.Memory, schema);
        IReadOnlyList<LoadedDatTable> found = tables.Read(FakeDatTables.RootStatic);

        Assert.Equal(string.Empty, tables.LastError);
        LoadedDatTable only = Assert.Single(found);
        Assert.Equal("KeywordPopups", only.Facts.Name);
        Assert.Equal("Data/Balance/KeywordPopups.dat", only.Facts.Path);
        Assert.Equal(Rows.Length, only.Facts.Rows);

        // NOT from the schema: the table divides its own rows by its own by-Id index, so this
        // is the game's arithmetic and it is what the glossary refuses to proceed without.
        Assert.Equal(0x48, only.Facts.RowSize);

        KeywordGlossary glossary = KeywordGlossary.Read(tables, game.Memory, schema);
        Assert.Equal(string.Empty, glossary.LastError);
        Assert.Equal("Critical Hits", glossary.Lookup("Critical")?.Term);
        Assert.Equal("Jagged Ground", glossary.Lookup("JaggedGround")?.Term);
        Assert.Null(glossary.Lookup("NotAKeyword"));
    }

    [Fact]
    public void AFileThatIsNotATableIsNotOne()
    {
        // The check that makes the walk mean something. Eight thousand records go past and
        // ninety of them are tables; if "a record" were enough, the glossary would be read out
        // of a texture.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.PlainFile("Art/Textures/Interface2/2DArt/UIImages/InGame/HUD/CharmBarBg.dds");
        game.PlainFile("Data/Balance/LooksLikeATable.dat");

        var tables = new LoadedDatTables(game.Memory, schema);

        Assert.Empty(tables.Read(FakeDatTables.RootStatic));
        Assert.Equal(2, tables.RecordsWalked);
        Assert.Contains("none of them is a dat table", tables.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void ASurveyAlsoNamesTheDatFilesThatAreNotTables()
    {
        // The cheap walk cannot say why a table is missing, because it reads a record's name
        // only after the structural checks pass - so a record that fails leaves nothing behind
        // to identify it by. This is the walk that names them, and the distinction it draws is
        // the one that matters: a .dat file present but unparsed is a different finding from a
        // .dat file that is not in the table at all.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.PlainFile("Art/Textures/Interface2/2DArt/UIImages/InGame/HUD/CharmBarBg.dds");
        game.PlainFile("Data/Balance/NotParsedYet.dat");
        game.Table("Data/Balance/KeywordPopups.dat", 0x48, Rows);

        var tables = new LoadedDatTables(game.Memory, schema);
        DatTableSurvey survey = tables.Survey(FakeDatTables.RootStatic);

        Assert.Equal(3, survey.Records);
        Assert.Equal("KeywordPopups", Assert.Single(survey.Tables).Facts.Name);

        // The texture is not listed: it is not a .dat file, so it is not a missing table.
        Assert.Equal(["Data/Balance/NotParsedYet.dat"], survey.Refused);
    }

    [Fact]
    public void ATableWithTheWrongRowSizeIsRefusedRatherThanRead()
    {
        // A column added by a patch moves every field after it, and the failure has no symptom:
        // the strings simply come out wrong. The table reports its own row size, so this is
        // catchable, and it is caught - the glossary skips it and says what it saw.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.Table("Data/Balance/KeywordPopups.dat", 0x50, Rows);

        var tables = new LoadedDatTables(game.Memory, schema);
        tables.Read(FakeDatTables.RootStatic);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, game.Memory, schema);

        Assert.Empty(glossary.ById);
        Assert.Contains("0x50", glossary.LastError, StringComparison.Ordinal);
        Assert.Contains("not 0x48", glossary.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocalisedCopyOfATableIsAlsoFound()
    {
        // The game loads some tables twice, the second under a language-prefixed path
        // ("en:Data/Balance/ClientStrings.dat" is in session-2026-08-deployed.rec). Both are
        // records, both answer to the same table name, and a lookup that took the first match
        // blindly would depend on bucket order for which one it read.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.Table("Data/Balance/KeywordPopups.dat", 0x50, Rows);          // a copy we cannot read
        game.Table("en:Data/Balance/KeywordPopups.dat", 0x48, Rows);       // and the one we can

        var tables = new LoadedDatTables(game.Memory, schema);
        tables.Read(FakeDatTables.RootStatic);

        Assert.Equal(2, tables.FindAll("KeywordPopups").Count);

        // First that DECODES, not first that matches.
        KeywordGlossary glossary = KeywordGlossary.Read(tables, game.Memory, schema);
        Assert.Equal("en:Data/Balance/KeywordPopups.dat", glossary.Table?.Facts.Path);
        Assert.Equal("Critical Hits", glossary.Lookup("Critical")?.Term);
    }

    [Fact]
    public void ASeventeenthBucketWouldBeNoticed()
    {
        // THE PROBE FOR THE CONSTANT EVERY WALK OF THAT TABLE IS SCALED BY. If this game used
        // more than BucketCount buckets, every walk would silently cover a fraction of the table
        // - and the symptom is not an error, it is a file that "is not in the table". A recording
        // cannot settle it, because nothing has ever read past the last bucket; the game can, in
        // one read per slot, and did on 2026-09-01: the count holds.
        //
        // What this test says is only that the probe WORKS: it stays quiet on a table that ends
        // where the constant says, and speaks up on one that does not.
        OffsetSchema schema = RealSessionTests.Schema();
        var game = new FakeDatTables(schema);
        game.PlainFile("Data/Balance/Something.dat");

        var files = new PreloadReader(game.Memory, schema);
        var bucketCount = (int)schema.Structs["LoadedFilesRoot"].Constants["BucketCount"];

        Assert.Equal(0, files.BucketsBeyondTheCount(FakeDatTables.RootStatic));

        game.PlantBucketAt(bucketCount, slots: 4);
        Assert.Equal(1, files.BucketsBeyondTheCount(FakeDatTables.RootStatic));

        game.PlantBucketAt(bucketCount + 3, slots: 9);
        Assert.Equal(2, files.BucketsBeyondTheCount(FakeDatTables.RootStatic));
    }

    /// <summary>Three rows of the real table, as the dissector showed them.</summary>
    private static readonly (string Id, string Term, string Definition)[] Rows =
    [
        ("Critical", "Critical Hits", ""),
        ("CriticalDamageBonus", "Critical Damage Bonus",
            "Multiplies the damage dealt by [Critical|Critical Hits]. Default value is +100%%."),
        ("JaggedGround", "Jagged Ground",
            "Jagged Ground [Slow|Slows] the movement speed of enemies in its area by 20%%."),
    ];

    /// <summary>
    /// A process with a loaded-file table in it, and dat tables among the files.
    /// </summary>
    /// <remarks>
    /// Built from the schema's own offsets rather than from numbers written here, so a schema
    /// edit moves the test's memory and the reader together - which is the point: what is being
    /// checked is that the reader walks the layout the schema DESCRIBES, not that two copies of
    /// the same constant agree.
    /// </remarks>
    private sealed class FakeDatTables
    {
        internal const ulong RootStatic = 0x10_0000;
        private const ulong Root = 0x20_0000;

        private readonly OffsetSchema _schema;
        private readonly List<ulong> _records = [];
        private ulong _next = 0x100_0000;

        internal FakeDatTables(OffsetSchema schema)
        {
            _schema = schema;
            Memory.Place(RootStatic, Root);
        }

        internal FakeMemoryReader Memory { get; } = new();

        /// <summary>A loaded file that is not a table - most of them.</summary>
        internal void PlainFile(string path)
        {
            ulong record = Take(0x80);
            Memory.PlaceStdWString(record + (ulong)_schema.Structs["FileRecord"].OffsetOf("Name"), path, Take(512));
            Add(record);
        }

        /// <summary>A loaded .dat file, with the row store and the by-Id index behind it.</summary>
        internal void Table(string path, int rowSize, (string Id, string Term, string Definition)[] rows)
        {
            StructDef table = _schema.Structs["DatTable"];
            StructDef store = _schema.Structs["DatRowStore"];
            StructDef row = _schema.Structs["KeywordPopupsRow"];
            var entrySize = (int)store.Constants["ByIdEntrySize"];

            ulong record = Take(0x80);
            Memory.PlaceStdWString(record + (ulong)table.OffsetOf("Path"), path, Take(512));

            ulong rowsBegin = Take(rowSize * rows.Length);
            ulong idBegin = Take(entrySize * rows.Length);
            ulong rowStore = Take(0x80);

            Memory.Place(record + (ulong)table.OffsetOf("RowStorePtr"), rowStore);
            Memory.Place(rowStore + (ulong)store.OffsetOf("Rows"), rowsBegin);
            Memory.Place(rowStore + (ulong)store.OffsetOf("Rows") + 8, rowsBegin + (ulong)(rowSize * rows.Length));
            Memory.Place(rowStore + (ulong)store.OffsetOf("ByIdIndex"), idBegin);
            Memory.Place(rowStore + (ulong)store.OffsetOf("ByIdIndex") + 8, idBegin + (ulong)(entrySize * rows.Length));

            // What tells a table from two containers that happen to divide: the first by-Id
            // entry points at the first row.
            Memory.Place(idBegin + (ulong)store.Constants["ByIdEntryRowAt"], rowsBegin);

            for (int i = 0; i < rows.Length; i++)
            {
                ulong at = rowsBegin + (ulong)(i * rowSize);
                Place(at + (ulong)row.OffsetOf("Id"), rows[i].Id);
                Place(at + (ulong)row.OffsetOf("Term"), rows[i].Term);
                Place(at + (ulong)row.OffsetOf("Definition"), rows[i].Definition);
            }

            Add(record);
        }

        /// <summary>A raw UTF-16 pointer column, which is how a dat row holds a string.</summary>
        /// <remarks>
        /// PADDED, and the padding is not decoration. A string reader asks for its whole window
        /// and halves the request until one succeeds, so a string placed at exactly its own
        /// length reads back as however many characters fit in the largest power of two that
        /// lands inside it - "Critical Hits" comes out "Critical". Real memory is pages, not
        /// exact allocations, so this is the fake reader being stricter than a process; the
        /// zeros put it back.
        /// </remarks>
        private void Place(ulong column, string text)
        {
            const int Window = 1024;
            ulong at = Take(Window);
            Memory.Place(at, new byte[Window]);
            Memory.PlaceUtf16(at, text);
            Memory.Place(column, at);
        }

        /// <summary>Puts every record so far into bucket zero, and empties the rest.</summary>
        private void Add(ulong record)
        {
            _records.Add(record);

            StructDef bucket = _schema.Structs["LoadedFilesBucket"];
            var slotSize = (int)_schema.Structs["FileRecordSlot"].Constants["Size"];
            int slotRecord = _schema.Structs["FileRecordSlot"].OffsetOf("Record");
            var bucketSize = (int)_schema.Structs["LoadedFilesRoot"].Constants["BucketSize"];
            var bucketCount = (int)_schema.Structs["LoadedFilesRoot"].Constants["BucketCount"];

            // Rebuilt rather than appended: FakeMemoryReader lets a later region win, so the
            // vector simply gets replaced each time a record is added.
            ulong slots = Take(slotSize * _records.Count);
            Memory.Place(Root, slots);
            Memory.Place(Root + 8, slots + (ulong)(slotSize * _records.Count));
            Memory.Place(Root + (ulong)bucket.OffsetOf("Capacity"), _records.Count);

            for (int i = 0; i < _records.Count; i++)
            {
                Memory.Place(slots + (ulong)(i * slotSize) + (ulong)slotRecord, _records[i]);
            }

            for (int b = 1; b < bucketCount; b++)
            {
                Memory.Place(Root + (ulong)(b * bucketSize) + (ulong)bucket.OffsetOf("Capacity"), 0);
            }
        }

        /// <summary>Puts a plausible bucket at an index, past the count or otherwise.</summary>
        internal void PlantBucketAt(int index, int slots)
        {
            var bucketSize = (int)_schema.Structs["LoadedFilesRoot"].Constants["BucketSize"];
            var slotSize = (int)_schema.Structs["FileRecordSlot"].Constants["Size"];

            ulong bucket = Root + (ulong)(index * bucketSize);
            ulong first = Take(slotSize * slots);
            Memory.Place(bucket, first);
            Memory.Place(bucket + 8, first + (ulong)(slotSize * slots));
        }

        private ulong Take(int bytes)
        {
            ulong at = _next;
            _next += (ulong)((bytes + 0xFF) & ~0xFF);
            return at;
        }
    }
}
