using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The hunt for the level ABOVE a tile.
/// </summary>
/// <remarks>
/// The game builds an area in two levels - rooms (.arm, under Rooms/) assembled from tiles
/// (.tdt, under Tiles/) - and the tile struct's one mapped string is the tile's own. So the
/// names drawn on the map are a materials list where the reference tool shows a floor plan.
/// Nothing here knows where the room hangs, and 0x10-0x2F of the tile struct is unaccounted
/// for, so this walks it and says what is there rather than deciding in advance.
///
/// What the tests pin down is the SHAPE of the answer: a pointer at some slot, leading to a
/// structure, whose own field is the path - which is exactly how the tile's own name is
/// stored, and the reason the probe follows one hop rather than stopping at the first level.
/// </remarks>
public class RoomProbeTests
{
    private const ulong Tile = 0x2000_0000;
    private const ulong Terrain = 0x3000_0000;
    private const ulong Detail = 0x4000_0000;
    private const ulong Text = 0x4000_1000;

    /// <summary>A tile entry whose unmapped span holds a pointer to a struct holding a path.</summary>
    private static FakeMemoryReader WithRoomAt(int slot, string path)
    {
        var memory = new FakeMemoryReader();

        // The tile's own 0x38 bytes, empty but for the pointer under test.
        memory.Place(Tile, new byte[0x38]);
        memory.Place(Tile + (ulong)slot, Detail);

        // The structure it leads to, with the path at +0x08 - the shape TgtFile uses.
        memory.Place(Detail, new byte[0x40]);

        // A PAGE around the characters, not just the characters. The fake reader serves a read
        // only when every requested byte was placed, while the game's pages carry whatever
        // follows a string - so a bare string here would make a long path read as a truncated
        // one, which is a property of the fixture and not of the code being tested.
        memory.Place(Text, new byte[1024]);
        memory.PlaceStdWString(Detail + 0x08, path, Text);

        memory.Place(Terrain, new byte[0xD0]);
        return memory;
    }

    [Fact]
    public void APathUnderARoomsDirectoryIsCalledOut()
    {
        // The marker is the whole point of the probe: whatever else the walk prints, the line
        // that answers the question has to be the one that stands out.
        FakeMemoryReader memory = WithRoomAt(
            0x10, "Metadata/Terrain/Gallows/Act2/2_8/Rooms/Overlays/overlay_superman.arm");

        IReadOnlyList<string> lines = new RoomProbe(memory).Probe(Terrain, Tile, "some.tdt");

        Assert.Contains(lines, line => line.Contains("ROOM?", StringComparison.Ordinal)
            && line.Contains("overlay_superman", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSlotItWasFoundInIsReportedWithBothOffsets()
    {
        // "+0x10 then +0x08" is the field this would become, so both halves have to survive
        // into the report - a probe that says only "a room is somewhere in here" has not
        // answered anything.
        FakeMemoryReader memory = WithRoomAt(0x18, "Metadata/Terrain/X/Rooms/exit_01.arm");

        IReadOnlyList<string> lines = new RoomProbe(memory).Probe(Terrain, Tile, "some.tdt");

        Assert.Contains(lines, line => line.Contains("+0x18+0x08", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOrdinaryTilePathIsReportedWithoutTheMarker()
    {
        // The tile's own name is found by the same walk and must NOT read as the answer -
        // that is the mistake the marker exists to prevent.
        FakeMemoryReader memory = WithRoomAt(
            0x08, "Metadata/Terrain/Maps/Port/Tiles/OceanEdge/BuildingWall_OceanEdge_CcMM_02.tdt");

        IReadOnlyList<string> lines = new RoomProbe(memory).Probe(Terrain, Tile, "some.tdt");

        Assert.Contains(lines, line => line.Contains("BuildingWall_OceanEdge", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("ROOM?", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTileBeingProbedIsNamedInTheReport()
    {
        IReadOnlyList<string> lines = new RoomProbe(new FakeMemoryReader().Place(Tile, new byte[0x38]))
            .Probe(0, Tile, "some.tdt");

        Assert.Contains(lines, line => line.Contains("some.tdt", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingToLookAtSaysSoRatherThanReportingAnEmptyWalk()
    {
        // A silent empty report and a probe that never ran look identical in a readout, and
        // the second is the one that wastes an evening.
        IReadOnlyList<string> lines = new RoomProbe(new FakeMemoryReader()).Probe(0, 0, string.Empty);

        Assert.Equal("nothing to probe - no terrain or no tile", Assert.Single(lines));
    }

    [Theory]
    [InlineData("Metadata/Terrain/X/Rooms/Overlays/a.arm", true)]
    [InlineData("wide \"Metadata/Terrain/X/Rooms/y.arm\"", true)]
    [InlineData("Metadata/Terrain/Maps/Port/Tiles/OceanEdge/a.tdt", false)]
    [InlineData("", false)]
    public void WhatCountsAsLookingLikeARoom(string summary, bool expected)
    {
        // Deliberately loose, and taken from the reference tool's own tooltip rather than from
        // a theory about what the game stores: a path under a Rooms directory, or a file ending
        // .arm. Either one appearing is the answer.
        Assert.Equal(expected, RoomProbe.LooksLikeRoom(summary));
    }
}
