using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading BaseItemTypes.dat, and the measurement that says the layout is the right one.
/// </summary>
/// <remarks>
/// TWO HALVES, BECAUSE NO ONE CAPTURE HOLDS BOTH. The row LAYOUT is settled against a live
/// client: the loader's file table reports what it thinks a row of this table is worth, and the
/// column list from poe-tool-dev/dat-schema, priced with the width table this project already
/// verified, has to sum to the same number. Every column in front of Name has to be right for
/// that total to land, which is what makes the agreement worth more than any single offset.
///
/// The ARITHMETIC is settled against a controlled image, because the capture that holds the walk
/// was taken by a build that never read these rows - a recording answers only the reads its build
/// performed, so against it this reader correctly comes back with nothing at all. That is itself
/// worth a test: nothing read must not be handed back as an empty table, or it would go in front
/// of the shipped file and blank every base type on screen.
/// </remarks>
public class BaseItemTableTests
{
    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }

    [Fact]
    public void THELiveClientAgreesWithTheSchemaAboutWhatARowIsWorth()
    {
        // THE GO/NO-GO FOR THE WHOLE READER, and it is what kept Mods out. dat-schema's columns
        // for BaseItemTypes sum to 0x168 under the widths scripts/dat-offsets.ps1 verified; the
        // client's own loader reports 5496 rows of 360 bytes. They agree exactly.
        //
        // The same measurement on Mods did NOT agree at first - 0x295 computed against 0x2B5
        // reported - and the answer was not missing columns but a missing WIDTH RULE: its eight
        // interval columns are ranges, two values each, which is the thirty-two bytes exactly.
        // See DatColumn.Interval. Mods is readable with that rule and is simply not read HERE,
        // because this reader is BaseItemTypes' and reading it is a change of its own.
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(
            Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-tables-055.rec")));

        OffsetSchema schema = RealSessionTests.LiveSchema();
        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);

        LoadedDatTable found = Assert.Single(tables.FindAll(BaseItemTable.TableName));
        Assert.Equal(
            (long)schema.Structs["BaseItemTypesRow"].Constants["ComputedRowSize"],
            found.Facts.RowSize);
        Assert.Equal(5496, found.Facts.Rows);

        // AND THE ROWS ARE NOT IN THIS CAPTURE, which is the other half of the rule: the build
        // that took it walked the file table and never read a single one of these rows, so the
        // reader comes back with nothing rather than with 5496 blanks.
        Assert.Null(BaseItemTable.From(tables, replay, schema));
    }

    [Fact]
    public void ANDAROWSTwoStringsAreFoundWhereTheLayoutSaysTheyAre()
    {
        const ulong RowsBegin = 0x2_0000_0000;
        const int RowSize = 0x168;

        OffsetSchema schema = RealSessionTests.LiveSchema();
        int idAt = schema.Structs["BaseItemTypesRow"].OffsetOf("IdPtr");
        int nameAt = schema.Structs["BaseItemTypesRow"].OffsetOf("NamePtr");

        var reader = new FakeMemoryReader();

        // Two real pairs out of the shipped table, laid out at the stride the client reports.
        (string Path, string Name)[] rows =
        [
            ("Metadata/Items/Currency/CurrencyWeaponQuality", "Blacksmith's Whetstone"),
            ("Metadata/Items/Currency/CurrencyMagicQuality", "Arcanist's Etcher"),
        ];

        // ONE CONTIGUOUS BLOCK, because that is what a dat table is - RowsBegin plus index times
        // RowSize, no padding - and it is what lets DatRows carry a hundred and eighty rows per
        // read. Placing the fields separately would test a fixture rather than the reader.
        var block = new byte[rows.Length * RowSize];
        for (int index = 0; index < rows.Length; index++)
        {
            int at = index * RowSize;
            ulong pathAt = 0x3_0000_0000 + (ulong)(index * 0x200);
            ulong wordsAt = 0x4_0000_0000 + (ulong)(index * 0x200);

            PlaceWide(reader, pathAt, rows[index].Path);
            PlaceWide(reader, wordsAt, rows[index].Name);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at + idAt), pathAt);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at + nameAt), wordsAt);
        }

        reader.Place(RowsBegin, block);

        BaseItemTable table = Assert.IsType<BaseItemTable>(BaseItemTable.Over(
            reader,
            new DatTableFacts("Data/BaseItemTypes.dat", "BaseItemTypes", rows.Length, RowSize, RowsBegin),
            schema));

        Assert.Equal(2, table.Named);
        Assert.Equal("Blacksmith's Whetstone", table.Of(rows[0].Path));
        Assert.Equal("Arcanist's Etcher", table.Of(rows[1].Path));
        Assert.Null(table.Of("Metadata/Items/Never/Heard/Of/It"));
    }

    [Fact]
    public void ANDATableWhoseRowsAreTheWrongWidthIsRefusedRatherThanRead()
    {
        // A stride that is not this table's turns every offset into a read of whatever happens to
        // be there, and "whatever happens to be there" is the one answer this project treats as
        // worse than none. The gate is on the number the CLIENT reports, so it holds whether the
        // mismatch is a wrong table, a patched layout or - as with Mods - an arithmetic of ours
        // that was one rule short.
        OffsetSchema schema = RealSessionTests.LiveSchema();

        Assert.Null(BaseItemTable.Over(
            new FakeMemoryReader(),
            new DatTableFacts("Data/Mods.dat", "Mods", 16784, 0x2B5, 0x2_0000_0000),
            schema));
    }

    [Fact]
    public void ANDTheGAMESNameBeatsTheShippedOneWhileTheFileStillCoversWhatItKnows()
    {
        // A base type is keyed by its own path, so the shipped file does not go WRONG the way the
        // row-keyed stat tables did - it goes SHORT. This says the layering works in both
        // directions: the game answers where it has read a row, and the file answers where the
        // game was never asked.
        const ulong RowsBegin = 0x2_0000_0000;
        const int RowSize = 0x168;
        const string Path = "Metadata/Items/Currency/CurrencyWeaponQuality";

        OffsetSchema schema = RealSessionTests.LiveSchema();
        var reader = new FakeMemoryReader();
        PlaceWide(reader, 0x3_0000_0000, Path);
        PlaceWide(reader, 0x4_0000_0000, "A Name Only The Game Has");
        var block = new byte[RowSize];
        BinaryPrimitives.WriteUInt64LittleEndian(
            block.AsSpan(schema.Structs["BaseItemTypesRow"].OffsetOf("IdPtr")), 0x3_0000_0000UL);
        BinaryPrimitives.WriteUInt64LittleEndian(
            block.AsSpan(schema.Structs["BaseItemTypesRow"].OffsetOf("NamePtr")), 0x4_0000_0000UL);
        reader.Place(RowsBegin, block);

        ItemNames names = ItemNames.Load(
            DataFile("item-stats.json"), DataFile("item-names.json"), DataFile("unique_ivi_name_map.tsv"));

        Assert.Equal("Blacksmith's Whetstone", names.Base(Path));

        names.Learn(baseItems: BaseItemTable.Over(
            reader, new DatTableFacts("Data/BaseItemTypes.dat", "BaseItemTypes", 1, RowSize, RowsBegin), schema));

        Assert.Equal("A Name Only The Game Has", names.Base(Path));

        // One row was read, so everything else still comes from the file rather than falling back
        // to the tail of the path.
        Assert.Equal("Arcanist's Etcher", names.Base("Metadata/Items/Currency/CurrencyMagicQuality"));
        Assert.Contains("BaseItemTypes.dat", names.StatSource, StringComparison.Ordinal);
    }

    private static string DataFile(string name)
        => Path.Combine(Root.FullName, "data", name);

    /// <summary>
    /// A UTF-16 string with the mapped page around it that a real process would have.
    /// </summary>
    /// <remarks>
    /// NOT PlaceUtf16, AND THE DIFFERENCE IS THE WHOLE READ. ReadUnicodeString asks for its full
    /// width and HALVES the request until one succeeds, which is how it copes with a string near
    /// the end of a page; FakeMemoryReader only answers a read it can satisfy whole, so a region
    /// sized to the string itself turns a 128-character ask into a 32-character one and hands
    /// back a truncated path. The game has memory after its strings. So does this.
    /// </remarks>
    private static void PlaceWide(FakeMemoryReader reader, ulong address, string text)
    {
        var bytes = new byte[512];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        reader.Place(address, bytes);
    }
}
