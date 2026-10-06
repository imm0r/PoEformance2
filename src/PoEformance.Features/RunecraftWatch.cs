using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>One recipe row with its price, placed on the screen, ready to draw.</summary>
/// <param name="Where">The row's rectangle this tick, in window pixels - rune icons to right edge.</param>
/// <param name="Text">The row's own text's rectangle, or null when its element did not read.</param>
/// <param name="Label">What the game painted on the row.</param>
/// <param name="Price">What it is worth and which door answered.</param>
/// <param name="Best">Whether this is the most valuable row on screen (ties all count).</param>
public sealed record RunecraftReward(ScreenRect Where, ScreenRect? Text, string Label, RunecraftPrice Price, bool Best)
{
    /// <summary>The whole reward's worth, or null.</summary>
    public double? Total => Price.Total;

    /// <summary>
    /// Whether the row's text element reads as one, inside its row, with a gap's room before it.
    /// </summary>
    /// <remarks>
    /// The guard on the anchor: a left edge past the row's own and a right edge not past the
    /// row's is what a text sized to itself and right-aligned on the row looks like. A child
    /// index that drifts onto some other element - the icons, the row itself - fails it, and
    /// the price goes back to the row's edge rather than somewhere unrelated.
    /// </remarks>
    public bool TextAnchors(float gap)
        => Text is { Width: >= 1f } text && text.Left > Where.Left + gap && text.Right <= Where.Right + gap;

    /// <summary>
    /// The x a price's right edge is aligned to: a gap before the row's text, or, when the
    /// text's element did not read, a gap inside the row's right edge.
    /// </summary>
    /// <remarks>
    /// BEFORE THE TEXT, NOT ON IT. The game right-aligns the reward's name at the row's edge,
    /// so a price put at that edge lands on the name's last word and both read worse. The
    /// text element is sized to its text and sits well clear of the rune icons at the row's
    /// left - 386 UI units in, on 0.5.5 - and that room is where the price goes.
    /// </remarks>
    public float Before(float gap)
        => TextAnchors(gap) ? Text!.Value.Left - gap : Where.Right - gap;
}

/// <summary>
/// What the Runeshape Combinations panel looks like right now. Immutable, published whole.
/// </summary>
/// <param name="Open">Whether the panel is open. Nothing else means anything when it is not.</param>
/// <param name="Rewards">The rows on screen, with their prices.</param>
/// <param name="Viewport">The scroll clip the rows are drawn under, so a scrolled-off row is not drawn.</param>
/// <param name="Median">The median of the priced totals, for Relative colouring. Zero when none.</param>
/// <param name="Priced">How many rows carry a price.</param>
/// <param name="Unpriced">How many do not.</param>
/// <param name="Status">What happened, in words. "panel shut" is the ordinary case.</param>
/// <param name="Named">What the game calls the panel's elements, as last walked, for the readout.</param>
public sealed record RunecraftView(
    bool Open,
    IReadOnlyList<RunecraftReward> Rewards,
    ScreenRect? Viewport,
    double Median,
    int Priced,
    int Unpriced,
    string Status,
    string Named = "")
{
    public static RunecraftView Closed { get; } = new(false, [], null, 0, 0, 0, "panel shut");

    /// <summary>Whether there is anything to draw.</summary>
    public bool Anything => Open && Rewards.Count > 0;
}

