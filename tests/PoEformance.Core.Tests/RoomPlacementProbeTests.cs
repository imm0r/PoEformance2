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

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

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

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

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

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

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

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

        Assert.Contains("nothing refers to a room file", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissSaysHowFarItLookedAndHowManyPointersItFollowed()
    {
        // "Found nothing" and "looked nowhere" must never read alike - the ground layer already
        // cost a round on exactly that, refusing in four places with a bare null.
        FakeMemoryReader memory = Instance(_ => { });

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

        Assert.Contains($"swept {RoomPlacementProbe.SweepBytes} bytes", lines[0], StringComparison.Ordinal);
        Assert.Contains("followed 0 pointers", lines[0], StringComparison.Ordinal);
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
            Assert.Single(new RoomPlacementProbe(memory).Probe(Area, new Dictionary<ulong, string>())),
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
            Assert.Single(new RoomPlacementProbe(memory).Probe(Area, Rooms)),
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

        IReadOnlyList<string> lines = new RoomPlacementProbe(memory).Probe(Area, Rooms);

        Assert.Contains(
            $"followed {RoomPlacementProbe.MostFollowed} pointers", lines[0], StringComparison.Ordinal);
    }
}
