using System.Text;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Opening the area's room files, which is the last place the layout could be.
/// </summary>
/// <remarks>
/// Three recordings established that the game does not keep the placement anywhere this tool
/// can see: no tile of 2336 references a room, and the terrain struct's first 0x400 bytes hold
/// the tile array, a per-corner array and the ground types. What memory has is which rooms the
/// area loaded. So the question is whether a room FILE says what it is built from - and the
/// number of .tdt mentions in it is the answer, which is why that is the thing reported.
/// </remarks>
public class RoomFileTests
{
    private static readonly string[] Loaded =
    [
        "Metadata/Terrain/Gallows/Act2/2_5/Rooms/BonePassage/BonePassage_Cnr_1.arm",
        "Metadata/Terrain/Desert/Badlands/BoneFill_01.tdt",
        "Metadata/Terrain/Gallows/Act2/2_5/Rooms/Fills/ritualsite_01.arm",
        "Art/Models/Terrain/Desert/Badlands/BoneFill_01.tmd",
    ];

    /// <summary>An area hash, which is how the game and the dumps beside this one name one.</summary>
    private const uint Area = 2117916152;

    private static byte[]? Nothing(string path) => null;

    [Fact]
    public void OnlyTheRoomsAreOpened()
    {
        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, Nothing);

