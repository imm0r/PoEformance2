using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// How much damage is being done, and to what.
/// </summary>
/// <remarks>
/// The headline is on the status readout, where it can be seen while fighting. This is for
/// the questions an instant cannot answer: which of these is actually taking it, how much of
/// the figure is watched rather than judged, and what the best moment of the map was.
///
/// The split between watched and judged is shown rather than hidden, because it is the one
/// number here that is a decision rather than a measurement - see <see cref="DamageMeter"/>.
/// Somebody who does not trust it can turn it off and watch what the figure does.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DamageWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 DpsText = new(1f, 1f, 0.6f, 1f);
    private static readonly Vector4 JudgedText = new(0.85f, 0.75f, 0.5f, 1f);

    // Dimmer than the confident credit, on purpose: this row is the least-known part of the
    // figure, and it should not read as loudly as the part that was watched.
    private static readonly Vector4 SoftText = new(0.78f, 0.62f, 0.42f, 1f);

    // The graph's three bands. A DELIBERATE RAMP from known to assumed - a bright, certain
    // yellow at the bottom, through orange, to a dull red on top - so the eye reads how much
    // of a spike was measured without being told. The text rows above use quieter versions of
    // the same three hues; a band has to hold its own against the plate behind it, and a row
    // of text does not.
    private static readonly Vector4 WatchedBar = new(1f, 0.85f, 0.30f, 0.95f);
    private static readonly Vector4 CreditedBar = new(0.95f, 0.55f, 0.22f, 0.95f);
    private static readonly Vector4 UntouchedBar = new(0.74f, 0.32f, 0.28f, 0.95f);
    private static readonly Vector4 PlotBack = new(0.08f, 0.09f, 0.11f, 0.85f);

    // Brighter than the rest, because these are the figures worth writing down: unlike the
    // headline they do not move as you fight, and unlike the peak they mean the same thing on
    // anybody else's machine.
    private static readonly Vector4 BurstText = new(0.65f, 1f, 0.75f, 1f);

    // The slowest kill, in the colour the read-cost tab uses for its worst reading, and for
    // the same reason: it is the one entry in the list somebody can act on.
    private static readonly Vector4 WorstText = OverlayInk.Warn;

    // The two halves of what a build is tuned between, told apart by colour rather than only
    // by their labels - they sit on one line and are read at a glance.
    private static readonly Vector4 SingleText = new(0.55f, 0.85f, 1f, 1f);
    private static readonly Vector4 PackText = new(0.75f, 0.7f, 1f, 1f);

    // The damage coming BACK, in the one colour nothing else here uses. It has to be told
    // apart from all three bands at once, and red is what a health bar is.
    private static readonly Vector4 TakenLine = new(1f, 0.35f, 0.38f, 1f);

    // The census, in the game's OWN rarity colours - white, blue, yellow, orange. Nobody has
    // to learn these; a player has been reading them since the first magic item. They are the
    // shared ladder rather than four numbers here, because the stash page prints the same four
    // and the two copies had already drifted apart by a shade of orange.
    private static readonly Vector4 NormalText = OverlayInk.Rarity(ItemRarity.Normal);
    private static readonly Vector4 MagicText = OverlayInk.Rarity(ItemRarity.Magic);
    private static readonly Vector4 RareText = OverlayInk.Rarity(ItemRarity.Rare);
    private static readonly Vector4 UniqueText = OverlayInk.Rarity(ItemRarity.Unique);

    private readonly DamageMeter _meter;
    private readonly Func<long> _clock;

    private bool _thisMapOnly = true;
    private float _height = 90f;

    /// <param name="clock">
    /// The same monotonic clock the reader stamps with, so "how long since this was hit"
    /// compares two readings of one clock rather than two different ones.
    /// </param>
    public DamageWindow(DamageMeter meter, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>The area being read right now, so "this map only" has something to mean.</summary>
    public uint CurrentArea { get; set; }

    /// <summary>
    /// The heat map painted over the game's own map, if one was attached.
    /// </summary>
    /// <remarks>
    /// Its switches live in THIS tab rather than beside the other map settings, because what
    /// it paints is the damage - it is a second view of the numbers on this page, and putting
    /// its controls where the numbers are is what makes that obvious.
    /// </remarks>
    public HeatLayer? Heat { get; set; }

    /// <summary>
    /// Draws the tab: the figures, then the graph, then what died - and the settings last.
    /// </summary>
    /// <remarks>
    /// THE CONTROLS USED TO PUSH THE DATA OFF THE SCREEN. Four switches and two sliders sat
    /// between the headline and the graph, with eight lines of prose among them explaining how
    /// each figure is arrived at - so the two things somebody opens this tab to look at, the
    /// graph and the kill list, started below the fold. Everything on this page except those
    /// two is either a number that belongs in the header or a setting that is changed once.
    ///
    /// So: figures, graph, table, and the settings folded under them. The settings did not get
    /// smaller, they got out of the way - which is the only thing wrong with them.
    /// </remarks>
    public void DrawTab()
    {
        DrawMetrics();

        ImGui.Separator();
        DrawGraph();
        DrawGraphControls();

        ImGui.Separator();
        DrawKills();

        ImGui.Separator();
        DrawTargets();

        ImGui.Spacing();
        DrawMeasurement();
        DrawHeat();
    }

    /// <summary>
    /// The headline figures, as a block rather than a paragraph.
    /// </summary>
    /// <remarks>
    /// THE SPLIT IS THE POINT OF THIS TAB and it was four lines of prose. A damage number that
    /// does not say how much of itself is inferred is a number nobody can check, and on a build
    /// that one-shots packs the inferred part is the majority - so the split is the difference
    /// between a figure and a claim. But saying that took four lines reading "credited: 891.4k
    /// from ones we were already hurting", and the four sentences pushed the graph down a
    /// screen to explain something that is read once and then known.
    ///
    /// Four figures on one line, each with its sentence in a tooltip. What is on screen is the
    /// comparison - which of the four is large - and that is what the split is FOR.
    /// </remarks>
    private void DrawMetrics()
    {
        ImGuiText.Mono(DpsText, $"{Number(_meter.Dps)} dps");
        ImGui.SameLine();
        ImGuiText.Mono(DimText, $"   this area {Number(_meter.Total)} total");

        DrawBurst();

        Metric(
            "Watched", Number(_meter.Observed), DimText,
            $"Seen coming off {_meter.Hurt} monsters' health. The part that rests on nothing but"
            + " a reading.");

        OverlayLayout.Cell(1);
        Metric(
            "Credited", Number(_meter.CreditedHurt), JudgedText,
            "Damage dealt to monsters that were already being hurt, and then vanished. Nearly"
            + " certain: something was hurting it, and then it was gone.");

        OverlayLayout.Cell(2);
        Metric(
            "Untouched", Number(_meter.CreditedUntouched), SoftText,
            $"{Share(_meter.CreditedUntouched, _meter.Total)} of the total. Monsters that"
            + " vanished without a scratch seen - this half rests entirely on the assumption,"
            + " and is where one that merely walked off would land.");

        if (_meter.WithheldCount > 0)
        {
            OverlayLayout.Cell(3);
            Metric(
                "Out of range", Number(_meter.Withheld), DimText,
                $"{_meter.WithheldCount} vanished too far away to have been killed, so their"
                + " health was refused rather than credited.");
        }
    }

    /// <summary>One headline figure: its name, its value, and its sentence under the pointer.</summary>
    /// <remarks>
    /// This was written here first and is now <see cref="OverlayLayout.Figure"/>, because the
    /// wealth page wanted the same block of headline numbers and a second copy of a layout shape
    /// is how nineteen field widths happened. Kept as a name so the call sites below still read
    /// as what they are.
    /// </remarks>
    private static void Metric(string label, string value, Vector4 ink, string tooltip)
        => OverlayLayout.Figure(label, value, ink, tooltip);

    /// <summary>The graph's own controls, on one line under it.</summary>
    /// <remarks>
    /// UNDER THE GRAPH AND ON ONE LINE, because they are about the graph: what it covers, how
    /// tall it is, how hard it is smoothed. They were spread over three places - two above the
    /// figures, one inside the plot - so changing how the graph looked meant finding the
    /// control in a different part of the page each time.
    /// </remarks>
    private void DrawGraphControls()
    {
        OverlayLayout.Toggle("Reset per Map", ref _thisMapOnly);
        OverlayLayout.Hint("Off keeps counting across areas, so the graph spans the session.");

        ImGui.SameLine();
        if (ImGui.Button("Reset DPS"))
        {
            _meter.History.Clear();
        }

        OverlayLayout.Next();
        OverlayLayout.Narrow.Slider("##dmg-height", ref _height, 40f, 240f, "%.0f px");
        ImGui.SameLine();
        ImGui.TextColored(DimText, "Graph height");

        OverlayLayout.Next();
        float smoothing = _meter.SmoothingSeconds;
        if (OverlayLayout.Narrow.Slider("##dmg-smoothing", ref smoothing, 0.1f, 5f, "%.2f s"))
        {
            _meter.SmoothingSeconds = smoothing;
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "Smooth window");
        OverlayLayout.Hint(
            "How long a window the headline dps is averaged over. The peak moves with this, which"
            + " is why the peak cannot be compared with anybody else's.");
    }

    /// <summary>
    /// How the figure is arrived at: the two settings that decide what counts, folded away.
    /// </summary>
    /// <remarks>
    /// FOLDED, because these are decided once and then left - and left open they were the two
    /// controls, the warning and the six diagnostic lines that stood between the headline and
    /// the graph. Folded is not hidden: the fold says what it is, and everything that argues
    /// about whether to trust the figure is behind it in one place instead of scattered up the
    /// page.
    /// </remarks>
    private void DrawMeasurement()
    {
        if (!OverlayLayout.Subsection("How the Figure Is Measured"))
        {
            return;
        }

        bool counting = _meter.CountKills;
        if (OverlayLayout.Toggle("Track Off-Screen / Vanished Kills", ref counting))
        {
            _meter.CountKills = counting;
        }

        // The reason the switch above is not merely a preference: a dead monster is dropped
        // from the snapshot rather than lingering at zero, so the last chunk of every kill -
        // and the whole of anything deleted between two reads - is never watched falling.
        if (!counting)
        {
            // A caveat about the FIGURE rather than about the switch, and it applies while the
            // switch is off - which is when nobody is hovering it. So it stays on screen.
            OverlayLayout.Warning(
                "With this off the figure is only what was watched, which under-reports by the"
                + " whole of every killing blow.");
        }
        else
        {
            // GRID UNITS, the same as the rows above and as the entity browser. It was in
            // screens, which read more naturally but could not be compared with anything:
            // the figures it has to be set against - how far the list reaches, where things
            // went missing, GameHelper2's measured bubble near 200 - are all in grid, and a
            // control in its own private unit cannot be set from them.
            float limit = _meter.CreditWithin / MapView.WorldToGrid;
            if (OverlayLayout.Slider("Max Range", ref limit, 0f, 600f,
                    limit <= 0f ? "any distance" : "%.0f grid"))
            {
                _meter.CreditWithin = limit * MapView.WorldToGrid;
            }

            OverlayLayout.Hint(
                "Measured from where the monster was last seen. Anything that vanished further"
                + " away than this is refused rather than credited.");
        }

        DrawFurthest();
    }

    /// <summary>
    /// The heat map's switches - what it paints, and whether it paints at all.
    /// </summary>
    /// <remarks>
    /// HERE rather than beside the other map settings, because what it paints is the damage:
    /// it is a second view of the numbers on this page, and putting its controls where the
    /// numbers are is what makes that obvious.
    /// </remarks>
    private void DrawHeat()
    {
        if (Heat is not HeatLayer heat)
        {
            return;
        }

        bool painting = heat.Enabled;
        if (OverlayLayout.Toggle("Damage Heatmap Overlay", ref painting))
        {
            heat.Enabled = painting;
        }

        OverlayLayout.Hint(
            "Paints the game's own map by where the damage happened. Open the map to see it. The"
            + " scale is this area's 95th busiest patch, so one boss cannot flatten the rest.");

        if (!painting)
        {
            return;
        }

        // Three, because they are three different questions about the same run: where the
        // fighting was, where it went badly, and where the time actually went - and a map can
        // answer one of them well and the other two not at all.
        //
        // A REAL INDENT rather than a leading SameLine, which is what put the first of them
        // hard against the switch above and the rest trailing off its line.
        float step = OverlayLayout.Step();
        ImGui.Indent(step);
        try
        {
            bool first = true;
            foreach ((HeatOf what, string label) in Sources)
            {
                if (!first)
                {
                    OverlayLayout.Next();
                }

                first = false;
                if (ImGui.RadioButton(label, heat.Showing == what))
                {
                    heat.Showing = what;
                }
            }

            OverlayLayout.Note($"{_meter.Heat.Count} patches held.");
        }
        finally
        {
            ImGui.Unindent(step);
        }
    }

    /// <summary>What the heat map can be asked to paint.</summary>
    private static readonly (HeatOf What, string Label)[] Sources =
    [
        (HeatOf.Dealt, "damage done"),
        (HeatOf.Taken, "damage taken"),
        (HeatOf.Time, "time spent"),
    ];

    /// <summary>
    /// What every rare and unique cost, newest first.
    /// </summary>
    /// <remarks>
    /// THE QUESTION BEHIND MOST SINGLE-TARGET COMPLAINTS. "How long does a rare take" cannot
    /// be got out of a rate averaged over a map, because no map is one long fight - the average
    /// is mostly the packs. One line per kill answers it directly, and the slowest one is the
    /// monster the build actually has trouble with.
    /// </remarks>
    private void DrawKills()
    {
        uint scope = _thisMapOnly ? CurrentArea : 0;
        IReadOnlyList<KillRecord> kills = _meter.Kills.In(scope);

        if (!OverlayLayout.Subsection($"Rares and Uniques ({kills.Count})###dmg-kills", openByDefault: true))
        {
            return;
        }

        if (kills.Count == 0)
        {
            ImGui.TextColored(DimText, "none down yet in this scope");
            return;
        }

        // The slowest first and on its own line, because it is the one worth acting on and it
        // would otherwise be somewhere in a list sorted by when things happened.
        if (_meter.Kills.Worst(scope) is { } worst)
        {
            ImGui.TextColored(
                WorstText,
                $"slowest: {worst.Name}  {worst.Seconds:F1}s  ({Number(worst.Damage)} at {Number(worst.Dps)} dps)");
        }

        if (!ImGui.BeginTable(
                "##dmg-kill-table",
                4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable
                | ImGuiTableFlags.Resizable))
        {
            return;
        }

        try
        {
            // SORTABLE, because the list answers different questions depending on the order:
            // by dps it is which fight went badly, by damage it is what was worth the time, by
            // name it is whether one kind keeps showing up. It arrived in the order things
            // happened, which answers only "what did I just kill" - and the slowest kill had
            // to be pulled out and printed above the table precisely because the order buried
            // it.
            ImGui.TableSetupColumn("monster", ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("took");
            ImGui.TableSetupColumn("damage");
            ImGui.TableSetupColumn("dps");
            ImGui.TableHeadersRow();

            foreach (KillRecord kill in Sorted(kills))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(RarityText(kill.Rarity), kill.Name.Length > 0 ? kill.Name : "(unnamed)");

                ImGui.TableNextColumn();

                // A one-shot has no fight to time, and printing 0.0s would read as a
                // measurement rather than as the absence of one.
                if (kill.Watched)
                {
                    ImGuiText.Mono($"{kill.Seconds:F1}s");
                }
                else
                {
                    ImGui.TextColored(SoftText, "one-shot");
                }

                ImGui.TableNextColumn();
                ImGuiText.Mono(Number(kill.Damage));

                ImGui.TableNextColumn();
                if (kill.Watched)
                {
                    ImGuiText.Mono(DimText, Number(kill.Dps));
                }
                else
                {
                    ImGui.TextColored(SoftText, "-");
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The kills in whatever order the table's own header was last clicked into.
    /// </summary>
    /// <remarks>
    /// ASKED PER FRAME rather than kept sorted, because ImGui owns the sort state: the header
    /// row writes it when somebody clicks, and there is nowhere else it could live without two
    /// copies that disagree. The cost is a sort of a list that holds one entry per rare or
    /// unique in a map - a few dozen - which is nothing beside what the frame is already doing.
    ///
    /// The unsorted list is returned UNCOPIED when nothing has been chosen, so the ordinary
    /// case allocates nothing at all.
    /// </remarks>
    private static unsafe IReadOnlyList<KillRecord> Sorted(IReadOnlyList<KillRecord> kills)
    {
        // ImGui hands back a null pointer until the header row has been drawn and somebody has
        // chosen a column, so the wrapper cannot be trusted without checking the pointer it
        // wraps - reading SpecsCount off a null one is a crash rather than a zero.
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || specs.SpecsCount == 0)
        {
            return kills;
        }

        ImGuiTableColumnSortSpecsPtr by = specs.Specs;
        bool up = by.SortDirection == ImGuiSortDirection.Ascending;

        var order = new List<KillRecord>(kills);
        order.Sort((left, right) =>
        {
            int said = by.ColumnIndex switch
            {
                1 => left.Seconds.CompareTo(right.Seconds),
                2 => left.Damage.CompareTo(right.Damage),
                3 => left.Dps.CompareTo(right.Dps),
                _ => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
            };

            return up ? said : -said;
        });

        return order;
    }

    /// <summary>A monster's rarity in the game's own colour for it.</summary>
    private static Vector4 RarityText(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Magic => MagicText,
        ItemRarity.Rare => RareText,
        >= ItemRarity.Unique => UniqueText,
        _ => NormalText,
    };

    /// <summary>
    /// The most damage sustained over one second, five and ten - the comparable numbers.
    /// </summary>
    /// <remarks>
    /// THREE LENGTHS because they answer three different questions, and a build can be good at
    /// one and poor at the next: a second is the opening hit, five is a rare going down, ten is
    /// whether it can keep going. One figure would hide exactly the difference somebody is
    /// tuning for.
    ///
    /// <c>Peak</c> is still shown, dimmed and last, with what it actually is written beside it -
    /// it is the high-water mark of a smoothed average and moves with the smoothing slider, so
    /// it is the one number here that cannot be compared with anybody else's.
    /// </remarks>
    private void DrawBurst()
    {
        uint scope = _thisMapOnly ? CurrentArea : 0;

        float second = _meter.History.Best(1, scope);
        if (second <= 0f)
        {
            return;
        }

        ImGuiText.Mono(
            BurstText,
            $"best 1s {Number(second)}"
            + $"   5s {Number(_meter.History.Best(5, scope))}"
            + $"   10s {Number(_meter.History.Best(10, scope))}");

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"   peak {Number(_meter.Peak)} (smoothed, moves with the slider)");

        DrawSplit(scope);
    }

    /// <summary>
    /// What the build does against one monster, and against a crowd.
    /// </summary>
    /// <remarks>
    /// The two numbers a build is actually tuned between, got for nothing: every bar already
    /// records how many monsters were around, so the stretches with one monster near ARE the
    /// single-target fight and no dummy-hitting exercise is needed to find them.
    ///
    /// Shown with HOW LONG each was measured over, because that is what says whether to believe
    /// it. Twelve seconds of single target across a map is a figure; two is an accident.
    /// </remarks>
    private void DrawSplit(uint scope)
    {
        (DamageHistory.Split single, DamageHistory.Split pack) = _meter.History.Alone(scope);
        if (single.Seconds <= 0 && pack.Seconds <= 0)
        {
            return;
        }

        if (single.Seconds > 0)
        {
            ImGuiText.Mono(
                SingleText, $"single target {Number(single.Dps)}");
            ImGui.SameLine();
            ImGui.TextColored(DimText, $"over {single.Seconds:F0}s alone with one   ");
            ImGui.SameLine();
        }

        if (pack.Seconds > 0)
        {
            ImGuiText.Mono(PackText, $"in a pack {Number(pack.Dps)}");
            ImGui.SameLine();
            ImGui.TextColored(DimText, $"over {pack.Seconds:F0}s with {DamageHistory.Crowd}+");
            ImGui.SameLine();
        }

        // Ends the SameLine run above, whichever of the two ran last.
        ImGui.NewLine();
    }

    /// <summary>
    /// What the damage did over the map, as a stacked band per quarter second.
    /// </summary>
    /// <remarks>
    /// STACKED AND COLOURED rather than one line, because the split is the interesting part.
    /// The height of a bar is the damage rate, and the colours say how much of it was really
    /// known: the solid base was watched off monsters' health, the middle was credited to
    /// monsters already being hurt, and the top rests entirely on the assumption. A burst made
    /// of the top band is a different event from the same burst made of the base, and one line
    /// cannot tell them apart - which matters here more than in most graphs, because on a
    /// build that one-shots packs the assumed part is the majority.
    ///
    /// Drawn by hand rather than with PlotLines, which takes one series in one colour and is
    /// therefore exactly the graph this must not be.
    /// </remarks>
    private void DrawGraph()
    {
        uint scope = _thisMapOnly ? CurrentArea : 0;

        // The controls that used to be here are on one line UNDER the plot now - see
        // DrawGraphControls. Above it they were the first thing on the graph's own block and
        // pushed the plot itself further down a page that already started too low.
        IReadOnlyList<DamageSample> samples = _meter.History.In(scope);
        if (samples.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                _thisMapOnly && CurrentArea == 0
                    ? "not in an area - nothing to scope to"
                    : "nothing measured yet - the graph fills as damage is done");
            return;
        }

        float tallest = _meter.History.Highest(scope);
        Vector2 size = new(MathF.Max(120f, ImGui.GetContentRegionAvail().X - 90f), _height);
        Vector2 at = ImGui.GetCursorScreenPos();

        // An invisible button rather than a Dummy: it reserves the same space AND makes the
        // plot hoverable, which is what turns a picture into something a number can be read
        // off. A graph nobody can query is decoration.
        ImGui.InvisibleButton("##dmg-plot", size);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size, ImGui.ColorConvertFloat4ToU32(PlotBack), 3f);

        if (tallest <= 0f)
        {
            draw.AddText(at + new Vector2(6f, 4f), ImGui.ColorConvertFloat4ToU32(DimText), "no damage in this scope");
            return;
        }

        // Newest on the RIGHT, and only as many bars as there are pixels: a map's worth of
        // samples is thousands and the plot is hundreds wide, so drawing them all would stack
        // several on every column and cost the frames to do it. The recent end is the end
        // anybody is looking at, so that is the end that is kept.
        int columns = Math.Min(samples.Count, (int)size.X);
        float step = size.X / columns;
        int first = samples.Count - columns;

        uint watched = ImGui.ColorConvertFloat4ToU32(WatchedBar);
        uint credited = ImGui.ColorConvertFloat4ToU32(CreditedBar);
        uint untouched = ImGui.ColorConvertFloat4ToU32(UntouchedBar);

        for (int i = 0; i < columns; i++)
        {
            DamageSample sample = samples[first + i];
            float left = at.X + (i * step);
            float right = left + MathF.Max(1f, step);
            float bottom = at.Y + size.Y;

            // Bottom up, in order of how well known each part is. Each band's height is its
            // own share of the bar, so the total is the rate and the colours are the split.
            bottom = Band(draw, left, right, bottom, sample.Watched, tallest, size.Y, watched);
            bottom = Band(draw, left, right, bottom, sample.Credited, tallest, size.Y, credited);
            Band(draw, left, right, bottom, sample.Untouched, tallest, size.Y, untouched);
        }

        // WHAT WAS COMING BACK, over the bars rather than beside them. A line on its own scale,
        // because the two figures are nowhere near each other in size - a build deals tens of
        // thousands and dies to four - and drawn to the same ceiling it would be a flat line
        // along the bottom saying nothing. What it is for is WHEN, not how much: the moment the
        // damage out stopped and the damage in started is a shape, and it only shows on one
        // timeline.
        DrawTaken(draw, at, size, samples, first, columns, step);

        // The ceiling, so a bar's height can be turned into a number without hovering it.
        draw.AddLine(at, at + new Vector2(size.X, 0f), ImGui.ColorConvertFloat4ToU32(DimText), 1f);
        draw.AddText(
            at + new Vector2(4f, 2f), ImGui.ColorConvertFloat4ToU32(DimText), $"{Number(tallest)} dps");

        DrawHover(draw, at, size, samples, first, columns, step);

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(WatchedBar, "watched");
        ImGui.TextColored(CreditedBar, "credited");
        ImGui.TextColored(UntouchedBar, "untouched");
        ImGui.TextColored(TakenLine, "taken");
        ImGui.TextColored(DimText, $"{_meter.History.SecondsIn(scope) / 60:F1} min");
        ImGui.EndGroup();

        DrawWorstHit();
    }

    /// <summary>
    /// The single biggest thing that came off the pool, and what was around when it did.
    /// </summary>
    /// <remarks>
    /// ONE READ'S drop rather than a second's worth, because "what nearly killed me" is about
    /// one moment. A slow bleed to the same total is a different problem with a different
    /// answer, and averaging the two together loses both.
    ///
    /// As a SHARE of the pool as well as a number, because that is the part that means
    /// something: four thousand is a scratch or most of a life bar depending on the character,
    /// and the tool knows which.
    /// </remarks>
    private void DrawWorstHit()
    {
        if (_meter.WorstHit <= 0)
        {
            return;
        }

        ImGui.TextColored(
            TakenLine,
            $"taken {Number(_meter.Taken)} this area"
            + $"   -   worst hit {Number(_meter.WorstHit)} ({_meter.WorstHitShare:P0} of the pool)");

        MonsterCensus against = _meter.WorstHitAgainst;
        if (against.Any)
        {
            ImGui.SameLine();
            ImGui.TextColored(DimText, $"   with {Describe(against)} nearby");
        }
    }

    /// <summary>A census in a few words, rarest first - what was actually worth naming.</summary>
    private static string Describe(MonsterCensus census)
    {
        var parts = new List<string>(4);
        if (census.Unique > 0)
        {
            parts.Add($"{census.Unique} unique");
        }

        if (census.Rare > 0)
        {
            parts.Add($"{census.Rare} rare");
        }

        if (census.Magic > 0)
        {
            parts.Add($"{census.Magic} magic");
        }

        if (census.Normal > 0)
        {
            parts.Add($"{census.Normal} normal");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The damage coming back, as a line on its own scale over the bars.</summary>
    /// <remarks>
    /// ITS OWN SCALE, said out loud because it is a graph with two y axes and that is normally
    /// a thing to avoid. Here the alternative is worse: a build deals tens of thousands and
    /// dies to four, so on the bars' ceiling this would be a flat line along the bottom. The
    /// line answers WHEN rather than how much, and the exact figure is in the hover.
    /// </remarks>
    private static void DrawTaken(
        ImDrawListPtr draw,
        Vector2 at,
        Vector2 size,
        IReadOnlyList<DamageSample> samples,
        int first,
        int columns,
        float step)
    {
        float worst = 0f;
        for (int i = 0; i < columns; i++)
        {
            worst = MathF.Max(worst, samples[first + i].Taken);
        }

        if (worst <= 0f)
        {
            return;
        }

        // Two thirds of the plate, so the line has room to peak without touching the ceiling
        // the bars are measured against and being read as one of them.
        float room = size.Y * 0.66f;
        uint colour = ImGui.ColorConvertFloat4ToU32(TakenLine);
        Vector2? previous = null;

        for (int i = 0; i < columns; i++)
        {
            var point = new Vector2(
                at.X + (i * step) + (step * 0.5f),
                at.Y + size.Y - (samples[first + i].Taken / worst * room));

            if (previous is Vector2 last)
            {
                draw.AddLine(last, point, colour, 1.5f);
            }

            previous = point;
        }
    }

    /// <summary>One band of a stacked bar. Returns the top it reached, for the next one up.</summary>
    private static float Band(
        ImDrawListPtr draw, float left, float right, float bottom, float value, float tallest, float height, uint colour)
    {
        if (value <= 0f)
        {
            return bottom;
        }

        float top = bottom - (value / tallest * height);
        draw.AddRectFilled(new Vector2(left, top), new Vector2(right, bottom), colour);
        return top;
    }

    /// <summary>The three numbers behind whichever bar the cursor is on.</summary>
    /// <remarks>
    /// In a tooltip rather than on the plot, because the answer is wanted for one bar at a
    /// time and labelling every bar is how a graph becomes unreadable.
    /// </remarks>
    private void DrawHover(
        ImDrawListPtr draw,
        Vector2 at,
        Vector2 size,
        IReadOnlyList<DamageSample> samples,
        int first,
        int columns,
        float step)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        int column = Math.Clamp((int)((ImGui.GetMousePos().X - at.X) / step), 0, columns - 1);
        float line = at.X + (column * step) + (step * 0.5f);
        draw.AddLine(
            new Vector2(line, at.Y), new Vector2(line, at.Y + size.Y), ImGui.ColorConvertFloat4ToU32(DimText), 1f);

        DamageSample sample = samples[first + column];
        ImGui.BeginTooltip();

        // Padded into a column, like the split above the graph and for the same reason.
        ImGuiText.Mono(DpsText, $"{Number(sample.Total)} dps");
        ImGuiText.Mono(WatchedBar, $"watched    {Number(sample.Watched)}");
        ImGuiText.Mono(CreditedBar, $"credited   {Number(sample.Credited)}");
        ImGuiText.Mono(UntouchedBar, $"untouched  {Number(sample.Untouched)}");

        if (sample.Taken > 0f)
        {
            ImGuiText.Mono(TakenLine, $"taken      {Number(sample.Taken)}");
        }

        // WHAT IT WAS AGAINST, which is what makes the number above mean anything: five
        // thousand into a rare is a build working, and five thousand into forty white monsters
        // is a build that cannot single-target.
        ImGui.Separator();
        MonsterCensus nearby = sample.Nearby;
        if (!nearby.Any)
        {
            ImGui.TextColored(DimText, "nothing nearby");
        }
        else
        {
            ImGui.TextColored(DimText, $"{nearby.All} nearby");
            Count("normal", nearby.Normal, NormalText);
            Count("magic", nearby.Magic, MagicText);
            Count("rare", nearby.Rare, RareText);
            Count("unique", nearby.Unique, UniqueText);
        }

        ImGui.EndTooltip();

        // Only the rarities that are actually there. A row of noughts is four lines saying
        // nothing, and the interesting part of a census is what IS in it.
        static void Count(string what, int many, Vector4 colour)
        {
            if (many > 0)
            {
                ImGui.TextColored(colour, $"  {many,3}  {what}");
            }
        }
    }

    /// <summary>
    /// How far away the furthest disappearance was - the check on the whole assumption.
    /// </summary>
    /// <remarks>
    /// Says what the number MEANS rather than just printing it, because it is read once to
    /// settle a question and then never again: a figure that stays inside fighting range is
    /// evidence that everything which went missing died, and one that runs to several
    /// screens is evidence that the game drops distant monsters and the gate is doing real
    /// work. Left unexplained it is just another row nobody knows what to do with.
    /// </remarks>
    private void DrawFurthest()
    {
        if (_meter.Vanished <= 0 || _meter.FurthestVanish < 0f)
        {
            return;
        }

        // IN GRID UNITS, which is what the entity browser measures distance in and what
        // GameHelper2 measured the network bubble in (~200, "by checking when entity leave
        // the bubble"). Screens were the wrong unit for a diagnostic: the conversion to them
        // is the one derived constant in this feature, so "1.45 screens" could mean 255 grid
        // or 128 depending on whether the coverage figure it comes from is a radius or a
        // diameter - and that is exactly the difference between outside the bubble and well
        // inside it. A number nobody can place is not a measurement.
        float furthest = _meter.FurthestVanish / MapView.WorldToGrid;
        float gate = _meter.CreditWithin / MapView.WorldToGrid;

        // The denominator: how far the entity list reaches at all. Without it the figure
        // above is far only in relation to nothing.
        if (_meter.FurthestSeen > 0f)
        {
            float seen = _meter.FurthestSeen / MapView.WorldToGrid;
            float edge = _meter.VanishedAtEdge;

            ImGui.TextColored(
                DimText,
                ImGuiText.Escape(
                    $"  seen out to: {seen:0} grid  - the reach of the entity list, so the"
                    + " furthest anything can be and still be watched"));

            // Reported WITHOUT a verdict, because it cannot carry one. Both figures are
            // maxima, and maxima coincide for free: the monster seen furthest away leaves
            // the list eventually, and if the player walked away from it its last sighting
            // is at that same distance. A ratio of 1 says only that something once left at
            // the edge - which happens all map long - and nothing about where the damage
            // was booked. The line below is the one that answers that.
            ImGui.TextColored(
                DimText,
                ImGuiText.Escape(
                    $"  gone at:     {furthest:0} grid  = {edge * 100:0}% of that, at the furthest"));

            // THE FIGURE THAT DECIDES. Weighted by pool rather than counted per vanish,
            // because the question is about the damage and not the population: one boss
            // credited at arm's length outweighs a hundred trash monsters that drifted off
            // the edge, and a plain average would report the opposite.
            if (_meter.CreditedMeanDistance >= 0f)
            {
                float mean = _meter.CreditedMeanDistance / MapView.WorldToGrid;
                bool close = mean <= seen * 0.5f;

                ImGui.TextColored(
                    close ? DimText : SoftText,
                    ImGuiText.Escape(
                        $"  credit from: {mean:0} grid out on average, weighted by pool"
                        + (close
                            ? "  - well inside the reach, so it is coming from things that died"
                            : "  - out near the reach, so it is coming from things that walked away")));
            }
        }

        // NOTHING REFUSED: one line about the limit, not a second copy of the figure above.
        // Two rows for one event is what let this readout contradict itself once, calling the
        // same 1.45 "beyond killing range" and "clear of the limit" a line apart.
        if (_meter.WithheldCount == 0)
        {
            ImGui.TextColored(
                DimText,
                gate > 0f && furthest < gate
                    ? $"  the {gate:0}-grid limit sits above all of them, so it is doing nothing"
                    : "  nothing has been refused by the limit");

            return;
        }

        // Once the limit HAS refused something, how far the believed ones reach is a separate
        // event from how far the disappearances reach, and the gap between them is the
        // finding: the limit's job is to sit in it. Pressed against the counted figure, it is
        // cutting through one population rather than between two.
        if (_meter.FurthestCounted < 0f)
        {
            return;
        }

        float counted = _meter.FurthestCounted / MapView.WorldToGrid;
        bool roomy = gate <= 0f || counted <= gate * 0.75f;

        ImGui.TextColored(
            roomy ? DimText : SoftText,
            $"  counted out to: {counted:0} grid, limit {gate:0}"
            + (roomy
                ? "  - clear of it, so it is not cutting into kills"
                : "  - close to it; it may be refusing real kills, so try widening it"));
    }

    private void DrawTargets()
    {
        IReadOnlyList<DamageTarget> targets = _meter.Targets(_clock());

        if (targets.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                _meter.Measuring
                    ? "nothing being hit right now"
                    : "nothing measured yet - the figures start with the first monster that takes damage");
            return;
        }

        if (!ImGui.BeginTable("damage-targets", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("target", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("dps", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();

            foreach (DamageTarget target in targets)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                // Escaped, because this one is not a line somebody wrote - it is the
                // monster's own name out of the game, and a mod called "100% Increased"
                // would be read as a format specifier.
                ImGui.TextColored(Tint(target.Rarity), ImGuiText.Escape(target.Name));

                // A single "%" rather than the doubled one this needed as a format string:
                // Mono is TextUnformatted, so the sign is a sign.
                ImGui.TableNextColumn();
                ImGuiText.Mono(DimText, target.Percent >= 0 ? $"{target.Percent}%" : "-");

                ImGui.TableNextColumn();
                ImGuiText.Mono(DpsText, Number(target.Dps));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>The game's own rarity colours, so a pack leader reads the same as everywhere else.</summary>
    /// <remarks>
    /// Kept as a method rather than replaced by its one line at the call sites, because "a
    /// monster's rarity" and "an item's rarity" are the same ladder read for different reasons -
    /// this is where a monster-only exception would go if one is ever wanted.
    /// </remarks>
    private static Vector4 Tint(ItemRarity rarity) => OverlayInk.Rarity(rarity);

    /// <summary>What share one part is of the whole, for reading the split at a glance.</summary>
    private static string Share(long part, long whole)
        => whole <= 0 ? "-" : $"{100d * part / whole:0}%";

    /// <summary>
    /// A damage figure short enough to read at a glance.
    /// </summary>
    /// <remarks>
    /// Path of Exile 2 damage runs to seven figures, and the digits past the first three are
    /// noise on a number that changes every frame - "1.4M" answers the question and "1438291"
    /// makes it be worked out.
    /// </remarks>
    public static string Number(double value)
    {
        double magnitude = Math.Abs(value);

        return magnitude >= 1_000_000_000 ? $"{value / 1_000_000_000:0.##}B"
            : magnitude >= 1_000_000 ? $"{value / 1_000_000:0.##}M"
            : magnitude >= 1_000 ? $"{value / 1_000:0.#}k"
            : $"{value:0}";
    }
}
