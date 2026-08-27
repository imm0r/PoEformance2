using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Walking the game's loaded-file list, against a process built to look like one.
/// </summary>
/// <remarks>
/// The two static ADDRESSES need the game; the walk between them does not. So the whole
/// structure is built here out of the same offsets the reader uses - a root of sixteen
/// buckets, vectors of slots, records with a name and the area-change count that loaded them -
/// and the thing being checked is that the reader finds what was planted and refuses what was
/// not.
///
/// This is the part that would otherwise fail silently in front of somebody: an off-by-one in
/// the slot size or a wrong bucket stride does not crash, it just returns nothing, and
/// "nothing" is also what a wrong static address returns.
/// </remarks>
public class PreloadReaderTests
{
    private const ulong RootStatic = 0x10_0000;
    private const ulong Root = 0x20_0000;
    private const ulong CounterStatic = 0x11_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>Builds a loaded-file list in synthetic memory, exactly as the game lays it out.</summary>
    private sealed class Files
    {
        private readonly FakeMemoryReader _memory = new();
        private readonly OffsetSchema _schema;
        private readonly int _bucketSize;
        private readonly int _slotSize;
        private ulong _next = 0x100_0000;

        internal Files(OffsetSchema schema)
        {
            _schema = schema;
            _bucketSize = (int)schema.Structs["LoadedFilesRoot"].Constants["BucketSize"];
            _slotSize = (int)schema.Structs["FileRecordSlot"].Constants["Size"];

            _memory.Place(RootStatic, Root);
        }

        internal FakeMemoryReader Memory => _memory;

        internal void Counter(int value) => _memory.Place(CounterStatic, value);

        /// <summary>Fills one bucket with records, each loaded at the count given.</summary>
        internal void Bucket(int index, params (string Path, int LoadedAt)[] records)
        {
            ulong bucket = Root + (ulong)(index * _bucketSize);
            ulong slots = Take(_slotSize * Math.Max(1, records.Length));

            _memory.Place(bucket, slots);
            _memory.Place(bucket + 8, slots + (ulong)(_slotSize * records.Length));
            _memory.Place(bucket + (ulong)_schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), records.Length);

            for (int i = 0; i < records.Length; i++)
            {
                ulong slot = slots + (ulong)(i * _slotSize);
                (string path, int loadedAt) = records[i];

                if (path.Length == 0)
                {
                    _memory.Place(slot + (ulong)_schema.Structs["FileRecordSlot"].OffsetOf("Record"), 0UL);
                    continue;   // an empty hash slot, which is the ordinary case
                }

                ulong record = Take(0x80);
                _memory.Place(slot + (ulong)_schema.Structs["FileRecordSlot"].OffsetOf("Record"), record);
                _memory.Place(
                    record + (ulong)_schema.Structs["FileRecord"].OffsetOf("AreaChangeCount"), loadedAt);
                _memory.PlaceStdWString(
                    record + (ulong)_schema.Structs["FileRecord"].OffsetOf("Name"), path, Take(512));
            }
        }