        Assert.Contains("2 room files loaded for this area", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("BonePassage_Cnr_1.arm", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("BoneFill_01.tdt", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileTheBundlesDoNotHaveSaysSo()
    {
        // Distinguishable from an empty one, because the two want opposite next steps: a
        // missing file means the path or the archive is wrong, an empty one means the format
        // is not what was hoped.
        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, Nothing);

        Assert.Contains(lines, line => line.Contains("not in the bundles", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoomThatNamesItsTilesIsReportedAsSuch()
    {
        // THE ANSWER THE DIAGNOSTIC EXISTS FOR. A room file that mentions tile paths is a room
        // file that says what it is built from - and with the tile grid already read, that is
        // enough to place it without a single new offset.
        const string content = """
            version 2
            Metadata/Terrain/Desert/Badlands/BoneFill_01.tdt 0 0
            Metadata/Terrain/Desert/Badlands/BoneEdge_St_01.tdt 1 0
            """;

        IReadOnlyList<string> lines = RoomFiles.Describe(
            Loaded, _ => Encoding.UTF8.GetBytes(content));

        string room = Assert.Single(lines, line => line.Contains("BonePassage_Cnr_1.arm", StringComparison.Ordinal));
        Assert.Contains("text", room, StringComparison.Ordinal);
        Assert.Contains("2 mentions of .tdt", room, StringComparison.Ordinal);
        Assert.Contains("BoneFill_01.tdt", room, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompiledFileIsReportedAsBinaryWithoutAWallOfBytes()
    {
        // A preview of binary as characters is noise; the length and the verdict are what a
        // person can act on.
        var content = new byte[512];
        Random.Shared.NextBytes(content);

        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, _ => content, most: 1);

        string room = Assert.Single(lines, line => line.Contains(".arm", StringComparison.Ordinal));
        Assert.Contains("512 bytes, binary", room, StringComparison.Ordinal);
    }

    [Fact]
    public void AUtf16FileIsNotMistakenForBinary()
    {
        // THE MISTAKE THIS FIXES, and it very nearly closed the question on nothing: decoding
        // UTF-16 as UTF-8 puts a NUL between every letter, so a text file reads as binary AND
        // every string in it hides from a search for ASCII. All 32 rooms of an area came back
        // "binary, 0 mentions of .tdt", which is what the check produces whatever the file says.
        byte[] content = Encoding.Unicode.GetBytes(
            "Metadata/Terrain/Desert/Badlands/BoneFill_01.tdt\nMetadata/Terrain/X/BoneEdge_St_01.tdt\n");

        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, _ => content, most: 1);

        string room = Assert.Single(lines, line => line.Contains(".arm", StringComparison.Ordinal));
        Assert.Contains("text (utf-16)", room, StringComparison.Ordinal);
        Assert.Contains("2 mentions of .tdt", room, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatAFileNamesIsReportedEvenWhenItNamesNoTiles()
    {
        // The payload once the tile question comes back no: a compiled format still carries the
        // names of what it references, and which names those are decides where to look next.
        byte[] content =
        [
            .. new byte[32],
            .. Encoding.UTF8.GetBytes("GroundTypeReference"),
            .. new byte[8],
            .. Encoding.Unicode.GetBytes("Rooms/BonePassage/entry"),
            .. new byte[16],
        ];

        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, _ => content, most: 1);

        string room = Assert.Single(lines, line => line.Contains(".arm", StringComparison.Ordinal));
        Assert.Contains("0 mentions of .tdt", room, StringComparison.Ordinal);
        Assert.Contains("GroundTypeReference", room, StringComparison.Ordinal);
        Assert.Contains("Rooms/BonePassage/entry", room, StringComparison.Ordinal);
    }

    [Fact]
    public void ABinaryFileStillReportsTheTilePathsHiddenInIt()
    {
        // A compiled format can still carry its references as plain strings, and that would be
        // just as good an answer - so the count is taken from the bytes either way.
        byte[] content = [.. new byte[64], .. Encoding.UTF8.GetBytes("Desert/Badlands/BoneFill_01.tdt"), .. new byte[64]];

        IReadOnlyList<string> lines = RoomFiles.Describe(Loaded, _ => content, most: 1);

        string room = Assert.Single(lines, line => line.Contains(".arm", StringComparison.Ordinal));
        Assert.Contains("binary", room, StringComparison.Ordinal);
        Assert.Contains("1 mentions of .tdt", room, StringComparison.Ordinal);
        Assert.Contains("BoneFill_01.tdt", room, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRoomsAtAllIsAStatementRatherThanAnEmptyList()
    {
        // An area with no rooms and a list that is not this area's look identical from an empty
        // report, and the second is the one that wastes an evening.
        IReadOnlyList<string> lines = RoomFiles.Describe(["Metadata/Terrain/X/a.tdt"], Nothing);

        Assert.Contains("no room files", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstFewAreOpenedAndTheRestAreCounted()
    {
        string[] many = [.. Enumerable.Range(0, 20).Select(i => $"Metadata/Terrain/X/Rooms/room_{i:00}.arm")];

        IReadOnlyList<string> lines = RoomFiles.Describe(many, Nothing, most: 3);

        Assert.Equal(5, lines.Count);   // the count, three rooms, and the tail
        Assert.Contains("...and 17 more", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheDumpCarriesEveryRoomWhole()
    {
        // ALL of them, and not the handful the readout opens: the variation between rooms is
        // the evidence. A field constant across thirty-two files is a header; one that tracks
        // the grid is a dimension - and neither is visible in a sample of six.
        string[] many = [.. Enumerable.Range(0, 20).Select(i => $"Metadata/Terrain/X/Rooms/room_{i:00}.arm")];
        string file = Temporary();

        string? written = RoomFiles.Dump(
            Area, many, room => Encoding.UTF8.GetBytes($"grid of {Name(room)}"), file);

        Assert.Equal(file, written);
        string text = File.ReadAllText(file);
        foreach (string room in many)
        {
            Assert.Contains($"### {room}", text, StringComparison.Ordinal);
            Assert.Contains($"grid of {Name(room)}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDumpDecodesUtf16RatherThanWritingNulsOut()
    {
        // The mistake this whole file exists around: a room is UTF-16, and writing its bytes
        // out as if they were UTF-8 produces a page of NULs that reads as a broken dump rather
        // than as a wrong decoder.
        byte[] content = [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("GroundType 0 = Sand\n")];
        string file = Temporary();

        RoomFiles.Dump(Area, Loaded, _ => content, file);

        string text = File.ReadAllText(file);
        Assert.Contains("GroundType 0 = Sand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoomTheBundlesLackIsMarkedRatherThanDroppedFromTheDump()
    {
        // A room that is missing and a room that is absent from the list want opposite next
        // steps, and a dump that simply skips the first cannot tell them apart.
        string file = Temporary();

        RoomFiles.Dump(Area, Loaded, Nothing, file);

        string text = File.ReadAllText(file);
        Assert.Contains("BonePassage_Cnr_1.arm", text, StringComparison.Ordinal);
        Assert.Contains("not in the bundles", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OneUnreadableRoomDoesNotCostTheRest()
    {
        string file = Temporary();

        RoomFiles.Dump(
            Area,
            Loaded,
            room => room.Contains("ritualsite", StringComparison.Ordinal)
                ? throw new InvalidDataException("bad chunk")
                : Encoding.UTF8.GetBytes("grid"),
            file);

        string text = File.ReadAllText(file);
        Assert.Contains("could not be read: bad chunk", text, StringComparison.Ordinal);
        Assert.Contains("BonePassage_Cnr_1.arm", text, StringComparison.Ordinal);
        Assert.Contains("grid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRoomsWritesNoFileAtAll()
    {
        // Rather than an empty document that looks like a failed read of a real area.
        string file = Temporary();

        Assert.Null(RoomFiles.Dump(Area, ["Metadata/Terrain/X/a.tdt"], Nothing, file));
        Assert.False(File.Exists(file));
    }

    private static string Temporary()
        => Path.Combine(Path.GetTempPath(), $"poef-rooms-{Guid.NewGuid():N}.txt");

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
