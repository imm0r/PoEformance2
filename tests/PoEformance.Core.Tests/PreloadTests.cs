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

/// <summary>What a loaded-file path means, and what the summary makes of it.</summary>
public class PreloadWatchTests
{
    [Fact]
    public void AMechanicIsRecognisedFromItsFolder()
    {
        Assert.Equal("Breach", PreloadMeanings.Meaning("Metadata/Terrain/Leagues/Breach/Thing", DefaultPreloadRules.Rules)?.Name);
        Assert.Equal("Ritual", PreloadMeanings.Meaning("Art/Textures/Leagues/Ritual/Altar", DefaultPreloadRules.Rules)?.Name);
    }

    [Fact]
    public void AndSceneryIsNot()
    {
        // Almost everything in the list is this, which is the reason the meanings are a short
        // curated list rather than an attempt to parse a path.
        Assert.Null(PreloadMeanings.Meaning("Metadata/Terrain/Dungeon/Rocks/Rock_01", DefaultPreloadRules.Rules));
        Assert.Null(PreloadMeanings.Meaning("Art/Models/Ground/Grass", DefaultPreloadRules.Rules));
    }

    [Fact]
    public void ONEBreachRatherThanThirteen()
    {
        // An area loads a dozen files for one encounter. Listing each is not a summary.
        var watch = new PreloadWatch();
        watch.Took(1, [
            "Metadata/Leagues/Breach/A",
            "Metadata/Leagues/Breach/B",
            "Art/Leagues/Breach/C",
            "Metadata/Terrain/Rocks/Rock",
        ]);

        Assert.Single(watch.Findings);
        Assert.Equal("Breach", watch.Summary());
    }

    [Fact]
    public void TheRawListIsKeptToo()
    {
        // The findings are only as good as the list of meanings, and the way that list grows
        // is somebody looking at what an area actually loaded when the tool had nothing to
        // say about it.
        var watch = new PreloadWatch();
        watch.Took(1, ["Metadata/Terrain/Rocks/Rock", "Metadata/Leagues/Breach/A"]);

        Assert.Equal(2, watch.All.Count);
        Assert.Equal("Metadata/Leagues/Breach/A", watch.All[0]);   // sorted, so it can be read
    }

    [Fact]
    public void DangerSortsAboveReward()
    {
        // What is going to hit you first, ahead of what you might pick up.
        var watch = new PreloadWatch();
        watch.Took(1, ["Metadata/Leagues/Breach/A", "Metadata/Monsters/ExileLeague/Exile"]);

        Assert.Equal(PreloadWeight.Dangerous, watch.Findings[0].Weight);
    }

    [Fact]
    public void AnAreaWithNothingInItSaysNothing()
    {
        var watch = new PreloadWatch();
        watch.Took(1, ["Metadata/Terrain/Rocks/Rock"]);

        Assert.Empty(watch.Findings);
        Assert.Equal(string.Empty, watch.Summary());
        Assert.True(watch.Looked, "an area with nothing in it has still been looked at");
    }

    [Fact]
    public void EveryMeaningIsUsableAndDistinct()
    {
        string[] names = [.. DefaultPreloadRules.Rules.Select(rule => rule.Name)];
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        foreach (PreloadRule rule in DefaultPreloadRules.Rules)
        {
            Assert.False(rule.SaysNothing, $"{rule.Name} looks for nothing");
        }
    }

    // ---- What the first live run taught. It reported EIGHT league mechanics in one map,
    // ---- which no map has, and every wrong one had the same shape: a single file that
    // ---- mentions a mechanic rather than a set of files that IS one.

    [Fact]
    public void HOWMANYFilesMatchedIsKept()
    {
        // The signal that separates a mechanic from a mention. Deduplicating by name was
        // right; throwing away the number was not.
        var watch = new PreloadWatch();
        watch.Took(1, [
            "Art/Leagues/Breach/A",
            "Art/Leagues/Breach/B",
            "Art/Leagues/Breach/C",
            "Metadata/Items/Currency/Ritual/RitualPinnacleKey",
        ]);

        Assert.Equal(3, watch.Findings.Single(f => f.Name == "Breach").Files);
        Assert.Equal(1, watch.Findings.Single(f => f.Name == "Ritual").Files);
    }

