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

    private static readonly Vector4 Up = new(0.55f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Down = new(0.92f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 PlotBack = new(0.05f, 0.05f, 0.06f, 0.85f);
    private static readonly Vector4 Line = new(0.85f, 0.68f, 0.34f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.72f, 0.3f, 1f);

    private readonly WealthTracker _tracker;
    private readonly WealthPanel _panel;
    private readonly Func<PriceBook> _book;

    private int _window = 1;
    private bool _confirmingReset;

    /// <param name="tracker">The record and the live count.</param>
    /// <param name="panel">The corner panel, so the page can switch it on and share its window.</param>
    /// <param name="book">
    /// What things are worth, asked for per frame rather than held: the store replaces the book
    /// wholesale on a refresh, and a page holding the old one would price today at last hour's
    /// rate for as long as it stayed open.
    /// </param>
    public WealthWindow(WealthTracker tracker, WealthPanel panel, Func<PriceBook> book)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(book);
        _tracker = tracker;
        _panel = panel;
        _book = book;
    }

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
            ImGuiText.Hint(
                OverlayTheme.Quiet,
                "Nothing is counted while this is off, and the record simply has a gap for the "
                + "time it was - which is the honest picture of not having been watching.");
            return;
        }

        DrawPrices();
        ImGui.Separator();
        DrawTotals(now);
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

        ImGuiText.Hint(
            OverlayTheme.Quiet,
            "The corner panel can be moved, and made click-through from its own title menu so it "
            + "stops taking the mouse during a fight.");
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
        if (book.Ready)
        {
            ImGuiText.Mono(
                OverlayTheme.Quiet,
                $"{book.Count} prices   1 div = {StashWorth.Money(book.Rate)} ex");
            return;
        }

        ImGuiText.Wrapped(
            Warn,
            "No prices yet. Nothing can be valued and nothing is being written to the record - "
            + "turn prices on in the Stash tab, which is where they are fetched from.");
    }

    private void DrawTotals(long now)
    {
        WealthTracker.Reading held = _tracker.Now;

        if (!held.Any)
        {
            ImGuiText.Wrapped(OverlayTheme.Quiet, "Nothing counted yet - the first count is a few seconds away.");
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
            WealthTracker.Shown showing = _tracker.Showing ?? new WealthTracker.Shown(0, 0, false);

            Row("holding", showing.Rate > 0
                ? $"{StashWorth.Money(showing.Exalted)} ex   {StashWorth.Money(showing.Divine)} div"
                : $"{StashWorth.Money(showing.Exalted)} ex");

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
                    moved.Exalted > 0 ? Up : moved.Exalted < 0 ? Down : OverlayTheme.Quiet,
                    $"{(moved.Exalted >= 0 ? "+" : "-")}{StashWorth.Money(Math.Abs(moved.Exalted))} ex"
                    + $"   over {WealthPanel.Ago(moved.Over)}");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

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
                ImGui.ColorConvertFloat4ToU32(OverlayTheme.Quiet),
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

        uint quiet = ImGui.ColorConvertFloat4ToU32(OverlayTheme.Quiet);
        draw.AddText(at + new Vector2(4f, 2f), quiet, $"{StashWorth.Money(high)} ex");
        draw.AddText(
            at + new Vector2(4f, size.Y - (ImGui.GetFontSize() * 1.2f)),
            quiet,
            $"{StashWorth.Money(low)} ex");

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
            ImGui.ColorConvertFloat4ToU32(OverlayTheme.Quiet),
            1f);

        ImGuiText.MonoTooltip(
            $"{nearest.When.ToLocalTime():yyyy-MM-dd HH:mm}\n"
            + $"holding  {StashWorth.Money(nearest.Exalted)} ex\n"
            + (nearest.Rate > 0
                ? $"         {StashWorth.Money(nearest.Divine)} div   (1 div = {StashWorth.Money(nearest.Rate)} ex)\n"
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
                OverlayTheme.Quiet,
                $"{history.Count} readings over {WealthPanel.Ago(now - begins.At)}"
                + $", since {begins.When.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            ImGuiText.Wrapped(OverlayTheme.Quiet, "Nothing recorded yet.");
        }

        ImGuiText.Hint(
            OverlayTheme.Quiet,
            "The record never clears itself - not on a new league, not on a new character, and "
            + "not when it gets long. Only the button below empties it.");

        // TWO PRESSES, because there is no undo. Everything this throws away is unrecoverable
        // and some of it is months old.
        if (!_confirmingReset)
        {
            if (ImGui.Button("start the record again"))
            {
                _confirmingReset = true;
            }

            return;
        }

        ImGuiText.Wrapped(Warn, "This deletes every reading. There is no way back.");

        if (ImGui.Button("yes, throw it away"))
        {
            _tracker.History.Reset(now);
            _confirmingReset = false;
            Changed?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.Button("keep it"))
        {
            _confirmingReset = false;
        }
    }
}
