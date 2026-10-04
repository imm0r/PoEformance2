using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// A room file read as far as its doodads, and the doodads drawn where it puts them.
/// </summary>
/// <remarks>
/// AGAINST poe_data_tools' <c>arm</c> parser, written out as files of the two shapes it reads: below
/// version 32 a group is a count and its lines, from 32 it is lines up to a <c>-1</c>. What these
/// cannot say is what the game's own rooms hold - the first real one opened in the tile book does,
/// and the line under its picture says how far its doodads reach.
/// </remarks>
public class RoomLayoutTests
{
    private const string Version31 = """
        version 31
        2
        "Metadata/Terrain/Woods/edge.et"
        "Metadata/Terrain/Woods/ground.gt"
        5 3
        1 1
        "roomtag"
        0 1
        k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0

        1 0
        1 0
        1 0
        1 0
        0
        1
        3 4 0.5 "spawn"
        0
        0
        0
        0
        k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 n
        2
        10 20 1.5 0 0 0 1 0 1 1 7.5 2 "Metadata/Doodads/Rock.ao" "stub"
        30 5 0 0 0 0 1 1 0 0 1 "Metadata/Doodads/Tree.ao" "stub2"
        0
        0
        """;

    private const string Version36 = """
        version 36
        0
        5 3
        0
        "roomtag"
        0
        f 0
        -1
        -1
        -1
        -1
        -1
        -1
        ""
        f 0
        4 6 1 0.1 0.2 3.14 0 0 0 1 1 0 0 1 "Metadata/Doodads/Rhoa.ao" "stub" 1 lane=4
        -1
        """;

    [Fact]
    public void AROOMBelowVersion32CountsItsGroupsAndReadsItsDoodads()
    {
        RoomLayout room = RoomLayout.Parse(Version31);

        Assert.True(room.Ready, room.Why);
        Assert.Equal(31, room.Version);
        Assert.Equal((2, 1), (room.Width, room.Height));
        Assert.Equal(2, room.Doodads.Count);

        RoomDoodad rock = room.Doodads[0];
        Assert.Equal((10, 20), (rock.X, rock.Y));
        Assert.Equal(1.5f, rock.Turn);
        Assert.Equal(2f, rock.Scale);
        Assert.Equal("Metadata/Doodads/Rock.ao", rock.Ao);
        Assert.Equal("stub", rock.Stub);

        Assert.Equal("Metadata/Doodads/Tree.ao", room.Doodads[1].Ao);
        Assert.Equal(1f, room.Doodads[1].Scale);
    }

    [Fact]
    public void ANDFrom32ItsGroupsEndAtMinusOneAndADoodadCarriesPairsAndKeys()
    {
        RoomLayout room = RoomLayout.Parse(Version36);

        Assert.True(room.Ready, room.Why);
        Assert.Equal((1, 1), (room.Width, room.Height));

        RoomDoodad rhoa = Assert.Single(room.Doodads);
        Assert.Equal((4, 6), (rhoa.X, rhoa.Y));
        Assert.Equal(3.14f, rhoa.Turn);
        Assert.Equal(1f, rhoa.Scale);
        Assert.Equal("Metadata/Doodads/Rhoa.ao", rhoa.Ao);
    }

    [Fact]
    public void ANDADoodadWhoseNumbersDoNotAddUpIsRefusedRatherThanMisread()
    {
        // One float too many before the file: a version whose layout moved reads exactly like this,
        // and taking the last number as the scale would put every doodad at the wrong size quietly.
        string wrong = Version31.Replace("1 0 1 1 7.5 2 \"Metadata/Doodads/Rock.ao\"", "1 0 1 1 7.5 9 2 \"Metadata/Doodads/Rock.ao\"", StringComparison.Ordinal);
        RoomLayout room = RoomLayout.Parse(wrong);

        Assert.False(room.Ready);
        Assert.Contains("accounts for", room.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAFileThatEndsEarlySaysWhere()
    {
        RoomLayout room = RoomLayout.Parse("version 31\n2\n\"a\"\n");

        Assert.False(room.Ready);
        Assert.Contains("ends early", room.Why, StringComparison.Ordinal);
        Assert.False(RoomLayout.Parse("not a room").Ready);
    }

    private static Dictionary<string, byte[]> Install() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/Terrain/Woods/Rooms/Clearing.arm"] = Encoding.Unicode.GetBytes(Version31),
        ["Metadata/Doodads/Rock.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
        ["Metadata/Doodads/Tree.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
        ["art/rock.fmt"] = Packed.Fmt("art/rock.mat"),
    };

    [Fact]
    public void AROOMSDoodadsAreJoinedWhereTheFilePutsThem()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel model = RoomModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm", RoomUnit.World);

        Assert.True(model.Ready, model.Why);
        Assert.Equal(2, model.Parts);

        // Two copies of a two-triangle prop.
        Assert.Equal(4, model.Mesh.Triangles);
        Assert.Equal(model.Mesh.Shapes.Count, model.Skins.Count);

        // THE TREE STANDS AT x 30 IN WORLD UNITS - its prop spans x -1 to 1, unscaled and unturned.
        Assert.Equal(31f, model.Mesh.Most.X, 3);
        Assert.Contains("doodads reach x 30, y 20", model.Move, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDTheUnitScalesThePositionsAndTheReachSaysWhichOneFits()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel cells = RoomModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm", RoomUnit.Cells);

        Assert.True(cells.Ready, cells.Why);
        Assert.Equal((30f * 250f / 23f) + 1f, cells.Mesh.Most.X, 2);

        // The room is 2 x 1 tiles - 46 x 23 cells - and nothing lies past it, so cells is not ruled out.
        RoomModels.Spread spread = RoomModels.SpreadOf(RoomLayout.Parse(Version31));
        Assert.False(spread.NotCells);

        RoomModels.Spread past = RoomModels.SpreadOf(RoomLayout.Parse(Version31.Replace("30 5 0", "300 5 0", StringComparison.Ordinal)));
        Assert.True(past.NotCells);
    }

    [Fact]
    public void ANDADoodadThatWillNotDrawIsCountedAndTheRestStillDraw()
    {
        Dictionary<string, byte[]> files = Install();
        files.Remove("Metadata/Doodads/Tree.ao");

        MonsterModel model = RoomModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm", RoomUnit.World);

        Assert.True(model.Ready, model.Why);
        Assert.Equal(1, model.Parts);
        Assert.Contains("1 doodads did not draw", model.Move, StringComparison.Ordinal);
    }

    [Fact]
    public void THEBOOKTellsRoomsFromTilesAndTheLoaderPicksByTheKey()
    {
        TileBook book = TileBook.Of(
            ["Metadata/Terrain/Woods/Rooms/Clearing.arm", "Metadata/Terrain/Woods/Slash/Arena_01.tdt"], null);

        RowSet? rooms = book.Matching(ColumnQuery.Parse("kind:room").Term, out string why);
        Assert.True(rooms is not null, why);
        var rows = new List<int>();
        rooms!.CopyTo(rows);
        Assert.Equal([book.Row("Metadata/Terrain/Woods/Rooms/Clearing.arm")], rows);
        Assert.Equal(("Woods", "Rooms", "Clearing"), TileBook.Split("Metadata/Terrain/Woods/Rooms/Clearing.arm"));
    }
}
