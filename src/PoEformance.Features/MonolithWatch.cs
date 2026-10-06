using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One recipe a monolith can roll, with what its reward is worth.</summary>
public sealed record MonolithCandidate(RuneshapeRecipe Recipe, RunecraftPrice Price)
{
    /// <summary>The whole reward's worth in Exalted, or null.</summary>
    public double? Total => Price.Total;

    /// <summary>What to call the reward: its English name, else the game's wording for a rolled one.</summary>
    public string Reward => Recipe.HasReward
        ? Recipe.RewardName.Length > 0 ? Recipe.RewardName : Recipe.RewardPath
        : Recipe.Description.Length > 0 ? Recipe.Description : Recipe.Id;
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

    /// <summary>One line for the tab's header: what it is and what it is worth.</summary>
    public string Headline
    {
        get
        {
            string worth = Priced ? $"{RunecraftPrices.Format(Best)}" : "unpriced";
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
    private MonolithsView _view = MonolithsView.None("runecraft prices off");

    private readonly Dictionary<uint, MonolithView> _known = [];
    private uint _areaHash;
    private bool _areaTagsRead;
    private IReadOnlySet<int>? _areaTags;
    private long _scannedAt = long.MinValue;
    private PriceBook? _pricedWith;
    private RecipeCatalog? _catalogWas;
    private RewardCatalog? _rewardsWas;

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

    /// <summary>The newest answer. Never blocks, never null.</summary>
    public MonolithsView View => Volatile.Read(ref _view);

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
        bool due = nowMs - _scannedAt >= ScanMs || nowMs < _scannedAt
                   || !ReferenceEquals(book, _pricedWith)
                   || !ReferenceEquals(catalog, _catalogWas)
                   || !ReferenceEquals(rewards, _rewardsWas);
        if (!due)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _view, Scan(snapshot, book, catalog, rewards));
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
    }

    /// <summary>Whether an entity is a Runecraft monolith's device.</summary>
    public static bool IsMonolith(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.MapIcon.StartsWith(MonolithReader.IconStem, StringComparison.Ordinal)
               || entity.Path.Contains(MonolithReader.DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    private MonolithsView Scan(WorldSnapshot snapshot, PriceBook book, RecipeCatalog catalog, RewardCatalog rewards)
    {
        var views = new List<MonolithView>();
        WorldEntity? player = snapshot.Player;
        int areaLevel = snapshot.AreaLevel;
        var read = 0;
        var remembered = 0;

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
                    MonolithView kept = old with { Listed = false, Distance = distance, MapIcon = entity.MapIcon };
                    _known[entity.Id] = kept;
                    views.Add(kept);
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
            views.Add(ReadOne(entity, distance, areaLevel, book, catalog, rewards));
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
              + (_areaTags is null ? ", area tags unread" : $", {_areaTags.Count} area tags");

        return new MonolithsView(views, status, maxBest);
    }

    private MonolithView ReadOne(
        WorldEntity entity, float distance, int areaLevel, PriceBook book, RecipeCatalog catalog, RewardCatalog rewards)
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

        var view = new MonolithView(
            entity.Id, entity.Address, entity.Path, entity.MapIcon,
            entity.WorldX, entity.WorldY, entity.TerrainHeight, distance, Listed: true,
            station, states, candidates, best, bestOffered, anchor);
        _known[entity.Id] = view;
        return view;
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
    }
}
