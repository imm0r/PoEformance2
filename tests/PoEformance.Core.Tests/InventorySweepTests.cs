using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the sweep's candidate hunt may and may not report.
/// </summary>
/// <remarks>
/// THE FIRST RUN OF IT REPORTED SIXTY-ODD OFFSETS and almost every one was the upper half of a
/// 64-bit pointer. Two things were wrong and both are pinned here: the test asked how MANY
/// distinct values a field took rather than what those values WERE, and nothing rejected a field
/// that was half of an address. With two heap regions in play those halves take exactly two
/// values, pass a "few values" rule perfectly, and agree across any two rows half the time by
/// chance - which is the weak structural fingerprint this project keeps being warned about.
/// </remarks>
public class InventorySweepTests
{
    private const int NormalId = 100;

    /// <summary>An inventory whose head is zeroes, for a test to plant one field in.</summary>
    private static InventoryObservation Make(int id, params (int Offset, uint Value)[] fields)
        => Shaped(id, 12, 12, fields);

    /// <summary>The same, with a grid shape - which is what the type test groups by.</summary>
    private static InventoryObservation Shaped(
        int id, int columns, int rows, params (int Offset, uint Value)[] fields)
    {
        var head = new byte[InventorySweep.Window];
        var slot = new byte[InventorySweep.EntryWindow];

        foreach ((int offset, uint value) in fields)
        {
            BitConverter.TryWriteBytes(head.AsSpan(offset), value);
        }

        return new InventoryObservation(
            id, 0x1000, slot, 0x2000, head, columns, rows, columns * rows, []);
    }

    private static string Sweep(params InventoryObservation[] seen)
    {
        var writer = new StringWriter();
        InventorySweep.Report([new InventorySweepFrame(0, seen)], writer);
        return writer.ToString();
    }

    [Fact]
    public void AFieldWhoseValuesFitStashTypeAndSplitsTheShopPagesOffIsReported()
    {
        // What a type field would look like: a small number, the same on both shop pages,
        // different on an ordinary tab.
        string said = Sweep(
            Make(int.MinValue, (0x40, 7)),
            Make(int.MinValue + 1, (0x40, 7)),
            Make(NormalId, (0x40, 2)),
            Make(NormalId + 1, (0x40, 2)));

        Assert.Contains("+0x040", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldHOLDINGValuesTooLargeForStashTypeIsNotACandidate()
    {
        // THE CORRECTION. Two distinct values is "few values" by any counting rule, and the shop
        // pages agree - but 0x38F is not a row of a table with 25 of them, so it says nothing
        // about sort. The old rule counted the values and let this through.
        string said = Sweep(
            Make(int.MinValue, (0x40, 0x38F)),
            Make(int.MinValue + 1, (0x40, 0x38F)),
            Make(NormalId, (0x40, 0x390)),
            Make(NormalId + 1, (0x40, 0x390)));

        Assert.DoesNotContain("+0x040", said, StringComparison.Ordinal);
    }

    [Fact]
    public void HalfOfAnAddressIsNotACandidateEvenWhenItsValuesAreSmall()
    {
        // The trap in its purest form: a plausible pointer whose LOW half happens to be a small
        // number. Values 8 and 16 pass the range rule, the shop pages agree, and the field is
        // still nothing but bytes of an address - which is why the eight bytes it sits inside
        // are what gets tested, not the four it occupies.
        string said = Sweep(
            Make(int.MinValue, (0x40, 8), (0x44, 0x38F)),
            Make(int.MinValue + 1, (0x40, 8), (0x44, 0x38F)),
            Make(NormalId, (0x40, 16), (0x44, 0x390)),
            Make(NormalId + 1, (0x40, 16), (0x44, 0x390)));

        Assert.DoesNotContain("+0x040", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldTHESHOPPagesDisagreeOnIsNotACandidate()
    {
        // They are the same sort by construction - confirmed against the Merchant window - so a
        // field that tells them apart is not telling sorts apart.
        string said = Sweep(
            Make(int.MinValue, (0x40, 3)),
            Make(int.MinValue + 1, (0x40, 4)),
            Make(NormalId, (0x40, 5)));

        Assert.DoesNotContain("+0x040", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldConstantWithinAGridShapeAndDifferentAcrossThemIsReported()
    {
        // WHAT A TYPE HAS TO LOOK LIKE, and the test that rests on something rather than on an
        // assumption: a type DECIDES a layout - a currency stash is 37x10 and nothing else is -
        // so it cannot vary among tabs sharing a shape and must vary between shapes.
        //
        // A BYTE at an ODD offset, because that is the shape the first pass could not see: it
        // stepped four at a time, and a twenty-five row enum most likely fits one byte.
        string said = Sweep(
            Shaped(1, 12, 12, (0x2D, 4)),
            Shaped(2, 12, 12, (0x2D, 4)),
            Shaped(3, 37, 10, (0x2D, 9)),
            Shaped(4, 24, 24, (0x2D, 1)));

        Assert.Contains("+0x02D w1", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldTHATVariesAmongTabsOfOneShapeIsNotACandidate()
    {
        // Two tabs of one shape disagreeing rules the field out whatever else it does: a type
        // that differed between two 12x12 tabs would not have made them both 12x12.
        string said = Sweep(
            Shaped(1, 12, 12, (0x2D, 4)),
            Shaped(2, 12, 12, (0x2D, 5)),
            Shaped(3, 37, 10, (0x2D, 9)));

        Assert.DoesNotContain("+0x02D w1", said, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTwoShopPagesTheReportSaysItCannotSettleAnything()
    {
        // The control group is what makes the check a check. Its absence looks exactly like
        // "no candidate survived", so it is said out loud rather than left to be inferred.
        string said = Sweep(Make(NormalId, (0x40, 1)), Make(NormalId + 1, (0x40, 2)));

        Assert.Contains("NO CONTROL GROUP", said, StringComparison.Ordinal);
    }
}
