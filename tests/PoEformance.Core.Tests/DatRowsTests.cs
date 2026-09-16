using PoEformance.Core.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading a dat table's rows in blocks, and what happens where a block will not read.
/// </summary>
/// <remarks>
/// WHY BLOCKS AT ALL. A table keyed by anything but its row number has to be read whole before the
/// first question can be answered, and field by field that is five round trips into another
/// process per row - more than eighty thousand for Mods, on the thread that also drives the
/// overlay. The rows are contiguous, so one read carries ninety of them.
///
/// WHAT THIS PINS IS THE EDGE. A read that spans a page the process will not hand over fails
/// WHOLE, and the first cut of this skipped the block for it - ninety rows lost to one boundary,
/// silently, with the table still looking read. So a refused block is taken apart and its rows are
/// asked for one at a time, and only the rows that genuinely will not read are missing.
/// </remarks>
public class DatRowsTests
{
    private const int RowSize = 16;
    private const ulong RowsBegin = 0x2_0000_0000;

    private static DatTableFacts Facts(long rows)
        => new("Data/Made/Up.dat", "MadeUp", rows, RowSize, RowsBegin);

    private static byte[] Marked(int rows, int from)
    {
        var bytes = new byte[rows * RowSize];
        for (int index = 0; index < rows; index++)
        {
            bytes[index * RowSize] = (byte)(from + index);
        }

        return bytes;
    }

    [Fact]
    public void EveryRowArrivesInOrderWithItsOwnIndex()
    {
        var reader = new FakeMemoryReader();
        reader.Place(RowsBegin, Marked(4, 0));

        var seen = new List<(int Index, byte First)>();
        long read = DatRows.Walk(reader, Facts(4), (index, bytes) => seen.Add((index, bytes[0])));

        Assert.Equal(4, read);
        Assert.Equal([(0, 0), (1, 1), (2, 2), (3, 3)], seen);
    }

    [Fact]
    public void ABLOCKThatWillNotReadCostsONLYTheRowsThatWillNot()
    {
        // THE EDGE THIS EXISTS FOR. Rows 2 and 3 are not mapped at all, so the four-row block
        // read fails - and the first cut of Walk skipped the whole block for it, losing rows 0
        // and 1 with them. Ninety rows to one boundary in the real thing, silently, with the
        // table still looking read. Verified by putting that shape back and watching this go red.
        //
        // The neighbouring case - a block that spans two mapped regions - cannot be written
        // against FakeMemoryReader, because it composes a read out of every region that covers
        // part of it and so never refuses one. That is a limit of the fixture and it is said here
        // rather than dressed up as a passing test.
        var reader = new FakeMemoryReader();
        reader.Place(RowsBegin, Marked(2, 0));

        var seen = new List<int>();
        long read = DatRows.Walk(reader, Facts(4), (index, _) => seen.Add(index));

        Assert.Equal(2, read);
        Assert.Equal([0, 1], seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void ARowCountThatCannotBeRightIsNotWalkedAtAll(long rows)
    {
        // A wrong RowsBegin or a table that is not this one must not read until something falls
        // over. The bound is on the COUNT rather than on the bytes, because the count is what the
        // table claims about itself and the claim is what might be nonsense.
        var reader = new FakeMemoryReader();
        reader.Place(RowsBegin, Marked(4, 0));

        Assert.Equal(0, DatRows.Walk(reader, Facts(rows), (_, _) => Assert.Fail("walked anyway")));
    }
}
