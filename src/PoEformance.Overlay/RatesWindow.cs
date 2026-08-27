using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What every currency is going for, where it has been, and where a loop pays better.
/// </summary>
/// <remarks>
/// TWO SOURCES ON ONE PAGE, deliberately. The rate and the depth are the game's own exchange -
/// executed trades, this league, this hour. The seven-day line is an aggregated index, because
/// a week of hourly digests is a hundred and sixty-eight requests to answer a question that is
/// daily anyway. Which number came from where is said in the header rather than blurred.
///
/// THE ARBITRAGE COLUMN IS EMPTY UNLESS BOTH ARE ANSWERING, and that is the point rather than a
/// limitation - see <see cref="Arbitrage"/>, where the same arithmetic on one source alone
/// produced a route reading three hundred thousand percent.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RatesWindow
{
    private static readonly Vector4 Up = new(0.55f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Down = new(0.92f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 Money = new(1f, 0.83f, 0.42f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.72f, 0.3f, 1f);

    /// <summary>How many rows are drawn. The rest are counted.</summary>
    /// <remarks>
    /// A league's exchange runs to five hundred markets and nobody reads five hundred rows. The
    /// list is sorted by worth, so what is cut is the tail nobody was looking for - and the count
    /// says how much was cut rather than pretending the page is the whole market.
    /// </remarks>
    public const int MostRows = 80;

    private readonly ExchangeStore _exchange;
    private readonly ScoutStore _scout;
    private readonly Func<string> _league;

    private string _search = string.Empty;
    private bool _routesOnly;

    public RatesWindow(ExchangeStore exchange, ScoutStore scout, Func<string> league)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(scout);
        ArgumentNullException.ThrowIfNull(league);
        _exchange = exchange;
        _scout = scout;
        _league = league;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        if (!Switch())
        {
            return;
        }

        ExchangePairs pairs = _exchange.Pairs;
        IReadOnlyDictionary<string, ScoutEntry> index = _scout.Index;

        if (pairs.Count == 0)
        {
            ImGuiText.Wrapped(
                OverlayTheme.Quiet,
                _exchange.Busy
                    ? "Reading the exchange..."
                    : "Nothing read yet.");
            return;
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16.5f);
        ImGui.InputTextWithHint("##rates-search", "name...", ref _search, 64);
        ImGui.SameLine();
        ImGui.Checkbox("only where a loop pays", ref _routesOnly);

        IReadOnlyList<Route> routes = Arbitrage.Routes(pairs, index);
        var better = routes.ToDictionary(route => route.Path, StringComparer.Ordinal);

        ImGuiText.Wrapped(
            OverlayTheme.Quiet,
            $"{pairs.Count} currencies in {pairs.League} from the game's own exchange, priced "
            + $"against {Short(pairs.Pivot)} because that is what the league trades in"
            + (index.Count > 0
                ? $". {index.Count} have a week of history from the index"
                : ". No index, so no arbitrage is offered - which is the intended refusal")
            + (routes.Count > 0 ? $". {routes.Count} loops pay better than selling straight." : "."));

        Draw(pairs, index, better);
    }

    /// <summary>
    /// The one switch, and what each source last said.
    /// </summary>
    /// <remarks>
    /// ONE SWITCH FOR TWO SOURCES, because a state where the exchange is on and the index is off
    /// is not a state anybody wants: the index costs a handful of requests every two hours, and its
    /// job here is to disagree with the exchange when the exchange is wrong. Wiring it to its own
    /// checkbox would offer the reader a way to turn off the check while leaving the numbers it
    /// guards - see <see cref="Arbitrage"/> for what that costs.
    ///
    /// It reads the store's own flag every frame rather than keeping a copy, so this and the
    /// Stash tab's switch cannot come apart.
    /// </remarks>
    private bool Switch()
    {
        bool asking = _exchange.Enabled;
        if (ImGui.Checkbox("read live rates", ref asking))
        {
            _exchange.Enabled = asking;
            _scout.Enabled = asking;
            if (asking)
            {
                string league = _league();
                _exchange.Playing(league);
                _scout.Playing(league);
            }
        }

        ImGui.SameLine();

        if (!asking)
        {
            ImGuiText.Wrapped(
                OverlayTheme.Quiet,
                "off - it is two public feeds, neither needing a sign-in: the game's own exchange "
                + "once an hour, and the index once every two hours, a category at a time.");
            return false;
        }

        ImGui.TextColored(_exchange.Busy ? Warn : OverlayTheme.Quiet,
            _exchange.Busy ? "reading the exchange..." : _exchange.Status);

        ImGuiText.Wrapped(
            _scout.Busy ? Warn : OverlayTheme.Quiet,
            _scout.Busy ? "reading the index..." : _scout.Status);

        return true;
    }

    private void Draw(
        ExchangePairs pairs,
        IReadOnlyDictionary<string, ScoutEntry> index,
        IReadOnlyDictionary<string, Route> better)
    {
        var rows = new List<(string Path, string Called, Valuation Worth, double Book)>();
        foreach (string path in pairs.Everything())
        {
            if (string.Equals(path, ExchangeFeed.Exalted, StringComparison.Ordinal))
            {
                continue;
            }

            Valuation worth = pairs.Worth(path);
            if (!worth.Known)
            {
                continue;
            }

            if (_routesOnly && !better.ContainsKey(path))
            {
                continue;
            }

            string called = index.TryGetValue(path, out ScoutEntry named)
                ? named.Called
                : Short(path);

            if (_search.Length > 0 && !called.Contains(_search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add((path, called, worth, worth.Stock));
        }

        // BY WHAT ONE IS WORTH, not by name. The question this page answers starts at the top of
        // the market and works down, and an alphabetical list buries a Mirror under an Augment.
        rows.Sort((a, b) => b.Worth.Exalted.CompareTo(a.Worth.Exalted));

        if (rows.Count == 0)
        {
            ImGuiText.Wrapped(OverlayTheme.Quiet, "Nothing matches.");
            return;
        }

        if (!ImGui.BeginTable(
                "rates",
                6,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("currency");
            ImGui.TableSetupColumn("in exalted");
            ImGui.TableSetupColumn("7 days");
            ImGui.TableSetupColumn("book");
            ImGui.TableSetupColumn("via");
            ImGui.TableSetupColumn("loop");
            ImGui.TableHeadersRow();

            foreach ((string path, string called, Valuation worth, double rate) in rows.Take(MostRows))
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(called);
                if (!worth.Direct && ImGui.IsItemHovered())
                {
                    // A two-leg value is a weaker claim than a one-leg one, and the reader should
                    // be able to find that out without it shouting on every row.
                    ImGuiText.MonoTooltip(
                        $"no Exalted market this hour\nvalued through {Short(worth.Through)}"
                        + (pairs.Ordinary(worth)
                            ? $", which is what {pairs.League} trades in"
                            : $" - {pairs.League} trades in {Short(pairs.Pivot)}"));
                }

                // GOLD FOR THE LEAGUE'S OWN MONEY, not for Exalted specifically. Colouring by
                // Direct painted nine Standard rows in ten as second-rate, when a Divine market
                // is simply what a currency has there - see ExchangePairs.Ordinary.
                ImGui.TableNextColumn();
                ImGuiText.Mono(
                    pairs.Ordinary(worth) ? Money : OverlayTheme.Quiet, StashWorth.Money(worth.Exalted));

                ImGui.TableNextColumn();
                Week(index, path);

                ImGui.TableNextColumn();
                ImGuiText.Mono(
                    rate > 0 ? OverlayTheme.Quiet : Warn,
                    rate > 0 ? StashWorth.Money(rate) : "-");

                // HOW THE VALUE WAS REACHED, when that is worth remarking on. This column used
                // to carry the arbitrage route's middle - blank on every row that had no loop,
                // which is nearly all of them - and then it carried every hop, which in Standard
                // meant the same word on all eighty rows, because a hop through Divine is what
                // pricing anything there looks like. Now it names only the UNUSUAL detour.
                ImGui.TableNextColumn();
                ImGuiText.Mono(
                    Warn, pairs.Ordinary(worth) ? string.Empty : Short(worth.Through));

                ImGui.TableNextColumn();
                if (better.TryGetValue(path, out Route loop))
                {
                    ImGuiText.Mono(Up, $"+{loop.Gain * 100:0.#}%");
                    if (ImGui.IsItemHovered())
                    {
                        // The loop is measured in the league's own money, so the unit is named
                        // from the route rather than assumed - labelling Divine figures "ex"
                        // would be off by a factor of two hundred and sixty in Standard.
                        string unit = Unit(loop.Money);
                        ImGuiText.MonoTooltip(
                            $"straight   {StashWorth.Money(loop.Direct)} {unit}\n"
                            + $"via {Short(loop.Through),-14} {StashWorth.Money(loop.Routed)} {unit}\n"
                            + $"the thinner leg holds {StashWorth.Money(loop.Carries)}\n\n"
                            + "Both legs agree with the index to within a quarter, which is what\n"
                            + "separates a route from a stale fill that looks like one.");
                    }
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        if (rows.Count > MostRows)
        {
            ImGuiText.Wrapped(OverlayTheme.Quiet, $"{rows.Count - MostRows} more not shown.");
        }
    }

    /// <summary>How wide the drawn week is, as a multiple of the font size.</summary>
    private const float LineWide = 3.4f;

    /// <summary>And how tall.</summary>
    private const float LineTall = 0.9f;

    /// <summary>The seven-day move: the shape of it, then the size of it.</summary>
    /// <remarks>
    /// BOTH, because they answer different questions. The number says how far the price moved
    /// and is the one to read when comparing two rows; the line says HOW it got there, which a
    /// single percentage cannot - a steady climb and a spike that fell back read identically at
    /// +26% and are not the same thing to trade against.
    ///
    /// This was a number alone, with a comment arguing that a few pixels in a table row could
    /// not say anything the figure did not. That was a design call standing in for a request:
    /// the reference this borrows from draws the line, and the screenshot asking for the feature
    /// had it in every row.
    ///
    /// Blank rather than zero where the index has nothing: "flat" and "unknown" are different
    /// answers, and a dash is the honest one.
    /// </remarks>
    private static void Week(IReadOnlyDictionary<string, ScoutEntry> index, string path)
    {
        if (!index.TryGetValue(path, out ScoutEntry entry) || entry.Trend is not { } moved)
        {
            ImGuiText.Mono(OverlayTheme.Quiet, "-");
            return;
        }

        // ONCE PER ROW. Settled is a property that sorts a copy of the volumes to find the median
        // day, so every mention of it is two allocations and a sort - and this cell mentions it
        // three times, on eighty rows, on every frame.
        IReadOnlyList<ScoutDay> days = entry.Settled;

        Vector4 tint = moved >= 0 ? Up : Down;
        bool hovering = Line(days, tint);
        ImGui.SameLine();
        ImGuiText.Mono(tint, $"{(moved >= 0 ? "+" : string.Empty)}{moved * 100:0.#}%");

        if (hovering || ImGui.IsItemHovered())
        {
            var said = new System.Text.StringBuilder();
            foreach (ScoutDay day in days)
            {
                said.Append(day.Day.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("   ")
                    .Append(StashWorth.Money(day.Exalted).PadLeft(8))
                    .Append(" ex   on ")
                    .Append(StashWorth.Money(day.Quantity))
                    .Append('\n');
            }

            // The unfinished day is named rather than silently dropped, because somebody looking
            // for today and not finding it deserves to know it was left out on purpose.
            if (days.Count < entry.Days.Count)
            {
                said.Append("\ntoday so far is left out - a part-day is not a day");
            }

            ImGuiText.MonoTooltip(said.ToString());
        }
    }

    /// <summary>
    /// One currency's settled days, drawn as a line.
    /// </summary>
    /// <remarks>
    /// SCALED TO ITS OWN RANGE, not to zero, for the reason the wealth graph is: a Mirror moving
    /// from 2.9M to 3.1M against a zero baseline is a flat line along the top, and the shape is
    /// the only thing this column has that the number beside it does not.
    ///
    /// A flat week draws down the middle rather than at the bottom - a price that did not move
    /// is not a price of nothing, and the eye reads "at the floor" as the second one.
    ///
    /// Space is reserved with Dummy rather than an InvisibleButton because a button is a widget:
    /// it takes the click, and the table's rows are clickable. Dummy reserves the space and is
    /// still hoverable, which is all this needs - it says so, and the caller puts the same
    /// tooltip on the line as on the figure beside it.
    /// </remarks>
    /// <returns>Whether the cursor is over the drawn week.</returns>
    private static bool Line(IReadOnlyList<ScoutDay> days, Vector4 tint)
    {
        float wide = ImGui.GetFontSize() * LineWide;
        float tall = ImGui.GetFontSize() * LineTall;

        Vector2 at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(wide, tall));
        bool hovering = ImGui.IsItemHovered();

        if (days.Count < 2)
        {
            // Guarded rather than assumed: the caller only draws this when the trend exists, which
            // means two settled days, but a helper that reads a list should not need to know that.
            return hovering;
        }

        double low = days[0].Exalted;
        double high = low;
        for (var i = 1; i < days.Count; i++)
        {
            low = Math.Min(low, days[i].Exalted);
            high = Math.Max(high, days[i].Exalted);
        }

        double range = high - low;
        float middle = at.Y + (tall / 2f);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint ink = ImGui.ColorConvertFloat4ToU32(tint with { W = tint.W * 0.85f });

        if (range <= 0)
        {
            draw.AddLine(new Vector2(at.X, middle), new Vector2(at.X + wide, middle), ink, 1.2f);
            return hovering;
        }

        // ACROSS BY DATE, NOT BY INDEX, the same way the wealth graph spaces its points by time.
        // A day the site had no trades for arrives as a null and the catalogue drops it rather
        // than carrying a zero, so the list has real holes: in the captured Standard index, 37 of
        // the day slots are null and three of the thirty-six currencies are missing a day - Chance
        // Shard has four points spanning five days. Spread evenly, that gap draws as an ordinary
        // step and the week reads a day shorter than it was.
        int first = days[0].Day.DayNumber;
        float across = Math.Max(1, days[^1].Day.DayNumber - first);

        Vector2 last = default;
        for (var i = 0; i < days.Count; i++)
        {
            var here = new Vector2(
                at.X + (wide * (days[i].Day.DayNumber - first) / across),
                Height(days[i].Exalted, low, range, at.Y, tall));

            if (i > 0)
            {
                draw.AddLine(last, here, ink, 1.2f);
            }

            last = here;
        }

        return hovering;
    }

    /// <summary>Where in the row one day's price sits, with the cheapest day at the bottom.</summary>
    /// <remarks>
    /// A seventh of the height is left free at the top and the same at the bottom. Without it the
    /// week's high and low sit exactly on the row's edges, where a line a pixel wide is half eaten
    /// by the row above and the row below - so the two points whose position is most certain end
    /// up the two drawn worst.
    /// </remarks>
    private static float Height(double value, double low, double range, float top, float tall)
    {
        const float Air = 0.15f;
        var part = (float)((value - low) / range);
        return top + (tall * (1f - Air - (part * (1f - (2f * Air)))));
    }

    private static string Short(string path)
        => path.Length == 0 ? string.Empty : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The short label for a currency a figure is quoted in.</summary>
    /// <remarks>
    /// The two the game itself treats as money get their usual abbreviations; anything else is
    /// named by its path rather than guessed at, which is ugly and honest rather than tidy and
    /// possibly wrong.
    /// </remarks>
    private static string Unit(string path) => path switch
    {
        ExchangeFeed.Exalted => "ex",
        ExchangeFeed.Divine => "div",
        _ => Short(path),
    };
}
