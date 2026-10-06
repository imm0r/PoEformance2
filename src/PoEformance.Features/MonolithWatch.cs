using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One recipe a monolith can roll, with what its reward is worth and what its rune is.</summary>
/// <param name="Recipe">The combination.</param>
/// <param name="Price">What its reward fetches.</param>
/// <param name="Rune">The rune it would drop on the gold socket, as an Expedition2Runes row, or -1.</param>
/// <param name="ChainEx">What propagating that rune from here is worth, in Exalted. Zero where it adds nothing.</param>
/// <param name="Taken">
/// Whether that rune is already in the chain - locked on another monolith or expected
/// upstream - so taking it again adds nothing, though it would have been worth something.
/// </param>
public sealed record MonolithCandidate(
    RuneshapeRecipe Recipe, RunecraftPrice Price, int Rune = -1, double ChainEx = 0, bool Taken = false)
{
    /// <summary>The whole reward's worth in Exalted, or null.</summary>
    public double? Total => Price.Total;

    /// <summary>Reward and chain together - the number one recipe is chosen by.</summary>
    public double Joint => (Total ?? 0) + ChainEx;

    /// <summary>What to call the reward: its English name, else the game's wording for a rolled one.</summary>
    public string Reward => Recipe.HasReward
        ? Recipe.RewardName.Length > 0 ? Recipe.RewardName : Recipe.RewardPath
        : Recipe.Description.Length > 0 ? Recipe.Description : Recipe.Id;
}

/// <summary>How a committed monolith's rune goes on its map label.</summary>
public enum MonolithRuneLabel
{
    /// <summary>Nothing committed, nothing propagating, or the player took the best-paying recipe: the price says it.</summary>
    None,

    /// <summary>
    /// The player gave up reward for this rune, so the price lies by omission - a small figure
    /// on a monolith worth walking to. The rune takes the price's place: "[5] Opulent".
    /// </summary>
    Replace,

    /// <summary>
    /// Sealed by a reroll, which force-picks at random: no intent to read, and both price and
    /// rune are pinned, so both are shown - "[5] 49 ex | Opulent".
    /// </summary>
    Append,
}

