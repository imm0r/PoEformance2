using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The Expedition tab: the plan's state and the Run button, then the plan and every weight it
/// is made with, each on a page of its own.
/// </summary>
/// <remarks>
/// THE RUN IS A BUTTON, NOT A CONSEQUENCE. Editing a weight marks the plan stale and nothing
/// more; the heavy search runs only on Run, or on the hotkey, so dragging a slider never costs
/// a route computation. The button says so - "Run*" while the plan is stale, "cooking" while
/// one is being made.
///
/// PAGES, NOT A SCROLL. The first cut stacked everything down one column - the state, forty
/// controls, three tables and the trace - and it read as one list with headings dropped into
/// it. What is looked at DURING a dig (the state, the plan) stays at the top; what is set up
/// between digs (the weights, the relics, the props, the map) is a tab each, the shape
/// <see cref="OverlayLayout.Tabs"/> exists for. Every control carries its sentence as a tooltip
/// and every table a line of plain words above it, because the first version explained itself
/// only on hover over its titles, which nobody hovers.
///
/// WHAT IS SHOWN DEPENDS ON THE EXPEDITION. A normal map's flags are valued by pole height and
/// a Grand one's by reward type, and the Sentinel and the marker-cluster gate belong to one
/// kind each - so the controls for the other kind are folded away once the map is known, and
/// all are reachable out of a map, which is where settings get made.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ExpeditionWindow
{
    private const int MostTraceLines = 80;

    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 MoneyText = OverlayInk.Money;

    /// <summary>What the reward icons mean, for the weight table. Anything else shows its icon name.</summary>
    private static readonly (string Icon, string Label)[] RewardKinds =
    [
        ("RewardChestCurrency", "Currency"),
        ("RewardChestCurrencyRare", "Currency (rare)"),
        ("RewardChestGeneric", "Generic"),
        ("RewardChestUnique", "Uniques"),
        ("RewardChestGems", "Gems"),
        ("RewardChestMaps", "Maps / Waystones"),
        ("RewardChestTrinkets", "Trinkets"),
        ("RewardChestArmour", "Armour"),
        ("RewardChestWeapons", "Weapons"),
        ("RewardChestRunes", "Runes"),
        ("RewardChestBreach", "Breach"),
        ("RewardChestRitual", "Ritual"),
        ("RewardChestExpedition", "Expedition"),
    ];

    private static readonly string[] KeyNames = Array.ConvertAll(RunKeys.Choices, c => c.Name);

    private readonly ExpeditionWatch _watch;
    private readonly Action<ExpeditionSettings> _saved;

    private bool _unsaved;
    private string _newProp = string.Empty;

    public ExpeditionWindow(ExpeditionWatch watch, Action<ExpeditionSettings> saved)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(saved);
        _watch = watch;
        _saved = saved;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        ExpeditionSettings settings = _watch.Settings;
        ExpeditionView view = _watch.View;

        bool enabled = settings.Enabled;
        if (OverlayLayout.Master("Expedition planner", ref enabled))
        {
            Apply(settings with { Enabled = enabled });
            settings = _watch.Settings;
        }

        OverlayLayout.Note(
            "Lays the explosive chain for you: from the detonator through every monolith and relic"
            + " worth the walk, bridging the gaps, taking reward flags on the way, spending the spare"
            + " charges where they pay. The chain is drawn on the map, numbered; the next charge is"
            + " ringed in the world. Press Run once the detonator is on the map, and again after"
            + " anything changes - the button says when.");

        if (!settings.Enabled)
        {
            Flush();
            return;
        }

        State(view, settings);
        OverlayLayout.Gap();

        OverlayLayout.Tabs(
            "expedition-pages",
            ("Plan", () => PlanPage(view)),
            ("Weights", () => WeightsPage(view, settings)),
            ("Relics", () => RelicsPage(settings)),
            ("Props", () => PropsPage(settings, view)),
            ("Map & Run", () => DrawingPage(settings)));

        Flush();
    }

    // ── The state and the Run button ──────────────────────────────────────────

    /// <summary>Where the expedition stands, and the Run button.</summary>
    private void State(ExpeditionView view, ExpeditionSettings settings)
    {
        OverlayLayout.Group("This Expedition");
        ImGuiText.Colored(view.HasDetonator ? GoodText : DimText, view.Status);

        if (!view.HasDetonator)
        {
            return;
        }

        string physics = view.IsGrand ? "Grand" : "normal";
        ImGuiText.Colored(
            view.CountsKnown ? DimText : WarnText,
            $"charges {view.Remaining} left / {view.Total} total ({view.CountsSource}) · {physics} base"
            + (view.IsLogbook ? ", logbook" : string.Empty)
            + $" · reach {view.EffDist:0} · radius {view.EffRadius:0} cells"
            + $" · map mods +{view.PlacementPct}% reach, +{view.RadiusPct}% radius");
        OverlayLayout.Hint(
            "Reach is how far the next charge may be from the last; radius is what one blast covers."
            + " A Grand expedition - a logbook, or ten charges and more - gets the longer figures, and"
            + " the map's own modifiers stretch both. The source in brackets says where the counts came"
            + " from: the game's controller, its counter widget, or the manual total below.");

        if (!view.CountsKnown)
        {
            int manual = settings.ManualTotal;
            if (OverlayLayout.Number("Total charges (manual)", ref manual))
            {
                Apply(settings with { ManualTotal = manual });
            }

            OverlayLayout.Hint("The charge total the plan is made with while the game's own count cannot be read.");
            OverlayLayout.Warning(
                "Neither the controller nor the counter widget could be read: set the total to match"
                + " the in-game counter. Progress counts the charges seen as entities.");
        }

        if (view.Activated)
        {
            ImGuiText.Colored(DimText, "The detonator has been pressed; the plan is frozen for this dig.");
            return;
        }

        if (view.Computing)
        {
            ImGui.BeginDisabled();
            OverlayLayout.Actions("cooking...");
            ImGui.EndDisabled();
        }
        else
        {
            if (view.Stale)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.55f, 0.20f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.70f, 0.26f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.16f, 0.45f, 0.16f, 1f));
            }

            int pressed = OverlayLayout.Actions(view.Stale ? "Run*" : "Run");
            if (view.Stale)
            {
                ImGui.PopStyleColor(3);
            }

            OverlayLayout.Hint(
                (view.Stale ? "Something changed since the last plan - click to build the route." : "The plan is up to date.")
                + (settings.RunKey > 0 ? $"\nHotkey: {RunKeys.Name(settings.RunKey)}" : string.Empty));

            if (pressed == 0)
            {
                _watch.RequestRun();
            }
        }

        PlanResult plan = view.Route;
        if (plan.ComputeMs > 0)
        {
            ImGuiText.Colored(
                DimText,
                $"last plan {plan.ComputeMs:0} ms · {plan.Searches} searches, {plan.Hits} from the memo · {plan.Phase}");
        }
    }

    // ── Plan ──────────────────────────────────────────────────────────────────

    private static void PlanPage(ExpeditionView view)
    {
        Plan(view);
        OverlayLayout.Gap();
        Targets(view);
        OverlayLayout.Gap();
        Trace(view);
    }

    /// <summary>The plan, charge by charge.</summary>
    private static void Plan(ExpeditionView view)
    {
        PlanResult plan = view.Route;
        OverlayLayout.Group(
            $"The Plan ({plan.Route.Count} charges)",
            "Each charge in the order to lay it, where it goes in grid cells, and why it landed there."
            + " Grey is already placed, green is next, gold still to come - the same colours as on the map.");

        if (plan.Route.Count == 0)
        {
            ImGuiText.Colored(DimText, view.HasDetonator ? "no plan yet - press Run" : "no detonator in this area");
            return;
        }

        ImGuiText.Colored(
            DimText,
            $"anchors {plan.AnchorsCovered}/{plan.Anchors} · covered {plan.Covered}/{plan.Targets} targets · {plan.Weight:0} ex"
            + $" · next charge #{view.NextIndex + 1}");
        if (plan.ShunnedHit > 0)
        {
            OverlayLayout.Warning($"{plan.ShunnedHit} shunned relic(s) would still be set off - a fault in the planner, please report the trace.");
        }

        if (!ImGui.BeginTable("##expplan", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("#");
            ImGui.TableSetupColumn("at");
            ImGui.TableSetupColumn("why");
            ImGui.TableHeadersRow();
            for (int i = 0; i < plan.Route.Count; i++)
            {
                RoutePoint point = plan.Route[i];
                Vector4 ink = i < view.NextIndex ? DimText : i == view.NextIndex ? GoodText : MoneyText;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGuiText.Colored(ink, (i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableSetColumnIndex(1);
                ImGuiText.Colored(ink, $"{point.Grid.X:0},{point.Grid.Y:0}");
                ImGui.TableSetColumnIndex(2);
                ImGuiText.Colored(point.Bridge ? DimText : ink, point.Note);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>What the scan found, by value.</summary>
    private static void Targets(ExpeditionView view)
    {
        if (!OverlayLayout.Subsection($"Targets ({view.Targets.Count})"))
        {
            return;
        }

        OverlayLayout.Note(
            "Everything the planner knows about on this map, most valuable first. A star marks an"
            + " anchor the route is built to reach; the rest are taken when a blast covers them.");

        if (view.Targets.Count == 0)
        {
            ImGuiText.Colored(DimText, "nothing found yet");
            return;
        }

        if (!ImGui.BeginTable("##exptargets", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("kind");
            ImGui.TableSetupColumn("at");
            ImGui.TableSetupColumn("worth");
            ImGui.TableSetupColumn("what");
            ImGui.TableHeadersRow();
            foreach (ExpeditionTargetView target in view.Targets)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGuiText.Colored(
                    target.Shunned ? WarnText : target.Primary ? GoodText : DimText,
                    target.Kind.ToString().ToLowerInvariant() + (target.Primary ? " *" : string.Empty));
                ImGui.TableSetColumnIndex(1);
                ImGuiText.Colored(DimText, $"{target.Grid.X:0},{target.Grid.Y:0}");
                ImGui.TableSetColumnIndex(2);
                ImGuiText.Colored(target.Value > 0 ? MoneyText : DimText, target.Value > 0 ? $"{target.Value:0} ex" : "-");
                ImGui.TableSetColumnIndex(3);
                string what = target.Kind switch
                {
                    ExpeditionKind.Marker => RewardLabel(target.Info) + (target.Tier.Length > 0 ? $" ({target.Tier})" : string.Empty),
                    ExpeditionKind.Remnant => (target.Shunned ? "AVOID  " : string.Empty)
                                              + (target.Info.Length > 0 ? Short(target.Info) : "(mods unread)"),
                    ExpeditionKind.Monolith => target.Info.Length > 0 ? $"anchor {target.Info}" : "(unpriced)",
                    _ => string.Empty,
                };
                ImGuiText.Colored(target.Shunned ? WarnText : DimText, what);
                if (target.Kind == ExpeditionKind.Remnant && target.Info.Length > 0)
                {
                    OverlayLayout.Hint(target.Info.Replace(';', '\n'));
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static string Short(string mods)
    {
        string[] parts = mods.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = (ExpeditionRelics.IsUpside(parts[i]) ? "+" : ExpeditionRelics.IsDownside(parts[i]) ? "-" : "?") + ExpeditionRelics.ShortName(parts[i]);
        }

        return string.Join("  ", parts);
    }

    /// <summary>The tail of the planner's decision trace.</summary>
    private static void Trace(ExpeditionView view)
    {
        IReadOnlyList<string> lines = view.Route.Trace;
        if (!OverlayLayout.Subsection($"Why ({lines.Count} lines)"))
        {
            return;
        }

        OverlayLayout.Note("Every candidate the planner weighed and why it picked what it did - the last lines of the trace.");

        if (lines.Count == 0)
        {
            ImGuiText.Colored(DimText, "no plan yet");
            return;
        }

        int from = Math.Max(0, lines.Count - MostTraceLines);
        if (from > 0)
        {
            ImGuiText.Colored(DimText, $"...{from} earlier lines in the file, when the trace is written");
        }

        for (int i = from; i < lines.Count; i++)
        {
            ImGuiText.Colored(DimText, lines[i]);
        }
    }

    // ── Weights ───────────────────────────────────────────────────────────────

    /// <summary>What is worth the walk: the monolith bar, then the flags by kind of expedition.</summary>
    private void WeightsPage(ExpeditionView view, ExpeditionSettings settings)
    {
        OverlayLayout.Group(
            "What Is Worth the Walk",
            "The anchors: the places the route is built to reach, in order. Everything else is a"
            + " pickup, taken only when a blast happens to cover it or a spare charge pays for it.");
        OverlayLayout.Note(
            "A monolith is an anchor when its recipe plus the rune it would propagate clears this bar,"
            + " or when it has enough holes to be worth the chain whatever it pays.");

        float minEx = settings.MonolithMinEx;
        if (OverlayLayout.Drag("Monolith worth a detour (ex)", ref minEx, 1f, 0f, 100_000f, "%.0f"))
        {
            Apply(settings with { MonolithMinEx = minEx });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Its recipe PLUS the rune it would propagate, so a cheap monolith holding Opulent still"
            + " qualifies. Under the bar it is still planned for - picked up when a blast covers it -"
            + " never struck off. If nothing clears the bar, the rune carriers are routed so the run"
            + " still collects runes. 0 routes every priced monolith.");

        int sockets = settings.MonolithMinSockets;
        if (OverlayLayout.Number("...or holes at least", ref sockets))
        {
            Apply(settings with { MonolithMinSockets = sockets });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "A monolith this big is a detour whatever its recipe pays: one wave per socket, each"
            + " carrying every rune propagated before it, so the chain ends on it. 0 switches this off.");

        // The Grand controls, and the normal ones, each shown unless the map is known to be the other.
        bool knownNormal = view.HasDetonator && view.CountsKnown && !view.IsGrand;
        bool knownGrand = view.HasDetonator && view.IsGrand;

        if (!knownGrand)
        {
            OverlayLayout.Group(
                "Reward Flags by Height (normal)",
                "On a normal expedition each flag's pole stands at a fixed height per reward tier, so"
                + " the tall ones are the good ones. The tiny throwaway flags weigh nothing.");
            OverlayLayout.Note("What a flag of each tier is worth to the route, in Exalted - white, magic, gold, and the two-triangle logbook flag.");

            int white = settings.MarkerWhite;
            if (OverlayLayout.Slider("White flag", ref white, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerWhite = white });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint("The ordinary flag: a normal-rarity reward.");

            int magic = settings.MarkerMagic;
            if (OverlayLayout.Slider("Magic flag", ref magic, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerMagic = magic });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint("The blue flag: a magic reward.");

            int gold = settings.MarkerGold;
            if (OverlayLayout.Slider("Gold flag", ref gold, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerGold = gold });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint("The gold flag: a rare reward.");

            int logbook = settings.MarkerLogbook;
            if (OverlayLayout.Slider("Logbook flag", ref logbook, 0, 1000, "%d ex"))
            {
                Apply(settings with { MarkerLogbook = logbook });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint("The tall two-triangle flag: a logbook. Also what makes the Kalguur Sentinel worth routing to first.");
        }

        if (!knownNormal)
        {
            OverlayLayout.Group(
                "Reward Types (Grand)",
                "On a Grand expedition - a logbook, or ten charges and more - the flags carry a reward"
                + " type instead of a height, so each type is weighed by name.");
            OverlayLayout.Note("What a flag of each reward type is worth to the route, in Exalted. A type not listed weighs 1; 0 ignores it.");

            int markers = settings.MinMarkersPerSpare;
            if (OverlayLayout.Slider("Flags per spare charge", ref markers, 1, 3, "%d"))
            {
                Apply(settings with { MinMarkersPerSpare = markers });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint(
                "A spare charge is spent only where one blast covers this many flags at once - a good"
                + " flag cannot be told from a trash one in memory, so density is the only honest signal.");

            RewardTable(settings, view);
        }
    }

    /// <summary>The weight per reward icon, as a table with an add box and a reset.</summary>
    private void RewardTable(ExpeditionSettings settings, ExpeditionView view)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ExpeditionTargetView target in view.Targets)
        {
            if (target.Kind == ExpeditionKind.Marker)
            {
                seen.TryGetValue(target.Info, out int n);
                seen[target.Info] = n + 1;
            }
        }

        IReadOnlyList<RewardWeight> rows = settings.RewardWeights;
        RewardWeight? changed = null;
        int changedAt = -1;
        int remove = -1;
        if (ImGui.BeginTable("##exprewards", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("reward");
                ImGui.TableSetupColumn("weight");
                ImGui.TableSetupColumn("##rm");
                ImGui.TableHeadersRow();
                float field = ImGui.GetFontSize() * 4f;
                for (int i = 0; i < rows.Count; i++)
                {
                    RewardWeight row = rows[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    try
                    {
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(RewardLabel(row.Icon));
                        if (seen.TryGetValue(row.Icon, out int n) && n > 0)
                        {
                            ImGui.SameLine();
                            ImGuiText.Colored(GoodText, $"x{n}");
                            OverlayLayout.Hint("How many flags of this type are on the map now.");
                        }

                        ImGui.TableSetColumnIndex(1);
                        ImGui.SetNextItemWidth(field);
                        float weight = row.Weight;
                        if (ImGui.DragFloat("##w", ref weight, 0.5f, 0f, 10_000f, "%.0f"))
                        {
                            changed = row with { Weight = weight };
                            changedAt = i;
                        }

                        OverlayLayout.Hint("Worth to the route, in Exalted. 0 ignores the type.");

                        ImGui.TableSetColumnIndex(2);
                        if (ImGui.SmallButton("x"))
                        {
                            remove = i;
                        }

                        OverlayLayout.Hint("Drop the row; the type then weighs 1 like any unlisted one.");
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        if (changed is not null)
        {
            var edited = new RewardWeight[rows.Count];
            for (int i = 0; i < edited.Length; i++)
            {
                edited[i] = i == changedAt ? changed : rows[i];
            }

            Apply(settings with { RewardWeights = edited });
            settings = _watch.Settings;
        }
        else if (remove >= 0)
        {
            var kept = new List<RewardWeight>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (i != remove)
                {
                    kept.Add(rows[i]);
                }
            }

            Apply(settings with { RewardWeights = kept });
            settings = _watch.Settings;
        }

        ImGui.SetNextItemWidth(OverlayLayout.FieldWidth());
        if (ImGui.BeginCombo("Add a reward type", "choose..."))
        {
            try
            {
                var offered = new List<string>();
                foreach ((string icon, _) in RewardKinds)
                {
                    offered.Add(icon);
                }

                foreach (string icon in seen.Keys)
                {
                    if (!offered.Contains(icon))
                    {
                        offered.Add(icon);
                    }
                }

                foreach (string icon in offered)
                {
                    if (Listed(rows, icon))
                    {
                        continue;
                    }

                    if (ImGui.Selectable(RewardLabel(icon)))
                    {
                        var added = new List<RewardWeight>(rows) { new(icon, ExpeditionSettings.DefaultRewardWeight) };
                        Apply(settings with { RewardWeights = added });
                        break;
                    }
                }
            }
            finally
            {
                ImGui.EndCombo();
            }
        }

        OverlayLayout.Hint("The known reward types, and any icon seen on this map that is not among them.");

        if (OverlayLayout.Actions("Reset the reward weights") == 0)
        {
            Apply(settings with { RewardWeights = ExpeditionSettings.DefaultRewardWeights });
        }

        OverlayLayout.Hint("Back to the four types the reference found worth more than one Exalted.");
    }

    private static bool Listed(IReadOnlyList<RewardWeight> rows, string icon)
    {
        foreach (RewardWeight row in rows)
        {
            if (string.Equals(row.Icon, icon, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RewardLabel(string icon)
    {
        foreach ((string known, string label) in RewardKinds)
        {
            if (string.Equals(known, icon, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return icon.StartsWith("RewardChest", StringComparison.Ordinal) ? icon["RewardChest".Length..] : icon;
    }

    // ── Relics ────────────────────────────────────────────────────────────────

    /// <summary>The relic mods: what to seek, weighed, and what to shun, ticked.</summary>
    private void RelicsPage(ExpeditionSettings settings)
    {
        OverlayLayout.Group(
            "Relics: seek (+) and shun (-)",
            "A relic (\"remnant\") is a field device whose mods apply to the whole dig once a blast"
            + " reaches it: more quantity, more packs - or monsters immune to your damage. The route"
            + " goes to the relics worth it and keeps every blast clear of the ones you tick avoid.");
        OverlayLayout.Note(
            "Left, the good mods: give each a weight in Exalted, and a relic is routed to when its"
            + " upsides outweigh its downsides. Right, the bad ones: a weight counts against a relic;"
            + " avoid means no blast may ever reach a relic carrying that mod. Nothing weighted means"
            + " no relic is sought. Stored by the game's own mod id, so it works in any client language.");

        RelicTable(settings);
    }

    /// <summary>Upsides and downsides side by side: a weight each, and an avoid tick on the downsides.</summary>
    private void RelicTable(ExpeditionSettings settings)
    {
        RelicModNames names = _watch.ModNames;
        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (RelicWeight weight in settings.RelicWeights)
        {
            map[weight.Mod] = weight.Weight;
        }

        string? changedMod = null;
        float changedWeight = 0f;
        string? toggledMod = null;
        var toggledOn = false;
        int rows = Math.Max(ExpeditionRelics.Upsides.Length, ExpeditionRelics.Downsides.Length);
        if (ImGui.BeginTable("##exprelics", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                ImGui.TableSetupColumn("seek (+)  ·  weight in ex");
                ImGui.TableSetupColumn("shun (-)  ·  avoid, or weight in ex");
                ImGui.TableHeadersRow();
                float field = ImGui.GetFontSize() * 3f;
                float gap = ImGui.GetStyle().ItemSpacing.X;
                for (int i = 0; i < rows; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (i < ExpeditionRelics.Upsides.Length)
                    {
                        Cell(ExpeditionRelics.Upsides[i], shunnable: false);
                    }

                    ImGui.TableSetColumnIndex(1);
                    if (i < ExpeditionRelics.Downsides.Length)
                    {
                        Cell(ExpeditionRelics.Downsides[i], shunnable: true);
                    }
                }

                void Cell(string mod, bool shunnable)
                {
                    ImGui.PushID(mod);
                    try
                    {
                        bool avoided = shunnable && settings.Avoids(mod);
                        if (shunnable)
                        {
                            bool tick = avoided;
                            if (ImGui.Checkbox("##avoid", ref tick))
                            {
                                toggledMod = mod;
                                toggledOn = tick;
                            }

                            OverlayLayout.Hint(
                                "Avoid: no blast may reach a relic carrying this mod. The route goes round it,"
                                + " a spare charge is never spent beside it, and an anchor only takeable by"
                                + " setting it off is skipped.");
                            ImGui.SameLine();
                        }

                        if (avoided)
                        {
                            // The field's place is kept, so the names stay in one column either way.
                            float start = ImGui.GetCursorPosX();
                            ImGui.AlignTextToFramePadding();
                            ImGuiText.Colored(WarnText, "avoided");
                            ImGui.SameLine();
                            ImGui.SetCursorPosX(start + field + gap);
                        }
                        else
                        {
                            map.TryGetValue(mod, out float value);
                            ImGui.SetNextItemWidth(field);
                            if (ImGui.DragFloat("##w", ref value, 0.5f, 0f, 10_000f, "%.0f"))
                            {
                                changedMod = mod;
                                changedWeight = value;
                            }

                            OverlayLayout.Hint(shunnable
                                ? "How much this mod counts AGAINST a relic, in Exalted. 0 ignores it. Drag, or Ctrl-click to type."
                                : "How much this mod is worth on a relic, in Exalted. 0 ignores it. Drag, or Ctrl-click to type.");
                            ImGui.SameLine();
                        }

                        ImGui.AlignTextToFramePadding();
                        ImGuiText.Colored(avoided ? WarnText : map.ContainsKey(mod) ? MoneyText : DimText, names.Label(mod));
                        OverlayLayout.Hint(mod);
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        if (toggledMod is not null)
        {
            var avoid = new List<string>(settings.AvoidMods);
            if (toggledOn)
            {
                if (!avoid.Contains(toggledMod, StringComparer.Ordinal))
                {
                    avoid.Add(toggledMod);
                }
            }
            else
            {
                avoid.Remove(toggledMod);
            }

            Apply(settings with { AvoidMods = avoid });
            settings = _watch.Settings;
        }

        if (changedMod is not null)
        {
            var kept = new List<RelicWeight>(settings.RelicWeights.Count + 1);
            var placed = false;
            foreach (RelicWeight weight in settings.RelicWeights)
            {
                if (string.Equals(weight.Mod, changedMod, StringComparison.Ordinal))
                {
                    if (changedWeight > 0f)
                    {
                        kept.Add(weight with { Weight = changedWeight });
                    }

                    placed = true;
                }
                else
                {
                    kept.Add(weight);
                }
            }

            if (!placed && changedWeight > 0f)
            {
                kept.Add(new RelicWeight(changedMod, changedWeight));
            }

            Apply(settings with { RelicWeights = kept });
            settings = _watch.Settings;
        }

        int pressed = OverlayLayout.Actions("Clear the weights", "Clear the avoid marks");
        if (pressed == 0)
        {
            Apply(settings with { RelicWeights = [] });
        }
        else if (pressed == 1)
        {
            Apply(settings with { AvoidMods = [] });
        }
    }

    // ── Props ─────────────────────────────────────────────────────────────────

    /// <summary>The exploding props: a rule per path fragment and radius.</summary>
    private void PropsPage(ExpeditionSettings settings, ExpeditionView view)
    {
        OverlayLayout.Group(
            "Exploding Props",
            "Barrels, oil derricks, Faridun explosives: scenery that explodes when a blast reaches it,"
            + " clearing its own radius for free - so one charge beside a derrick covers far more than its"
            + " own blast. The chain does not continue from the prop, and one prop never sets off another.");
        OverlayLayout.Note(
            "One rule per kind of prop: a piece of its path, and the radius its own explosion clears, in"
            + " grid cells. The three shipped ones are measured; names and radii differ per tileset.");

        IReadOnlyList<PropRule> rules = settings.PropRules;
        PropRule? changed = null;
        int changedAt = -1;
        int remove = -1;
        float field = ImGui.GetFontSize() * 4f;
        for (int i = 0; i < rules.Count; i++)
        {
            PropRule rule = rules[i];
            ImGui.PushID(i);
            try
            {
                bool on = rule.Enabled;
                if (ImGui.Checkbox("##on", ref on))
                {
                    changed = rule with { Enabled = on };
                    changedAt = i;
                }

                OverlayLayout.Hint("Whether the rule is in force.");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
                string path = rule.PathContains;
                if (ImGui.InputText("##path", ref path, 128) && !string.Equals(path, rule.PathContains, StringComparison.Ordinal))
                {
                    changed = rule with { PathContains = path };
                    changedAt = i;
                }

                OverlayLayout.Hint("A piece of the entity path, matched without regard to case - the leaf, since the tileset prefix differs per map.");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(field);
                float radius = rule.Radius;
                if (ImGui.DragFloat("##r", ref radius, 1f, 0f, 500f, "%.0f cells"))
                {
                    changed = rule with { Radius = radius };
                    changedAt = i;
                }

                OverlayLayout.Hint("What its own blast clears, in grid cells around the object.");

                ImGui.SameLine();
                if (ImGui.SmallButton("x"))
                {
                    remove = i;
                }

                OverlayLayout.Hint("Drop the rule.");
            }
            finally
            {
                ImGui.PopID();
            }
        }

        if (changed is not null)
        {
            var edited = new PropRule[rules.Count];
            for (int i = 0; i < edited.Length; i++)
            {
                edited[i] = i == changedAt ? changed : rules[i];
            }

            Apply(settings with { PropRules = edited });
            settings = _watch.Settings;
        }
        else if (remove >= 0)
        {
            var kept = new List<PropRule>(rules.Count);
            for (int i = 0; i < rules.Count; i++)
            {
                if (i != remove)
                {
                    kept.Add(rules[i]);
                }
            }

            Apply(settings with { PropRules = kept });
            settings = _watch.Settings;
        }

        OverlayLayout.Input("New rule's path fragment", ref _newProp, 128);
        OverlayLayout.Hint("A piece of the object's path - copy one from the list below. The radius starts at 55 cells, a barrel's.");
        if (OverlayLayout.Actions("Add rule") == 0 && _newProp.Trim().Length > 0)
        {
            var added = new List<PropRule>(rules) { new(_newProp.Trim(), 55f) };
            Apply(settings with { PropRules = added });
            _newProp = string.Empty;
        }

        OverlayLayout.Group(
            "Objects Matching No Rule",
            "The expedition objects on this map that no rule names. Most are scenery; add a rule only"
            + " for one that really explodes, and click a name to copy its path.");
        if (view.UnmatchedProps.Count == 0)
        {
            ImGuiText.Colored(DimText, view.HasDetonator ? "every expedition object here matches a rule" : "no expedition on this map");
            return;
        }

        foreach (string path in view.UnmatchedProps)
        {
            int cut = path.LastIndexOf('/');
            string leaf = cut >= 0 && cut + 1 < path.Length ? path[(cut + 1)..] : path;
            ImGui.Bullet();
            ImGuiText.Colored(DimText, leaf);
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(path);
            }

            OverlayLayout.Hint(path + "\n(click to copy)");
        }
    }

    // ── Map & Run ─────────────────────────────────────────────────────────────

    /// <summary>The map toggles, the hotkey, the trace file.</summary>
    private void DrawingPage(ExpeditionSettings settings)
    {
        OverlayLayout.Group(
            "On the Map",
            "Drawn on whichever map is open: the large map when it is up, else the minimap. The"
            + " numbered charges and the avoid marks are always drawn; these are the extras.");

        bool rings = settings.ShowRings;
        if (OverlayLayout.Toggle("Blast ring on each planned charge", ref rings))
        {
            Apply(settings with { ShowRings = rings });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("A ring the size of one blast round every planned charge, so overlapping coverage is visible.");

        bool spine = settings.ShowSpine;
        if (OverlayLayout.Toggle("The line the route walks", ref spine))
        {
            Apply(settings with { ShowSpine = spine });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("The walkable line from the detonator through the anchors that the charges are laid along.");

        bool props = settings.ShowProps;
        if (OverlayLayout.Toggle("Exploding props and their radius", ref props))
        {
            Apply(settings with { ShowProps = props });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("Each prop with a rule, and the area its own explosion clears.");

        bool gates = settings.ShowGates;
        if (OverlayLayout.Toggle("Path-blockers and the holes they punch", ref gates))
        {
            Apply(settings with { ShowGates = gates });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("The shut blockers (red) with the cells they make solid, and the opened ones (green) the chain may pass.");

        OverlayLayout.Group("Run", "Re-plan without a trip to this window, and keep the planner's reasoning on disk.");
        int key = RunKeys.IndexOf(settings.RunKey);
        if (OverlayLayout.Combo("Run hotkey", ref key, KeyNames))
        {
            Apply(settings with { RunKey = RunKeys.Choices[key].Code });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Listens only while a detonator is on the map and unpressed, and only while the game or"
            + " this tool is in front. Off by default: no key is safe on every keyboard.");

        bool trace = settings.TraceFile;
        if (OverlayLayout.Toggle("Write the decision trace to config/expedition-plan.txt", ref trace))
        {
            Apply(settings with { TraceFile = trace });
        }

        OverlayLayout.Hint("The whole trace of every run, for a plan that looks wrong. The last lines are on the Plan page too.");
    }

    /// <summary>Takes a changed setting into use at once; the write waits for the typing to stop.</summary>
    private void Apply(ExpeditionSettings changed)
    {
        _watch.Settings = changed.Normalised();
        _unsaved = true;
    }

    private void Flush()
    {
        if (_unsaved && !ImGui.IsAnyItemActive())
        {
            _unsaved = false;
            _saved(_watch.Settings);
        }
    }
}
