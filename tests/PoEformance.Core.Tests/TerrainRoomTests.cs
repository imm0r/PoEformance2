using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Grouping the area's tiles into the rooms the game placed them as.
/// </summary>
/// <remarks>
/// The game builds an area out of named room files and writes that name on every tile the room
/// covers, so the layout can be read in words the moment the area loads. What the grouping has
/// to get right is which tiles belong together: one name per room, at the room's own centre,
/// and two placements of the same file in different corners kept apart.
/// </remarks>
public class TerrainRoomTests
{
    private const string Bridge = "Metadata/Terrain/Gallows/Act2/2_8/Rooms/overlay_bridge_03.tdt";
    private const string Rubble = "Metadata/Terrain/Gallows/Act2/2_8/Rooms/rubble_06.tdt";

    private static TerrainTile Tile(string path, int column, int row)
        => new(path, column, row, 0, 0, 0);

    /// <summary>Fills a rectangle of tiles with one room file.</summary>
    private static IEnumerable<TerrainTile> Block(
        string path, int fromColumn, int fromRow, int columns, int rows)
    {
        for (int x = fromColumn; x < fromColumn + columns; x++)
        {
            for (int y = fromRow; y < fromRow + rows; y++)
            {
                yield return Tile(path, x, y);
            }
        }
    }

    [Fact]
    public void ABlockOfTilesUnderOneFileIsONERoom()
    {
        List<TerrainRoom> rooms = TerrainRooms.Find([.. Block(Bridge, 87, 75, 4, 4)], 128, 128);

        TerrainRoom room = Assert.Single(rooms);
        Assert.Equal(16, room.Tiles);
        Assert.Equal("overlay_bridge_03", room.Name);
        Assert.Equal((87, 75, 90, 78), (room.MinTileX, room.MinTileY, room.MaxTileX, room.MaxTileY));
    }

    [Fact]
    public void ARoomSitsAtTheCentreOfItsTilesRatherThanAtACorner()
    {
        // The half-tile is the whole point: a tile is 23 cells across, so anchoring on the mean
        // INDEX puts every name half a tile - 125 world units - toward the map's origin. That
        // offset was shipped once already, by the AHK tool, and had to be corrected.
        TerrainRoom room = Assert.Single(TerrainRooms.Find([.. Block(Bridge, 87, 75, 4, 4)], 128, 128));

        Assert.Equal((88.5f + 0.5f) * TerrainGrid.CellsPerTile, room.GridX, 3);
        Assert.Equal((76.5f + 0.5f) * TerrainGrid.CellsPerTile, room.GridY, 3);
    }

    [Fact]
    public void TwoPlacementsOfTheSameFileAreTwoRooms()
    {
        // Which is what makes the names usable at all: "rubble_06" is placed all over an area,
        // and one room spanning the lot would put a single label in the middle of the map with
        // nothing under it.
        List<TerrainRoom> rooms = TerrainRooms.Find(
            [.. Block(Rubble, 4, 4, 2, 2), .. Block(Rubble, 40, 40, 2, 2)], 64, 64);

        Assert.Equal(2, rooms.Count);
        Assert.All(rooms, room => Assert.Equal(4, room.Tiles));
        Assert.Equal(2, rooms.Select(room => room.Id).Distinct().Count());
    }

