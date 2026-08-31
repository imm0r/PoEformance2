using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The record of what the currency has been worth, as a shape.
/// </summary>
/// <remarks>
/// WHAT A LIVE NUMBER CANNOT ANSWER. The corner panel says what the purse is worth and which way
/// it went this hour, which is the question you have while playing. The ones worth asking need
/// the record: was this league better than the last one, what did that crafting session actually
/// cost, is the number going up because of what is being done or because Divine moved.
///
/// IT MEASURES CURRENCY AND NOTHING ELSE, deliberately, and that is what makes two points on it
/// comparable. Valuing gear means pricing rares and uniques, which is where the price book is
/// least sure of itself - the listing gate throws away thin lines, so a total that included gear
/// would swing by whatever share of it happened to be priceable that refresh. A line whose two
/// ends were measured differently is not a line. See <see cref="CurrencyPurse"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WealthWindow
{
    /// <summary>The stretches the record can be looked at over.</summary>
    /// <remarks>
    /// "Everything" is last and is not a duration: it is whatever the record holds, which is the
    /// only one of these that answers "since I started playing this league".
    /// </remarks>
    private static readonly (string Label, TimeSpan Span)[] Windows =
    [
        ("hour", TimeSpan.FromHours(1)),
        ("day", TimeSpan.FromDays(1)),
        ("week", TimeSpan.FromDays(7)),
        ("everything", TimeSpan.MaxValue),
    ];

    private static readonly Vector4 Up = OverlayInk.Good;
    private static readonly Vector4 Down = OverlayInk.Bad;
    private static readonly Vector4 PlotBack = OverlayInk.Sunken with { W = 0.85f };
    private static readonly Vector4 Line = OverlayInk.Accent;
    private static readonly Vector4 Warn = OverlayInk.Warn;

    private readonly WealthTracker _tracker;
    private readonly WealthPanel _panel;
    private readonly Func<PriceBook> _book;
    private readonly Func<string> _league;
    private readonly Func<PurseView> _purse;

    private int _window = 1;
    private bool _confirmingReset;
    private bool _breakdown;

    /// <param name="tracker">The record and the live count.</param>
    /// <param name="panel">The corner panel, so the page can switch it on and share its window.</param>
    /// <param name="book">
    /// What things are worth, asked for per frame rather than held: the store replaces the book
    /// wholesale on a refresh, and a page holding the old one would price today at last hour's
    /// rate for as long as it stayed open.
    /// </param>
    /// <param name="league">
    /// Which league the prices are for. Shown rather than assumed: the whole total rests on it,
    /// and until it was on screen there was no way for a reader to tell a plausible-looking
    /// figure priced against the wrong economy from a right one.
    /// </param>
    /// <param name="purse">The currency itself, for breaking the total down into what made it.</param>
    /// <param name="pairs">
    /// The game's own exchange, for the per-tab worth. Null where nothing wired one, and then
    /// the section simply does not appear - it would be a table of blanks, since the aggregated
    /// index knows a fraction of what a stash actually holds.
    /// </param>
    public WealthWindow(
        WealthTracker tracker,
        WealthPanel panel,
        Func<PriceBook> book,
        Func<string> league,
        Func<PurseView> purse,
        Func<ExchangePairs?>? pairs = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(purse);
        _tracker = tracker;
        _panel = panel;
        _book = book;
        _league = league;
        _purse = purse;
        _pairs = pairs;
    }

    private readonly Func<ExchangePairs?>? _pairs;

    /// <summary>
    /// Tabs the player has taken out of the total, remembered by the game's own tab id.
    /// </summary>
    /// <remarks>
    /// BY ID RATHER THAN BY NAME, because a tab can be renamed and a mule tab usually is. The
    /// set lives here rather than in the settings file for now: it is a view choice about one
    /// session's stash, and persisting it would mean deciding what happens to an id that no
    /// longer exists.
    /// </remarks>
    private readonly HashSet<int> _skipping = [];

    private bool _worth = true;

    /// <summary>Whether the purse is being counted at all. Wired to the inspector's own switch.</summary>
    public bool Watching { get; set; }

    /// <summary>
    /// Which stretch both views report on, in minutes - what the settings file holds.
    /// </summary>
    /// <remarks>
    /// MINUTES RATHER THAN THE INDEX, because the index is a position in a list this file owns
    /// and can reorder. A saved 2 that meant "week" and now means "day" is a setting that
    /// silently became a different setting; a saved 10080 means a week whatever the list does.
    /// An unrecognised value lands on the nearest one rather than being refused - a hand-edited
    /// file asking for six hours gets the day, which is closer to what it asked for than a reset.
    /// </remarks>
    public int WindowMinutes
    {
        get => Windows[_window].Span == TimeSpan.MaxValue
            ? int.MaxValue
            : (int)Windows[_window].Span.TotalMinutes;

        set
        {
            var best = 0;
            double closest = double.MaxValue;

            for (var i = 0; i < Windows.Length; i++)
            {
                double minutes = Windows[i].Span == TimeSpan.MaxValue
                    ? int.MaxValue
                    : Windows[i].Span.TotalMinutes;

                double apart = Math.Abs(minutes - value);
                if (apart < closest)
                {
                    closest = apart;
                    best = i;
                }
            }

            _window = best;
            _panel.Window = Windows[best].Span == TimeSpan.MaxValue
                ? TimeSpan.FromDays(365)
                : Windows[best].Span;
        }
    }

    /// <summary>Called when the page turns the watch on or off, so the wiring can follow.</summary>
    public Action<bool>? WatchChanged { get; set; }

    /// <summary>Called when the record changed and is worth writing to disk.</summary>
    public Action? Changed { get; set; }

    /// <summary>Draws the page.</summary>
    public void DrawTab()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        DrawSwitch();

        if (!Watching)
        {
            // Stays: it describes what is happening RIGHT NOW because the switch is off, and
            // nobody hovers a switch to find out why nothing is being recorded.
            OverlayLayout.Note(
                "Nothing is counted while this is off, and the record simply has a gap for the"
                + " time it was - which is the honest picture of not having been watching.");
            return;
        }

        DrawPrices();
        ImGui.Separator();
        DrawTotals(now);
        ImGui.Separator();
        DrawBreakdown();
        ImGui.Separator();
        DrawByTab();
        ImGui.Separator();
        DrawGraph(now);
        ImGui.Separator();
        DrawRecord(now);
    }

    private void DrawSwitch()
    {
        bool on = Watching;
        if (ImGui.Checkbox("watch what the currency is worth", ref on) && on != Watching)
        {
            Watching = on;
            WatchChanged?.Invoke(on);
        }

        bool panel = _panel.Enabled;
        if (ImGui.Checkbox("and show it in a corner while playing", ref panel))
        {
            _panel.Enabled = panel;
            Changed?.Invoke();
        }

        OverlayLayout.Hint(
            "The corner panel can be moved, and made click-through from its own title menu so it"
            + " stops taking the mouse during a fight.");
    }

    /// <summary>
    /// Whether anything can be priced, and what it means when it cannot.
    /// </summary>
    /// <remarks>
    /// SAID HERE RATHER THAN LEFT TO BE INFERRED. Prices are off until somebody turns them on -
    /// they come from somebody else's server and nothing in this tool goes to a network
    /// uninvited - and with no book every reading values at nothing. A page showing a wealth of
    /// zero without saying why is a page reporting a catastrophe.
    /// </remarks>
    private void DrawPrices()
    {
        PriceBook book = _book();
        if (!book.Ready)
        {
            ImGuiText.Wrapped(
                Warn,
                "No prices yet. Nothing can be valued and nothing is being written to the record - "
                + "turn prices on in the Stash tab, which is where they are fetched from.");
            return;
        }

        // THE LEAGUE IS THE FIRST THING SHOWN, because every figure below is only as right as it
        // is. It comes from the game rather than from a setting - so it cannot go stale at a
        // league start - but that also means nobody ever typed it, and until it was on screen a
        // total priced against the wrong economy looked exactly like a total priced against the
        // right one.
        string league = _league();
        ImGuiText.Mono(
            OverlayInk.Quiet,
            $"{(league.Length > 0 ? league : "league not read yet")}"
            + $"   {book.Count} prices   1 div = {StashWorth.Money(book.Rate)} ex");

        OverlayLayout.Hint(
            "Prices are poe.ninja's, for that league, and everything is converted into Exalted"
            + " using that rate. If the figures look wrong, the breakdown below says which stack"
            + " is producing them.");
    }

    /// <summary>
    /// What the total is made of, so a wrong one can be pointed at.
    /// </summary>
    /// <remarks>
    /// A SINGLE TOTAL IS UNFALSIFIABLE - it is believed or distrusted, and neither can be acted
    /// on. Broken into the stacks that produced it, the reader can compare a count against the
    /// tab in front of them and a unit price against what that currency actually trades at, and
    /// find the wrong one in a second. Folded away, because it is the thing you open when
    /// something looks off rather than something to read every time.
    /// </remarks>
    private void DrawBreakdown()
    {
        _breakdown = OverlayLayout.Subsection("What the Total Is Made Of###wealth-breakdown");
        if (!_breakdown)
        {
            return;
        }

        IReadOnlyList<CurrencyPurse.PurseLine> lines = _book().Breakdown(_purse().Pages);
        if (lines.Count == 0)
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, "No currency counted yet.");
            return;
        }

        if (!ImGui.BeginTable(
                "wealth-breakdown",
                4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0f, ImGui.GetFontSize() * 14f)))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("currency", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("held", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 4f);
            ImGui.TableSetupColumn("each", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5f);
            ImGui.TableSetupColumn("worth", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 6f);
            ImGui.TableHeadersRow();

            foreach (CurrencyPurse.PurseLine line in lines)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ImGuiText.Escape(line.Called));

                ImGui.TableNextColumn();
                ImGuiText.Mono(line.Stack.ToString(System.Globalization.CultureInfo.CurrentCulture));

                // The unit price is the one to check against what the currency actually trades
                // at - a total that is out by a factor is usually one of these being out by it.
                ImGui.TableNextColumn();
                if (line.Unit is { } unit)
                {
                    ImGuiText.Mono(StashWorth.Purse(unit, _book().Rate));
                }
                else
                {
                    ImGui.TextColored(OverlayInk.Quiet, "no price");
                }

                ImGui.TableNextColumn();
                if (line.Unit is not null)
                {
                    ImGuiText.Mono(StashWorth.Purse(line.Exalted, _book().Rate));
                }
                else
                {
                    ImGui.TextColored(OverlayInk.Quiet, "-");
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void DrawTotals(long now)
    {
        WealthTracker.Reading held = _tracker.Now;

        if (!held.Any)
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, "Nothing counted yet - the first count is a few seconds away.");
            return;
        }

        if (!ImGui.BeginTable("wealth-totals", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            // The SHOWN figure rather than the raw count, so this and the change below are the
            // same number's two readings - see WealthTracker.Showing.
            WealthTracker.Shown showing = _tracker.Showing ?? new WealthTracker.Shown(0, 0, false, 0);

            Row("holding", StashWorth.Purse(showing.Exalted, showing.Rate));

            if (!showing.Live)
            {
                Row("but", "no prices right now - this is the last figure that could be believed");
            }

            Row("stacks", held.Unpriced > 0
                ? $"{held.Stacks}   ({held.Unpriced} of them the book has no price for)"
                : held.Stacks.ToString(System.Globalization.CultureInfo.CurrentCulture));

            // WHERE THE STASH HALF CAME FROM. During a map the tabs are not loaded at all, so the
            // total rests on what they last held - which is a fact about the number that changes
            // how it should be read, not a footnote.
            Row(
                "stash",
                held.StashSeenAt == 0
                    ? "not seen yet - this is only what is carried"
                    : $"as of {WealthPanel.Ago(now - held.StashSeenAt)} ago");

            if (_tracker.Moved(Span(), now) is { } moved)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled(moved.WholeRecord ? "all of it" : Windows[_window].Label);
                ImGui.TableNextColumn();
                ImGuiText.Mono(
                    moved.Exalted > 0 ? Up : moved.Exalted < 0 ? Down : OverlayInk.Quiet,
                    $"{(moved.Exalted >= 0 ? "+" : string.Empty)}{StashWorth.Purse(moved.Exalted, showing.Rate)}"
                    + $"   over {WealthPanel.Ago(moved.Over)}");

                Split(showing.Rate, moved.Over, now);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// What the movement was made of: picked up, against the prices moving under it.
    /// </summary>
    /// <remarks>
    /// A LINE OF ITS OWN rather than a longer one above, because these answer different
    /// questions and only one of them is about how the evening went. A purse of six hundred
    /// Divine moves by tens on an ordinary price refresh, which reads as a map's loot and is
    /// not - and the figure above cannot say so, because it is one number.
    ///
    /// Shown only where there is something to say. A stretch where the prices did not move is a
    /// stretch where the line above is already the whole answer.
    /// </remarks>
    private void Split(double rate, TimeSpan over, long now)
    {
        if (_tracker.Made(over, now) is not { } made)
        {
            return;
        }

        if (Math.Abs(made.Repriced) < 1)
        {
            return;
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled("made of");
        ImGui.TableNextColumn();
        ImGuiText.Mono(
            OverlayInk.Quiet,
            $"{Signed(made.Gathered, rate)} gathered, {Signed(made.Repriced, rate)} the prices");
    }

    private static string Signed(double exalted, double rate)
        => (exalted >= 0 ? "+" : string.Empty) + StashWorth.Purse(exalted, rate);

    private static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGuiText.Mono(value);
    }

    /// <summary>Which stretch is being looked at, or everything.</summary>
    private TimeSpan Span() => Windows[_window].Span;

    /// <summary>
    /// Where the money actually is, one line per inventory.
    /// </summary>
    /// <remarks>
    /// PRICED FROM THE GAME'S OWN EXCHANGE rather than the index above it, and that is what makes
    /// the section possible at all: the index knows 38 currencies in Standard where the exchange
    /// knows 95. Built on the index, an Essence tab, a Rune tab and an Omen tab would all read as
    /// empty - not a rounding error but the wrong answer, printed confidently.
    ///
    /// AN UNTICKED TAB STAYS ON SCREEN, greyed and still counted, just not added up. Somebody
    /// taking a mule tab out of the total wants it out of the total; a row that vanished could
    /// never be found again to put back.
    /// </remarks>
    private void DrawByTab()
    {
        if (_pairs is null)
        {
            return;
        }

        _worth = OverlayLayout.Subsection("Where It Is###wealth-by-tab");
        if (!_worth)
        {
            return;
        }

        ExchangePairs? pairs = _pairs();
        IReadOnlyList<TabWorth> tabs = NetWorth.ByTab(_purse().Pages, pairs, _book(), _skipping);
        if (tabs.Count == 0)
        {
            ImGuiText.Wrapped(
                OverlayInk.Quiet,
                "No inventory read yet. The stash tabs the game has not opened are not readable, "
                + "so this fills in as they are visited.");
            return;
        }

        TabWorth all = NetWorth.Total(tabs);
        double rate = _book().Rate;

        ImGui.TextColored(Line, StashWorth.Purse(all.Exalted, rate));
        ImGui.SameLine();
        ImGuiText.Wrapped(
            OverlayInk.Quiet,
            $"across {all.Called}"
            + (all.Unpriced > 0 ? $", {all.Unpriced} stacks unpriced" : string.Empty));

        if (!ImGui.BeginTable(
                "wealth-by-tab",
                4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0f, ImGui.GetFontSize() * 12f)))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(" ");
            ImGui.TableSetupColumn("tab");
            ImGui.TableSetupColumn("worth");
            ImGui.TableSetupColumn("stacks");
            ImGui.TableHeadersRow();

            foreach (TabWorth tab in tabs)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                bool counted = tab.Counted;
                if (ImGui.Checkbox($"##tab{tab.Id}", ref counted))
                {
                    if (counted)
                    {
                        _skipping.Remove(tab.Id);
                    }
                    else
                    {
                        _skipping.Add(tab.Id);
                    }
                }

                Vector4 ink = tab.Counted ? OverlayInk.Ink : OverlayInk.Quiet;

                ImGui.TableNextColumn();
                ImGui.TextColored(ink, tab.Called);

                ImGui.TableNextColumn();
                ImGuiText.Mono(tab.Counted ? Line : OverlayInk.Quiet,
                    StashWorth.Purse(tab.Exalted, rate));

                ImGui.TableNextColumn();

                // The unpriced count sits beside the stack count rather than in a column of its
                // own, because it is only ever interesting when it is not zero.
                ImGuiText.Mono(
                    tab.Unpriced > 0 ? Warn : ink,
                    tab.Unpriced > 0
                        ? $"{tab.Stacks}  ({tab.Unpriced} unpriced)"
                        : tab.Stacks.ToString(System.Globalization.CultureInfo.CurrentCulture));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void DrawGraph(long now)
    {
        for (var i = 0; i < Windows.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.RadioButton(Windows[i].Label, _window == i))
            {
                _window = i;

                // The panel reports on the same stretch, so choosing here chooses there too -
                // two figures on screen labelled differently and answering the same question is
                // how somebody ends up believing the smaller one.
                _panel.Window = Windows[i].Span == TimeSpan.MaxValue ? TimeSpan.FromDays(365) : Windows[i].Span;
                Changed?.Invoke();
            }
        }

        TimeSpan span = Span();
        long from = span == TimeSpan.MaxValue ? 0 : now - (long)span.TotalMilliseconds;
        IReadOnlyList<WealthPoint> points = _tracker.History.Between(from, now);

        var size = new Vector2(
            Math.Max(ImGui.GetContentRegionAvail().X, ImGui.GetFontSize() * 10f),
            ImGui.GetFontSize() * 9f);

        Vector2 at = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##wealth-graph", size);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size, ImGui.ColorConvertFloat4ToU32(PlotBack), 3f);

        if (points.Count < 2)
        {
            draw.AddText(
                at + new Vector2(6f, 4f),
                ImGui.ColorConvertFloat4ToU32(OverlayInk.Quiet),
                "not enough recorded yet to draw a line");
            return;
        }

        double low = points.Min(point => point.Exalted);
        double high = points.Max(point => point.Exalted);

        // SCALED TO ITS OWN RANGE, not to zero. A purse that moved from 4,100 to 4,300 against a
        // zero baseline is a flat line along the top of the box - and the whole reason to draw
        // this is the shape of the movement, which the figures above already fail to show.
        // The floor and ceiling are printed so the scale can never be mistaken for absolute.
        double range = high - low;
        if (range <= 0)
        {
            range = 1;
            low -= 0.5;
        }

        long first = points[0].At;
        long across = Math.Max(1, points[^1].At - first);

        uint ink = ImGui.ColorConvertFloat4ToU32(Line);
        Vector2 last = default;

        for (var i = 0; i < points.Count; i++)
        {
            var here = new Vector2(
                at.X + (size.X * (points[i].At - first) / across),
                at.Y + size.Y - (float)((points[i].Exalted - low) / range * size.Y));

            if (i > 0)
            {
                draw.AddLine(last, here, ink, 1.75f);
            }

            last = here;
        }

        uint quiet = ImGui.ColorConvertFloat4ToU32(OverlayInk.Quiet);
        draw.AddText(at + new Vector2(4f, 2f), quiet, StashWorth.Purse(high, _book().Rate));
        draw.AddText(
            at + new Vector2(4f, size.Y - (ImGui.GetFontSize() * 1.2f)),
            quiet,
            StashWorth.Purse(low, _book().Rate));

        Hover(at, size, points, first, across);
    }

    /// <summary>What the purse was worth at the moment under the cursor.</summary>
    /// <remarks>
    /// The point NEAREST the cursor rather than the one before it: this record is not evenly
    /// spaced - it writes when something changes and once in a while when nothing does - so
    /// "the last point before x" can be an hour to the left of where the cursor is pointing.
    /// </remarks>
    private static void Hover(
        Vector2 at, Vector2 size, IReadOnlyList<WealthPoint> points, long first, long across)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        float x = Math.Clamp(ImGui.GetMousePos().X - at.X, 0f, size.X);
        long wanted = first + (long)(x / size.X * across);

        WealthPoint nearest = points[0];
        long best = long.MaxValue;
        foreach (WealthPoint point in points)
        {
            long apart = Math.Abs(point.At - wanted);
            if (apart < best)
            {
                best = apart;
                nearest = point;
            }
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        float line = at.X + (size.X * (nearest.At - first) / across);
        draw.AddLine(
            new Vector2(line, at.Y),
            new Vector2(line, at.Y + size.Y),
            ImGui.ColorConvertFloat4ToU32(OverlayInk.Quiet),
            1f);

        ImGuiText.MonoTooltip(
            $"{nearest.When.ToLocalTime():yyyy-MM-dd HH:mm}\n"
            + $"holding  {StashWorth.Purse(nearest.Exalted, nearest.Rate)}\n"
            + (nearest.Rate > 0
                ? $"         1 div was {StashWorth.Money(nearest.Rate)} ex then\n"
                : "         no rate recorded\n")
            + $"stacks   {nearest.Stacks}");
    }

    /// <summary>How far the record goes back, and the one control that throws it away.</summary>
    private void DrawRecord(long now)
    {
        WealthHistory history = _tracker.History;

        if (!history.Readable)
        {
            ImGuiText.Wrapped(
                Warn,
                "The record on disk could not be read and has been left exactly as it is rather "
                + "than replaced. Nothing recorded this session will be saved until it is moved "
                + "aside by hand.");
        }

        if (history.Earliest is { } begins)
        {
            ImGuiText.Mono(
                OverlayInk.Quiet,
                $"{history.Count} readings over {WealthPanel.Ago(now - begins.At)}"
                + $", since {begins.When.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, "Nothing recorded yet.");
        }

        OverlayLayout.Hint(
            "The record never clears itself - not on a new league, not on a new character, and not"
            + " when it gets long. Only the button below empties it.");

        // TWO PRESSES, because there is no undo. Everything this throws away is unrecoverable
        // and some of it is months old.
        if (!_confirmingReset)
        {
            if (ImGui.Button("Start the Record Again"))
            {
                _confirmingReset = true;
            }

            return;
        }

        ImGuiText.Wrapped(Warn, "This deletes every reading. There is no way back.");

        if (ImGui.Button("Yes, Throw It Away"))
        {
            _tracker.History.Reset(now);
            _confirmingReset = false;
            Changed?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.Button("Keep It"))
        {
            _confirmingReset = false;
        }
    }
}
