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
        InventorySweep.Report([new InventorySweepFrame(0, seen, [], true, [], false, 0x9000, 0x9100, null)], writer);
        return writer.ToString();
    }

    [Fact]
    public void AListSearchTHATCouldNotRunSaysSoRatherThanReportingNone()
    {
        // THE MISTAKE THIS FLAG EXISTS FOR, pinned. A capture made by a build that never
        // performed the read comes back with an empty list, and an empty list printed as "none"
        // reads as an answer - which cost this line of work a whole round once already.
        var writer = new StringWriter();
        InventorySweep.Report(
            [new InventorySweepFrame(0, [Make(NormalId, (0x40, 1))], [], false, [], false, 0x9000, 0x9100, null)], writer);

        Assert.Contains("NOT SEARCHED", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACandidateHoldingTextIsReportedEvenWithNoPlausibleStride()
    {
        // The count rule can only ever REJECT, and a name list covering a different set of tabs
        // than the inventories would fail it. Text is the evidence, so text alone is enough to
        // be printed - and printed first, with the stars.
        var writer = new StringWriter();
        InventorySweep.Report(
            [
                new InventorySweepFrame(
                    0,
                    [Make(NormalId, (0x40, 1))],
                    [new ParallelList("inner", 0x320, 0x7F00, 0, 0, ["breach"])],
                    true,
                    [],
                    false,
                    0x9000,
                    0x9100,
                    null),
            ],
            writer);

        string said = writer.ToString();
        Assert.Contains("*** inner +0x0320", said, StringComparison.Ordinal);
        Assert.Contains("breach", said, StringComparison.Ordinal);
        Assert.Contains("no plausible stride", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHuntsWindowReachesTheHopAPointerScanActuallyFound()
    {
        // MEASURED, NOT CHOSEN. A player's pointer scan on a live client produced
        // [X + 0x1E0] -> [Y + 0x18] -> the characters, so the hop that reaches the record holding
        // a tab name is at 0x1E0. The hunt shipped reading 0x100 per struct, which cannot see that
        // pointer - it would have walked past the record and reported NOT FOUND, which is the same
        // confident wrong negative as a read that never happened, just produced by a width.
        //
        // Pinned rather than left to a comment because narrowing this back is invisible: the
        // report still looks healthy, it just stops being able to find the thing.
        Assert.True(InventorySweep.HuntWindow > 0x1E0 + 8);

        // And the record is one hop past X, so two is not enough to start from a container.
        Assert.True(InventorySweep.HuntDepth >= 3);
    }

    [Fact]
    public void ANameHuntNOBodyAskedForIsNotReportedAsHavingFoundNothing()
    {
        // The same distinction as NOT SEARCHED, for the one search that can settle the question:
        // a run without --tabname cannot have found the name, and printing that as "not found"
        // would turn a missing argument into evidence.
        string said = Sweep(Make(NormalId, (0x40, 1)));

        Assert.Contains("NOT HUNTED", said, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT FOUND", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameHuntTHATRanAndFoundNothingSaysNOTFOUND()
    {
        // And the other side of it: hunted and empty is a real result, so it says NOT FOUND rather
        // than staying silent.
        //
        // WHAT IT IS NOT is evidence that the name is absent from the stash data, which is what
        // this message used to claim outright. A live pointer scan then produced a working chain
        // whose last hops are +0x3A90 then +0x8 - past the end of every window the hunt reads, so
        // invisible to it at any depth. The wording now bounds the search instead of the game, and
        // that is asserted here so it cannot quietly grow back into a conclusion.
        var writer = new StringWriter();
        InventorySweep.Report(
            [new InventorySweepFrame(0, [Make(NormalId, (0x40, 1))], [], true, [], true, 0x9000, 0x9100, null)], writer);

        string said = writer.ToString();
        Assert.Contains("NOT FOUND", said, StringComparison.Ordinal);
        Assert.Contains("NOT A NEGATIVE RESULT ABOUT THE GAME", said, StringComparison.Ordinal);
    }

    [Fact]
    public void APathToTheNameIsReportedWithTheHopsThatReachedIt()
    {
        // THE ANSWER, if there is one. The path is the deliverable - it is what a schema entry is
        // written from - so it is printed whole rather than summarised.
        var writer = new StringWriter();
        InventorySweep.Report(
            [
                new InventorySweepFrame(
                    0,
                    [Make(NormalId, (0x40, 1))],
                    [],
                    true,
                    [new NameHit("inv 42 +0x0F0 +0x018", 0x7F00, 0x18, "blaaaaffp4ff")],
                    true,
                    0x9000,
                    0x9100,
                    null),
            ],
            writer);

        string said = writer.ToString();
        Assert.Contains("inv 42 +0x0F0 +0x018", said, StringComparison.Ordinal);
        Assert.Contains("blaaaaffp4ff", said, StringComparison.Ordinal);
    }

    /// <summary>A tab scan that found some names, for the report to render.</summary>
    private static string Tabs(TabScan? scan)
    {
        var writer = new StringWriter();
        InventorySweep.Report(
            [
                new InventorySweepFrame(
                    0, [Make(NormalId, (0x40, 1))], [], true, [], false, 0x9000, 0x9100, scan),
            ],
            writer);
        return writer.ToString();
    }

    [Fact]
    public void ABlockTHATCouldNotBeReadIsNotReportedAsHavingNoTabs()
    {
        // The same distinction the whole report insists on, now for the tab array: a block nobody
        // could read has not been shown to be empty.
        string said = Tabs(new TabScan(0x7F00, 0, 0, [], 0, Read: false));

        Assert.Contains("NOT READ", said, StringComparison.Ordinal);
        Assert.DoesNotContain("none found", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ATabRecordsPointerTHATDidNotResolveSaysSoRatherThanReportingNothing()
    {
        string said = Tabs(null);

        Assert.Contains("NOT REACHED", said, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNameIsPrintedAndTheFilledOnesAreMarked()
    {
        // The Filled column is the open question - every entry examined by hand held an empty
        // vector - so a full one has to be visible at a glance rather than counted by hand.
        string said = Tabs(new TabScan(
            0x7F00,
            0x3A90,
            3,
            [
                new StashTab(0x3A90, 0x8000, "getTheNameNO", Filled: false),
                new StashTab(0x3AA8, 0x8100, "Armor I", Filled: true),
                new StashTab(0x3AC0, 0x8200, "1", Filled: false),
            ],
            0x8000,
            Read: true));

        Assert.Contains("getTheNameNO", said, StringComparison.Ordinal);
        Assert.Contains("* Armor I", said, StringComparison.Ordinal);
        Assert.Contains("  1", said, StringComparison.Ordinal);
        Assert.Contains("(1 followed by a non-empty vector)", said, StringComparison.Ordinal);

        // THE SPACING IS REPORTED, not required. Three names 0x18 apart give two gaps of 0x18,
        // and printing that is what lets a person tell an array from a scatter - the judgement
        // the scan used to make for them, wrongly.
        Assert.Contains("gaps between them: 0x18x2", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ScatteredNamesReportTheirScatterRatherThanBeingDiscarded()
    {
        // The old scan required a run and so found nothing at all on a live client. Names at
        // irregular offsets are still names; what changes is that the gaps say so.
        string said = Tabs(new TabScan(
            0x7F00,
            0x40,
            2,
            [
                new StashTab(0x40, 0x8000, "curr", Filled: false),
                new StashTab(0x2C0, 0x8100, "ritu", Filled: false),
            ],
            0x8000,
            Read: true));

        Assert.Contains("curr", said, StringComparison.Ordinal);
        Assert.Contains("ritu", said, StringComparison.Ordinal);
        Assert.Contains("gaps between them: 0x280x1", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AllEmptyVectorsIsSaidOutLoudBecauseItIsTheOpenQuestion()
    {
        // Finding every name and no contents is a result, not a silence: it says the vector's
        // meaning is still unknown, which is exactly what the schema records.
        string said = Tabs(new TabScan(
            0x7F00, 0, 1, [new StashTab(0, 0x8000, "curr", Filled: false)], 0x8000, Read: true));

        Assert.Contains("NO entry has a non-empty vector", said, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingFoundNamesTheBytesActuallyREADRatherThanTheWindowSize()
    {
        // THE MISTAKE THIS PINS cost a live run. The scan asked for the whole window in one
        // all-or-nothing read, fell back to a quarter of it when the block ended earlier, and
        // then printed the window CONSTANT in its "nothing found" line - so the report described
        // a search over 0x8000 bytes that had actually covered 0x2000, missing the one entry
        // known to exist at +0x3A90 entirely.
        //
        // A reach that disagrees with the constant is exactly the case that has to read
        // correctly, so the fake here is deliberately short.
        string said = Tabs(new TabScan(0x7F00, 0, 0, [], 0x2000, Read: true));

        Assert.Contains("0x2000 bytes readable", said, StringComparison.Ordinal);
        Assert.DoesNotContain($"0x{InventorySweep.TabWindow:X} bytes", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlockIsReadInPiecesSmallEnoughToNotOvershootTheOneKnownEntry()
    {
        // A chunk has to divide the distance to the answer finely enough that a block ending
        // just past it still yields it. 0x3A90 was reachable and 0x2000 was not - so a chunk
        // that large can lose it again.
        Assert.True(InventorySweep.TabChunk <= 0x1000);
        Assert.Equal(0, InventorySweep.TabWindow % InventorySweep.TabChunk);
    }

    [Fact]
    public void TheWindowReachesPastTheOneOffsetAPointerScanEverProduced()
    {
        // 0x3A90 is where a live pointer scan put a record, so a window that stops before it
        // cannot answer anything - and one that did exactly that has already been shipped once.
        Assert.True(InventorySweep.TabWindow > 0x3A90 + InventorySweep.TabStride);
    }

    [Fact]
    public void TheStrideIsAnObservationAndNoLongerDecidesWhatCounts()
    {
        // THE CORRECTION, pinned so it cannot come back. Three records 0x18 apart were seen once,
        // and that became a filter: runs of at least four entries shaped {record*, vector} at
        // exactly 0x18. On a live client with the whole block readable it found NOTHING, so the
        // shape those three shared does not generalise - three points with a line drawn through
        // them. The block is not even a tab array: several unrelated vectors on the inner
        // server-data struct have their storage inside it.
        //
        // The scan assumes no shape now. It follows pointers and keeps the ones landing on a
        // std::wstring, which is recognisable rather than assumed, and REPORTS the spacing.
        // 0x18 survives only as the number that observation produced.
        Assert.Equal(0x18, InventorySweep.TabStride);

        // A run rule would be the old mistake returning, so there is nothing to configure.
        Assert.DoesNotContain(
            "ShortestRun",
            typeof(InventorySweep).GetFields().Select(one => one.Name),
            StringComparer.Ordinal);
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
