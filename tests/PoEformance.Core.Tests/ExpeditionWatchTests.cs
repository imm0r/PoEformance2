using System.Diagnostics;
using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The reader-thread half of the route planner: a snapshot's entities sorted into targets,
/// the counts from whichever source answers, the plan cooked on request and taken up, and the
/// staleness that decides when it is cooked again.
/// </summary>
public class ExpeditionWatchTests
{
    private const string MonolithPath = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";
    private const string ChestPath = "Metadata/Chests/LeaguesExpedition/ExpeditionChestCurrency";
    private const string BarrelPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_BoomBarrel";
    private const string RockPath = "Metadata/Terrain/Gallows/Leagues/Expedition/Objects/Rock";
    private const string SulphitePath = "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Wastes/Objects/Sulphite";
    private const string SentinelPath = "Metadata/MiscellaneousObjects/Sentinel/SentinelRandomEncounterObject";
    private const string GatePath = "Metadata/Terrain/Gallows/Logbook_Gully/Objects/DevourerSegment";

    private const ulong ServerData = 0x0000_0600_0010_0000;
    private const ulong Controller = 0x0000_0600_0020_0000;

    private static OffsetSchema Schema() => MonolithFixture.ShippedSchema();

    private static ExpeditionWatch Make(FakeMemoryReader fake, OffsetSchema schema, ExpeditionSettings? settings = null)
        => new(fake, schema, MonolithWatchTests.GameStates)
        {
            Settings = settings ?? new ExpeditionSettings(Enabled: true),
        };

    private static WorldEntity Player() => new(1, 0x1000, "Metadata/Characters/Int/IntFour", EntityKind.Player, 10f * MapView.WorldToGrid, 60f * MapView.WorldToGrid, 0f);

    /// <summary>An entity at a grid cell. Its address is never laid into memory, so every component read answers "nothing".</summary>
    private static WorldEntity At(uint id, string path, float gx, float gy, float z = 0f, string icon = "", int? remembered = null)
        => new(id, 0x7000_0000UL + ((ulong)id << 8), path, EntityKind.Unknown, gx * MapView.WorldToGrid, gy * MapView.WorldToGrid, z, MapIcon: icon, RememberedForMs: remembered);

    private static WorldSnapshot Snapshot(TerrainGrid? grid, IEnumerable<WorldEntity> entities, string areaId = "MapExpedition", uint hash = 7, ulong serverData = 0)
        => new(true, Player(), [.. entities], new float[16], Area: new AreaInfo(areaId, "Somewhere", 10, false, false), Terrain: grid, AreaHash: hash, ServerData: serverData);

    /// <summary>A monolith as the chain-valued view reports it.</summary>
    private static MonolithView Mono(
        uint id, float gx, float gy, double best, double joint = 0, int holes = 5, int waves = 5, double uplift = 0, int rune = -1,
        int mode = 1, string icon = "Expedition2RemnantActive")
        => new(
            id, 0, MonolithPath, icon, gx * MapView.WorldToGrid, gy * MapView.WorldToGrid, 0f, 0f, true,
            new MonolithStation(1, holes, 20, 2, false, [2], mode, false, string.Empty, false, string.Empty),
            new MonolithStates(holes, 1, false, string.Empty), [], best, best, "Opulent")
        {
            Joint = joint > 0 ? joint : best,
            ExpectedWaves = waves,
            RouteUplift = uplift,
            RouteRune = rune,
        };

    private static MonolithsView Monoliths(params MonolithView[] views) => new([.. views], "ok", 0);

    /// <summary>Asks for a plan and services until it is taken up. Fake time advances ten ms a call.</summary>
    private static ExpeditionView Cook(ExpeditionWatch watch, WorldSnapshot snapshot, MonolithsView monoliths, ref long now)
    {
        watch.RequestRun();
        watch.Service(snapshot, now, monoliths);
        Assert.True(watch.View.Computing, "the run was not launched");

        var clock = Stopwatch.StartNew();
        while (watch.View.Computing && clock.ElapsedMilliseconds < 20_000)
        {
            Thread.Sleep(5);
            now += 10;
            watch.Service(snapshot, now, monoliths);
        }

        Assert.False(watch.View.Computing, "the plan never came back");
        return watch.View;
    }