    [Fact]
    public void ASINGLEFileIsFlaggedRatherThanTrusted()
    {
        // ...and rather than dropped. One file is usually a passing reference, but "usually"
        // is not "always", and a summary that silently deletes the marginal case cannot be
        // checked by anybody. Marking it keeps both properties.
        var watch = new PreloadWatch();
        watch.Took(1, [
            "Art/Leagues/Breach/A",
            "Art/Leagues/Breach/B",
            "Metadata/Items/Ultimatum/TrialmasterKey1",
        ]);

        Assert.Equal("Breach, Ultimatum?", watch.Summary());
    }

    [Fact]
    public void THESTRONGESTFindingLeadsWithinAWeight()
    {
        var watch = new PreloadWatch();
        watch.Took(1, [
            "Metadata/Items/Currency/Ritual/RitualPinnacleKey",
            "Art/Leagues/Breach/A",
            "Art/Leagues/Breach/B",
        ]);

        Assert.Equal("Breach", watch.Findings[0].Name);
    }

    [Fact]
    public void ANATLASMapPinIsNotEvidenceOfAnything()
    {
        // The one path class that is wrong by construction rather than merely weak. A pin is
        // the icon drawn on the world map SCREEN for some other map; its league folder says
        // nothing about the ground under your feet, and it is loaded in every area forever.
        Assert.Null(PreloadMeanings.Meaning(
            "Metadata/Terrain/WorldMaps/Maps/Doodads/Pins/Leagues/Delirium/Delirium01.ao",
            DefaultPreloadRules.Rules));

        // The same mechanic still reports when something real carries it.
        Assert.Equal("Delirium", PreloadMeanings.Meaning("Art/Models/Leagues/Delirium/Fog", DefaultPreloadRules.Rules)?.Name);
    }

    [Fact]
    public void ANITEMIsNotQUOTEDAsTheReasonWhenSomethingRealMatched()
    {
        // An item definition says the thing CAN exist - a pinnacle key is defined whether or
        // not the mechanic is in this map - so quoting one as the evidence points the person
        // reading it at entirely the wrong thing.
        var watch = new PreloadWatch();
        watch.Took(1, [
            "Metadata/Items/Currency/Ritual/RitualPinnacleKey",
            "Metadata/Terrain/Leagues/Ritual/RitualAltar",
        ]);

        Assert.Equal("Metadata/Terrain/Leagues/Ritual/RitualAltar", watch.Findings[0].Path);
    }

