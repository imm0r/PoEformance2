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

    /// <summary>
    /// The room laid a quarter turn: its gate slot stands one wide and two tall and its boss slot a cell over, each tile's first piece on the lowest corner of the slot's rectangle turned, as the game lays them - and the place found is the room's corner that way round, not its gate's own cell turned, which would put the room two tiles over.
    /// </summary>
    [Fact]
    public void AROOMTurnedStandsWhereItsSlotsRectanglesTurnedHaveTheirTilesLaidWhole()
    {
        // Turned a quarter, a cell (c, l) of the five by five room goes to (4 - l, c): the gate's two cells to (4, 0) and (4, 1), the boss's nine to (1..3, 1..3).
        (int[] ids, byte[] subX, byte[] subY) = Dunes();
        Lay(ids, subX, subY, 5, 4, 3, 3, 0);
        Lay(ids, subX, subY, 8, 3, 1, 2, 1);
        (TerrainTiles tiles, Func<string, TileIdentity?> identity) = Area(ids, subX, subY);

        RoomTilePlaces found = RoomTileFinder.Find(Room(), tiles, identity);

        RoomTilePlace place = Assert.Single(found.Places);
        Assert.Equal(new RoomCandidate(4, 3, 1, 5, 5, 0, 0) { Tiles = 16, TilesAgree = 16, Big = 2, BigAgree = 2 }, place.Where);
        Assert.Equal((8, 3), (place.AnchorX, place.AnchorY));
        Assert.Equal((1, 2), (place.AnchorWide, place.AnchorTall));

        // The same gate laid two wide and one tall is a gate for the room as written, whose boss slot then stands on the floor: no place either way round.
        (ids, subX, subY) = Dunes();
        Lay(ids, subX, subY, 5, 4, 3, 3, 0);
        Lay(ids, subX, subY, 8, 3, 2, 1, 1);
        (tiles, identity) = Area(ids, subX, subY);
        Assert.Empty(RoomTileFinder.Find(Room(), tiles, identity).Places);
    }

    /// <summary>
    /// A room of one square slot stands on its tile every way round and the places are kept one a way: the way round is what a doodad line tells, the entity standing where one way puts the line confirming that way - and the one other way whose image of the line lies within a tile of it.
    /// </summary>
    [Fact]
    public void AROOMOfOneSquareSlotStandsOnItsTileEveryWayRoundAndItsLinePicksTheWay()
    {
        (int[] ids, byte[] subX, byte[] subY) = Dunes();
        Lay(ids, subX, subY, 5, 4, 3, 3, 0);
        (TerrainTiles tiles, Func<string, TileIdentity?> identity) = Area(ids, subX, subY);
        const string boss = "k 3 3 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0";
        RoomLayout room = RoomLayout.Parse(string.Join('\n',
            "version 36", "1", "\"boss\"", "5 3", "0", "\"roomtag\"", "0",
            "k 3 3 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0",
            "-1", "-1", "-1", "-1", "-1", "-1", "\"\"",
            $"{boss} n n", "n n n", "n n n", "-1") + "\n");
        Assert.True(room.Ready, room.Why);

        RoomTilePlaces found = RoomTileFinder.Find(room, tiles, identity);

        Assert.Equal(8, found.Places.Count);
        Assert.All(found.Places, place => Assert.Equal((5, 4, 3, 3), (place.Where.X, place.Where.Y, place.Where.Width, place.Where.Height)));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], found.Places.Select(place => place.Where.Turn));

        // A line 3 cells along and 30 down: as written 0.13 tiles along and 1.30 down from the corner; mirrored and turned twice (6) it lands 0.4 tiles from that, every other way more than a tile away.
        float side = RoomDoodadFinder.WithinUnits;
        RoomDoodad[] lines = [new RoomDoodad(3, 30, 0f, 1f, string.Empty, Checkpoint)];
        var at = new DoodadSighting(1, Checkpoint, string.Empty, (5f + (3f / TerrainGrid.CellsPerTile)) * side, (4f + (30f / TerrainGrid.CellsPerTile)) * side, 0f, false);
        (IReadOnlyList<RoomTilePlace> picked, bool confirmed) = RoomTileFinder.Confirmed(found.Places, lines, 3, 3, [at]);
        Assert.True(confirmed);
        Assert.Equal([0, 6], picked.Select(place => place.Where.Turn));
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
    /// Settled against the doodads: a place on a doodad place's tiles yields; a room's lone place is its own; a room standing on one tile two ways round, and two variants standing on one tile, become that tile's footprint where the ways disagree on the room's - the variants' under their shared name, as a room of its own - and the room's whole footprint where they agree; a room whose props stand in the area is not asked; a room whose tile is laid more than once gets no place without a doodad to pick one.
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
            (folder + "twice.arm", layout, RoomDoodadPlaces.Not(1, 0, "its doodad lines name no stub")),
            (folder + "pair_01.arm", layout, RoomDoodadPlaces.Not(5, 1, "only 1 of its doodads stand in the area")),
            (folder + "pair_02.arm", layout, RoomDoodadPlaces.Not(5, 1, "only 1 of its doodads stand in the area")),
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
                // One room two ways round, and two variants, agreeing on the room's footprint: the footprint is drawn, not the tile.
                "twice" => new RoomTilePlaces([Place(0, 14, 0, "Metadata/Terrain/Test/stack.tdt", 1, 15), Place(0, 14, 4, "Metadata/Terrain/Test/stack.tdt", 1, 15)], string.Empty),
                "pair_01" => new RoomTilePlaces([Place(8, 14, 0, "Metadata/Terrain/Test/forge.tdt", 9, 15)], string.Empty),
                "pair_02" => new RoomTilePlaces([Place(8, 14, 7, "Metadata/Terrain/Test/forge.tdt", 9, 15)], string.Empty),
                _ => RoomTilePlaces.Not("never asked"),
            };
        }

        List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> settled = RoomTileFinder.Settled(placed, Tiles, []);

        Assert.Equal(["ledge_01", "entrance_01", "entrance_02", "stack", "common", "rare", "twice", "pair_01", "pair_02"], asked);
        Assert.Equal(13, settled.Count);
        Assert.Equal(placed[0], settled[0]);
        Assert.Equal(placed[5], settled[5]);
        Assert.Empty(settled[6].Places.Places);
        Assert.Equal(
            "its doodad lines name no stub - by its tiles, the tiles alike to its 3x3 stack edges [-, -, -, -] grounds [-, -, -, -] slot give 2 places in the area over every way round, none on a tagged feature tile laid once, and no doodad of its picks one",
            settled[6].Places.Why);
        Assert.Empty(settled[7].Places.Places);
        Assert.Contains("give 1 place in the area over every way round, none on a tagged feature tile laid once", settled[7].Places.Why, StringComparison.Ordinal);

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
        Assert.False(stack.WholeRoom);

        (string room, RoomLayout shared, RoomDoodadPlaces places) = settled[11];
        Assert.Equal(folder + "entrance", room);
        Assert.Same(layout, shared);
        RoomDoodadPlace outline = Assert.Single(places.Places);
        Assert.Equal(new RoomCandidate(9, 5, 0, 3, 3, 0, 0), outline.Where);
        Assert.Equal("Metadata/Terrain/Test/forge.tdt", outline.Anchor);
        Assert.Equal([folder + "entrance_01.arm", folder + "entrance_02.arm"], outline.Variants);
        Assert.False(outline.WholeRoom);

        RoomDoodadPlace twice = Assert.Single(settled[8].Places.Places);
        Assert.Equal(new RoomCandidate(0, 14, 0, 5, 5, 0, 0) { Tiles = 4, TilesAgree = 4, Big = 1, BigAgree = 1 }, twice.Where);
        Assert.Equal([folder + "twice.arm"], twice.Variants);
        Assert.True(twice.WholeRoom);

        Assert.Equal("only 1 of its doodads stand in the area - by its tiles one of the rooms drawn once as 'pair' at tile 8, 14, 5 x 5", settled[9].Places.Why);
        Assert.Equal(settled[9].Places.Why, settled[10].Places.Why);
        (room, shared, places) = settled[12];
        Assert.Equal(folder + "pair", room);
        RoomDoodadPlace whole = Assert.Single(places.Places);
        Assert.Equal(new RoomCandidate(8, 14, 0, 5, 5, 0, 0) { Tiles = 4, TilesAgree = 4, Big = 1, BigAgree = 1 }, whole.Where);
        Assert.Equal([folder + "pair_01.arm", folder + "pair_02.arm"], whole.Variants);
        Assert.True(whole.WholeRoom);
    }

    /// <summary>
    /// A doodad is laid by one room: where places of two rooms claim one entity, the footprint whose line stands nearer keeps it and the other's claim is lost - its place, on a tile laid more than once, then has nothing to stand on, and its reason says whose the entity is.
    /// </summary>
    [Fact]
    public void ANENTITYClaimedByTwoFootprintsConfirmsTheNearerLineAlone()
    {
        RoomLayout circle = Circle();
        const string folder = "Metadata/Terrain/Maps/Test/Rooms/Unique/";
        (string Room, RoomLayout Layout, RoomDoodadPlaces Places)[] placed =
        [
            (folder + "near.arm", circle, RoomDoodadPlaces.Not(1, 1, "only 1 of its doodads stand in the area")),
            (folder + "far.arm", circle, RoomDoodadPlaces.Not(1, 1, "only 1 of its doodads stand in the area")),
        ];
        static RoomTilePlace Place(int x, string anchor, int laid)
            => new(new RoomCandidate(x, 0, 0, 3, 3, 0, 0) { Tiles = 1, TilesAgree = 1, Big = 1, BigAgree = 1 }, anchor, laid, x, 0, 3, 3, false);
        static RoomTilePlaces Tiles(string room, RoomLayout _) => TerrainRooms.NameFor(room) switch
        {
            "near" => new RoomTilePlaces([Place(0, "Metadata/Terrain/Test/circle.tdt", 2)], string.Empty) { Anchor = "3x3 circle" },
            "far" => new RoomTilePlaces([Place(1, "Metadata/Terrain/Test/ledge.tdt", 3)], string.Empty) { Anchor = "3x3 ledge" },
            _ => RoomTilePlaces.Not("never asked"),
        };

        // The circle's checkpoint line at 57, 10 cells: 2.48 tiles along and 0.43 down. An entity 8 units right of where the near room's place puts it stands 242 from where the far room's does - a tile over, within a tile still.
        float side = RoomDoodadFinder.WithinUnits;
        RoomDoodad line = Assert.Single(circle.Doodads);
        var entity = new DoodadSighting(1, line.Stub, string.Empty, ((57f / TerrainGrid.CellsPerTile) * side) + 8f, (10f / TerrainGrid.CellsPerTile) * side, 0f, false);

        List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> settled = RoomTileFinder.Settled(placed, Tiles, [entity]);

        RoomDoodadPlace near = Assert.Single(settled[0].Places.Places);
        Assert.Equal(0, near.Where.X);
        Assert.Empty(settled[1].Places.Places);
        Assert.Equal(
            "only 1 of its doodads stand in the area - by its tiles, the tiles alike to its 3x3 ledge slot give 1 place in the area over every way round, none on a tagged feature tile laid once, and no doodad of its picks one - the Checkpoint_Endgame at tile 2.5, 0.4 is near's, whose line stands 8 units from it where this room's stands 242",
            settled[1].Places.Why);
    }

    /// <summary>Sinter Rift's StoneCircle_01 room as its file has it: one three by three "StoneCircle" slot and a checkpoint line at 57, 10 cells.</summary>
    private static RoomLayout Circle()
    {
        RoomLayout room = RoomLayout.Parse(string.Join('\n',
            "version 36", "1", "\"StoneCircle\"", "3 1", "1 1", "\"StoneCircle\"", "1 0",
            "k 3 3 0 0 0 0 9 9 9 9 9 9 9 9 0 0 0 0 0 0 0 0 0 0",
            "0 0 0", "0 0 0", "0 0 0", "0 0 0", "-1", "-1", "-1", "-1",
            "62 4 0.785398 \"checkpoint\"", "-1", "-1", "\"\"",
            "k 3 3 0 0 0 0 9 9 9 9 9 9 9 9 0 0 0 0 0 0 0 0 1 0 n n", "n n n", "n n n",
            "57 10 0 0.756104 0 0 0.369111 0.929385 0 0 0 1 \"Metadata/Terrain/Doodads/Checkpoints/EzomyteCheckpointBaseless.ao\" \"Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame\" 0",
            "-1", "-1", "-1", "0", "-1", "0") + "\n");
        Assert.True(room.Ready, room.Why);
        Assert.Equal((3, 3), (room.Width, room.Height));
        return room;
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
        (int[] ids, byte[] subX, byte[] subY) = Dunes();
        Lay(ids, subX, subY, 5, 4, 3, 3, 0);
        if (gate)
        {
            Lay(ids, subX, subY, 4, 3, 2, 1, 1);
        }

        if (decoyBoss)
        {
            Lay(ids, subX, subY, 9, 7, 3, 3, 0);
        }

        return Area(ids, subX, subY);
    }

    /// <summary>Dune floor everywhere, nothing laid on it yet.</summary>
    private static (int[] Ids, byte[] SubX, byte[] SubY) Dunes()
    {
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, 2);
        return (ids, new byte[ids.Length], new byte[ids.Length]);
    }

    /// <summary>The area as laid, with the three files' identities.</summary>
    private static (TerrainTiles Tiles, Func<string, TileIdentity?> Identity) Area(int[] ids, byte[] subX, byte[] subY)
    {
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
