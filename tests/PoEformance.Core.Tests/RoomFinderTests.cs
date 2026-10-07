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
/// blank corners, which nothing else in the area has, so exactly one place and one way round can
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

    private static readonly string[] Types = [string.Empty, WallType, FloorType];

    /// <summary>The room's corner types, [u, v] for a three by two room: 0 blank, 1 wall, 2 floor - no symmetry.</summary>
    private static readonly int[,] Pattern =
    {
        { 1, 0, 1 },
        { 0, 2, 1 },
        { 2, 2, 0 },
        { 1, 0, 2 },
    };

    [Fact]
    public void AROOMSGridReadsEverySlotAndItsCornersByTheFilesOwnConvention()
    {
        RoomLayout room = RoomLayout.Parse(Room(Pattern, 3, 2));

        Assert.True(room.Ready, room.Why);
        Assert.Equal(string.Empty, room.SlotsWhy);
        Assert.Equal(6, room.Slots.Count);
        Assert.Equal([WallType, FloorType], room.Strings);

        // The slot at column 1, line 0: its down-left corner is the grid corner (1, 0), its up-right (2, 1).
        RoomSlot slot = room.SlotAt(1, 0);
        Assert.True(slot.IsTile);
        Assert.Equal((1, 1), (slot.Width, slot.Height));
        Assert.Equal(string.Empty, room.Named(slot.Ground(0)));
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
        Assert.Contains("no one by one k slot", search.Why, StringComparison.Ordinal);
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

    /// <summary>A version 36 room of one by one k slots whose corners carry the pattern, by the down-left, down-right, up-right, up-left order.</summary>
    private static string Room(int[,] pattern, int width, int height)
    {
        var text = new StringBuilder();
        text.AppendLine("version 36").AppendLine("2")
            .AppendLine(CultureInfo.InvariantCulture, $"\"{WallType}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"\"{FloorType}\"")
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
    private static (TerrainGroundTypes Ground, TerrainGrid Walkable) Area(bool withRoom, bool spoil = false)
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
