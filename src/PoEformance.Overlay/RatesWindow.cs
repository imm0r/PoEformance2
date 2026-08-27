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
            $"{pairs.Count} currencies in {pairs.League} from the game's own exchange"
            + (index.Count > 0
                ? $", {index.Count} with a week of history from the index"
                : ", and no index - so no arbitrage is offered, which is the intended refusal")
            + (routes.Count > 0 ? $". {routes.Count} loops pay better than selling straight." : string.Empty));

        Draw(pairs, index, better);
    }

    /// <summary>
    /// The one switch, and what each source last said.
    /// </summary>
    /// <remarks>
    /// ONE SWITCH FOR TWO SOURCES, because a state where the exchange is on and the index is off
    /// is not a state anybody wants: the index is one request every half hour and its only job
    /// here is to disagree with the exchange when the exchange is wrong. Wiring it to its own
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
                "off - it is two public feeds, one request an hour and one every half hour, "
                + "and neither needs a sign-in.");
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
                        $"no Exalted market this hour\nvalued through {Short(worth.Through)}");
                }

                ImGui.TableNextColumn();
                ImGuiText.Mono(worth.Direct ? Money : OverlayTheme.Quiet, StashWorth.Money(worth.Exalted));

                ImGui.TableNextColumn();
                Week(index, path);

                ImGui.TableNextColumn();
                ImGuiText.Mono(
                    rate > 0 ? OverlayTheme.Quiet : Warn,
                    rate > 0 ? StashWorth.Money(rate) : "-");

                // HOW THE VALUE WAS REACHED, which is a fact about most rows. This column used
                // to carry the arbitrage route's middle instead - so it was blank on every row
                // that had no loop, which is nearly all of them, while the routing that DID
                // happen was hidden in a tooltip. The loop has its own column beside this one.
                ImGui.TableNextColumn();
                ImGuiText.Mono(OverlayTheme.Quiet, worth.Direct ? string.Empty : Short(worth.Through));

                ImGui.TableNextColumn();
                if (better.TryGetValue(path, out Route loop))
                {
                    ImGuiText.Mono(Up, $"+{loop.Gain * 100:0.#}%");
                    if (ImGui.IsItemHovered())
                    {
                        ImGuiText.MonoTooltip(
                            $"straight   {StashWorth.Money(loop.Direct)} ex\n"
                            + $"via {Short(loop.Through),-14} {StashWorth.Money(loop.Routed)} ex\n"
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

    /// <summary>The seven-day move, from the index rather than the feed.</summary>
    /// <remarks>
    /// A NUMBER RATHER THAN A DRAWN LINE, for now. A sparkline in a table row is a handful of
    /// pixels either way, and the thing somebody actually reads off it is the direction and the
    /// size - which fit in six characters and cannot be misread at a glance.
    ///
    /// It is blank rather than zero where the index has nothing: "flat" and "unknown" are
    /// different answers, and a dash is the honest one.
    /// </remarks>
    private static void Week(IReadOnlyDictionary<string, ScoutEntry> index, string path)
    {
        if (!index.TryGetValue(path, out ScoutEntry entry) || entry.Trend is not { } moved)
        {
            ImGuiText.Mono(OverlayTheme.Quiet, "-");
            return;
        }

        ImGuiText.Mono(moved >= 0 ? Up : Down, $"{(moved >= 0 ? "+" : string.Empty)}{moved * 100:0.#}%");

        if (ImGui.IsItemHovered())
        {
            var said = new System.Text.StringBuilder();
            foreach (ScoutDay day in entry.Settled)
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
            if (entry.Settled.Count < entry.Days.Count)
            {
                said.Append("\ntoday so far is left out - a part-day is not a day");
            }

            ImGuiText.MonoTooltip(said.ToString());
        }
    }

    private static string Short(string path)
        => path.Length == 0 ? string.Empty : path[(path.LastIndexOf('/') + 1)..];
}
