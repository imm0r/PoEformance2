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
        Assert.Equal("Breach", PreloadMeanings.Meaning("Metadata/Terrain/Leagues/Breach/Thing")?.Name);
        Assert.Equal("Ritual", PreloadMeanings.Meaning("Art/Textures/Leagues/Ritual/Altar")?.Name);
    }

    [Fact]
    public void AndSceneryIsNot()
    {
        // Almost everything in the list is this, which is the reason the meanings are a short
        // curated list rather than an attempt to parse a path.
        Assert.Null(PreloadMeanings.Meaning("Metadata/Terrain/Dungeon/Rocks/Rock_01"));
        Assert.Null(PreloadMeanings.Meaning("Art/Models/Ground/Grass"));
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
        string[] names = [.. PreloadMeanings.Known.Select(k => k.Name)];
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        foreach (PreloadFinding known in PreloadMeanings.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(known.Name));
            Assert.False(string.IsNullOrWhiteSpace(known.Path));
        }
    }
}
