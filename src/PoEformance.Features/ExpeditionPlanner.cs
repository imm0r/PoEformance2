using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace PoEformance.Features;

/// <summary>What an expedition entity is to the planner.</summary>
public enum ExpeditionKind
{
    Detonator,
    Charge,
    Chest,
    Marker,
    Remnant,
    Monolith,
    Sentinel,
    Prop,
    Gate,
}

/// <summary>One thing the route can collect, as the planner sees it.</summary>
/// <param name="Id">The entity's id - what a monolith is joined back to the chain plan by.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Grid">Where it stands, in grid cells.</param>
/// <param name="Z">The ground under it, for the map.</param>
/// <param name="Weight">Its routing value in Exalted - the joint reward-plus-rune figure for a monolith.</param>
/// <param name="Primary">An ANCHOR the tour goes to, rather than a pickup taken when a blast covers it.</param>
/// <param name="Sentinel">The Kalguur Sentinel, pinned first in the tour.</param>
/// <param name="Waves">Packs a monolith will spawn; 0 for anything else.</param>
/// <param name="Uplift">The strongest loot uplift a monolith can propagate, order-independent; 0 for anything else.</param>
/// <param name="RuneId">That rune's row, for duplicate suppression along an order; -1 for none.</param>
/// <param name="Reward">A monolith's recipe price alone - what the min-ex gate would have read before the chain inflated it.</param>
public sealed record PlanTarget(
    uint Id,
    ExpeditionKind Kind,
    Vector2 Grid,
    float Z,
    double Weight,
    bool Primary,
    bool Sentinel = false,
    int Waves = 0,
    double Uplift = 0,
    int RuneId = -1,
    double Reward = 0);

/// <summary>An exploding prop: free area when a charge's blast reaches it.</summary>
public readonly record struct PlanProp(Vector2 Grid, float Radius);

/// <summary>Everything a plan needs, snapshotted so the background task never touches live state.</summary>
public sealed class PlanInputs
{
    public required ExpeditionPaths Paths { get; init; }

    public bool HasDetonator { get; init; }

    public Vector2 Detonator { get; init; }

    public float DetonatorZ { get; init; }

    /// <summary>Charges to plan with - the map's total.</summary>
    public int Budget { get; init; }

    /// <summary>One hop's reach, in cells, map modifier applied.</summary>
    public float EffDist { get; init; }

    /// <summary>One blast's radius, in cells, map modifier applied.</summary>
    public float EffRadius { get; init; }

    /// <summary>The reach a stepping stone is laid at: a cell of slack under the hop, since the smoothed path can under-measure the game's.</summary>
    public float StepDist { get; init; }

    public IReadOnlyList<PlanTarget> Targets { get; init; } = [];

    public IReadOnlyList<PlanProp> Props { get; init; } = [];

    /// <summary>
    /// Points no blast may reach - the relics carrying a mod the player will not have. A hard
    /// rule rather than a negative weight: a charge that would set one off is not a worse
    /// candidate, it is no candidate, so an anchor only takeable by hitting one is skipped.
    /// </summary>
    public IReadOnlyList<Vector2> Shunned { get; init; } = [];

    /// <summary>Normal expeditions: a spare charge needs a primary or a marker cluster. Off on Grand.</summary>
    public bool MarkerCoverageMode { get; init; }

    public int MinMarkers { get; init; } = 1;

    /// <summary>Whether the rune chain re-scores the tour order.</summary>
    public bool ChainOrder { get; init; }

    public float ChainBaseEx { get; init; }

    /// <summary>The decision trace, or null for none.</summary>
    public List<string>? Trace { get; init; }
}

/// <summary>One charge of the plan.</summary>
/// <param name="Grid">Where to put it.</param>
/// <param name="Z">The ground there, for the map.</param>
/// <param name="Marginal">Exalted this charge newly captures.</param>
/// <param name="Captured">Targets it newly captures.</param>
/// <param name="Note">Why it landed here, in words.</param>
/// <param name="Bridge">A stepping stone toward an anchor, capturing nothing on its own.</param>
/// <param name="Sentinel">The charge that sets the Sentinel off - kept early in the chain.</param>
public sealed record RoutePoint(Vector2 Grid, float Z, double Marginal, int Captured, string Note, bool Bridge, bool Sentinel = false);

/// <summary>What a plan came to.</summary>
public sealed class PlanResult
{
    public static PlanResult Empty { get; } = new();

    public IReadOnlyList<RoutePoint> Route { get; init; } = [];

    public double Weight { get; init; }

    public int Covered { get; init; }

    public int Targets { get; init; }

    public int Anchors { get; init; }

    public int AnchorsCovered { get; init; }

    /// <summary>The line the player walks: detonator to anchors, every cell.</summary>
    public IReadOnlyList<Vector2> SpinePoints { get; init; } = [];

    public IReadOnlyList<float> SpineHeights { get; init; } = [];

    /// <summary>Where in the spine each ordered anchor sits.</summary>
    public IReadOnlyList<int> SpineAnchorIndex { get; init; } = [];

    /// <summary>The monoliths' entity ids in detonation order - what the rune chain plan takes.</summary>
    public IReadOnlyList<uint> MonolithOrder { get; init; } = [];

    public IReadOnlyList<string> Trace { get; init; } = [];

    public double ComputeMs { get; init; }

    public long Searches { get; init; }

    public long Hits { get; init; }

    public string Phase { get; init; } = string.Empty;

    /// <summary>Shunned relics the final route sets off anyway. Zero by construction; counted so a bug shows.</summary>
    public int ShunnedHit { get; init; }

    /// <summary>Whether a plan was made at all.</summary>
    public bool Any => Route.Count > 0;
}