    [Fact]
    public void RoomsThatTouchDiagonallyStayApart()
    {
        // Four-neighbour, deliberately: tiles that only meet at a corner are two placements
        // that happen to be adjacent, and joining them would report a room shaped like an X.
        List<TerrainRoom> rooms = TerrainRooms.Find([Tile(Rubble, 5, 5), Tile(Rubble, 6, 6)], 32, 32);

        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public void NeighbouringRoomsOfDifferentFilesAreNotMerged()
    {
        List<TerrainRoom> rooms = TerrainRooms.Find(
            [.. Block(Bridge, 10, 10, 2, 2), .. Block(Rubble, 12, 10, 2, 2)], 64, 64);

        Assert.Equal(2, rooms.Count);
        Assert.Contains(rooms, room => room.Name == "overlay_bridge_03");
        Assert.Contains(rooms, room => room.Name == "rubble_06");
    }

    [Fact]
    public void ATileNamingNoFileIsNotARoom()
        => Assert.Empty(TerrainRooms.Find([Tile(string.Empty, 3, 3)], 16, 16));

    [Fact]
    public void TilesOutsideTheGridAreIgnoredRatherThanThrowing()
    {
        // The tile count comes out of memory beside the tile array, and the two disagreeing is
        // a bad read rather than an impossible one - it must not take the whole terrain down.
        List<TerrainRoom> rooms = TerrainRooms.Find(
            [Tile(Bridge, 99, 1), Tile(Bridge, -1, 1), Tile(Bridge, 2, 2)], 16, 16);

        TerrainRoom room = Assert.Single(rooms);
        Assert.Equal(1, room.Tiles);
    }

    [Fact]
    public void AnIdentityIsTheSameForTheSameRoomAndDiffersBetweenPlaces()
    {
        // A route is keyed by this number, so it has to survive the area being read again -
        // and the top bit has to be set, because that is what keeps a room out of the range
        // any real entity address can occupy.
        Assert.Equal(TerrainRooms.IdFor(Bridge, 87, 75), TerrainRooms.IdFor(Bridge, 87, 75));
        Assert.NotEqual(TerrainRooms.IdFor(Bridge, 87, 75), TerrainRooms.IdFor(Bridge, 87, 76));
        Assert.NotEqual(TerrainRooms.IdFor(Bridge, 87, 75), TerrainRooms.IdFor(Rubble, 87, 75));
        Assert.NotEqual(0UL, TerrainRooms.IdFor(Bridge, 87, 75) & 0x8000_0000_0000_0000UL);
    }

    [Fact]
    public void ARoomIsWrittenDownByItsFileAndItsCorner()
    {
        TerrainRoom room = Assert.Single(TerrainRooms.Find([.. Block(Bridge, 87, 75, 2, 2)], 128, 128));

        // Readable in the settings file, which a hash would not be - and made of the two facts
        // that find the same room again on the next visit.
        Assert.Equal($"{Bridge}@87,75", room.Key);
    }

    [Fact]
    public void TheNameKeepsTheGamesOwnSpelling()
    {
        // Unlike a landmark's label, which is tidied for reading. A room's name IS the file,
        // and prettifying it would break reading it back against the game's own data.
        Assert.Equal("overlay_superman", TerrainRooms.NameFor(
            "Metadata/Terrain/Gallows/Act2/2_8/Rooms/Overlays/overlay_superman.arm"));

        Assert.Equal("3open_01", TerrainRooms.NameFor("Metadata/Terrain/X/3open_01.tdt"));
        Assert.Equal("plain", TerrainRooms.NameFor("plain"));
    }

    [Fact]
    public void AnAreaWithNoTileCountFindsNothing()
        => Assert.Empty(TerrainRooms.Find([Tile(Bridge, 1, 1)], 0, 0));

    [Fact]
    public void SceneryIsTheRoomWithNoGroundToStandOn()
    {
        // The distinction the labels live or die by. An area's tile grid is a full rectangle
        // and its walkable ground a subset, so the buildings along the road and the sea beside
        // them are rooms with names - and they are most of what an area is built from.
        List<TerrainRoom> rooms = TerrainRooms.Find(
            [.. Block(Bridge, 10, 10, 2, 2), .. Block(Rubble, 20, 20, 2, 2)],
            64,
            64,
            (x, y) => x < 15);

        TerrainRoom road = rooms.Single(room => room.Name == "overlay_bridge_03");
        TerrainRoom scenery = rooms.Single(room => room.Name == "rubble_06");

        Assert.Equal(4, road.WalkableTiles);
        Assert.True(road.IsWalkable);
        Assert.Equal(0, scenery.WalkableTiles);
        Assert.False(scenery.IsWalkable);
    }

    [Fact]
    public void ARoomIsWalkableWhenANYOfItsTilesIs()
    {
        // Not all of them: a room is a placed piece of level, and its own walls are inside it.
        // Requiring every tile would drop the room the doorway is in.
        TerrainRoom room = Assert.Single(TerrainRooms.Find(
            [.. Block(Bridge, 10, 10, 3, 1)], 64, 64, (x, y) => x == 11));

        Assert.Equal(3, room.Tiles);
        Assert.Equal(1, room.WalkableTiles);
        Assert.True(room.IsWalkable);
    }

    [Fact]
    public void NoOpinionAboutWalkabilityCountsEveryTile()
    {
        // "No filter", never "hide everything". A caller that cannot answer the question - a
        // test, a page drawing the layout from outside the game - must not silently get an
        // empty map.
        TerrainRoom room = Assert.Single(TerrainRooms.Find([.. Block(Bridge, 3, 3, 2, 2)], 32, 32));

        Assert.Equal(room.Tiles, room.WalkableTiles);
        Assert.True(room.IsWalkable);
    }
}