    [Fact]
    public void ANDISQuotedWhenItIsAllThereIs()
    {
        // No pretending. If the only thing that matched is an item, that is what gets shown,
        // and the count beside it says how much weight to give it.
        var watch = new PreloadWatch();
        watch.Took(1, ["Metadata/Items/Currency/Ritual/RitualPinnacleKey"]);

        PreloadFinding only = watch.Findings.Single();
        Assert.Equal("Metadata/Items/Currency/Ritual/RitualPinnacleKey", only.Path);
        Assert.Equal(1, only.Files);
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
/// The rules that say what a loaded path means, now that they belong to whoever is playing.
/// </summary>
/// <remarks>
/// Written from the request that produced them: being able to search the raw list for the path
/// you care about and watch for it, instead of waiting for a release to add it. Everything here
/// is about the ways that can go wrong - a rule that matches everything, the same rule added
/// twice, and a banner that fires on a single stray file.
/// </remarks>
public class PreloadRuleTests
{
    [Fact]
    public void ARuleWithNoFragmentLooksForNOTHING()
    {
        // The one thing this feature must never do. An empty fragment is contained by every
        // string, so a rule carrying one would report the whole file table as one finding -
        // and an add box somebody hit return in is exactly where such a rule comes from.
        Assert.True(new PreloadRule("Everything", string.Empty).SaysNothing);
        Assert.True(new PreloadRule("Everything", "   ").SaysNothing);
        Assert.False(new PreloadRule("Everything", "   ").Matches("Metadata/Whatever"));
    }

    [Fact]
    public void ARuleMatchesWhateverTheCaseItWasTypedIn()
    {
        // Nobody types the game's capitalisation, and the paths in the table are not
        // consistent about it either.
        Assert.True(new PreloadRule("Ritual", "/ritual/").Matches("Art/Leagues/Ritual/Altar"));
        Assert.False(new PreloadRule("Ritual", "/ritual/").Matches("Art/Leagues/Breach/Hand"));
    }

    [Fact]
    public void ADisabledRuleIsKeptAndNotActedOn()
    {
        // The difference between switching a rule off and deleting it. Both have to exist:
        // one is "not this map", the other is "never again".
        Assert.False(new PreloadRule("Ritual", "/Ritual/", Enabled: false).Matches("Leagues/Ritual/A"));
    }

    [Fact]
    public void THEFragmentAddedFromAPathIsTheFolderRatherThanTheFile()
    {
        // The file is renamed and re-versioned between patches; the folder that names the
        // thing does not move. Slashes both sides, so it cannot match a similarly-named file
        // somewhere else entirely.
        Assert.Equal("/Ritual/", PreloadMeanings.WatchableFragment("Art/Leagues/Ritual/Altar_02.ao"));
        Assert.Equal("/Breach/", PreloadMeanings.WatchableFragment("Metadata/Terrain/Leagues/Breach/Hand.ao"));
    }

    [Fact]
    public void ANDAPathWithNoFolderFallsBackToItself()
    {
        // Nothing better to use. A rule somebody can see and delete beats refusing to add one
        // and leaving them wondering which of the two buttons they got wrong.
        Assert.Equal("Rock_01", PreloadMeanings.WatchableFragment("Rock_01"));
        Assert.Equal("Art/", PreloadMeanings.WatchableFragment("Art/Grass"));
    }
}

/// <summary>Adding to the list while the tool is running, and what that changes.</summary>
public class PreloadEditingTests
{
    private static readonly string[] Loaded =
    [
        "Art/Leagues/Ritual/Altar_01.ao",
        "Art/Leagues/Ritual/Altar_02.ao",
        "Metadata/Terrain/Dungeon/Rocks/Rock_01.ao",
    ];

    [Fact]
    public void ANEWRuleReReadsTheAreaAlreadyInHand()
    {
        // The point of adding from the window that shows the paths: the finding appears at
        // once. Walking the file table again costs a whole read budget, and the list cannot
        // have changed while you stand there - so the answer comes from the paths in hand.
        var watch = new PreloadWatch();
        watch.UseRules([]);
        watch.Took(1, Loaded);
        Assert.Empty(watch.Findings);

        Assert.True(watch.AddRule(new PreloadRule("Ritual", "/Ritual/", PreloadWeight.Valuable)));

        PreloadFinding found = Assert.Single(watch.Findings);
        Assert.Equal("Ritual", found.Name);
        Assert.Equal(2, found.Files);
    }

    [Fact]
    public void THESameFragmentIsNotAddedTwice()
    {
        // Adding is two clicks from a list of thousands, so the same thing gets added twice.
        // Matched on the FRAGMENT, not the name: two rules with one fragment produce a single
        // finding whatever they are called, and the second is a line the delete button appears
        // not to remove.
        var watch = new PreloadWatch();
        watch.UseRules([]);

        Assert.True(watch.AddRule(new PreloadRule("Ritual", "/Ritual/")));
        Assert.False(watch.AddRule(new PreloadRule("Rituals, again", "/ritual/")));
        Assert.Single(watch.Rules);
    }

    [Fact]
    public void ARuleThatLooksForNothingIsRefused()
    {
        var watch = new PreloadWatch();
        watch.UseRules([]);

        Assert.False(watch.AddRule(new PreloadRule("Everything", string.Empty)));
        Assert.Empty(watch.Rules);
    }

    [Fact]
    public void REMOVINGARuleTakesItsFindingWithIt()
    {
        var watch = new PreloadWatch();
        watch.UseRules([new PreloadRule("Ritual", "/Ritual/")]);
        watch.Took(1, Loaded);
        Assert.Single(watch.Findings);

        watch.UseRules([]);
        Assert.Empty(watch.Findings);
    }
}

/// <summary>What gets said out loud on the way in, as opposed to merely listed.</summary>
public class PreloadAnnouncementTests
{
    private static PreloadWatch Watching(params string[] paths)
    {
        var watch = new PreloadWatch();
        watch.UseRules(
        [
            new PreloadRule("Ritual", "/Ritual/", PreloadWeight.Valuable),
            new PreloadRule("Strongbox", "/StrongBoxes/", PreloadWeight.Notable, Alert: false),
        ]);

        watch.Took(1, paths);
        return watch;
    }

    [Fact]
    public void ASingleStrayFileDoesNotInterruptAnybody()
    {
        // One matching file is a mention somewhere - a pinnacle key is defined whether or not
        // the mechanic is in this map. The window still shows it, marked; the banner is the
        // one place the marginal case has to stay quiet, because it cannot be gone back to.
        PreloadWatch watch = Watching("Art/Leagues/Ritual/Altar_01.ao");

        Assert.Single(watch.Findings);
        Assert.Empty(watch.Announceable(PreloadSettings.DefaultMinFiles));
        Assert.Equal(string.Empty, watch.Announcement(PreloadSettings.DefaultMinFiles));
    }

    [Fact]
    public void ANDAWholeArtSetDoes()
    {
        PreloadWatch watch = Watching(
            "Art/Leagues/Ritual/Altar_01.ao",
            "Art/Leagues/Ritual/Altar_02.ao",
            "Art/Leagues/Ritual/Rune.ao");

        Assert.Equal("Ritual", watch.Announcement(PreloadSettings.DefaultMinFiles));
    }

    [Fact]
    public void ARuleThatAsksNotToBeAnnouncedIsStillListed()
    {
        // Strongboxes are in most areas. Announcing them teaches the eye to skip the line
        // that also says Ultimatum, which is the failure this whole feature is one step from.
        PreloadWatch watch = Watching(
            "Metadata/Chests/StrongBoxes/Arcanist.ao",
            "Metadata/Chests/StrongBoxes/Armoury.ao",
            "Metadata/Chests/StrongBoxes/Ornate.ao");

        Assert.Single(watch.Findings);
        Assert.Equal(string.Empty, watch.Announcement(PreloadSettings.DefaultMinFiles));
    }

    [Fact]
    public void AGateOfZeroStillNeedsOneFile()
    {
        // A slider dragged to nothing, or a hand-edited file. Zero would make every rule fire
        // on an area that contains none of it, since a finding with no files does not exist.
        PreloadWatch watch = Watching("Art/Leagues/Ritual/Altar_01.ao");
        Assert.Equal("Ritual", watch.Announcement(0));
    }
}

/// <summary>Keeping the list between sessions.</summary>
public class PreloadStoreTests
{
    [Fact]
    public void WhatWasAddedSurvivesARestart()
    {
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new PreloadSettings(
                Banner: false,
                List: true,
                MinFiles: 5,
                Rules: [new PreloadRule("Ritual", "/Ritual/", PreloadWeight.Dangerous, Alert: false)]);

            Assert.True(PreloadStore.Save(settings, file));

            PreloadSettings back = PreloadStore.Load(file);
            Assert.False(back.Banner);
            Assert.Equal(5, back.MinFiles);

            PreloadRule rule = Assert.Single(back.Watching);
            Assert.Equal("Ritual", rule.Name);
            Assert.Equal("/Ritual/", rule.PathContains);
            Assert.Equal(PreloadWeight.Dangerous, rule.Weight);
            Assert.False(rule.Alert);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AHandEditedRuleThatMatchesEverythingIsDroppedOnTheWayIn()
    {
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                file,
                """{"banner":true,"list":true,"minFiles":2,"rules":[{"name":"Oops","pathContains":""}]}""");

            // Empty rather than the shipped defaults: the file DID choose, and it chose one
            // unusable rule. Handing back the defaults would quietly re-add things somebody
            // had deleted.
            Assert.Empty(PreloadStore.Load(file).Watching);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ANDAMissingFileTakesTheShippedList()
    {
        PreloadSettings fresh = PreloadStore.Load(
            Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}.json"));

        Assert.NotEmpty(fresh.Watching);
        Assert.True(fresh.Banner);
    }
}