/// <summary>One Runecraft monolith in the area: where it is, what it holds, what it can pay.</summary>
/// <param name="EntityId">The device entity's id - stable for the area, and what a view is remembered by.</param>
/// <param name="Address">The device entity's address as last listed. Stale once the player walks away.</param>
/// <param name="Path">The device's metadata path.</param>
/// <param name="MapIcon">The game's own icon for it: Active, or Deactivated once collected.</param>
/// <param name="WorldX">Where it stands - static for the area.</param>
/// <param name="TerrainHeight">The ground under it, for the map projection.</param>
/// <param name="Distance">From the player at the last scan, in grid cells.</param>
/// <param name="Listed">Whether the game listed it this scan - false for one remembered from earlier.</param>
/// <param name="Station">The RuneStation as last read.</param>
/// <param name="States">The device's own states as last read.</param>
/// <param name="Candidates">What it can roll, most valuable first. One entry once a recipe is committed.</param>
/// <param name="Best">The most valuable candidate's total, or 0 when nothing priced.</param>
/// <param name="BestOffered">
/// The best total among what it OFFERED before a recipe was committed - kept so the map can tell
/// a player who took the money from one who gave it up for a rune. Equal to Best while open.
/// </param>
/// <param name="Anchor">The anchor rune's name, "?" when unresolved, empty on the anchor-less one.</param>
public sealed record MonolithView(
    uint EntityId,
    ulong Address,
    string Path,
    string MapIcon,
    float WorldX,
    float WorldY,
    float TerrainHeight,
    float Distance,
    bool Listed,
    MonolithStation Station,
    MonolithStates States,
    IReadOnlyList<MonolithCandidate> Candidates,
    double Best,
    double BestOffered,
    string Anchor)
{
    /// <summary>Whether the monolith has been collected - the game's icon says so, or the device's state does.</summary>
    public bool Collected
        => MapIcon.EndsWith("Deactivated", StringComparison.Ordinal) || States.LooksCollected;

    /// <summary>The standalone "additional" monolith, collected by hand and never by the chain.</summary>
    public bool Foreign => Station.RecipeMode == 0;

    /// <summary>The anchor-less monolith that offers everything that fits its holes.</summary>
    public bool Unique => Station.Anchorless && Station.HoleCount > 0;

    /// <summary>Sealed by a currency reroll.</summary>
    public bool Rerolled => States.Rerolled;

    /// <summary>Whether the Runeshape Combinations panel is open on this one.</summary>
    public bool PanelOpen => Station.PanelOpen;

    /// <summary>The holes: the station's count, else the (capped) state.</summary>
    public int HoleCount => Station.HoleCount > 0 ? Station.HoleCount : Math.Max(0, States.Sockets);

    /// <summary>Whether anything on it has a price.</summary>
    public bool Priced => Best > 0;

    /// <summary>The rune its committed recipe propagates, as an Expedition2Runes row, or -1 while open.</summary>
    public int LockedRune { get; init; } = -1;

    /// <summary>That rune's name, or empty while nothing is committed or nothing propagates.</summary>
    public string ChosenRune { get; init; } = string.Empty;

    /// <summary>That rune's multiplier here: above 1 gains loot, 1 is neutral or already in the chain, below 1 costs.</summary>
    public double ChosenMult { get; init; } = 1;

    /// <summary>The rune the joint-best recipe would propagate, or -1.</summary>
    public int ChainRune { get; init; } = -1;

    /// <summary>That rune's name, or empty.</summary>
    public string ChainRuneName { get; init; } = string.Empty;

    /// <summary>The chain part of the joint best, in Exalted.</summary>
    public double ChainEx { get; init; }

    /// <summary>The joint-best recipe's id, or empty.</summary>
    public string ChainRecipeId { get; init; } = string.Empty;

    /// <summary>The joint best: reward plus chain. Equal to <see cref="Best"/> while the chain is off.</summary>
    public double Joint { get; init; }

    /// <summary>Packs it is expected to raise: the committed or recommended recipe's length, else its holes.</summary>
    public int ExpectedWaves { get; init; }

    /// <summary>Where it stands in the chain: what is already propagating over it.</summary>
    public ChainSite Site { get; init; }

    /// <summary>
    /// The strongest loot uplift this monolith could propagate, and which rune carries it -
    /// ORDER-INDEPENDENT, for the route planner.
    /// </summary>
    /// <remarks>
    /// Deliberately not the chain figure above: the planner compares candidate tour orders, and
    /// a value already priced for the order in force is the circularity that let a strong rune
    /// sit at the tail and value itself at zero. No duplicate suppression either, for the same
    /// reason - whether a rune repeats depends on what precedes it in the candidate order. A
    /// committed recipe pins the rune; an open monolith reports the best over its offers.
    /// </remarks>
    public double RouteUplift { get; init; }

    public int RouteRune { get; init; } = -1;

    /// <summary>The runes it could still propagate that are worth it, best first, at most a few - for the map.</summary>
    public IReadOnlyList<string> Scout { get; init; } = [];

    /// <summary>Which way the committed rune goes on the map label, if at all.</summary>
    public MonolithRuneLabel RuneOnMap
    {
        get
        {
            if (ChosenRune.Length == 0)
            {
                return MonolithRuneLabel.None;
            }

            // Sealed: both - unless there is no price to show, when "[5]  | Opulent" would read
            // as a rendering fault rather than as missing data.
            if (Rerolled)
            {
                return Priced ? MonolithRuneLabel.Append : MonolithRuneLabel.Replace;
            }

            // Equal or better means the player took the money, and the money is what to say.
            return BestOffered > 0 && Best < BestOffered ? MonolithRuneLabel.Replace : MonolithRuneLabel.None;
        }
    }

    /// <summary>One line for the tab's header: what it is and what it is worth.</summary>
    public string Headline
    {
        get
        {
            string worth = Priced ? $"{RunecraftPrices.Format(Best)}" : "unpriced";
            if (ChainEx > 0 && ChainRuneName.Length > 0)
            {
                worth += $" +{RunecraftPrices.Format(ChainEx)} {ChainRuneName}";
            }

            if (Station.Committed && Candidates.Count > 0)
            {
                return $"[chosen] {Candidates[0].Reward}  ·  {Distance:0}  ·  {worth}";
            }

            if (Unique)
            {
                return $"Unique monolith  ·  {HoleCount} holes  ·  {Distance:0}  ·  best {worth}";
            }

            if (Station.AnchorRune >= 0)
            {
                return $"{Anchor}  ·  hole {Station.AnchorHole + 1}/{HoleCount}  ·  {Distance:0}  ·  best {worth}";
            }

            return $"(anchor ?)  ·  {HoleCount} holes  ·  {Distance:0}";
        }
    }
}

