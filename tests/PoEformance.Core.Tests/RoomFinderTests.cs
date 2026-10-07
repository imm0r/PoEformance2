using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// A room found on the area's ground by the corner types its one by one slots name.
/// </summary>
/// <remarks>
/// THE AREA IS BUILT SO THE ANSWER IS KNOWN: floor on the left and walkable, wall on the right, and
/// the room's corner pattern pressed into the wall turned a quarter and mirrored. The pattern holds
/// rubble corners, which nothing else in the area has, so exactly one place and one way round can
/// fit - and the placement is worked out here from the two operations written out, not from the
/// finder's own arithmetic.
/// </remarks>
public class RoomFinderTests
{
    private const int Cells = TerrainGrid.CellsPerTile;
    private const int Corner = TerrainGroundTypes.BytesPerCorner;
    private const int TilesX = 20;
    private const int TilesY = 10;
    private const int Floor = 10;

    private const string WallType = "Metadata/Terrain/Test/wall.gt";
    private const string FloorType = "Metadata/Terrain/Test/floor.gt";
    private const string RubbleType = "Metadata/Terrain/Test/rubble.gt";

    private static readonly string[] Types = [string.Empty, WallType, FloorType, RubbleType];

    /// <summary>The room's corner types, [u, v] for a three by two room: 1 wall, 2 floor, 3 rubble - which only the room has - and no symmetry.</summary>
    private static readonly int[,] Pattern =
    {
        { 1, 3, 1 },
        { 3, 2, 1 },
        { 2, 2, 3 },
        { 1, 3, 2 },
    };

