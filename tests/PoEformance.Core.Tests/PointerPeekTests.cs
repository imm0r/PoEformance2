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
}
