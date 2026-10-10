using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// A room of big slots placed by the tiles laid where they fall - see RoomTileFinder.
/// </summary>
/// <remarks>
/// THE AREA IS BUILT SO THE ANSWER IS KNOWN: a floor of one by one dune tiles, a three by three "boss"
/// tile laid twice and a two by one "gate" tile laid once, the two together only where the room stands.
/// The gate is the rarer and anchors the room; the second boss tile has no gate beside it and gives
/// nothing; the seven other ways round put the boss slot on the floor and fail.
/// </remarks>
public class RoomTileFinderTests
{
    private const int TilesX = 12;
    private const int TilesY = 10;
    private const string Boss = "Metadata/Terrain/Test/boss.tdt";
    private const string Gate = "Metadata/Terrain/Test/gate.tdt";
    private const string Floor = "Metadata/Terrain/Test/floor.tdt";
    private const string FloorType = "Metadata/Terrain/Test/floor.gt";
    private const string Checkpoint = "Metadata/MiscellaneousObjects/Checkpoint";

    [Fact]
    public void THEROOMStandsWhereEveryBigSlotHasItsTilesFirstPieceOnItsOwnCell()
    {
        (TerrainTiles tiles, Func<string, TileIdentity?> identity) = Area(decoyBoss: true);

        RoomTilePlaces found = RoomTileFinder.Find(Room(), tiles, identity);

        Assert.Equal(string.Empty, found.Why);
        Assert.Equal(1, found.Anchors);
        RoomTilePlace place = Assert.Single(found.Places);
        Assert.Equal(new RoomCandidate(4, 3, 0, 5, 5, 0, 0) { Tiles = 16, TilesAgree = 16, Big = 2, BigAgree = 2 }, place.Where);
        Assert.Equal(Gate, place.Anchor);
        Assert.Equal(1, place.AnchorLaid);
        Assert.Equal((4, 3), (place.AnchorX, place.AnchorY));
        Assert.Equal((2, 1), (place.AnchorWide, place.AnchorTall));
        Assert.True(place.AnchorTagged);
        Assert.StartsWith("2x1 gate", found.Anchor, StringComparison.Ordinal);
    }