/// <summary>
/// Lays the explosive chain: which anchors, in what order, where each charge goes.
/// </summary>
/// <remarks>
/// PORTED FROM yokkenUA's RunecraftHelper's spine planner, and the shape is what makes it
/// worth porting - it builds the route the way a player does rather than chasing value:
///
/// 1. THE ROUTER. Every anchor (a monolith worth the detour, a beneficial relic, the Sentinel)
///    is ordered into a short open tour from the detonator - nearest-neighbour seeded, 2-opt
///    improved, over WALKABLE distances - then re-scored by what the rune chain propagates
///    along that order (<see cref="ChainReorder"/>), and turned into one continuous walkable
///    polyline, rasterised to four cells, that the player follows.
/// 2. THE PLACER. A forward sweep along that line: each anchor is covered by the charge whose
///    blast catches the most uncaptured weight, ties to the FURTHEST-FORWARD cell - so the
///    anchor sits at the blast's forward edge and the chain head advances further per charge,
///    which is the habit of a player who places past the monolith rather than on it. Reach
///    between charges is the real walkable distance, never the polyline's arc, because the
///    line doubles back at a spur anchor and the arc would wildly overestimate. An anchor out
///    of one hop gets a BRIDGE as far toward it as a hop allows, placed where a bridge grabs
///    something if a cell within a radius behind the furthest one does.
/// 3. THE SPARE OPTIMISER. What is left of the budget goes to marker clusters by value per
///    charge - counting the bridges out AND the bridges back, since explosives form one rope
///    from the detonator and a detour must rejoin the next charge or the blast dies there -
///    spliced into the route where it flows rather than hung off the nearest node, and rolled
///    back whole when it cannot complete within the budget.
///
/// Every number the reference measured is kept with the reason it measured it, and the trace
/// says why each charge landed where it did, because "the plan walked past the Opulent
/// monolith" is the kind of report that otherwise costs a screenshot and a guess.
/// </remarks>
public static class ExpeditionPlanner
{
    /// <summary>Spine resolution: the sparse smoothed path rasterised to this spacing, far under a blast radius.</summary>
    public const float SpineStep = 4f;

    /// <summary>
    /// What one extra spine charge costs, in Exalted, when the chain re-orders the tour.
    /// </summary>
    /// <remarks>
    /// Measured by the reference off its own spare optimiser's log: the cheapest cluster still
    /// worth a charge runs 2-4 ex on a Grand map, a rich one 25-46. Eight means "spend a walking
    /// charge only when the rune gain clearly beats a mediocre cluster", while a genuinely good
    /// cluster still outbids a marginal reorder.
    /// </remarks>
    public const double ChargeOpportunityEx = 8.0;

    /// <summary>Plans the chain.</summary>
    public static PlanResult Plan(PlanInputs inp)
    {
        ArgumentNullException.ThrowIfNull(inp);
        var clock = Stopwatch.StartNew();
        PlanResult result = Spine(inp);
        clock.Stop();
        inp.Trace?.Add($"=== TIMING === {clock.Elapsed.TotalMilliseconds:F0} ms · {inp.Paths.Searches} searches, {inp.Paths.Hits} from the memo");
        return new PlanResult
        {
            Route = result.Route,
            Weight = result.Weight,
            Covered = result.Covered,
            Targets = result.Targets,
            Anchors = result.Anchors,
            AnchorsCovered = result.AnchorsCovered,
            SpinePoints = result.SpinePoints,
            SpineHeights = result.SpineHeights,
            SpineAnchorIndex = result.SpineAnchorIndex,
            MonolithOrder = result.MonolithOrder,
            Trace = inp.Trace ?? [],
            ComputeMs = clock.Elapsed.TotalMilliseconds,
            Searches = inp.Paths.Searches,
            Hits = inp.Paths.Hits,
            Phase = result.Phase,
            ShunnedHit = result.ShunnedHit,
        };
    }

    private static void Log(PlanInputs inp, string line) => inp.Trace?.Add(line);

    private static string F0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    private static string At(Vector2 p) => $"({F0(p.X)},{F0(p.Y)})";