        /// <summary>An empty bucket - most of them are.</summary>
        internal void EmptyBucket(int index)
        {
            ulong bucket = Root + (ulong)(index * _bucketSize);
            _memory.Place(bucket + (ulong)_schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), 0);
        }

        private ulong Take(int bytes)
        {
            ulong at = _next;
            _next += (ulong)((bytes + 0xFF) & ~0xFF);
            return at;
        }
    }

    [Fact]
    public void ItFindsWhatTHISAreaLoaded()
    {
        var files = new Files(Schema());
        files.Counter(7);
        files.Bucket(0, ("Metadata/Terrain/Leagues/Breach/BreachObject", 7));
        for (int i = 1; i < 16; i++)
        {
            files.EmptyBucket(i);
        }

        var reader = new PreloadReader(files.Memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, reader.AreaChangeCount(CounterStatic));

        Assert.Contains("Metadata/Terrain/Leagues/Breach/BreachObject", found);
    }

    [Fact]
    public void AndNothingTheLASTAreaLoaded()
    {
        // The whole feature is this one comparison. Without it the list is every file in
        // memory - the menu, the character, the last three zones - and says nothing about
        // where you are standing.
        var files = new Files(Schema());
        files.Counter(7);
        files.Bucket(0,
            ("Metadata/Terrain/Leagues/Breach/BreachObject", 7),
            ("Metadata/Terrain/Leagues/Ritual/RitualAltar", 6));
        for (int i = 1; i < 16; i++)
        {
            files.EmptyBucket(i);
        }

        var reader = new PreloadReader(files.Memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 7);

        Assert.Single(found);
        Assert.DoesNotContain("Metadata/Terrain/Leagues/Ritual/RitualAltar", found);
    }

    [Fact]
    public void EVERYBucketIsWalked()
    {
        // Sixteen of them, and the stride between them is a number from the reference. Get it
        // wrong and the reader still works - it just quietly finds a fraction of the list,
        // which reads as "this area has nothing in it".
        var files = new Files(Schema());
        files.Counter(9);
        for (int i = 0; i < 16; i++)
        {
            files.Bucket(i, ($"Metadata/Bucket{i}/Thing", 9));
        }

        var reader = new PreloadReader(files.Memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 9);

        Assert.Equal(16, found.Count);
        Assert.Contains("Metadata/Bucket15/Thing", found);
    }

    [Fact]
    public void EmptySlotsAreOrdinaryRatherThanAFailure()
    {
        // A hash table is mostly empty. Treating a null slot as a problem would abandon the
        // bucket at its first hole and find almost nothing.
        var files = new Files(Schema());
        files.Counter(4);
        files.Bucket(0, ("", 0), ("", 0), ("Metadata/Real/File", 4), ("", 0));
        for (int i = 1; i < 16; i++)
        {
            files.EmptyBucket(i);
        }

        var reader = new PreloadReader(files.Memory, Schema());

        Assert.Contains("Metadata/Real/File", reader.Read(RootStatic, 4));
    }

    [Fact]
    public void AVariantSuffixIsDropped()
    {
        // The game appends a marker to some records, so one encounter turns up several times
        // under names differing by nothing anybody cares about.
        Assert.Equal("Metadata/Thing", PreloadReader.Tidy("Metadata/Thing@3"));
        Assert.Equal("Metadata/Thing", PreloadReader.Tidy("Metadata/Thing"));
    }

    [Fact]
    public void TheFirstAreasOfASessionAreRefusedRatherThanDumped()
    {
        // Everything in memory was loaded during them, so every file would match and the
        // "list" would be the whole game. Saying so beats three thousand paths.
        var files = new Files(Schema());
        files.Counter(1);
        files.Bucket(0, ("Metadata/Anything", 1));

        var reader = new PreloadReader(files.Memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 1);

        Assert.Empty(found);
        Assert.Contains("areas loaded so far", reader.LastError, StringComparison.Ordinal);
        Assert.Equal(1, reader.Newest);
    }

    [Fact]
    public void AnUnresolvedRootIsSaidRatherThanCrashed()
    {
        var memory = new FakeMemoryReader();
        var reader = new PreloadReader(memory, Schema());

        Assert.Empty(reader.Read(RootStatic, 9));
        Assert.Contains("did not resolve", reader.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void ARIDICULOUSSlotCountIsRefused()
    {
        // The vector's length comes from game memory. A drifted layout makes the end pointer
        // arbitrary, and the difference between "a bad read" and "walk until the process
        // falls over" is the guard.
        var files = new Files(Schema());
        files.Counter(5);
        files.Bucket(0, ("Metadata/Thing", 5));

        // Push the end pointer absurdly far past the start.
        files.Memory.Place(Root + 8, 0x7000_0000_0000UL);
        for (int i = 1; i < 16; i++)
        {
            files.EmptyBucket(i);
        }

        var reader = new PreloadReader(files.Memory, Schema());
        reader.Read(RootStatic, 5);

        Assert.Contains("drifted", reader.LastError, StringComparison.Ordinal);
        Assert.True(reader.SlotsWalked < PreloadReader.MostSlotsPerBucket);
    }
}


/// <summary>
/// Finding the count field instead of assuming one.
/// </summary>
/// <remarks>
/// Written the moment the walk came back "13948 slots and matched nothing at count 188". That
/// sentence has several causes wanting opposite fixes - the record struct moved, the count
/// sits elsewhere, or the game counts areas differently from how the comparison assumes - and
/// one number cannot tell them apart.
///
/// So the sweep asks two questions of the same read: do the NAMES come back as paths, which
/// says whether the record base is right at all, and which offsets hold the counter's value.
/// Between them there is no case left that reads as "nothing".
/// </remarks>
public class PreloadSweepTests
{
    private const ulong RootStatic = 0x10_0000;
    private const ulong Root = 0x20_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>A table whose count sits at a CHOSEN offset rather than the expected one.</summary>
    private static FakeMemoryReader TableWithCountAt(int countOffset, int count, int records)
    {
        var memory = new FakeMemoryReader();
        OffsetSchema schema = Schema();
        int bucketSize = (int)schema.Structs["LoadedFilesRoot"].Constants["BucketSize"];
        int slotSize = (int)schema.Structs["FileRecordSlot"].Constants["Size"];
        int slotRecord = schema.Structs["FileRecordSlot"].OffsetOf("Record");
        int name = schema.Structs["FileRecord"].OffsetOf("Name");

        memory.Place(RootStatic, Root);

        ulong next = 0x100_0000;
        ulong Take(int bytes)
        {
            ulong at = next;
            next += (ulong)((bytes + 0xFF) & ~0xFF);
            return at;
        }

        ulong slots = Take(slotSize * records);
        memory.Place(Root, slots);
        memory.Place(Root + 8, slots + (ulong)(slotSize * records));
        memory.Place(Root + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), records);

        for (int i = 0; i < records; i++)
        {
            ulong record = Take(0x200);
            memory.Place(record, new byte[0x200]);   // a whole mapped record, as the game has
            memory.Place(slots + (ulong)(i * slotSize) + (ulong)slotRecord, record);
            memory.Place(record + (ulong)countOffset, count);
            memory.PlaceStdWString(record + (ulong)name, $"Metadata/Terrain/Thing{i}", Take(512));
        }

        for (int b = 1; b < 16; b++)
        {
            memory.Place(
                Root + (ulong)(b * bucketSize)
                    + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"),
                0);
        }

        return memory;
    }

    [Fact]
    public void ItNamesTheOffsetTheWHOLETableAgreesOn()
    {
        // The answer, when there is one. A field a thousand records share is the field; a
        // stray match is one record.
        // Under the per-bucket share, so every planted record is sampled. The sweep spreads
        // its budget across the buckets rather than filling from the first - see the reader.
        FakeMemoryReader memory = TableWithCountAt(countOffset: 0x58, count: 188, records: 200);
        var reader = new PreloadReader(memory, Schema());

        PreloadReader.PreloadSweep swept = reader.Sweep(RootStatic, 188);

        Assert.Equal(0x58, swept.CountAt[0].Offset);
        Assert.Equal(200, swept.CountAt[0].Agreeing);
    }

    [Fact]
    public void ReadableNamesSayTheRECORDIsRight()
    {
        // The half that separates "the count moved" from "we are not looking at records at
        // all" - and the two want completely different work.
        FakeMemoryReader memory = TableWithCountAt(countOffset: 0x58, count: 188, records: 50);
        var reader = new PreloadReader(memory, Schema());

        PreloadReader.PreloadSweep swept = reader.Sweep(RootStatic, 188);

        Assert.Equal(50, swept.Records);
        Assert.Equal(50, swept.Named);
        Assert.NotEmpty(swept.Samples);
        Assert.StartsWith("Metadata/", swept.Samples[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AndGIBBERISHNamesSayItIsNot()
    {
        // Records that are not records. Without this the sweep would report "no offset holds
        // the counter" and send somebody hunting a field in a struct that was never there.
        var memory = new FakeMemoryReader();
        OffsetSchema schema = Schema();
        int slotSize = (int)schema.Structs["FileRecordSlot"].Constants["Size"];

        memory.Place(RootStatic, Root);
        memory.Place(Root, 0x100_0000UL);
        memory.Place(Root + 8, 0x100_0000UL + (ulong)(slotSize * 10));
        memory.Place(Root + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), 10);

        for (int i = 0; i < 10; i++)
        {
            ulong garbage = 0x200_0000UL + (ulong)(i * 0x200);
            memory.Place(garbage, new byte[0x200]);
            memory.Place(
                0x100_0000UL + (ulong)(i * slotSize)
                    + (ulong)schema.Structs["FileRecordSlot"].OffsetOf("Record"),
                garbage);
        }

        var reader = new PreloadReader(memory, Schema());
        PreloadReader.PreloadSweep swept = reader.Sweep(RootStatic, 188);

        Assert.True(swept.Records > 0, "the slots still led somewhere readable");
        Assert.Equal(0, swept.Named);
    }

    [Fact]
    public void ItAlsoSaysWhatTheOffsetInUseHOLDS()
    {
        // The case that is not a drifted offset at all: the records agree on a number NEAR
        // the counter, because the game counts areas differently from how the comparison
        // assumes. Reported separately, because the fix is a comparison rather than an offset.
        int inUse = Schema().Structs["FileRecord"].OffsetOf("AreaChangeCount");
        FakeMemoryReader memory = TableWithCountAt(countOffset: inUse, count: 187, records: 120);
        var reader = new PreloadReader(memory, Schema());

        PreloadReader.PreloadSweep swept = reader.Sweep(RootStatic, 188);

        Assert.Empty(swept.CountAt);
        Assert.Equal(187, swept.NearbyValues[0].Value);
        Assert.Equal(120, swept.NearbyValues[0].Records);
    }

    [Fact]
    public void APathIsToldFromGibberish()
    {
        Assert.True(PreloadReader.LooksLikeAPath("Metadata/Terrain/Thing"));
        Assert.True(PreloadReader.LooksLikeAPath("Art/Models/Ground"));
        Assert.False(PreloadReader.LooksLikeAPath("nope"));
        Assert.False(PreloadReader.LooksLikeAPath(string.Empty));
        Assert.False(PreloadReader.LooksLikeAPath("a/"));
    }
}

/// <summary>
/// The table decides which area is current, not a separate counter.
/// </summary>
/// <remarks>
/// Written after the live run settled it. The counter static read 188 while every record in
/// the table held a number two orders of magnitude smaller, and the paths those records handed
/// back were real game files - so the table was right and the static was reading something
/// else. Files are stamped when they load and nothing re-stamps them, which makes the NEWEST
/// stamp in the table the current area by construction.
/// </remarks>
public class PreloadNewestStampTests
{
    private const ulong RootStatic = 0x10_0000;
    private const ulong Root = 0x20_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>A table where each bucket's records carry a stamp of their own.</summary>
    private static FakeMemoryReader TableWith(params (string Path, int LoadedAt)[] records)
    {
        var memory = new FakeMemoryReader();
        OffsetSchema schema = Schema();
        int bucketSize = (int)schema.Structs["LoadedFilesRoot"].Constants["BucketSize"];
        int slotSize = (int)schema.Structs["FileRecordSlot"].Constants["Size"];

        memory.Place(RootStatic, Root);

        ulong next = 0x100_0000;
        ulong Take(int bytes)
        {
            ulong at = next;
            next += (ulong)((bytes + 0xFF) & ~0xFF);
            return at;
        }

        ulong slots = Take(slotSize * records.Length);
        memory.Place(Root, slots);
        memory.Place(Root + 8, slots + (ulong)(slotSize * records.Length));
        memory.Place(Root + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), records.Length);

        for (int i = 0; i < records.Length; i++)
        {
            ulong record = Take(0x200);
            memory.Place(record, new byte[0x200]);
            memory.Place(
                slots + (ulong)(i * slotSize) + (ulong)schema.Structs["FileRecordSlot"].OffsetOf("Record"),
                record);
            memory.Place(
                record + (ulong)schema.Structs["FileRecord"].OffsetOf("AreaChangeCount"), records[i].LoadedAt);
            memory.PlaceStdWString(
                record + (ulong)schema.Structs["FileRecord"].OffsetOf("Name"), records[i].Path, Take(512));
        }

        for (int b = 1; b < 16; b++)
        {
            memory.Place(
                Root + (ulong)(b * bucketSize)
                    + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"),
                0);
        }

        return memory;
    }

    [Fact]
    public void TheNEWESTStampIsTheCurrentArea()
    {
        // No counter involved. Startup files carry an early stamp forever, the area you are
        // standing in carries the latest one, and nothing in between needs asking.
        FakeMemoryReader memory = TableWith(
            ("Data/Balance/BaseItemTypes.dat", 2),
            ("Data/Balance/FlavourText.dat", 2),
            ("Metadata/Terrain/Leagues/Breach/BreachObject", 9),
            ("Metadata/Terrain/Maps/Riverhold", 9),
            ("Metadata/Terrain/LastArea/Thing", 8));

        var reader = new PreloadReader(memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 0);

        Assert.Equal(9, reader.Newest);
        Assert.Equal(2, found.Count);
        Assert.Contains("Metadata/Terrain/Leagues/Breach/BreachObject", found);
        Assert.DoesNotContain("Data/Balance/BaseItemTypes.dat", found);
        Assert.DoesNotContain("Metadata/Terrain/LastArea/Thing", found);
    }

    [Fact]
    public void AWRONGCounterCannotBreakIt()
    {
        // The live failure, exactly: a counter reading 188 against a table whose newest stamp
        // is 9. The old shape compared against the counter and found nothing at all.
        FakeMemoryReader memory = TableWith(
            ("Data/Balance/BaseItemTypes.dat", 2),
            ("Metadata/Terrain/Maps/Riverhold", 9));

        var reader = new PreloadReader(memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 188);

        Assert.Single(found);
        Assert.Equal(9, reader.Newest);
        Assert.Equal(188, reader.Counter);
    }

    [Fact]
    public void ONEGarbageRecordCannotTakeTheListWithIt()
    {
        // The newest stamp decides everything, so a single record reading a huge number would
        // make every real file older than it and the list empty. A session does not reach a
        // hundred thousand areas.
        FakeMemoryReader memory = TableWith(
            ("Metadata/Terrain/Maps/Riverhold", 9),
            ("Metadata/Terrain/Maps/Other", 9),
            ("Metadata/Garbage", 900_000));

        var reader = new PreloadReader(memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 0);

        Assert.Equal(9, reader.Newest);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void ATableThatCannotBeWalkedSaysSORatherThanNoAreasYet()
    {
        // Both arrive with a newest stamp of zero, and they are completely different problems.
        // "No areas loaded yet" over a drifted layout points at the wrong thing entirely.
        var memory = new FakeMemoryReader();
        OffsetSchema schema = Schema();
        int slotSize = (int)schema.Structs["FileRecordSlot"].Constants["Size"];

        memory.Place(RootStatic, Root);
        memory.Place(Root, 0x100_0000UL);
        memory.Place(Root + 8, 0x100_0000UL + (ulong)(slotSize * 5_000_000L));
        memory.Place(Root + (ulong)schema.Structs["LoadedFilesBucket"].OffsetOf("Capacity"), 10);

        var reader = new PreloadReader(memory, Schema());
        reader.Read(RootStatic, 0);

        Assert.Contains("drifted", reader.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void ASTAMPFieldThatMovedIsNotReportedAsAYoungSession()
    {
        // The second live failure, and the nastier one. Once the counter static was fixed it
        // read 13, but the stamp was still being read one field past where it lives, so every
        // record looked like a 2 and the walk reported "only 2 areas loaded so far - the list
        // is still the whole game". That is a real state with a real message, it just was not
        // THIS state, and the wording sent the search in the wrong direction entirely.
        //
        // The counter is the witness. It knows the session is thirteen areas old, so a table
        // claiming two is not a young session, it is a table being read in the wrong place.
        FakeMemoryReader memory = TableWith(
            ("Data/Balance/BaseItemTypes.dat", 2),
            ("Data/Balance/FlavourText.dat", 2));

        var reader = new PreloadReader(memory, Schema());
        HashSet<string> found = reader.Read(RootStatic, 13);

        Assert.Empty(found);
        Assert.Contains("moved", reader.LastError, StringComparison.Ordinal);
        Assert.Contains("13", reader.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("whole game", reader.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealYoungSessionStillSaysSo()
    {
        // The other side of it. When the counter AGREES that barely anything has loaded, the
        // early-session message is the right one and must survive - a warning about a moved
        // field every time somebody starts the tool at the login screen would be noise, and
        // noise is how a real warning stops being read.
        FakeMemoryReader memory = TableWith(
            ("Data/Balance/BaseItemTypes.dat", 2),
            ("Data/Balance/FlavourText.dat", 2));

        var reader = new PreloadReader(memory, Schema());
        reader.Read(RootStatic, 2);

        Assert.Contains("whole game", reader.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("moved", reader.LastError, StringComparison.Ordinal);
    }
}
/// <summary>
/// Matching the curated list against what an area loaded.
/// </summary>
/// <remarks>
/// The list this replaced matched FRAGMENTS, and its tests were mostly about the defences that
/// needed - a file count to tell a mechanic from a mention, and a list of paths that could not
/// testify at all. Neither survives here, and that is the point of the change rather than a gap
/// in the cover: an exact path is its own evidence, and nothing matches unless somebody put it
/// in the list on purpose.
/// </remarks>
public class PreloadWatchTests
{
    private static readonly PreloadAlertEntry Breach =
        new("Metadata/Terrain/Leagues/Breach/BreachObject", "Breach");

    private static readonly PreloadAlertEntry Ritual =
        new("Metadata/Terrain/Leagues/Ritual/RitualAltar", "Ritual");

    [Fact]
    public void APathInTheListIsFound()
    {
        var watch = new PreloadWatch();
        watch.Watch([Breach, Ritual]);
        watch.Took(1, [Breach.Path, "Metadata/Terrain/Dungeon/Rocks/Rock_01"]);

        Assert.Equal(["Breach"], watch.Found.Select(entry => entry.Shown));
    }

    [Fact]
    public void APathTHATMERELYCONTAINSItIsNot()
    {
        // THE WHOLE REASON THE MATCH IS EXACT. A fragment of "/Breach/" caught the mechanic in
        // one line and also caught every file that merely mentions it - which on the first live
        // run reported eight league mechanics in one map, and no map has eight.
        var watch = new PreloadWatch();
        watch.Watch([Breach]);
        watch.Took(1, [Breach.Path + "Variant", "Art/Textures/Leagues/Breach/Pin.dds"]);

        Assert.Empty(watch.Found);
    }

    [Fact]
    public void CaseIsNotWhatDecides()
    {
        // A path that differs only in case would match nothing and say nothing about why, and a
        // setting whose only symptom is silence is what this project has already paid for once.
        var watch = new PreloadWatch();
        watch.Watch([Breach with { Path = Breach.Path.ToUpperInvariant() }]);
        watch.Took(1, [Breach.Path]);

        Assert.Single(watch.Found);
    }

    [Fact]
    public void TheListsOwnOrderIsThePriority()
    {
        var watch = new PreloadWatch();
        watch.Watch([Ritual, Breach]);
        watch.Took(1, [Breach.Path, Ritual.Path]);

        // Ritual first because it is first in the LIST, not because of anything about the area:
        // the loaded paths arrive in whatever order the game held them.
        Assert.Equal(["Ritual", "Breach"], watch.Found.Select(entry => entry.Shown));
    }

    [Fact]
    public void ADisabledEntryIsKeptAndNotActedOn()
    {
        var watch = new PreloadWatch();
        watch.Watch([Breach with { Enabled = false }]);
        watch.Took(1, [Breach.Path]);

        Assert.Empty(watch.Found);
        Assert.Single(watch.Watching);
    }

    [Fact]
    public void TheRawListIsKeptToo()
    {
        // The way the watch list grows is somebody reading what an area actually loaded on the
        // day the tool had nothing to say about it. Losing the raw paths loses that.
        var watch = new PreloadWatch();
        watch.Watch([Breach]);
        watch.Took(1, ["Metadata/Terrain/Dungeon/Rocks/Rock_01", Breach.Path]);

        Assert.Equal(2, watch.All.Count);
    }

    [Fact]
    public void ANEWAreaTakesTheOldFindingsWithIt()
    {
        var watch = new PreloadWatch();
        watch.Watch([Breach]);
        watch.Took(1, [Breach.Path]);
        Assert.Single(watch.Found);

        watch.Took(2, ["Metadata/Terrain/Dungeon/Rocks/Rock_01"]);
        Assert.Empty(watch.Found);
    }

    [Fact]
    public void ForgettingClearsBothHalves()
    {
        var watch = new PreloadWatch();
        watch.Watch([Breach]);
        watch.Took(7, [Breach.Path]);
        watch.Forget();

        Assert.False(watch.Looked);
        Assert.Equal(0u, watch.Area);
        Assert.Empty(watch.All);
        Assert.Empty(watch.Found);

        // The list is not an area's property and must survive one.
        Assert.Single(watch.Watching);
    }

    [Fact]
    public void SummaryNamesWhatIsHere()
    {
        var watch = new PreloadWatch();
        watch.Watch([Breach, Ritual]);
        watch.Took(1, [Ritual.Path, Breach.Path]);

        Assert.Equal("Breach, Ritual", watch.Summary());
        Assert.Equal(string.Empty, new PreloadWatch().Summary());
    }
}

/// <summary>One entry, and what it says about itself.</summary>
public class PreloadAlertEntryTests
{
    [Fact]
    public void AnEntryWithNoPathLooksForNOTHING()
    {
        // It arrives from a hand-edited file and from an add box somebody hit return in, and
        // the one thing this feature must never do is match everything.
        Assert.True(new PreloadAlertEntry("").SaysNothing);
        Assert.True(new PreloadAlertEntry("   ").SaysNothing);
        Assert.False(new PreloadAlertEntry("Metadata/X").SaysNothing);
    }

    [Fact]
    public void ANamelessEntryShowsItsFileRatherThanItsPath()
    {
        // A path is eighty characters of folders and one word that means something, and the
        // window it goes in is a corner of the screen read at a glance.
        Assert.Equal(
            "BreachObject",
            new PreloadAlertEntry("Metadata/Terrain/Leagues/Breach/BreachObject").Shown);

        Assert.Equal("Breach", new PreloadAlertEntry("Metadata/X", "Breach").Shown);
        Assert.Equal("Metadata", new PreloadAlertEntry("Metadata").Shown);
    }

    [Fact]
    public void ASuggestedNameDropsTheFoldersAndTheExtension()
    {
        Assert.Equal("Hand", PreloadMeanings.Suggest("Metadata/Terrain/Leagues/Breach/Hand.ao"));
        Assert.Equal("Exile01", PreloadMeanings.Suggest("Metadata/Monsters/ExileLeague/Exile01"));
        Assert.Equal("Thing", PreloadMeanings.Suggest("Thing"));
    }

    [Fact]
    public void FoundIsEmptyWhenEitherSideIs()
    {
        Assert.Empty(PreloadAlerts.Found(null, ["a"]));
        Assert.Empty(PreloadAlerts.Found([new PreloadAlertEntry("a")], null));
        Assert.Empty(PreloadAlerts.Found([], ["a"]));
        Assert.Empty(PreloadAlerts.Found([new PreloadAlertEntry("a")], []));
    }

    [Fact]
    public void HereAnswersTheSameQuestionAsFound()
    {
        // The editor asks it per row, so that a typo is visible as "this matches nothing here"
        // rather than as an area that simply does not have the thing.
        Assert.True(PreloadAlerts.Here("Metadata/X", PreloadAlerts.Lookup(["metadata/x"])));
        Assert.False(PreloadAlerts.Here("Metadata/X", PreloadAlerts.Lookup(["Metadata/Y"])));
        Assert.False(PreloadAlerts.Here("", PreloadAlerts.Lookup(["Metadata/X"])));
        Assert.False(PreloadAlerts.Here("Metadata/X", null));

        // Built once per frame rather than per row, so an empty area answers as nothing at all
        // rather than as a set nobody can be in.
        Assert.Null(PreloadAlerts.Lookup(null));
        Assert.Null(PreloadAlerts.Lookup([]));
    }

    [Fact]
    public void ALoggedLineCarriesTheAreaRatherThanItsName()
    {
        // Two runs of the same map share a name and nothing else, so the hash is what says
        // which instance a line belongs to.
        string line = PreloadAlerts.LogLine(
            new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero),
            4242,
            new PreloadAlertEntry("Metadata/X", "Breach"));

        Assert.Contains("2026-08-27 09:30:00", line, StringComparison.Ordinal);
        Assert.Contains("area 4242", line, StringComparison.Ordinal);
        Assert.Contains("Breach", line, StringComparison.Ordinal);
        Assert.Contains("Metadata/X", line, StringComparison.Ordinal);
    }
}

/// <summary>Adding to the list from the window that shows the raw paths.</summary>
public class PreloadEditingTests
{
    [Fact]
    public void ANEWEntryReReadsTheAreaAlreadyInHand()
    {
        // The walk costs a whole read budget and the raw list cannot change while you stand in
        // an area - so adding an entry has to show its line without reloading the area, or the
        // editor is one nobody trusts.
        var watch = new PreloadWatch();
        watch.Took(1, ["Metadata/Terrain/Leagues/Breach/BreachObject"]);
        Assert.Empty(watch.Found);

        Assert.True(watch.Add(new PreloadAlertEntry("Metadata/Terrain/Leagues/Breach/BreachObject", "Breach")));
        Assert.Equal(["Breach"], watch.Found.Select(entry => entry.Shown));
    }

    [Fact]
    public void THESamePathIsNotAddedTwice()
    {
        // Adding is one click on a row of a list of thousands, so the same row gets clicked
        // twice. A repeat would draw the same line twice while only one could be edited.
        var watch = new PreloadWatch();

        Assert.True(watch.Add(new PreloadAlertEntry("Metadata/X", "One")));
        Assert.False(watch.Add(new PreloadAlertEntry("Metadata/X", "Two")));
        Assert.False(watch.Add(new PreloadAlertEntry("METADATA/X", "Three")));
        Assert.Single(watch.Watching);
        Assert.Equal("One", watch.Watching[0].Shown);
    }

    [Fact]
    public void AnEntryThatLooksForNothingIsRefused()
    {
        var watch = new PreloadWatch();

        Assert.False(watch.Add(new PreloadAlertEntry("")));
        Assert.Empty(watch.Watching);
    }

    [Fact]
    public void REPLACINGTheListTakesItsFindingsWithIt()
    {
        var watch = new PreloadWatch();
        watch.Watch([new PreloadAlertEntry("Metadata/X", "One")]);
        watch.Took(1, ["Metadata/X"]);
        Assert.Single(watch.Found);

        watch.Watch([]);
        Assert.Empty(watch.Found);
    }

    [Fact]
    public void AListWithRepeatsInItIsCollapsedOnTheWayIn()
    {
        var watch = new PreloadWatch();
        watch.Watch(
        [
            new PreloadAlertEntry("Metadata/X", "One"),
            new PreloadAlertEntry("metadata/x", "Two"),
            new PreloadAlertEntry("", "Empty"),
        ]);

        Assert.Single(watch.Watching);
        Assert.Equal("One", watch.Watching[0].Shown);
    }
}

/// <summary>Reading and writing the two files.</summary>
public class PreloadStoreTests
{
    private static string Scratch()
        => Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}", "alerts.json");

    [Fact]
    public void WhatWasAddedSurvivesARestart()
    {
        string file = Scratch();
        try
        {
            IReadOnlyList<PreloadAlertEntry> saving =
            [
                new("Metadata/A", "Alpha", 0xFF00FF00, Enabled: true, Log: true),
                new("Metadata/B", "Beta", 0xFF0000FF, Enabled: false),
            ];

            Assert.True(PreloadAlertStore.Save(saving, file));
            IReadOnlyList<PreloadAlertEntry> back = PreloadAlertStore.Load(file);

            Assert.Equal(saving, back);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void THEFILESORDERISTHEPriority()
    {
        // There is no priority FIELD, on purpose: the reference carries one and then spends a
        // container keeping it in step with the list's order, and the two can disagree. A list
        // that IS the order cannot drift from itself - but only if the file preserves it.
        string file = Scratch();
        try
        {
            PreloadAlertStore.Save(
                [new("Metadata/C"), new("Metadata/A"), new("Metadata/B")], file);

            Assert.Equal(
                ["Metadata/C", "Metadata/A", "Metadata/B"],
                PreloadAlertStore.Load(file).Select(entry => entry.Path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void AHandEditedEmptyPathIsDroppedOnTheWayIn()
    {
        string file = Scratch();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                [
                  { "path": "", "called": "matches everything" },
                  { "path": "Metadata/A", "called": "Alpha" },
                  { "path": "metadata/a", "called": "the same one again" }
                ]
                """);

            IReadOnlyList<PreloadAlertEntry> back = PreloadAlertStore.Load(file);

            Assert.Single(back);
            Assert.Equal("Alpha", back[0].Called);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void ONLYTheEntriesThatAskedForItAreLogged()
    {
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}", "found.log");
        try
        {
            var when = new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);
            int wrote = PreloadAlertStore.Log(
                77,
                [
                    new("Metadata/A", "Alpha", Log: true),
                    new("Metadata/B", "Beta"),
                ],
                when,
                file);

            Assert.Equal(1, wrote);

            string[] lines = File.ReadAllLines(file);
            Assert.Single(lines);
            Assert.Contains("Alpha", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("Beta", lines[0], StringComparison.Ordinal);

            // APPEND, never rewrite: the point of the file is what turned up over a league, so
            // a second area must not answer the question by destroying the first one's answer.
            PreloadAlertStore.Log(78, [new("Metadata/A", "Alpha", Log: true)], when, file);
            Assert.Equal(2, File.ReadAllLines(file).Length);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void NOTHINGWorthLoggingWritesNoFileAtAll()
    {
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}", "found.log");

        Assert.Equal(0, PreloadAlertStore.Log(1, [new("Metadata/A")], DateTimeOffset.Now, file));
        Assert.Equal(0, PreloadAlertStore.Log(1, [], DateTimeOffset.Now, file));
        Assert.Equal(0, PreloadAlertStore.Log(1, null, DateTimeOffset.Now, file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AMissingFileIsANEMPTYListRatherThanAShippedOne()
    {
        // Nothing ships, and that is the honest consequence of matching exactly: the exact
        // paths a PoE2 area loads are not something this project has captured, so a shipped
        // list would be a guess presented as a default.
        Assert.Empty(PreloadAlertStore.Load(Scratch()));
    }

    [Fact]
    public void TheSwitchesAreTheirOwnFile()
    {
        // Separate from the list, and it is the exact matching that earns the split: a list of
        // full paths is the one part worth handing to somebody else, and a file that also
        // carried "hide when in town" would make importing one overwrite the other's window.
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}", "settings.json");
        try
        {
            var saving = new PreloadSettings(Card: false, List: true, Window: false, HideInTown: false);

            Assert.True(PreloadStore.Save(saving, file));
            Assert.Equal(saving, PreloadStore.Load(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void AMissingSettingsFileTakesTheDefaults()
    {
        PreloadSettings loaded = PreloadStore.Load(
            Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}", "settings.json"));

        Assert.Equal(PreloadSettings.Default, loaded);
        Assert.True(loaded.Card);
        Assert.True(loaded.Window);
        Assert.True(loaded.HideInTown);
        Assert.False(loaded.HideWhenEmpty);
    }
}

/// <summary>How long the card on the way in stays, and how it arrives and leaves.</summary>
/// <remarks>
/// The drawing cannot be reached from here - the overlay is Windows-only and these tests are
/// not - so what is pinned is the timing, which is the part with edges. It is also the part
/// that has been wrong: the first version threw every card away on its own first frame.
/// </remarks>
public class PreloadCardTests
{
    [Fact]
    public void ITFadesInThenHoldsThenFadesOut()
    {
        Assert.Equal(0f, PreloadCard.Readability(0));
        Assert.Equal(1f, PreloadCard.Readability(PreloadCard.FadeInMs));
        Assert.Equal(1f, PreloadCard.Readability(PreloadCard.ShownMs - PreloadCard.FadeOutMs));
        Assert.Equal(0f, PreloadCard.Readability(PreloadCard.ShownMs));

        Assert.InRange(PreloadCard.Readability(PreloadCard.FadeInMs / 2), 0.4f, 0.6f);
        Assert.InRange(
            PreloadCard.Readability(PreloadCard.ShownMs - (PreloadCard.FadeOutMs / 2)), 0.4f, 0.6f);
    }

    [Fact]
    public void ACardIsSTILLTHEREOnTheFrameItWasAnnounced()
    {
        // BEING INVISIBLE AND BEING OVER ARE DIFFERENT STATES. Asking "is it readable" and
        // dropping the card when the answer is zero drops it at age zero, which is where the
        // fade in begins - and announcing happens in the same frame as drawing.
        Assert.True(PreloadCard.Showing(0));
        Assert.Equal(0f, PreloadCard.Readability(0));

        Assert.True(PreloadCard.Showing(PreloadCard.ShownMs - 1));
        Assert.False(PreloadCard.Showing(PreloadCard.ShownMs));
    }

    [Fact]
    public void ANDAClockThatWentBackwardsIsGoneRatherThanAnException()
    {
        Assert.False(PreloadCard.Showing(-1));
        Assert.Equal(0f, PreloadCard.Readability(-1));
    }
}
