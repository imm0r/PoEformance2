using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading the atlas's ritual line out of the panel.
/// </summary>
/// <remarks>
/// The offsets are UNVERIFIED and a fixture built from the schema follows the schema anywhere,
/// so nothing here vouches for a single address. What it covers is the walk around them: that a
/// length out of game memory cannot become a loop of any size it likes, that the candidate
/// table's empty slots are dropped rather than read as node (0,0), that a half-loaded table is
/// not cached forever, and that the localised pick counter is read by its digits.
///
/// Those are the parts that turn a wrong offset into a hang or a wrong answer instead of an
/// empty result - which is the difference between a diagnostic and a mystery.
/// </remarks>
public class RitualLineReaderTests
{
    private const ulong Panel = 0x20_0000;
    private const ulong Table = 0x30_0000;
    private const ulong Vector = 0x40_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    private static RitualLineReader ReaderFor(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    /// <summary>A panel in ritual-line mode, with a candidate table of the given entries.</summary>
    private static FakeMemoryReader Line(
        OffsetSchema schema,
        (int X, int Y)[] committed,
        ((int X, int Y) Node, (int X, int Y)[] Next)[] table,
        byte mode = 1,
        uint lineId = 4242)
    {
        StructDef ritual = schema.Structs["AtlasRitual"];
        int stride = (int)ritual.Constants["CandidateEntryStride"];
        var fake = new FakeMemoryReader();

        fake.Place(Panel + (ulong)ritual.OffsetOf("LineMode"), mode);
        fake.Place<uint>(Panel + (ulong)ritual.OffsetOf("LineId"), lineId);

        // The committed vector.
        fake.Place(Panel + (ulong)ritual.OffsetOf("CommittedVector"), Vector);
        fake.Place(Panel + (ulong)ritual.OffsetOf("CommittedVector") + 8, Vector + (8 * (ulong)committed.Length));
        for (int i = 0; i < committed.Length; i++)
        {
            fake.Place(Vector + (8 * (ulong)i), committed[i].X);
            fake.Place(Vector + (8 * (ulong)i) + 4, committed[i].Y);
        }

        // And the candidate table, as one contiguous block of fixed-size entries.
        fake.Place(Panel + (ulong)ritual.OffsetOf("CandidateTableBegin"), Table);
        fake.Place(Panel + (ulong)ritual.OffsetOf("CandidateTableEnd"), Table + ((ulong)stride * (ulong)table.Length));
        fake.Place(Table, new byte[stride * Math.Max(1, table.Length)]);

        for (int e = 0; e < table.Length; e++)
        {
            ulong at = Table + ((ulong)stride * (ulong)e);
            fake.Place(at, table[e].Node.X);
            fake.Place(at + 4, table[e].Node.Y);
            for (int c = 0; c < table[e].Next.Length; c++)
            {
                fake.Place(at + 8 + ((ulong)c * 12), table[e].Next[c].X);
                fake.Place(at + 12 + ((ulong)c * 12), table[e].Next[c].Y);
            }
        }

        return fake;
    }

    [Fact]
    public void NOTHINGIsReadWhileTheLineIsNotBeingDrawn()
    {
        // The mode byte is checked first and nothing else is touched when it is zero, which is
        // almost all of a session. The candidate table alone is a few hundred kilobytes.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Line(schema, [(1, 1)], [((1, 1), [(2, 2)])], mode: 0);

        RitualLine line = ReaderFor(fake, schema).Read(Panel);

        Assert.False(line.Active);
        Assert.Empty(line.Committed);
        Assert.Empty(line.Candidates);
        Assert.Equal(RitualLine.Idle, line);
    }

    [Fact]
    public void ANDNothingIsReadFromAPanelThatIsNotThere()
        => Assert.False(ReaderFor(new FakeMemoryReader(), Schema()).Read(0).Active);

    [Fact]
    public void ALineReadsItsIdAndTheMapsAlreadyOnIt()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Line(schema, [(3, 4), (5, 6)], [((5, 6), [(7, 8)])], lineId: 99);

        RitualLine line = ReaderFor(fake, schema).Read(Panel);

        Assert.True(line.Active);
        Assert.Equal(99u, line.LineId);
        Assert.Equal([(3, 4), (5, 6)], line.Committed);
        Assert.True(line.Started);
    }

    [Fact]
    public void THECandidateTablesEmptySlotsAreDroppedRatherThanReadAsANode()
    {
        // Every entry has room for five candidates and most use fewer; the spare slots are
        // zeroed. Kept, they would all read as a candidate at grid (0,0) - a node that exists,
        // so the line would appear to be able to go there from everywhere.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Line(schema, [(1, 1)],
        [
            ((1, 1), [(2, 2)]),
            ((2, 2), [(3, 3), (4, 4)]),
        ]);

