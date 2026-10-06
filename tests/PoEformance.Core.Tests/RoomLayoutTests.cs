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
        RoomLayout room = RoomLayout.Parse("version 31\n1\n\"a\"\n");

        Assert.False(room.Ready);
        Assert.Contains("ends early", room.Why, StringComparison.Ordinal);
        Assert.False(RoomLayout.Parse("not a room").Ready);
    }

    [Theory]
    [InlineData("version 31\n-3\n", "the string count")]
    [InlineData("version 31\n2147483647\n", "the string count")]
    [InlineData("version 31\n0\n5 3\n-1 -1\n\"tag\"\n0\nk 2 1\n", "doubled")]
    [InlineData("version 31\n0\n5 3\n1500000000\n\"tag\"\n0\nk 2 1\n", "doubled")]
    public void ANDACountTheFileCannotHoldIsRefusedRatherThanThrown(string text, string said)
    {
        // "-3" IS A WHOLE NUMBER, and a count near two billion doubles to a negative one. Both used to
        // land the line index before the start of the array, where the end-of-file check did not
        // look, and the read threw past a method that says it never does.
        RoomLayout room = RoomLayout.Parse(text);

        Assert.False(room.Ready);
        Assert.Contains(said, room.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDADoodadWithANegativeCountIsRefusedRatherThanThrown()
    {
        string pairs = Version36.Replace("4 6 1 0.1 0.2 3.14", "4 6 -1 0.1 0.2 3.14", StringComparison.Ordinal);
        RoomLayout room = RoomLayout.Parse(pairs);
        Assert.False(room.Ready);
        Assert.Contains("pairs", room.Why, StringComparison.Ordinal);

        string floats = Version31.Replace("1 0 1 1 7.5 2 \"Metadata/Doodads/Rock.ao\"", "1 0 1 -2 7.5 2 \"Metadata/Doodads/Rock.ao\"", StringComparison.Ordinal);
        room = RoomLayout.Parse(floats);
        Assert.False(room.Ready);
        Assert.Contains("floats", room.Why, StringComparison.Ordinal);

        string group = Version31.Replace("k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 n\n2\n", "k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 n\n-2\n", StringComparison.Ordinal);
        room = RoomLayout.Parse(group);
        Assert.False(room.Ready);
        Assert.Contains("group", room.Why, StringComparison.Ordinal);
    }

    private static Dictionary<string, byte[]> Install() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/Terrain/Woods/Rooms/Clearing.arm"] = Encoding.Unicode.GetBytes(Version31),
        ["Metadata/Doodads/Rock.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
        ["Metadata/Doodads/Tree.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
        ["art/rock.fmt"] = Packed.Fmt("art/rock.mat"),
    };

    [Fact]
    public void ANDTWODoodadsOnOneSheetShareOneDecodeOfIt()
    {
        // THE ROCK AND THE TREE WEAR ONE MATERIAL. Loaded with a cache per model, the sheet behind it
        // was decoded once each and reached the renderer as two objects - two uploads of one texture.
        Dictionary<string, byte[]> files = Install();
        files["art/rock.mat"] = TileFilesTests.Mat("art/rock.dds");
        files["art/rock.dds"] = TileFilesTests.Dds();

        var handed = new List<string>();
        byte[]? Read(string path)
        {
            byte[]? got = files.GetValueOrDefault(path);
            if (got is not null)
            {
                handed.Add(path);
            }

            return got;
        }

        MonsterModel model = RoomModels.Of(Read, "Metadata/Terrain/Woods/Rooms/Clearing.arm");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(2, model.Skins.Count);
        Assert.NotNull(model.Skins[0]);
        Assert.Same(model.Skins[0], model.Skins[1]);
        Assert.Equal(1, handed.Count(one => one == "art/rock.dds"));
        Assert.Equal(1, handed.Count(one => one == "art/rock.mat"));
    }

    [Fact]
    public void AROOMSDoodadsAreJoinedWhereTheFilePutsThem()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel model = RoomModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(2, model.Parts);

        // Two copies of a two-triangle prop.
        Assert.Equal(4, model.Mesh.Triangles);
        Assert.Equal(model.Mesh.Shapes.Count, model.Skins.Count);

        // THE TREE STANDS AT CELL 30 - 30 cells of 250 / 23 units - and its prop spans x -1 to 1, unscaled and unturned.
        Assert.Equal((30f * 250f / 23f) + 1f, model.Mesh.Most.X, 2);
        Assert.Contains("doodads reach x 30, y 20", model.Move, StringComparison.Ordinal);

        // AND THE DUMP CAN SAY WHICH FILE EACH DOODAD'S MESH IS - once per file, with the file's own
        // facts, which the joined mesh no longer carries. Both doodads here are cut from one file.
        MeshNamed named = Assert.Single(model.Meshes);
        Assert.Equal("art/rock.fmt", named.Path);
        Assert.True(named.Facts.Vertices > 0, "the body's facts travel with the file");

        // AND THE ROOM'S DUMP PRINTS IT, as a room and with no ground section.
        string dump = ModelDump.OfTile(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm", model);
        Assert.StartsWith("room: Metadata/Terrain/Woods/Rooms/Clearing.arm", dump, StringComparison.Ordinal);
        Assert.Contains("=== mesh art/rock.fmt", dump, StringComparison.Ordinal);
        Assert.Contains("format 0x8 · stride", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("=== ground", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAROOMCostsWhatItRead()
    {
        // COUNTED AT THE READER, so the number under the picture is what the install was asked for
        // and gave - the room, the doodads' .ao files and the meshes behind them - and a file asked
        // for and not there (the .mat here) is not a file.
        Dictionary<string, byte[]> files = Install();
        var handed = new List<string>();
        long bytes = 0;
        byte[]? Read(string path)
        {
            byte[]? got = files.GetValueOrDefault(path);
            if (got is not null)
            {
                handed.Add(path);
                bytes += got.Length;
            }

            return got;
        }

        MonsterModel model = RoomModels.Of(Read, "Metadata/Terrain/Woods/Rooms/Clearing.arm");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(handed.Count, model.Files);
        Assert.Equal(bytes, model.Bytes);
        Assert.Contains("Metadata/Terrain/Woods/Rooms/Clearing.arm", handed);
        Assert.Contains("Metadata/Doodads/Rock.ao", handed);
        Assert.Contains("Metadata/Doodads/Tree.ao", handed);
        Assert.Contains("art/rock.fmt", handed);
        Assert.True(model.Files >= 4, $"{model.Files} files");
    }

    [Fact]
    public void ANDTheReachSaysWhenARoomBreaksTheCellRule()
    {
        // The room is 2 x 1 tiles - 46 x 23 cells - and nothing lies past it.
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

        MonsterModel model = RoomModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Woods/Rooms/Clearing.arm");

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