    /// <summary>A big slot whose tile is laid nowhere is a room the area did not lay; a room of one by one slots is the search's, not this.</summary>
    [Fact]
    public void AROOMWhoseBigSlotsTileIsLaidNowhereOrThatHasNoneHasNoPlaceAndSaysWhy()
    {
        (TerrainTiles tiles, Func<string, TileIdentity?> identity) = Area(gate: false);

        RoomTilePlaces found = RoomTileFinder.Find(Room(), tiles, identity);
        Assert.Empty(found.Places);
        Assert.Contains("gate", found.Why, StringComparison.Ordinal);
        Assert.Contains("laid nowhere in the area", found.Why, StringComparison.Ordinal);

        const string floor = "k 1 1 0 0 0 0 0 0 0 0 0 0 0 0 1 1 1 1 0 0 0 0 0 0";
        RoomLayout small = RoomLayout.Parse(string.Join('\n',
            "version 36", "1", $"\"{FloorType}\"", "5 3", "0", "\"roomtag\"", "0",
            "k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0",
            "-1", "-1", "-1", "-1", "-1", "-1", "\"\"", $"{floor} {floor}", "-1") + "\n");
        Assert.True(small.Ready, small.Why);
        Assert.Contains("no slot bigger than one tile", RoomTileFinder.Find(small, tiles, identity).Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A doodad line too lonely to place the room picks among the places the tiles found: the one its entity stands in. No entity anywhere, or one standing in none of them, keeps every place.
    /// </summary>
    [Fact]
    public void AFEWDoodadLinesConfirmThePlaceTheirEntityStandsIn()
    {
        float side = RoomDoodadFinder.WithinUnits;
        var a = new RoomTilePlace(new RoomCandidate(0, 0, 0, 3, 3, 0, 0), "t.tdt", 2, 0, 0, 3, 3, true);
        var b = new RoomTilePlace(new RoomCandidate(6, 4, 0, 3, 3, 0, 0), "t.tdt", 2, 6, 4, 3, 3, true);
        RoomDoodad[] lines = [new RoomDoodad(TerrainGrid.CellsPerTile, TerrainGrid.CellsPerTile, 0f, 1f, string.Empty, Checkpoint)];
        static DoodadSighting At(string path, float x, float y) => new(1, path, string.Empty, x, y, 0f, false);
        static void Expect(RoomTilePlace[] places, bool confirmed, (IReadOnlyList<RoomTilePlace> Places, bool Confirmed) found)
        {
            Assert.Equal(places, found.Places);
            Assert.Equal(confirmed, found.Confirmed);
        }

        Expect([b], true, RoomTileFinder.Confirmed([a, b], lines, 3, 3, [At(Checkpoint, 7f * side, 5f * side)]));
        Expect([a, b], false, RoomTileFinder.Confirmed([a, b], lines, 3, 3, []));
        Expect([a, b], false, RoomTileFinder.Confirmed([a, b], lines, 3, 3, [At(Checkpoint, 3f * side, 3f * side)]));
        Expect([a, b], false, RoomTileFinder.Confirmed([a, b], lines, 3, 3, [At("Metadata/MiscellaneousObjects/Doodad", 7f * side, 5f * side)]));
        Expect([b], true, RoomTileFinder.Confirmed([b], lines, 3, 3, [At(Checkpoint, 7f * side, 5f * side)]));
    }

    /// <summary>
    /// Settled against the doodads: a place on a doodad place's tiles yields; a room's lone place is its own; a room standing on one tile two ways round, and two variants standing on one tile, become that tile's footprint - the variants' under their shared name, as a room of its own; a room whose props stand in the area is not asked; a room whose tile is laid more than once gets no place without a doodad to pick one.
    /// </summary>
    [Fact]
    public void SETTLEDKeepsARoomsOwnPlacesAndDrawsASharedTileOnce()
    {
        RoomLayout layout = Room();
        const string folder = "Metadata/Terrain/Maps/Test/Rooms/Unique/";
        var byDoodads = new RoomDoodadPlace(new RoomCandidate(0, 0, 0, 3, 3, 3, 3), 3, 3, 1f, []);
        (string Room, RoomLayout Layout, RoomDoodadPlaces Places)[] placed =
        [
            (folder + "boss.arm", layout, new RoomDoodadPlaces([byDoodads], 3, 3, 3, string.Empty)),
            (folder + "ledge_01.arm", layout, RoomDoodadPlaces.Not(1, 0, "its doodad lines name no stub")),
            (folder + "entrance_01.arm", layout, RoomDoodadPlaces.Not(5, 1, "only 1 of its doodads stand in the area")),
            (folder + "entrance_02.arm", layout, RoomDoodadPlaces.Not(5, 1, "only 1 of its doodads stand in the area")),
            (folder + "stack.arm", layout, RoomDoodadPlaces.Not(1, 0, "its doodad lines name no stub")),
            (folder + "props.arm", layout, RoomDoodadPlaces.Not(6, 4, "no tile collects two thirds of its props")),
            (folder + "common.arm", layout, RoomDoodadPlaces.Not(1, 0, "its doodad lines name no stub")),
            (folder + "rare.arm", layout, RoomDoodadPlaces.Not(1, 0, "its doodad lines name no stub")),
        ];

        static RoomTilePlace Place(int x, int y, int turn, string anchor, int anchorX, int anchorY, int laid = 1, bool tagged = true)
            => new(new RoomCandidate(x, y, turn, 5, 5, 0, 0) { Tiles = 4, TilesAgree = 4, Big = 1, BigAgree = 1 }, anchor, laid, anchorX, anchorY, 3, 3, tagged);
        var asked = new List<string>();
        RoomTilePlaces Tiles(string room, RoomLayout _)
        {
            asked.Add(TerrainRooms.NameFor(room));
            return TerrainRooms.NameFor(room) switch
            {
                // The first stands on the boss's tiles past the rims and yields; the second is the room's.
                "ledge_01" => new RoomTilePlaces([Place(1, 1, 0, "Metadata/Terrain/Test/ledge.tdt", 2, 2), Place(6, 0, 0, "Metadata/Terrain/Test/ledge.tdt", 7, 1)], string.Empty),
                "entrance_01" => new RoomTilePlaces([Place(8, 4, 0, "Metadata/Terrain/Test/forge.tdt", 9, 5)], string.Empty),
                "entrance_02" => new RoomTilePlaces([Place(7, 3, 0, "Metadata/Terrain/Test/forge.tdt", 9, 5)], string.Empty),
                "stack" => new RoomTilePlaces([Place(0, 6, 0, "Metadata/Terrain/Test/stack.tdt", 1, 7), Place(1, 6, 1, "Metadata/Terrain/Test/stack.tdt", 1, 7)], string.Empty),
                // A feature tile laid three times, and no doodad of the room's in the area to pick one.
                "common" => new RoomTilePlaces([Place(0, 10, 0, "Metadata/Terrain/Test/common.tdt", 1, 11, 3), Place(6, 10, 0, "Metadata/Terrain/Test/common.tdt", 7, 11, 3)], string.Empty) { Anchor = "3x3 stack edges [-, -, -, -] grounds [-, -, -, -]" },
                // A piece of terrain that happens to be laid once: no feature, so no fingerprint.
                "rare" => new RoomTilePlaces([Place(0, 20, 0, "Metadata/Terrain/Test/ledge_rare.tdt", 1, 21, 1, tagged: false)], string.Empty) { Anchor = "3x3 edges [-, -, ledge, ledge] grounds [-, -, -, -]" },
                _ => RoomTilePlaces.Not("never asked"),
            };
        }

        List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> settled = RoomTileFinder.Settled(placed, Tiles, []);

        Assert.Equal(["ledge_01", "entrance_01", "entrance_02", "stack", "common", "rare"], asked);
        Assert.Equal(9, settled.Count);
        Assert.Equal(placed[0], settled[0]);
        Assert.Equal(placed[5], settled[5]);
        Assert.Empty(settled[6].Places.Places);
        Assert.Equal(
            "its doodad lines name no stub - by its tiles, the tiles alike to its 3x3 stack edges [-, -, -, -] grounds [-, -, -, -] slot stand 2 times in the area, none of them a tagged feature tile laid once, and no doodad of its picks one",
            settled[6].Places.Why);
        Assert.Empty(settled[7].Places.Places);
        Assert.Contains("stand 1 times in the area, none of them a tagged feature tile laid once", settled[7].Places.Why, StringComparison.Ordinal);

        RoomDoodadPlace ledge = Assert.Single(settled[1].Places.Places);
        Assert.Equal(new RoomCandidate(6, 0, 0, 5, 5, 0, 0) { Tiles = 4, TilesAgree = 4, Big = 1, BigAgree = 1 }, ledge.Where);
        Assert.Equal(("Metadata/Terrain/Test/ledge.tdt", 1, 4), (ledge.Anchor, ledge.AnchorLaid, ledge.TilesAgree));
        Assert.Empty(ledge.Variants);
        Assert.Equal(string.Empty, settled[1].Places.Why);

        Assert.Empty(settled[2].Places.Places);
        Assert.Equal("only 1 of its doodads stand in the area - by its tiles one of the rooms drawn once as 'entrance' on the forge tile at 9, 5", settled[2].Places.Why);
        Assert.Equal(settled[2].Places.Why, settled[3].Places.Why);

        RoomDoodadPlace stack = Assert.Single(settled[4].Places.Places);
        Assert.Equal(new RoomCandidate(1, 7, 0, 3, 3, 0, 0), stack.Where);
        Assert.Equal([folder + "stack.arm"], stack.Variants);

        (string room, RoomLayout shared, RoomDoodadPlaces places) = settled[8];
        Assert.Equal(folder + "entrance", room);
        Assert.Same(layout, shared);
        RoomDoodadPlace outline = Assert.Single(places.Places);
        Assert.Equal(new RoomCandidate(9, 5, 0, 3, 3, 0, 0), outline.Where);
        Assert.Equal("Metadata/Terrain/Test/forge.tdt", outline.Anchor);
        Assert.Equal([folder + "entrance_01.arm", folder + "entrance_02.arm"], outline.Variants);
    }

    [Fact]
    public void THESTEMOfVariantNamesIsWhatTheyShare()
    {
        Assert.Equal("entrance", RoomTileFinder.Stem(["entrance_01", "entrance_02", "entrance_04"]));
        Assert.Equal("checkpoint", RoomTileFinder.Stem(["checkpoint_02", "checkpoint_03"]));
        Assert.Equal("boss", RoomTileFinder.Stem(["boss"]));
        Assert.Equal("boss/entrance_01", RoomTileFinder.Stem(["boss", "entrance_01"]));
        Assert.Equal(string.Empty, RoomTileFinder.Stem([]));
    }

    /// <summary>A five by five room: a two by one "gate" slot in its down-left corner, a three by three "boss" slot in its middle, one by one floor slots round them.</summary>
    private static RoomLayout Room()
    {
        const string gate = "k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 2 0";
        const string boss = "k 3 3 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0";
        const string floor = "k 1 1 0 0 0 0 0 0 0 0 0 0 0 0 3 3 3 3 0 0 0 0 0 0";
        RoomLayout room = RoomLayout.Parse(string.Join('\n',
            "version 36", "3", "\"boss\"", "\"gate\"", $"\"{FloorType}\"",
            "5 3", "0", "\"roomtag\"", "0",
            "k 5 5 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0",
            "-1", "-1", "-1", "-1", "-1", "-1",
            "\"\"",
            $"{gate} n {floor} {floor} {floor}",
            $"{floor} {boss} n n {floor}",
            $"{floor} n n n {floor}",
            $"{floor} n n n {floor}",
            $"{floor} {floor} {floor} {floor} {floor}",
            "-1") + "\n");
        Assert.True(room.Ready, room.Why);
        Assert.Equal(string.Empty, room.SlotsWhy);
        Assert.Equal((3, 3), (room.SlotAt(1, 1).Width, room.SlotAt(1, 1).Height));
        return room;
    }

    /// <summary>The area: dune floor everywhere, the boss tile at (5, 4), the gate at (4, 3) beside it where asked, and a second boss tile at (9, 7) where asked.</summary>
    private static (TerrainTiles Tiles, Func<string, TileIdentity?> Identity) Area(bool decoyBoss = false, bool gate = true)
    {
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, 2);
        var subX = new byte[ids.Length];
        var subY = new byte[ids.Length];
        Lay(ids, subX, subY, 5, 4, 3, 3, 0);
        if (gate)
        {
            Lay(ids, subX, subY, 4, 3, 2, 1, 1);
        }

        if (decoyBoss)
        {
            Lay(ids, subX, subY, 9, 7, 3, 3, 0);
        }

        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)-1);
        var tiles = new TerrainTiles([Boss, Gate, Floor], ids, subX, subY, placements, TilesX, TilesY);
        var identities = new Dictionary<string, TileIdentity>(StringComparer.Ordinal)
        {
            [Boss] = new TileIdentity(3, 3, "boss", ["", "", "", ""], ["", "", "", ""]),
            [Gate] = new TileIdentity(2, 1, "gate", ["", "", "", ""], ["", "", "", ""]),
            [Floor] = new TileIdentity(1, 1, string.Empty, ["", "", "", ""], [FloorType, FloorType, FloorType, FloorType]),
        };
        return (tiles, path => identities.GetValueOrDefault(path));
    }

    /// <summary>Lays one tile whole: every cell of its footprint carries its id and its own piece.</summary>
    private static void Lay(int[] ids, byte[] subX, byte[] subY, int x, int y, int width, int height, int id)
    {
        for (var dy = 0; dy < height; dy++)
        {
            for (var dx = 0; dx < width; dx++)
            {
                int at = ((y + dy) * TilesX) + x + dx;
                ids[at] = id;
                subX[at] = (byte)dx;
                subY[at] = (byte)dy;
            }
        }
    }
}