        RitualLine line = ReaderFor(fake, schema).Read(Panel);

        Assert.Equal(2, line.Candidates.Count);
        Assert.Equal([(2, 2)], line.Candidates[(1, 1)]);
        Assert.Equal([(3, 3), (4, 4)], line.Candidates[(2, 2)]);
        Assert.DoesNotContain(line.Candidates.Values.SelectMany(next => next), next => next == (0, 0));
    }

    [Fact]
    public void ATableWhoseLengthIsNotAWholeNumberOfEntriesIsRefused()
    {
        // Which is what a read torn across a resize looks like. Parsed anyway it would walk off
        // the end of the buffer, on the reader thread.
        OffsetSchema schema = Schema();
        StructDef ritual = schema.Structs["AtlasRitual"];
        FakeMemoryReader fake = Line(schema, [(1, 1)], [((1, 1), [(2, 2)])]);

        int stride = (int)ritual.Constants["CandidateEntryStride"];
        fake.Place(Panel + (ulong)ritual.OffsetOf("CandidateTableEnd"), Table + (ulong)stride + 3);

        Assert.Empty(ReaderFor(fake, schema).Read(Panel).Candidates);
    }

    [Fact]
    public void ANDAnAbsurdlyLongOneIsRefusedToo()
    {
        OffsetSchema schema = Schema();
        StructDef ritual = schema.Structs["AtlasRitual"];
        FakeMemoryReader fake = Line(schema, [(1, 1)], [((1, 1), [(2, 2)])]);

        ulong stride = (ulong)(int)ritual.Constants["CandidateEntryStride"];
        fake.Place(
            Panel + (ulong)ritual.OffsetOf("CandidateTableEnd"),
            Table + (stride * ((ulong)RitualLineReader.MostCandidateEntries + 1)));

        Assert.Empty(ReaderFor(fake, schema).Read(Panel).Candidates);
    }

    [Fact]
    public void AHalfLoadedTableIsNotRememberedForTheRestOfTheSession()
    {
        // While an atlas loads, the vector exists and its entries are still zeroed - so every
        // slot parses as the empty-slot sentinel. The pointers do not change afterwards, so
        // caching that emptiness would keep the ritual line blank until a restart.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Line(schema, [(1, 1)],
        [
            ((0, 0), []),
            ((0, 0), []),
        ]);

        RitualLineReader reader = ReaderFor(fake, schema);
        Assert.All(reader.Read(Panel).Candidates.Values, Assert.Empty);

        // The same table, now populated. A cache would still be serving the empty one.
        StructDef ritual = schema.Structs["AtlasRitual"];
        int stride = (int)ritual.Constants["CandidateEntryStride"];
        fake.Place(Table, 1);
        fake.Place(Table + 4, 1);
        fake.Place(Table + 8, 2);
        fake.Place(Table + 12, 2);
        fake.Place(Table + (ulong)stride, 2);
        fake.Place(Table + (ulong)stride + 4, 2);

        RitualLine second = reader.Read(Panel);
        Assert.Equal([(2, 2)], second.Candidates[(1, 1)]);
    }

    [Fact]
    public void THELineLengthComesFromTheStatsAndStartsAtFive()
    {
        var none = new Dictionary<int, int>();
        var line = RitualLine.Idle with { Stats = none };
        Assert.Equal(5, line.Length);

        // Either spelling of the id: the game's ids run one below the data table's, and which
        // this field holds has not been pinned down.
        Assert.Equal(8, (line with { Stats = new Dictionary<int, int> { [0x670B] = 3 } }).Length);
        Assert.Equal(8, (line with { Stats = new Dictionary<int, int> { [0x670C] = 3 } }).Length);

        // A negative would shorten the line below its base, which the game does not do.
        Assert.Equal(5, (line with { Stats = new Dictionary<int, int> { [0x670B] = -4 } }).Length);
    }

    [Theory]
    [InlineData("Select 3 maps", 3)]
    [InlineData("Wähle 3 Karten aus", 3)]
    [InlineData("3", 3)]
    [InlineData("Select 12 maps", 12)]
    [InlineData("", -1)]
    [InlineData("Select maps", -1)]
    [InlineData("Select 0 maps", -1)]
    [InlineData("Select 400 maps", -1)]
    public void THEPickCounterIsReadByItsDigitsSoItHoldsInAnyLanguage(string label, int expected)
    {
        // The header is translated, so anything that reads it by position or by matching words
        // works in English and nowhere else. The number is the only part that does not move.
        Assert.Equal(expected, RitualLineReader.FirstNumberIn(label));
    }
}