    [Fact]
    public void AROOMSGridReadsEverySlotAndItsCornersByTheFilesOwnConvention()
    {
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));

        Assert.True(room.Ready, room.Why);
        Assert.Equal(string.Empty, room.SlotsWhy);
        Assert.Equal(6, room.Slots.Count);
        Assert.Equal([WallType, FloorType, RubbleType], room.Strings);

        // The slot at column 1, line 0: its down-left corner is the grid corner (1, 0), its up-right (2, 1).
        RoomSlot slot = room.SlotAt(1, 0);
        Assert.True(slot.IsTile);
        Assert.Equal((1, 1), (slot.Width, slot.Height));
        Assert.Equal(RubbleType, room.Named(slot.Ground(0)));
        Assert.Equal(FloorType, room.Named(slot.Ground(1)));
        Assert.Equal(FloorType, room.Named(slot.Ground(2)));
        Assert.Equal(FloorType, room.Named(slot.Ground(3)));
        Assert.Equal(WallType, room.Named(room.SlotAt(0, 1).Ground(3)));
    }

    [Fact]
    public void ANDTheFinderFindsItWhereAndHowItWasLaidAndNowhereElse()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));

        RoomSearch search = RoomFinder.Find(room, ground, TilesX, TilesY);

        Assert.Equal(string.Empty, search.Why);
        Assert.True(search.Found);
        RoomCandidate only = Assert.Single(search.Candidates);
        Assert.Equal(new RoomCandidate(12, 4, 5, 2, 3, 12, 12), only);
        Assert.Equal("turned 90, mirrored", RoomFinder.Said(only.Turn));
    }

    [Fact]
    public void ANDWhereNothingFitsExactlyTheNearestAreSaidWithHowFarOff()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, spoil: true);
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));

        RoomSearch search = RoomFinder.Find(room, ground, TilesX, TilesY);

        Assert.False(search.Found);
        Assert.Equal(11, search.Candidates[0].Matched);
        Assert.Contains(new RoomCandidate(12, 4, 5, 2, 3, 11, 12), search.Candidates);
        Assert.True(search.Candidates.Count <= RoomFinder.Nearest);

        // AND WHERE IT MISSES: the one corner spoiled, for the map's red dot.
        int at = search.Candidates.ToList().IndexOf(new RoomCandidate(12, 4, 5, 2, 3, 11, 12));
        Assert.Equal([(14, 7)], search.Misses[at].Corners);
    }

    /// <summary>
    /// A corner the room leaves at nought names no ground and fits whatever the area laid there.
    /// </summary>
    /// <remarks>
    /// THE ROOMS' OWN READING: the channel's 1open_01.arm leaves its 36 inner corners - its floor - at
    /// nought while the area names that floor's type. Here the area keeps floor at the corner the room
    /// now leaves unnamed, and the room is still found, exactly, with that corner counted free.
    /// </remarks>
    [Fact]
    public void ACORNERTheRoomLeavesUnnamedFitsWhateverTheAreaLaidThere()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        var freed = (int[,])Pattern.Clone();
        freed[1, 1] = 0;

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(Room(freed, 3, 2)), ground, TilesX, TilesY);

        Assert.True(search.Found);
        RoomCandidate only = Assert.Single(search.Candidates);
        Assert.Equal(new RoomCandidate(12, 4, 5, 2, 3, 11, 11), only);
        Assert.Equal(1, search.Free);
    }

    /// <summary>
    /// Two places fit the ground exactly; the tiles laid under each say which is the room.
    /// </summary>
    /// <remarks>
    /// THE TEMPLE'S CASE, made small: the pattern pressed twice into the wall, once mirrored and turned
    /// a quarter at (12, 4), once as written at (16, 1). Under the first every slot's cell holds a
    /// tile whose sizes, edges and grounds are what that slot asks for; under the second the tiles
    /// carry an edge type the room never names - see <see cref="RoomAndDecoyTiles"/>.
    /// </remarks>
    [Fact]
    public void ANDWhereTwoPlacesFitTheGroundTheTilesLaidSayWhichIsTheRoom()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, decoy: true);
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();

        RoomSearch search = RoomFinder.Find(room, ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path));

        Assert.True(search.TileChecked);
        Assert.Equal(2, search.Fits);
        RoomCandidate first = search.Candidates[0];
        Assert.True(first.Exact);
        Assert.Equal((12, 4, 5), (first.X, first.Y, first.Turn));
        Assert.Equal((6, 6), (first.TilesAgree, first.Tiles));

        // The decoy is listed too, below every place any tile agrees with, with all its corners and none of its tiles.
        int decoy = search.Candidates.ToList().FindIndex(one => (one.X, one.Y, one.Turn) == (16, 1, 0));
        Assert.True(decoy > 0);
        Assert.True(search.Candidates[decoy].Exact);
        Assert.Equal((0, 6), (search.Candidates[decoy].TilesAgree, search.Candidates[decoy].Tiles));

        // The map's orange rings: every tile of the decoy, none of the room.
        Assert.Empty(search.Misses[0].Tiles);
        Assert.Equal(6, search.Misses[decoy].Tiles.Count);
        Assert.Contains((16, 1), search.Misses[decoy].Tiles);
    }

    /// <summary>
    /// The room misses a corner where it lies and a decoy fits every one; the tiles still rank the room first.
    /// </summary>
    /// <remarks>
    /// SEEPAGE'S BOSS ARENA, made small. Where it stands it agreed on 208 of 218 corners and 191 of 205
    /// tiles - the misses all along the side it joins the map by - while the void fitted every corner
    /// with 150 tiles. A list that put every corner first listed only the void.
    /// </remarks>
    [Fact]
    public void AROOMMissingACornerWhereItLiesStillOutranksADecoyThatFitsEveryCorner()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, spoil: true, decoy: true);
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(Room(Pattern, 3, 2)), ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path));

        RoomCandidate first = search.Candidates[0];
        Assert.Equal((12, 4, 5), (first.X, first.Y, first.Turn));
        Assert.Equal((11, 12, 6, 6), (first.Matched, first.Corners, first.TilesAgree, first.Tiles));
        Assert.Equal([(14, 7)], search.Misses[0].Corners);
        Assert.Equal(1, search.Fits);
        Assert.Contains(search.Candidates, one => (one.X, one.Y, one.Turn) == (16, 1, 0) && one.Exact && one.TilesAgree == 0);
    }

    /// <summary>
    /// Over walkable ground only, a placement covering none is left out - the room of pure scenery among them, which is why it is a choice.
    /// </summary>
    /// <remarks>
    /// The test area's room is pressed into the wall, right of the walkable floor: asked for walkable
    /// ground only, the search leaves it out and lists only places reaching the floor; asked for
    /// anywhere, or given a mask with no walkable tile at all, it finds the room.
    /// </remarks>
    [Fact]
    public void OVERWALKABLEGROUNDOnlyAPlaceCoveringNoneIsLeftOut()
    {
        (TerrainGroundTypes ground, TerrainGrid walkable) = Area(withRoom: true, decoy: true);
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));
        bool[] mask = walkable.WalkableTileMask();
        Assert.True(mask[(4 * TilesX) + Floor - 1]);
        Assert.False(mask[(4 * TilesX) + Floor]);

        RoomSearch standing = RoomFinder.Find(room, ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path), walkable: mask);

        Assert.True(standing.Standing);
        Assert.NotEmpty(standing.Candidates);
        Assert.All(standing.Candidates, one => Assert.True(one.X < Floor, $"{one} covers no walkable tile"));
        Assert.Equal(0, standing.Fits);

        RoomSearch anywhere = RoomFinder.Find(room, ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path));
        Assert.False(anywhere.Standing);
        Assert.Equal((12, 4, 5), (anywhere.Candidates[0].X, anywhere.Candidates[0].Y, anywhere.Candidates[0].Turn));

        RoomSearch nowhere = RoomFinder.Find(room, ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path), walkable: new bool[TilesX * TilesY]);
        Assert.False(nowhere.Standing);
        Assert.Equal(anywhere.Candidates, nowhere.Candidates);
    }

    /// <summary>
    /// More places fit the ground than are listed, and the room is the last of them in grid order; it is still ranked first.
    /// </summary>
    /// <remarks>
    /// THE SEEPAGE CASE, made small. 0.1.97 kept the first fits in grid order up to the cap and ranked
    /// only those by their tiles; the void past a map's edge fits a room's rim thousands of times and
    /// comes first in that order, so the room itself was never ranked. Here the cap is one: the decoy,
    /// as written, comes before the room, mirrored and turned, and only the tiles say which is which.
    /// </remarks>
    [Fact]
    public void MOREPLACESFITTHANARELISTEDAndTheTilesStillRankTheRoomFirst()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, decoy: true);
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(Room(Pattern, 3, 2)), ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path), most: 1);

        RoomCandidate only = Assert.Single(search.Candidates);
        Assert.Equal((12, 4, 5), (only.X, only.Y, only.Turn));
        Assert.Equal((6, 6), (only.TilesAgree, only.Tiles));
        Assert.Equal(2, search.Fits);
        Assert.Equal(1, search.More);
    }

    /// <summary>
    /// The probe for the tile a person stands on gives the room's best placement over it, scored both ways, whether or not it made the list.
    /// </summary>
    [Fact]
    public void AROUNDATileTheRoomIsLaidItsBestWayOverItAndScoredBothWays()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, decoy: true);
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();
        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(Room(Pattern, 3, 2)), ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path), most: 1);

        // Inside the room's footprint, (12..13, 4..6): the room as it was laid, everything agreeing.
        RoomPlace? room = search.Around(13, 5);
        Assert.NotNull(room);
        Assert.Equal(new RoomCandidate(12, 4, 5, 2, 3, 12, 12) { Tiles = 6, TilesAgree = 6 }, room.Where);
        Assert.Same(RoomMisses.None, room.Misses);

        // Over the decoy, which the list left out: its ground fits, every one of its tiles does not.
        RoomPlace? decoy = search.Around(17, 2);
        Assert.NotNull(decoy);
        Assert.Equal((16, 1, 0, 12, 0), (decoy.Where.X, decoy.Where.Y, decoy.Where.Turn, decoy.Where.Matched, decoy.Where.TilesAgree));
        Assert.Equal(6, decoy.Misses.Tiles.Count);

        Assert.Null(search.Around(TilesX, 0));
        Assert.Null(RoomSearch.Not("nothing").Around(13, 5));
    }

    /// <summary>
    /// The room's slots as tiles under the room, mirrored and turned at (12, 4), and under the decoy, as written at (16, 1), with an edge the room never names.
    /// </summary>
    /// <remarks>The cells by hand: turned that way, slot (c, l) falls on cell (13 - l, 6 - c); as written, on (16 + c, 1 + l).</remarks>
    private static (TerrainTiles Laid, Dictionary<string, TileIdentity> Identities) RoomAndDecoyTiles()
    {
        var paths = new List<string>();
        var identities = new Dictionary<string, TileIdentity>(StringComparer.Ordinal);
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, -1);
        for (var line = 0; line < 2; line++)
        {
            for (var column = 0; column < 3; column++)
            {
                IReadOnlyList<string> grounds = Grounds(column, line);
                Lay(paths, identities, ids, 13 - line, 6 - column, new TileIdentity(1, 1, string.Empty, ["", "", "", ""], grounds));
                Lay(paths, identities, ids, 16 + column, 1 + line, new TileIdentity(1, 1, string.Empty, ["Metadata/Terrain/Test/cliff.et", "", "", ""], grounds));
            }
        }

        return (Laid(paths, ids), identities);
    }

    /// <summary>
    /// A point of the room goes where its corners were found: the laying matrix and the found placement agree corner for corner.
    /// </summary>
    /// <remarks>
    /// The area helper presses the pattern in mirrored and turned a quarter at (12, 4): corner (u, v)
    /// lands at (12 + 2 - v, 4 + 3 - u), written out by hand there - so the matrix the room's doodads
    /// are laid by is checked against that, not against the finder's own arithmetic.
    /// </remarks>
    [Fact]
    public void APOINTOfTheRoomIsLaidWhereItsCornersWereFound()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        RoomCandidate found = Assert.Single(RoomFinder.Find(RoomLayout.Parse(Room(Pattern, 3, 2)), ground, TilesX, TilesY).Candidates);
        Matrix3x2 laying = RoomFinder.Laying(3, 2, found.Turn);
        for (var u = 0; u <= 3; u++)
        {
            for (var v = 0; v <= 2; v++)
            {
                Vector2 at = Vector2.Transform(new Vector2(u, v), laying);
                Assert.Equal((12f + 2 - v, 4f + 3 - u), (found.X + at.X, found.Y + at.Y));
            }
        }

        // As written it is the room itself; turned a quarter, (u, v) goes to (h - v, u).
        Assert.Equal(Matrix3x2.Identity, RoomFinder.Laying(3, 2, 0));
        Assert.Equal(new Vector2(2f - 1f, 3f), Vector2.Transform(new Vector2(3f, 1f), RoomFinder.Laying(3, 2, 1)));
    }

    /// <summary>What follows the doodads in a version 36 room, down to an optional line of ground overrides.</summary>
    private static string Tail(string overrides = "") => "-1\n-1\n0\n-1\n0\n" + (overrides.Length > 0 ? overrides + "\n" : string.Empty);

    /// <summary>
    /// The file's ground overrides name an inner corner its slots leave free, and the search holds the area to it.
    /// </summary>
    /// <remarks>
    /// The room's inner corners are (1, 1) and (2, 1), written in that order. With (1, 1) freed in the
    /// slots and named floor by the overrides - what the area has there - the room is found with every
    /// corner named; named rubble instead, it misses there by one.
    /// </remarks>
    [Fact]
    public void THEFILESGroundOverridesNameAnInnerCornerAndTheSearchHoldsTheAreaToIt()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        var freed = (int[,])Pattern.Clone();
        freed[1, 1] = 0;

        RoomSearch named = RoomFinder.Find(RoomLayout.Parse(Room(freed, 3, 2) + Tail("2 0")), ground, TilesX, TilesY);

        Assert.True(named.Overrides);
        Assert.Equal(1, named.Overridden);
        Assert.Equal(0, named.Free);
        Assert.True(named.Found);
        Assert.Equal(new RoomCandidate(12, 4, 5, 2, 3, 12, 12), Assert.Single(named.Candidates));

        RoomSearch wrong = RoomFinder.Find(RoomLayout.Parse(Room(freed, 3, 2) + Tail("3 0")), ground, TilesX, TilesY);
        Assert.False(wrong.Found);
        Assert.Contains(new RoomCandidate(12, 4, 5, 2, 3, 11, 12), wrong.Candidates);

        RoomSearch without = RoomFinder.Find(RoomLayout.Parse(Room(freed, 3, 2) + Tail()), ground, TilesX, TilesY);
        Assert.False(without.Overrides);
        Assert.Equal(string.Empty, without.OverridesWhy);
        Assert.Equal(1, without.Free);
    }

    /// <summary>
    /// Where the room is joined to the map, its misses are told apart: the opening, the tiles beside it, and the opening's corners.
    /// </summary>
    /// <remarks>
    /// SEEPAGE'S OFFICES, made small. The room lies at (12, 4) over tiles 12..13 by 4..6. On its right
    /// rim the area laid its own tiles: (13, 5) walkable with walkable ground beyond at (14, 5) - the
    /// opening - and (13, 4), (13, 6) beside it, not walkable - the caps - and turned the opening's
    /// corner (14, 5) from floor to wall. Every miss is a join's; none is anywhere else.
    /// </remarks>
    [Fact]
    public void WHERETheRoomIsJoinedToTheMapItsMissesAreToldApartAsAJoin()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true, set: [(14, 5, 1)]);
        (TerrainTiles laid, Dictionary<string, TileIdentity> identities) = RoomAndDecoyTiles();
        foreach ((int x, int y) in new[] { (13, 4), (13, 5), (13, 6) })
        {
            string path = string.Create(CultureInfo.InvariantCulture, $"Metadata/Terrain/Test/tile_{x}_{y}.tdt");
            identities[path] = identities[path] with { Edges = ["Metadata/Terrain/Test/door.et", "", "", ""] };
        }

        var walkable = new bool[TilesX * TilesY];
        foreach ((int x, int y) in new[] { (12, 4), (12, 5), (12, 6), (13, 5), (14, 5) })
        {
            walkable[(y * TilesX) + x] = true;
        }

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(Room(Pattern, 3, 2)), ground, TilesX, TilesY, laid, path => identities.GetValueOrDefault(path), walkable: walkable);

        int at = search.Candidates.ToList().FindIndex(one => (one.X, one.Y, one.Turn) == (12, 4, 5));
        Assert.True(at >= 0);
        Assert.Equal((11, 3, 6), (search.Candidates[at].Matched, search.Candidates[at].TilesAgree, search.Candidates[at].Tiles));
        RoomMisses misses = search.Misses[at];
        Assert.True(misses.Classified);
        Assert.Equal([(13, 5)], misses.Openings);
        Assert.Equal([(13, 4), (13, 6)], misses.Caps.OrderBy(one => one.Y));
        Assert.Equal([(14, 5)], misses.JoinCorners);
        Assert.Equal(1, misses.Joins);
        Assert.Equal(0, misses.Elsewhere);

        // AND IN FULL: the opening's tile and corner, each with what was asked and what was laid.
        IReadOnlyList<RoomPart> parts = search.Parts(search.Candidates[at]);
        Assert.Equal(4, parts.Count);
        RoomPart opening = Assert.Single(parts, one => one.Join == RoomJoin.Opening);
        Assert.Equal((13, 5, false, true), (opening.X, opening.Y, opening.IsCorner, opening.Walkable));
        Assert.Equal(4, opening.SideEdges.Count);
        Assert.Equal(8, opening.SideExits.Count);
        Assert.Contains("door", opening.Laid, StringComparison.Ordinal);
        RoomPart corner = Assert.Single(parts, one => one.IsCorner);
        Assert.Equal((14, 5, RoomJoin.Corner, "floor", "wall"), (corner.X, corner.Y, corner.Join, corner.Wanted, corner.Laid));
        Assert.Equal(2, parts.Count(one => one.Join == RoomJoin.Cap));
    }

    /// <summary>
    /// A slot bigger than a tile, left out of the ground's stamp, is checked against the tile laid where it falls - either way round.
    /// </summary>
    [Fact]
    public void ABIGSlotLeftOutOfTheGroundIsCheckedAgainstTheTileLaidThere()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        string text = Room(Pattern, 3, 2);
        int first = text.IndexOf("k 1 1 ", StringComparison.Ordinal);
        RoomLayout room = RoomLayout.Parse(string.Concat(text.AsSpan(0, first), "k 2 1 ", text.AsSpan(first + 6)));
        Assert.Equal((2, 1), (room.SlotAt(0, 0).Width, room.SlotAt(0, 0).Height));

        var paths = new List<string>();
        var identities = new Dictionary<string, TileIdentity>(StringComparer.Ordinal);
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, -1);
        for (var line = 0; line < 2; line++)
        {
            for (var column = 0; column < 3; column++)
            {
                // Laid turned, so the two by one tile reads one by two - which must still agree.
                int height = column == 0 && line == 0 ? 2 : 1;
                Lay(paths, identities, ids, 13 - line, 6 - column, new TileIdentity(1, height, string.Empty, ["", "", "", ""], Grounds(column, line)));
            }
        }

        RoomSearch search = RoomFinder.Find(room, ground, TilesX, TilesY, Laid(paths, ids), path => identities.GetValueOrDefault(path));

        Assert.Equal(1, search.Left);
        Assert.Equal(1, search.Fits);
        RoomCandidate found = search.Candidates[0];
        Assert.Equal((12, 4, 5), (found.X, found.Y, found.Turn));
        Assert.Equal((1, 1), (found.BigAgree, found.Big));
        Assert.Equal((6, 6), (found.TilesAgree, found.Tiles));
    }

    /// <summary>The ground types at one slot's four corners, down-left round to up-left, by name.</summary>
    private static string[] Grounds(int column, int line)
        => [Name(Pattern[column, line]), Name(Pattern[column + 1, line]), Name(Pattern[column + 1, line + 1]), Name(Pattern[column, line + 1])];

    private static string Name(int type) => type switch
    {
        1 => WallType,
        2 => FloorType,
        3 => RubbleType,
        _ => string.Empty,
    };

    private static void Lay(List<string> paths, Dictionary<string, TileIdentity> identities, int[] ids, int x, int y, TileIdentity identity)
    {
        string path = string.Create(CultureInfo.InvariantCulture, $"Metadata/Terrain/Test/tile_{x}_{y}.tdt");
        ids[(y * TilesX) + x] = paths.Count;
        paths.Add(path);
        identities[path] = identity;
    }

    private static TerrainTiles Laid(List<string> paths, int[] ids)
    {
        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)-1);
        return new TerrainTiles(paths, ids, new byte[ids.Length], new byte[ids.Length], placements, TilesX, TilesY);
    }

    [Fact]
    public void AROOMWhoseGroundTheAreaDoesNotListIsNotLaidThere()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        string text = Room(Pattern, 3, 2).Replace(FloorType, "Metadata/Terrain/Elsewhere/sand.gt", StringComparison.Ordinal);

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(text), ground, TilesX, TilesY);

        Assert.Empty(search.Candidates);
        Assert.Contains("sand.gt is not among this area's", search.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ASLOTBiggerThanOneTileIsLeftOutOfTheStampAndCounted()
    {
        (TerrainGroundTypes ground, _) = Area(withRoom: true);
        string text = Room(Pattern, 3, 2).Replace("k 1 1 ", "k 3 3 ", StringComparison.Ordinal);

        RoomSearch search = RoomFinder.Find(RoomLayout.Parse(text), ground, TilesX, TilesY);

        Assert.Empty(search.Candidates);
        Assert.Equal(6, search.Left);
        Assert.Contains("names no corner's ground in a one by one k slot", search.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The channel's 1open_01.arm agrees with itself at every shared corner and side read the way the finder reads it.
    /// </summary>
    /// <remarks>
    /// THE EVIDENCE FOR THE CONVENTION, kept as a test: a nine by nine grid of one by one slots, from
    /// the channel room dump. Down is the side towards the grid's first line, the corners run
    /// down-left, down-right, up-right, up-left, the sides down, right, up, left - and so read, a
    /// slot's right side and right-hand corners are its right neighbour's left ones, and its top its
    /// upper neighbour's bottom. Of the eight ways to place the four corners only this one agrees
    /// everywhere; the next best leaves 21 shared corners in conflict.
    /// </remarks>
    [Fact]
    public void THECHANNELSROOMAgreesWithItselfAtEverySharedCornerAndSide()
    {
        RoomLayout room = RoomLayout.Parse(File.ReadAllText(Fixture("rooms/channel_1open_01.arm")));

        Assert.True(room.Ready, room.Why);
        Assert.Equal(string.Empty, room.SlotsWhy);
        Assert.Equal((9, 9), (room.Width, room.Height));

        int corners = 0;
        int sides = 0;
        for (var line = 0; line < room.Height; line++)
        {
            for (var column = 0; column < room.Width; column++)
            {
                RoomSlot slot = room.SlotAt(column, line);
                if (!slot.IsTile)
                {
                    continue;
                }

                Assert.Equal((1, 1), (slot.Width, slot.Height));
                RoomSlot right = room.SlotAt(column + 1, line);
                if (right.IsTile)
                {
                    Assert.Equal(slot.Ground(1), right.Ground(0));
                    Assert.Equal(slot.Ground(2), right.Ground(3));
                    Assert.Equal(slot.Edge(1), right.Edge(3));
                    corners += 2;
                    sides++;
                }

                RoomSlot up = room.SlotAt(column, line + 1);
                if (up.IsTile)
                {
                    Assert.Equal(slot.Ground(3), up.Ground(0));
                    Assert.Equal(slot.Ground(2), up.Ground(1));
                    Assert.Equal(slot.Edge(2), up.Edge(0));
                    corners += 2;
                    sides++;
                }
            }
        }

        Assert.True(corners >= 190, $"{corners} shared corners compared");
        Assert.True(sides >= 100, $"{sides} shared sides compared");
    }

    private static string Fixture(string path)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", path);
    }

    /// <summary>A version 36 room of one by one k slots whose corners carry the pattern, by the down-left, down-right, up-right, up-left order; nought names no ground.</summary>
    private static string Room(int[,] pattern, int width, int height)
    {
        var text = new StringBuilder();
        text.AppendLine("version 36").AppendLine("3")
            .AppendLine(CultureInfo.InvariantCulture, $"\"{WallType}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"\"{FloorType}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"\"{RubbleType}\"")
            .AppendLine("5 3").AppendLine("0").AppendLine("\"roomtag\"").AppendLine("0")
            .AppendLine(CultureInfo.InvariantCulture, $"k {width} {height} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0");
        for (var group = 0; group < 6; group++)
        {
            text.AppendLine("-1");
        }

        text.AppendLine("\"\"");
        for (var line = 0; line < height; line++)
        {
            var slots = new List<string>();
            for (var column = 0; column < width; column++)
            {
                int dl = pattern[column, line];
                int dr = pattern[column + 1, line];
                int ur = pattern[column + 1, line + 1];
                int ul = pattern[column, line + 1];
                slots.Add(string.Create(CultureInfo.InvariantCulture, $"k 1 1 0 0 0 0 0 0 0 0 0 0 0 0 {dl} {dr} {ur} {ul} 0 0 0 0 0 0"));
            }

            text.AppendLine(string.Join(' ', slots));
        }

        text.AppendLine("-1");
        return text.ToString();
    }

    /// <summary>
    /// Floor and walkable on the left, wall on the right, and - where asked - the pattern pressed into
    /// the wall at tile (12, 4), mirrored and then turned a quarter: (u, v) goes to (3 - u, v), then to (2 - v, 3 - u).
    /// </summary>
    private static (TerrainGroundTypes Ground, TerrainGrid Walkable) Area(bool withRoom, bool spoil = false, bool decoy = false, (int X, int Y, byte Type)[]? set = null)
    {
        int across = TilesX + 1;
        var corners = new byte[across * (TilesY + 1) * Corner];
        for (var y = 0; y <= TilesY; y++)
        {
            for (var x = 0; x <= TilesX; x++)
            {
                corners[((y * across) + x) * Corner] = (byte)(x < Floor ? 2 : 1);
            }
        }

        if (withRoom)
        {
            for (var u = 0; u <= 3; u++)
            {
                for (var v = 0; v <= 2; v++)
                {
                    int mirroredU = 3 - u;
                    int x = 12 + (2 - v);
                    int y = 4 + mirroredU;
                    corners[((y * across) + x) * Corner] = (byte)Pattern[u, v];
                }
            }
        }

        if (decoy)
        {
            // The pattern again, as written, at (16, 1): corner (u, v) to (16 + u, 1 + v).
            for (var u = 0; u <= 3; u++)
            {
                for (var v = 0; v <= 2; v++)
                {
                    corners[(((1 + v) * across) + 16 + u) * Corner] = (byte)Pattern[u, v];
                }
            }
        }

        if (spoil)
        {
            // One wall corner of the room's turned to floor: where it lies, the room now misses by one.
            corners[(((4 + 3) * across) + 12 + 2) * Corner] = 2;
        }

        foreach ((int x, int y, byte type) in set ?? [])
        {
            corners[((y * across) + x) * Corner] = type;
        }

        int width = TilesX * Cells;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * TilesY * Cells];
        for (var y = 0; y < TilesY * Cells; y++)
        {
            for (var x = 0; x < Floor * Cells; x++)
            {
                cells[(y * stride) + (x >> 1)] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
            }
        }

        var walkable = new TerrainGrid(cells, stride, TilesY * Cells, TilesX, TilesY, heights: null);
        TerrainGroundTypes ground = Assert.IsType<TerrainGroundTypes>(TerrainGroundTypes.From(Types, corners, TilesX, TilesY, walkable));
        Assert.True(ground.Trusted, ground.Note);
        return (ground, walkable);
    }
}
