using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding the way across the atlas from wherever you can currently go.
/// </summary>
/// <remarks>
/// Pure graph work on a list of nodes, so all of it is checkable without a game - which is the
/// half of the atlas port that can be finished and trusted while the offsets underneath it are
/// still guesses.
/// </remarks>
public class AtlasRouteTests
{
    private static AtlasNode Node(
        int x, int y, AtlasNodeState state, string mapId = "MapSomething", params (int X, int Y)[] joined)
        => new(
            Index: 0,
            Address: 0x1000,
            MapId: mapId,
            Grid: (x, y),
            State: state,
            Biome: 0,
            Connections: joined,
            Screen: Vector2.Zero,
            Size: Vector2.Zero,
            BadgeIds: [],
            ContentTokens: []);

    /// <summary>A line of four maps, only the first of which can be entered.</summary>
    private static List<AtlasNode> Line() =>
    [
        Node(0, 0, AtlasNodeState.Open, "MapA", (1, 0)),
        Node(1, 0, AtlasNodeState.Locked, "MapB", (0, 0), (2, 0)),
        Node(2, 0, AtlasNodeState.Locked, "MapC", (1, 0), (3, 0)),
        Node(3, 0, AtlasNodeState.Locked, "MapD", (2, 0)),
    ];

    [Fact]
    public void AMapYouCanAlreadyEnterIsNoDistanceAway()
    {
        // A route of one, not nothing. "No route" and "no distance" are different answers, and
        // a caller told the second as the first would hide every map already open.
        AtlasRoutes routes = AtlasRoutes.From(Line());

        Assert.Equal(new[] { (0, 0) }, routes.To((0, 0)));
        Assert.Equal(0, routes.Hops((0, 0)));
    }

    [Fact]
    public void ANDEverythingElseIsCountedFromThere()
    {
        AtlasRoutes routes = AtlasRoutes.From(Line());

        Assert.Equal(new[] { (0, 0), (1, 0), (2, 0), (3, 0) }, routes.To((3, 0)));
        Assert.Equal(3, routes.Hops((3, 0)));
    }

    [Fact]
    public void AMapWithNoLineToItIsUnreachableRatherThanFarAway()
    {
        List<AtlasNode> nodes = Line();
        nodes.Add(Node(9, 9, AtlasNodeState.Locked, "MapIsland"));

        AtlasRoutes routes = AtlasRoutes.From(nodes);

        Assert.Null(routes.To((9, 9)));
        Assert.Equal(-1, routes.Hops((9, 9)));
    }

    [Fact]
    public void THEShortestOfTwoWaysIsTheOneReported()
    {
        // A diamond: the long way round is three hops, the short way two.
        List<AtlasNode> nodes =
        [
            Node(0, 0, AtlasNodeState.Open, "MapStart", (1, 0), (0, 1)),
            Node(1, 0, AtlasNodeState.Locked, "MapShort", (0, 0), (1, 1)),
            Node(0, 1, AtlasNodeState.Locked, "MapLongA", (0, 0), (0, 2)),
            Node(0, 2, AtlasNodeState.Locked, "MapLongB", (0, 1), (1, 1)),
            Node(1, 1, AtlasNodeState.Locked, "MapEnd", (1, 0), (0, 2)),
        ];

        Assert.Equal(2, AtlasRoutes.From(nodes).Hops((1, 1)));
    }

    [Fact]
    public void SEVERALOpenMapsAreAllStartingPoints()
    {
        // The point of searching from all of them at once: the answer for a node is its
        // distance from the NEAREST way in, not from an arbitrary one.
        List<AtlasNode> nodes = Line();
        nodes[3] = Node(3, 0, AtlasNodeState.Open, "MapD", (2, 0));

        AtlasRoutes routes = AtlasRoutes.From(nodes);

        Assert.Equal(2, routes.Open);
        Assert.Equal(1, routes.Hops((2, 0)));   // from MapD, not three from MapA
    }

    [Fact]
    public void AMapThatCannotBeTraversedIsStillADestination()
    {
        // It is reachable as an end and never a step in somebody else's route - which is why
        // the no-pass check happens when a node is taken off the queue, not when it goes on.
        string blocker = AtlasRoutes.NeverThrough[0];
        List<AtlasNode> nodes =
        [
            Node(0, 0, AtlasNodeState.Open, "MapA", (1, 0)),
            Node(1, 0, AtlasNodeState.Locked, blocker, (0, 0), (2, 0)),
            Node(2, 0, AtlasNodeState.Locked, "MapC", (1, 0)),
        ];

        AtlasRoutes routes = AtlasRoutes.From(nodes);

        Assert.Equal(1, routes.Hops((1, 0)));   // you can go there
        Assert.Null(routes.To((2, 0)));         // but nothing routes through it
    }

    [Fact]
    public void ANDItIsNotOfferedAsAStartingPointEither()
    {
        string blocker = AtlasRoutes.NeverThrough[0];
        List<AtlasNode> nodes =
        [
            Node(0, 0, AtlasNodeState.Open, blocker, (1, 0)),
            Node(1, 0, AtlasNodeState.Locked, "MapB", (0, 0)),
        ];

        AtlasRoutes routes = AtlasRoutes.From(nodes);

        Assert.Equal(0, routes.Open);
        Assert.Null(routes.To((1, 0)));
    }

    [Fact]
    public void ACLOSEDAtlasRoutesNothingAndSaysSo()
    {
        AtlasRoutes routes = AtlasRoutes.From([]);

        Assert.Equal(0, routes.Open);
        Assert.Equal(0, routes.Reachable);
        Assert.Null(routes.To((0, 0)));
    }
}
