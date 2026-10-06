using System.Globalization;
using System.Numerics;
using System.Text;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One thing the planner knows about, for the tab and the map.</summary>
/// <param name="Id">The entity's id.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Grid">Where, in grid cells.</param>
/// <param name="Z">The ground under it, for the map.</param>
/// <param name="Info">The reward icon, the relic's mods, the monolith's anchor - whatever names it.</param>
/// <param name="Value">Its routing weight in Exalted, 0 when it has none.</param>
/// <param name="Primary">Whether the tour goes to it.</param>
/// <param name="Tier">A marker's height tier, or empty.</param>
public sealed record ExpeditionTargetView(
    uint Id, ExpeditionKind Kind, Vector2 Grid, float Z, string Info, double Value, bool Primary, string Tier);

/// <summary>A path-blocker: where it stands, whether it is shut, and the hole it punches in the grid.</summary>
public sealed record ExpeditionGateView(Vector2 Grid, float Z, bool Blocked, IReadOnlyList<(int X, int Y)> Footprint);

/// <summary>Everything the expedition tab and the map draw, as last scanned. Immutable, published whole.</summary>
public sealed record ExpeditionView(
    string Status,
    bool HasDetonator,
    Vector2 Detonator,
    float DetonatorZ,
    bool Activated,
    int Total,
    int Placed,
    string CountsSource,
    bool IsGrand,
    bool IsLogbook,
    int PlacementPct,
    int RadiusPct,
    float EffDist,
    float EffRadius,
    IReadOnlyList<ExpeditionTargetView> Targets,
    IReadOnlyList<PlanProp> Props,
    IReadOnlyList<string> UnmatchedProps,
    IReadOnlyList<ExpeditionGateView> Gates,
    IReadOnlyList<Vector2> Charges,
    PlanResult Route,
    bool Stale,
    bool Computing,
    int NextIndex)
{
    public static ExpeditionView None(string status)
        => new(status, false, default, 0, false, 0, 0, "none", false, false, 0, 0, 0, 0, [], [], [], [], [], PlanResult.Empty, true, false, 0);

    /// <summary>Whether there is anything to plan: a detonator that has not been pressed.</summary>
    public bool CanPlan => HasDetonator && !Activated;

    /// <summary>Charges still to place.</summary>
    public int Remaining => Math.Max(0, Total - Placed);

    /// <summary>Whether the counts came from the game rather than the manual total.</summary>
    public bool CountsKnown => !string.Equals(CountsSource, "manual", StringComparison.Ordinal);
}

/// <summary>
/// Serves the expedition route planner from the reader thread: the scan, the counts, the
/// plan on request, the view.
/// </summary>
/// <remarks>
/// TWO THREADS AND ONE OWNER. Everything here is owned by the reader thread: the target cache,
/// the counts, the fingerprint, the route. The overlay reads the immutable view and asks for a
/// plan with <see cref="RequestRun"/>, which only raises a flag; the next service builds the
/// plan's inputs from the state it owns and hands them to a background task, and the task's
/// result is taken up by a later service. So the plan never reads live state and the state is
/// never touched off its thread - the reference plugin ran the plan on a task for the same
/// reason (its "Run" froze the interface before it did) and snapshotted its inputs by hand.
///
/// THE TARGET CACHE ACCUMULATES PER AREA. Walking the map takes targets out of the game's
/// list and brings them back; a route recomputed on every such change would be a different
/// route every few seconds. So a target seen once stays until the area changes, the fingerprint
/// that decides whether the plan is STALE is taken over the cache rather than over this scan,
/// and movement alone never re-plans - only a new target, a resolved value, a changed knob, a
/// changed budget.
///
/// THE COUNTS, in order of trust: the controller the ServerData holds (range-independent and
/// authoritative - a cancelled charge comes off at once), the counter widget's remaining text
/// plus the charges counted as entities (while standing at the detonator), the charges counted
/// as entities under the manual total. The fallbacks drop as charges leave the game's list, so
/// only they are clamped to never regress; the controller is trusted directly.
/// </remarks>
public sealed class ExpeditionWatch
{
    /// <summary>How often the area is re-scanned.</summary>
    public const long ScanMs = 500;

    /// <summary>The one-cell slack under the hop a stepping stone is laid at - see <see cref="PlanInputs.StepDist"/>.</summary>
    public const float StepMargin = 1f;

    private const int GateFloodRadius = 36;
    private const int GateFloodMostCells = 1200;
    private const int GateDiskRadius = 7;

    private const string MonolithPath = "Expedition2Encounter";
    private const string PropLeague = "Leagues/Expedition/";
    private const string PropObjects = "/Objects/";
    private const string ChestPath = "Metadata/Chests/LeaguesExpedition";

    /// <summary>What the cache keeps about one target.</summary>
    private readonly record struct Cached(
        ExpeditionKind Kind, Vector2 Grid, float Z, float WorldZ, string Info, double Value,
        double Reward = 0, int Sockets = 0, int Waves = 0, double Uplift = 0, int RuneId = -1);

