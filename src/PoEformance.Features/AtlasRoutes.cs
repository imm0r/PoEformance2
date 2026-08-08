using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>
/// The way from where you can go to where you want to go, across the atlas.
/// </summary>
/// <remarks>
/// The question this answers is the one the atlas itself will not: a map three hops away is
/// worth running for, and the screen shows a web of lines with no indication of which of them
/// leads anywhere. So the maps you can enter right now are the sources, and everything else is
/// measured from all of them at once.
///
/// ONE SEARCH FOR EVERY TARGET, not one per target. A breadth-first walk out of every open map
/// at the same time reaches each node by its shortest route, so hovering forty maps in turn
/// costs the same as hovering one. That is why this is a tree built once and read many times
/// rather than a path-finder called per node.
///
/// Ported from GameHelper2's Atlas2, including the rule that sounds like a bug and is not: a
/// node can be a destination without being a corridor. Some maps cannot be traversed, so they
/// are reachable as an END but never expanded through, and a route drawn THROUGH one would be
/// a route nobody can walk.
/// </remarks>
public sealed class AtlasRoutes
{
    /// <summary>
    /// Maps that are a destination but never a way through.
    /// </summary>
    /// <remarks>
    /// The reference carries exactly one, and by its own id rather than its name, because the
    /// name is whatever the client's language calls it. Kept in code rather than in the data
    /// file for the same reason the biome numbers are not there: this is a rule about how the
    /// atlas connects, not a label somebody might want to reword.
    /// </remarks>
    public static IReadOnlyList<string> NeverThrough { get; } = ["MapUniqueReactor_04"];

    private readonly Dictionary<(int X, int Y), (int X, int Y)> _cameFrom;
    private readonly HashSet<(int X, int Y)> _open;

    private AtlasRoutes(Dictionary<(int X, int Y), (int X, int Y)> cameFrom, HashSet<(int X, int Y)> open)
    {
        _cameFrom = cameFrom;
        _open = open;
    }

    /// <summary>Nothing routed - what a closed atlas leaves.</summary>
    public static AtlasRoutes None { get; } = new([], []);

    /// <summary>How many maps can be entered right now.</summary>
    public int Open => _open.Count;

    /// <summary>How many can be reached at all, the open ones included.</summary>
    public int Reachable => _open.Count + _cameFrom.Count;

    /// <summary>
    /// Works out every route across an atlas, from the maps that can be entered now.
    /// </summary>
    /// <remarks>
    /// The graph is taken from the nodes' own connections rather than from their positions.
    /// Adjacency on the screen is not adjacency on the atlas - lines cross, and a neighbour
    /// that looks next door may have no line to you at all.
    /// </remarks>
    public static AtlasRoutes From(IReadOnlyList<AtlasNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var graph = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>();
        var open = new HashSet<(int X, int Y)>();
        var noPass = new HashSet<(int X, int Y)>();

        foreach (AtlasNode node in nodes)
        {
            graph[node.Grid] = node.Connections;

            if (NeverThrough.Contains(node.MapId, StringComparer.OrdinalIgnoreCase))
            {
                noPass.Add(node.Grid);
            }
            else if (node.State == AtlasNodeState.Open)
            {
                open.Add(node.Grid);
            }
        }

        return new AtlasRoutes(Search(graph, open, noPass), open);
    }

    /// <summary>
    /// The route to a map, nearest open map first, or null when there is no way there.
    /// </summary>
    /// <remarks>
    /// A map you are standing next to comes back as a route of one - itself - rather than as
    /// nothing, because "no route" and "no distance" are different answers and a caller
    /// drawing the first as the second would quietly hide the maps that are already open.
    /// </remarks>
    public IReadOnlyList<(int X, int Y)>? To((int X, int Y) target)
    {
        if (_open.Contains(target))
        {
            return [target];
        }

        if (!_cameFrom.ContainsKey(target))
        {
            return null;
        }

        var route = new List<(int X, int Y)> { target };
        (int X, int Y) at = target;
        while (_cameFrom.TryGetValue(at, out (int X, int Y) previous))
        {
            at = previous;
            route.Add(at);
        }

        route.Reverse();
        return route;
    }

    /// <summary>How many maps have to be run to get there, or -1 when it cannot be reached.</summary>
    public int Hops((int X, int Y) target) => To(target) is { } route ? route.Count - 1 : -1;

    /// <summary>
    /// Walks out of every open map at once, remembering how each node was first reached.
    /// </summary>
    /// <remarks>
    /// Breadth first, so the first time a node is seen is by its shortest route and the entry
    /// never needs replacing. The no-pass set is checked when a node is TAKEN OFF the queue
    /// rather than when it is put on: that is what lets such a map be an answer without ever
    /// being a step in someone else's.
    /// </remarks>
    private static Dictionary<(int X, int Y), (int X, int Y)> Search(
        Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> graph,
        HashSet<(int X, int Y)> sources,
        HashSet<(int X, int Y)> noPass)
    {
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var seen = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();

        foreach ((int X, int Y) source in sources)
        {
            if (graph.ContainsKey(source) && !noPass.Contains(source) && seen.Add(source))
            {
                queue.Enqueue(source);
            }
        }

        while (queue.Count > 0)
        {
            (int X, int Y) at = queue.Dequeue();
            if (noPass.Contains(at) || !graph.TryGetValue(at, out IReadOnlyList<(int X, int Y)>? joined))
            {
                continue;
            }

            foreach ((int X, int Y) next in joined)
            {
                if (seen.Add(next))
                {
                    cameFrom[next] = at;
                    queue.Enqueue(next);
                }
            }
        }

        return cameFrom;
    }
}
