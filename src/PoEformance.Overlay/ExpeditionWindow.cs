using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The Expedition tab: the plan's state, the Run button, and every weight the plan is made with.
/// </summary>
/// <remarks>
/// THE RUN IS A BUTTON, NOT A CONSEQUENCE. Editing a weight marks the plan stale and nothing
/// more; the heavy search runs only on Run, or on the hotkey, so dragging a slider never costs
/// a route computation. The button says so - "Run*" while the plan is stale, "cooking" while
/// one is being made.
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
            + " worth the walk, bridging the gaps, taking reward markers on the way, spending the"
            + " spare charges where they pay. The chain is drawn on the map, numbered; the next"
            + " charge is ringed in the world. Press Run once the detonator is on the map, and"
            + " again after anything changes - the button says when.");

        if (settings.Enabled)
        {
            State(view, settings);
            ImGui.Separator();
            Weighing(view, settings);
            ImGui.Separator();
            Drawing(settings);
        }

        Flush();

        if (settings.Enabled)
        {
            ImGui.Separator();
            Plan(view);
            Targets(view);
            Trace(view);
        }
    }

    /// <summary>Where the expedition stands, and the Run button.</summary>
    private void State(ExpeditionView view, ExpeditionSettings settings)
    {
        OverlayLayout.Group("This Expedition");
        ImGui.TextColored(view.HasDetonator ? GoodText : DimText, view.Status);

        if (!view.HasDetonator)
        {
            return;
        }

        string physics = view.IsGrand ? "Grand" : "normal";
        ImGui.TextColored(
            view.CountsKnown ? DimText : WarnText,
            $"charges {view.Remaining} left / {view.Total} total ({view.CountsSource}) · {physics} base"
            + (view.IsLogbook ? ", logbook" : string.Empty)
            + $" · reach {view.EffDist:0} · radius {view.EffRadius:0} cells"
            + $" · map mods +{view.PlacementPct}% reach, +{view.RadiusPct}% radius");

        if (!view.CountsKnown)
        {
            int manual = settings.ManualTotal;
            if (OverlayLayout.Number("Total charges (manual)", ref manual))
            {
                Apply(settings with { ManualTotal = manual });
            }

            OverlayLayout.Hint(
                "Neither the controller nor the counter widget could be read: set the total to"
                + " match the in-game counter. Progress counts the charges seen as entities.");
        }

        if (view.Activated)
        {
            ImGui.TextColored(DimText, "The detonator has been pressed; the plan is frozen for this dig.");
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

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    (view.Stale ? "Something changed since the last plan - click to build the route." : "The plan is up to date.")
                    + (settings.RunKey > 0 ? $"\nHotkey: {RunKeys.Name(settings.RunKey)}" : string.Empty));
            }

            if (pressed == 0)
            {
                _watch.RequestRun();
            }
        }

        PlanResult plan = view.Route;
        if (plan.ComputeMs > 0)
        {
            ImGui.TextColored(
                DimText,
                $"last plan {plan.ComputeMs:0} ms · {plan.Searches} searches, {plan.Hits} from the memo · {plan.Phase}");
        }
    }

    /// <summary>Every weight the plan is made with.</summary>
    private void Weighing(ExpeditionView view, ExpeditionSettings settings)
    {
        OverlayLayout.Group("What Is Worth the Walk");

        float minEx = settings.MonolithMinEx;
        if (OverlayLayout.Drag("Monolith worth a detour (ex)", ref minEx, 1f, 0f, 100_000f, "%.0f"))
        {
            Apply(settings with { MonolithMinEx = minEx });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Its recipe PLUS the rune it would propagate, so a cheap monolith holding Opulent"
            + " still qualifies. Under the bar it is still planned for - picked up when a blast"
            + " covers it - never struck off. If nothing clears the bar, the rune carriers are"
            + " routed so the run still collects runes. 0 routes every priced monolith.");

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

        if (!knownNormal)
        {
            int markers = settings.MinMarkersPerSpare;
            if (OverlayLayout.Slider("Markers per spare charge (Grand)", ref markers, 1, 3, "%d"))
            {
                Apply(settings with { MinMarkersPerSpare = markers });
                settings = _watch.Settings;
            }

            OverlayLayout.Hint(
                "On a Grand expedition a spare charge is spent only where one blast covers this many"
                + " markers at once - a good marker cannot be told from a trash one in memory, so"
                + " density is the only honest signal.");

            RewardTable(settings, view);
        }

        if (!knownGrand)
        {
            OverlayLayout.Group("Reward Flags by Height (normal)");
            OverlayLayout.Hint(
                "On a normal expedition each flag's pole stands at a fixed height per reward tier,"
                + " so the tall ones are the good ones: white, magic, gold, and the two-triangle"
                + " logbook flag. The tiny throwaway flags weigh nothing.");

            int white = settings.MarkerWhite;
            if (OverlayLayout.Slider("White flag", ref white, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerWhite = white });
                settings = _watch.Settings;
            }

            int magic = settings.MarkerMagic;
            if (OverlayLayout.Slider("Magic flag", ref magic, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerMagic = magic });
                settings = _watch.Settings;
            }

            int gold = settings.MarkerGold;
            if (OverlayLayout.Slider("Gold flag", ref gold, 0, 500, "%d ex"))
            {
                Apply(settings with { MarkerGold = gold });
                settings = _watch.Settings;
            }

            int logbook = settings.MarkerLogbook;
            if (OverlayLayout.Slider("Logbook flag", ref logbook, 0, 1000, "%d ex"))
            {
                Apply(settings with { MarkerLogbook = logbook });
                settings = _watch.Settings;
            }
        }

        RelicTable(settings);
        PropTable(settings, view);
    }

    /// <summary>The weight per reward type, on a Grand expedition.</summary>
    private void RewardTable(ExpeditionSettings settings, ExpeditionView view)
    {
        OverlayLayout.Group("Reward Types (Grand)");
        OverlayLayout.Hint("What a marker of each type is worth to the route, in Exalted. A type not listed weighs 1; 0 ignores it.");

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
                            ImGui.TextColored(GoodText, $"x{n}");
                        }

                        ImGui.TableSetColumnIndex(1);
                        ImGui.SetNextItemWidth(field);
                        float weight = row.Weight;
                        if (ImGui.DragFloat("##w", ref weight, 0.5f, 0f, 10_000f, "%.0f"))
                        {
                            changed = row with { Weight = weight };
                            changedAt = i;
                        }

                        ImGui.TableSetColumnIndex(2);
                        if (ImGui.SmallButton("x"))
                        {
                            remove = i;
                        }
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

        if (ImGui.SmallButton("Reset the reward weights"))
        {
            Apply(settings with { RewardWeights = ExpeditionSettings.DefaultRewardWeights });
        }
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

    /// <summary>The weight per relic mod: upsides and downsides side by side, both positive.</summary>
    private void RelicTable(ExpeditionSettings settings)
    {
        if (!OverlayLayout.Subsection("Relic Mods"))
        {
            return;
        }

        OverlayLayout.Hint(
            "A relic (remnant) is routed through when its upsides outweigh its downsides by these"
            + " weights - plain magnitudes, the sign comes from the column. Nothing weighted means"
            + " no relic is ever sought. Stored by the game's own mod id, in any client language.");

        RelicModNames names = _watch.ModNames;
        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (RelicWeight weight in settings.RelicWeights)
        {
            map[weight.Mod] = weight.Weight;
        }

        string? changedMod = null;
        float changedWeight = 0f;
        int rows = Math.Max(ExpeditionRelics.Upsides.Length, ExpeditionRelics.Downsides.Length);
        if (ImGui.BeginTable("##exprelics", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                ImGui.TableSetupColumn("upside (+)");
                ImGui.TableSetupColumn("downside (-)");
                ImGui.TableHeadersRow();
                float field = ImGui.GetFontSize() * 3f;
                for (int i = 0; i < rows; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (i < ExpeditionRelics.Upsides.Length)
                    {
                        Cell(ExpeditionRelics.Upsides[i]);
                    }

                    ImGui.TableSetColumnIndex(1);
                    if (i < ExpeditionRelics.Downsides.Length)
                    {
                        Cell(ExpeditionRelics.Downsides[i]);
                    }
                }

                void Cell(string mod)
                {
                    ImGui.PushID(mod);
                    try
                    {
                        map.TryGetValue(mod, out float value);
                        ImGui.SetNextItemWidth(field);
                        if (ImGui.DragFloat("##w", ref value, 0.5f, 0f, 10_000f, "%.0f"))
                        {
                            changedMod = mod;
                            changedWeight = value;
                        }

                        ImGui.SameLine();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(map.ContainsKey(mod) ? MoneyText : DimText, names.Label(mod));
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(mod);
                        }
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

        if (ImGui.SmallButton("Clear the relic weights"))
        {
            Apply(settings with { RelicWeights = [] });
        }
    }

    /// <summary>The exploding props: a rule per path fragment and radius.</summary>
    private void PropTable(ExpeditionSettings settings, ExpeditionView view)
    {
        if (!OverlayLayout.Subsection("Exploding Props"))
        {
            return;
        }

        OverlayLayout.Hint(
            "A charge that reaches one of these sets it off, and ITS blast takes everything in its"
            + " radius - so one charge can cover a far cluster. The chain does not continue from the"
            + " prop. Names and radii differ per tileset; the list below names the expedition objects"
            + " on this map that no rule matched, so the ones that explode can be added.");

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

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
                string path = rule.PathContains;
                if (ImGui.InputText("##path", ref path, 128) && !string.Equals(path, rule.PathContains, StringComparison.Ordinal))
                {
                    changed = rule with { PathContains = path };
                    changedAt = i;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(field);
                float radius = rule.Radius;
                if (ImGui.DragFloat("##r", ref radius, 1f, 0f, 500f, "%.0f cells"))
                {
                    changed = rule with { Radius = radius };
                    changedAt = i;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("x"))
                {
                    remove = i;
                }
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
        ImGui.SameLine();
        if (ImGui.SmallButton("Add rule") && _newProp.Trim().Length > 0)
        {
            var added = new List<PropRule>(rules) { new(_newProp.Trim(), 55f) };
            Apply(settings with { PropRules = added });
            _newProp = string.Empty;
        }

        if (view.UnmatchedProps.Count > 0)
        {
            ImGui.TextColored(WarnText, $"{view.UnmatchedProps.Count} expedition object path(s) on this map match no rule:");
            foreach (string path in view.UnmatchedProps)
            {
                int cut = path.LastIndexOf('/');
                string leaf = cut >= 0 && cut + 1 < path.Length ? path[(cut + 1)..] : path;
                ImGui.Bullet();
                ImGui.TextColored(DimText, leaf);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(path + "\n(click to copy)");
                }

                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(path);
                }
            }

            OverlayLayout.Hint("Scenery is expected here - add a rule only for one that really explodes.");
        }
    }

    /// <summary>The map toggles, the hotkey, the trace.</summary>
    private void Drawing(ExpeditionSettings settings)
    {
        OverlayLayout.Group("On the Map");

        bool rings = settings.ShowRings;
        if (OverlayLayout.Toggle("Blast ring on each planned charge", ref rings))
        {
            Apply(settings with { ShowRings = rings });
            settings = _watch.Settings;
        }

        bool spine = settings.ShowSpine;
        if (OverlayLayout.Toggle("The line the route walks", ref spine))
        {
            Apply(settings with { ShowSpine = spine });
            settings = _watch.Settings;
        }

        bool props = settings.ShowProps;
        if (OverlayLayout.Toggle("Exploding props and their radius", ref props))
        {
            Apply(settings with { ShowProps = props });
            settings = _watch.Settings;
        }

        bool gates = settings.ShowGates;
        if (OverlayLayout.Toggle("Path-blockers and the holes they punch", ref gates))
        {
            Apply(settings with { ShowGates = gates });
            settings = _watch.Settings;
        }

        OverlayLayout.Group("Run");
        int key = RunKeys.IndexOf(settings.RunKey);
        if (OverlayLayout.Combo("Run hotkey", ref key, KeyNames))
        {
            Apply(settings with { RunKey = RunKeys.Choices[key].Code });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Re-plans without a trip to this window. Listens only while a detonator is on the map"
            + " and unpressed, and only while the game or this tool is in front. Off by default:"
            + " no key is safe on every keyboard.");

        bool trace = settings.TraceFile;
        if (OverlayLayout.Toggle("Write the decision trace to config/expedition-plan.txt", ref trace))
        {
            Apply(settings with { TraceFile = trace });
        }

        OverlayLayout.Hint("Every candidate the planner weighed and why it picked what it did. The last lines are also below.");
    }

    /// <summary>The plan, charge by charge.</summary>
    private static void Plan(ExpeditionView view)
    {
        PlanResult plan = view.Route;
        if (!OverlayLayout.Subsection($"The Plan ({plan.Route.Count} charges)", openByDefault: true))
        {
            return;
        }

        if (plan.Route.Count == 0)
        {
            ImGui.TextColored(DimText, view.HasDetonator ? "no plan yet - press Run" : "no detonator in this area");
            return;
        }

        ImGui.TextColored(
            DimText,
            $"anchors {plan.AnchorsCovered}/{plan.Anchors} · covered {plan.Covered}/{plan.Targets} targets · {plan.Weight:0} ex"
            + $" · next charge #{view.NextIndex + 1}");

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
                ImGui.TextColored(ink, (i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(ink, $"{point.Grid.X:0},{point.Grid.Y:0}");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(point.Bridge ? DimText : ink, point.Note);
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

        if (view.Targets.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing found yet");
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
                ImGui.TextColored(target.Primary ? GoodText : DimText, target.Kind.ToString().ToLowerInvariant() + (target.Primary ? " *" : string.Empty));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(DimText, $"{target.Grid.X:0},{target.Grid.Y:0}");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(target.Value > 0 ? MoneyText : DimText, target.Value > 0 ? $"{target.Value:0} ex" : "-");
                ImGui.TableSetColumnIndex(3);
                string what = target.Kind switch
                {
                    ExpeditionKind.Marker => RewardLabel(target.Info) + (target.Tier.Length > 0 ? $" ({target.Tier})" : string.Empty),
                    ExpeditionKind.Remnant => target.Info.Length > 0 ? Short(target.Info) : "(mods unread)",
                    ExpeditionKind.Monolith => target.Info.Length > 0 ? $"anchor {target.Info}" : "(unpriced)",
                    _ => string.Empty,
                };
                ImGui.TextColored(DimText, what);
                if (target.Kind == ExpeditionKind.Remnant && ImGui.IsItemHovered() && target.Info.Length > 0)
                {
                    ImGui.SetTooltip(target.Info.Replace(';', '\n'));
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

        if (lines.Count == 0)
        {
            ImGui.TextColored(DimText, "no plan yet");
            return;
        }

        int from = Math.Max(0, lines.Count - MostTraceLines);
        if (from > 0)
        {
            ImGui.TextColored(DimText, $"...{from} earlier lines in the file, when the trace is written");
        }

        for (int i = from; i < lines.Count; i++)
        {
            ImGui.TextColored(DimText, lines[i]);
        }
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
