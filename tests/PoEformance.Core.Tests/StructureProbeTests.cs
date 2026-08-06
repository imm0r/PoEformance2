using PoEformance.Core.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading a structure nobody has decoded yet.
/// </summary>
/// <remarks>
/// Bytes carry no type, so everything here is a GUESS and the tests are about whether the
/// guess is useful rather than whether it is right. The bar: it must be right about the cases
/// that actually occur in this game's structures, and it must not sound confident about the
/// ones it cannot know.
/// </remarks>
public class StructureProbeTests
{
    private const ulong Somewhere = 0x2000_0000_0000;

    private static byte[] Window(params object[] fields)
    {
        var bytes = new List<byte>();
        foreach (object field in fields)
        {
            bytes.AddRange(field switch
            {
                int value => BitConverter.GetBytes(value),
                long value => BitConverter.GetBytes(value),
                ulong value => BitConverter.GetBytes(value),
                float value => BitConverter.GetBytes(value),
                string text => System.Text.Encoding.ASCII.GetBytes(text.PadRight(8, '\0')),
                _ => throw new ArgumentException($"unsupported {field.GetType()}"),
            });
        }

        return [.. bytes];
    }

    [Fact]
    public void ACoordinateReadsAsAFloatAndACountAsAWholeNumber()
    {
        // The distinction that makes the view readable, and it works because the WRONG reading
        // of each is absurd: 4366 as a float is 6.1e-42, and 4366.0 as an integer is over a
        // billion. Whichever reading is sane is nearly always the intended one.
        List<StructureSlot> slots = StructureProbe.Describe(Window(4366.0f, 0, 4366, 0), Somewhere);

        Assert.Equal(SlotGuess.Float, slots[0].Guess);
        Assert.Equal(4366.0f, slots[0].FloatLow);

        Assert.Equal(SlotGuess.Integer, slots[1].Guess);
        Assert.Equal(4366, slots[1].Low);
    }

    [Fact]
    public void AnAddressIsMarkedAsSomethingToFollow()
    {
        List<StructureSlot> slots = StructureProbe.Describe(Window(0x1_4000_5000UL), Somewhere);

        Assert.Equal(SlotGuess.Pointer, slots[0].Guess);
        Assert.True(slots[0].Followable);
    }

    [Fact]
    public void AnAddressInsideTheGamesOwnCodeIsNotWorthFollowing()
    {
        // A vtable is the commonest pointer in any structure and the least interesting to
        // descend into - but it is not noise either: it identifies the TYPE of a thing when
        // nothing else does, so it is labelled rather than hidden.
        List<StructureSlot> slots = StructureProbe.Describe(
            Window(0x1_4000_1234UL), Somewhere, moduleBase: 0x1_4000_0000, moduleSize: 0x8000_0000);

        Assert.Equal(SlotGuess.Code, slots[0].Guess);
        Assert.False(slots[0].Followable);
    }

    [Fact]
    public void ANumBERThatMerelyLOOKSLikeAnAddressIsNotCalledOne()
    {
        // The false positive that would have made the whole view useless, and it is not
        // exotic: a coordinate of 4366.0 beside an empty field is 0x45887800, which is a
        // perfectly valid address to try to follow. Two of the commonest rows in any structure
        // are "float, nothing" and "count, nothing", so labelling those pointers would put the
        // wrong word on a large share of every screen.
        List<StructureSlot> slots = StructureProbe.Describe(Window(4366.0f, 0, 12, 0), Somewhere);

        Assert.Equal(SlotGuess.Float, slots[0].Guess);
        Assert.Equal(SlotGuess.Integer, slots[1].Guess);
        Assert.All(slots, slot => Assert.False(slot.Followable));
    }

    [Fact]
    public void EmptySpaceIsCalledEmptyRatherThanZero()
    {
        Assert.Equal(SlotGuess.Empty, StructureProbe.Describe(Window(0L), Somewhere)[0].Guess);
    }

    [Fact]
    public void TextStoredInPlaceIsRecognised()
    {
        Assert.Equal(SlotGuess.Text, StructureProbe.Describe(Window("Fortress"), Somewhere)[0].Guess);
    }

