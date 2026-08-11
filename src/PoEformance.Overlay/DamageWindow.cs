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
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
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
    private static readonly Vector4 WorstText = new(1f, 0.62f, 0.35f, 1f);

    // The two halves of what a build is tuned between, told apart by colour rather than only
    // by their labels - they sit on one line and are read at a glance.
    private static readonly Vector4 SingleText = new(0.55f, 0.85f, 1f, 1f);
    private static readonly Vector4 PackText = new(0.75f, 0.7f, 1f, 1f);

    // The damage coming BACK, in the one colour nothing else here uses. It has to be told
    // apart from all three bands at once, and red is what a health bar is.
    private static readonly Vector4 TakenLine = new(1f, 0.35f, 0.38f, 1f);

    // The census, in the game's OWN rarity colours - white, blue, yellow, orange. Nobody has
    // to learn these; a player has been reading them since the first magic item.
    private static readonly Vector4 NormalText = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 MagicText = new(0.53f, 0.53f, 1f, 1f);
    private static readonly Vector4 RareText = new(1f, 1f, 0.46f, 1f);
    private static readonly Vector4 UniqueText = new(0.68f, 0.37f, 0.13f, 1f);

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

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        ImGui.TextColored(DpsText, $"{Number(_meter.Dps)} dps");
        ImGui.SameLine();
        ImGui.TextColored(DimText, $"   this area {Number(_meter.Total)} total");

        DrawBurst();

        ImGui.Separator();

        // WHERE the figure came from, split three ways by how much each part is really
        // KNOWN. A damage number that does not say how much of itself is inferred is a
        // number nobody can check, and on a build that one-shots packs the inferred part is
        // the majority - so the split is the difference between a figure and a claim.
        ImGui.TextColored(DimText, $"watched:   {Number(_meter.Observed)}  off {_meter.Hurt} monsters' health");

        // Nearly certain: something was hurting it, and then it was gone.
        ImGui.TextColored(
            JudgedText,
            $"credited:  {Number(_meter.CreditedHurt)}  from ones we were already hurting");

        // The half that rests entirely on the assumption - and the half a monster that
        // merely walked off would land in.
        ImGui.TextColored(
            SoftText,
            ImGuiText.Escape(
                $"  untouched: {Number(_meter.CreditedUntouched)}  vanished without a scratch seen"
                + $"   ({Share(_meter.CreditedUntouched, _meter.Total)} of the total)"));

        if (_meter.WithheldCount > 0)
        {
            ImGui.TextColored(
                DimText,
                $"  refused:   {Number(_meter.Withheld)}  from {_meter.WithheldCount} that vanished"
                + " too far away to have been killed");
        }

        DrawFurthest();

        bool counting = _meter.CountKills;
        if (ImGui.Checkbox("count what vanished  (off leaves only health seen to fall)", ref counting))
        {
            _meter.CountKills = counting;
        }

        // The reason the switch above is not merely a preference: a dead monster is dropped
        // from the snapshot rather than lingering at zero, so the last chunk of every kill -
        // and the whole of anything deleted between two reads - is never watched falling.
        if (!counting)
        {
            ImGui.TextColored(
                DimText,
                "    with this off the figure is only what was watched, which under-reports"
                + " by the whole of every killing blow.");
        }
        else
        {
            // GRID UNITS, the same as the rows above and as the entity browser. It was in
            // screens, which read more naturally but could not be compared with anything:
            // the figures it has to be set against - how far the list reaches, where things
            // went missing, GameHelper2's measured bubble near 200 - are all in grid, and a
            // control in its own private unit cannot be set from them.
            ImGui.SetNextItemWidth(220f);
            float limit = _meter.CreditWithin / MapView.WorldToGrid;
            if (ImGui.SliderFloat("only within", ref limit, 0f, 600f,
                    limit <= 0f ? "any distance" : "%.0f grid"))
            {
                _meter.CreditWithin = limit * MapView.WorldToGrid;
            }

            ImGui.SameLine();
            ImGui.TextColored(DimText, "of where it was last seen");
        }

        ImGui.SetNextItemWidth(220f);
        float smoothing = _meter.SmoothingSeconds;
        if (ImGui.SliderFloat("smoothing", ref smoothing, 0.1f, 5f, "%.2f s"))
        {
            _meter.SmoothingSeconds = smoothing;
        }

        ImGui.Separator();
        DrawGraph();

        ImGui.Separator();
        DrawKills();

        ImGui.Separator();
        DrawTargets();
    }

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

        if (!ImGui.CollapsingHeader($"Rares and uniques ({kills.Count})###dmg-kills", ImGuiTreeNodeFlags.DefaultOpen))
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

        if (!ImGui.BeginTable("##dmg-kill-table", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("monster");
            ImGui.TableSetupColumn("took");
            ImGui.TableSetupColumn("damage");
            ImGui.TableSetupColumn("dps");
            ImGui.TableHeadersRow();

            foreach (KillRecord kill in kills)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(RarityText(kill.Rarity), kill.Name.Length > 0 ? kill.Name : "(unnamed)");

                ImGui.TableNextColumn();

                // A one-shot has no fight to time, and printing 0.0s would read as a
                // measurement rather than as the absence of one.
                if (kill.Watched)
                {
                    ImGui.Text($"{kill.Seconds:F1}s");
                }
                else
                {
                    ImGui.TextColored(SoftText, "one-shot");
                }

                ImGui.TableNextColumn();
                ImGui.Text(Number(kill.Damage));

                ImGui.TableNextColumn();
                ImGui.TextColored(kill.Watched ? DimText : SoftText, kill.Watched ? Number(kill.Dps) : "-");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
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

        ImGui.TextColored(
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
            ImGui.TextColored(
                SingleText, $"single target {Number(single.Dps)}");
            ImGui.SameLine();
            ImGui.TextColored(DimText, $"over {single.Seconds:F0}s alone with one   ");
            ImGui.SameLine();
        }

        if (pack.Seconds > 0)
        {
            ImGui.TextColored(PackText, $"in a pack {Number(pack.Dps)}");
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

        ImGui.Checkbox("this map only##dmg", ref _thisMapOnly);
        ImGui.SameLine();
        if (ImGui.Button("start over##dmg"))
        {
            _meter.History.Clear();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        ImGui.SliderFloat("height##dmg", ref _height, 40f, 240f, "%.0f px");

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
        ImGui.TextColored(DpsText, $"{Number(sample.Total)} dps");
        ImGui.TextColored(WatchedBar, $"watched    {Number(sample.Watched)}");
        ImGui.TextColored(CreditedBar, $"credited   {Number(sample.Credited)}");
        ImGui.TextColored(UntouchedBar, $"untouched  {Number(sample.Untouched)}");

        if (sample.Taken > 0f)
        {
            ImGui.TextColored(TakenLine, $"taken      {Number(sample.Taken)}");
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

                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, target.Percent >= 0 ? $"{target.Percent}%%" : "-");

                ImGui.TableNextColumn();
                ImGui.TextColored(DpsText, Number(target.Dps));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>The game's own rarity colours, so a pack leader reads the same as everywhere else.</summary>
    private static Vector4 Tint(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Unique => new Vector4(0.68f, 0.37f, 0.13f, 1f),
        ItemRarity.Rare => new Vector4(1f, 1f, 0.46f, 1f),
        ItemRarity.Magic => new Vector4(0.53f, 0.53f, 1f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };

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
