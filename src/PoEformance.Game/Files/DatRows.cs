using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Game.Files;

/// <summary>
/// Walks a loaded dat table's rows in blocks, so a whole table costs reads in the hundreds.
/// </summary>
/// <remarks>
/// WHY BLOCKS AND NOT A ROW AT A TIME. A table keyed by anything other than its row number has to
/// be read WHOLE before the first question can be answered - an item names itself by path, a mod
/// by id - and read field by field that is arithmetic nobody notices multiplied by a row count
/// that they do. Mods is 16784 rows: its two strings and a word are five reads each, so more than
/// eighty thousand round trips into another process, on a thread that also drives the overlay.
///
/// The rows are CONTIGUOUS, which is the whole opportunity - RowsBegin plus index times RowSize,
/// with no padding anywhere - so one read of sixty-four kilobytes carries ninety of Mods' rows and
/// the pointers are picked out of the buffer in this process for nothing. Eighty thousand reads
/// become about a hundred and eighty.
///
/// A BLOCK THAT WILL NOT READ IS SKIPPED RATHER THAN ENDING THE WALK, because the reason is
/// usually a page boundary partway through a table rather than a bad address, and the rows after
/// it are as good as the rows before it. What it costs is the rows in that block, and the caller
/// finds out by counting what came back.
/// </remarks>
public static class DatRows
{
    /// <summary>
    /// How much is asked for at once. Big enough that the read count stops mattering, small
    /// enough that one refused block is a handful of rows rather than the table.
    /// </summary>
    private const int BlockBytes = 64 * 1024;

    /// <summary>A bound on the rows walked, so a wrong RowsBegin cannot read until it falls over.</summary>
    private const long MostRows = 200_000;

    /// <summary>
    /// Hands each row's bytes to <paramref name="onRow"/>, in order, and says how many arrived.
    /// </summary>
    /// <remarks>
    /// The span is the shared buffer and is only valid for the call, which is why this takes a
    /// callback rather than handing back a list: a table read this way is turned into whatever
    /// the caller actually wants - two strings, a word - without ever holding eleven megabytes.
    /// </remarks>
    public static long Walk(IMemoryReader reader, DatTableFacts facts, Action<int, ReadOnlySpan<byte>> onRow)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(onRow);

        if (facts.Rows <= 0 || facts.Rows > MostRows || facts.RowSize <= 0 || facts.RowSize > BlockBytes)
        {
            return 0;
        }

        int size = (int)facts.RowSize;
        int perBlock = BlockBytes / size;
        var buffer = new byte[perBlock * size];

        long read = 0;
        for (long first = 0; first < facts.Rows; first += perBlock)
        {
            int rows = (int)Math.Min(perBlock, facts.Rows - first);
            Span<byte> block = buffer.AsSpan(0, rows * size);

            if (reader.TryRead(facts.RowsBegin + (ulong)(first * size), block))
            {
                for (int index = 0; index < rows; index++)
                {
                    onRow((int)(first + index), block.Slice(index * size, size));
                    read++;
                }

                continue;
            }

            // A BLOCK THAT WILL NOT READ COSTS ONE ROW, NOT NINETY. The usual reason is a page
            // boundary partway through, and the rows either side of it are as good as any other -
            // so the block is taken apart rather than abandoned, which is the same bargain
            // ReadUnicodeString makes when it halves a request that ran off the end of a page.
            for (int index = 0; index < rows; index++)
            {
                Span<byte> one = block.Slice(index * size, size);
                if (reader.TryRead(facts.RowsBegin + (ulong)((first + index) * size), one))
                {
                    onRow((int)(first + index), one);
                    read++;
                }
            }
        }

        return read;
    }
}