/// <summary>
/// Serves the Runeshape Combinations panel from the reader thread.
/// </summary>
/// <remarks>
/// THE SAME SHAPE AS THE ATLAS, and for the same reasons: the panel is interface rather than
/// world, it is shut for almost all of a session, and reading it is pointer chains and wide
/// strings that must not run on the thread that draws. So this reads where the reading already
/// happens and publishes a finished view; the overlay draws whatever came back.
///
/// TWO RATES. What a row IS - its label, its recipe, its reward and therefore its price - cannot
/// change while the panel sits open at one monolith, so it is studied on an interval. WHERE it
/// is changes with every scroll, so that is read every tick, for the studied rows only. The
/// prices are re-studied when the BOOK changes too, since a refresh that lands while the panel
/// is open should show on the panel.
///
/// IDLE UNLESS OPEN: the whole cost of having it switched on while the panel is shut is the
/// reader's gate check - two reads and a short visibility walk a tick - see RunecraftPanelReader.
/// </remarks>
public sealed class RunecraftWatch
{
    /// <summary>How long the studied rows are kept before they are read again.</summary>
    /// <remarks>
    /// A quarter of a second: the offered set changes when the player opens another monolith's
    /// panel, and a lag that short is invisible, while re-reading every row's label and recipe
    /// each tick would be the most expensive thing in the tick for information that never moves.
    /// </remarks>
    public const long RestudyMs = 250;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly RunecraftPanelReader _panel;
    private readonly Func<string?, string?> _shippedName;

    private RunecraftSettings _settings = RunecraftSettings.Default;
    private RewardCatalog _catalog = RewardCatalog.Empty;
    private RunecraftView _view = RunecraftView.Closed;

    // The slow half's answers: the rows as last studied, their prices, and what they were
    // priced with - so a new book re-prices and an old one does not.
    private ulong _studiedContainer;
    private long _studiedAt;
    private List<RunecraftRow> _rows = [];
    private readonly Dictionary<ulong, int> _index = [];
    private RunecraftPrice[] _prices = [];
    private PriceBook? _pricedWith;
    private RewardCatalog? _pricedFrom;
    private string _named = string.Empty;

    public RunecraftWatch(
        IMemoryReader reader,
        OffsetSchema schema,
        ulong gameStatesStatic,
        Func<string?, string?>? shippedName = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _panel = new RunecraftPanelReader(reader, schema, new UiElementReader(reader, schema));
        _shippedName = shippedName ?? (_ => null);
    }

