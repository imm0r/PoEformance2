using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Looking for the flags a CHARACTER has set, rather than the list of flags that exist.
/// </summary>
/// <remarks>
/// The sweep's two shapes are what these pin down: a row POINTER, which is how the game's own
/// data refers to a flag, and the row's HASH32, which is what a compact runtime set would
/// plausibly store given the table carries an index keyed on exactly that. Both matter because
/// nobody knows yet which one the game uses - and a sweep that quietly looked for only one of
/// them would report a confident nothing.
/// </remarks>
public class QuestFlagHuntTests
{
    private const ulong RowsAt = 0x6000_0000_0000;
    private const ulong RegionAt = 0x3000_0000_0000;
    private const ulong IdsAt = 0x1100_0000_0000;
    private const int RowSize = 0x0C;
    private const int Rows = 4;

    private static readonly uint[] Hashes = [0x5085C604, 0x234F4A13, 0xA8ACC161, 0xE758D90C];
    private static readonly string[] Ids =
        ["a1q1-KilledHillock", "Act4HoodedMentorSummoned", "PrisonerWeaponPartAcquired", "a1q2-KilledBrutus"];

    /// <summary>A rows array of four QuestFlags rows, each an Id pointer and a HASH32.</summary>
    private static FakeMemoryReader FakeTable()
    {
        var reader = new FakeMemoryReader();
        reader.Place(RowsAt, new byte[Rows * RowSize]);
        for (int i = 0; i < Rows; i++)
        {
            reader.Place(RowsAt + (ulong)(i * RowSize), IdsAt + (ulong)(i * 0x40));
            reader.Place(RowsAt + (ulong)((i * RowSize) + 8), Hashes[i]);
            reader.Place(IdsAt + (ulong)(i * 0x40), new byte[0x40]);
            reader.PlaceUtf16(IdsAt + (ulong)(i * 0x40), Ids[i]);
        }

        return reader;
    }

    private static Dictionary<uint, int> Needles()
    {
        var needles = new Dictionary<uint, int>();
        for (int i = 0; i < Rows; i++)
        {
            needles[Hashes[i]] = i;
        }

        return needles;
    }

    private static QuestFlagHunt Hunt(FakeMemoryReader reader)
        => new(reader, RealSessionTests.Schema());

