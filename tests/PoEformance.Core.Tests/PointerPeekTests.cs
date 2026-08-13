using PoEformance.Core.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Following one pointer to find out what is on the other end.
/// </summary>
/// <remarks>
/// This is what turns a column of addresses into a map. A structure full of pointers says
/// nothing until you know that this one is a name, that one is a list of forty-two somethings,
/// and the third is another structure - and by then the shape of the thing is visible without
/// a single field of it having been decoded.
/// </remarks>
public class PointerPeekTests
{
    private const ulong Target = 0x2000_0000_0000;

    [Fact]
    public void ANameIsTheMostUsefulThingToFindSoItIsLookedForFirst()
    {
        // Padded first, because a string in a real process has an allocation around it - and
        // a read only succeeds when every byte is there, so a region cut off at the final
        // character is a test artefact rather than a case worth reproducing.
        var reader = new FakeMemoryReader();
        reader.Place(Target, new byte[256]);
        reader.PlaceUtf16(Target, "MapFortress");

        PeekResult found = PointerPeek.Peek(reader, Target);

        Assert.Equal(TargetKind.WideText, found.Kind);
        Assert.Contains("MapFortress", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextIsFoundToo()
    {
        var reader = new FakeMemoryReader();
        reader.PlaceUtf8(Target, "Metadata/Terrain");

        Assert.Equal(TargetKind.Text, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void AListSaysHowManyThingsAreInIt()
    {
        // A begin/end pair is how every array in this game is stored, and the count is the
        // single most informative number about an unknown one - "forty-two of something" is a
        // much better starting point than an address.
        const ulong Items = 0x3000_0000_0000;

        var reader = new FakeMemoryReader();
        reader.Place(Target, Items);
        reader.Place(Target + 8, Items + (42 * 0x38));

        PeekResult found = PointerPeek.Peek(reader, Target);

        Assert.Equal(TargetKind.Vector, found.Kind);
        Assert.Equal(42 * 0x38, found.Span);
        Assert.Contains(0x38, found.ElementGuesses!);
        Assert.Contains("42 x 0x38", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoUNRELATEDPointersAreNotCalledAList()
    {
        // The guess that would mislead most. Any two neighbouring pointers into one allocation
        // satisfy "both readable, second after first" - so an implausible span disqualifies it,
        // or the label would appear on half the rows in the tool and mean nothing.
        var reader = new FakeMemoryReader();
        reader.Place(Target, 0x3000_0000_0000UL);
        reader.Place(Target + 8, 0x3000_2000_0000UL);   // half a gigabyte apart

        Assert.NotEqual(TargetKind.Vector, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void AnEmptyListIsNotAList()
    {
        // begin == end. Real, common, and says nothing about what the elements are - so
        // claiming a vector here would be a guess with no evidence behind it.
        var reader = new FakeMemoryReader();
        reader.Place(Target, 0x3000_0000_0000UL);
        reader.Place(Target + 8, 0x3000_0000_0000UL);

        Assert.NotEqual(TargetKind.Vector, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void ARowOfADataTableIsRecognisedByTheIdItStartsWith()
    {
        // Half of this game's PoE2 tables declare Id as their first column, and a string column
        // is a pointer to wide characters once the row is loaded. So a structure whose first
        // eight bytes lead to a name is almost always a row - and the name says which one,
        // which is the difference between "another structure" and "flask_effect_life".
        const ulong Id = 0x4000_0000_0000;

        var reader = new FakeMemoryReader();
        reader.Place(Target, new byte[64]);
        reader.Place(Target, Id);
        reader.Place(Id, new byte[256]);
        reader.PlaceUtf16(Id, "flask_effect_life");

        PeekResult found = PointerPeek.Peek(reader, Target);

        Assert.Equal(TargetKind.DatRow, found.Kind);
        Assert.Contains("flask_effect_life", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowIsNotReportedAsAListJustBecauseTwoOfItsColumnsAreStrings()
    {
        // Why the row is looked for BEFORE the list. Id and Description sit next to each other
        // in several tables and their characters share one blob, so the two pointers are a
        // readable begin/end pair with a small, neatly divisible span - every requirement a
        // vector has. Called a list, the row's own name would never be shown.
        const ulong Strings = 0x4000_0000_0000;

        var reader = new FakeMemoryReader();
        reader.Place(Target, new byte[64]);
        reader.Place(Target, Strings);
        reader.Place(Target + 8, Strings + 0x40);
        reader.Place(Strings, new byte[256]);
        reader.PlaceUtf16(Strings, "flask_effect_life");

        Assert.Equal(TargetKind.DatRow, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void AnEngineObjectIsNotARowBecauseItStartsWithAVtable()
    {
        // What makes the fingerprint worth stating at all. The two commonest things behind a
        // pointer in this game are a component and a data row, and they are told apart by
        // their first field: one points into the module, the other at a name.
        var reader = new FakeMemoryReader { ModuleBase = 0x1_4000_0000, ModuleSize = 0x8000_0000 };
        reader.Place(Target, new byte[64]);
        reader.Place(Target, 0x1_4000_1234UL);

        Assert.NotEqual(TargetKind.DatRow, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void CodeIsNamedAsAnOffsetIntoTheGameRatherThanAnAddress()
    {
        // module+0x1234 is the same across every run of the game; the raw address is not. One
        // can be written down and compared later, the other is noise.
        var reader = new FakeMemoryReader { ModuleBase = 0x1_4000_0000, ModuleSize = 0x8000_0000 };

        PeekResult found = PointerPeek.Peek(reader, 0x1_4000_1234);

        Assert.Equal(TargetKind.Code, found.Kind);
        Assert.Contains("module+0x1234", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingReadableThatIsNoneOfTheAboveIsAnotherStructure()
    {
        // Not a failure - it is the ordinary case, and the invitation to go and look.
        var reader = new FakeMemoryReader();
        reader.Place(Target, new byte[64]);
        reader.Place(Target, 0x0000_0001_0000_0002UL);

        Assert.Equal(TargetKind.Structure, PointerPeek.Peek(reader, Target).Kind);
    }

    [Fact]
    public void AnAddressWithNothingBehindItSaysSo()
    {
        Assert.Equal(TargetKind.Unreadable, PointerPeek.Peek(new FakeMemoryReader(), Target).Kind);
    }

    [Fact]
    public void SomethingThatIsNotAnAddressAtAllIsRefusedWithoutReading()
    {
        Assert.Equal(TargetKind.Unreadable, PointerPeek.Peek(new FakeMemoryReader(), 42).Kind);
    }

    private const ulong FakeTableAt = 0x2100_0000_0000;
    private const ulong FakeStoreAt = 0x5000_0000_0000;
    private const ulong FakeRowsAt = 0x6000_0000_0000;
    private const ulong FakeByIdAt = 0x7000_0000_0000;
    private const ulong FakePathAt = 0x8000_0000_0000;
    private const ulong FakeIdAt = 0x9000_0000_0000;

    /// <summary>
    /// Builds a dat table in fake memory, at whatever offsets the shipped schema declares.
    /// </summary>
    private static (FakeMemoryReader Reader, DatTableShape Shape) FakeTable(string path, int rows, int rowSize)
    {
        DatTableShape? shape = DatTableShape.From(RealSessionTests.Schema());
        Assert.NotNull(shape);

        var reader = new FakeMemoryReader { ModuleSize = 0x8000_0000 };
        reader.Place(FakeTableAt, new byte[0x40]);
        reader.Place(FakeTableAt, reader.ModuleBase + 0x1234);   // a vtable, as a real one has
        reader.Place(FakeTableAt + (ulong)shape.Value.RowStoreOffset, FakeStoreAt);
        if (path.Length > 0)
        {
            reader.PlaceStdWString(FakeTableAt + (ulong)shape.Value.PathOffset, path, FakePathAt);
        }

        reader.Place(FakeStoreAt, new byte[0x90]);
        reader.Place(FakeStoreAt + (ulong)shape.Value.RowsOffset, FakeRowsAt);
        reader.Place(FakeStoreAt + (ulong)shape.Value.RowsOffset + 8, FakeRowsAt + (ulong)(rows * rowSize));
        reader.Place(FakeStoreAt + (ulong)shape.Value.ByIdIndexOffset, FakeByIdAt);
        reader.Place(
            FakeStoreAt + (ulong)shape.Value.ByIdIndexOffset + 8,
            FakeByIdAt + (ulong)(rows * shape.Value.ByIdEntrySize));

        reader.Place(FakeByIdAt, new byte[0x40]);
        reader.Place(FakeByIdAt + (ulong)shape.Value.ByIdEntryRowAt, FakeRowsAt);

        return (reader, shape.Value);
    }

    [Fact]
    public void ATableNamesItselfAndWorksOutItsOwnRowSize()
    {
        // The row size is DERIVED, not looked up: a by-Id entry is a fixed 0x18 whatever the
        // table, and there is one per row, so the index says how many rows there are and the
        // rows array divided by that says how big one is. Before this, a row size could only
        // come from the community schema's column list.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable("Data/Balance/QuestFlags.dat", 4, 0x0C);

        PeekResult found = PointerPeek.Peek(reader, FakeTableAt, shape, 0);

        Assert.Equal(TargetKind.DatTable, found.Kind);
        Assert.Equal("dat table \"Data/Balance/QuestFlags.dat\", 4 rows of 0xC", found.Summary);
    }

    [Fact]
    public void ATableWithNoReadablePathIsStillATable()
    {
        // The path is the label, not the evidence. It lives one pointer further away than the
        // numbers do, and a table caught without it is still worth recognising and counting.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable(string.Empty, 4, 0x0C);

        PeekResult found = PointerPeek.Peek(reader, FakeTableAt, shape, 0);

        Assert.Equal(TargetKind.DatTable, found.Kind);
        Assert.Equal("dat table, 4 rows of 0xC", found.Summary);
    }

    [Fact]
    public void TwoListsThatDisagreeWithEachOtherAreNotATable()
    {
        // The check that makes this a fingerprint rather than a coincidence. Two containers
        // whose spans happen to divide is a shape plenty of structures have; two containers
        // that agree about where the rows START is not - so the first index entry has to point
        // at the first row, and here it deliberately does not.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable("Data/Balance/QuestFlags.dat", 4, 0x0C);
        reader.Place(FakeByIdAt + (ulong)shape.ByIdEntryRowAt, FakeRowsAt + 8);

        Assert.NotEqual(TargetKind.DatTable, PointerPeek.Peek(reader, FakeTableAt, shape, 0).Kind);
    }

    [Fact]
    public void ARowIsNumberedWhenItsTableIsInTheNextEightBytes()
    {
        // A foreign reference is a row followed by its table, so a peek handed both halves can
        // answer the one question a row has never been able to answer about itself: which
        // table it is a row of, and which row.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable("Data/Balance/QuestFlags.dat", 4, 0x0C);
        ulong row = FakeRowsAt + (2 * 0x0C);
        reader.Place(row, new byte[0x30]);
        reader.Place(row, FakeIdAt);
        reader.Place(FakeIdAt, new byte[256]);
        reader.PlaceUtf16(FakeIdAt, "EnteredHideout");

        PeekResult found = PointerPeek.Peek(reader, row, shape, FakeTableAt);

        Assert.Equal(TargetKind.DatRow, found.Kind);
        Assert.Equal("QuestFlags row 2 of 4, Id \"EnteredHideout\"", found.Summary);
    }

    [Fact]
    public void ARowWithNoTableBesideItIsStillJustARow()
    {
        // What every row that is not a foreign reference looks like, which is most of them.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable("Data/Balance/QuestFlags.dat", 4, 0x0C);
        ulong row = FakeRowsAt + (2 * 0x0C);
        reader.Place(row, new byte[0x30]);
        reader.Place(row, FakeIdAt);
        reader.Place(FakeIdAt, new byte[256]);
        reader.PlaceUtf16(FakeIdAt, "EnteredHideout");

        Assert.Equal("dat row, Id \"EnteredHideout\"", PointerPeek.Peek(reader, row, shape, 0).Summary);
    }

    [Fact]
    public void AnAddressThatIsNotOnTheRowGridIsNotGivenARowNumber()
    {
        // Halfway into a row - a pointer at its second column, say. Dividing with a remainder
        // means this is not row 2, and saying "row 2" would be worse than saying nothing.
        (FakeMemoryReader reader, DatTableShape shape) = FakeTable("Data/Balance/QuestFlags.dat", 4, 0x0C);
        ulong inside = FakeRowsAt + (2 * 0x0C) + 4;
        reader.Place(inside, new byte[0x30]);
        reader.Place(inside, FakeIdAt);
        reader.Place(FakeIdAt, new byte[256]);
        reader.PlaceUtf16(FakeIdAt, "EnteredHideout");

        Assert.Equal(
            "QuestFlags row, Id \"EnteredHideout\"",
            PointerPeek.Peek(reader, inside, shape, FakeTableAt).Summary);
    }
}