    /// <summary>What to draw and how. Replaced whole, from whichever thread saved it.</summary>
    public RunecraftSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _settings, value);
        }
    }

    /// <summary>
    /// The install's English names and pictures, by path. Arrives late, from a background read.
    /// </summary>
    /// <remarks>
    /// A property rather than a constructor argument because the install is read on its own
    /// task well after this is built; the rows are re-priced the first tick after it lands.
    /// </remarks>
    public RewardCatalog Catalog
    {
        get => Volatile.Read(ref _catalog);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _catalog, value);
        }
    }

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public RunecraftView View => Volatile.Read(ref _view);

    /// <summary>The rows as last studied, with their prices - for the tab's own list.</summary>
    /// <remarks>
    /// Published as one array alongside the view rather than inside it, because the view is
    /// rebuilt every tick for the positions and this half only every quarter second.
    /// </remarks>
    public IReadOnlyList<(RunecraftRow Row, RunecraftPrice Price)> Studied => Volatile.Read(ref _studied);

    private IReadOnlyList<(RunecraftRow Row, RunecraftPrice Price)> _studied = [];

    /// <summary>Reads the panel once. Called on the reader thread, in the same pass as a world read.</summary>
    public void Service(UiScale scale, long nowMs, PriceBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        RunecraftSettings settings = Volatile.Read(ref _settings);
        if (!settings.Enabled)
        {
            Forget();
            Volatile.Write(ref _view, RunecraftView.Closed with { Status = "runecraft prices off" });
            return;
        }

        try
        {
            Volatile.Write(ref _view, Build(scale, nowMs, book));
        }
        catch (Exception exception)
        {
            // A stale view beats a dead servicing thread. Every pointer here belongs to a panel
            // that can be shut between two reads, so this is an ordinary event.
            Forget();
            Volatile.Write(ref _view, RunecraftView.Closed with { Status = $"read failed: {exception.Message}" });
        }
    }

    private RunecraftView Build(UiScale scale, long nowMs, PriceBook book)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        if (chain.UiRoot == 0)
        {
            Forget();
            return RunecraftView.Closed with { Status = chain.InGame ? "UI root did not resolve" : "not in an area" };
        }

        RunecraftPanelState panel = _panel.Resolve(chain.UiRoot, nowMs);
        if (!panel.Open)
        {
            Forget();
            return RunecraftView.Closed with { Status = panel.Why };
        }

        if (panel.Named.Length > 0)
        {
            _named = panel.Named;
        }

        RewardCatalog catalog = Volatile.Read(ref _catalog);
        bool restudy = panel.Container != _studiedContainer
                       || nowMs - _studiedAt >= RestudyMs
                       || nowMs < _studiedAt;
        if (restudy)
        {
            Study(panel.Container, nowMs);
        }

        if (restudy || !ReferenceEquals(book, _pricedWith) || !ReferenceEquals(catalog, _pricedFrom))
        {
            Reprice(book, catalog);
        }

        if (_rows.Count == 0)
        {
            return RunecraftView.Closed with
            {
                Open = true,
                Status = "panel open, no rows are shown on it",
                Named = _named,
            };
        }

        (List<RunecraftPlace> placed, ScreenRect? viewport) = _panel.Place(panel, _rows, scale);
        return Compose(placed, viewport);
    }

    /// <summary>The slow half: every shown row's label, recipe and reward.</summary>
    private void Study(ulong container, long nowMs)
    {
        _rows = _panel.Rows(container);
        _index.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            _index[_rows[i].Address] = i;
        }

        _studiedContainer = container;
        _studiedAt = nowMs;
    }

    /// <summary>Prices the studied rows against this book and catalogue, and publishes the list.</summary>
    private void Reprice(PriceBook book, RewardCatalog catalog)
    {
        var prices = new RunecraftPrice[_rows.Count];
        var studied = new (RunecraftRow, RunecraftPrice)[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            prices[i] = RunecraftPrices.Price(book, _rows[i], catalog, _shippedName);
            studied[i] = (_rows[i], prices[i]);
        }

        _prices = prices;
        _pricedWith = book;
        _pricedFrom = catalog;
        Volatile.Write(ref _studied, studied);
    }

    /// <summary>The fast half joined to the slow one: each placed row with its price.</summary>
    private RunecraftView Compose(List<RunecraftPlace> placed, ScreenRect? viewport)
    {
        // The best and the median over the FULL priced set, not over the rows whose geometry
        // read this tick - a single read miss must not shift the colours of every other row.
        double best = double.NegativeInfinity;
        var totals = new List<double>(_prices.Length);
        var priced = 0;
        foreach (RunecraftPrice price in _prices)
        {
            if (price.Total is { } total)
            {
                totals.Add(total);
                priced++;
                if (total > best)
                {
                    best = total;
                }
            }
        }

        var rewards = new List<RunecraftReward>(placed.Count);
        foreach (RunecraftPlace place in placed)
        {
            if (!_index.TryGetValue(place.Address, out int at))
            {
                continue;
            }

            RunecraftPrice price = _prices[at];
            bool top = price.Total is { } total && priced > 0 && total >= best;
            rewards.Add(new RunecraftReward(place.Where, place.Text, _rows[at].Label, price, top));
        }

        return new RunecraftView(
            true,
            rewards,
            viewport,
            RunecraftPrices.Median(totals),
            priced,
            _prices.Length - priced,
            $"panel open: {_rows.Count} rows, {priced} priced",
            _named);
    }

    private void Forget()
    {
        if (_rows.Count == 0 && _studiedContainer == 0)
        {
            return;
        }

        _rows = [];
        _index.Clear();
        _prices = [];
        _pricedWith = null;
        _pricedFrom = null;
        _studiedContainer = 0;
        Volatile.Write(ref _studied, []);
    }
}
