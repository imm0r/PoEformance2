using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the capture key writes beside its pictures: where it goes, which rooms count as around the player, and what each text file says.
/// </summary>
public class CaptureReportTests
{
    private const float W = MapView.WorldToGrid;
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>A folder is named by when and then where, and what Windows will not take in a name is taken out - wherever the tests run.</summary>
    [Fact]
    public void AFOLDERIsNamedByTimeThenAreaAndSafeForWindows()
    {
        var at = new DateTime(2026, 10, 8, 14, 3, 22, DateTimeKind.Local);
        Assert.Equal(
            "2026-10-08 14-03-22 The Clearfell_ Encampment_",
            CaptureReport.FolderName(at, new AreaInfo("G1_town", "The Clearfell: Encampment?", 1, true, false)));
        Assert.Equal("2026-10-08 14-03-22 G1_2", CaptureReport.FolderName(at, new AreaInfo("G1_2", string.Empty, 1, false, false)));
        Assert.Equal("2026-10-08 14-03-22", CaptureReport.FolderName(at, AreaInfo.Unknown));
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", CaptureReport.Clean("a<b>c:d\"e/f\\g|h?i*j."));
    }

    /// <summary>A position finds its cell and tile by flooring, so a step west of nought is tile minus one rather than tile nought.</summary>
    [Fact]
    public void APOSITIONSTileIsFloored()
    {
        Assert.Equal((Cells, 2 * Cells, 1, 2), CaptureReport.Where((Cells + 0.5f) * W, ((2 * Cells) + 0.5f) * W));
        Assert.Equal((-1, 0, -1, 0), CaptureReport.Where(-0.5f * W, 0.5f * W));
    }

    /// <summary>
    /// The rooms around the player by the tiles they cover: both rooms sharing the rim the player stands on at nought, one in reach at its distance, one past it left out - nearest first.
    /// </summary>
    [Fact]
    public void ROOMSAroundThePlayerAreTheOnesInReachNearestFirst()
    {
        RoomLayout square = RoomArrangementTests.Square();
        RoomLayout one = RoomArrangementTests.Room(1, 1, "f 0");
        RoomArrangement arranged = RoomArrangement.Arrange(
            [
                ("boss.arm", square, RoomArrangementTests.Search(RoomArrangementTests.Place(0, 0, 3, 3))),
                ("offices.arm", square, RoomArrangementTests.Search(RoomArrangementTests.Place(2, 0, 3, 3, missed: 1))),
                ("near.arm", one, RoomArrangementTests.Search(RoomArrangementTests.Place(5, 1, 1, 1))),
                ("far.arm", one, RoomArrangementTests.Search(RoomArrangementTests.Place(6, 1, 1, 1))),
            ],
            12,
            4,
            RoomOverlap.Rims);
        Assert.Equal(4, arranged.Laid.Count);

        List<RoomNearby> near = CaptureReport.RoomsNear(Standing(arranged), 12, 4, 2, 1);
        Assert.Equal([("boss.arm", 0), ("offices.arm", 0), ("near.arm", 3)], near.Select(room => (room.Room, room.Tiles)));
    }

    /// <summary>A room laid twice stands twice, and each place is listed at its own distance.</summary>
    [Fact]
    public void AROOMLaidTwiceIsListedAtEachPlace()
    {
        RoomLayout one = RoomArrangementTests.Room(1, 1, "f 0");
        (string Room, RoomLayout Layout, RoomCandidate Where)[] standing =
        [
            ("wall.arm", one, new RoomCandidate(4, 0, 0, 1, 1, 10, 10)),
            ("wall.arm", one, new RoomCandidate(1, 0, 2, 1, 1, 10, 10)),
            ("wall.arm", one, new RoomCandidate(9, 0, 0, 1, 1, 10, 10)),
        ];

        List<RoomNearby> near = CaptureReport.RoomsNear(standing, 12, 4, 2, 0);
        Assert.Equal([(1, 1), (4, 2)], near.Select(room => (room.Where.X, room.Tiles)));
        Assert.Equal("1 off  wall  at tile 1, 0, 1 x 1, turned 180  (wall.arm)", CaptureReport.Room(near[0]));
    }

    /// <summary>The arrangement's rooms as a standing list - what AreaRooms.Standing gives where the entities cannot be read.</summary>
    private static List<(string Room, RoomLayout Layout, RoomCandidate Where)> Standing(RoomArrangement arranged)
        => [.. arranged.Laid.Select(laid => (laid.Room, laid.Layout, laid.Where))];

    /// <summary>Standing in the notch an L-shaped room leaves empty is not standing in the room - its covered tiles decide, not its rectangle.</summary>
    [Fact]
    public void ANOTCHIsNotTheRoomsFloor()
    {
        RoomLayout ell = RoomArrangementTests.Room(2, 2, "f 0 f 0", "f 0 n");
        RoomArrangement arranged = RoomArrangement.Arrange(
            [("ell.arm", ell, RoomArrangementTests.Search(RoomArrangementTests.Place(0, 0, 2, 2)))], 4, 4, RoomOverlap.Rims);
        RoomCandidate where = Assert.Single(arranged.Laid).Where;
        int[] covered = RoomArrangement.Tiles(ell, where, 4, 4);
        int notch = new[] { 0, 1, 4, 5 }.Single(tile => !covered.Contains(tile));

        Assert.Equal(1, Assert.Single(CaptureReport.RoomsNear(Standing(arranged), 4, 4, notch % 4, notch / 4)).Tiles);
    }

    /// <summary>The summary says when, which build, where the player stood, which rooms are around, and what each part of the capture holds.</summary>
    [Fact]
    public void THESUMMARYSaysWhereAndWhatTheCaptureHolds()
    {
        var player = new WorldEntity(1, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 10f, 20f, -5f, TerrainHeight: -5f);
        var snapshot = new WorldSnapshot(true, player, [player], new float[16], Area: new AreaInfo("G1_2", "Clearfell", 1, false, false), AreaHash: 0xABCD);
        RoomLayout square = RoomArrangementTests.Square();
        var room = new RoomNearby("Metadata/Terrain/Rooms/boss_01.arm", square, new RoomCandidate(0, 0, 0, 3, 3, 10, 10), 0);

        string said = CaptureReport.Summary(
            snapshot, new DateTime(2026, 10, 8, 14, 3, 22, DateTimeKind.Local), "v0.1.119 · local", (0, 0, 1920, 1080), [room], ["game.png  1920 x 1080"]);

        Assert.Contains("capture  2026-10-08 14:03:22 local, PoEformance v0.1.119 · local", said, StringComparison.Ordinal);
        Assert.Contains("area     Clearfell (G1_2), act 1", said, StringComparison.Ordinal);
        Assert.Contains("hash 0x0000ABCD", said, StringComparison.Ordinal);
        Assert.Contains("screen   game client at 0, 0, 1920 x 1080", said, StringComparison.Ordinal);
        Assert.Contains("player   world 10 20 -5, terrain height -5", said, StringComparison.Ordinal);
        Assert.Contains("grid cell 0, 1, tile 0, 0", said, StringComparison.Ordinal);
        Assert.Contains("tile     (no terrain read)", said, StringComparison.Ordinal);
        Assert.Contains("  here   boss_01  at tile 0, 0, 3 x 3, as written  (Metadata/Terrain/Rooms/boss_01.arm)", said, StringComparison.Ordinal);
        Assert.Contains("  game.png  1920 x 1080", said, StringComparison.Ordinal);

        Assert.Contains("(none arranged)", CaptureReport.Summary(snapshot, DateTime.Now, string.Empty, (0, 0, 1, 1), null, []), StringComparison.Ordinal);
    }

    /// <summary>The entities nearest the player first, each with where it stands and where in memory it was.</summary>
    [Fact]
    public void ENTITIESAreListedNearestFirst()
    {
        var player = new WorldEntity(1, 0x1000, "Metadata/Characters/Int/IntFourb", EntityKind.Player, 0f, 0f, 0f);
        var far = new WorldEntity(2, 0x2000, "Metadata/Monsters/Far", EntityKind.Monster, 500f, 0f, 0f);
        var pot = new WorldEntity(3, 0x3000, "Metadata/Terrain/Doodads/Pot", EntityKind.Terrain, 30f, 40f, -115f);
        var snapshot = new WorldSnapshot(true, player, [far, player, pot], new float[16]);

        string[] lines = CaptureReport.Entities(snapshot).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("3 entities", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("0\tPlayer", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("50\tTerrain\t3\t0x3000\t30\t40\t-115", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("500\tMonster", lines[4], StringComparison.Ordinal);
    }

    /// <summary>Each room around the player gets its lights; only the rooms the player stands in get their file printed whole.</summary>
    [Fact]
    public void ROOMSGetTheirLightsAndTheOnesStoodInTheirFile()
    {
        RoomLayout square = RoomArrangementTests.Square();
        RoomNearby here = new("rooms/here.arm", square, new RoomCandidate(0, 0, 0, 3, 3, 10, 10), 0);
        RoomNearby beside = new("rooms/beside.arm", square, new RoomCandidate(3, 0, 0, 3, 3, 10, 10), 2);
        byte[]? Read(string path) => path switch
        {
            "rooms/here.arm" => Encoding.UTF8.GetBytes("version 36\nthe here room"),
            "rooms/beside.arm" => Encoding.UTF8.GetBytes("version 36\nthe beside room"),
            _ => null,
        };

        string said = CaptureReport.Rooms(Read, [here, beside]);
        Assert.Contains("##### here   here  at tile 0, 0", said, StringComparison.Ordinal);
        Assert.Contains("##### 2 off  beside  at tile 3, 0", said, StringComparison.Ordinal);
        Assert.Equal(2, said.Split("=== doodad lights").Length - 1);
        Assert.Contains("=== rooms/here.arm as the file has it\nversion 36\nthe here room", said.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("the beside room", said, StringComparison.Ordinal);
    }

    /// <summary>The environments of several rooms are printed once each, saying which room named them.</summary>
    [Fact]
    public void ENVIRONMENTSOfSeveralRoomsArePrintedOnce()
    {
        const string Shared = "Metadata/EnvironmentSettings/shared.env";
        const string Own = "Metadata/EnvironmentSettings/own.env";
        byte[]? Read(string path) => path switch
        {
            "rooms/a.arm" => Encoding.UTF8.GetBytes("env_file \"" + Shared + "\""),
            "rooms/b.arm" => Encoding.UTF8.GetBytes("env_file \"" + Shared + "\"\nenv_file \"" + Own + "\""),
            Shared => Encoding.UTF8.GetBytes("{ \"shared\": 1 }"),
            Own => Encoding.UTF8.GetBytes("{ \"own\": 2 }"),
            _ => null,
        };

        string said = ModelDump.Environments(Read, ["rooms/a.arm", "rooms/b.arm"], null);
        Assert.StartsWith("=== environments", said, StringComparison.Ordinal);
        Assert.Equal(1, said.Split(Shared).Length - 1); // printed once, though both rooms name it
        Assert.Contains("=== .env " + Shared + "  (named by a)", said, StringComparison.Ordinal);
        Assert.Contains("=== .env " + Own + "  (named by b)", said, StringComparison.Ordinal);
        Assert.Contains("{ \"own\": 2 }", said, StringComparison.Ordinal);
    }

    /// <summary>Every room file the area may lay - the loaded list's and its room set's, once each in path order - is printed whole under its path, and nothing that is not a room.</summary>
    [Fact]
    public void ROOMFILESArePrintedWhole()
    {
        byte[]? Read(string path) => path switch
        {
            "rooms/a.arm" => Encoding.UTF8.GetBytes("version 36\nthe a room"),
            "rooms/b.arm" => Encoding.UTF8.GetBytes("version 36\nthe b room"),
            "rooms/c.arm" => Encoding.UTF8.GetBytes("version 36\nthe c room"),
            "Metadata/Terrain/Test/generate.rs" => Encoding.UTF8.GetBytes("version 2\n100 \"rooms/c.arm\" I\n\"rooms/A.arm\"\n"),
            _ => null,
        };

        string said = CaptureReport.RoomFiles(Read, ["tiles/floor.tdt", "rooms/a.arm", "Metadata/Terrain/Test/generate.rs", "rooms/gone.arm", "rooms/b.arm"]).ReplaceLineEndings("\n");
        Assert.StartsWith(
            "4 room files the area may lay - the loaded list's and its room sets'\n\n##### rooms/a.arm\nversion 36\nthe a room\n\n##### rooms/b.arm\nversion 36\nthe b room\n\n##### rooms/c.arm\nversion 36\nthe c room\n\n##### rooms/gone.arm\n(not in the install)\n",
            said,
            StringComparison.Ordinal);
        Assert.DoesNotContain("floor.tdt", said, StringComparison.Ordinal);
        Assert.DoesNotContain("generate.rs", said, StringComparison.Ordinal);
    }

    /// <summary>Each place's entities are written by the id the sightings table carries, the yielded places marked.</summary>
    [Fact]
    public void PLACEDEntitiesAreWrittenById()
    {
        var survey = new DoodadSurvey(3, 3, 0, 3, [
            new DoodadSighting(700, "p", "m", 0f, 0f, 0f, true),
            new DoodadSighting(701, "p", "m", 1f, 0f, 0f, true),
            new DoodadSighting(702, "p", "m", 2f, 0f, 0f, true),
        ], 1d, string.Empty);
        var kept = new RoomDoodadPlace(new RoomCandidate(1, 2, 0, 3, 3, 3, 3), 3, 3, 1f, []) { Entities = [0, 2] };
        var gone = new RoomDoodadPlace(new RoomCandidate(1, 2, 0, 3, 3, 2, 3), 2, 3, 1f, []) { Entities = [1, 9] };
        (string Room, RoomLayout Layout, RoomDoodadPlaces Places)[] placed =
        [
            ("rooms/one.arm", RoomArrangementTests.Square(), new RoomDoodadPlaces([kept], 3, 3, 3, string.Empty)),
            ("rooms/two.arm", RoomArrangementTests.Square(), new RoomDoodadPlaces([], 3, 3, 3, "yielded") { Yielded = [(gone, "rooms/one.arm")] }),
        ];

        string[] lines = CaptureReport.PlacedEntities(placed, survey).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("one tile 1, 2, as written: 2 entities - 700 702", lines[1]);
        Assert.Equal("two (yielded) tile 1, 2, as written: 2 entities - 701 ?", lines[2]);
    }

    /// <summary>The arrangement's line counts what was drawn, moved, crowded out and not found, and its detail says where each room went and why.</summary>
    [Fact]
    public void THEARRANGEMENTIsSaidAsTheTileBookShowsIt()
    {
        RoomLayout square = RoomArrangementTests.Square();
        RoomArrangement arranged = RoomArrangement.Arrange(
            [
                ("Metadata/Terrain/Rooms/boss_01.arm", square, RoomArrangementTests.Search(RoomArrangementTests.Place(0, 0, 3, 3))),
                ("Metadata/Terrain/Rooms/lost.arm", square, RoomArrangementTests.Search()),
            ],
            6,
            3,
            RoomOverlap.Rims);

        (string said, string detail) = CaptureReport.Arranged(arranged);
        Assert.Equal("rooms: 1 drawn, 1 not found", said);
        Assert.StartsWith("boss_01: tile 0, 0, as written, 100% beside the joins - 0 corners and 0 tiles disagree", detail, StringComparison.Ordinal);
        Assert.EndsWith("lost: its search found nothing", detail, StringComparison.Ordinal);
        Assert.Equal(said + '\n' + detail, CaptureReport.Lined((said, detail)));
        Assert.Equal("alone", CaptureReport.Lined(("alone", string.Empty)));
    }

    /// <summary>The placing's line counts the rooms placed and their places, and its detail has each place's figures, what a room yielded, and why one has no place.</summary>
    [Fact]
    public void THEPLACINGIsSaidAsTheTileBookShowsIt()
    {
        RoomLayout square = RoomArrangementTests.Square();
        var laid = new RoomDoodadPlace(new RoomCandidate(5, 7, 1, 3, 3, 12, 12), 12, 12, 4.2f, []) { TilesAgree = 9, PropHits = 9, Props = 9 };
        var yielded = new RoomDoodadPlace(new RoomCandidate(5, 7, 1, 3, 3, 10, 12), 10, 12, 3f, []) { PropHits = 8, Props = 8 };
        (string Room, RoomLayout Layout, RoomDoodadPlaces Places)[] placed =
        [
            ("Metadata/Terrain/Rooms/desert.arm", square, new RoomDoodadPlaces([laid], 12, 12, 12, string.Empty)),
            ("Metadata/Terrain/Rooms/grass.arm", square, new RoomDoodadPlaces([], 12, 10, 10, "every place it found stands on another room's doodads")
            {
                Yielded = [(yielded, "Metadata/Terrain/Rooms/desert.arm")],
                Props = 8,
            }),
        ];

        (string said, string detail) = CaptureReport.Placed(placed);
        Assert.Equal("rooms: 1 placed by their doodads at 1 places, 1 with no place", said);
        string[] lines = detail.Split('\n');
        Assert.Equal("desert: 1 place, 12 of 12 doodads told by their model - tile 5, 7, turned 90: 12 of 12 doodads within a tile (9 of 9 props), off 4 on average, 9 tiles agree", lines[0]);
        Assert.Equal(
            "grass: no place - every place it found stands on another room's doodads; 10 of its 12 doodad lines stand in the area, 8 of them props, 10 told by their model - yielded tile 5, 7, turned 90 (10 of 12, 8 of 8 props, tiles not scored) to desert",
            lines[1]);
    }

    /// <summary>The survey's line has its three numbers, its detail each path with how many stand, and the sightings table every entity with its model and position.</summary>
    [Fact]
    public void THESURVEYIsSaidWithEverySighting()
    {
        const string Plain = "Metadata/MiscellaneousObjects/Doodad";
        var survey = new DoodadSurvey(
            1274, 1274, 167, 1300,
            [
                new DoodadSighting(7, Plain, "Metadata/Doodads/Pot_01.ao", 1250.5f, 2000f, -115f, true),
                new DoodadSighting(9, Plain, string.Empty, 1300f, 2100f, 0f, false),
                new DoodadSighting(12, "Metadata/Terrain/Doodads/Lamp", "Metadata/Doodads/Lamp.ao", 10f, 20f, -30f, true),
            ],
            47.3d,
            string.Empty);

        (string said, string detail) = CaptureReport.Doodads(survey, null);
        Assert.Equal("doodads: 3 entities stand where the rooms name a doodad, on 2 paths - sleeping map 1274 of 1274 walked, awake 167, 1300 with a path, read in 47 ms", said);
        Assert.Equal([Plain + ": 2 entities, 1 asleep", "Metadata/Terrain/Doodads/Lamp: 1 entities, 1 asleep"], detail.Split('\n'));

        string[] table = CaptureReport.Sightings(survey).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("3 sightings; position in world units, z the game's way up", table[0]);
        Assert.Equal("id\tpath\tmodel\tx\ty\tz\tmap", table[1]);
        Assert.Equal($"7\t{Plain}\tMetadata/Doodads/Pot_01.ao\t1250.5\t2000\t-115\tsleeping", table[2]);
        Assert.Equal($"9\t{Plain}\t\t1300\t2100\t0\tawake", table[3]);

        Assert.Equal(("doodads: not in an area", string.Empty), CaptureReport.Doodads(DoodadSurvey.Not("not in an area"), null));
    }

    /// <summary>The capture key is kept with the rest of the settings.</summary>
    [Fact]
    public void THECAPTUREKeyIsSaved()
    {
        string path = Path.Combine(Path.GetTempPath(), $"capture-key-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(OverlaySettingsStore.Save(OverlaySettings.Default with { CaptureKey = 0x77 }, path));
            Assert.Equal(0x77, OverlaySettingsStore.Load(path).CaptureKey);
            Assert.Equal(0, OverlaySettings.Default.CaptureKey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// What the game itself does with a key the tool listens for - read from the player's own config, the binding set they use.
/// </summary>
public class KeyConflictTests
{
    private static readonly string[] Config =
    [
        "[GENERAL]",
        "user_input_mode=wasd",
        "[SOUND]",
        "master_volume=70",
        "[WASD_ACTION_KEYS]",
        "open_atlas_screen=120",
        "use_flask_in_slot1=81",
        "[ACTION_KEYS]",
        "toggle_map=120 2",
        "open_inventory_panel=70",
    ];

    /// <summary>The live set decides, a modifier after the key changes nothing, and a number outside the binding sections is no key.</summary>
    [Fact]
    public void THELIVEBindingSetSaysWhatAKeyDoes()
    {
        Assert.Equal(["open_atlas_screen"], FlaskKeyBindings.ActionsOn(Config, 0x78));
        Assert.Empty(FlaskKeyBindings.ActionsOn(Config, 0x46));

        string[] clicking = [.. Config.Select(line => line == "user_input_mode=wasd" ? "user_input_mode=click" : line)];
        Assert.Equal(["toggle_map"], FlaskKeyBindings.ActionsOn(clicking, 0x78));
        Assert.Equal(["open_inventory_panel"], FlaskKeyBindings.ActionsOn(clicking, 0x46));
        Assert.Empty(FlaskKeyBindings.ActionsOn(clicking, 0));
    }
}
