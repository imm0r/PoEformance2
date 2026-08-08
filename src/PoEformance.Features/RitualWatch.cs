using System.Numerics;
using System.Text;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// What the ritual line looks like right now. Immutable, published whole, drawn as-is.
/// </summary>
/// <param name="Drawing">Whether the atlas is in ritual-line mode at all.</param>
/// <param name="Started">Whether a line has already been begun.</param>
/// <param name="Length">How many maps the line takes in total.</param>
/// <param name="Chains">Every route it could take, best first.</param>
/// <param name="Where">Where each map is on screen, for whoever draws the route.</param>
/// <param name="Status">What happened, in words. Empty when there is nothing to say.</param>
public sealed record RitualView(
    bool Drawing,
    bool Started,
    int Length,
    IReadOnlyList<RitualChain> Chains,
    IReadOnlyDictionary<(int X, int Y), Vector2> Where,
    string Status)
{
    public static RitualView Idle { get; } = new(
        false, false, 0, [], new Dictionary<(int X, int Y), Vector2>(), string.Empty);

    /// <summary>Whether there is a route worth showing.</summary>
    public bool Anything => Chains.Count > 0;
}

/// <summary>
/// Reads the ritual line and works out where it could go.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="AtlasWatch"/> even though it rides the same tick, because it is a
/// different question with a different cost: the atlas is read whenever the panel is open, and
/// this reads nothing at all unless a line is being drawn - which is a few seconds of a session.
///
/// The plan is remembered until the LINE changes, not until the atlas does. Panning does not
/// change which route is worth taking, and re-enumerating a few thousand routes because somebody
/// dragged the map would be the one expensive thing in a tick that had no reason to be.
/// </remarks>
public sealed class RitualWatch
{
    private readonly RitualLineReader _reader;
    private readonly RitualMods _mods;
    private readonly AtlasMapNames _names;

    private RitualView _view = RitualView.Idle;
    private string _planned = string.Empty;
    private IReadOnlyList<RitualChain> _chains = [];

    public RitualWatch(RitualLineReader reader, RitualMods? mods = null, AtlasMapNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _mods = mods ?? RitualMods.Empty;
        _names = names ?? AtlasMapNames.Empty;
    }

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public RitualView View => Volatile.Read(ref _view);

    /// <summary>How many rewards the pool knows about. Zero means nothing can be predicted.</summary>
    public int KnownRewards => _mods.Count;

    /// <summary>Every reward that can roll, shortened - the list a filter chooses from.</summary>
    public IReadOnlyList<string> Rewards => RitualRewards.Everything(_mods);

    /// <summary>
    /// Reads the line and plans it, or publishes nothing when none is being drawn.
    /// </summary>
    /// <param name="panel">The atlas panel, or 0 when it is shut.</param>
    /// <param name="uiRoot">The interface root, for the game's own remaining-picks header.</param>
    /// <param name="nodes">The atlas as last studied - where the maps are and what they are.</param>
    /// <param name="worth">What each reward is worth to this player.</param>
    public void Service(
        ulong panel,
        ulong uiRoot,
        IReadOnlyList<AtlasNode> nodes,
        IReadOnlyDictionary<string, int>? worth)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        RitualLine line = _reader.Read(panel);
        if (!line.Active)
        {
            Forget();
            Volatile.Write(ref _view, RitualView.Idle);
            return;
        }

        if (_mods.Count == 0)
        {
            Forget();
            Volatile.Write(ref _view, RitualView.Idle with
            {
                Drawing = true,
                Status = "no reward list - data/ritual-mods.json is missing, so nothing can be predicted",
            });
            return;
        }

        // The game's own header wins when it can be read: the length worked out from stats is a
        // reconstruction, and this is the number on the screen.
        int length = line.Length;
        int left = _reader.PicksLeft(uiRoot);
        if (left > 0)
        {
            length = line.Committed.Count + left;
        }

        var state = new RitualLineState(line.LineId, line.Committed, line.Candidates, line.SecondRewardChance);