    [Fact]
    public void ArbitraryBytesAreNOTCalledText()
    {
        // The guess that would do real damage. Almost anything can be squinted at as text, and
        // a view that labels random numbers "text" sends you looking for a name that is not
        // there - so one byte outside the printable range disqualifies the row.
        //
        // The value matters: it has to be one that reaches the text check at all. An earlier
        // version of this test used a number whose top half was non-zero, so it was called a
        // pointer long before text was considered - and the test passed with the printable
        // range removed entirely, which is to say it tested nothing.
        Assert.NotEqual(SlotGuess.Text, StructureProbe.Describe(Window(0x0102_0304, 0), Somewhere)[0].Guess);
    }

    [Fact]
    public void ACoupleOfLettersAmongTheNullsIsANumberNotAName()
    {
        // 0x4241 is a perfectly ordinary small integer, and its bytes happen to read "AB".
        // Short runs like that occur constantly, so text needs enough letters to mean
        // something - otherwise the label lands on numbers all over the view.
        Assert.NotEqual(SlotGuess.Text, StructureProbe.Describe(Window("AB"), Somewhere)[0].Guess);
    }

    [Fact]
    public void EveryRowKnowsItsOwnAddress()
    {
        // Because the address is what gets written down, followed, and pasted into the schema.
        List<StructureSlot> slots = StructureProbe.Describe(Window(1, 2, 3, 4), Somewhere);

        Assert.Equal(Somewhere, slots[0].Address);
        Assert.Equal(Somewhere + 8, slots[1].Address);
        Assert.Equal(8, slots[1].Offset);
    }

    [Fact]
    public void FourByteRowsExistBecauseNotEveryFieldSitsOnAnEightByteBoundary()
    {
        // A real case from the schema: CurrentAreaLevel at +0xC4. On eight-byte rows it shares
        // a line with its neighbour and neither reads cleanly.
        List<StructureSlot> slots = StructureProbe.Describe(Window(7, 9), Somewhere, stride: 4);

        Assert.Equal(2, slots.Count);
        Assert.Equal(7, slots[0].Low);
        Assert.Equal(9, slots[1].Low);
        Assert.Equal(4, slots[1].Offset);
    }

    [Fact]
    public void APointerIsNotClaimedOnAFourByteRow()
    {
        // Half a pointer is not a pointer, and following one would go nowhere real.
        List<StructureSlot> slots = StructureProbe.Describe(Window(0x1_4000_5000UL), Somewhere, stride: 4);

        Assert.All(slots, slot => Assert.False(slot.Followable));
    }

    [Fact]
    public void OnlyTheRowsThatCHANGEDAreReported()
    {
        // The most productive thing in the file. Reading an unknown structure tells you
        // almost nothing; reading it before and after doing something tells you which part of
        // it is that thing.
        byte[] before = Window(100, 0, 50, 0, 7, 0);
        byte[] after = Window(100, 0, 42, 0, 7, 0);

        Assert.Equal([8], StructureProbe.Changes(before, after));
    }

    [Fact]
    public void NothingHappeningIsReportedAsNothing()
    {
        byte[] same = Window(1, 2, 3, 4);

        Assert.Empty(StructureProbe.Changes(same, same));
    }

    [Fact]
    public void ComparingWindowsOfDifferentLengthsUsesWhatBothHave()
    {
        // A read can come back short. Comparing what is there beats refusing to compare.
        byte[] shorter = Window(1, 0);
        byte[] longer = Window(1, 0, 99, 0);

        Assert.Empty(StructureProbe.Changes(shorter, longer));
    }

    [Theory]
    [InlineData(0f, false)]              // ambiguous - every empty field reads as this
    [InlineData(1e-42f, false)]          // a small integer misread
    [InlineData(1e12f, false)]           // a bit pattern, not a measurement
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(12750.0f, true)]         // a world coordinate
    [InlineData(-0.751f, true)]          // a camera axis
    [InlineData(1.0f, true)]
    public void AFloatCountsWhenItIsAQuantityRatherThanABitPattern(float value, bool sensible)
        => Assert.Equal(sensible, StructureProbe.SensibleFloat(value));

    [Fact]
    public void ARowWidthOtherThanFourOrEightIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureProbe.Describe(Window(1, 2), Somewhere, stride: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureProbe.Changes(Window(1), Window(2), stride: 7));
    }
}
