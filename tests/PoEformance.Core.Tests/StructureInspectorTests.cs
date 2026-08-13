using PoEformance.Core.Diagnostics;
using PoEformance.Core.Schema;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Serving the dissector: reading a window, and remembering what it used to say.
/// </summary>
/// <remarks>
/// The remembering is the point. A window of bytes on its own is nearly useless - the whole
/// method is to note what it says, do something in the game, and look at what moved. So most
/// of what is worth testing here is about WHEN a comparison is meaningful and when it would
/// be noise dressed up as a finding.
/// </remarks>
public class StructureInspectorTests
{
    private const ulong Somewhere = 0x2000_0000_0000;
    private const ulong Elsewhere = 0x3000_0000_0000;
    private const ulong Yonder = 0x4000_0000_0000;
    private const ulong Beyond = 0x5000_0000_0000;

    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json"));
    }

    private static StructureInspector Inspector(PoEformance.Core.Memory.IMemoryReader reader)
        => new(reader, Schema(), gameStatesStatic: 0);

    private static byte[] Bytes(params int[] values)
    {
        var bytes = new List<byte>();
        foreach (int value in values)
        {
            bytes.AddRange(BitConverter.GetBytes(value));
        }

        return [.. bytes];
    }

    /// <summary>
    /// Places values inside a region big enough to read a whole window out of.
    /// </summary>
    /// <remarks>
    /// The padding is not decoration. A read only succeeds when EVERY byte asked for is
    /// there, so a region cut off at the last interesting value fails the read entirely and
    /// the test then proves nothing - which is a test artefact, since a real structure sits
    /// inside a much larger allocation.
    /// </remarks>
    private static void Fill(FakeMemoryReader reader, ulong address, params int[] values)
    {
        reader.Place(address, new byte[512]);
        reader.Place(address, Bytes(values));
    }

    /// <summary>Asks for one window and returns what came back.</summary>
    private static StructureView Look(StructureInspector inspector, StructureRequest request)
    {
        inspector.Request(request);
        inspector.Service();
        return inspector.View;
    }

    private static StructureRequest At(ulong address, int size = 32)
        => new(Enabled: true, Root: StructureRoot.Custom, Address: address, Size: size);

    [Fact]
    public void NothingIsReadUntilTheWindowIsOpen()
    {
        // A dissector nobody is looking at must cost the reader nothing - it shares its thread
        // with the world read and with auto-flask.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 2, 3, 4);

        StructureInspector inspector = Inspector(reader);
        StructureView view = Look(inspector, At(Somewhere) with { Enabled = false });

        Assert.Empty(view.Slots);
    }

    [Fact]
    public void AWindowIsReadIntoRowsThatKnowWhereTheyLive()
    {
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 9, 0, 11, 0, 13, 0);

        StructureView view = Look(Inspector(reader), At(Somewhere));

        Assert.Equal(Somewhere, view.Address);
        Assert.Equal(4, view.Slots.Count);
        Assert.Equal(7, view.Slots[0].Low);
        Assert.Equal(Somewhere + 8, view.Slots[1].Address);
    }

    [Fact]
    public void AnAddressWithNothingBehindItIsReportedRatherThanThrown()
    {
        // Pointing this at nonsense is an ORDINARY thing to do - every address in the tool is
        // one the user typed or followed, and a wrong turn must cost a message, not the run.
        StructureView view = Look(Inspector(new FakeMemoryReader()), At(Somewhere));

        Assert.Empty(view.Slots);
        Assert.Contains("nothing readable", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkingThenChangingSomethingShowsWHICHPartChanged()
    {
        // The whole method, in one test. Mark, act, and the answer is the rows that moved.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 100, 0, 50, 0, 7, 0, 1, 0);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });

        reader.Place(Somewhere + 8, Bytes(42));            // the second row, and only it
        StructureView after = Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });

        Assert.True(after.HasBaseline);
        Assert.Equal([8], after.SinceBaseline.Order());
    }

    [Fact]
    public void TheMarkIsTakenONCERatherThanEveryTick()
    {
        // Served every tick it would re-mark continuously, nothing would ever look changed,
        // and the tool would quietly do the exact opposite of its job.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });

        reader.Place(Somewhere + 8, Bytes(99));
        Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });
        StructureView third = Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });

        Assert.Equal([8], third.SinceBaseline.Order());
    }

    [Fact]
    public void MarkingAgainForgetsTheOldMark()
    {
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });

        reader.Place(Somewhere + 8, Bytes(99));
        StructureView after = Look(inspector, At(Somewhere) with { SnapshotSequence = 2 });

        Assert.Empty(after.SinceBaseline);
    }

    [Fact]
    public void AMarkTakenSomewhereElseIsNotComparedAgainstTHISPlace()
    {
        // The comparison that would be pure noise. After following a pointer, every row
        // differs from the previous place's bytes - so an address-blind diff would light the
        // whole screen and read as a discovery.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);
        Fill(reader, Elsewhere, 9, 0, 8, 0);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere) with { SnapshotSequence = 1 });
        StructureView moved = Look(inspector, At(Elsewhere) with { SnapshotSequence = 1 });

        Assert.False(moved.HasBaseline);
        Assert.Empty(moved.SinceBaseline);

        // And the same for the tick-to-tick comparison, which has the identical problem: the
        // previous window belongs to the previous PLACE. Both windows are the same size, so
        // nothing but an address check stops them being compared - and the result would be
        // every differing row lit on arrival, which reads as a discovery and is not one.
        Assert.Empty(moved.Live);
    }

    [Fact]
    public void WhatMovedSinceTheLastTickIsReportedSeparately()
    {
        // A different question from the mark, and both are worth asking: this one says what is
        // live AT ALL, which is how a timer or a position announces itself without any action.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere));

        reader.Place(Somewhere, Bytes(5));
        StructureView after = Look(inspector, At(Somewhere));

        Assert.Equal([0], after.Live.Order());
    }

    [Fact]
    public void TheFirstLookAtAPlaceReportsNothingAsMoving()
    {
        // There is nothing to compare against yet, and inventing a comparison would mark
        // every row on arrival - the one moment the marks certainly mean nothing.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);

        Assert.Empty(Look(Inspector(reader), At(Somewhere)).Live);
    }

    [Fact]
    public void AKnownLayoutPutsTheSchemasOwnNamesOnTheRows()
    {
        // Which is also how a MISALIGNED read announces itself: every row gets a name and none
        // of them make sense.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);

        StructureView view = Look(
            Inspector(reader),
            At(Somewhere, 512) with { StructName = "TileStruct" });

        // From the schema: SubTileDetailsPtr at +0x00 and TgtFilePtr at +0x08.
        Assert.Equal("SubTileDetailsPtr", view.FieldNames[0]);
        Assert.Equal("TgtFilePtr", view.FieldNames[8]);
    }

    [Fact]
    public void FieldsSharingARowAreBOTHNamed()
    {
        // TileIdX at +0x34 and TileIdY at +0x35 are single bytes. On eight-byte rows they
        // share a line with TileHeight, and showing only one of them would hide the others -
        // the offsets get carried so it stays clear which is which.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);

        StructureView view = Look(
            Inspector(reader),
            At(Somewhere, 512) with { StructName = "TileStruct" });

        string row = view.FieldNames[0x30];
        Assert.Contains("TileHeight", row, StringComparison.Ordinal);
        Assert.Contains("TileIdX(+0x4)", row, StringComparison.Ordinal);
        Assert.Contains("TileIdY(+0x5)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownLayoutNamesNothingRatherThanFailing()
    {
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 2);

        Assert.Empty(Look(Inspector(reader), At(Somewhere) with { StructName = "NoSuchStruct" }).FieldNames);
    }

    [Fact]
    public void FollowingAPointerSaysWhatIsOnTheOtherEnd()
    {
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, BitConverter.GetBytes(Elsewhere));
        reader.Place(Elsewhere, new byte[256]);
        reader.PlaceUtf16(Elsewhere, "MapFortress");

        StructureView view = Look(
            Inspector(reader),
            At(Somewhere) with { PeekOffset = 0, PeekSequence = 1 });

        Assert.NotNull(view.Peek);
        Assert.Equal(TargetKind.WideText, view.Peek.Kind);
        Assert.Equal(0, view.PeekOffset);
    }

    [Fact]
    public void AnAbsurdWindowSizeIsBroughtBackToSomethingSensible()
    {
        // Not about speed - a few kilobytes is nothing. It bounds how WRONG one request can
        // be: an address typed a digit out asks for a window over unmapped memory.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[StructureInspector.MaxSize * 2]);

        StructureView view = Look(Inspector(reader), At(Somewhere, size: 1_000_000));

        Assert.Equal(StructureInspector.MaxSize / 8, view.Slots.Count);
    }

    [Fact]
    public void SomethingPointerSHAPEDThatLeadsNowhereIsNotOfferedAsAPointer()
    {
        // Seen live, and it is not exotic: a row holding the two ordinary integers 4 and 531
        // is, read as one number, over four gigabytes - which is all the SHAPE of a value can
        // ever be asked. The bytes genuinely cannot tell the two apart. Reading can.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, 0x0000_0004_0000_0213UL);

        StructureView view = Look(Inspector(reader), At(Somewhere));

        Assert.NotEqual(SlotGuess.Pointer, view.Slots[0].Guess);
        Assert.False(view.Slots[0].Followable);
    }

    [Fact]
    public void APointerThatLeadsSomewhereRealSTAYSAPointer()
    {
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, Elsewhere);
        reader.Place(Elsewhere, new byte[64]);

        StructureView view = Look(Inspector(reader), At(Somewhere));

        Assert.Equal(SlotGuess.Pointer, view.Slots[0].Guess);
        Assert.True(view.Slots[0].Followable);
    }

    [Fact]
    public void AnAddressIsOnlyCheckedONCE()
    {
        // The window is re-read every tick while the dissector is open, so checking every
        // candidate every time would multiply the reads on the thread that also drives
        // auto-flask. Whether an address is mapped does not change from one tick to the next.
        var reader = new CountingReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, 0x0000_0004_0000_0213UL);

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere));
        int afterFirst = reader.Reads;
        Look(inspector, At(Somewhere));

        // One read for the window itself, and nothing more for the candidate it already knows.
        Assert.Equal(1, afterFirst - 1);
        Assert.Equal(afterFirst + 1, reader.Reads);
    }

    [Fact]
    public void EveryComponentHasTwoNamedRowsEvenWhenNothingElseIsKnownAboutIt()
    {
        // The entity browser opens an undescribed component under this layout, by NAME - so a
        // rename in the schema would silently produce a window with no names at all, on
        // exactly the components where names are worth the most.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);

        StructureView view = Look(Inspector(reader), At(Somewhere, 512) with { StructName = "Component" });

        Assert.Equal("StaticPtr", view.FieldNames[0]);
        Assert.Equal("OwnerEntity", view.FieldNames[8]);
    }

    [Fact]
    public void AShortStringShowsWithoutAnyExtraReading()
    {
        // It lives in the row, so the window read already has it. Anything more would be
        // paying for something already in hand.
        var reader = new CountingReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, Inline("Chest"));

        StructureView view = Look(Inspector(reader), At(Somewhere, 64));

        Assert.Equal("Chest", view.Text[0]);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void ALongStringIsFetchedFromWhereItsCharactersLive()
    {
        // The case that matters most: every metadata path in this game is one of these, and
        // without it a name reads as a pointer and two meaningless numbers over four rows.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, HeapHeader("Metadata/Monsters/MudGolem", Elsewhere));
        reader.Place(Elsewhere, new byte[512]);
        reader.PlaceUtf16(Elsewhere, "Metadata/Monsters/MudGolem");

        StructureView view = Look(Inspector(reader), At(Somewhere, 64));

        Assert.Equal("Metadata/Monsters/MudGolem", view.Text[0]);
    }

    [Fact]
    public void AStringIsOnlyFetchedONCE()
    {
        var reader = new CountingReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, HeapHeader("Metadata/Monsters/MudGolem", Elsewhere));
        reader.Place(Elsewhere, new byte[512]);
        reader.PlaceUtf16(Elsewhere, "Metadata/Monsters/MudGolem");

        StructureInspector inspector = Inspector(reader);
        Look(inspector, At(Somewhere, 64));
        int afterFirst = reader.Reads;
        StructureView again = Look(inspector, At(Somewhere, 64));

        // One read for the window, and nothing more for a string already fetched.
        Assert.Equal(afterFirst + 1, reader.Reads);
        Assert.Equal("Metadata/Monsters/MudGolem", again.Text[0]);
    }

    [Fact]
    public void RowsThatAreNotTextSayNothing()
    {
        // Silence is the right answer, and the important one: a name invented over ordinary
        // numbers sends someone looking for something that is not there.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 100, 0, 50, 0, 7, 0, 1, 0);

        Assert.Empty(Look(Inspector(reader), At(Somewhere, 64)).Text);
    }

    private static byte[] Inline(string text)
    {
        var bytes = new byte[32];
        System.Text.Encoding.Unicode.GetBytes(text, bytes.AsSpan(0, 16));
        BitConverter.GetBytes((long)text.Length).CopyTo(bytes, 16);
        BitConverter.GetBytes(7L).CopyTo(bytes, 24);
        return bytes;
    }

    private static byte[] HeapHeader(string text, ulong data)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(data).CopyTo(bytes, 0);
        BitConverter.GetBytes((long)text.Length).CopyTo(bytes, 16);
        BitConverter.GetBytes((long)text.Length).CopyTo(bytes, 24);
        return bytes;
    }

    [Fact]
    public void SeveralPlacesAreReadAtOnceAndEachSaysWhatItHolds()
    {
        // The comparison the dissector exists for: one structure in two instances, held side
        // by side. Neither window says anything the other does not - that is the point.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 11, 0);
        Fill(reader, Elsewhere, 7, 0, 99, 0);

        StructureView view = Look(Inspector(reader), At(Somewhere) with { Compared = [Elsewhere] });

        Assert.Equal(2, view.Columns.Count);
        Assert.Equal(Somewhere, view.Columns[0].Address);
        Assert.Equal(Elsewhere, view.Columns[1].Address);
        Assert.Equal(11, view.Columns[0].Slots[1].Low);
        Assert.Equal(99, view.Columns[1].Slots[1].Low);
    }

    [Fact]
    public void WhereThePlacesDISAGREEIsTheWholeFinding()
    {
        // What two monsters share is the species - the vtable, the class pointer, the dat row
        // they were built from. What they do not share is the individual. From one window
        // both are just bytes, and no amount of staring separates them.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 11, 0, 4, 0);
        Fill(reader, Elsewhere, 7, 0, 99, 0, 4, 0);

        StructureView view = Look(Inspector(reader), At(Somewhere) with { Compared = [Elsewhere] });

        Assert.Equal([8], view.Differing.Order());
    }

    [Fact]
    public void OnePlaceOnItsOwnDisagreesWithNothing()
    {
        // A structure agrees with itself. Reporting every row as differing - or none of them
        // as matching - would put a finding on the screen that means nothing at all.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 11, 0);

        Assert.Empty(Look(Inspector(reader), At(Somewhere)).Differing);
    }

    [Fact]
    public void APlaceThatCannotBeReadDoesNotTakeTheOthersDownWithIt()
    {
        // An address goes stale on every area change, and a comparison assembled over a few
        // minutes will have one. The rest of it is still worth having, and a place that reads
        // nothing must not silently count as one that agrees with nothing either.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 11, 0);

        StructureView view = Look(Inspector(reader), At(Somewhere) with { Compared = [Elsewhere] });

        Assert.Equal(2, view.Columns.Count);
        Assert.NotEmpty(view.Columns[0].Slots);
        Assert.Empty(view.Columns[1].Slots);
        Assert.Contains("nothing readable", view.Columns[1].Status, StringComparison.Ordinal);
        Assert.Empty(view.Differing);
    }

    [Fact]
    public void TheMarkIsTakenInEveryPlaceAtTheSameINSTANT()
    {
        // Marked one place at a time, the set of marks would come from different moments and
        // the comparison against them would be between things that never coexisted.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 1, 0, 2, 0);
        Fill(reader, Elsewhere, 1, 0, 2, 0);

        StructureInspector inspector = Inspector(reader);
        StructureRequest marked = At(Somewhere) with { Compared = [Elsewhere], SnapshotSequence = 1 };
        Look(inspector, marked);

        reader.Place(Elsewhere + 8, Bytes(42));            // the second place, and only it
        StructureView after = Look(inspector, marked);

        Assert.True(after.Columns[0].HasBaseline);
        Assert.True(after.Columns[1].HasBaseline);
        Assert.Empty(after.Columns[0].SinceBaseline);
        Assert.Equal([8], after.Columns[1].SinceBaseline.Order());
    }

    [Fact]
    public void WhatAPointerLeadsToIsAnsweredPerPlace()
    {
        // The fastest way there is to name an unknown row: peek at it with two monsters open
        // and "Skeleton" beside "Zombie" settles what it is in one click. Answered once for
        // the first place, it would name the row after whichever monster happened to be left.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);
        reader.Place(Somewhere, Elsewhere);
        reader.Place(Yonder, new byte[512]);
        reader.Place(Yonder, Beyond);
        reader.Place(Elsewhere, new byte[256]);
        reader.PlaceUtf16(Elsewhere, "Skeleton");
        reader.Place(Beyond, new byte[256]);
        reader.PlaceUtf16(Beyond, "Zombie");

        StructureView view = Look(
            Inspector(reader),
            At(Somewhere) with { Compared = [Yonder], PeekOffset = 0, PeekSequence = 1 });

        Assert.Contains("Skeleton", view.Columns[0].Peek?.Summary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Zombie", view.Columns[1].Peek?.Summary ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void MorePlacesThanAreServedAreLeftUnread()
    {
        // Every place is a full window read every tick, on the thread that also drives the
        // world read and auto-flask. The cap is what stops a list of pinned addresses from
        // quietly becoming a cost nobody chose.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);

        var compared = new ulong[StructureInspector.MaxColumns + 4];
        for (int i = 0; i < compared.Length; i++)
        {
            compared[i] = Somewhere;
        }

        StructureView view = Look(Inspector(reader), At(Somewhere) with { Compared = compared });

        Assert.Equal(StructureInspector.MaxColumns, view.Columns.Count);
    }

    [Fact]
    public void OnlyTheFirstPlaceCanBlameARootForHavingNoAddress()
    {
        // A pinned place that came out zero is a pointer that led nowhere, and saying
        // "AreaInstance did not resolve" over it would send someone looking at the game state
        // instead of at the hop they just took.
        var reader = new FakeMemoryReader();
        reader.Place(Somewhere, new byte[512]);

        StructureView view = Look(
            Inspector(reader),
            At(Somewhere) with { Root = StructureRoot.Custom, Compared = [0UL] });

        Assert.Equal("no address", view.Columns[1].Status);
    }

    [Fact]
    public void TheSinglePlaceViewStillReadsAsOneWindow()
    {
        // Everything else in the tool asks this view for slots, text and an address without
        // knowing places exist. The first place IS that view, and it stays that way.
        var reader = new FakeMemoryReader();
        Fill(reader, Somewhere, 7, 0, 11, 0);
        Fill(reader, Elsewhere, 1, 0, 2, 0);

        StructureView view = Look(Inspector(reader), At(Somewhere) with { Compared = [Elsewhere] });

        Assert.Equal(Somewhere, view.Address);
        Assert.Equal(view.Columns[0].Slots, view.Slots);
    }

    [Fact]
    public void TheKnownStructuresAreOfferedInAStableOrder()
    {
        // They fill a dropdown, and a list that reshuffles between frames cannot be clicked.
        List<string> names = [.. Inspector(new FakeMemoryReader()).KnownStructures];

        Assert.Contains("AreaInstance", names);
        Assert.Equal([.. names.Order(StringComparer.Ordinal)], names);
    }
}

/// <summary>A fake reader that counts how many reads it was asked for.</summary>
/// <remarks>
/// Exists for one question: is a check being repeated that should have been remembered? That
/// cannot be seen from the answers - only from how many times the process was asked.
/// </remarks>
internal sealed class CountingReader : PoEformance.Core.Memory.IMemoryReader
{
    private readonly FakeMemoryReader _inner = new();

    public int Reads { get; private set; }

    public bool IsAttached => _inner.IsAttached;

    public int ProcessId => _inner.ProcessId;

    public ulong ModuleBase => _inner.ModuleBase;

    public uint ModuleSize => _inner.ModuleSize;

    public void Place(ulong address, params byte[] bytes) => _inner.Place(address, bytes);

    public void Place<T>(ulong address, T value)
        where T : unmanaged => _inner.Place(address, value);

    public void PlaceUtf16(ulong address, string text) => _inner.PlaceUtf16(address, text);

    public bool TryRead(ulong address, Span<byte> destination)
    {
        Reads++;
        return _inner.TryRead(address, destination);
    }

    public void Dispose() => _inner.Dispose();
}