/// <summary>Every monolith in the area, as last scanned. Immutable, published whole.</summary>
/// <param name="Monoliths">Nearest first.</param>
/// <param name="Status">What happened, in words.</param>
/// <param name="MaxBest">The best total among the uncollected ones - what Relative colouring compares against.</param>
public sealed record MonolithsView(IReadOnlyList<MonolithView> Monoliths, string Status, double MaxBest)
{
    public static MonolithsView None(string status) => new([], status, 0);

    /// <summary>Whether there is anything to draw or list.</summary>
    public bool Any => Monoliths.Count > 0;

    /// <summary>The monolith whose panel is open, or null.</summary>
    public MonolithView? Open
    {
        get
        {
            foreach (MonolithView view in Monoliths)
            {
                if (view.PanelOpen)
                {
                    return view;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Serves the area's Runecraft monoliths from the reader thread: found, read, offered, priced.
/// </summary>
/// <remarks>
/// WHAT THIS ADDS TO THE PANEL PRICES: the panel says what ONE monolith offers once it is open.
/// This says what EVERY monolith in the area can roll before anybody walks to it, which is the
/// question a map answers - and the input the route planner will need. The offers are not read
/// off the game: they are RECOMPUTED by the game's own rule from the station's anchor and hole
/// count against the install's recipe table (see <see cref="MonolithOffers"/>), which is how the
/// reference plugin does it and the reason the station offsets matter.
///
/// RUNS ON THE SNAPSHOT the world reader already took: every monolith is an entity with a
/// position and a map icon there, so finding them costs nothing, and only the handful found
/// pay for the station read - once every <see cref="ScanMs"/>, since nothing about a monolith
/// changes faster than a player can choose a recipe. A monolith the game has stopped listing
/// (the player walked out of range) keeps its last reading, as the plugin keeps its cache: the
/// position still draws, the offers still stand, and the address is never followed again.
///
/// COLLECTED IS THE ICON'S WORD. The plugin inferred it from the 'activated' state reaching 7;
/// this tool reads the MinimapIcon, which the game itself flips from Expedition2RemnantActive
/// to Expedition2RemnantDeactivated, and keeps the state as the second opinion.
///
/// THE CHAIN IS VALUED IN A SECOND PASS over the whole area, because what a rune is worth on
/// one monolith depends on every other: a rune committed anywhere is dead everywhere, and what
/// an earlier monolith on the plan propagates is dead downstream. So the stations are read
/// first, the <see cref="RuneChainPlan"/> is built from all of them, and each monolith's offers
/// are then valued where it stands - see <see cref="RuneChain"/> for the model.
/// </remarks>
public sealed class MonolithWatch
{
    /// <summary>How often the stations are re-read.</summary>
    public const long ScanMs = 750;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly EntityReader _entities;
    private readonly MonolithReader _monoliths;
    private readonly Func<string?, string?> _shippedName;

    private RunecraftSettings _settings = RunecraftSettings.Default;
    private RecipeCatalog _catalog = RecipeCatalog.Empty;
    private RewardCatalog _rewards = RewardCatalog.Empty;
    private IReadOnlyList<uint> _order = [];
    private MonolithsView _view = MonolithsView.None("runecraft prices off");

    private readonly Dictionary<uint, MonolithView> _known = [];
    private uint _areaHash;
    private bool _areaTagsRead;
    private IReadOnlySet<int>? _areaTags;
    private long _scannedAt = long.MinValue;
    private PriceBook? _pricedWith;
    private RecipeCatalog? _catalogWas;
    private RewardCatalog? _rewardsWas;
    private RunecraftSettings? _settingsWas;
    private IReadOnlyList<uint>? _orderWas;

    // The weights compiled against the rune rows, kept while neither half changes.
    private RuneChainTable _table = RuneChainTable.Off;
    private RunecraftSettings? _tableSettings;
    private RecipeCatalog? _tableCatalog;

    public MonolithWatch(
        IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, Func<string?, string?>? shippedName = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _entities = new EntityReader(reader, schema);
        _monoliths = new MonolithReader(reader, schema, new StateMachineReader(reader, schema));
        _shippedName = shippedName ?? (_ => null);
    }

    /// <summary>The Runecraft settings - the master switch is theirs. Replaced whole.</summary>
    public RunecraftSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value);
        }
    }

    /// <summary>The install's recipes. Arrives late, from a background read.</summary>
    public RecipeCatalog Catalog
    {
        get => Volatile.Read(ref _catalog);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _catalog, value);
        }
    }