    [Fact]
    public void AFlagStoredAsItsHashIsFoundAndNamed()
    {
        // The name is read only for the rows that matched, which is the whole point of
        // carrying hashes rather than strings through the sweep.
        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[4096]);
        reader.Place(RegionAt + 0x100, Hashes[2]);

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 4096, Needles(), RowsAt, Rows, RowSize);

        FlagSighting found = Assert.Single(swept.Sightings);
        Assert.Equal(2, found.Row);
        Assert.Equal("PrisonerWeaponPartAcquired", found.Id);
        Assert.False(found.ByPointer);
        Assert.Equal(RegionAt + 0x100, found.At);
    }

    [Fact]
    public void AFlagStoredAsARowPointerIsFoundToo()
    {
        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[4096]);
        reader.Place(RegionAt + 0x200, RowsAt + (1 * RowSize));

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 4096, Needles(), RowsAt, Rows, RowSize);

        FlagSighting found = Assert.Single(swept.Sightings);
        Assert.Equal(1, found.Row);
        Assert.True(found.ByPointer);
    }

    [Fact]
    public void APointerIntoTheMiddleOfARowIsNotAFlag()
    {
        // Off the 12-byte grid, so it refers to a column and not to a row. Reporting it as
        // "row 1" would be a wrong answer where no answer was needed.
        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[4096]);
        reader.Place(RegionAt + 0x200, RowsAt + (1 * RowSize) + 4);

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 4096, Needles(), RowsAt, Rows, RowSize);

        Assert.Empty(swept.Sightings);
    }

    [Fact]
    public void APointerPastTheLastRowIsNotAFlag()
    {
        // On the grid but outside the table - the allocation after it, most likely.
        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[4096]);
        reader.Place(RegionAt + 0x200, RowsAt + (Rows * RowSize));

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 4096, Needles(), RowsAt, Rows, RowSize);

        Assert.Empty(swept.Sightings);
    }

    [Fact]
    public void AnUnmappedTailCostsOnlyTheTail()
    {
        // Regions are guesses about where a blob ends, so running off the end is the normal
        // case rather than a failure - and a read refuses ALL of its bytes when one page is
        // missing, so this is the difference between half a region and none of it.
        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[1024]);
        reader.Place(RegionAt + 0x100, Hashes[3]);

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 8192, Needles(), RowsAt, Rows, RowSize);

        Assert.Equal(1024, swept.Read);
        Assert.Equal(8192, swept.Wanted);
        Assert.Equal(3, Assert.Single(swept.Sightings).Row);
    }

    [Fact]
    public void WhatARegionPointsAtIsNotedForReading()
    {
        // The reason the first sweep found nothing: a set of flags is a CONTAINER, and its
        // contents live wherever it allocated them - so the bytes of ServerData hold the two
        // pointers bracketing the answer and none of the answer itself. A begin/end pair gets
        // read to its own length; anything else gets a kilobyte, which is the whole of a
        // bitset over 5717 flags.
        const ulong ListAt = 0x5000_0000_0000;
        const ulong LoneAt = 0x5500_0000_0000;

        FakeMemoryReader reader = FakeTable();
        reader.Place(RegionAt, new byte[4096]);
        reader.Place(RegionAt + 0x40, ListAt);
        reader.Place(RegionAt + 0x48, ListAt + 0x1000);
        reader.Place(RegionAt + 0x100, LoneAt);

        SweptRegion swept = Hunt(reader).Sweep("ServerData", RegionAt, 4096, Needles(), RowsAt, Rows, RowSize);

        Assert.Contains(swept.Targets, t => t.At == RegionAt + 0x40 && t.Begin == ListAt && t.Bytes == 0x1000);
        Assert.Contains(
            swept.Targets,
            t => t.At == RegionAt + 0x100 && t.Begin == LoneAt && t.Bytes == QuestFlagHunt.LonePointerBytes);
    }

    /// <summary>
    /// The session <c>--questflags</c> was first run in: the whole table, and the four regions
    /// read in full.
    /// </summary>
    private static string HuntFixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "fixtures", "session-2026-08-questflags-hunt.rec");
        }
    }

    /// <summary>ServerData as that session resolved it, read to its full 128 KB.</summary>
    private const ulong HuntServerData = 0x41EB5EA4000UL;

    /// <summary>
    /// The session where following pointers paid off: the same ServerData, plus everything one
    /// hop out of it.
    /// </summary>
    private static string MarkerFixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "fixtures", "session-2026-08-areamarkers.rec");
        }
    }

    /// <summary>Same address as the hunt fixture - the same process, captured twice.</summary>
    private const ulong MarkerServerData = 0x41EB5EA4000UL;

    [Fact]
    public void TheWholeTableIsReadWhenTheWholeTableIsThere()
    {
        // The other half of the partial-read test below: given a session that actually read
        // the rows, all 5717 come back. 68 KB in seventeen chunks, and no row missing means
        // no chunk was silently dropped.
        var replay = ReplayMemoryReader.Load(File.OpenRead(HuntFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();

        Dictionary<uint, int> hashes = new QuestFlagHunt(replay, schema).HashesIn(0x41ED8080990UL);

        Assert.Equal(5717, hashes.Count);
        Assert.Equal(0, hashes[0x5085C604]);
    }

    [Fact]
    public void ServerDataHeldNoFlagsAndPlentyOfPointers()
    {
        // THE NEGATIVE RESULT, kept as a test because it is what the next step was built on.
        // 128 KB of a character's own server blob, every one of the 5717 hashes tested against
        // it, and nothing - while the same bytes hold thousands of pointers leading somewhere
        // that was never read. A set of flags is a container; its contents are not in the
        // struct that owns it. If a future change makes this assertion fail, the hunt has
        // either found something or broken, and both are worth stopping for.
        var replay = ReplayMemoryReader.Load(File.OpenRead(HuntFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new QuestFlagHunt(replay, schema);

        ulong table = 0x41ED8080990UL;
        ulong rows = replay.ReadPointer(
            replay.ReadPointer(table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr"))
            + (ulong)schema.Structs["DatRowStore"].OffsetOf("Rows"));

        SweptRegion swept = hunt.Sweep(
            "ServerData", HuntServerData, 128 * 1024, hunt.HashesIn(table), rows, 5717, 0x0C);

        Assert.Equal(128 * 1024, swept.Read);
        Assert.Empty(swept.Sightings);
        Assert.Equal(5370, swept.Targets.Count);
        Assert.Equal(2326, swept.Targets.Select(t => t.Begin).Distinct().Count());
        Assert.Equal(825, swept.Targets.Count(t => t.Bytes != QuestFlagHunt.LonePointerBytes));
    }

    [Fact]
    public void TheAreaMarkersAreWhatFollowingAPointerFound()
    {
        // What the hunt turned up while looking for something else: ServerData+0x2248 is the
        // area's own marker list, and one of its entries hangs off a quest flag. The entry
        // NAMES ITSELF twice over - a MinimapIcons row pointer and the same row as an index -
        // and the two agreeing is what makes a single sighting worth a struct.
        var replay = ReplayMemoryReader.Load(File.OpenRead(MarkerFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef marker = schema.Structs["AreaMarker"];
        long stride = marker.Constants["EntrySize"];

        ulong list = replay.ReadPointer(
            MarkerServerData + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("AreaMarkers"));
        ulong end = replay.ReadPointer(
            MarkerServerData + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("AreaMarkers") + 8);
        long count = (long)(end - list) / stride;
        Assert.Equal(14, count);

        // Two entries whose indices differ by one, whose rows differ by the MinimapIcons row
        // size, and whose names say what they are.
        ulong stash = list + (ulong)(9 * stride);
        ulong guild = list + (ulong)(13 * stride);
        Assert.Equal(169u, replay.Read<uint>(stash + (ulong)marker.OffsetOf("IconRow")));
        Assert.Equal(170u, replay.Read<uint>(guild + (ulong)marker.OffsetOf("IconRow")));
        Assert.Equal(
            0x26UL,
            replay.ReadPointer(guild + (ulong)marker.OffsetOf("IconRowPtr"))
                - replay.ReadPointer(stash + (ulong)marker.OffsetOf("IconRowPtr")));
        Assert.Equal(
            "StashPlayer",
            replay.ReadUnicodeString(replay.ReadPointer(replay.ReadPointer(stash + (ulong)marker.OffsetOf("IconRowPtr")))));

        // The one entry that is conditional, and the flag it waits for.
        ulong ange = list + (ulong)(4 * stride);
        ulong flagRow = replay.ReadPointer(ange + (ulong)marker.OffsetOf("QuestFlagRowPtr"));
        ulong flagTable = replay.ReadPointer(ange + (ulong)marker.OffsetOf("QuestFlagTablePtr"));
        DatTableShape? shape = DatTableShape.From(schema);
        DatTableFacts? facts = PointerPeek.DescribeTable(replay, flagTable, shape!.Value);

        Assert.NotNull(facts);
        Assert.Equal("Data/Balance/QuestFlags.dat", facts!.Path);
        Assert.Equal(851, facts.IndexOf(flagRow));
        Assert.Equal(
            "CanSeeAngeInHideout",
            replay.ReadUnicodeString(replay.ReadPointer(flagRow)));

        // ONE condition among the fourteen - and counting them is the test, because the
        // obvious way to count is wrong. Four entries have a non-zero value in that slot;
        // three of those are floats and small integers left over from whatever the allocation
        // held before, since a marker without a condition never writes there. Only landing on
        // the row grid separates them.
        int nonZero = 0;
        int plausible = 0;
        int onGrid = 0;
        for (int i = 0; i < count; i++)
        {
            ulong at = list + (ulong)(i * stride) + (ulong)marker.OffsetOf("QuestFlagRowPtr");
            if (replay.Read<ulong>(at) != 0)
            {
                nonZero++;
            }

            // ReadPointer refuses anything outside the range a heap pointer can occupy, which
            // is already enough to throw out all three leftovers here - 1.0f and 0xFEF are not
            // addresses. The grid check below is what makes it robust rather than lucky: a
            // leftover CAN be a plausible-looking number, and then only the table can say.
            ulong flag = replay.ReadPointer(at);
            if (flag != 0)
            {
                plausible++;
            }

            if (facts!.IndexOf(flag) >= 0)
            {
                onGrid++;
            }
        }

        Assert.Equal(4, nonZero);
        Assert.Equal(1, plausible);
        Assert.Equal(1, onGrid);

        // Every NPC marker names its NPC, and they all sit on the 0xBF NPCs row grid - which
        // is what says the field is an NPCs reference rather than a pointer that happens to be
        // there. One of them is the row an earlier session identified by name.
        long npcStride = schema.Structs["NpcsRow"].Constants["RowSize"];
        int npcs = 0;
        for (int i = 0; i < count; i++)
        {
            ulong row = replay.ReadPointer(list + (ulong)(i * stride) + (ulong)marker.OffsetOf("NpcRowPtr"));
            if (row == 0)
            {
                continue;
            }

            npcs++;
            long delta = (long)row - (long)QuestFlagsTableTests.NpcsRowAddress;
            Assert.Equal(0L, delta % npcStride);
        }

        Assert.Equal(6, npcs);
    }

    [Fact]
    public void RealTable_YieldsTheHashesTheRecordingActuallyHolds()
    {
        // Against the session the table was found in. The recording holds the first kilobyte
        // of the rows array and nothing after it, so this is also the check that a chunk which
        // could not be read contributes NOTHING: the zeros left behind would otherwise be
        // indexed as row 85's hash and every zero in memory would match it.
        var replay = ReplayMemoryReader.Load(File.OpenRead(QuestFlagsTableTests.FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        ulong table = replay.ReadPointer(
            QuestFlagsTableTests.NpcsRowAddress + (ulong)schema.Structs["NpcsRow"].OffsetOf("QuestFlagsTablePtr"));

        Dictionary<uint, int> hashes = new QuestFlagHunt(replay, schema).HashesIn(table);

        Assert.Equal(85, hashes.Count);
        Assert.Equal(0, hashes[0x5085C604]);
        Assert.DoesNotContain(0u, hashes.Keys);
    }
}
