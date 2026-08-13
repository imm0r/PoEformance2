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
