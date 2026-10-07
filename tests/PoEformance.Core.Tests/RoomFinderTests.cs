using System.Globalization;
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
        Assert.Equal(2, search.Candidates.Count);
        Assert.True(search.Candidates.All(one => one.Exact));
        RoomCandidate first = search.Candidates[0];
        Assert.Equal((12, 4, 5), (first.X, first.Y, first.Turn));
        Assert.Equal((6, 6), (first.TilesAgree, first.Tiles));
        Assert.Equal((16, 1, 0), (search.Candidates[1].X, search.Candidates[1].Y, search.Candidates[1].Turn));
        Assert.Equal((0, 6), (search.Candidates[1].TilesAgree, search.Candidates[1].Tiles));

        // The map's orange rings: every tile of the decoy, none of the room.
        Assert.Empty(search.Misses[0].Tiles);
        Assert.Equal(6, search.Misses[1].Tiles.Count);
        Assert.Contains((16, 1), search.Misses[1].Tiles);
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
        RoomCandidate found = Assert.Single(search.Candidates);
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
    private static (TerrainGroundTypes Ground, TerrainGrid Walkable) Area(bool withRoom, bool spoil = false, bool decoy = false)
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
