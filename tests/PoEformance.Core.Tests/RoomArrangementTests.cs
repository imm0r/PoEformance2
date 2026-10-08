using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>Every room of an area at the best place its search found that no surer room holds.</summary>
public class RoomArrangementTests
{
    /// <summary>Two rooms ranking the same spot first: the surer keeps it, the other takes the next free place on its own list.</summary>
    [Fact]
    public void THESURERRoomKeepsASharedSpotAndTheOtherMovesDownItsList()
    {
        RoomLayout wide = Room(2, 1, "f 0 f 0");
        RoomSearch sure = Search(Place(0, 0, 2, 1));
        RoomSearch unsure = Search(Place(1, 0, 2, 1, missed: 1), Place(4, 0, 2, 1));

        RoomArrangement arranged = RoomArrangement.Arrange([("b.arm", wide, unsure), ("a.arm", wide, sure)], 10, 4);

        Assert.Equal(["a.arm", "b.arm"], arranged.Laid.Select(one => one.Room));
        Assert.Equal((0, 0, 0), (arranged.Laid[0].Where.X, arranged.Laid[0].Where.Y, arranged.Laid[0].Rank));
        Assert.Equal(100, arranged.Laid[0].Beside);
        Assert.Equal((4, 0, 1), (arranged.Laid[1].Where.X, arranged.Laid[1].Where.Y, arranged.Laid[1].Rank));
        Assert.Empty(arranged.Crowded);
        Assert.Empty(arranged.Unfound);
    }

    /// <summary>A slot the room leaves empty ("n") is no tile of its: another room may lie in its notch.</summary>
    [Fact]
    public void ANOTCHTheRoomLeavesEmptyHoldsAnotherRoom()
    {
        RoomLayout ell = Room(2, 2, "f 0 f 0", "f 0 n");
        RoomLayout one = Room(1, 1, "f 0");

        RoomArrangement arranged = RoomArrangement.Arrange(
            [("ell.arm", ell, Search(Place(0, 0, 2, 2, missed: 1))), ("one.arm", one, Search(Place(1, 1, 1, 1)))], 4, 4);

        Assert.Equal(["one.arm", "ell.arm"], arranged.Laid.Select(laid => laid.Room));
        Assert.All(arranged.Laid, laid => Assert.Equal(0, laid.Rank));
    }

    /// <summary>A room whose every place is taken is crowded out, and one whose search found nothing is unfound - neither drawn.</summary>
    [Fact]
    public void AROOMWithNoFreePlaceIsCrowdedAndOneNotFoundIsSaidSo()
    {
        RoomLayout wide = Room(2, 1, "f 0 f 0");
        RoomArrangement arranged = RoomArrangement.Arrange(
            [
                ("a.arm", wide, Search(Place(0, 0, 2, 1))),
                ("b.arm", wide, Search(Place(1, 0, 2, 1, missed: 1))),
                ("c.arm", wide, RoomSearch.Not("the room did not read")),
            ],
            10,
            4);

        Assert.Equal(["a.arm"], arranged.Laid.Select(one => one.Room));
        Assert.Equal(["b.arm"], arranged.Crowded);
        Assert.Equal(["c.arm"], arranged.Unfound);
    }

    /// <summary>A version 36 room of the given size whose grid is the given lines - only which slots are "n" matters here.</summary>
    private static RoomLayout Room(int width, int height, params string[] lines)
    {
        string text = string.Join('\n', [
            "version 36", "0", "5 3", "0", "\"roomtag\"", "0",
            $"k {width} {height} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0",
            "-1", "-1", "-1", "-1", "-1", "-1", "\"\"",
            .. lines,
            "-1", "-1", "-1", "0", "-1", "0"]);
        RoomLayout room = RoomLayout.Parse(text);
        Assert.True(room.Ready, room.Why);
        Assert.Equal((width, height), (room.Width, room.Height));
        return room;
    }

    /// <summary>A place as written, every corner and tile agreeing but <paramref name="missed"/> corners no join explains.</summary>
    private static (RoomCandidate Where, RoomMisses Misses) Place(int x, int y, int width, int height, int missed = 0)
        => (new RoomCandidate(x, y, 0, width, height, 10 - missed, 10) { Tiles = 4, TilesAgree = 4 },
            new RoomMisses([.. Enumerable.Range(0, missed).Select(one => (one, 0))], []) { Classified = true });

    private static RoomSearch Search(params (RoomCandidate Where, RoomMisses Misses)[] places)
        => new([.. places.Select(one => one.Where)], 10, 0, 0, string.Empty) { Misses = [.. places.Select(one => one.Misses)] };
}
