using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// An area's master read the way poe_data_tools reads one: a key and its value a line, quoted or not.
/// </summary>
public class MasterFileTests
{
    /// <summary>Quoted and unquoted values, a tab, a comment, a blank line, a key alone and a repeated key - each as the reference keeps it.</summary>
    [Fact]
    public void AMASTERReadsEveryKeyAndItsValue()
    {
        MasterFile master = MasterFile.Parse(
            "version 2\r\n"
            + "RoomSet \"generate.rs\"\r\n"
            + "TileSet\ttiles.tst\r\n"
            + "// the fills\r\n"
            + "\r\n"
            + "  FillTiles   random_fill_tiles.gft  \r\n"
            + "Reskin\r\n"
            + "EnvironmentPreload \"Metadata/EnvironmentSettings/Gallows/3_6_2.env\"\r\n"
            + "RoomOverlap 0\r\n"
            + "RoomOverlap 1\r\n");

        Assert.True(master.Ready, master.Why);
        Assert.Equal("generate.rs", master.RoomSet);
        Assert.Equal(
            [("version", "2"), ("RoomSet", "generate.rs"), ("TileSet", "tiles.tst"), ("FillTiles", "random_fill_tiles.gft"), ("Reskin", string.Empty), ("EnvironmentPreload", "Metadata/EnvironmentSettings/Gallows/3_6_2.env"), ("RoomOverlap", "1")],
            master.Values.Select(pair => (pair.Key, pair.Value)));
    }

    /// <summary>Nothing to read is None, and a master naming no set says so with an empty name.</summary>
    [Fact]
    public void AMASTERWithoutASetNamesNone()
    {
        Assert.Same(MasterFile.None, MasterFile.Read(null));
        Assert.Same(MasterFile.None, MasterFile.Read([]));
        Assert.Same(MasterFile.None, MasterFile.Parse(" \n// nothing\n"));
        Assert.False(MasterFile.None.Ready);
        Assert.Equal(string.Empty, MasterFile.None.RoomSet);

        MasterFile utf16 = MasterFile.Read([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("version 2\nTileSet tiles.tst\n")]);
        Assert.True(utf16.Ready);
        Assert.Equal(string.Empty, utf16.RoomSet);
        Assert.Equal("tiles.tst", utf16.Values["TileSet"]);
    }

    /// <summary>A name is tried as written and then beside the master, with GGG's backslashes and doubled slashes straightened; a full path that is its own neighbour is tried once; no name is no candidate.</summary>
    [Fact]
    public void ANAMEIsLookedForAsWrittenThenBesideTheMaster()
    {
        const string Master = "Metadata/Terrain/Maps/VaalFactory/master.tsi";
        Assert.Equal(["generate.rs", "Metadata/Terrain/Maps/VaalFactory/generate.rs"], MasterFile.Candidates(Master, "generate.rs"));
        Assert.Equal(["Rooms/extra.rs", "Metadata/Terrain/Maps/VaalFactory/Rooms/extra.rs"], MasterFile.Candidates(Master, "Rooms\\\\extra.rs "));
        Assert.Equal(
            ["Metadata/Terrain/Maps/Other/generate.rs", "Metadata/Terrain/Maps/VaalFactory/Metadata/Terrain/Maps/Other/generate.rs"],
            MasterFile.Candidates(Master, "Metadata/Terrain/Maps/Other/generate.rs"));
        Assert.Equal(["generate.rs"], MasterFile.Candidates("master.tsi", "generate.rs"));
        Assert.Empty(MasterFile.Candidates(Master, "  "));
    }
}
