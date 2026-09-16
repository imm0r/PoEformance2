using System.Buffers.Binary;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading Mods.dat - the table the interval rule brought back from being written off.
/// </summary>
/// <remarks>
/// THE LAYOUT IS THE CLAIM, and it is settled the same way BaseItemTypes' was: the client's own
/// loader says what it thinks a row of this table is worth, and the column list from
/// poe-tool-dev/dat-schema has to sum to the same number. Here that is worth more than usual,
/// because Name sits at +0x62 with eight interval columns among the ones in front of it - priced
/// wrong, the total misses by thirty-two and every string read after it is a lottery. The total
/// landing exactly is what says all of them are priced right.
/// </remarks>
public class ModTableTests
{
    private const int RowSize = 0x2B5;

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
    public void THELiveClientAgreesWithTheSchemaOnceIntervalsArePriced()
    {
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(
            Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-tables-055.rec")));

        OffsetSchema schema = RealSessionTests.LiveSchema();
        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);

        LoadedDatTable found = Assert.Single(tables.FindAll(ModTable.TableName));

        // 0x2B5, which the columns only reach when the eight Stat*Value intervals are counted as
        // the two values each that they are. Without that rule this is 0x295 and the table was
        // about to be recorded as unreadable.
        Assert.Equal((long)schema.Structs["ModsRow"].Constants["ComputedRowSize"], found.Facts.RowSize);
        Assert.Equal(16784, found.Facts.Rows);

        // And the rows are not in this capture: the build that took it walked the file table and
        // never read one of them, so the reader comes back with nothing rather than blanks.
        Assert.Null(ModTable.From(tables, replay, schema));
    }