    /// <summary>The install's English names and pictures, for pricing. Arrives late too.</summary>
    public RewardCatalog Rewards
    {
        get => Volatile.Read(ref _rewards);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _rewards, value);
        }
    }

    /// <summary>
    /// The planned detonation order, as device entity ids - the route planner's, when there is
    /// one. Empty means no order, and the chain then counts every other monolith's waves.
    /// </summary>
    public IReadOnlyList<uint> PlannedOrder
    {
        get => Volatile.Read(ref _order);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _order, value);
        }
    }

    /// <summary>The newest answer. Never blocks, never null.</summary>
    public MonolithsView View => Volatile.Read(ref _view);

    /// <summary>The weights as last compiled - what the tab's offers and the panel's rows are valued by.</summary>
    public RuneChainTable Table => Volatile.Read(ref _table);

    /// <summary>Reads the monoliths once. Called on the reader thread with the frame's snapshot.</summary>
    public void Service(WorldSnapshot snapshot, long nowMs, PriceBook book)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(book);

        if (!Volatile.Read(ref _settings).Enabled)
        {
            Forget();
            Volatile.Write(ref _view, MonolithsView.None("runecraft prices off"));
            return;
        }

        if (!snapshot.InGame)
        {
            Forget();
            Volatile.Write(ref _view, MonolithsView.None("not in an area"));
            return;
        }

        if (snapshot.AreaHash != _areaHash)
        {
            Forget();
            _areaHash = snapshot.AreaHash;
        }

        RecipeCatalog catalog = Volatile.Read(ref _catalog);
        RewardCatalog rewards = Volatile.Read(ref _rewards);
        RunecraftSettings settings = Volatile.Read(ref _settings);
        IReadOnlyList<uint> order = Volatile.Read(ref _order);
        bool due = nowMs - _scannedAt >= ScanMs || nowMs < _scannedAt
                   || !ReferenceEquals(book, _pricedWith)
                   || !ReferenceEquals(catalog, _catalogWas)
                   || !ReferenceEquals(rewards, _rewardsWas)
                   || !ReferenceEquals(settings, _settingsWas)
                   || !ReferenceEquals(order, _orderWas);
        if (!due)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _view, Scan(snapshot, book, catalog, rewards, TableFor(settings, catalog), order));
        }
        catch (Exception exception)
        {
            // A stale view beats a dead servicing thread: every station pointer belongs to an
            // object the game can free between two reads.
            Volatile.Write(ref _view, MonolithsView.None($"read failed: {exception.Message}"));
        }

        _scannedAt = nowMs;
        _pricedWith = book;
        _catalogWas = catalog;
        _rewardsWas = rewards;
        _settingsWas = settings;
        _orderWas = order;
    }

    /// <summary>The weights compiled against this catalogue's runes, rebuilt only when either changes.</summary>
    private RuneChainTable TableFor(RunecraftSettings settings, RecipeCatalog catalog)
    {
        if (!ReferenceEquals(settings, _tableSettings) || !ReferenceEquals(catalog, _tableCatalog))
        {
            Volatile.Write(ref _table, RuneChainTable.Build(settings, catalog));
            _tableSettings = settings;
            _tableCatalog = catalog;
        }

        return _table;
    }

    /// <summary>Whether an entity is a Runecraft monolith's device.</summary>
    public static bool IsMonolith(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.MapIcon.StartsWith(MonolithReader.IconStem, StringComparison.Ordinal)
               || entity.Path.Contains(MonolithReader.DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    private MonolithsView Scan(
        WorldSnapshot snapshot, PriceBook book, RecipeCatalog catalog, RewardCatalog rewards,
        RuneChainTable table, IReadOnlyList<uint> order)
    {
        var views = new List<MonolithView>();
        WorldEntity? player = snapshot.Player;
        int areaLevel = snapshot.AreaLevel;
        var read = 0;
        var remembered = 0;

        // Phase one: every monolith read or remembered, nothing valued down the chain yet.
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (!IsMonolith(entity))
            {
                continue;
            }

            float distance = player is null
                ? 0f
                : MathF.Sqrt(Square(entity.WorldX - player.WorldX) + Square(entity.WorldY - player.WorldY)) / MapView.WorldToGrid;

            if (entity.IsRemembered)
            {
                // Out of the bubble: the address is stale, the reading is not. The icon is the
                // one thing the snapshot still carries, so a collection seen late still shows.
                if (_known.TryGetValue(entity.Id, out MonolithView? old))
                {
                    views.Add(old with { Listed = false, Distance = distance, MapIcon = entity.MapIcon });
                    remembered++;
                }

                continue;
            }

            // The area's tags, read once per area off the chain and only when a monolith wants
            // them - a null leaves the gate off, by MonolithReader's rule.
            if (!_areaTagsRead)
            {
                GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
                _areaTags = chain.AreaInstance != 0 ? _monoliths.AreaTags(chain.AreaInstance) : null;
                _areaTagsRead = true;
            }

            read++;
            views.Add(ReadOne(entity, distance, areaLevel, book, catalog, rewards, table));
        }

        // Phase two: the chain over the whole area, from what is committed and what each
        // monolith was expected to propagate LAST scan - a fresh area converges over a few.
        RuneChainPlan plan = RuneChainPlan.Empty;
        if (table.Enabled && views.Count > 0)
        {
            var stands = new ChainStand[views.Count];
            for (var i = 0; i < views.Count; i++)
            {
                MonolithView view = views[i];
                _known.TryGetValue(view.EntityId, out MonolithView? old);
                int expected = view.LockedRune >= 0 ? view.LockedRune : old?.ChainRune ?? -1;
                int waves = old is { ExpectedWaves: > 0 } ? old.ExpectedWaves : view.ExpectedWaves;
                stands[i] = new ChainStand(view.EntityId, view.Foreign || view.Collected, waves, view.LockedRune, expected);
            }

            plan = RuneChainPlan.Build(table, stands, order);
        }

        // Phase three: each monolith valued where it stands, and remembered as such.
        for (var i = 0; i < views.Count; i++)
        {
            MonolithView valued = Value(views[i], table, plan);
            views[i] = valued;
            _known[valued.EntityId] = valued;
        }

        views.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        double maxBest = 0;
        var priced = 0;
        foreach (MonolithView view in views)
        {
            if (view.Collected)
            {
                continue;
            }

            if (view.Priced)
            {
                priced++;
                maxBest = Math.Max(maxBest, view.Best);
            }
        }

        string status = views.Count == 0
            ? "no monoliths in this area"
            : $"{views.Count} monoliths ({read} read, {remembered} remembered), {priced} priced"
              + (catalog.Count == 0 ? ", no recipe catalogue yet" : string.Empty)
              + (_areaTags is null ? ", area tags unread" : $", {_areaTags.Count} area tags")
              + (table.Enabled ? plan.Ordered > 0 ? $", chain over {plan.Ordered} planned" : ", chain unordered" : string.Empty);

        return new MonolithsView(views, status, maxBest);
    }

    /// <summary>
    /// What one monolith's offers are worth down the chain, and which is best all told.
    /// </summary>
    /// <remarks>
    /// A JOINT maximum, not two separate ones: the player picks ONE recipe, so the dearest
    /// reward and the strongest rune usually cannot both be had, and the list is ordered by
    /// what each recipe is worth all told. Nothing is valued on a monolith the game draws no
    /// gold frame on (modes 0 and 3 hand the panel an empty socket vector even though the
    /// station's is populated), because nothing propagates from one.
    /// </remarks>
    private static MonolithView Value(MonolithView view, RuneChainTable table, RuneChainPlan plan)
    {
        MonolithStation station = view.Station;
        ChainSite site = plan.SiteOf(view.EntityId);
        bool chained = table.Enabled && station.FramesASocket && station.GlowSockets.Count > 0 && !view.Foreign;

        string chosen = string.Empty;
        double chosenMult = 1;
        if (chained && view.LockedRune >= 0)
        {
            chosen = table.Name(view.LockedRune);
            chosenMult = table.EffMultAt(site, view.LockedRune, station.Empowered);
        }

        if (!chained || view.Candidates.Count == 0)
        {
            return view with
            {
                Candidates = Unchained(view.Candidates),
                ChosenRune = chosen,
                ChosenMult = chosenMult,
                ChainRune = -1,
                ChainRuneName = string.Empty,
                ChainEx = 0,
                ChainRecipeId = string.Empty,
                Joint = view.Best,
                Site = site,
                Scout = [],
                RouteUplift = 0,
                RouteRune = -1,
            };
        }

        var candidates = new List<MonolithCandidate>(view.Candidates.Count);
        var scouted = new List<(int Rune, double Mult)>();
        double routeUplift = 0;
        int routeRune = -1;
        foreach (MonolithCandidate candidate in view.Candidates)
        {
            int rune = table.Propagated(site, station.GlowSockets, candidate.Recipe.Runes, station.Empowered);
            double chain = plan.ChainEx(table, view.EntityId, rune, candidate.Recipe.Size, station.Empowered);
            bool taken = rune >= 0 && site.Taken(rune) && table.EffMult(rune, station.Empowered) > 1;
            candidates.Add(candidate with { Rune = rune, ChainEx = chain, Taken = taken });

            // Scouting: the runes worth propagating from here, each once, for the map.
            if (rune >= 0)
            {
                double mult = table.EffMultAt(site, rune, station.Empowered);
                if (mult > 1 && !scouted.Exists(s => s.Rune == rune))
                {
                    scouted.Add((rune, mult));
                }

                // For the planner: the best uplift over every framed socket of every offer,
                // with no chain context - see RouteUplift.
                foreach (int socket in station.GlowSockets)
                {
                    int at = candidate.Recipe.RuneAt(socket);
                    double up = at >= 0 ? table.EffMult(at, station.Empowered) - 1 : 0;
                    if (up > routeUplift)
                    {
                        routeUplift = up;
                        routeRune = at;
                    }
                }
            }
        }

        if (view.LockedRune >= 0)
        {
            routeRune = view.LockedRune;
            routeUplift = Math.Max(0, table.EffMult(view.LockedRune, station.Empowered) - 1);
        }

        // A committed monolith has one candidate, so its chain value is what it WILL propagate.
        candidates.Sort((a, b) => b.Joint.CompareTo(a.Joint));
        MonolithCandidate best = candidates[0];

        scouted.Sort((a, b) => b.Mult.CompareTo(a.Mult));
        var scout = new string[Math.Min(scouted.Count, RuneChain.MostScouted)];
        for (var i = 0; i < scout.Length; i++)
        {
            scout[i] = table.Name(scouted[i].Rune);
        }

        return view with
        {
            Candidates = candidates,
            ChosenRune = chosen,
            ChosenMult = chosenMult,
            ChainRune = best.Rune,
            ChainRuneName = best.Rune >= 0 ? table.Name(best.Rune) : string.Empty,
            ChainEx = best.ChainEx,
            ChainRecipeId = best.Recipe.Id,
            Joint = best.Joint,
            ExpectedWaves = best.Recipe.Size > 0 ? best.Recipe.Size : view.ExpectedWaves,
            Site = site,
            Scout = scout,
            RouteUplift = routeUplift,
            RouteRune = routeRune,
        };
    }

    private MonolithView ReadOne(
        WorldEntity entity, float distance, int areaLevel, PriceBook book, RecipeCatalog catalog, RewardCatalog rewards,
        RuneChainTable table)
    {
        Entity? device = _entities.Read(entity.Address);
        ulong machine = device?.Component("StateMachine") ?? 0;
        MonolithStates states = machine != 0 ? _monoliths.States(machine) : new MonolithStates(-1, -1, false, string.Empty);

        ulong stationAt = _monoliths.FindStation(machine, entity.Address, out string why);
        MonolithStation station = stationAt != 0 ? _monoliths.Read(stationAt) : MonolithStation.None(why);

        // The station's count is authoritative; the state is the fallback that the plugin found
        // caps at six - better than no count for the offers of a small monolith.
        int holes = station.HoleCount > 0 ? station.HoleCount : Math.Max(0, states.Sockets);

        List<RuneshapeRecipe> offered = station.Resolved && holes > 0 && (station.Anchorless || station.AnchorRune >= 0)
            ? MonolithOffers.Offered(catalog, holes, station.AnchorRune, station.AnchorHole, areaLevel, station.Anchorless, _areaTags)
            : [];

        var candidates = new List<MonolithCandidate>(offered.Count);
        double bestOffered = 0;
        foreach (RuneshapeRecipe recipe in offered)
        {
            var candidate = new MonolithCandidate(recipe, PriceOf(recipe, book, rewards));
            candidates.Add(candidate);
            if (candidate.Total is { } total)
            {
                bestOffered = Math.Max(bestOffered, total);
            }
        }

        // A committed recipe makes the other offers moot: the monolith is worth what it will
        // pay, not the most it could have. What it offered is kept beside it.
        double best = bestOffered;
        if (station.Committed && catalog.ById(station.SelectedRecipeId) is { } chosen)
        {
            var only = new MonolithCandidate(chosen, PriceOf(chosen, book, rewards));
            candidates = [only];
            best = only.Total ?? 0;
        }

        candidates.Sort((a, b) => (b.Total ?? -1).CompareTo(a.Total ?? -1));

        string anchor = station.Anchorless
            ? string.Empty
            : station.AnchorRune >= 0 ? catalog.RuneName(station.AnchorRune) : "?";

        // What a committed recipe propagates is a fact, read here so the plan can count it
        // everywhere; the best of the gold sockets on its own, with no chain context, since
        // the context is what this feeds.
        int locked = -1;
        int expectedWaves = Math.Max(1, holes);
        if (station.Committed && candidates.Count == 1 && station.FramesASocket)
        {
            RuneshapeRecipe sealedRecipe = candidates[0].Recipe;
            locked = table.Propagated(ChainSite.Alone, station.GlowSockets, sealedRecipe.Runes, station.Empowered);
            expectedWaves = Math.Max(1, sealedRecipe.Size);
        }

        return new MonolithView(
            entity.Id, entity.Address, entity.Path, entity.MapIcon,
            entity.WorldX, entity.WorldY, entity.TerrainHeight, distance, Listed: true,
            station, states, candidates, best, bestOffered, anchor)
        {
            LockedRune = locked,
            Joint = best,
            ExpectedWaves = expectedWaves,
        };
    }

    /// <summary>The candidates with no chain on them, dearest reward first - for a monolith off the chain.</summary>
    private static IReadOnlyList<MonolithCandidate> Unchained(IReadOnlyList<MonolithCandidate> candidates)
    {
        var plain = true;
        foreach (MonolithCandidate candidate in candidates)
        {
            if (candidate.Rune >= 0 || candidate.ChainEx != 0 || candidate.Taken)
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return candidates;
        }

        var stripped = new List<MonolithCandidate>(candidates.Count);
        foreach (MonolithCandidate candidate in candidates)
        {
            stripped.Add(candidate with { Rune = -1, ChainEx = 0, Taken = false });
        }

        stripped.Sort((a, b) => (b.Total ?? -1).CompareTo(a.Total ?? -1));
        return stripped;
    }

    /// <summary>
    /// The same three doors the panel's rows go through, fed from the catalogue instead of a row.
    /// </summary>
    /// <remarks>
    /// The label stands in for what the game would paint: the English name for a fixed reward,
    /// the game's own description for a rolled one - so a reward the install names prices even
    /// where the reward catalogue has not landed yet.
    /// </remarks>
    private RunecraftPrice PriceOf(RuneshapeRecipe recipe, PriceBook book, RewardCatalog rewards)
    {
        var row = new RunecraftRow(
            0, 0,
            recipe.Description.Length > 0 ? recipe.Description : recipe.RewardName,
            recipe.Id, recipe.RewardPath, recipe.RewardName, string.Empty,
            recipe.RewardCount, recipe.RewardGemLevel, recipe.MinLevel, recipe.MaxLevel);
        return RunecraftPrices.Price(book, row, rewards, _shippedName);
    }

    private static float Square(float value) => value * value;

    private void Forget()
    {
        _known.Clear();
        _areaTags = null;
        _areaTagsRead = false;
        _scannedAt = long.MinValue;
        _pricedWith = null;
        _catalogWas = null;
        _rewardsWas = null;
        _settingsWas = null;
        _orderWas = null;
    }
}