    private readonly record struct Gate(uint Id, Vector2 Grid, float Z, bool Blocked);

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly ExpeditionReader _expedition;

    private readonly int _grandPlacement;
    private readonly int _normalPlacement;
    private readonly int _grandRadius;
    private readonly int _normalRadius;
    private readonly int _grandThreshold;

    private ExpeditionSettings _settings = ExpeditionSettings.Default;
    private ExpeditionView _view = ExpeditionView.None("expedition planner off");
    private IReadOnlyList<uint> _plannedOrder = [];

    // The area, as accumulated.
    private uint _areaHash;
    private long _scannedAt = long.MinValue;
    private readonly Dictionary<uint, Cached> _cache = [];
    private readonly HashSet<uint> _placedIds = [];
    private readonly Dictionary<uint, Vector2> _placedAt = [];
    private readonly List<Gate> _gates = [];
    private readonly Dictionary<uint, List<(int X, int Y)>> _footprints = [];
    private readonly List<PlanProp> _props = [];
    private readonly SortedSet<string> _unmatched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int X, int Y)> _doors = [];
    private bool _hasDetonator;
    private Vector2 _detonator;
    private float _detonatorZ;
    private bool _activated;
    private int _placementPct;
    private int _radiusPct;
    private bool _logbook;
    private int _chests;

    // The counts.
    private ExpeditionCounts? _counts;
    private bool _hudResolved;
    private int _hudTotal;
    private int _hudRemaining;
    private int _placedMax;

    // The plan.
    private PlanResult _route = PlanResult.Empty;
    private string _routeFingerprint = string.Empty;
    private string _fingerprint = string.Empty;
    private bool _runRequested;
    private volatile bool _computing;
    private readonly object _pendingLock = new();
    private (PlanResult Result, string Fingerprint, uint Area)? _pending;

    public ExpeditionWatch(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _expedition = new ExpeditionReader(reader, schema, new UiElementReader(reader, schema));

        StructDef physics = schema.Structs["ExpeditionPhysics"];
        _grandPlacement = (int)physics.Constants["GrandPlacement"];
        _normalPlacement = (int)physics.Constants["NormalPlacement"];
        _grandRadius = (int)physics.Constants["GrandRadius"];
        _normalRadius = (int)physics.Constants["NormalRadius"];
        _grandThreshold = (int)physics.Constants["GrandChargeThreshold"];
    }

    /// <summary>The settings. Replaced whole, from whichever thread saved them.</summary>
    public ExpeditionSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value);
        }
    }

    /// <summary>The newest answer. Never blocks, never null.</summary>
    public ExpeditionView View => Volatile.Read(ref _view);

    /// <summary>The monoliths' ids in the plan's detonation order - what the rune chain plan takes. Empty without a plan.</summary>
    public IReadOnlyList<uint> PlannedOrder => Volatile.Read(ref _plannedOrder);

    /// <summary>Asks for a plan. The next service launches it; a plan already cooking is left to finish.</summary>
    public void RequestRun() => Volatile.Write(ref _runRequested, true);

    /// <summary>Reads the area once. Called on the reader thread with the frame's snapshot.</summary>
    public void Service(WorldSnapshot snapshot, long nowMs, MonolithsView monoliths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(monoliths);

        ExpeditionSettings settings = Volatile.Read(ref _settings);
        if (!settings.Enabled)
        {
            Forget();
            Volatile.Write(ref _view, ExpeditionView.None("expedition planner off"));
            return;
        }

        if (!snapshot.InGame)
        {
            Forget();
            Volatile.Write(ref _view, ExpeditionView.None("not in an area"));
            return;
        }

        if (snapshot.AreaHash != _areaHash)
        {
            Forget();
            _areaHash = snapshot.AreaHash;
        }

        TakeUpPending(settings);

        // The never-scanned mark is tested on its own: subtracting long.MinValue wraps negative,
        // and read as "not due yet" that leaves the planner blind for the whole session.
        bool due = _scannedAt == long.MinValue || nowMs - _scannedAt >= ScanMs || nowMs < _scannedAt;
        if (due)
        {
            try
            {
                Scan(snapshot, monoliths, settings);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _view, ExpeditionView.None($"scan failed: {exception.Message}"));
                _scannedAt = nowMs;
                return;
            }

            _scannedAt = nowMs;
        }

        if (Volatile.Read(ref _runRequested) && !_computing && _hasDetonator)
        {
            Volatile.Write(ref _runRequested, false);
            Launch(snapshot, settings);
        }
        else if (Volatile.Read(ref _runRequested) && !_hasDetonator)
        {
            Volatile.Write(ref _runRequested, false);
        }

        Volatile.Write(ref _view, Compose(settings));
    }

    /// <summary>The scan: every expedition thing in the snapshot into the cache, then the counts.</summary>
    private void Scan(WorldSnapshot snapshot, MonolithsView monoliths, ExpeditionSettings settings)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);

        if (_expedition.MapMods(chain.AreaInstance) is { } mapMods)
        {
            _placementPct = mapMods.Placement;
            _radiusPct = mapMods.Radius;
        }

        _logbook = IsLogbookArea(snapshot.Area.Id);

        // The monoliths, by entity id, from the chain-valued view.
        var monolithById = new Dictionary<uint, MonolithView>(monoliths.Monoliths.Count);
        foreach (MonolithView view in monoliths.Monoliths)
        {
            monolithById[view.EntityId] = view;
        }

        _props.Clear();
        _unmatched.Clear();
        _doors.Clear();
        _chests = 0;
        var blockers = new List<(uint Id, Vector2 Grid, float Z, bool Blocked)>();

        foreach (WorldEntity entity in snapshot.Entities)
        {
            string path = entity.Path;
            if (path.Length == 0)
            {
                continue;
            }

            var grid = new Vector2(entity.WorldX / MapView.WorldToGrid, entity.WorldY / MapView.WorldToGrid);
            float z = entity.TerrainHeight;
            bool live = !entity.IsRemembered;

            if (path.Contains(ExpeditionReader.GatePath, StringComparison.OrdinalIgnoreCase))
            {
                bool? blocked = live ? _expedition.IsBlocked(entity.Address) : null;
                if (blocked is { } shut)
                {
                    blockers.Add((entity.Id, grid, z, shut));
                }
                else if (_gates.Exists(g => g.Id == entity.Id))
                {
                    Gate old = _gates.Find(g => g.Id == entity.Id);
                    blockers.Add((entity.Id, grid, z, old.Blocked));
                }

                // Falls through: the Gully blocker is both the gate and a remnant.
            }

            // A prop with a rule is free coverage and nothing else; one without falls through,
            // because the net is wide and swallowing an entity would delete a target silently.
            // It is listed as unmatched only when nothing below claims it either - a logbook
            // remnant lives under the same Objects folder and is not a missing rule.
            var unmatchedProp = false;
            if (path.Contains(PropLeague, StringComparison.OrdinalIgnoreCase)
                && path.Contains(PropObjects, StringComparison.OrdinalIgnoreCase))
            {
                float radius = settings.PropRadiusFor(path);
                if (radius > 0f)
                {
                    _props.Add(new PlanProp(grid, radius));
                    continue;
                }

                unmatchedProp = true;
            }

            if (path.Equals(ExpeditionReader.DetonatorPath, StringComparison.OrdinalIgnoreCase))
            {
                _hasDetonator = true;
                _detonator = grid;
                _detonatorZ = z;
                if (!_activated && live && _expedition.DetonatorActivated(entity.Address))
                {
                    _activated = true;
                }

                continue;
            }

            if (path.Equals(ExpeditionReader.ExplosivePath, StringComparison.OrdinalIgnoreCase))
            {
                _placedIds.Add(entity.Id);
                _placedAt[entity.Id] = grid;
                continue;
            }

            if (path.Contains(MonolithPath, StringComparison.OrdinalIgnoreCase))
            {
                if (monolithById.TryGetValue(entity.Id, out MonolithView? mono) && (mono.Foreign || mono.Collected))
                {
                    _cache.Remove(entity.Id);
                    continue;
                }

                double value = mono is null ? 0 : Math.Max(mono.Best, mono.Joint);
                double reward = mono?.Best ?? 0;
                int sockets = mono?.HoleCount ?? 0;
                int waves = mono is { ExpectedWaves: > 0 } ? mono.ExpectedWaves : Math.Max(1, sockets);
                double uplift = mono?.RouteUplift ?? 0;
                int rune = mono?.RouteRune ?? -1;
                if (value <= 0 && _cache.TryGetValue(entity.Id, out Cached old) && old.Kind == ExpeditionKind.Monolith)
                {
                    // Out of the bubble, or not yet priced: the last known reading stands.
                    value = old.Value;
                    reward = old.Reward;
                    if (sockets <= 0)
                    {
                        sockets = old.Sockets;
                    }

                    if (old.Waves > 0)
                    {
                        waves = old.Waves;
                        uplift = old.Uplift;
                        rune = old.RuneId;
                    }
                }

                _cache[entity.Id] = new Cached(
                    ExpeditionKind.Monolith, grid, z, entity.WorldZ, mono?.Anchor ?? string.Empty,
                    value, reward, sockets, waves, uplift, rune);
                continue;
            }

            if (path.StartsWith(ChestPath, StringComparison.OrdinalIgnoreCase))
            {
                _chests++;
                continue;
            }

            if (path.Contains(ExpeditionReader.SentinelPath, StringComparison.OrdinalIgnoreCase))
            {
                _cache[entity.Id] = new Cached(ExpeditionKind.Sentinel, grid, z, entity.WorldZ, "sentinel", 0);
                continue;
            }

            if (path.Contains(ExpeditionReader.MarkerPath, StringComparison.OrdinalIgnoreCase))
            {
                string icon = entity.MapIcon.Length > 0 ? entity.MapIcon : "marker";
                _cache[entity.Id] = new Cached(ExpeditionKind.Marker, grid, z, entity.WorldZ, icon, 0);
                continue;
            }

            if (ExpeditionRelics.TryMatchLogbookRemnant(path, out _, out string typeMod))
            {
                string mods = live ? string.Join(';', _expedition.ModIds(entity.Address)) : string.Empty;
                if (mods.Length == 0 && _cache.TryGetValue(entity.Id, out Cached known) && known.Kind == ExpeditionKind.Remnant)
                {
                    mods = known.Info;
                }

                if (mods.Length == 0)
                {
                    mods = typeMod;
                }

                _cache[entity.Id] = new Cached(ExpeditionKind.Remnant, grid, z, entity.WorldZ, mods, 0);
                continue;
            }

            if (path.Contains(ExpeditionReader.RelicPath, StringComparison.OrdinalIgnoreCase))
            {
                // The mods can read empty before the component resolves: keep the last non-empty set.
                string mods = live ? string.Join(';', _expedition.ModIds(entity.Address)) : string.Empty;
                if (mods.Length == 0 && _cache.TryGetValue(entity.Id, out Cached known) && known.Kind == ExpeditionKind.Remnant)
                {
                    mods = known.Info;
                }

                _cache[entity.Id] = new Cached(ExpeditionKind.Remnant, grid, z, entity.WorldZ, mods, 0);
                continue;
            }

            // A doorway: the grid marks it solid, the chain walks through it. The reference's
            // generic convenience, five cells square around the entity.
            if (path.Contains("Door", StringComparison.OrdinalIgnoreCase))
            {
                MarkDoor(grid);
            }

            if (unmatchedProp)
            {
                _unmatched.Add(path);
            }
        }

        // The gates: a shut one's hole is flood-filled from the raw grid and memoised while shut;
        // an open one is a doorway. The gate's OWN cells are never a door while it is shut.
        _gates.Clear();
        TerrainGrid? terrain = snapshot.Terrain;
        foreach ((uint id, Vector2 grid, float z, bool blocked) in blockers)
        {
            if (blocked)
            {
                if (terrain is not null && !_footprints.ContainsKey(id))
                {
                    _footprints[id] = FloodFootprint(terrain, (int)MathF.Round(grid.X), (int)MathF.Round(grid.Y));
                }
            }
            else
            {
                _footprints.Remove(id);
                MarkDoor(grid);
            }

            _gates.Add(new Gate(id, grid, z, blocked));
        }

        foreach (Gate gate in _gates)
        {
            if (gate.Blocked)
            {
                UnmarkDoor(gate.Grid);
            }
        }

        // The counts.
        _counts = _expedition.Counts(snapshot.ServerData, chain.UiRoot);
        int placedFromEntities = _placedIds.Count;
        _hudResolved = false;
        if (_counts is null && _expedition.HudRemaining(chain.UiRoot) is { } remaining)
        {
            _hudResolved = true;
            _hudRemaining = remaining;

            // The total is fixed for the map, so remaining plus placed converges to it; the
            // running maximum means a late-opened planner can never shrink it.
            _hudTotal = Math.Max(_hudTotal, remaining + placedFromEntities);
        }

        int rawPlaced = _counts is { } counts ? counts.Placed
            : _hudResolved ? Math.Max(0, _hudTotal - _hudRemaining)
            : placedFromEntities;
        _placedMax = _counts is not null ? rawPlaced : Math.Max(_placedMax, rawPlaced);

        _fingerprint = Fingerprint(settings);
    }

    private void MarkDoor(Vector2 grid)
    {
        int cx = (int)MathF.Round(grid.X);
        int cy = (int)MathF.Round(grid.Y);
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                _doors.Add((cx + dx, cy + dy));
            }
        }
    }

    private void UnmarkDoor(Vector2 grid)
    {
        int cx = (int)MathF.Round(grid.X);
        int cy = (int)MathF.Round(grid.Y);
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                _doors.Remove((cx + dx, cy + dy));
            }
        }
    }

    /// <summary>
    /// The connected solid region a blocker punches into the grid, 4-connected from a solid
    /// seed near its cell, bounded to a window; a disk when it spills into a permanent wall.
    /// </summary>
    public static List<(int X, int Y)> FloodFootprint(TerrainGrid terrain, int cx, int cy)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int sx = -1;
        int sy = -1;
        for (int r = 0; r <= 4 && sx < 0; r++)
        {
            for (int dy = -r; dy <= r && sx < 0; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && y >= 0 && x < terrain.Width && y < terrain.Height && !terrain.IsWalkable(x, y))
                    {
                        sx = x;
                        sy = y;
                        break;
                    }
                }
            }
        }

        if (sx < 0)
        {
            return [];
        }

        var seen = new HashSet<(int, int)> { (sx, sy) };
        var stack = new Stack<(int X, int Y)>();
        stack.Push((sx, sy));
        var cells = new List<(int X, int Y)>();
        while (stack.Count > 0)
        {
            (int x, int y) = stack.Pop();
            if (Math.Abs(x - cx) > GateFloodRadius || Math.Abs(y - cy) > GateFloodRadius)
            {
                continue;
            }

            cells.Add((x, y));
            if (cells.Count > GateFloodMostCells)
            {
                return Disk(cx, cy, GateDiskRadius);
            }

            Try(x + 1, y);
            Try(x - 1, y);
            Try(x, y + 1);
            Try(x, y - 1);
        }

        return cells;

        void Try(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= terrain.Width || ny >= terrain.Height || seen.Contains((nx, ny)) || terrain.IsWalkable(nx, ny))
            {
                return;
            }

            seen.Add((nx, ny));
            stack.Push((nx, ny));
        }
    }

    private static List<(int X, int Y)> Disk(int cx, int cy, int r)
    {
        var cells = new List<(int X, int Y)>();
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                if ((dx * dx) + (dy * dy) <= r * r && cx + dx >= 0 && cy + dy >= 0)
                {
                    cells.Add((cx + dx, cy + dy));
                }
            }
        }

        return cells;
    }

    /// <summary>A Logbook area: the WorldArea ids every logbook and its sub-areas carry.</summary>
    public static bool IsLogbookArea(string? areaId)
        => areaId is { Length: > 0 }
           && (areaId.StartsWith("ExpeditionLogBook_", StringComparison.OrdinalIgnoreCase)
               || areaId.StartsWith("ExpeditionSubArea_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Charges to plan with: the controller, the counter, the manual total.</summary>
    private int EffectiveTotal(ExpeditionSettings settings)
        => _counts is { Total: > 0 } counts ? counts.Total
            : _hudResolved && _hudTotal > 0 ? _hudTotal
            : settings.ManualTotal;

    private bool CountsResolved => _counts is { Total: > 0 } || (_hudResolved && _hudTotal > 0);

    /// <summary>
    /// Grand physics: a Logbook by its area id, else a confirmed total at or over the threshold.
    /// </summary>
    /// <remarks>
    /// Normal when unconfirmed, because it is the safe direction - 90 under 108, 30 under 37
    /// never proposes a point the game refuses - and it flips the moment the counts resolve.
    /// The charge count is a PROXY for the engine's area-type test and fails both ways: a
    /// Lush Isle logbook read as normal and drew its ring too small, and a normal map pushed to
    /// ten charges by the atlas would claim Grand reach. The area id is the authoritative half.
    /// </remarks>
    private bool IsGrand(ExpeditionSettings settings)
        => _logbook || (CountsResolved && EffectiveTotal(settings) >= _grandThreshold);

    private float EffDist(ExpeditionSettings settings)
        => (IsGrand(settings) ? _grandPlacement : _normalPlacement) * (1f + (_placementPct / 100f));

    private float EffRadius(ExpeditionSettings settings)
        => (IsGrand(settings) ? _grandRadius : _normalRadius) * (1f + (_radiusPct / 100f));

    /// <summary>Marker pole height above the ground - the tier signal.</summary>
    private static float PoleOffset(in Cached t) => t.WorldZ - t.Z;

    /// <summary>The height of the commonest marker pole - the tiny throwaway flags. NaN with none.</summary>
    private float MarkerBaseline()
    {
        var counts = new Dictionary<int, int>();
        var bestKey = 0;
        var bestCount = 0;
        var any = false;
        foreach (Cached t in _cache.Values)
        {
            if (t.Kind != ExpeditionKind.Marker)
            {
                continue;
            }

            any = true;
            int key = (int)MathF.Round(PoleOffset(t));
            counts.TryGetValue(key, out int c);
            c++;
            counts[key] = c;
            if (c > bestCount)
            {
                bestCount = c;
                bestKey = key;
            }
        }

        return any ? bestKey : float.NaN;
    }

    /// <summary>
    /// A marker's value by how tall its pole stands over the tiny baseline: the game fixes
    /// each reward tier's pole height, so taller is better and the tiny swarm weighs nothing.
    /// </summary>
    private static double TierWeight(ExpeditionSettings s, float poleOffset, float baseline, out string tier)
    {
        if (float.IsNaN(baseline))
        {
            tier = "white";
            return s.MarkerWhite;
        }

        float delta = baseline - poleOffset;
        if (delta >= 45f)
        {
            tier = "logbook";
            return s.MarkerLogbook;
        }

        if (delta >= 24f)
        {
            tier = "gold";
            return s.MarkerGold;
        }

        if (delta >= 19f)
        {
            tier = "magic";
            return s.MarkerMagic;
        }

        if (delta >= 4f)
        {
            tier = "white";
            return s.MarkerWhite;
        }

        tier = "tiny";
        return 0;
    }

    private static Dictionary<string, float> RelicMap(ExpeditionSettings settings)
    {
        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (RelicWeight weight in settings.RelicWeights)
        {
            map[weight.Mod] = weight.Weight;
        }

        return map;
    }

    private static double RelicNet(string mods, Dictionary<string, float> weights)
        => mods.Length == 0 || weights.Count == 0
            ? 0
            : ExpeditionRelics.NetWeight(mods.Split(';', StringSplitOptions.RemoveEmptyEntries), weights);

    /// <summary>
    /// The cache as the planner's targets: the weight per kind, anchor or pickup, the Sentinel
    /// pinned, then the two fallbacks that keep a map from going unplanned.
    /// </summary>
    private List<PlanTarget> Targets(ExpeditionSettings s, bool coverageMode, float baseline, bool sentinelWorthwhile, List<string>? trace)
    {
        var targets = new List<PlanTarget>();
        Dictionary<string, float> relics = RelicMap(s);
        var monolithIdx = new List<int>();
        var anyWorthTheWalk = false;

        foreach ((uint id, Cached t) in _cache)
        {
            double w = 0;
            var primary = false;
            var sentinel = false;
            switch (t.Kind)
            {
                case ExpeditionKind.Monolith:
                    // "Min ex" decides DETOUR-worthiness, not admission: under it the monolith
                    // stays a pickup, captured when a blast covers it, never struck off - and it
                    // reads the JOINT value, since a cheap recipe with Opulent is worth the walk.
                    // Size is the second way in: the big monolith is where the chain cashes out.
                    if (t.Value > 0)
                    {
                        w = t.Value;
                        bool worth = t.Value >= s.MonolithMinEx;
                        bool big = s.MonolithMinSockets > 0 && t.Sockets >= s.MonolithMinSockets;
                        anyWorthTheWalk |= worth;
                        primary = worth || big;
                        trace?.Add($"[gate] monolith {Fmt(t.Grid)} recipe {t.Reward:F0} ex, with rune {t.Value:F0} ex, {t.Sockets} sockets => "
                                   + (primary ? "ANCHOR" : "pickup only"));
                    }

                    break;

                case ExpeditionKind.Marker:
                    // Normal: by pole height. Grand: those flags are inert and the reward icon
                    // profile weighs them instead, so the height weights must not leak in.
                    w = coverageMode ? TierWeight(s, PoleOffset(t), baseline, out _) : s.RewardWeightOf(t.Info);
                    break;

                case ExpeditionKind.Sentinel:
                    if (sentinelWorthwhile)
                    {
                        w = (s.MarkerLogbook * 1.5) + 1;
                        primary = true;
                        sentinel = true;
                    }

                    break;

                case ExpeditionKind.Remnant:
                    double net = RelicNet(t.Info, relics);
                    if (net > 0)
                    {
                        w = net;
                        primary = true;
                    }

                    break;
            }

            if (w <= 0)
            {
                continue;
            }

            if (t.Kind == ExpeditionKind.Monolith)
            {
                monolithIdx.Add(targets.Count);
            }

            targets.Add(new PlanTarget(
                id, t.Kind, t.Grid, t.Z, w, primary, sentinel, t.Waves, t.Uplift, t.RuneId,
                t.Kind == ExpeditionKind.Monolith ? t.Reward : w));
        }

        // Fallback one: nothing clears the price bar. The monoliths still carry runes, and a
        // rune multiplies every pack unearthed after it - so the rune carriers are routed, and
        // only those: anchoring every monolith spends the charges the reorder needs as slack.
        if (!anyWorthTheWalk && monolithIdx.Count > 0)
        {
            var carriers = monolithIdx.FindAll(i => targets[i].Uplift > 0);
            List<int> promote = carriers.Count > 0 ? carriers : monolithIdx;
            foreach (int i in promote)
            {
                targets[i] = targets[i] with { Primary = true };
            }

            trace?.Add($"[fallback] nothing is worth >= {s.MonolithMinEx:F0} ex - promoted {promote.Count} of {monolithIdx.Count} monolith(s) to anchors"
                       + (carriers.Count > 0 ? " (the rune carriers)" : " (all; none carries a rune)"));
        }

        // Fallback two: no anchor at all - a normal expedition without a priced monolith or a
        // weighted relic. Every surviving flag becomes one, so a route is built over them.
        var anyPrimary = false;
        foreach (PlanTarget t in targets)
        {
            if (t.Primary)
            {
                anyPrimary = true;
                break;
            }
        }

        if (!anyPrimary && targets.Count > 0)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i] = targets[i] with { Primary = true };
            }

            trace?.Add($"[fallback] no anchors - promoted {targets.Count} target(s) to route drivers");
        }

        return targets;
    }

    private static string Fmt(Vector2 p) => $"({p.X.ToString("F0", CultureInfo.InvariantCulture)},{p.Y.ToString("F0", CultureInfo.InvariantCulture)})";

    /// <summary>Builds the plan's inputs from what this thread owns and hands them to a task.</summary>
    private void Launch(WorldSnapshot snapshot, ExpeditionSettings settings)
    {
        bool grand = IsGrand(settings);
        float effDist = EffDist(settings);
        float effRadius = EffRadius(settings);
        bool coverageMode = !grand;
        float baseline = MarkerBaseline();

        // The Sentinel is worth routing only where a logbook-tier flag exists for it to empower.
        bool sentinelWorthwhile = coverageMode;
        if (sentinelWorthwhile)
        {
            sentinelWorthwhile = false;
            foreach (Cached t in _cache.Values)
            {
                if (t.Kind == ExpeditionKind.Marker)
                {
                    TierWeight(settings, PoleOffset(t), baseline, out string tier);
                    if (tier == "logbook")
                    {
                        sentinelWorthwhile = true;
                        break;
                    }
                }
            }
        }

        var trace = new List<string>();
        List<PlanTarget> targets = Targets(settings, coverageMode, baseline, sentinelWorthwhile, trace);
        RunecraftSettings? chain = ChainSettings;
        var inputs = new PlanInputs
        {
            Paths = new ExpeditionPaths(snapshot.Terrain, _doors, effDist),
            HasDetonator = _hasDetonator,
            Detonator = _detonator,
            DetonatorZ = _detonatorZ,
            Budget = _hasDetonator ? EffectiveTotal(settings) : 0,
            EffDist = effDist,
            EffRadius = effRadius,
            StepDist = Math.Max(1f, effDist - StepMargin),
            Targets = targets,
            Props = [.. _props],
            MarkerCoverageMode = coverageMode,
            MinMarkers = grand ? Math.Max(1, settings.MinMarkersPerSpare) : 1,
            ChainOrder = chain is { ChainEnabled: true },
            ChainBaseEx = chain?.ChainBaseEx ?? 0f,
            Trace = trace,
        };

        if (_props.Count > 0)
        {
            trace.Add($"exploding props: {_props.Count}");
        }

        if (_unmatched.Count > 0)
        {
            trace.Add($"expedition objects matching no prop rule: {string.Join(", ", _unmatched)}");
        }

        string fingerprint = _fingerprint;
        uint area = _areaHash;
        _computing = true;
        _ = Task.Run(() =>
        {
            PlanResult result;
            try
            {
                result = ExpeditionPlanner.Plan(inputs);
            }
            catch (Exception exception)
            {
                // A planner throw must never take the tool down; the trace says what happened.
                trace.Add("plan failed: " + exception.Message);
                result = new PlanResult { Trace = trace };
            }

            lock (_pendingLock)
            {
                _pending = (result, fingerprint, area);
            }
        });
    }

    /// <summary>The rune-chain settings the monolith values were made with, for the order re-scoring.</summary>
    public RunecraftSettings? ChainSettings { get; set; }

    /// <summary>The relic mods' English names, for the tab. Arrives from the data file at start-up.</summary>
    public RelicModNames ModNames { get; set; } = RelicModNames.Empty;

    private void TakeUpPending(ExpeditionSettings settings)
    {
        (PlanResult Result, string Fingerprint, uint Area)? pending;
        lock (_pendingLock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not { } done)
        {
            return;
        }

        // A plan made for the previous area, arriving late: nothing to apply it to.
        if (done.Area != _areaHash)
        {
            _computing = false;
            return;
        }

        _route = done.Result;
        _routeFingerprint = done.Fingerprint;
        _computing = false;
        Volatile.Write(ref _plannedOrder, done.Result.MonolithOrder);

        if (settings.TraceFile && done.Result.Trace.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExpeditionStore.TracePath)!);
                File.WriteAllLines(ExpeditionStore.TracePath, done.Result.Trace);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The trace is an aid; losing it must not cost the plan.
            }
        }
    }

    /// <summary>
    /// Everything the plan depends on, in one string: a change re-plans, movement does not.
    /// </summary>
    private string Fingerprint(ExpeditionSettings s)
    {
        var monoliths = 0;
        double monolithSum = 0;
        var markers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var relics = new List<string>();
        foreach (Cached t in _cache.Values)
        {
            switch (t.Kind)
            {
                case ExpeditionKind.Monolith:
                    monoliths++;
                    monolithSum += t.Value;
                    break;
                case ExpeditionKind.Marker:
                    markers.TryGetValue(t.Info, out int count);
                    markers[t.Info] = count + 1;
                    break;
                case ExpeditionKind.Remnant when t.Info.Length > 0:
                    relics.Add(t.Info);
                    break;
            }
        }

        relics.Sort(StringComparer.Ordinal);
        double propRadii = 0;
        foreach (PlanProp prop in _props)
        {
            propRadii += prop.Radius;
        }

        var sb = new StringBuilder(256);
        sb.Append(_placementPct).Append('|').Append(_radiusPct).Append('|').Append(_logbook).Append('|');
        sb.Append(s.MonolithMinEx.ToString("F1", CultureInfo.InvariantCulture)).Append('|').Append(s.MonolithMinSockets).Append('|');
        sb.Append(s.MinMarkersPerSpare).Append('|');
        sb.Append(s.MarkerWhite).Append(',').Append(s.MarkerMagic).Append(',').Append(s.MarkerGold).Append(',').Append(s.MarkerLogbook).Append('|');
        sb.Append(_props.Count).Append(',').Append(propRadii.ToString("F0", CultureInfo.InvariantCulture)).Append('|');
        foreach (RewardWeight w in s.RewardWeights)
        {
            sb.Append(w.Icon).Append('=').Append(w.Weight.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append('|');
        foreach (RelicWeight w in s.RelicWeights)
        {
            sb.Append(w.Mod).Append('=').Append(w.Weight.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append('|');
        foreach ((string icon, int count) in markers)
        {
            sb.Append(icon).Append(':').Append(count).Append(',');
        }

        sb.Append('|').Append(string.Join(';', relics)).Append('|');
        sb.Append(monoliths).Append(',').Append(monolithSum.ToString("F0", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(_detonator.X.ToString("F0", CultureInfo.InvariantCulture)).Append(',').Append(_detonator.Y.ToString("F0", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(EffectiveTotal(s)).Append('|').Append(_counts is not null).Append('|').Append(_hasDetonator).Append('|');
        RunecraftSettings? chain = ChainSettings;
        sb.Append(chain is { ChainEnabled: true } ? chain.ChainBaseEx.ToString("F1", CultureInfo.InvariantCulture) : "0");
        return sb.ToString();
    }

    private ExpeditionView Compose(ExpeditionSettings settings)
    {
        bool grand = IsGrand(settings);
        float baseline = MarkerBaseline();
        Dictionary<string, float> relics = RelicMap(settings);
        var targets = new List<ExpeditionTargetView>(_cache.Count);
        foreach ((uint id, Cached t) in _cache)
        {
            double value = 0;
            var primary = false;
            var tier = string.Empty;
            switch (t.Kind)
            {
                case ExpeditionKind.Monolith:
                    value = t.Value;
                    primary = value > 0 && (value >= settings.MonolithMinEx || (settings.MonolithMinSockets > 0 && t.Sockets >= settings.MonolithMinSockets));
                    break;
                case ExpeditionKind.Marker:
                    value = grand ? settings.RewardWeightOf(t.Info) : TierWeight(settings, PoleOffset(t), baseline, out tier);
                    break;
                case ExpeditionKind.Remnant:
                    value = RelicNet(t.Info, relics);
                    primary = value > 0;
                    break;
                case ExpeditionKind.Sentinel:
                    primary = !grand;
                    break;
            }

            targets.Add(new ExpeditionTargetView(id, t.Kind, t.Grid, t.Z, t.Info, value, primary, tier));
        }

        targets.Sort((a, b) => b.Value.CompareTo(a.Value));

        var gates = new List<ExpeditionGateView>(_gates.Count);
        foreach (Gate gate in _gates)
        {
            gates.Add(new ExpeditionGateView(
                gate.Grid, gate.Z, gate.Blocked,
                gate.Blocked && _footprints.TryGetValue(gate.Id, out List<(int X, int Y)>? cells) ? cells : []));
        }

        var charges = new List<Vector2>(_placedAt.Count);
        foreach (uint id in _placedIds.Order())
        {
            if (_placedAt.TryGetValue(id, out Vector2 at))
            {
                charges.Add(at);
            }
        }

        int total = EffectiveTotal(settings);
        string source = _counts is { } counts ? counts.Source : _hudResolved ? "counter widget" : "manual";
        var status = new StringBuilder();
        status.Append(_hasDetonator ? _activated ? "detonator pressed - the dig is under way" : "detonator found" : "no detonator in this area");
        status.Append(" · ").Append(_cache.Count).Append(" targets (");
        status.Append(Count(ExpeditionKind.Monolith)).Append(" monoliths, ").Append(Count(ExpeditionKind.Marker)).Append(" markers, ");
        status.Append(Count(ExpeditionKind.Remnant)).Append(" remnants, ").Append(_chests).Append(" chests)");
        if (_props.Count > 0)
        {
            status.Append(" · ").Append(_props.Count).Append(" props");
        }

        if (_gates.Count > 0)
        {
            status.Append(" · ").Append(_gates.Count).Append(" gates");
        }

        return new ExpeditionView(
            status.ToString(), _hasDetonator, _detonator, _detonatorZ, _activated,
            total, _placedMax, source, grand, _logbook, _placementPct, _radiusPct, EffDist(settings), EffRadius(settings),
            targets, [.. _props], [.. _unmatched], gates, charges,
            _route, !string.Equals(_fingerprint, _routeFingerprint, StringComparison.Ordinal), _computing, _placedMax);
    }

    private int Count(ExpeditionKind kind)
    {
        var n = 0;
        foreach (Cached t in _cache.Values)
        {
            if (t.Kind == kind)
            {
                n++;
            }
        }

        return n;
    }

    private void Forget()
    {
        _cache.Clear();
        _placedIds.Clear();
        _placedAt.Clear();
        _gates.Clear();
        _footprints.Clear();
        _props.Clear();
        _unmatched.Clear();
        _doors.Clear();
        _hasDetonator = false;
        _detonator = default;
        _detonatorZ = 0;
        _activated = false;
        _chests = 0;
        _counts = null;
        _hudResolved = false;
        _hudTotal = 0;
        _hudRemaining = 0;
        _placedMax = 0;
        _route = PlanResult.Empty;
        _routeFingerprint = string.Empty;
        _fingerprint = string.Empty;
        _scannedAt = long.MinValue;
        Volatile.Write(ref _plannedOrder, []);
        lock (_pendingLock)
        {
            _pending = null;
        }

        _computing = false;
    }
}
