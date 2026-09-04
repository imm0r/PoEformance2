using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Searching AreaInstance for anything that refers to one of the area's room files.
/// </summary>
/// <remarks>
/// WHAT THESE GUARD is the distinction that makes this probe worth running at all. The previous
/// room hunt asked whether a pointer LOOKED like it led to a room, and this project's record
/// says what shape questions are worth. This one searches for the ADDRESS of a known .arm
/// record, so a hit cannot happen by accident - and the tests hold that: a plausible pointer
/// that is not a room record must produce nothing, and a miss must still say how far it looked.
/// </remarks>
public class RoomPlacementProbeTests
{
    private const ulong Area = 0x0000_0500_0000_0000;
    private const ulong Vector = 0x0000_0500_0010_0000;
    private const ulong Decoy = 0x0000_0500_0020_0000;

    private const ulong FirstRoom = 0x0000_0500_0030_0000;
    private const ulong SecondRoom = 0x0000_0500_0031_0000;

    private static readonly Dictionary<ulong, string> Rooms = new()
    {
        [FirstRoom] = "Metadata/Terrain/Gallows/Act2/2_5/Rooms/Unique/entrance.arm",
        [SecondRoom] = "Metadata/Terrain/Gallows/Act2/2_5/Rooms/Fills/bones_fill_02.arm",
    };

    /// <summary>An AreaInstance whose sweep window is filled by the caller.</summary>
    private static FakeMemoryReader Instance(Action<byte[]> fill)
    {
        var bytes = new byte[RoomPlacementProbe.SweepBytes];
        fill(bytes);
        return new FakeMemoryReader().Place(Area, bytes);
    }

    private static void Put(byte[] into, int at, ulong value)
        => BitConverter.GetBytes(value).CopyTo(into, at);

