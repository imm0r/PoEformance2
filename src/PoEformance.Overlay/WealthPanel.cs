using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What the purse is worth and which way it is going, in a corner while you play.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE TOOLS WINDOW because it answers a question you have WHILE doing the thing,
/// and the tools window is a thing you stop to read. The graph and the record belong on a page;
/// "am I up or down on this session" is one line and belongs where it can be glanced at without
/// opening anything.
///
/// A REAL WINDOW rather than something painted on, for the reason <see cref="PreloadPanel"/>
/// gives: painted pixels cannot be dragged or dismissed, and where this sits is exactly the
/// thing somebody will want to change. The dead spot it costs is what click-through is for -
/// flip it on from the title menu and the panel stops taking the mouse entirely, which is how it
/// ends up being usable during a fight.
///
/// EVERY FIGURE IS IN THE MONO FACE. It is a number that changes while being watched, which is
/// the case a proportional face handles worst: "999" and "1000" are different widths, so the
/// line twitches sideways every time the total moves.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WealthPanel
{
    /// <summary>The id this window's lock and click-through are filed under.</summary>
    public const string ChromeId = "wealth";

    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoCollapse;

    /// <summary>
    /// How stale the stash half has to be before the panel says so.
    /// </summary>
    /// <remarks>
    /// Long enough that walking through a hideout does not make it flicker on and off, short
    /// enough that a total resting on tabs last seen an hour ago never passes for a live one.
    /// </remarks>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private static readonly Vector4 Up = new(0.55f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Down = new(0.92f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 Flat = new(0.72f, 0.70f, 0.65f, 1f);
    private static readonly Vector4 Line = new(0.85f, 0.68f, 0.34f, 1f);

    /// <summary>Whether the panel is wanted at all - the user's setting.</summary>
    public bool Enabled { get; set; }

    /// <summary>How solid its backing is. The readout's opacity, because that is what this is.</summary>
    public float Alpha { get; set; } = InterfaceStyle.DefaultReadoutOpacity;

    /// <summary>Which stretch the movement line reports on. Shared with the page.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Whether this window is pinned in place or handed to the mouse.</summary>
    public WindowChrome Chrome { get; set; } = new();

    /// <summary>Draws the panel, when there is anything to draw.</summary>
    /// <param name="tracker">What is being watched. Nothing is drawn before its first count.</param>
    /// <param name="nowMs">Unix milliseconds - the same clock the record is kept on.</param>
    public void Draw(WealthTracker? tracker, long nowMs)
    {
        if (!Enabled || tracker is null || !tracker.Now.Any || Chrome.Covered(ChromeId))
        {
            return;
        }

        Vector2 screen = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(screen.X * 0.015f, screen.Y * 0.5f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Alpha);

        bool expanded = ImGui.Begin("Wealth###wealth-corner", Chrome.Flags(ChromeId, Flags));

        // Before the early return: a collapsed window is still on screen, and where it sits is
        // what decides next frame whether it is over one of the game's panels.
        Chrome.Measure(ChromeId);

        if (!expanded)
        {
            ImGui.End();
            return;
        }

        try
        {
            Body(tracker, nowMs);
            Chrome.Menu(ChromeId);
        }
        finally
        {
            ImGui.End();
        }
    }

    private void Body(WealthTracker tracker, long nowMs)
    {
        WealthTracker.Reading now = tracker.Now;

        if (tracker.Showing is not { } showing)
        {
            ImGuiText.Mono(Flat, "nothing counted yet");
            return;
        }

        ImGuiText.Mono(Total(showing));

        if (tracker.Moved(Window, nowMs) is { } moved)
        {
            ImGuiText.Mono(Tint(moved.Exalted), Movement(moved));
        }
        else
        {
            ImGuiText.Mono(Flat, "no change recorded yet");
        }

        Sparkline(tracker, nowMs);

        // NOT LIVE means the current count could not be believed - no prices yet, or a book
        // mid-refresh - and what is on screen is the last one that could. Said plainly, because
        // the alternative is showing the count itself, which in that state is a zero that reads
        // as having spent everything.
        if (!showing.Live)
        {
            ImGuiText.Mono(Flat, "no prices - last known figure");
        }

        // Only when it matters. A total resting on tabs last seen an hour ago is not wrong, but
        // it is not what somebody reads it as - and during a map that is the ordinary state, so
        // saying it always would be noise that gets tuned out exactly when it stops being true.
        if (now.StashSeenAt == 0)
        {
            ImGuiText.Mono(Flat, "carried only - stash not seen yet");
        }
        else if (nowMs - now.StashSeenAt > (long)Stale.TotalMilliseconds)
        {
            ImGuiText.Mono(Flat, $"stash as of {Ago(nowMs - now.StashSeenAt)} ago");
        }
    }

    /// <summary>The total, in both units the tracker is measured in.</summary>
    /// <remarks>
    /// Divine is omitted rather than shown as zero when the book has no rate. A rate of nothing
    /// is not a purse worth no Divine, and "0 div" beside a real Exalted total reads as one.
    /// </remarks>
    private static string Total(WealthTracker.Shown showing)
        => showing.Rate > 0
            ? $"{StashWorth.Money(showing.Exalted)} ex   {StashWorth.Money(showing.Divine)} div"
            : $"{StashWorth.Money(showing.Exalted)} ex";

    /// <summary>
    /// The movement, with the stretch it actually covers.
    /// </summary>
    /// <remarks>
    /// THE SPAN IS NEVER LEFT OFF. When the record is younger than the window asked for, what
    /// comes back is everything it holds - and a figure labelled with the window rather than
    /// with its real span is a number somebody divides to get an hourly rate. See
    /// <see cref="WealthTracker.Moved"/>.
    /// </remarks>
    private static string Movement(WealthTracker.Movement moved)
        => $"{(moved.Exalted >= 0 ? "+" : "-")}{StashWorth.Money(Math.Abs(moved.Exalted))} ex"
           + $"   {Ago(moved.Over)}";

    private static Vector4 Tint(double change)
        => change > 0 ? Up : change < 0 ? Down : Flat;

    /// <summary>
    /// The shape of the window, small enough to sit under two lines of text.
    /// </summary>
    /// <remarks>
    /// SCALED TO ITS OWN RANGE rather than to zero, and that is the whole reason it is readable.
    /// A purse that moved from 4,100 to 4,300 Exalted drawn against a zero baseline is a flat
    /// line across the top of the box; against its own low and high it is the shape of the
    /// session. What this answers is "which way and how steadily", not "how much" - the line
    /// above it already answers that.
    /// </remarks>
    private void Sparkline(WealthTracker tracker, long nowMs)
    {
        IReadOnlyList<WealthPoint> points =
            tracker.History.Between(nowMs - (long)Window.TotalMilliseconds, nowMs);

        var size = new Vector2(ImGui.GetFontSize() * 9f, ImGui.GetFontSize() * 1.6f);
        Vector2 at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);

        if (points.Count < 2)
        {
            return;
        }

        double low = points.Min(point => point.Exalted);
        double high = points.Max(point => point.Exalted);
        double span = high - low;

        long from = points[0].At;
        long to = points[^1].At;
        long across = Math.Max(1, to - from);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint ink = ImGui.ColorConvertFloat4ToU32(Line);

        // A purse that did not move at all has no shape - drawn against its own range it would
        // be whatever the rounding of zero over zero produced. A line down the middle is the
        // honest picture of "flat".
        if (span <= 0)
        {
            draw.AddLine(
                at + new Vector2(0f, size.Y * 0.5f),
                at + new Vector2(size.X, size.Y * 0.5f),
                ink,
                1.5f);
            return;
        }

        Vector2 last = default;
        for (var i = 0; i < points.Count; i++)
        {
            var point = new Vector2(
                at.X + (size.X * (points[i].At - from) / across),
                at.Y + size.Y - (float)((points[i].Exalted - low) / span * size.Y));

            if (i > 0)
            {
                draw.AddLine(last, point, ink, 1.5f);
            }

            last = point;
        }
    }

    /// <summary>A stretch of time in one short unit - what fits beside a figure.</summary>
    internal static string Ago(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{span.TotalDays:0.#}d",
        { TotalHours: >= 1 } => $"{span.TotalHours:0.#}h",
        { TotalMinutes: >= 1 } => $"{span.TotalMinutes:0}m",
        _ => $"{Math.Max(0, span.TotalSeconds):0}s",
    };

    /// <summary>The same, from a count of milliseconds.</summary>
    internal static string Ago(long ms) => Ago(TimeSpan.FromMilliseconds(Math.Max(0, ms)));
}
