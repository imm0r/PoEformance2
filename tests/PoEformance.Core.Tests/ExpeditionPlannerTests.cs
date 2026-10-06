using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>The spine planner on synthetic fields: order, placement, bridges, spares, props, the chain.</summary>
public class ExpeditionPlannerTests
{
    private static PlanInputs Inputs(
        TerrainGrid? grid, Vector2 detonator, int budget, IReadOnlyList<PlanTarget> targets,
        IReadOnlyList<PlanProp>? props = null, bool chain = false, float effDist = 108f, float effRadius = 37f,
        bool coverage = false, int minMarkers = 1)
        => new()
        {
            Paths = new ExpeditionPaths(grid, null, effDist),
            HasDetonator = true,
            Detonator = detonator,
            DetonatorZ = 0f,
            Budget = budget,
            EffDist = effDist,
            EffRadius = effRadius,
            StepDist = effDist - 1f,
            Targets = targets,
            Props = props ?? [],
            MarkerCoverageMode = coverage,
            MinMarkers = minMarkers,
            ChainOrder = chain,
            ChainBaseEx = 30f,
            Trace = [],
        };

    private static PlanTarget Monolith(uint id, float x, float y, double value, int waves = 5, double uplift = 0, int rune = -1, bool primary = true)
        => new(id, ExpeditionKind.Monolith, new Vector2(x, y), 0f, value, primary, false, waves, uplift, rune, value);

    private static PlanTarget Marker(uint id, float x, float y, double value)
        => new(id, ExpeditionKind.Marker, new Vector2(x, y), 0f, value, false);

    [Fact]
    public void TwoMonolithsOnAnOpenFieldAreCoveredInOrder_WithTheNearerFirst()
    {
        TerrainGrid field = ExpeditionPathsTests.Field(400, 60);
        PlanInputs inputs = Inputs(field, new Vector2(20, 30), budget: 5,
        [
            Monolith(2, 200, 30, 50),
            Monolith(1, 100, 30, 10),
        ]);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);

        Assert.True(plan.Any);
        Assert.Equal(2, plan.Anchors);
        Assert.Equal(2, plan.AnchorsCovered);
        Assert.Equal([1u, 2u], plan.MonolithOrder);
        Assert.True(plan.Route.Count <= 5);
        Assert.Equal(60.0, plan.Weight, 3);
        Assert.True(plan.SpinePoints.Count > 2);
        Assert.Equal(2, plan.SpineAnchorIndex.Count);
        Assert.Contains(plan.Trace, line => line.StartsWith("=== FINAL ===", StringComparison.Ordinal));

        // Edge placement: the first charge sits past the near monolith, toward the far one,
        // with the monolith still inside the blast.
        RoutePoint first = plan.Route[0];
        Assert.False(first.Bridge);
        Assert.True(first.Grid.X > 100f, $"placed at {first.Grid.X}, not past the anchor");
        Assert.True(Vector2.Distance(first.Grid, new Vector2(100, 30)) <= 37f + 0.01f);