    [Fact]
    public void ANDAMODSTwoStringsAndItsKindComeOutOfTheRow()
    {
        const ulong RowsBegin = 0x2_0000_0000;

        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef row = schema.Structs["ModsRow"];

        var reader = new FakeMemoryReader();

        // Three real rows out of the shipped table, one of each kind the game gives a name.
        (string Id, string Name, int Kind, string Word)[] rows =
        [
            ("Strength1", "of the Brute", 2, "suffix"),
            ("LocalPhysicalDamage1", "Heavy", 1, "prefix"),
            ("StrengthUnique1", "Kaom's", 3, "unique"),
        ];

        // ONE CONTIGUOUS BLOCK, because that is what a dat table is: RowsBegin plus index times
        // RowSize with no padding, which is the whole reason DatRows can read ninety at a time.
        // Placing the fields separately would make a block read fail and test a fixture instead.
        var block = new byte[rows.Length * RowSize];
        for (int index = 0; index < rows.Length; index++)
        {
            int at = index * RowSize;
            ulong idAt = 0x3_0000_0000 + (ulong)(index * 0x400);
            ulong nameAt = 0x4_0000_0000 + (ulong)(index * 0x400);

            Wide(reader, idAt, rows[index].Id);
            Wide(reader, nameAt, rows[index].Name);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at + row.OffsetOf("IdPtr")), idAt);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(at + row.OffsetOf("NamePtr")), nameAt);
            BinaryPrimitives.WriteInt32LittleEndian(
                block.AsSpan(at + row.OffsetOf("GenerationType")), rows[index].Kind);
        }

        reader.Place(RowsBegin, block);

        ModTable table = Assert.IsType<ModTable>(ModTable.Over(
            reader, new DatTableFacts("Data/Mods.dat", "Mods", rows.Length, RowSize, RowsBegin), schema));

        Assert.Equal(3, table.Named);
        foreach ((string id, string name, int _, string word) in rows)
        {
            (string Name, string Kind) said = Assert.NotNull(table.Of(id));
            Assert.Equal(name, said.Name);
            Assert.Equal(word, said.Kind);
        }

        Assert.Null(table.Of("NoSuchModEver"));
    }

    [Fact]
    public void ANDAGenerationTypeTheFileGivesNoKindGetsNoneHereEither()
    {
        // Of the 16784 rows the client carries, the shipped export kept 6567 - and every one of
        // them is generation type 1, 2 or 3. The rest are real rows with real names and no kind,
        // so inventing one for them would be this reader claiming more than the table says.
        const ulong RowsBegin = 0x2_0000_0000;

        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef row = schema.Structs["ModsRow"];

        var reader = new FakeMemoryReader();
        Wide(reader, 0x3_0000_0000, "SomeCorruptedThing");
        Wide(reader, 0x4_0000_0000, "Corrupted");
        reader.Place(RowsBegin, Row(row, 0x3_0000_0000, 0x4_0000_0000, 11));

        ModTable table = Assert.IsType<ModTable>(ModTable.Over(
            reader, new DatTableFacts("Data/Mods.dat", "Mods", 1, RowSize, RowsBegin), schema));

        (string Name, string Kind) said = Assert.NotNull(table.Of("SomeCorruptedThing"));
        Assert.Equal("Corrupted", said.Name);
        Assert.Equal(string.Empty, said.Kind);
    }

    [Fact]
    public void ANDAROWWithNoNameCostsNoStringReadAtAll()
    {
        // Ten thousand of the client's rows have no name - the ones the export drops. Reading
        // their ids to find that out is two thirds of the table's cost for nothing.
        const ulong RowsBegin = 0x2_0000_0000;

        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef row = schema.Structs["ModsRow"];

        var reader = new FakeMemoryReader();
        Wide(reader, 0x3_0000_0000, "AnIdNobodyShouldReach");
        reader.Place(RowsBegin, Row(row, 0x3_0000_0000, 0, 0));

        Assert.Null(ModTable.Over(
            reader, new DatTableFacts("Data/Mods.dat", "Mods", 1, RowSize, RowsBegin), schema));
    }

    [Fact]
    public void ANDTheGAMESAffixBeatsTheShippedOne()
    {
        const ulong RowsBegin = 0x2_0000_0000;
        const string Mod = "Strength1";

        OffsetSchema schema = RealSessionTests.LiveSchema();
        StructDef row = schema.Structs["ModsRow"];

        var reader = new FakeMemoryReader();
        Wide(reader, 0x3_0000_0000, Mod);
        Wide(reader, 0x4_0000_0000, "of the Renamed Brute");
        reader.Place(RowsBegin, Row(row, 0x3_0000_0000, 0x4_0000_0000, 2));

        ItemNames names = ItemNames.Load(
            Data("item-stats.json"), Data("item-names.json"), Data("unique_ivi_name_map.tsv"));

        Assert.Equal("of the Brute", names.Mod(Mod).Name);

        names.Learn(modNames: ModTable.Over(
            reader, new DatTableFacts("Data/Mods.dat", "Mods", 1, RowSize, RowsBegin), schema));

        Assert.Equal("of the Renamed Brute", names.Mod(Mod).Name);
        Assert.Equal("suffix", names.Mod(Mod).Kind);

        // One row was read, so every other mod still comes from the file.
        Assert.Equal("of the Wrestler", names.Mod("Strength2").Name);
        Assert.Contains("Mods.dat", names.StatSource, StringComparison.Ordinal);
    }

    private static string Data(string name) => Path.Combine(Root.FullName, "data", name);

    /// <summary>One row's bytes, laid out the way the table lays them out.</summary>
    private static byte[] Row(StructDef row, ulong id, ulong name, int kind)
    {
        var bytes = new byte[RowSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(row.OffsetOf("IdPtr")), id);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(row.OffsetOf("NamePtr")), name);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row.OffsetOf("GenerationType")), kind);
        return bytes;
    }

    /// <summary>See BaseItemTableTests.PlaceWide - the game has memory after its strings.</summary>
    private static void Wide(FakeMemoryReader reader, ulong address, string text)
    {
        var bytes = new byte[512];
        System.Text.Encoding.Unicode.GetBytes(text, bytes);
        reader.Place(address, bytes);
    }
}
