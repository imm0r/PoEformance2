using System.Text;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The rooms an area may lay: the loaded list's room files and every room of the room sets it loaded, once each.
/// </summary>
public class AreaRoomSetTests
{
    /// <summary>
    /// A room the list and a set both name is one room, in the list's spelling; a set under Metadata/Terrain is read and one elsewhere is not; a set that does not read adds nothing and sinks nothing.
    /// </summary>
    [Fact]
    public void THESETIsTheListsRoomsAndItsRoomSetsOnceEach()
    {
        var asked = new List<string>();
        byte[]? Read(string path)
        {
            asked.Add(path);
            return path switch
            {
                "Metadata/Terrain/Maps/Test/generate.rs" => Encoding.UTF8.GetBytes(
                    "version 2\n100 \"Metadata/Terrain/Maps/Test/Rooms/Fills/floor_01.arm\" I\n\"Metadata\\Terrain\\Maps\\Test\\Rooms\\Unique\\boss.arm\"\n"),
                "Metadata/Terrain/Maps/Test/Encounters/league.rs" => Encoding.UTF8.GetBytes(
                    "version 2\n\"Metadata/Terrain/Maps/Test/Rooms/Encounters/expedition.arm\"\n"),
                _ => null,
            };
        }

        List<string> files = AreaRoomSet.Files(
            [
                "Metadata/Terrain/Maps/Test/Rooms/Fills/FLOOR_01.arm",
                "Metadata/Terrain/Maps/Test/Tiles/floor.tdt",
                "Metadata/Terrain/Maps/Test/generate.rs",
                "Metadata/Terrain/Maps/Test/Encounters/league.rs",
                "Metadata/Terrain/Maps/Test/missing.rs",
                "Metadata/Effects/something.rs",
                "Metadata/Terrain/Maps/Test/Rooms/entrance.arm",
            ],
            Read);

        // THE LIST'S SPELLING for a room both name: the list is the game's own record of the file.
        Assert.Equal(
            [
                "Metadata/Terrain/Maps/Test/Rooms/Encounters/expedition.arm",
                "Metadata/Terrain/Maps/Test/Rooms/entrance.arm",
                "Metadata/Terrain/Maps/Test/Rooms/Fills/FLOOR_01.arm",
                "Metadata/Terrain/Maps/Test/Rooms/Unique/boss.arm",
            ],
            files);
        Assert.Equal(
            ["Metadata/Terrain/Maps/Test/generate.rs", "Metadata/Terrain/Maps/Test/Encounters/league.rs", "Metadata/Terrain/Maps/Test/missing.rs"],
            asked);
    }

    /// <summary>
    /// A re-entered instance's list carries the master and not the set: the master's RoomSet is read beside it, after the name as written draws a blank, and the set's rooms are the area's.
    /// </summary>
    [Fact]
    public void THEMASTERNamesTheSetWhereTheListDoesNot()
    {
        var asked = new List<string>();
        byte[]? Read(string path)
        {
            asked.Add(path);
            return path switch
            {
                "Metadata/Terrain/Maps/Test/master.tsi" => Encoding.UTF8.GetBytes("version 2\nRoomSet \"generate.rs\"\nTileSet tiles.tst\n"),
                "Metadata/Terrain/Maps/Test/generate.rs" => Encoding.UTF8.GetBytes("version 2\n\"Metadata/Terrain/Maps/Test/Rooms/Unique/boss.arm\"\n\"Metadata/Terrain/Maps/Test/Rooms/Fills/floor_01.arm\"\n"),
                _ => null,
            };
        }

        List<string> files = AreaRoomSet.Files(
            ["Metadata/Terrain/Maps/Test/tiles.tst", "Metadata/Terrain/Maps/Test/master.tsi", "Metadata/Terrain/Maps/Test/Rooms/entrance.arm", "Metadata/Effects/other.tsi"],
            Read);

        Assert.Equal(
            ["Metadata/Terrain/Maps/Test/Rooms/entrance.arm", "Metadata/Terrain/Maps/Test/Rooms/Fills/floor_01.arm", "Metadata/Terrain/Maps/Test/Rooms/Unique/boss.arm"],
            files);
        Assert.Equal(["Metadata/Terrain/Maps/Test/master.tsi", "generate.rs", "Metadata/Terrain/Maps/Test/generate.rs"], asked);
    }

    /// <summary>A set the list and a master both reach is read once, and a master naming no set, or one that is not there, adds nothing.</summary>
    [Fact]
    public void ASETBothRoadsReachIsReadOnce()
    {
        var asked = new List<string>();
        byte[]? Read(string path)
        {
            asked.Add(path);
            return path switch
            {
                "Metadata/Terrain/Maps/Test/master.tsi" => Encoding.UTF8.GetBytes("RoomSet \"generate.rs\"\n"),
                "Metadata/Terrain/Maps/Test/generate.rs" => Encoding.UTF8.GetBytes("version 2\n\"Metadata/Terrain/Maps/Test/Rooms/Unique/boss.arm\"\n"),
                "Metadata/Terrain/Maps/Bare/master.tsi" => Encoding.UTF8.GetBytes("TileSet tiles.tst\n"),
                "Metadata/Terrain/Maps/Gone/master.tsi" => Encoding.UTF8.GetBytes("RoomSet \"gone.rs\"\n"),
                _ => null,
            };
        }

        List<string> files = AreaRoomSet.Files(
            ["Metadata/Terrain/Maps/Test/generate.rs", "Metadata/Terrain/Maps/Test/master.tsi", "Metadata/Terrain/Maps/Bare/master.tsi", "Metadata/Terrain/Maps/Gone/master.tsi"],
            Read);

        Assert.Equal(["Metadata/Terrain/Maps/Test/Rooms/Unique/boss.arm"], files);
        Assert.Equal(1, asked.Count(path => path == "Metadata/Terrain/Maps/Test/generate.rs"));
        Assert.Equal(["gone.rs", "Metadata/Terrain/Maps/Gone/gone.rs"], asked.Where(path => path.Contains("gone", StringComparison.Ordinal)));
    }

    /// <summary>A list with no room and no set is no rooms, and nothing is read for it.</summary>
    [Fact]
    public void ALISTWithoutRoomsIsNoRooms()
    {
        Assert.Empty(AreaRoomSet.Files(["Metadata/Terrain/Maps/Test/Tiles/floor.tdt"], _ => throw new InvalidOperationException("nothing should be read")));
        Assert.Empty(AreaRoomSet.Files([], _ => null));
    }
}
