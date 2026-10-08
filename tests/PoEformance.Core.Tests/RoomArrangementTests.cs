using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>Every room of an area at the best place its search found that no surer room holds.</summary>
public class RoomArrangementTests
{
    /// <summary>Two rooms ranking the same spot first: the surer keeps it, the other takes the next free place on its own list - with no tile shared.</summary>
    [Fact]
    public void THESURERRoomKeepsASharedSpotAndTheOtherMovesDownItsList()
    {
        RoomLayout wide = Room(2, 1, "f 0 f 0");
        RoomSearch sure = Search(Place(0, 0, 2, 1));
        RoomSearch unsure = Search(Place(1, 0, 2, 1, missed: 1), Place(4, 0, 2, 1));

        RoomArrangement arranged = RoomArrangement.Arrange([("b.arm", wide, unsure), ("a.arm", wide, sure)], 10, 4, RoomOverlap.None);

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
            [("ell.arm", ell, Search(Place(0, 0, 2, 2, missed: 1))), ("one.arm", one, Search(Place(1, 1, 1, 1)))], 4, 4, RoomOverlap.None);

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
            4,
            RoomOverlap.None);

        Assert.Equal(["a.arm"], arranged.Laid.Select(one => one.Room));
        Assert.Equal(["b.arm"], arranged.Crowded);
        Assert.Equal(["c.arm"], arranged.Unfound);
    }

    /// <summary>Two rooms whose rims lie on one column, as rooms that join do: both keep their first place with rims shared, and the less sure moves under the first rule.</summary>
    [Fact]
    public void ROOMSLayingTheirRimsOnOneColumnBothKeepTheirFirstPlaceWithRimsShared()
    {
        RoomLayout square = Square();
        RoomSearch boss = Search(Place(0, 0, 3, 3));
        RoomSearch offices = Search(Place(2, 0, 3, 3, missed: 1), Place(6, 0, 3, 3));
        (string, RoomLayout, RoomSearch)[] rooms = [("boss.arm", square, boss), ("offices.arm", square, offices)];

        RoomArrangement shared = RoomArrangement.Arrange(rooms, 10, 4, RoomOverlap.Rims);
        Assert.Equal([("boss.arm", 0), ("offices.arm", 0)], shared.Laid.Select(one => (one.Room, one.Rank)));
        Assert.Equal(RoomOverlap.Rims, shared.Rule);

        RoomArrangement apart = RoomArrangement.Arrange(rooms, 10, 4, RoomOverlap.None);
        Assert.Equal([("boss.arm", 0), ("offices.arm", 1)], apart.Laid.Select(one => (one.Room, one.Rank)));
    }

    /// <summary>A rim laid over another room's inside is no join: with rims shared the room still moves down its list.</summary>
    [Fact]
    public void ARIMOverAnotherRoomsInsideStillMovesTheRoom()
    {
        RoomLayout square = Square();
        RoomArrangement arranged = RoomArrangement.Arrange(
            [("boss.arm", square, Search(Place(0, 0, 3, 3))), ("offices.arm", square, Search(Place(1, 0, 3, 3, missed: 1), Place(6, 0, 3, 3)))],
            10,
            4,
            RoomOverlap.Rims);

        Assert.Equal([("boss.arm", 0), ("offices.arm", 1)], arranged.Laid.Select(one => (one.Room, one.Rank)));
    }

    /// <summary>A placement held against the rooms laid: tiles on both rims, tiles inside either, and tiles beside - the three ways two rooms can meet.</summary>
    [Fact]
    public void SHARINGCountsRimOnRimInsideAndTouchingApart()
    {
        RoomLayout square = Square();
        RoomArrangement arranged = RoomArrangement.Arrange([("boss.arm", square, Search(Place(0, 0, 3, 3)))], 10, 4, RoomOverlap.Rims);
        Assert.True(arranged.Layouts.ContainsKey("BOSS.ARM"));

        static RoomCandidate At(int x) => new(x, 0, 0, 3, 3, 10, 10);

        // One column both rims lie on, and the column past it beside the boss's rim.
        RoomShared rims = Assert.Single(arranged.Sharing(square, At(2)));
        Assert.Equal(("boss.arm", 3, 0, 3), (rims.Laid.Room, rims.Rims, rims.Elsewhere, rims.Touching));

        // Side by side: nothing shared, one column touching.
        RoomShared beside = Assert.Single(arranged.Sharing(square, At(3)));
        Assert.Equal((0, 0, 3), (beside.Rims, beside.Elsewhere, beside.Touching));

        // Two columns over: the shared stretch of the top and bottom rows is on both rims, the middle row
        // is inside one room or the other - and that is what keeps such a place out under the rule.
        RoomShared over = Assert.Single(arranged.Sharing(square, At(1)));
        Assert.Equal((4, 2, 3), (over.Rims, over.Elsewhere, over.Touching));

        // Far off, and the room itself as arranged, are left out.
        Assert.Empty(arranged.Sharing(square, At(6)));
        Assert.Empty(arranged.Sharing(square, At(2), except: "Boss.arm"));
    }

    /// <summary>A three-by-three room of fill slots, every slot a tile.</summary>
    internal static RoomLayout Square() => Room(3, 3, "f 0 f 0 f 0", "f 0 f 0 f 0", "f 0 f 0 f 0");

    /// <summary>A version 36 room of the given size whose grid is the given lines - only which slots are "n" matters here.</summary>
    internal static RoomLayout Room(int width, int height, params string[] lines)
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
    internal static (RoomCandidate Where, RoomMisses Misses) Place(int x, int y, int width, int height, int missed = 0)
        => (new RoomCandidate(x, y, 0, width, height, 10 - missed, 10) { Tiles = 4, TilesAgree = 4 },
            new RoomMisses([.. Enumerable.Range(0, missed).Select(one => (one, 0))], []) { Classified = true });

    internal static RoomSearch Search(params (RoomCandidate Where, RoomMisses Misses)[] places)
        => new([.. places.Select(one => one.Where)], 10, 0, 0, string.Empty) { Misses = [.. places.Select(one => one.Misses)] };
}
