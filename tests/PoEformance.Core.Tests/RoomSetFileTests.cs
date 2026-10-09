using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// A room set read the way poe_data_tools reads one: a version line, then a room a line - an optional weight, the quoted file, the laying tokens.
/// </summary>
public class RoomSetFileTests
{
    /// <summary>Weighted and unweighted lines, laying tokens, backslashes, comments, blank lines and Windows line ends - each room as its line has it.</summary>
    [Fact]
    public void AROOMSetReadsEveryRoomLine()
    {
        RoomSetFile set = RoomSetFile.Parse(
            "version 2\r\n"
            + "// the fills\r\n"
            + "\r\n"
            + "100 \"Rooms/Fills/floor_01.arm\" I FI R180\r\n"
            + "\"Rooms\\Unique\\boss.arm\"\r\n"
            + "  5 \"Rooms/entrance.arm\"  \r\n");

        Assert.True(set.Ready, set.Why);
        Assert.Equal(2, set.Version);
        Assert.Equal(
            [(100, "Rooms/Fills/floor_01.arm", "I FI R180"), (null, "Rooms/Unique/boss.arm", string.Empty), (5, "Rooms/entrance.arm", string.Empty)],
            set.Rooms.Select(room => (room.Weight, room.Arm, string.Join(' ', room.Rotations))));
    }

    /// <summary>A set without its version line first, a room line without a quoted file, one naming no .arm, and one whose weight is not a number each say what is wrong; nothing to read is None.</summary>
    [Fact]
    public void ASETThatIsNotOneSaysWhy()
    {
        Assert.Same(RoomSetFile.None, RoomSetFile.Read(null));
        Assert.Same(RoomSetFile.None, RoomSetFile.Read([]));
        Assert.Same(RoomSetFile.None, RoomSetFile.Parse("  \n"));
        Assert.False(RoomSetFile.None.Ready);
        Assert.Empty(RoomSetFile.None.Rooms);

        Assert.Equal("no version line first: \"100 \"a.arm\"\"", RoomSetFile.Parse("100 \"a.arm\"").Why);
        Assert.Equal("a room line with no quoted file: \"a.arm\"", RoomSetFile.Parse("version 2\na.arm").Why);
        Assert.Equal("a room line naming no .arm: \"\"a.tdt\"\"", RoomSetFile.Parse("version 2\n\"a.tdt\"").Why);
        Assert.Equal("a room line whose weight is not a number: \"x \"a.arm\"\"", RoomSetFile.Parse("version 2\nx \"a.arm\"").Why);
        Assert.Equal("no version line", RoomSetFile.Parse("// nothing but a comment").Why);
        Assert.Empty(RoomSetFile.Parse("version 2\na.arm").Rooms);
    }

    /// <summary>The bytes are decoded the way the install's other text files are - UTF-16 with its mark here, and UTF-8 just the same.</summary>
    [Fact]
    public void ASETIsReadFromTheInstallsEncoding()
    {
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("version 2\n\"Rooms/a.arm\"\n")];
        RoomSetFile set = RoomSetFile.Read(utf16);
        Assert.True(set.Ready, set.Why);
        Assert.Equal("Rooms/a.arm", Assert.Single(set.Rooms).Arm);

        RoomSetFile utf8 = RoomSetFile.Read(Encoding.UTF8.GetBytes("version 3\n7 \"Rooms/b.arm\" FI\n"));
        Assert.Equal(3, utf8.Version);
        Assert.Equal((7, "Rooms/b.arm"), (Assert.Single(utf8.Rooms).Weight, utf8.Rooms[0].Arm));
    }
}
