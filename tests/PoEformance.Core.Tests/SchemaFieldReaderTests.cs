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