    [Fact]
    public void SwitchedOffOrOutOfTheGameItPublishesNothing_AndReadsNothing()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionWatch off = Make(fake, schema, new ExpeditionSettings(Enabled: false));

        long before = fake.Reads;
        off.Service(Snapshot(ExpeditionPathsTests.Field(100, 100), [At(100, ExpeditionReader.DetonatorPath, 20, 60)]), 0, Monoliths());
        Assert.Equal("expedition planner off", off.View.Status);
        Assert.False(off.View.HasDetonator);
        Assert.Equal(0, fake.Reads - before);
        Assert.Empty(off.PlannedOrder);

        ExpeditionWatch on = Make(fake, schema);
        on.Service(new WorldSnapshot(false, null, [], new float[16]), 0, Monoliths());
        Assert.Equal("not in an area", on.View.Status);
        Assert.False(on.View.CanPlan);
    }

    [Fact]
    public void AScanSortsTheAreaIntoTargets_PropsAndCharges()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionWatch watch = Make(fake, schema, new ExpeditionSettings(
            Enabled: true, RelicWeights: [new("ExpeditionRelicUpsideSpecialSulphite", 25f)]));
        TerrainGrid field = ExpeditionPathsTests.Field(400, 120);

        WorldEntity[] entities =
        [
            At(100, ExpeditionReader.DetonatorPath, 20, 60),
            At(101, ExpeditionReader.ExplosivePath, 40, 60),
            At(102, ExpeditionReader.ExplosivePath, 60, 60),
            At(200, MonolithPath, 120, 60, icon: "Expedition2RemnantActive"),
            At(201, MonolithPath, 150, 80, icon: "Expedition2RemnantActive"),
            At(202, MonolithPath, 150, 40, icon: "Expedition2RemnantDeactivated"),
            At(203, MonolithPath, 180, 60, icon: "Expedition2RemnantActive"),

            // Three tiny flags fix the baseline; the rest stand taller by the game's fixed tiers.
            At(300, ExpeditionReader.MarkerPath, 70, 90),
            At(301, ExpeditionReader.MarkerPath, 72, 92),
            At(302, ExpeditionReader.MarkerPath, 74, 94),
            At(303, ExpeditionReader.MarkerPath, 100, 100, z: -50f, icon: "RewardChestCurrencyRare"),
            At(304, ExpeditionReader.MarkerPath, 110, 100, z: -30f, icon: "RewardChestCurrency"),
            At(305, ExpeditionReader.MarkerPath, 120, 100, z: -20f),
            At(306, ExpeditionReader.MarkerPath, 130, 100, z: -10f),

            At(400, ExpeditionReader.RelicPath, 90, 30),
            At(401, SulphitePath, 95, 30),
            At(500, ChestPath, 200, 20),
            At(600, BarrelPath, 90, 60),
            At(601, RockPath, 92, 62),
            At(700, SentinelPath, 200, 60),
            At(800, GatePath, 250, 60),
        ];
        MonolithsView monoliths = Monoliths(
            Mono(200, 120, 60, best: 40, joint: 55, waves: 5, uplift: 0.35, rune: 20),
            Mono(201, 150, 80, best: 30, mode: 0),
            Mono(202, 150, 40, best: 30, icon: "Expedition2RemnantDeactivated"));

        watch.Service(Snapshot(field, entities), 0, monoliths);
        ExpeditionView view = watch.View;

        Assert.True(view.HasDetonator);
        Assert.True(view.CanPlan);
        Assert.Equal(new Vector2(20, 60), view.Detonator);
        Assert.False(view.Activated);

        // Two charges down, counted as entities under the manual total.
        Assert.Equal(2, view.Charges.Count);
        Assert.Equal(2, view.Placed);
        Assert.Equal(15, view.Total);
        Assert.Equal(13, view.Remaining);
        Assert.Equal("manual", view.CountsSource);
        Assert.False(view.CountsKnown);
        Assert.Equal(2, view.NextIndex);

        // Unconfirmed counts on a non-logbook map: normal physics.
        Assert.False(view.IsGrand);
        Assert.False(view.IsLogbook);
        Assert.Equal(90f, view.EffDist, 3);
        Assert.Equal(30f, view.EffRadius, 3);

        // Thirteen: the Gully blocker is a remnant as well as a gate, so it counts among the remnants.
        Assert.Equal("detonator found · 13 targets (2 monoliths, 7 markers, 3 remnants, 1 chests) · 1 props", view.Status);
        Assert.Single(view.Props);
        Assert.Equal(55f, view.Props[0].Radius);
        Assert.Equal([RockPath], view.UnmatchedProps);
        Assert.Empty(view.Gates);

        // The foreign and the collected monolith are gone; the unpriced one stays, unvalued.
        Dictionary<uint, ExpeditionTargetView> byId = view.Targets.ToDictionary(t => t.Id);
        Assert.DoesNotContain(201u, byId.Keys);
        Assert.DoesNotContain(202u, byId.Keys);
        Assert.Equal(55.0, byId[200].Value, 6);
        Assert.True(byId[200].Primary);
        Assert.Equal("Opulent", byId[200].Info);
        Assert.Equal(0.0, byId[203].Value, 6);
        Assert.False(byId[203].Primary);

        // The marker tiers by pole height over the tiny baseline.
        Assert.Equal(("tiny", 0.0), (byId[300].Tier, byId[300].Value));
        Assert.Equal(("logbook", 100.0), (byId[303].Tier, byId[303].Value));
        Assert.Equal(("gold", 60.0), (byId[304].Tier, byId[304].Value));
        Assert.Equal(("magic", 30.0), (byId[305].Tier, byId[305].Value));
        Assert.Equal(("white", 10.0), (byId[306].Tier, byId[306].Value));
        Assert.Equal("RewardChestCurrencyRare", byId[303].Info);

        // The relic with no readable mods weighs nothing; the logbook remnant carries its type's mod.
        Assert.Equal(("", 0.0, false), (byId[400].Info, byId[400].Value, byId[400].Primary));
        Assert.Equal(("ExpeditionRelicUpsideSpecialSulphite", 25.0, true), (byId[401].Info, byId[401].Value, byId[401].Primary));
        Assert.Equal((ExpeditionKind.Remnant, "ExpeditionRelicUpsideSpecialDevourerTail", 0.0), (byId[800].Kind, byId[800].Info, byId[800].Value));

        Assert.True(byId[700].Primary);
        Assert.Equal(ExpeditionKind.Sentinel, byId[700].Kind);

        // Most valuable first.
        Assert.Equal(303u, view.Targets[0].Id);

        // No plan yet: stale, not cooking.
        Assert.True(view.Stale);
        Assert.False(view.Computing);
        Assert.False(view.Route.Any);
    }

    [Fact]
    public void TargetsAccumulateForTheArea_AndAreForgottenWithIt()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionWatch watch = Make(fake, schema);
        TerrainGrid field = ExpeditionPathsTests.Field(200, 100);

        watch.Service(Snapshot(field, [At(100, ExpeditionReader.DetonatorPath, 20, 60), At(300, ExpeditionReader.MarkerPath, 70, 60)]), 0, Monoliths());
        Assert.Single(watch.View.Targets);

        // Walked out of range: the game stops listing the flag, the cache keeps it. A monolith
        // remembered from memory still joins the priced view by its id.
        watch.Service(
            Snapshot(field, [At(100, ExpeditionReader.DetonatorPath, 20, 60), At(200, MonolithPath, 120, 60, remembered: 5000)]),
            ExpeditionWatch.ScanMs, Monoliths(Mono(200, 120, 60, best: 12)));
        Assert.Equal(2, watch.View.Targets.Count);
        Assert.Equal(12.0, watch.View.Targets.Single(t => t.Id == 200).Value, 6);

        // Between scans nothing is re-read: the view stands.
        long reads = fake.Reads;
        watch.Service(Snapshot(field, []), ExpeditionWatch.ScanMs + 1, Monoliths());
        Assert.Equal(reads, fake.Reads);
        Assert.Equal(2, watch.View.Targets.Count);

        // A new area: everything goes.
        watch.Service(Snapshot(field, [], hash: 8), ExpeditionWatch.ScanMs * 2, Monoliths());
        Assert.Empty(watch.View.Targets);
        Assert.False(watch.View.HasDetonator);
        Assert.StartsWith("no detonator in this area", watch.View.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunIsCookedOffTheThread_TakenUp_AndGoesStaleWhenAKnobMoves()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionWatch watch = Make(fake, schema, new ExpeditionSettings(Enabled: true, ManualTotal: 5));
        TerrainGrid field = ExpeditionPathsTests.Field(400, 60);
        WorldEntity[] entities =
        [
            At(100, ExpeditionReader.DetonatorPath, 20, 30),
            At(200, MonolithPath, 100, 30, icon: "Expedition2RemnantActive"),
            At(201, MonolithPath, 160, 30, icon: "Expedition2RemnantActive"),
        ];
        MonolithsView monoliths = Monoliths(Mono(200, 100, 30, best: 40), Mono(201, 160, 30, best: 10));
        WorldSnapshot snapshot = Snapshot(field, entities);
        long now = 0;

        watch.Service(snapshot, now, monoliths);
        ExpeditionView cooked = Cook(watch, snapshot, monoliths, ref now);

        Assert.True(cooked.Route.Any);
        Assert.Equal(2, cooked.Route.AnchorsCovered);
        Assert.Equal([200u, 201u], cooked.Route.MonolithOrder);
        Assert.Equal([200u, 201u], watch.PlannedOrder);
        Assert.False(cooked.Stale);
        Assert.True(cooked.Route.Route.Count is >= 1 and <= 5);
        Assert.True(cooked.Route.ComputeMs >= 0);

        // The same area, a step later: the route stands and is not stale.
        now += ExpeditionWatch.ScanMs;
        watch.Service(snapshot, now, monoliths);
        Assert.False(watch.View.Stale);
        Assert.Same(cooked.Route, watch.View.Route);

        // A knob the plan depends on: stale, with the old route still shown.
        watch.Settings = new ExpeditionSettings(Enabled: true, ManualTotal: 5, MonolithMinEx: 500f);
        now += ExpeditionWatch.ScanMs;
        watch.Service(snapshot, now, monoliths);
        Assert.True(watch.View.Stale);
        Assert.Same(cooked.Route, watch.View.Route);

        // Cooked again under the new knob: fresh. Under 500 ex nothing is worth the walk, so
        // the fallback routes the monoliths anyway rather than leaving the map unplanned.
        ExpeditionView again = Cook(watch, snapshot, monoliths, ref now);
        Assert.False(again.Stale);
        Assert.True(again.Route.Any);
        Assert.Contains(again.Route.Trace, line => line.Contains("[fallback]", StringComparison.Ordinal));

        // A new area takes the plan with it.
        watch.Service(Snapshot(field, entities, hash: 9), now + ExpeditionWatch.ScanMs, monoliths);
        Assert.False(watch.View.Route.Any);
        Assert.Empty(watch.PlannedOrder);
    }

    [Fact]
    public void ARunWithoutADetonatorIsDropped_NotCooked()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ExpeditionWatch watch = Make(fake, schema);
        WorldSnapshot snapshot = Snapshot(ExpeditionPathsTests.Field(100, 100), [At(300, ExpeditionReader.MarkerPath, 70, 60)]);

        watch.RequestRun();
        watch.Service(snapshot, 0, Monoliths());
        Assert.False(watch.View.Computing);
        Assert.False(watch.View.CanPlan);

        // The request did not linger: a detonator appearing later does not start a plan by itself.
        watch.Service(Snapshot(ExpeditionPathsTests.Field(100, 100), [At(100, ExpeditionReader.DetonatorPath, 20, 60)]), ExpeditionWatch.ScanMs, Monoliths());
        Assert.True(watch.View.CanPlan);
        Assert.False(watch.View.Computing);
    }

    [Fact]
    public void GrandPhysicsComeFromTheLogbookArea_OrFromAConfirmedTotal()
    {
        OffsetSchema schema = Schema();
        TerrainGrid field = ExpeditionPathsTests.Field(100, 100);
        WorldEntity[] entities = [At(100, ExpeditionReader.DetonatorPath, 20, 60), At(101, ExpeditionReader.ExplosivePath, 30, 60)];

        // A logbook by its id, with the manual total: Grand, counts still manual.
        var fake = new FakeMemoryReader();
        ExpeditionWatch logbook = Make(fake, schema);
        logbook.Service(Snapshot(field, entities, areaId: "ExpeditionLogBook_Wastes"), 0, Monoliths());
        Assert.True(logbook.View.IsLogbook);
        Assert.True(logbook.View.IsGrand);
        Assert.Equal(108f, logbook.View.EffDist, 3);
        Assert.Equal(37f, logbook.View.EffRadius, 3);
        Assert.Equal("manual", logbook.View.CountsSource);

        // A map whose controller confirms fifteen charges: Grand by the threshold, the counts trusted.
        fake = new FakeMemoryReader();
        int slot = schema.Structs["ServerData"].OffsetOf("ExpeditionControllerPtr");
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)slot, Controller);
        ExpeditionReaderTests.PlaceController(fake, schema, Controller, ServerData, total: 15, placed: 3, placementAt: Controller + 0x1000, placedVector: Controller + 0x2000);
        ExpeditionWatch confirmed = Make(fake, schema);
        confirmed.Service(Snapshot(field, entities, serverData: ServerData), 0, Monoliths());
        Assert.Equal("ServerData slot", confirmed.View.CountsSource);
        Assert.True(confirmed.View.CountsKnown);
        Assert.Equal((15, 3), (confirmed.View.Total, confirmed.View.Placed));
        Assert.False(confirmed.View.IsLogbook);
        Assert.True(confirmed.View.IsGrand);
        Assert.Equal(108f, confirmed.View.EffDist, 3);

        // The controller counts a cancelled charge off at once: no clamp on the trusted source.
        ExpeditionReaderTests.PlaceController(fake, schema, Controller, ServerData, total: 15, placed: 2, placementAt: Controller + 0x1000, placedVector: Controller + 0x2000);
        confirmed.Service(Snapshot(field, entities, serverData: ServerData), ExpeditionWatch.ScanMs, Monoliths());
        Assert.Equal(2, confirmed.View.Placed);

        // Five confirmed: a normal expedition.
        fake = new FakeMemoryReader();
        fake.Place(ServerData, new byte[0x2800]);
        fake.Place<ulong>(ServerData + (ulong)slot, Controller);
        ExpeditionReaderTests.PlaceController(fake, schema, Controller, ServerData, total: 5, placed: 0, placementAt: Controller + 0x1000, placedVector: Controller + 0x2000);
        ExpeditionWatch normal = Make(fake, schema);
        normal.Service(Snapshot(field, entities, serverData: ServerData), 0, Monoliths());
        Assert.Equal(5, normal.View.Total);
        Assert.False(normal.View.IsGrand);
        Assert.Equal(90f, normal.View.EffDist, 3);

        Assert.True(ExpeditionWatch.IsLogbookArea("ExpeditionSubArea_Basin_1"));
        Assert.False(ExpeditionWatch.IsLogbookArea("MapExpedition"));
        Assert.False(ExpeditionWatch.IsLogbookArea(null));
    }

    [Fact]
    public void TheMapModifiersStretchTheReachAndTheBlast()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        MonolithWatchTests.Chain(fake, schema);
        int at = schema.Structs["AreaInstance"].OffsetOf("MapMods");
        ulong vector = MonolithFixture.Area + 0x800;
        fake.Place(MonolithFixture.Area, new byte[0x200]);
        fake.Place<ulong>(MonolithFixture.Area + (ulong)at, vector);
        fake.Place<ulong>(MonolithFixture.Area + (ulong)at + 8, vector + 16);
        fake.Place<int>(vector, 13686);
        fake.Place<int>(vector + 4, 50);
        fake.Place<int>(vector + 8, 13471);
        fake.Place<int>(vector + 12, 20);

        ExpeditionWatch watch = Make(fake, schema);
        watch.Service(Snapshot(ExpeditionPathsTests.Field(100, 100), [At(100, ExpeditionReader.DetonatorPath, 20, 60)]), 0, Monoliths());

        Assert.Equal((50, 20), (watch.View.PlacementPct, watch.View.RadiusPct));
        Assert.Equal(135f, watch.View.EffDist, 3);
        Assert.Equal(36f, watch.View.EffRadius, 3);
    }

    [Fact]
    public void AGateIsKeptWhileItsFlagReads_AndItsHoleIsTheSolidBlockBesideIt()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        ulong gate = 0x0000_0600_0040_0000;
        ulong blockage = gate + 0x10_000;
        MonolithFixture.PlaceEntity(fake, schema, gate, 800, GatePath, [("TriggerableBlockage", blockage)]);
        fake.Place(blockage, new byte[0x40]);
        fake.Place<byte>(blockage + (ulong)schema.Structs["TriggerableBlockage"].OffsetOf("IsBlocked"), 1);

        // A corridor with a solid plug where the gate stands.
        var rows = new string[20];
        for (int y = 0; y < 20; y++)
        {
            rows[y] = y is >= 8 and <= 11 ? string.Concat(new string('.', 48), "###", new string('.', 49)) : new string('.', 100);
        }

        TerrainGrid grid = ExpeditionPathsTests.Grid(rows);
        var entity = new WorldEntity(800, gate, GatePath, EntityKind.Unknown, 49f * MapView.WorldToGrid, 9f * MapView.WorldToGrid, 0f);
        ExpeditionWatch watch = Make(fake, schema);

        watch.Service(Snapshot(grid, [At(100, ExpeditionReader.DetonatorPath, 10, 10), entity]), 0, Monoliths());
        ExpeditionGateView shut = Assert.Single(watch.View.Gates);
        Assert.True(shut.Blocked);
        Assert.Equal(12, shut.Footprint.Count);
        Assert.Contains("1 gates", watch.View.Status, StringComparison.Ordinal);

        // Opened: a doorway, no hole.
        fake.Place<byte>(blockage + (ulong)schema.Structs["TriggerableBlockage"].OffsetOf("IsBlocked"), 0);
        watch.Service(Snapshot(grid, [At(100, ExpeditionReader.DetonatorPath, 10, 10), entity]), ExpeditionWatch.ScanMs, Monoliths());
        ExpeditionGateView open = Assert.Single(watch.View.Gates);
        Assert.False(open.Blocked);
        Assert.Empty(open.Footprint);

        // Remembered from memory, its flag unreadable: the last state stands.
        watch.Service(
            Snapshot(grid, [At(100, ExpeditionReader.DetonatorPath, 10, 10), entity with { RememberedForMs = 2000 }]),
            ExpeditionWatch.ScanMs * 2, Monoliths());
        Assert.False(Assert.Single(watch.View.Gates).Blocked);

        // The flood on its own: nothing solid within reach is no hole at all.
        Assert.Empty(ExpeditionWatch.FloodFootprint(grid, 10, 2));
        Assert.Equal(12, ExpeditionWatch.FloodFootprint(grid, 46, 9).Count);
    }
}