    [Fact]
    public void ARecordAddressSittingInAreaInstanceIsFoundAndNamed()
    {
        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x450, FirstRoom));

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("+0x0450", StringComparison.Ordinal)
                                    && l.Contains("entrance.arm", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecordBehindOneHopIsFoundAndTheHopIsNamed()
    {
        // The shape a placement list actually has: a field pointing at a vector whose elements
        // carry the rooms. Following one hop is what makes that reachable at all.
        var elements = new byte[0x200];
        Put(elements, 0x18, SecondRoom);

        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x930, Vector))
            .Place(Vector, elements);

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("AreaInstance+0x0930 -> +0x0018", StringComparison.Ordinal)
                                    && l.Contains("bones_fill_02.arm", StringComparison.Ordinal));
    }

    [Fact]
    public void APathStoredInlineIsFoundEvenWithNoRecordAddress()
    {
        // The other form the game could use. Searching only for record addresses would report
        // "nothing found" about a structure sitting in the window carrying the path in plain
        // sight - which is a miss reported as a fact.
        var inline = new byte[0x200];
        byte[] text = System.Text.Encoding.Unicode.GetBytes("rooms/entrance.arm");
        text.CopyTo(inline, 0x40);

        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x120, Decoy))
            .Place(Decoy, inline);

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("the text \".arm\"", StringComparison.Ordinal));
    }

    [Fact]
    public void APlausiblePointerThatIsNotARoomProducesNothing()
    {
        // THE CHECK THAT MAKES A HIT MEAN SOMETHING. A window of plausible pointers is what
        // AreaInstance mostly IS, and a probe that reported those would be the shape-matching
        // this replaced - the kind that accepted a decoy and cost this project real time.
        FakeMemoryReader memory = Instance(bytes =>
        {
            for (int at = 0; at + 8 <= bytes.Length; at += 8)
            {
                Put(bytes, at, Decoy + (ulong)at);
            }
        });

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("nothing refers to a room file", StringComparison.Ordinal));
    }

    [Fact]
    public void AVectorsELEMENTSAreReachedWhichIsTwoHopsOut()
    {
        // WHAT THE FIRST VERSION COULD NOT REACH, and the reason its miss was thin even once
        // the control had earned it. A placement list is a vector hanging off a field, so its
        // ELEMENTS are two hops from AreaInstance: field -> owner -> vector data. One hop saw
        // the owner and stopped, which is exactly where the begin pointer lives and exactly
        // one short of where the rooms would be.
        //
        // Followed by SHAPE - three slots reading begin, end, end-of-storage - but what is
        // searched for at the other end is still a known record address, so a false triple
        // costs one read and finds nothing.
        var elements = new byte[0x80];
        Put(elements, 0x10, FirstRoom);

        var owner = new byte[0x200];
        Put(owner, 0x40, Vector);                       // begin
        Put(owner, 0x48, Vector + (ulong)elements.Length);   // end
        Put(owner, 0x50, Vector + (ulong)elements.Length);   // end of storage

        const ulong Owner = 0x0000_0500_0060_0000;
        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x200, Owner))
            .Place(Owner, owner)
            .Place(Vector, elements);

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("vector ->", StringComparison.Ordinal)
                                    && l.Contains("entrance.arm", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreeSlotsThatAreNotAVectorCostOneReadAndFindNothing()
    {
        // The guard on the shape test. Following a triple is a decision about WHERE TO READ,
        // never about what was found - so a window full of plausible-looking triples must still
        // produce no hits, or the probe would be back to matching shapes.
        var owner = new byte[0x200];
        for (int at = 0; at + 8 <= owner.Length; at += 8)
        {
            Put(owner, at, Decoy + (ulong)at);
        }

        const ulong Owner = 0x0000_0500_0060_0000;
        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x200, Owner))
            .Place(Owner, owner);

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("nothing refers to a room file", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissIsWORTHLESSWithoutTheControlSayingTheSearchCouldHaveWorked()
    {
        // THE FLAW THE FIRST VERSION SHIPPED WITH, and it was found by running it: a sweep that
        // finds nothing has two meanings - "no room is referred to here" and "rooms are referred
        // to by something other than a record address" - and the probe reported the first with
        // no way of ruling out the second. It said "nothing refers to a room file" as
        // confidently as if it had checked. Every run now carries the control, hit or miss.
        FakeMemoryReader memory = Instance(_ => { });

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains("control:", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorSpanningMoreThanHalfTheAddressSpaceIsRejectedAndNotAllocated()
    {
        // THE CRASH THIS PROBE ACTUALLY DIED OF IN THE FIELD, reported as
        // "OverflowException: Arithmetic operation resulted in an overflow" and, before the
        // buttons learned to report, as nothing happening at all.
        //
        // The span guard compared (long)(end - begin) against its cap. That subtraction is
        // UNSIGNED, so a difference of 2^63 or more turns NEGATIVE on the cast - and a negative
        // number is not greater than the cap, so the garbage triple sailed through the one check
        // meant to stop it. The truncating (int) cast that followed could then be negative too,
        // and `new byte[negative]` is an OverflowException.
        //
        // Nothing here is exotic: AreaInstance is swept as raw memory, so any three consecutive
        // qwords may look like begin/end/capacity by accident, and whether one does is a fact
        // about the area's heap. That is why this crashed in one area and not another.
        const ulong Begin = 0x0000_0500_0080_0000;
        const ulong Span = 0x8000_0000_FFFF_FFFF;

        FakeMemoryReader memory = Instance(bytes =>
        {
            Put(bytes, 0x600, Begin);            // begin: a plausible pointer
            Put(bytes, 0x608, Begin + Span);     // end: past it, as the guard demands
            Put(bytes, 0x610, Begin + Span);     // capacity: no smaller than end
        });

        // The whole assertion is that this RETURNS. Every line of it was unreachable before.
        IReadOnlyList<string> lines =
            new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(lines, l => l.Contains("nothing refers to a room file", StringComparison.Ordinal));

        // And it is REJECTED rather than read: a span of eight exabytes is not a vector, so it
        // must not be counted as one either.
        Assert.Contains("0 vectors", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlProvesTheSearchWhenTilesPointAtRecords()
    {
        // A tile carries TgtFilePtr to its own .tdt, which the same table holds a record for -
        // so if that pointer IS a record address, the game refers to files by the value this
        // search looks for, and a miss elsewhere is a real absence rather than a wrong premise.
        OffsetSchema schema = RealSessionTests.Schema();
        ulong terrain = Area + (ulong)schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata");
        int tileVector = schema.Structs["TerrainMetadata"].OffsetOf("TileDetailsPtr");
        int tgtFile = schema.Structs["TileStruct"].OffsetOf("TgtFilePtr");

        const ulong Tiles = 0x0000_0500_0040_0000;
        const ulong TileFile = 0x0000_0500_0050_0000;

        var tile = new byte[0x38 * 2];
        BitConverter.GetBytes(TileFile).CopyTo(tile, tgtFile);
        BitConverter.GetBytes(TileFile).CopyTo(tile, 0x38 + tgtFile);

        FakeMemoryReader memory = Instance(_ => { })
            .Place(Tiles, tile)
            .Place<ulong>(terrain + (ulong)tileVector, Tiles)
            .Place<ulong>(terrain + (ulong)tileVector + 8, Tiles + (ulong)tile.Length);

        var files = new Dictionary<ulong, string>(Rooms) { [TileFile] = "Tiles/Something.tdt" };

        IReadOnlyList<string> lines =
            new RoomPlacementProbe(memory, schema).Probe(Area, Rooms, files);

        Assert.Contains("ARE record addresses", lines[^1], StringComparison.Ordinal);
        Assert.Contains("a real absence", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A tile whose file pointer is NOT a record address, carrying a path of its own.
    /// </summary>
    /// <remarks>
    /// The fixture both failing-control tests need, and the whole point of it is the last
    /// argument: whether that path is among the loaded files decides WHICH failure it is, and
    /// nothing else about the two cases differs.
    /// </remarks>
    private static IReadOnlyList<string> ControlWithTilePath(string path, params string[] loaded)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        ulong terrain = Area + (ulong)schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata");
        int tileVector = schema.Structs["TerrainMetadata"].OffsetOf("TileDetailsPtr");
        int tgtFile = schema.Structs["TileStruct"].OffsetOf("TgtFilePtr");
        int tgtPath = schema.Structs["TgtFile"].OffsetOf("TgtPath");

        const ulong Tiles = 0x0000_0500_0040_0000;
        const ulong PathData = 0x0000_0500_0060_0000;

        var tile = new byte[0x38];
        BitConverter.GetBytes(Decoy).CopyTo(tile, tgtFile);

        FakeMemoryReader memory = Instance(_ => { })
            .Place(Tiles, tile)
            .Place<ulong>(terrain + (ulong)tileVector, Tiles)
            .Place<ulong>(terrain + (ulong)tileVector + 8, Tiles + (ulong)tile.Length);

        memory.PlaceStdWString(Decoy + (ulong)tgtPath, path, PathData);

        // Addresses nothing points at, because what is being tested is the PATH lookup: if the
        // record addresses were reachable the control would have passed on the first question.
        var files = new Dictionary<ulong, string>(Rooms);
        ulong at = 0x0000_0500_0070_0000;
        foreach (string name in loaded)
        {
            files[at] = name;
            at += 0x1000;
        }

        return new RoomPlacementProbe(memory, schema).Probe(Area, Rooms, files);
    }

    [Fact]
    public void TheControlSaysTheSearchCannotWorkWhenTheFileIsLoadedButNotPointedAt()
    {
        // The answer that would explain a miss completely: the file IS in the table, so the
        // table is not the problem - the game simply refers to it by something that is not its
        // record address, and searching for record addresses cannot work here.
        IReadOnlyList<string> lines =
            ControlWithTilePath("Tiles/Steppe/Steppe_Fill_01.tdt", "Tiles/Steppe/Steppe_Fill_01.tdt");

        Assert.Contains("cannot work", lines[^1], StringComparison.Ordinal);
        Assert.Contains("their files ARE loaded", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("FILE TABLE is short", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheWalkNeverCollectedIsCalledAShortTableAndNotAFindingAboutTheGame()
    {
        // THE DISTINCTION THIS PROBE GOT WRONG IN THE FIELD. The control reported "the game
        // refers to a file by some OTHER object" in an area listing 846 files where another had
        // listed 2573 - a claim about the GAME resting on a table that may simply be missing the
        // record. A file the walk never saw cannot demonstrate anything about how the game
        // points at files, so this must read as a bug here rather than as a finding.
        IReadOnlyList<string> lines =
            ControlWithTilePath("Tiles/Steppe/Steppe_Fill_01.tdt", "Tiles/Elsewhere/Other.tdt");

        Assert.Contains("FILE TABLE is short", lines[^1], StringComparison.Ordinal);
        Assert.Contains("proves nothing", lines[^1], StringComparison.Ordinal);

        // And it names the file it could not find, so the next question has somewhere to start.
        Assert.Contains("Steppe_Fill_01.tdt", lines[^1], StringComparison.Ordinal);

        // The conclusion it must NOT reach.
        Assert.DoesNotContain("some OTHER object", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissSaysHowFarItLookedAndHowManyPointersItFollowed()
    {
        // "Found nothing" and "looked nowhere" must never read alike - the ground layer already
        // cost a round on exactly that, refusing in four places with a bare null.
        FakeMemoryReader memory = Instance(_ => { });

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains($"swept {RoomPlacementProbe.SweepBytes} bytes", lines[0], StringComparison.Ordinal);
        Assert.Contains("followed 0 pointers", lines[0], StringComparison.Ordinal);
        Assert.Contains("0 vectors", lines[0], StringComparison.Ordinal);
        Assert.Contains("2 .arm records", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoRoomsLoadedItSaysThatRatherThanNothingFound()
    {
        // With no addresses to search for, the sweep cannot say anything either way - and
        // reporting that as "nothing refers to a room file" would be a verdict on no evidence.
        FakeMemoryReader memory = Instance(bytes => Put(bytes, 0x450, FirstRoom));

        Assert.Contains(
            "nothing to search for",
            Assert.Single(new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, new Dictionary<ulong, string>())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableAreaInstanceIsNotReportedAsAMiss()
    {
        // The buffer's unread bytes are zero, and sweeping those would report "nothing refers
        // to a room" about memory nobody read.
        var memory = new FakeMemoryReader();

        Assert.Contains(
            "unreadable",
            Assert.Single(new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FollowingIsBoundedSoAGarbageWindowCannotWalkTheHeap()
    {
        // A window of nonsense is mostly plausible-looking pointers. Following every one turns
        // one probe into a walk of the heap, and a recording into something nobody can open.
        FakeMemoryReader memory = Instance(bytes =>
        {
            for (int at = 0; at + 8 <= bytes.Length; at += 8)
            {
                Put(bytes, at, Decoy + (ulong)at);
            }
        });

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory, RealSessionTests.Schema()).Probe(Area, Rooms);

        Assert.Contains(
            $"followed {RoomPlacementProbe.MostFollowed} pointers", lines[0], StringComparison.Ordinal);
    }
}