        // Re-planned when the LINE changes, not when the atlas moves. Panning does not change
        // which route is worth taking.
        string signature = Signature(line, length, nodes.Count);
        if (signature != _planned)
        {
            _planned = signature;
            _chains = RitualPlanner.Plan(
                state,
                Blocked(nodes, _names),
                Startable(nodes, _names),
                _mods.Available(line.Stats),
                length,
                worth);
        }

        Volatile.Write(ref _view, new RitualView(
            true,
            line.Started,
            length,
            _chains,
            Placed(nodes),
            _chains.Count > 0 ? string.Empty : "no route reaches the full length from here"));
    }

    /// <summary>Re-plans on the next tick even if the line has not changed.</summary>
    /// <remarks>
    /// For when the REWARDS change rather than the line: somebody weighting a reward differently
    /// wants the list re-sorted, and the line's signature says nothing about that.
    /// </remarks>
    public void Reconsider() => _planned = string.Empty;

    private void Forget()
    {
        _planned = string.Empty;
        _chains = [];
    }

    /// <summary>What state the plan was made for.</summary>
    /// <remarks>
    /// The node count is in it because the blocked set and the eligible starts come from the
    /// atlas: a map finishing changes both, and the line's own fields say nothing about it.
    /// </remarks>
    private static string Signature(RitualLine line, int length, int nodes)
    {
        var built = new StringBuilder();
        built.Append(line.LineId).Append('#').Append(length).Append('#').Append(nodes);
        foreach ((int X, int Y) at in line.Committed)
        {
            built.Append(';').Append(at.X).Append(',').Append(at.Y);
        }

        return built.ToString();
    }

    /// <summary>
    /// The maps a ritual line can never take.
    /// </summary>
    /// <remarks>
    /// The game's own rule, as the reference records it: a map already run, and the special
    /// categories - uniques, towers, hideouts. They still hold their place among the candidates
    /// (see <see cref="RitualPlanner"/>), they simply cannot be picked.
    ///
    /// Public so it can be tested. It is a rule of the game rather than an implementation
    /// detail, and getting it wrong plans routes the game will refuse to let anybody walk.
    /// </remarks>
    public static HashSet<(int X, int Y)> Blocked(IReadOnlyList<AtlasNode> nodes, AtlasMapNames names)
    {
        var blocked = new HashSet<(int X, int Y)>();
        foreach (AtlasNode node in nodes)
        {
            if (node.State == AtlasNodeState.Completed || Special(names.Of(node.MapId)))
            {
                blocked.Add(node.Grid);
            }
        }

        return blocked;
    }

    /// <summary>Whether a map is one of the categories a ritual line may never take.</summary>
    /// <remarks>
    /// The game tests a field on the map's own data row for this; the reference falls back to
    /// the same categories by tag, which is what is available here. Being wrong in this
    /// direction offers a route the game will refuse, which is worse than offering one fewer -
    /// so the list follows the reference exactly rather than being narrowed.
    /// </remarks>
    private static bool Special(AtlasMapInfo info)
        => info.Unique || info.Tagged("tower") || info.Tagged("hideout");

    /// <summary>The maps a line could start from: the ones that can be entered now.</summary>
    /// <remarks>
    /// Public for the same reason <see cref="Blocked"/> is: it encodes a rule of the game, and a
    /// rule that cannot be tested is one nobody notices going wrong.
    /// </remarks>
    public static List<(int X, int Y)> Startable(IReadOnlyList<AtlasNode> nodes, AtlasMapNames names)
    {
        var starts = new List<(int X, int Y)>();
        foreach (AtlasNode node in nodes)
        {
            if (node.State == AtlasNodeState.Open && !Special(names.Of(node.MapId)))
            {
                starts.Add(node.Grid);
            }
        }

        return starts;
    }

    /// <summary>Where each map is on screen, so a route can be drawn over the atlas.</summary>
    private static Dictionary<(int X, int Y), Vector2> Placed(IReadOnlyList<AtlasNode> nodes)
    {
        var placed = new Dictionary<(int X, int Y), Vector2>(nodes.Count);
        foreach (AtlasNode node in nodes)
        {
            placed[node.Grid] = node.Screen + (node.Size * 0.5f);
        }

        return placed;
    }
}
