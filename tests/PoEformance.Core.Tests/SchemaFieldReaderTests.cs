using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading a struct through its schema layout, which is what makes an offset checkable.
/// </summary>
/// <remarks>
/// The display these feed has one job: let somebody stand in front of the thing and see
/// whether the number moves. So the cases worth pinning are the ones where a wrong answer
/// would still LOOK right - a failed read that shows as 0, or a string that comes back as
/// its own address.
/// </remarks>
public class SchemaFieldReaderTests
{
    private const ulong At = 0x3000_0000_0000;

    private static StructDef Layout(params FieldDef[] fields)
        => new("Test", null, fields, new Dictionary<string, long>());

    /// <summary>A schema holding just the structs a test wants to be followed into.</summary>
    private static OffsetSchema Schema(params StructDef[] structs)
        => new(1, "test", new Dictionary<string, StaticAnchor>(), structs.ToDictionary(s => s.Name));

    [Fact]
    public void EachFieldIsReadTheWayItIsDeclared()
    {
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x100]);
        reader.Place(At + 0x10, -7);
        reader.Place(At + 0x20, 2.5f);
        reader.Place(At + 0x30, (byte)1);
        reader.Place(At + 0x40, 0x1234UL);

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader,
            Layout(
                new FieldDef("Count", 0x10, FieldType.I32, null, null),
                new FieldDef("Scale", 0x20, FieldType.F32, null, null),
                new FieldDef("Flag", 0x30, FieldType.U8, null, null),
                new FieldDef("Next", 0x40, FieldType.Ptr, null, null)),
            At);

        Assert.Equal("-7", read.Single(field => field.Name == "Count").Text);
        Assert.Equal("2.5", read.Single(field => field.Name == "Scale").Text);
        Assert.Equal("1", read.Single(field => field.Name == "Flag").Text);
        Assert.Equal("0x1234", read.Single(field => field.Name == "Next").Text);
    }

    [Fact]
    public void AFieldNobodyCouldReadSaysSoRatherThanShowingAZero()
    {
        // The one that matters most. A struct read at the wrong base is the ordinary way to
        // be wrong here, and a column of plausible zeroes would read as "this component holds
        // nothing" instead of "this address is not that component".
        var reader = new FakeMemoryReader();

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader, Layout(new FieldDef("Health", 0x1B0, FieldType.I32, null, null)), At);

        Assert.Equal("unreadable", read.Single().Text);
    }

    [Fact]
    public void BothStringShapesComeBackAsTheirText()
    {
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x80]);
        reader.PlaceStdWString(At + 0x10, "Vaal Chest", At + 0x200);

        // Room around the characters, because the reader asks for its whole limit at once and
        // halves until a read succeeds - so a region that ends where the string ends returns a
        // SHORTER string rather than failing. That is the page-edge behaviour, pinned in its
        // own test below; here it would be an accident, and it was one.
        reader.Place(At + 0x300, new byte[0x100]);
        reader.Place(At + 0x40, At + 0x300);
        reader.PlaceUtf16(At + 0x300, "Waypoint");

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader,
            Layout(
                new FieldDef("Name", 0x10, FieldType.StdWString, null, null),
                new FieldDef("Icon", 0x40, FieldType.Utf16Ptr, null, null)),
            At);

        Assert.Equal("\"Vaal Chest\"", read.Single(field => field.Name == "Name").Text);
        Assert.Equal("\"Waypoint\"", read.Single(field => field.Name == "Icon").Text);
    }

    [Fact]
    public void AStringWhoseTailCannotBeReadComesBackShortRatherThanEmpty()
    {
        // Deliberate, in the reader this borrows: a string sitting near the end of a mapped
        // page cannot be read at full length, and returning nothing there would lose names
        // that are perfectly readable up to the edge. Worth pinning because the shortened
        // answer arrives with no mark on it - "Waypoi" looks like a value, not a truncation -
        // and this test exists because it fooled the test next to it first.
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x80]);
        reader.Place(At + 0x40, At + 0x300);
        reader.PlaceUtf16(At + 0x300, "Waypoint");   // and nothing behind it

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader, Layout(new FieldDef("Icon", 0x40, FieldType.Utf16Ptr, null, null)), At);

        Assert.Equal("\"Waypoi\"", read.Single().Text);
    }

    [Fact]
    public void AStructSittingInsideAFieldIsUnfoldedWhereItSits()
    {
        // Life's vitals, which is the case that asked for this: eight bytes that are not an
        // address at all but the start of a Vital, and read as a pointer they show up as
        // "0x333C00003335" - a number that happens to be a full health pool spelled sideways.
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x100]);
        reader.Place(At + 0x10 + 0x2C, 2410);
        reader.Place(At + 0x10 + 0x30, 1532);

        var vital = new StructDef(
            "Vital",
            null,
            [
                new FieldDef("Max", 0x2C, FieldType.I32, null, null),
                new FieldDef("Current", 0x30, FieldType.I32, null, null),
            ],
            new Dictionary<string, long>());

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader,
            Layout(new FieldDef("Health", 0x10, FieldType.Ptr, null, null, "Vital", inline: true)),
            At,
            Schema(vital));

        FieldReading health = read.Single();
        Assert.Equal("Vital", health.Text);
        Assert.NotNull(health.Children);
        Assert.Equal("2410", health.Children!.Single(field => field.Name == "Max").Text);
        Assert.Equal("1532", health.Children!.Single(field => field.Name == "Current").Text);
    }

    [Fact]
    public void APointerToAStructIsFollowedAndANullOneIsNot()
    {
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x40]);
        reader.Place(At + 0x10, At + 0x800);
        reader.Place(At + 0x800, new byte[0x40]);
        reader.Place(At + 0x800 + 0x18, 42);

        var inner = new StructDef(
            "ChargesInternal", null, [new FieldDef("PerUseCharges", 0x18, FieldType.I32, null, null)],
            new Dictionary<string, long>());

        StructDef layout = Layout(new FieldDef("Charges", 0x10, FieldType.Ptr, null, null, "ChargesInternal"));

        FieldReading followed = SchemaFieldReader.Read(reader, layout, At, Schema(inner)).Single();
        Assert.NotNull(followed.Children);
        Assert.Equal("42", followed.Children!.Single().Text);

        // The address is kept as well as followed: it is what somebody copies into the
        // dissector when the fields below it look wrong.
        Assert.Contains("->", followed.Text, StringComparison.Ordinal);

        // A pointer at zero has nothing behind it, and an empty child list under it would
        // read as a struct full of nothing rather than as an absent one.
        var empty = new FakeMemoryReader();
        empty.Place(At, new byte[0x40]);
        Assert.Null(SchemaFieldReader.Read(empty, layout, At, Schema(inner)).Single().Children);
    }

    [Fact]
    public void UnfoldingStopsAfterOneLevel()
    {
        // A struct that names itself is not hypothetical - a linked list node does - and
        // without a floor the read would not finish.
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x40]);
        reader.Place(At + 0x10, At);

        var ring = new StructDef(
            "Ring", null, [new FieldDef("Next", 0x10, FieldType.Ptr, null, null, "Ring")],
            new Dictionary<string, long>());

        FieldReading first = SchemaFieldReader.Read(reader, ring, At, Schema(ring)).Single();

        Assert.NotNull(first.Children);
        Assert.Null(first.Children!.Single().Children);
    }

    [Fact]
    public void NumbersTheSchemaHasNamesForComeBackNamed()
    {
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x20]);
        reader.Place(At + 0x00, 2);
        reader.Place(At + 0x08, 7);
        reader.Place(At + 0x10, (byte)1);

        var names = new Dictionary<long, string> { [0] = "Normal", [2] = "Rare" };

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader,
            Layout(
                new FieldDef("Rarity", 0x00, FieldType.I32, null, null, values: names),
                new FieldDef("Unlisted", 0x08, FieldType.I32, null, null, values: names),
                new FieldDef("IsBoss", 0x10, FieldType.U8, null, new Invariant.Range(0, 1))),
            At);

        Assert.Equal("Rare (2)", read.Single(field => field.Name == "Rarity").Text);

        // A number nobody has named keeps its digits. Hiding it behind the nearest label
        // would be inventing a fact, and this is the value most worth noticing.
        Assert.Equal("7", read.Single(field => field.Name == "Unlisted").Text);

        // And a field whose invariant says 0..1 has already declared itself a yes or no.
        Assert.Equal("yes", read.Single(field => field.Name == "IsBoss").Text);
    }

    [Fact]
    public void TheSchemasOwnNotesTravelWithTheValue()
    {
        // Why the comment is carried rather than looked up later: it is the expensive part of
        // this project - drift history, what proved the offset - and the moment somebody is
        // looking at a suspicious value is exactly when they want it.
        var reader = new FakeMemoryReader();
        reader.Place(At, new byte[0x10]);

        IReadOnlyList<FieldReading> read = SchemaFieldReader.Read(
            reader,
            Layout(new FieldDef("Reaction", 0x00, FieldType.U8, "1 means friendly.", null)),
            At);

        Assert.Equal("1 means friendly.", read.Single().Comment);
        Assert.Equal(FieldType.U8, read.Single().Type);
    }
}