        // Every charge is within one hop of the previous, from the detonator.
        Vector2 previous = inputs.Detonator;
        foreach (RoutePoint point in plan.Route)
        {
            Assert.True(Vector2.Distance(previous, point.Grid) <= 108f + 0.01f, "a hop longer than the reach");
            previous = point.Grid;
        }
    }

    [Fact]
    public void AnAnchorBeyondOneHopGetsABridgeFirst()
    {
        TerrainGrid field = ExpeditionPathsTests.Field(400, 60);
        PlanInputs inputs = Inputs(field, new Vector2(20, 30), budget: 5, [Monolith(1, 250, 30, 40)]);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);

        Assert.Equal(1, plan.AnchorsCovered);
        Assert.True(plan.Route.Count >= 2);
        Assert.True(plan.Route[0].Bridge);
        Assert.Contains("toward the anchor", plan.Route[0].Note, StringComparison.Ordinal);
        Assert.False(plan.Route[^1].Bridge);
        Assert.Equal(40.0, plan.Weight, 3);
    }

    [Fact]
    public void TheRouteGoesRoundAWall_AndEveryChargeStandsOnFloor()
    {
        // A wall across the field with a gap at the bottom.
        var rows = new string[60];
        for (int y = 0; y < 60; y++)
        {
            rows[y] = y >= 50 ? new string('.', 300) : string.Concat(new string('.', 150), "#", new string('.', 149));
        }

        TerrainGrid grid = ExpeditionPathsTests.Grid(rows);
        PlanInputs inputs = Inputs(grid, new Vector2(20, 10), budget: 8, [Monolith(1, 280, 10, 40)]);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);

        Assert.Equal(1, plan.AnchorsCovered);
        foreach (RoutePoint point in plan.Route)
        {
            Assert.True(grid.IsWalkable((int)MathF.Round(point.Grid.X), (int)MathF.Round(point.Grid.Y)), $"a charge on the wall at {point.Grid}");
        }

        // The spine dips to the gap rather than crossing the wall.
        Assert.Contains(plan.SpinePoints, p => p.Y >= 49f);
    }

    [Fact]
    public void SpareChargesHarvestAMarkerCluster_NotALoneMarker()
    {
        // The cluster sits ninety cells off the monolith: no one blast takes both, so it costs
        // a spare - one hop for fifteen ex.
        TerrainGrid field = ExpeditionPathsTests.Field(400, 200);
        PlanInputs inputs = Inputs(field, new Vector2(20, 60), budget: 4,
        [
            Monolith(1, 80, 60, 40),
            Marker(10, 80, 150, 5),
            Marker(11, 90, 152, 5),
            Marker(12, 85, 158, 5),
            Marker(20, 300, 60, 5),
        ], coverage: true, minMarkers: 2);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);

        Assert.Equal(1, plan.AnchorsCovered);
        Assert.Equal(2, plan.Route.Count);
        Assert.Contains("SPARE harvest", plan.Route[1].Note, StringComparison.Ordinal);
        Assert.Equal(55.0, plan.Weight, 3);

        // The lone marker far off needs three charges to reach and a cluster of one: never worth it.
        Assert.DoesNotContain(plan.Route, p => Vector2.Distance(p.Grid, new Vector2(300, 60)) <= 37f);
    }

    [Fact]
    public void AnExplodingPropCoversWhatTheChargeCannot()
    {
        TerrainGrid field = ExpeditionPathsTests.Field(400, 120);
        PlanInputs inputs = Inputs(field, new Vector2(20, 30), budget: 2,
        [
            Monolith(1, 100, 30, 40),
            Marker(10, 100, 95, 20),
        ], props: [new PlanProp(new Vector2(100, 55), 55f)]);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);

        // One charge: the monolith by its own blast, the marker 40 cells further by the prop's.
        Assert.Single(plan.Route);
        Assert.Equal(2, plan.Route[0].Captured);
        Assert.Contains("sets off 1 prop", plan.Route[0].Note, StringComparison.Ordinal);
        Assert.Equal(60.0, plan.Weight, 3);
    }

    [Fact]
    public void TheChainPutsTheStrongRuneFirstWhenThatIsWorthTheWalk()
    {
        TerrainGrid field = ExpeditionPathsTests.Field(400, 60);
        PlanInputs geometric = Inputs(field, new Vector2(0, 30), budget: 6,
        [
            Monolith(1, 50, 30, 10, waves: 5),
            Monolith(2, 100, 30, 10, waves: 5, uplift: 0.35, rune: 20),
        ]);
        PlanResult plain = ExpeditionPlanner.Plan(geometric);
        Assert.Equal([1u, 2u], plain.MonolithOrder);

        // With the chain: Opulent first buffs both monoliths' waves - 30 x 0.35 x 10 = 105 ex
        // over two charges against 52.5 over one, which clears the eight an extra charge costs.
        PlanInputs chained = Inputs(field, new Vector2(0, 30), budget: 6,
        [
            Monolith(1, 50, 30, 10, waves: 5),
            Monolith(2, 100, 30, 10, waves: 5, uplift: 0.35, rune: 20),
        ], chain: true);
        PlanResult ordered = ExpeditionPlanner.Plan(chained);
        Assert.Equal([2u, 1u], ordered.MonolithOrder);
        Assert.Contains(ordered.Trace, line => line.Contains("REORDERED", StringComparison.Ordinal));

        // No rune anywhere: the chain has nothing to say and the geometric order stands.
        PlanInputs runeless = Inputs(field, new Vector2(0, 30), budget: 6,
        [
            Monolith(1, 50, 30, 10, waves: 5),
            Monolith(2, 100, 30, 10, waves: 5),
        ], chain: true);
        Assert.Equal([1u, 2u], ExpeditionPlanner.Plan(runeless).MonolithOrder);
    }

    [Fact]
    public void TheSentinelIsPinnedFirst()
    {
        // Nearest-first would go monolith, sentinel, flag. The sentinel is pulled to the front so
        // its drone is up before the logbook flag is unearthed; the flag's charge comes after it.
        TerrainGrid field = ExpeditionPathsTests.Field(400, 60);
        PlanInputs inputs = Inputs(field, new Vector2(0, 30), budget: 8,
        [
            Monolith(1, 50, 30, 40),
            new PlanTarget(9, ExpeditionKind.Sentinel, new Vector2(150, 30), 0f, 151, true, Sentinel: true),
            new PlanTarget(10, ExpeditionKind.Marker, new Vector2(250, 30), 0f, 100, true),
        ], coverage: true);

        PlanResult plan = ExpeditionPlanner.Plan(inputs);
        Assert.Contains(plan.Trace, line => line.Contains("[sentinel] pinned", StringComparison.Ordinal));
        Assert.Contains(plan.Route, p => p.Sentinel);
        Assert.Equal(3, plan.AnchorsCovered);

        int sentinelAt = plan.Route.ToList().FindIndex(p => p.Sentinel);
        int flagAt = plan.Route.ToList().FindIndex(p => Vector2.Distance(p.Grid, new Vector2(250, 30)) <= 37f);
        Assert.True(sentinelAt >= 0 && flagAt >= 0 && sentinelAt < flagAt, $"sentinel at {sentinelAt}, flag at {flagAt}:\n{string.Join('\n', plan.Trace)}");
    }

    [Fact]
    public void NothingToPlanIsSaidRatherThanPlanned()
    {
        TerrainGrid field = ExpeditionPathsTests.Field(100, 20);

        PlanResult noTargets = ExpeditionPlanner.Plan(Inputs(field, new Vector2(10, 10), 5, []));
        Assert.False(noTargets.Any);
        Assert.Contains("no targets", noTargets.Trace[0], StringComparison.Ordinal);

        PlanResult noBudget = ExpeditionPlanner.Plan(Inputs(field, new Vector2(10, 10), 0, [Monolith(1, 50, 10, 10)]));
        Assert.False(noBudget.Any);

        PlanResult pickupsOnly = ExpeditionPlanner.Plan(Inputs(field, new Vector2(10, 10), 5, [Marker(1, 50, 10, 10)]));
        Assert.False(pickupsOnly.Any);
        Assert.Contains(pickupsOnly.Trace, line => line.Contains("no anchors", StringComparison.Ordinal));

        // No grid at all: straight lines, and still a plan.
        PlanResult straight = ExpeditionPlanner.Plan(Inputs(null, new Vector2(10, 10), 5, [Monolith(1, 60, 10, 10)]));
        Assert.True(straight.Any);
        Assert.Equal(1, straight.AnchorsCovered);
    }
}
