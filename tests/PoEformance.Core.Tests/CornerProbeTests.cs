using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The corner probe: three bytes per tile corner, and whether any of them names a ground type.
/// </summary>
/// <remarks>
/// WHAT THESE ARE GUARDING AGAINST is the previous round rather than a bug. A landscape nibble
/// was taken for an index into the area's ground-type list and was not one, and what settled it
/// was counting every value that occurs rather than only the ones that fit. So the thing to hold
/// still here is that a value the list cannot name is COUNTED and SAID - a probe that quietly
/// ignored them would report a clean reading of nonsense.
/// </remarks>
public class CornerProbeTests
{
    private const ulong Vector = 0x0000_0400_0000_0000;
    private const ulong Data = 0x0000_0400_0010_0000;

    private static readonly string[] Types =
    [
        string.Empty,                                       // every area's list starts blank
        "Metadata/Terrain/Desert/Badlands/bone_fill.gt",
        "Metadata/Terrain/Desert/Badlands/bone_abyss.gt",
    ];

    /// <summary>An area of tilesX by tilesY whose corner array is filled by the caller.</summary>
    private static FakeMemoryReader Area(int tilesX, int tilesY, Func<long, int, byte> lane)
    {
        long corners = (long)(tilesX + 1) * (tilesY + 1);
        var data = new byte[corners * CornerProbe.BytesPerCorner];
        for (long c = 0; c < corners; c++)
        {
            for (int b = 0; b < CornerProbe.BytesPerCorner; b++)
            {
                data[(c * CornerProbe.BytesPerCorner) + b] = lane(c, b);
            }
        }

        return new FakeMemoryReader()
            .Place(Data, data)
            .Place<ulong>(Vector, Data)
            .Place<ulong>(Vector + 8, Data + (ulong)data.Length);
    }

    [Fact]
    public void TheArrayIsIdentifiedByItsSizeAndNothingElse()
    {
        // (tilesX+1) * (tilesY+1) * 3 is the whole identification. An array of another size is
        // not this array, and histogramming it anyway is how a drifted offset produces a
        // plausible-looking reading of something else entirely.
        FakeMemoryReader memory = Area(8, 4, (_, _) => 0);

        IReadOnlyList<string> lines = new CornerProbe(memory).Probe(Vector, 9, 4, Types);

        Assert.Contains("not that array", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EachLaneIsCountedSeparately()
    {
        // Byte 0 constant, byte 1 alternating, byte 2 a value no list entry can name - three
        // different shapes, so a probe that mixed the lanes could not report this.
        FakeMemoryReader memory = Area(8, 4, (c, b) => b switch
        {
            0 => 1,
            1 => (byte)(c % 2),
            _ => 11,
        });

        IReadOnlyList<string> lines = new CornerProbe(memory).Probe(Vector, 8, 4, Types);

        Assert.Contains("corners: 45 at 9x5", lines[0], StringComparison.Ordinal);
        Assert.Contains("1 values", lines[1], StringComparison.Ordinal);
        Assert.Contains("2 values", lines[2], StringComparison.Ordinal);
        Assert.Contains("1 values", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void AValueTheListCannotNameIsCountedAndSaid()
    {
        // THE CHECK THE PREVIOUS ROUND TURNED ON. A lane whose values all fall inside the list
        // is the shape an index has; one that does not is not an index, and the count is what
        // says so. Folding those into silence is what left the last verdict unactionable.
        FakeMemoryReader memory = Area(8, 4, (_, b) => b == 2 ? (byte)11 : (byte)1);

        IReadOnlyList<string> lines = new CornerProbe(memory).Probe(Vector, 8, 4, Types);

        Assert.Contains("0 outside the list", lines[1], StringComparison.Ordinal);
        Assert.Contains("45 outside the list", lines[3], StringComparison.Ordinal);
        Assert.Contains("beyond", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void AValueTheListCanNameIsNamed()
    {
        FakeMemoryReader memory = Area(8, 4, (_, b) => b == 0 ? (byte)1 : (byte)0);

        IReadOnlyList<string> lines = new CornerProbe(memory).Probe(Vector, 8, 4, Types);

        Assert.Contains("bone_fill", lines[1], StringComparison.Ordinal);

        // The BLANK first slot is named as such rather than left looking like a missing entry:
        // a value of zero means no type, not the first type, and the two read the same on a
        // screen unless one of them says so.
        Assert.Contains("blank slot", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyVectorSaysSoRatherThanReadingNothing()
    {
        var memory = new FakeMemoryReader()
            .Place<ulong>(Vector, 0UL)
            .Place<ulong>(Vector + 8, 0UL);

        Assert.Contains(
            "empty vector",
            Assert.Single(new CornerProbe(memory).Probe(Vector, 8, 4, Types)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableArrayIsNotReportedAsData()
    {
        // The buffer's unread bytes are zero, and counting those as corners is how a histogram
        // comes back "all zero" about memory nobody read. Refused instead.
        var memory = new FakeMemoryReader()
            .Place<ulong>(Vector, Data)
            .Place<ulong>(Vector + 8, Data + (9 * 5 * 3));

        Assert.Contains(
            "unreadable",
            Assert.Single(new CornerProbe(memory).Probe(Vector, 8, 4, Types)),
            StringComparison.Ordinal);
    }
}