    private static float DistSq(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Which props a charge here sets off: only those inside its own blast.
    /// </summary>
    /// <remarks>
    /// THERE IS NO CASCADE. A prop is set off by our explosion and by nothing else - its own
    /// blast takes everything in its radius except another prop, so two props inside each
    /// other's radius still need two charges (confirmed in play, two Oil Derricks in Stagnant
    /// Basin). The reference first wrote this as a cascading flood, which over-credited coverage,
    /// and that is exactly the error that makes a plan promise more than the dig delivers.
    /// </remarks>
    private static List<int>? Triggered(PlanInputs inp, Vector2 pos, float r2)
    {
        IReadOnlyList<PlanProp> props = inp.Props;
        if (props.Count == 0)
        {
            return null;
        }

        List<int>? fired = null;
        for (int i = 0; i < props.Count; i++)
        {
            if (DistSq(pos, props[i].Grid) <= r2)
            {
                (fired ??= []).Add(i);
            }
        }

        return fired;
    }

    private static bool PropCovers(PlanInputs inp, List<int>? fired, Vector2 target)
    {
        if (fired is null)
        {
            return false;
        }

        foreach (int k in fired)
        {
            float r = inp.Props[k].Radius;
            if (DistSq(inp.Props[k].Grid, target) <= r * r)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covers(PlanInputs inp, List<int>? fired, Vector2 pos, Vector2 target, float r2)
        => DistSq(pos, target) <= r2 || PropCovers(inp, fired, target);

    /// <summary>Whether a charge here would set off a shunned relic - by its own blast or a prop's.</summary>
    private static bool Forbidden(PlanInputs inp, List<int>? fired, Vector2 pos, float r2)
    {
        foreach (Vector2 shunned in inp.Shunned)
        {
            if (Covers(inp, fired, pos, shunned, r2))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Forbidden(PlanInputs inp, Vector2 pos, float r2)
        => inp.Shunned.Count > 0 && Forbidden(inp, Triggered(inp, pos, r2), pos, r2);

    /// <summary>Uncaptured weight a charge here collects: its blast, plus the blasts of the props it sets off.</summary>
    private static double CoverGain(PlanInputs inp, bool[] captured, Vector2 p, float r2, out int count)
    {
        IReadOnlyList<PlanTarget> targets = inp.Targets;
        List<int>? fired = Triggered(inp, p, r2);
        double gain = 0;
        count = 0;
        for (int u = 0; u < targets.Count; u++)
        {
            if (!captured[u] && Covers(inp, fired, p, targets[u].Grid, r2))
            {
                gain += targets[u].Weight;
                count++;
            }
        }

        return gain;
    }

    private static int CountUncovered(PlanInputs inp, bool[] captured, Vector2 pos, float r2)
    {
        List<int>? fired = Triggered(inp, pos, r2);
        var count = 0;
        for (int u = 0; u < inp.Targets.Count; u++)
        {
            if (!captured[u] && Covers(inp, fired, pos, inp.Targets[u].Grid, r2))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Captures everything a charge here covers and appends the route point.</summary>
    private static double Commit(
        PlanInputs inp, bool[] captured, List<RoutePoint> route, Vector2 pos, float z, float r2,
        bool bridge, float reach, string note, bool sentinel = false)
    {
        List<int>? fired = Triggered(inp, pos, r2);
        double gain = 0;
        var count = 0;
        var viaProp = 0;
        for (int u = 0; u < inp.Targets.Count; u++)
        {
            if (captured[u])
            {
                continue;
            }

            bool direct = DistSq(pos, inp.Targets[u].Grid) <= r2;
            if (!direct && !PropCovers(inp, fired, inp.Targets[u].Grid))
            {
                continue;
            }

            if (!direct)
            {
                viaProp++;
            }

            captured[u] = true;
            count++;
            gain += inp.Targets[u].Weight;
        }

        // A capture outside our own radius is the one the player cannot check by eye, so a
        // placement that looks wrong should say why it is not.
        string props = fired is not null
            ? $" · sets off {fired.Count} prop(s)" + (viaProp > 0 ? $", +{viaProp} via their blast" : string.Empty)
            : string.Empty;
        route.Add(new RoutePoint(
            pos, z, gain, count,
            $"{(bridge ? "bridge" : "cover")} {count} · {F0(gain)} ex · reach {F0(reach)}/{F0(inp.EffDist)}{note}{props}",
            bridge, sentinel));
        return gain;
    }

    /// <summary>
    /// Slides a harvest charge off the bare cluster marker to the reachable point catching the
    /// most uncaptured weight, so a strong flag just outside the cluster's radius joins the blast.
    /// </summary>
    /// <remarks>
    /// Candidates: the weighted centroid of the uncaptured targets within two radii, the
    /// midpoint from the cluster to each, and the midpoint of every PAIR of them - the covering
    /// centre for two outliers each just outside the other's radius, which no cluster-based
    /// candidate reaches because it is centred between two flags neither of which is the
    /// marker the charge sat on. Never lowers coverage, so it is a strict free upgrade.
    /// </remarks>
    private static Vector2 MaxCoverPoint(
        PlanInputs inp, bool[] captured, Vector2 node, Vector2 c0, float r2, out float reach)
    {
        ExpeditionPaths paths = inp.Paths;
        IReadOnlyList<PlanTarget> targets = inp.Targets;
        float wide = 4f * r2;

        Vector2 best = c0;
        double bestGain = CoverGain(inp, captured, c0, r2, out _);
        reach = paths.Reach(node, c0);

        double sumW = 0;
        float cx = 0f;
        float cy = 0f;
        var near = new List<int>();
        for (int u = 0; u < targets.Count; u++)
        {
            if (captured[u] || DistSq(c0, targets[u].Grid) > wide)
            {
                continue;
            }

            near.Add(u);
            sumW += targets[u].Weight;
            cx += (float)(targets[u].Grid.X * targets[u].Weight);
            cy += (float)(targets[u].Grid.Y * targets[u].Weight);
        }

        var candidates = new List<Vector2>();
        for (int i = 0; i < near.Count; i++)
        {
            candidates.Add(Vector2.Lerp(c0, targets[near[i]].Grid, 0.5f));
            for (int j = i + 1; j < near.Count; j++)
            {
                candidates.Add(Vector2.Lerp(targets[near[i]].Grid, targets[near[j]].Grid, 0.5f));
            }
        }

        if (sumW > 0)
        {
            candidates.Add(new Vector2(cx / (float)sumW, cy / (float)sumW));
        }

        foreach (Vector2 p in candidates)
        {
            double gain = CoverGain(inp, captured, p, r2, out _);
            if (gain <= bestGain || Forbidden(inp, p, r2))
            {
                continue;
            }

            float rr = paths.Reach(node, p);
            if (rr < 0f)
            {
                continue;
            }

            best = p;
            bestGain = gain;
            reach = rr;
        }

        return best;
    }

    /// <summary>The spine planner: anchors, order, line, charges, spares.</summary>
    private static PlanResult Spine(PlanInputs inp)
    {
        int n = inp.Targets.Count;
        if (inp.Budget <= 0 || !inp.HasDetonator || n == 0)
        {
            Log(inp, inp.HasDetonator ? n == 0 ? "no targets to route" : "no charge budget" : "no detonator");
            return new PlanResult { Targets = n };
        }

        float effDist = inp.EffDist;
        float effRadius = inp.EffRadius;
        float r2 = effRadius * effRadius;
        Vector2 det = inp.Detonator;
        ExpeditionPaths paths = inp.Paths;

        var anchors = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (inp.Targets[i].Primary)
            {
                anchors.Add(i);
            }
        }

        Log(inp, $"=== SPINE PLAN === budget={inp.Budget} effDist={F0(effDist)} effRadius={F0(effRadius)} "
                 + $"anchors={anchors.Count} pickups={n - anchors.Count} shunned={inp.Shunned.Count} det={At(det)}");

        if (anchors.Count == 0)
        {
            Log(inp, "  no anchors (monolith, relic, sentinel) - nothing to route");
            return new PlanResult { Targets = n };
        }

        var anchorPos = new List<Vector2>(anchors.Count);
        foreach (int i in anchors)
        {
            anchorPos.Add(inp.Targets[i].Grid);
        }

        // The geometric tour, then the chain's re-scoring of it - free, over the same matrix.
        List<int> order = TourOrder(inp, anchorPos, out float[] ddet, out float[,] dmat);
        List<int>? chained = ChainReorder(inp, anchors, order, ddet, dmat);
        if (chained is not null)
        {
            order = chained;
        }

        // The Sentinel FIRST: detonating it as early as possible maximises the drone's uptime
        // and so the empowered logbook drops. The rest keep their order behind it.
        for (int k = 0; k < order.Count; k++)
        {
            if (inp.Targets[anchors[order[k]]].Sentinel && k > 0)
            {
                int pinned = order[k];
                order.RemoveAt(k);
                order.Insert(0, pinned);
                Log(inp, $"  [sentinel] pinned {At(inp.Targets[anchors[pinned]].Grid)} first - detonate as soon as possible");
                break;
            }
        }

        var ordered = new List<Vector2>(order.Count);
        var orderedTargets = new List<int>(order.Count);
        var monolithOrder = new List<uint>();
        foreach (int oi in order)
        {
            ordered.Add(anchorPos[oi]);
            orderedTargets.Add(anchors[oi]);
            PlanTarget target = inp.Targets[anchors[oi]];
            if (target.Kind == ExpeditionKind.Monolith)
            {
                monolithOrder.Add(target.Id);
            }
        }

        BuildSpine(inp, orderedTargets, out List<Vector2> spine, out List<float> spineZ, out List<int> anchorIdx);
        Log(inp, $"--- ROUTER === {spine.Count} cells, {ordered.Count} segments, {F0(ExpeditionPaths.LengthOf(spine))} grid ---");

        Log(inp, "--- SPINE order (det -> anchors) ---");
        Vector2 prev = det;
        float spineLen = 0f;
        for (int k = 0; k < orderedTargets.Count; k++)
        {
            PlanTarget a = inp.Targets[orderedTargets[k]];
            float hop = paths.Length(prev, a.Grid);
            if (hop < 0f)
            {
                hop = Vector2.Distance(prev, a.Grid);
            }

            spineLen += hop;
            Log(inp, $"  #{k + 1} {F0(a.Weight),7} ex {a.Kind} {At(a.Grid)} hop={F0(hop)}");
            prev = a.Grid;
        }

        int spineCharges = (int)Math.Ceiling(spineLen / effDist);
        Log(inp, $"  spine path={F0(spineLen)}, charges={spineCharges} / budget {inp.Budget}"
                 + (spineCharges > inp.Budget ? "  - over budget: the tail will not be reached" : string.Empty));

        var captured = new bool[n];
        List<RoutePoint> route = Place(inp, spine, spineZ, anchorIdx, orderedTargets, r2, captured, out double weight);

        var anchorsCovered = 0;
        foreach (int i in anchors)
        {
            if (captured[i])
            {
                anchorsCovered++;
            }
            else
            {
                Log(inp, $"  MISSED anchor {F0(inp.Targets[i].Weight)} ex {At(inp.Targets[i].Grid)} - out of budget or unreachable");
            }
        }

        Log(inp, $"--- LAID (spine) === {route.Count} charges, anchors covered {anchorsCovered}/{anchors.Count} ---");

        weight += Spares(inp, route, captured, r2);

        // Recount by scanning the final placements: the spares may have shifted what is covered.
        var covered = 0;
        var finalCaptured = new bool[n];
        foreach (RoutePoint rp in route)
        {
            List<int>? fired = Triggered(inp, rp.Grid, r2);
            for (int u = 0; u < n; u++)
            {
                if (!finalCaptured[u] && Covers(inp, fired, rp.Grid, inp.Targets[u].Grid, r2))
                {
                    finalCaptured[u] = true;
                    covered++;
                }
            }
        }

        int spare = inp.Budget - route.Count;
        var shunnedHit = 0;
        if (inp.Shunned.Count > 0)
        {
            var hit = new bool[inp.Shunned.Count];
            foreach (RoutePoint rp in route)
            {
                List<int>? fired = Triggered(inp, rp.Grid, r2);
                for (int s = 0; s < hit.Length; s++)
                {
                    if (!hit[s] && Covers(inp, fired, rp.Grid, inp.Shunned[s], r2))
                    {
                        hit[s] = true;
                        shunnedHit++;
                    }
                }
            }
        }

        Log(inp, $"=== FINAL === {route.Count} charges, weight={F0(weight)}, covered={covered}/{n}, spare={spare} left unplaced"
                 + (inp.Shunned.Count > 0 ? $", shunned relics hit={shunnedHit}/{inp.Shunned.Count}" : string.Empty));
        for (int i = 0; i < route.Count; i++)
        {
            Log(inp, $"  #{i + 1} {At(route[i].Grid)} {route[i].Note}");
        }

        return new PlanResult
        {
            Route = route,
            Weight = weight,
            Covered = covered,
            Targets = n,
            Anchors = anchors.Count,
            AnchorsCovered = anchorsCovered,
            SpinePoints = spine,
            SpineHeights = spineZ,
            SpineAnchorIndex = anchorIdx,
            MonolithOrder = monolithOrder,
            Phase = $"anchors {anchorsCovered}/{anchors.Count}, {route.Count} charges, {spare} spare"
                    + (shunnedHit > 0 ? $", {shunnedHit} shunned hit" : string.Empty),
            ShunnedHit = shunnedHit,
        };
    }

    /// <summary>
    /// The Router: the ordered anchors as one continuous dense walkable polyline from the
    /// detonator, with a height per point interpolated along each hop.
    /// </summary>
    /// <remarks>
    /// The smoothed path is SPARSE - a 1365-cell route can be thirteen points - and consecutive
    /// waypoints can be further apart than a hop, which would stop the placer's sweep dead; so
    /// each pair is rasterised to <see cref="SpineStep"/>. Without a grid it degrades to straight
    /// detonator-to-anchor hops.
    /// </remarks>
    private static void BuildSpine(
        PlanInputs inp, List<int> orderedTargets, out List<Vector2> pts, out List<float> zs, out List<int> anchorIdx)
    {
        pts = [inp.Detonator];
        zs = [inp.DetonatorZ];
        anchorIdx = new List<int>(orderedTargets.Count);

        Vector2 from = inp.Detonator;
        float fromZ = inp.DetonatorZ;
        foreach (int ti in orderedTargets)
        {
            Vector2 to = inp.Targets[ti].Grid;
            float toZ = inp.Targets[ti].Z;
            List<Vector2>? seg = inp.Paths.HasGrid ? inp.Paths.Path(from, to) : null;
            List<Vector2> wp = seg is { Count: >= 2 } ? seg : [from, to];

            float total = ExpeditionPaths.LengthOf(wp);
            float acc = 0f;
            for (int i = 1; i < wp.Count; i++)
            {
                Vector2 a = wp[i - 1];
                Vector2 b = wp[i];
                float segLen = Vector2.Distance(a, b);
                int sub = Math.Max(1, (int)MathF.Ceiling(segLen / SpineStep));
                for (int s = 1; s <= sub; s++)
                {
                    acc += segLen / sub;
                    float t = total > 0f ? acc / total : 1f;
                    pts.Add(Vector2.Lerp(a, b, (float)s / sub));
                    zs.Add(fromZ + ((toZ - fromZ) * t));
                }
            }

            anchorIdx.Add(pts.Count - 1);
            from = to;
            fromZ = toZ;
        }
    }

    /// <summary>The Placer: the forward sweep along the spine - see the class remarks.</summary>
    private static List<RoutePoint> Place(
        PlanInputs inp, List<Vector2> pts, List<float> zs, List<int> anchorIdx, List<int> orderedTargets,
        float r2, bool[] captured, out double weight)
    {
        weight = 0;
        var route = new List<RoutePoint>();
        int m = pts.Count;
        if (m == 0 || orderedTargets.Count == 0)
        {
            return route;
        }

        ExpeditionPaths paths = inp.Paths;
        float effRadius = inp.EffRadius;
        Log(inp, $"--- PLACER effDist={F0(inp.EffDist)} effRadius={F0(effRadius)} cells={m} ---");

        // How far past an anchor's spine index a covering cell may sit: a blast radius of cells
        // either side plus slack, so a coincidental far match cannot be chosen.
        int band = (int)Math.Ceiling(2f * effRadius / SpineStep) + 4;

        Vector2 node = inp.Detonator;
        var nodeIdx = 0;
        var ai = 0;
        var safety = 0;
        while (ai < orderedTargets.Count && route.Count < inp.Budget)
        {
            if (++safety > m + orderedTargets.Count + 8)
            {
                break;
            }

            int at = orderedTargets[ai];
            if (captured[at])
            {
                ai++;
                continue;
            }

            int anchorPosIdx = Math.Min(anchorIdx[ai], m - 1);
            Vector2 anchorPos = inp.Targets[at].Grid;

            // Coverage: among the band cells covering the anchor and reachable from the node,
            // the one whose blast catches the most uncaptured weight; ties to the furthest
            // forward. The reach check only runs where a cell would beat the best, so the
            // extra cost over "first reachable" is small.
            int bestJ = -1;
            float bestReach = 0f;
            double bestScore = double.NegativeInfinity;
            Vector2 bestP = default;
            float bestZ = 0f;
            var forbiddenCovers = 0;
            int hi = Math.Min(m - 1, anchorPosIdx + band);
            for (int j = hi; j > nodeIdx; j--)
            {
                List<int>? fired = Triggered(inp, pts[j], r2);
                if (!Covers(inp, fired, pts[j], anchorPos, r2))
                {
                    continue;
                }

                if (Forbidden(inp, fired, pts[j], r2))
                {
                    forbiddenCovers++;
                    continue;
                }

                double score = CoverGain(inp, captured, pts[j], r2, out _) + (j * 1e-6);
                if (score <= bestScore)
                {
                    continue;
                }

                float rr = paths.Reach(node, pts[j]);
                if (rr < 0f)
                {
                    continue;
                }

                bestScore = score;
                bestJ = j;
                bestReach = rr;
                bestP = pts[j];
                bestZ = zs[j];
            }

            // Off the spine: a reward beside the anchor falls on no band cell, but one point can
            // cover both when they are within two radii - the point on the anchor's blast edge
            // toward the reward. Only a strict coverage win overrides the spine cell.
            float twoR = 2f * effRadius;
            for (int u = 0; u < inp.Targets.Count; u++)
            {
                if (captured[u] || inp.Targets[u].Primary)
                {
                    continue;
                }

                float d = Vector2.Distance(anchorPos, inp.Targets[u].Grid);
                if (d < 1e-3f || d > twoR)
                {
                    continue;
                }

                Vector2 dir = (inp.Targets[u].Grid - anchorPos) / d;
                Vector2 p = anchorPos + (dir * Math.Min(d, effRadius * 0.999f));
                if (DistSq(p, anchorPos) > r2 || DistSq(p, inp.Targets[u].Grid) > r2 || !paths.IsWalkable(p))
                {
                    continue;
                }

                if (Forbidden(inp, p, r2))
                {
                    forbiddenCovers++;
                    continue;
                }

                double gain = CoverGain(inp, captured, p, r2, out _);
                if (gain <= bestScore)
                {
                    continue;
                }

                float rr = paths.Reach(node, p);
                if (rr < 0f)
                {
                    continue;
                }

                bestScore = gain;
                bestReach = rr;
                bestP = p;
                bestJ = anchorPosIdx;
                bestZ = inp.Targets[at].Z;
            }

            if (bestJ >= 0)
            {
                bool sentinel = inp.Targets[at].Sentinel;
                weight += Commit(
                    inp, captured, route, bestP, bestZ, r2, false, bestReach,
                    sentinel ? $" · SENTINEL {F0(inp.Targets[at].Weight)} ex at the edge" : $" · anchor {F0(inp.Targets[at].Weight)} ex at the edge",
                    sentinel);
                node = bestP;
                nodeIdx = bestJ;
                var advanced = 0;
                while (ai < orderedTargets.Count && captured[orderedTargets[ai]])
                {
                    ai++;
                    advanced++;
                }

                if (advanced > 1)
                {
                    Log(inp, $"    (one blast covered {advanced} anchors)");
                }

                continue;
            }

            // No way to cover it from here. Before bridging toward it, make sure covering it is
            // possible AT ALL: an anchor with a shunned relic at its side has no allowed covering
            // point, and a bridge laid toward it would be a charge spent on nothing.
            if (forbiddenCovers > 0 && !CoverableAtAll(inp, pts, anchorPosIdx, band, anchorPos, r2))
            {
                Log(inp, $"  MISSED anchor {F0(inp.Targets[at].Weight)} ex {At(anchorPos)} - every point covering it would set off a shunned relic");
                ai++;
                continue;
            }

            // Bridge: the furthest reachable cell toward the anchor that sets off no shunned relic.
            int bridgeJ = -1;
            float br = 0f;
            for (int j = anchorPosIdx; j > nodeIdx; j--)
            {
                if (Forbidden(inp, pts[j], r2))
                {
                    continue;
                }

                float rr = paths.Reach(node, pts[j]);
                if (rr >= 0f)
                {
                    bridgeJ = j;
                    br = rr;
                    break;
                }
            }

            if (bridgeJ < 0)
            {
                Log(inp, $"  MISSED anchor {F0(inp.Targets[at].Weight)} ex {At(anchorPos)} - cannot advance"
                         + (forbiddenCovers > 0 ? " without setting off a shunned relic" : string.Empty));
                ai++;
                continue;
            }

            // A bridge is pure traversal, so within a small band behind its furthest cell prefer
            // one whose blast catches more - a free pickup for negligible progress.
            int pickBand = (int)Math.Ceiling(effRadius / SpineStep);
            int chosenJ = bridgeJ;
            int chosenCount = CountUncovered(inp, captured, pts[bridgeJ], r2);
            for (int j = bridgeJ - 1; j >= Math.Max(nodeIdx + 1, bridgeJ - pickBand); j--)
            {
                int count = CountUncovered(inp, captured, pts[j], r2);
                if (count > chosenCount && !Forbidden(inp, pts[j], r2))
                {
                    chosenCount = count;
                    chosenJ = j;
                }
            }

            float chosenReach = chosenJ == bridgeJ ? br : paths.Reach(node, pts[chosenJ]);
            weight += Commit(
                inp, captured, route, pts[chosenJ], zs[chosenJ], r2, true, chosenReach,
                chosenCount > 0 ? " · toward the anchor (+grabs on the way)" : " · toward the anchor");
            node = pts[chosenJ];
            nodeIdx = chosenJ;
        }

        return route;
    }

    /// <summary>
    /// Whether any spine cell around an anchor covers it without setting off a shunned relic -
    /// reach aside, since the question is geometry, not where the chain head is now.
    /// </summary>
    private static bool CoverableAtAll(PlanInputs inp, List<Vector2> pts, int anchorPosIdx, int band, Vector2 anchorPos, float r2)
    {
        int hi = Math.Min(pts.Count - 1, anchorPosIdx + band);
        int lo = Math.Max(0, anchorPosIdx - band);
        for (int j = hi; j >= lo; j--)
        {
            List<int>? fired = Triggered(inp, pts[j], r2);
            if (Covers(inp, fired, pts[j], anchorPos, r2) && !Forbidden(inp, fired, pts[j], r2))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The spare optimiser - see the class remarks.</summary>
    private static double Spares(PlanInputs inp, List<RoutePoint> route, bool[] captured, float r2)
    {
        int spare0 = inp.Budget - route.Count;
        if (spare0 <= 0)
        {
            Log(inp, "--- SPARES: none ---");
            return 0;
        }

        ExpeditionPaths paths = inp.Paths;
        IReadOnlyList<PlanTarget> targets = inp.Targets;
        float effDist = inp.EffDist;
        float stepDist = inp.StepDist;
        float effRadius = inp.EffRadius;
        int n = targets.Count;
        int minCluster = Math.Max(1, inp.MinMarkers);
        double extra = 0;

        Log(inp, $"--- SPARES: {spare0} spare, cluster >= {minCluster}, budget-aware ---");

        var guard = 0;
        while (inp.Budget - route.Count > 0 && guard++ < spare0 + 8)
        {
            int remaining = inp.Budget - route.Count;

            // The Sentinel stays early: a detour may splice in at or after its charge, never before.
            var minEdge = 0;
            for (int i = 0; i < route.Count; i++)
            {
                if (route[i].Sentinel)
                {
                    minEdge = i;
                }
            }

            Vector2 bestC = default;
            double bestScore = 0;
            double bestGain = 0;
            var bestCount = 0;
            var bestHops = 0;
            float bestZ = 0f;
            float bestPath = float.MaxValue;
            int bestStart = -1;
            for (int c = 0; c < n; c++)
            {
                if (captured[c])
                {
                    continue;
                }

                Vector2 cand = targets[c].Grid;
                var count = 0;
                double gain = 0;
                List<int>? fired = Triggered(inp, cand, r2);
                if (Forbidden(inp, fired, cand, r2))
                {
                    continue;
                }

                for (int u = 0; u < n; u++)
                {
                    if (!captured[u] && Covers(inp, fired, cand, targets[u].Grid, r2))
                    {
                        count++;
                        gain += targets[u].Weight;
                    }
                }

                if (count < minCluster || gain <= 0)
                {
                    continue;
                }

                int si = BestDetourEdge(inp, route, cand, effDist, minEdge, out float outPath, out int hops);
                if (si < 0 || hops > remaining)
                {
                    continue;
                }

                double score = gain / hops;
                if (score > bestScore + 1e-9 || (score > bestScore - 1e-9 && outPath < bestPath))
                {
                    bestScore = score;
                    bestC = cand;
                    bestGain = gain;
                    bestCount = count;
                    bestHops = hops;
                    bestZ = targets[c].Z;
                    bestPath = outPath;
                    bestStart = si;
                }
            }

            if (bestScore <= 0 || bestStart < 0)
            {
                Log(inp, $"  no affordable cluster >= {minCluster} within {remaining} charge(s) - stop, {remaining} spare left");
                break;
            }

            Vector2 node = route[bestStart].Grid;
            float nodeZ = route[bestStart].Z;
            bool terminal = bestStart >= route.Count - 1;
            Log(inp, $"  -> cluster {At(bestC)} x{bestCount} {F0(bestGain)} ex, ~{bestHops} charge(s) from #{bestStart + 1}"
                     + (terminal ? " (terminal)" : $" -> rejoin #{bestStart + 2}") + $" (path {F0(bestPath)}, {F0(bestScore)} ex/charge)");

            // The detour, inserted after its edge so the player drops it in passing. Snapshot
            // first: a detour that cannot complete or reconnect within budget is rolled back
            // whole rather than spliced as a broken chain.
            var snapshot = (bool[])captured.Clone();
            double extraBefore = extra;
            var branch = new List<RoutePoint>();
            var aborted = false;
            var harvested = false;
            while (inp.Budget - (route.Count + branch.Count) > 0)
            {
                float reach = paths.Reach(node, bestC);
                if (reach >= 0f)
                {
                    Vector2 hp = MaxCoverPoint(inp, captured, node, bestC, r2, out float hpReach);
                    extra += Commit(inp, captured, branch, hp, bestZ, r2, false, hpReach, " · SPARE harvest");
                    node = hp;
                    harvested = true;
                    break;
                }

                if (!paths.StepToward(node, bestC, stepDist, out Vector2 step) || Forbidden(inp, step, r2))
                {
                    aborted = true;
                    break;
                }

                // A traversal charge should grab a reward by radius rather than land on empty
                // ground - when that costs no extra charge to reach the cluster.
                float nodeToCluster = paths.Length(node, bestC);
                int straightHops = Math.Max(1, (int)Math.Ceiling(nodeToCluster / effDist));
                Vector2 place = step;
                double placeCover = CoverGain(inp, captured, step, r2, out _);
                float r = paths.Reach(node, step);
                for (int u = 0; u < n; u++)
                {
                    if (captured[u])
                    {
                        continue;
                    }

                    float du = Vector2.Distance(node, targets[u].Grid);
                    if (du - effRadius > effDist)
                    {
                        continue;
                    }

                    float aMax = du > 1f ? Math.Min(1f, effRadius / du) : 1f;
                    for (float a = 0f; a <= aMax + 1e-3f; a += 0.1f)
                    {
                        Vector2 cand = Vector2.Lerp(targets[u].Grid, node, a);
                        if (!paths.IsWalkable(cand) || Forbidden(inp, cand, r2))
                        {
                            continue;
                        }

                        float rr = paths.Reach(node, cand);
                        if (rr < 0f)
                        {
                            continue;
                        }

                        double cover = CoverGain(inp, captured, cand, r2, out _);
                        if (cover > placeCover)
                        {
                            float onward = paths.Length(cand, bestC);
                            if (onward >= 0f && 1 + (int)Math.Ceiling(onward / effDist) <= straightHops)
                            {
                                place = cand;
                                placeCover = cover;
                                r = rr;
                            }
                        }

                        break;
                    }
                }

                if (r < 0f)
                {
                    aborted = true;
                    break;
                }

                float placeZ = nodeZ;
                for (int u = 0; u < n; u++)
                {
                    if (!captured[u] && DistSq(place, targets[u].Grid) <= 4f)
                    {
                        placeZ = targets[u].Z;
                        break;
                    }
                }

                extra += Commit(
                    inp, captured, branch, place, placeZ, r2, true, r,
                    placeCover > 0 ? " · SPARE bridge to the cluster (+grabs)" : " · SPARE bridge to the cluster");
                node = place;
            }

            if (!harvested)
            {
                aborted = true;
            }

            // Reconnect: explosives form ONE rope from the detonator, each within a hop of the
            // previous in placement order, so a mid-route detour must bridge back to the next
            // existing charge or the blast dies at the harvest. A terminal detour needs none.
            if (!aborted && !terminal)
            {
                Vector2 rejoin = route[bestStart + 1].Grid;
                while (paths.Reach(node, rejoin) < 0f && inp.Budget - (route.Count + branch.Count) > 0)
                {
                    if (!paths.StepToward(node, rejoin, stepDist, out Vector2 rstep) || Forbidden(inp, rstep, r2))
                    {
                        aborted = true;
                        break;
                    }

                    float rr = paths.Reach(node, rstep);
                    if (rr < 0f)
                    {
                        aborted = true;
                        break;
                    }

                    float rz = nodeZ;
                    for (int u = 0; u < n; u++)
                    {
                        if (!captured[u] && DistSq(rstep, targets[u].Grid) <= 4f)
                        {
                            rz = targets[u].Z;
                            break;
                        }
                    }

                    extra += Commit(inp, captured, branch, rstep, rz, r2, true, rr, " · SPARE reconnect");
                    node = rstep;
                }

                if (!aborted && paths.Reach(node, rejoin) < 0f)
                {
                    aborted = true;
                }
            }

            if (aborted)
            {
                Array.Copy(snapshot, captured, captured.Length);
                extra = extraBefore;
                Log(inp, "  the detour cannot complete or reconnect within budget - skipped, stop");
                break;
            }

            if (branch.Count > 0)
            {
                route.InsertRange(bestStart + 1, branch);
            }
        }

        return extra;
    }

    /// <summary>
    /// Where to splice a detour so the chain stays one rope: among the five charges nearest the
    /// cluster, the insert-after index minimising bridges out plus bridges back.
    /// </summary>
    private static int BestDetourEdge(
        PlanInputs inp, List<RoutePoint> route, Vector2 cand, float effDist, int minEdge, out float outPath, out int totalHops)
    {
        ExpeditionPaths paths = inp.Paths;
        outPath = -1f;
        totalHops = 0;
        if (route.Count == 0)
        {
            return -1;
        }

        const int K = 5;
        Span<int> near = stackalloc int[K];
        Span<float> nd = stackalloc float[K];
        for (int i = 0; i < K; i++)
        {
            near[i] = -1;
            nd[i] = float.MaxValue;
        }

        for (int i = minEdge; i < route.Count; i++)
        {
            float d = Vector2.Distance(route[i].Grid, cand);
            for (int k = 0; k < K; k++)
            {
                if (d < nd[k])
                {
                    for (int s = K - 1; s > k; s--)
                    {
                        nd[s] = nd[s - 1];
                        near[s] = near[s - 1];
                    }

                    nd[k] = d;
                    near[k] = i;
                    break;
                }
            }
        }

        int bestIdx = -1;
        int bestTotal = int.MaxValue;
        float bestOut = -1f;
        for (int k = 0; k < K; k++)
        {
            int i = near[k];
            if (i < 0)
            {
                continue;
            }

            float op = paths.Length(route[i].Grid, cand);
            if (op < 0f)
            {
                continue;
            }

            int outHops = Math.Max(1, (int)Math.Ceiling(op / effDist));
            var reconHops = 0;
            if (i != route.Count - 1)
            {
                float rp = paths.Length(cand, route[i + 1].Grid);
                if (rp < 0f)
                {
                    continue;
                }

                // The harvest already sits at the cluster and the next charge exists, so the
                // closing hop is free: only the bridges actually laid count.
                reconHops = Math.Max(0, (int)Math.Ceiling(rp / effDist) - 1);
            }

            int total = outHops + reconHops;
            if (total < bestTotal)
            {
                bestTotal = total;
                bestIdx = i;
                bestOut = op;
            }
        }

        if (bestIdx < 0)
        {
            return -1;
        }

        outPath = bestOut;
        totalHops = Math.Max(1, bestTotal);
        return bestIdx;
    }

    /// <summary>
    /// The anchors as a short open tour from the detonator: nearest-neighbour seeded, 2-opt
    /// improved, over walkable distances with unreachable pairs penalised but finite.
    /// </summary>
    /// <remarks>
    /// The all-pairs matrix is the expensive part - m² cross-map searches - and the dominant
    /// serial cost, so it is filled in PARALLEL: row i writes only its own cells, and the path
    /// memo is concurrent. Handed back so the chain can re-score orders for free over it.
    /// </remarks>
    private static List<int> TourOrder(PlanInputs inp, List<Vector2> stops, out float[] ddet, out float[,] dmat)
    {
        int m = stops.Count;
        var order = new List<int>(m);
        for (int i = 0; i < m; i++)
        {
            order.Add(i);
        }

        float[] ddetL = new float[m];
        float[,] dmatL = new float[m, m];
        ddet = ddetL;
        dmat = dmatL;
        ExpeditionPaths paths = inp.Paths;
        Vector2 det = inp.Detonator;

        float Dist(Vector2 a, Vector2 b)
        {
            float d = paths.Length(a, b);
            return d < 0f ? Vector2.Distance(a, b) * 4f : d;
        }

        if (m <= 2)
        {
            for (int i = 0; i < m; i++)
            {
                ddetL[i] = Dist(det, stops[i]);
                for (int j = i + 1; j < m; j++)
                {
                    float d = Dist(stops[i], stops[j]);
                    dmatL[i, j] = d;
                    dmatL[j, i] = d;
                }
            }

            // Two stops share the one leg between them, so the nearer to the detonator goes first.
            if (m == 2 && ddetL[1] < ddetL[0])
            {
                order.Reverse();
            }

            return order;
        }

        Parallel.For(0, m, i =>
        {
            ddetL[i] = Dist(det, stops[i]);
            for (int j = i + 1; j < m; j++)
            {
                float d = Dist(stops[i], stops[j]);
                dmatL[i, j] = d;
                dmatL[j, i] = d;
            }
        });

        var used = new bool[m];
        order.Clear();
        int current = -1;
        for (int s = 0; s < m; s++)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                if (used[i])
                {
                    continue;
                }

                float d = current < 0 ? ddetL[i] : dmatL[current, i];
                if (d < bd)
                {
                    bd = d;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            used[best] = true;
            order.Add(best);
            current = best;
        }

        var improved = true;
        var guard = 0;
        while (improved && guard++ < 60)
        {
            improved = false;
            for (int i = 0; i < order.Count - 1; i++)
            {
                for (int k = i + 1; k < order.Count; k++)
                {
                    float ab = i == 0 ? ddetL[order[i]] : dmatL[order[i - 1], order[i]];
                    float ac = i == 0 ? ddetL[order[k]] : dmatL[order[i - 1], order[k]];
                    bool hasD = k + 1 < order.Count;
                    float cd = hasD ? dmatL[order[k], order[k + 1]] : 0f;
                    float bd = hasD ? dmatL[order[i], order[k + 1]] : 0f;
                    if (ac + bd + 1e-3f < ab + cd)
                    {
                        order.Reverse(i, k - i + 1);
                        improved = true;
                    }
                }
            }
        }

        return order;
    }

    /// <summary>
    /// The chain-aware order: re-scores the tour by what the run is worth along it.
    /// </summary>
    /// <remarks>
    /// The geometric tour minimises walking and never looks at what a monolith propagates,
    /// while a rune's chain value is computed FOR whatever order is in force - so a strong
    /// rune at the tail reaches nothing, prices itself at zero, and nothing pulls it forward.
    /// Measured live by the reference: Opulent last was worth 52 ex, the same rune first 346.
    ///
    ///     J(order) = baseEx × Σ_j waves_j × (Σ_{i≤j} uplift_i) + rewards − 8 ex × ceil(walk / hop)
    ///
    /// scored on the part of the tour the budget AFFORDS: anchors past the budget are never
    /// reached and propagate nothing. Without the truncation a saturated plan scored every
    /// order at minus infinity and the re-ordering silently switched itself off exactly where
    /// it mattered. Rewards count for the same reason - they only cancel as a constant while
    /// every order reaches every anchor. Hill-climb (2-opt reversal and single relocation) from
    /// the geometric tour and from its reverse, the reverse being the move that usually
    /// matters. Null keeps the geometric order, so an equal-value order never churns the route.
    /// </remarks>
    private static List<int>? ChainReorder(PlanInputs inp, List<int> anchors, List<int> geo, float[] ddet, float[,] dmat)
    {
        if (!inp.ChainOrder || inp.ChainBaseEx <= 0f || geo.Count < 2)
        {
            return null;
        }

        var anyUplift = false;
        foreach (int g in geo)
        {
            if (inp.Targets[anchors[g]].Uplift > 0)
            {
                anyUplift = true;
                break;
            }
        }

        if (!anyUplift)
        {
            return null;
        }

        float hop = Math.Max(1f, inp.EffDist);

        (double Value, int Charges, int Reached) Score(List<int> ord)
        {
            double cum = 0;
            double sum = 0;
            double rewards = 0;
            ulong seen = 0;
            float walked = 0f;
            var charges = 0;
            var reached = 0;
            for (int i = 0; i < ord.Count; i++)
            {
                walked += i == 0 ? ddet[ord[0]] : dmat[ord[i - 1], ord[i]];
                int need = Math.Max(1, (int)Math.Ceiling(walked / hop));
                if (need > inp.Budget)
                {
                    break;
                }

                charges = need;
                reached = i + 1;
                PlanTarget t = inp.Targets[anchors[ord[i]]];
                double up = t.Uplift;
                if (up > 0 && t.RuneId is >= 0 and < 64)
                {
                    ulong bit = 1UL << t.RuneId;
                    if ((seen & bit) != 0)
                    {
                        up = 0;
                    }
                    else
                    {
                        seen |= bit;
                    }
                }

                cum += up;
                sum += t.Waves * cum;
                rewards += t.Reward;
            }

            return ((inp.ChainBaseEx * sum) + rewards, charges, reached);
        }

        double J(List<int> ord)
        {
            (double value, int charges, _) = Score(ord);
            return value - (ChargeOpportunityEx * charges);
        }

        List<int> Climb(List<int> start)
        {
            var cur = new List<int>(start);
            double curJ = J(cur);
            var better = true;
            var guard = 0;
            while (better && guard++ < 40)
            {
                better = false;
                for (int i = 0; i < cur.Count - 1 && !better; i++)
                {
                    for (int k = i + 1; k < cur.Count && !better; k++)
                    {
                        cur.Reverse(i, k - i + 1);
                        double j2 = J(cur);
                        if (j2 > curJ + 1e-6)
                        {
                            curJ = j2;
                            better = true;
                        }
                        else
                        {
                            cur.Reverse(i, k - i + 1);
                        }
                    }
                }

                for (int i = 0; i < cur.Count && !better; i++)
                {
                    int v = cur[i];
                    cur.RemoveAt(i);
                    for (int k = 0; k <= cur.Count && !better; k++)
                    {
                        cur.Insert(k, v);
                        double j2 = J(cur);
                        if (j2 > curJ + 1e-6)
                        {
                            curJ = j2;
                            better = true;
                        }
                        else
                        {
                            cur.RemoveAt(k);
                        }
                    }

                    if (!better)
                    {
                        cur.Insert(i, v);
                    }
                }
            }

            return cur;
        }

        var reversed = new List<int>(geo);
        reversed.Reverse();
        List<int> best = Climb(geo);
        double bestJ = J(best);
        List<int> alt = Climb(reversed);
        double altJ = J(alt);
        if (altJ > bestJ)
        {
            best = alt;
            bestJ = altJ;
        }

        double geoJ = J(geo);
        (double geoValue, int geoCharges, int geoReached) = Score(geo);
        (double bestValue, int bestCharges, int bestReached) = Score(best);
        Log(inp, $"--- ORDER (chain-aware) --- geometric {F0(geoValue)} ex over {geoCharges} charges reaching {geoReached}/{geo.Count}"
                 + $" => J={F0(geoJ)} | best {F0(bestValue)} ex over {bestCharges} reaching {bestReached}/{geo.Count} => J={F0(bestJ)}");

        if (!(bestJ > geoJ + 1e-6))
        {
            Log(inp, "  keeping the geometric order (no order propagates more than it costs)");
            return null;
        }

        Log(inp, $"  REORDERED: +{F0(bestValue - geoValue)} ex for {bestCharges - geoCharges:+0;-0;0} charge(s)");
        return best;
    }
}
