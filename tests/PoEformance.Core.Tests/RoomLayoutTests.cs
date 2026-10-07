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

        // THE COUNTED FLOATS' FIRST IS KEPT AS THE HEIGHT - the rock counts one, the tree none.
        Assert.Equal(7.5f, rock.Height);
        Assert.Null(room.Doodads[1].Height);
        Assert.Null(rock.Exact);
    }

    /// <summary>A pot's line from seepage's 2x2_offices_01 as the game wrote it: its exact place, its turn, and the height it carries.</summary>
    [Fact]
    public void ADOODADLineCarriesItsExactPlaceAndAHeight()
    {
        string text = Version36.Replace(
            "4 6 1 0.1 0.2 3.14 0 0 0 1 1 0 0 1 \"Metadata/Doodads/Rhoa.ao\" \"stub\" 1 lane=4",
            "311 195 1 3386.6 2125.29 3.1196 0 0 0.99994 0.010994 1 0 1 -240 1 \"Metadata/Terrain/Doodads/Jungle/VaalProps/VaalPots/VaalPotCluster01_Light.ao\" \"Metadata/Terrain/Doodads/Jungle/VaalProps/VaalPots/VaalPotCluster01\" 0",
            StringComparison.Ordinal);
        RoomLayout room = RoomLayout.Parse(text);

        Assert.True(room.Ready, room.Why);
        RoomDoodad pot = Assert.Single(room.Doodads);
        Assert.Equal((311, 195), (pot.X, pot.Y));
        Assert.Equal(3.1196f, pot.Turn);
        Assert.Equal(1f, pot.Scale);
        Assert.Equal(-240f, pot.Height);
        Assert.Equal(new System.Numerics.Vector2(3386.6f, 2125.29f), pot.Exact);
        Assert.Equal("Metadata/Terrain/Doodads/Jungle/VaalProps/VaalPots/VaalPotCluster01", pot.Stub);
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
        Assert.Equal(new System.Numerics.Vector2(0.1f, 0.2f), rhoa.Exact);
        Assert.Null(rhoa.Height);
    }

    /// <summary>A three by two room of version 36 whose tail after the doodads ends in the given lines.</summary>
    private static string Tailed(params string[] tail) => string.Join('\n', [
        "version 36", "1", "\"Metadata/Terrain/Woods/ground.gt\"", "5 3", "0", "\"roomtag\"", "0",
        "k 3 2 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0",
        "-1", "-1", "-1", "-1", "-1", "-1", "\"\"",
        "n n n", "n n n",
        "4 6 1 0.1 0.2 3.14 0 0 0 1 1 0 0 1 \"Metadata/Doodads/Rhoa.ao\" \"stub\" 0", "-1",
        .. tail]);

    /// <summary>
    /// The tail after the doodads is read as poe_data_tools reads it, to the ground overrides at the very end.
    /// </summary>
    /// <remarks>
    /// AND PAST ITS WARNING: the last boss line is often written without its line end, so the line
    /// after it - here the zones' closing -1 - follows its quoted strings, and must be read as a line.
    /// </remarks>
    [Fact]
    public void THEGROUNDOverridesAtTheEndAreReadEvenWhereTheLastBossLineSwallowedTheNext()
    {
        RoomLayout room = RoomLayout.Parse(Tailed("-1", "-1", "1", "\"Boss\" \"Metadata/Monsters/Boss\" -1", "1 nospawner", "1 0"));

        Assert.True(room.Ready, room.Why);
        Assert.Equal(string.Empty, room.OverridesWhy);
        Assert.Equal([1, 0], room.GroundOverrides);
        Assert.Equal(1, room.OverrideAt(1, 1));
        Assert.Equal(0, room.OverrideAt(2, 1));
        Assert.Equal(0, room.OverrideAt(0, 0));
        Assert.Single(room.Doodads);

        // The same with the line end where it belongs.
        Assert.Equal([1, 0], RoomLayout.Parse(Tailed("-1", "-1", "1", "\"Boss\" \"Metadata/Monsters/Boss\"", "-1", "0", "1 0")).GroundOverrides);
    }

    /// <summary>
    /// A room with no line of overrides has none and nothing to say about it; one whose line does not fit its grid says so, and keeps its doodads.
    /// </summary>
    [Fact]
    public void ANDAROOMWithoutThemHasNoneAndABadLineSaysWhyKeepingTheRest()
    {
        RoomLayout none = RoomLayout.Parse(Tailed("-1", "-1", "0", "-1", "0"));
        Assert.Empty(none.GroundOverrides);
        Assert.Equal(string.Empty, none.OverridesWhy);

        RoomLayout bad = RoomLayout.Parse(Tailed("-1", "-1", "0", "-1", "0", "1 0 1"));
        Assert.True(bad.Ready, bad.Why);
        Assert.Empty(bad.GroundOverrides);
        Assert.Contains("2 inner corners", bad.OverridesWhy, StringComparison.Ordinal);
        Assert.Single(bad.Doodads);
        Assert.Equal(6, bad.Slots.Count);
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

        var progress = new ModelProgress();
        MonsterModel model = RoomModels.Of(Read, "Metadata/Terrain/Woods/Rooms/Clearing.arm", progress: progress);

        Assert.True(model.Ready, model.Why);
        Assert.Equal(2, model.Skins.Count);

        // THE BAR FOLLOWED THE DOODADS, the outermost step - each doodad's own painting inside it did not count.
        Assert.Equal("laying the doodads", progress.Stage);
        Assert.True(progress.Total > 0);
        Assert.Equal(progress.Total, progress.Done);
        Assert.NotNull(model.Skins[0]);
        Assert.Same(model.Skins[0], model.Skins[1]);
        Assert.Equal(1, handed.Count(one => one == "art/rock.dds"));
        Assert.Equal(1, handed.Count(one => one == "art/rock.mat"));

        // AND EACH SHAPE'S COLOUR MAP IS CARRIED, so the dump names it rather than "tex -".
        Assert.Equal(["art/rock.dds", "art/rock.dds"], model.ShapeTextures);

        // AND WHAT EACH SHAPE CAME FROM, so the probe can name the doodad under a pixel.
        Assert.Equal(["Metadata/Doodads/Rock.ao", "Metadata/Doodads/Tree.ao"], model.ShapeSources);
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
    public void THEDOODADCapIsTheCallersAndSaysWhatItLeftOut()
    {
        Dictionary<string, byte[]> files = Install();
        const string path = "Metadata/Terrain/Woods/Rooms/Clearing.arm";

        MonsterModel one = RoomModels.Of(p => files.GetValueOrDefault(p), path, doodads: 1);
        Assert.True(one.Ready, one.Why);
        Assert.Equal(1, one.Parts);
        Assert.Contains("1 more left out to keep it turnable - 1 doodads or 1000 triangles", one.Move, StringComparison.Ordinal);

        // Zero is the usual, which places both.
        Assert.Equal(2, RoomModels.Of(p => files.GetValueOrDefault(p), path, doodads: 0).Parts);
    }

    [Fact]
    public void ARoomKeyCarriesItsCapAndTheUsualOneWritesTheBarePath()
    {
        const string path = "Metadata/Terrain/Maps/Port/Rooms/Unique/boss_01.arm";

        Assert.Equal(path, new RoomKey(path).ToString());
        Assert.Equal(path + "|doodads=800", new RoomKey(path, 800).ToString());
        Assert.Equal(new RoomKey(path, 800), RoomKey.Read(path + "|doodads=800"));
        Assert.Equal(new RoomKey(path), RoomKey.Read(path));

        // Outside the slider's ends, or not a number: the usual.
        Assert.Equal(RoomModels.UsualDoodads, RoomKey.Read(path + "|doodads=99999").Doodads);
        Assert.Equal(RoomModels.UsualDoodads, RoomKey.Read(path + "|doodads=lots").Doodads);

        // AND THE TOOLS, with the cap or alone - and a key without them hides them.
        Assert.Equal(path + "|tools", new RoomKey(path, Tools: true).ToString());
        Assert.Equal(path + "|doodads=800+tools", new RoomKey(path, 800, Tools: true).ToString());
        Assert.Equal(new RoomKey(path, 800, Tools: true), RoomKey.Read(path + "|doodads=800+tools"));
        Assert.Equal(new RoomKey(path, Tools: true), RoomKey.Read(path + "|tools"));
        Assert.False(RoomKey.Read(path + "|doodads=800").Tools);

        // AND THE PLACE IT IS LAID AT, read back whole - and one that does not read is dropped, not half-kept.
        string laid = RoomKey.LaidAt(13, 21, 7, -12345);
        var key = new RoomKey(path, 800, Tools: true, Laid: laid);
        Assert.Equal(path + "|doodads=800+tools+laid=13,21,7,-12345", key.ToString());
        Assert.Equal(key, RoomKey.Read(key.ToString()));
        Assert.True(RoomKey.Read(key.ToString()).TryLaid(out int x, out int y, out int turn, out int area));
        Assert.Equal((13, 21, 7, -12345), (x, y, turn, area));
        Assert.Equal(string.Empty, RoomKey.Read(path + "|laid=13,21,9,5").Laid);
        Assert.Equal(string.Empty, RoomKey.Read(path + "|laid=nowhere").Laid);
        Assert.False(new RoomKey(path).TryLaid(out _, out _, out _, out _));

        // AND THE PIECES AT THEIR TILES' LEVELS, a word of its own that reads back - and is off unless written.
        var level = new RoomKey(path, Laid: laid, AtLevel: true);
        Assert.Equal(path + "|laid=13,21,7,-12345+level", level.ToString());
        Assert.Equal(level, RoomKey.Read(level.ToString()));
        Assert.False(RoomKey.Read(key.ToString()).AtLevel);

        // AND HOW HIGH A DOODAD WITH A HEIGHT IS SET, ground unless written.
        var file = new RoomKey(path, Heights: DoodadHeight.File);
        Assert.Equal(path + "|heights=file", file.ToString());
        Assert.Equal(file, RoomKey.Read(file.ToString()));
        var added = new RoomKey(path, Laid: laid, AtLevel: true, Heights: DoodadHeight.Added);
        Assert.Equal(path + "|laid=13,21,7,-12345+level+heights=added", added.ToString());
        Assert.Equal(added, RoomKey.Read(added.ToString()));
        Assert.Equal(DoodadHeight.Ground, RoomKey.Read(level.ToString()).Heights);
        Assert.Equal(DoodadHeight.Ground, RoomKey.Read(path + "|heights=sideways").Heights);
    }

    /// <summary>A doodad whose line carries a height is set at it where asked, on a room of its own where the ground is nought; one carrying none stays put.</summary>
    [Fact]
    public void ADOODADWithAHeightIsSetAtItWhereAsked()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel ground = RoomModels.Of(p => files.GetValueOrDefault(p), "Metadata/Terrain/Woods/Rooms/Clearing.arm");
        MonsterModel file = RoomModels.Of(p => files.GetValueOrDefault(p), "Metadata/Terrain/Woods/Rooms/Clearing.arm", heights: DoodadHeight.File);
        MonsterModel added = RoomModels.Of(p => files.GetValueOrDefault(p), "Metadata/Terrain/Woods/Rooms/Clearing.arm", heights: DoodadHeight.Added);
        Assert.True(ground.Ready, ground.Why);

        // THE ROCK CARRIES 7.5 AND THE TREE NOTHING - the rock's shape moves by it, the tree's not at all.
        MeshShape rock = ground.Mesh.Shapes[0];
        MeshShape tree = ground.Mesh.Shapes[1];
        Assert.Equal(7.5f, Z(file, rock) - Z(ground, rock), 3);
        Assert.Equal(7.5f, Z(added, rock) - Z(ground, rock), 3);
        Assert.Equal(0f, Z(file, tree) - Z(ground, tree), 3);

        static float Z(MonsterModel model, MeshShape shape) => model.Mesh.Positions[model.Mesh.Indices[shape.From]].Z;
    }

    /// <summary>
    /// The level editor's tools are hidden unless asked for: a doodad the room names DoodadInvisible, and anything from the tools folder.
    /// </summary>
    [Fact]
    public void THELEVELEDITORSToolsAreHiddenUnlessAskedFor()
    {
        Dictionary<string, byte[]> files = Install();
        const string path = "Metadata/Terrain/Woods/Rooms/Clearing.arm";
        const string Blocker = "Metadata/Terrain/Doodads/Tools/Blocker_Walk_3_1_01.ao";
        files[path] = Encoding.Unicode.GetBytes(Version31
            .Replace("\"Metadata/Doodads/Rock.ao\" \"stub\"", "\"Metadata/Doodads/Rock.ao\" \"Metadata/MiscellaneousObjects/DoodadInvisible\"", StringComparison.Ordinal)
            .Replace("\"Metadata/Doodads/Tree.ao\"", "\"" + Blocker + "\"", StringComparison.Ordinal));
        files[Blocker] = files["Metadata/Doodads/Tree.ao"];
        Func<string, byte[]?> read = one => files.GetValueOrDefault(one);

        RoomLayout room = RoomLayout.Read(read(path));
        Assert.All(room.Doodads, one => Assert.True(RoomModels.IsTool(one), one.Ao));

        MonsterModel hidden = RoomModels.Of(read, path);
        Assert.False(hidden.Ready);
        Assert.Contains("the room places only the level editor's tools, which are hidden", hidden.Why, StringComparison.Ordinal);

        MonsterModel shown = RoomModels.Of(read, path, tools: true);
        Assert.True(shown.Ready, shown.Why);
        Assert.Equal(2, shown.Parts);

        // ONE OF EACH, so the line under the picture says how many went.
        files[path] = Encoding.Unicode.GetBytes(Version31.Replace("\"Metadata/Doodads/Tree.ao\"", "\"" + Blocker + "\"", StringComparison.Ordinal));
        MonsterModel one = RoomModels.Of(read, path);
        Assert.True(one.Ready, one.Why);
        Assert.Equal(1, one.Parts);
        Assert.Contains("1 of the level editor's tools hidden", one.Move, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoomThatDidNotLoadIsStillDumpedWithItsOwnFile()
    {
        // "the room places no doodads" is a claim about the file, so the dump of such a room is the
        // file itself and the reason - Port's town rooms said it and could not be checked.
        Dictionary<string, byte[]> files = Install();
        const string path = "Metadata/Terrain/Woods/Rooms/Clearing.arm";
        MonsterModel failed = MonsterModel.None with { Why = "the room places no doodads: " + path };

        string dump = ModelDump.OfTile(one => files.GetValueOrDefault(one), path, failed);

        Assert.StartsWith("room: " + path, dump, StringComparison.Ordinal);
        Assert.Contains("=== .arm " + path, dump, StringComparison.Ordinal);
        Assert.Contains("\"Metadata/Doodads/Rock.ao\"", dump, StringComparison.Ordinal);
        Assert.Contains("did not load: the room places no doodads", dump, StringComparison.Ordinal);
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
