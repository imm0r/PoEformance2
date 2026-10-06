using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The Runecraft tab: the switch, how the prices are drawn, and what the panel read.
/// </summary>
/// <remarks>
/// THE LIST OF ROWS IS THE DEBUGGING AID the reference plugin shipped as a separate window, kept
/// here on the tab instead: every row the panel shows, the key it was priced by and the door
/// that answered - or the hop that failed. A row with no price on the panel is a row on this
/// list saying why, which is the difference between "the overlay is broken" and "the install
/// could not be read, so a localised reward went unmatched".
///
/// THE NAMES LINE IS FOR THE NEXT PERSON. The panel is found by a fingerprint walk because the
/// reference tool could not read StringIds; this one can, and the line prints what the game
/// calls the elements it resolved. If they are named, the name is the sturdier anchor.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RunecraftWindow
{
    /// <summary>How many rows the list shows. The panel offers a handful; the rest are counted.</summary>
    private const int MostRows = 48;

    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 MoneyText = OverlayInk.Money;

    /// <summary>A rune that gains loot down the chain - the same amber the overlay writes it in.</summary>
    private static readonly Vector4 AmberText = new(1f, 0.80f, 0.30f, 1f);

    private static readonly string[] ColourModes = ["Off", "Relative - against the rows on screen", "Absolute - fixed Exalted thresholds"];

    private readonly RunecraftWatch _watch;
    private readonly MonolithWatch _monoliths;
    private readonly Action<RunecraftSettings> _saved;
    private readonly PriceStore _prices;
    private readonly Action _pricesChanged;

    private bool _unsaved;

    /// <param name="watch">The reader's half for the panel.</param>
    /// <param name="monoliths">The reader's half for the area's monoliths.</param>
    /// <param name="saved">Writes a changed setting down - called once the typing has stopped.</param>
    /// <param name="prices">The poe.ninja book, and its switch.</param>
    /// <param name="pricesChanged">
    /// Says the price switch moved, so the overlay settings that remember it are written. The
    /// same callback the Stash tab's copy of the switch uses, so the two cannot disagree.
    /// </param>
    public RunecraftWindow(
        RunecraftWatch watch, MonolithWatch monoliths, Action<RunecraftSettings> saved, PriceStore prices, Action pricesChanged)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(monoliths);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(pricesChanged);
        _watch = watch;
        _monoliths = monoliths;
        _saved = saved;
        _prices = prices;
        _pricesChanged = pricesChanged;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        RunecraftSettings settings = _watch.Settings;

        bool enabled = settings.Enabled;
        if (OverlayLayout.Master("Runecraft prices", ref enabled))
        {
            Apply(settings with { Enabled = enabled });
            settings = _watch.Settings;
        }

        OverlayLayout.Note(
            "While the Runeshape Combinations panel is open at a monolith, each offered reward's"
            + " poe.ninja price is written on its row, in Exalted, and the rune the recipe would"
            + " propagate down the chain is named before it. The row's own name stays the game's."
            + " Every monolith in the area is priced on the map too, from what it can roll, before"
            + " anybody walks to it.");

        Sources();

        if (settings.Enabled)
        {
            Drawing(settings);
        }

        Flush();

        ImGui.Separator();
        Reading();

        ImGui.Separator();
        Monoliths();
    }

    /// <summary>Where the prices come from, and whether they are being fetched at all.</summary>
    private void Sources()
    {
        OverlayLayout.Group("Where the Prices Come From");

        bool asking = _prices.Enabled;
        if (ImGui.Checkbox("Fetch poe.ninja", ref asking))
        {
            _prices.Enabled = asking;
            _pricesChanged();
        }

        OverlayLayout.Hint(
            "The same switch as the Stash tab's. One anonymous request for a league's prices;"
            + " what goes out is the league name and nothing else.");

        PriceBook book = _prices.Book;
        if (!_prices.Enabled)
        {
            if (_watch.Settings.Enabled)
            {
                OverlayLayout.Warning("Nothing will be priced until poe.ninja is being asked.");
            }
        }
        else if (!book.Ready)
        {
            ImGui.TextColored(DimText, _prices.Busy ? "fetching..." : _prices.Status);
        }
        else
        {
            ImGui.TextColored(
                DimText,
                $"{book.Count} prices for {_prices.League}, 1 Divine = {book.Rate:0} Exalted - {_prices.Status}");
        }

        RewardCatalog catalog = _watch.Catalog;
        ImGui.TextColored(
            catalog.Count > 0 ? DimText : WarnText,
            catalog.Count > 0
                ? $"{catalog.Count} reward names from the install's own tables"
                : "reward names from the shipped table only"
                  + (catalog.Say.Count > 0 ? $" - {catalog.Say[0]}" : " - the install has not been read yet"));
        OverlayLayout.Hint(
            "A recipe names its reward by metadata path. The install's BaseItemTypes gives the"
            + " English name poe.ninja prices under, whatever language the client runs in; the"
            + " shipped data/item-names.json is the same column from an older export.");
    }

    /// <summary>How the prices are drawn.</summary>
    private void Drawing(RunecraftSettings settings)
    {
        OverlayLayout.Group("How the Price Is Drawn");

        int mode = (int)settings.ColourMode;
        if (OverlayLayout.Combo("Colour", ref mode, ColourModes))
        {
            Apply(settings with { ColourMode = (RunecraftColourMode)mode });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Relative tints each price against the median of the rows on screen - a third above"
            + " it green, a third below it red. Absolute uses the two thresholds below.");

        if (settings.ColourMode == RunecraftColourMode.Absolute)
        {
            float good = settings.GoodFrom;
            if (OverlayLayout.Drag("Green from (ex)", ref good, 0.5f, 0.1f, 10_000f, "%.1f"))
            {
                Apply(settings with { GoodFrom = good });
                settings = _watch.Settings;
            }

            float bad = settings.BadBelow;
            if (OverlayLayout.Drag("Red below (ex)", ref bad, 0.1f, 0f, 10_000f, "%.2f"))
            {
                Apply(settings with { BadBelow = bad });
                settings = _watch.Settings;
            }
        }

        bool frame = settings.FrameBest;
        if (OverlayLayout.Toggle("Frame the most valuable row", ref frame))
        {
            Apply(settings with { FrameBest = frame });
            settings = _watch.Settings;
        }

        bool unpriced = settings.ShowUnpriced;
        if (OverlayLayout.Toggle("Mark rows nothing could price", ref unpriced))
        {
            Apply(settings with { ShowUnpriced = unpriced });
            settings = _watch.Settings;
        }

        float offset = settings.XOffset;
        if (OverlayLayout.Slider(
                "Sideways offset", ref offset, -RunecraftSettings.FurthestOffset, RunecraftSettings.FurthestOffset, "%.0f px"))
        {
            Apply(settings with { XOffset = offset });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Slides the price left or right of where it sits, just before the reward's name - for"
            + " a client language whose names run further left, or a letterboxed display.");

        float text = settings.Writing;
        if (OverlayLayout.Slider("Writing size", ref text, RunecraftSettings.SmallestText, RunecraftSettings.LargestText, "%.2f x"))
        {
            Apply(settings with { TextScale = text });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("Against half the row's height, which is what 1.00 writes at.");

        OverlayLayout.Group("On the Map");

        bool labels = settings.MapLabels;
        if (OverlayLayout.Toggle("Price each monolith on the map", ref labels))
        {
            Apply(settings with { MapLabels = labels });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "The best reward each monolith can roll, written at the monolith on whichever map is"
            + " open. Worked out from the monolith's own anchor rune and hole count against the"
            + " install's recipe table - the game's own rule - so it is known before the panel is.");

        bool sockets = settings.MapSockets;
        if (OverlayLayout.Toggle("Hole count before the price", ref sockets))
        {
            Apply(settings with { MapSockets = sockets });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("\"[5] 49 ex\": five holes, best reward 49 Exalted. The holes decide the walk first.");

        Chain(settings);
    }

    /// <summary>The rune chain: the switch, the calibration knob, the map's two rune labels, and the weight per rune.</summary>
    private void Chain(RunecraftSettings settings)
    {
        OverlayLayout.Group("The Rune Chain");

        bool chain = settings.ChainEnabled;
        if (OverlayLayout.Toggle("Value the rune a recipe propagates", ref chain))
        {
            Apply(settings with { ChainEnabled = chain });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "The gold-framed socket's rune propagates to every pack unearthed later in the chain,"
            + " and buffing them raises their drops. Each offer is then worth its reward plus its"
            + " rune: the panel names the rune before the price and rings the strongest in amber,"
            + " beside the green frame on the dearest reward - two calls, not one figure.");

        if (!settings.ChainEnabled)
        {
            return;
        }

        float baseEx = settings.ChainBaseEx;
        if (OverlayLayout.Drag("Loot per pack (ex)", ref baseEx, 0.5f, 0f, 10_000f, "%.1f"))
        {
            Apply(settings with { ChainBaseEx = baseEx });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "Expected drop value of ONE pack of runic monsters. The whole chain value scales with"
            + " this, so it is the knob to calibrate: the reference measured 22 to 58 Exalted a"
            + " wave on high Grand expeditions and sat at the low end.");

        bool rune = settings.MapRune;
        if (OverlayLayout.Toggle("Name the chosen rune on the map", ref rune))
        {
            Apply(settings with { MapRune = rune });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint(
            "\"[5] Opulent\" in place of the price on a monolith whose player gave up reward for the"
            + " rune; \"[5] 49 ex | Opulent\" on one sealed by a reroll, where both are pinned.");

        bool scout = settings.MapScout;
        if (OverlayLayout.Toggle("Scout propagatable runes on the map", ref scout))
        {
            Apply(settings with { MapScout = scout });
            settings = _watch.Settings;
        }

        OverlayLayout.Hint("The best few runes a monolith could still propagate, on a line above its price.");

        Weights(settings);
    }

    /// <summary>The weight per rune, editable as a multiplier or as the Exalted it adds to a wave.</summary>
    private void Weights(RunecraftSettings settings)
    {
        OverlayLayout.Hint(
            "Loot multiplier per propagated rune, and the same weight as the Exalted it adds to"
            + " ONE buffed wave - edit either. 1.00 is no loot effect (pure danger); below 1 is a"
            + " net cost. The magnitudes are server-side, so the ex column is the half to measure.");

        IReadOnlyList<RuneWeight> weights = settings.ChainWeights;
        float baseEx = Math.Max(0.01f, settings.ChainBaseEx);
        RuneWeight? changed = null;
        int changedAt = -1;
        int remove = -1;

        if (ImGui.BeginTable("##runechain", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("rune");
                ImGui.TableSetupColumn("loot x");
                ImGui.TableSetupColumn("ex/wave");
                ImGui.TableSetupColumn("avoid");
                ImGui.TableSetupColumn("effect", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##rm");
                ImGui.TableHeadersRow();

                float field = ImGui.GetFontSize() * 4f;
                for (int i = 0; i < weights.Count; i++)
                {
                    RuneWeight weight = weights[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    try
                    {
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(weight.Avoid ? WarnText : RuneInk(weight.LootMult), weight.Rune);

                        ImGui.TableSetColumnIndex(1);
                        ImGui.SetNextItemWidth(field);
                        float mult = weight.LootMult;
                        if (ImGui.DragFloat("##mult", ref mult, 0.01f, 0f, RunecraftSettings.LargestMult, "%.2f"))
                        {
                            changed = weight with { LootMult = mult };
                            changedAt = i;
                        }

                        // The same weight as ex per wave, the measurable form: editing it writes
                        // the multiplier back, so the two columns are one number.
                        ImGui.TableSetColumnIndex(2);
                        ImGui.SetNextItemWidth(field);
                        float perWave = (weight.LootMult - 1f) * baseEx;
                        if (ImGui.DragFloat("##ex", ref perWave, 0.1f, -baseEx, (RunecraftSettings.LargestMult - 1f) * baseEx, "%.1f"))
                        {
                            changed = weight with { LootMult = 1f + (perWave / baseEx) };
                            changedAt = i;
                        }

                        ImGui.TableSetColumnIndex(3);
                        bool avoid = weight.Avoid;
                        if (ImGui.Checkbox("##avoid", ref avoid))
                        {
                            changed = weight with { Avoid = avoid };
                            changedAt = i;
                        }

                        ImGui.TableSetColumnIndex(4);
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(DimText, RuneChain.EffectOf(weight.Rune));

                        ImGui.TableSetColumnIndex(5);
                        if (ImGui.SmallButton("x"))
                        {
                            remove = i;
                        }

                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip("Take the rune out of the table - it is then worth 1.00, no loot effect");
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
            var edited = new RuneWeight[weights.Count];
            for (int i = 0; i < edited.Length; i++)
            {
                edited[i] = i == changedAt ? changed : weights[i];
            }

            Apply(settings with { ChainWeights = edited });
            settings = _watch.Settings;
        }
        else if (remove >= 0)
        {
            var kept = new List<RuneWeight>(weights.Count);
            for (int i = 0; i < weights.Count; i++)
            {
                if (i != remove)
                {
                    kept.Add(weights[i]);
                }
            }

            Apply(settings with { ChainWeights = kept });
            settings = _watch.Settings;
        }

        // A rune the table does not list yet: the install's names once read, the known
        // thirty-four until then - the same order either way.
        RuneChainTable table = _monoliths.Table;
        ImGui.SetNextItemWidth(OverlayLayout.FieldWidth());
        if (ImGui.BeginCombo("Add a rune", "choose..."))
        {
            try
            {
                for (int i = 0; i < table.Count; i++)
                {
                    string name = table.Name(i);
                    if (Listed(weights, name))
                    {
                        continue;
                    }

                    string effect = RuneChain.EffectOf(name);
                    if (ImGui.Selectable(effect.Length > 0 ? $"{name}  -  {effect}" : name))
                    {
                        var added = new List<RuneWeight>(weights) { new(name, 1f) };
                        Apply(settings with { ChainWeights = added });
                        break;
                    }
                }
            }
            finally
            {
                ImGui.EndCombo();
            }
        }

        if (ImGui.SmallButton("Reset to the tier-list defaults"))
        {
            Apply(settings with { ChainWeights = RuneChain.DefaultWeights });
        }
    }

    private static bool Listed(IReadOnlyList<RuneWeight> weights, string rune)
    {
        foreach (RuneWeight weight in weights)
        {
            if (string.Equals(weight.Rune, rune, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The ink a rune is written in on the tab: amber gains, warn costs, dim neutral.</summary>
    private static Vector4 RuneInk(double mult) => mult > 1 ? AmberText : mult < 1 ? WarnText : DimText;

    /// <summary>The area's monoliths: what each is, what it can roll, and what the walk read.</summary>
    private void Monoliths()
    {
        MonolithsView monoliths = _monoliths.View;
        OverlayLayout.Group("Monoliths in This Area");
        ImGui.TextColored(monoliths.Any ? DimText : WarnText, monoliths.Status);

        RecipeCatalog catalog = _monoliths.Catalog;
        if (catalog.Count == 0)
        {
            ImGui.TextColored(WarnText, catalog.Say.Count > 0 ? catalog.Say[0] : "the install's recipe tables have not been read yet");
        }

        if (!monoliths.Any)
        {
            return;
        }

        RunecraftSettings settings = _watch.Settings;
        float least = settings.ListMinEx;
        if (OverlayLayout.Drag("Hide offers under (ex)", ref least, 0.5f, 0f, 10_000f, "%.1f"))
        {
            Apply(settings with { ListMinEx = least });
            settings = _watch.Settings;
        }

        foreach (MonolithView view in monoliths.Monoliths)
        {
            string mark = view.PanelOpen ? "> " : view.Collected ? "(collected) " : string.Empty;
            bool open = ImGui.CollapsingHeader($"{mark}{view.Headline}###monolith{view.EntityId}");
            if (!open)
            {
                continue;
            }

            Monolith(view, catalog, settings.ListMinEx);
        }
    }

    /// <summary>One monolith unfolded: its reading, then its offers.</summary>
    private void Monolith(MonolithView view, RecipeCatalog catalog, float least)
    {
        MonolithStation station = view.Station;
        if (!station.Resolved)
        {
            ImGui.TextColored(WarnText, "  no station: " + station.Why);
        }
        else if (station.Why.Length > 0)
        {
            ImGui.TextColored(WarnText, "  " + station.Why);
        }

        ImGui.TextColored(
            DimText,
            $"  holes {station.HoleCount} (state {view.States.Sockets}) · mode {station.RecipeMode}"
            + $" · gold sockets {string.Join(",", station.GlowSockets)} · empowered {(station.Empowered ? "yes" : "no")}"
            + (view.Rerolled ? " · sealed by a reroll" : string.Empty)
            + (view.Foreign ? " · standalone, not part of the dig" : string.Empty)
            + (station.Committed ? $" · chosen {station.SelectedRecipeId}" : string.Empty)
            + (view.ChosenRune.Length > 0 ? $" · propagates {view.ChosenRune}" : string.Empty)
            + (view.Scout.Count > 0 ? $" · could propagate {string.Join(", ", view.Scout)}" : string.Empty)
            + (view.Listed ? string.Empty : " · out of range, as last read"));
        if (view.States.Summary.Length > 0)
        {
            ImGui.TextColored(DimText, "  states: " + view.States.Summary);
        }

        if (view.Candidates.Count == 0)
        {
            ImGui.TextColored(DimText, catalog.Count == 0 ? "  (no catalogue to offer from)" : "  (no recipe fits this monolith)");
            return;
        }

        // The chain's three columns only where something propagates: the standalone monolith
        // and the ones the game frames no socket on would show a column of dashes.
        RuneChainTable table = _monoliths.Table;
        bool chained = table.Enabled && station.FramesASocket && station.GlowSockets.Count > 0 && !view.Foreign;
        if (!ImGui.BeginTable($"##offers{view.EntityId}", chained ? 7 : 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("reward");
            ImGui.TableSetupColumn("x");
            ImGui.TableSetupColumn("unit");
            ImGui.TableSetupColumn("total");
            if (chained)
            {
                ImGui.TableSetupColumn("rune");
                ImGui.TableSetupColumn("chain");
                ImGui.TableSetupColumn("all told");
            }

            ImGui.TableHeadersRow();

            var shown = 0;
            foreach (MonolithCandidate candidate in view.Candidates)
            {
                double? total = candidate.Total;
                double worth = chained ? candidate.Joint : total ?? 0;
                if (least > 0 && worth < least)
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(candidate.Reward);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"[{candidate.Recipe.Size}] {Runes(candidate.Recipe, catalog, station.AnchorHole)}  ·  {candidate.Recipe.Id}");
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(Math.Max(1, candidate.Recipe.RewardCount).ToString(System.Globalization.CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(
                    candidate.Price.Priced ? DimText : WarnText,
                    candidate.Price.Unit is { } unit ? RunecraftPrices.Format(unit) : candidate.Price.Via);

                ImGui.TableSetColumnIndex(3);
                if (total is { } paid)
                {
                    ImGui.TextColored(MoneyText, RunecraftPrices.Format(paid));
                }
                else
                {
                    ImGui.TextColored(DimText, "-");
                }

                if (chained)
                {
                    ImGui.TableSetColumnIndex(4);
                    if (candidate.Rune >= 0)
                    {
                        double mult = table.EffMultAt(view.Site, candidate.Rune, station.Empowered);
                        ImGui.TextColored(RuneInk(mult), table.Name(candidate.Rune) + (candidate.Taken ? " (taken)" : string.Empty));
                    }
                    else
                    {
                        ImGui.TextColored(DimText, "-");
                    }

                    ImGui.TableSetColumnIndex(5);
                    if (candidate.ChainEx != 0)
                    {
                        ImGui.TextColored(
                            candidate.ChainEx > 0 ? AmberText : WarnText,
                            (candidate.ChainEx > 0 ? "+" : "-") + RunecraftPrices.Format(Math.Abs(candidate.ChainEx)));
                    }
                    else
                    {
                        ImGui.TextColored(DimText, "-");
                    }

                    ImGui.TableSetColumnIndex(6);
                    ImGui.TextColored(MoneyText, RunecraftPrices.Format(candidate.Joint));
                }

                shown++;
            }

            if (shown == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(DimText, "(nothing above the threshold)");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>The recipe's runes by hole, the anchor's in brackets.</summary>
    private static string Runes(RuneshapeRecipe recipe, RecipeCatalog catalog, int anchorHole)
    {
        var parts = new string[recipe.Runes.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            string name = catalog.RuneName(recipe.Runes[i]);
            parts[i] = i == anchorHole ? $"[{name}]" : name;
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What the panel read, row by row, with the door each price came through.</summary>
    private void Reading()
    {
        RunecraftView view = _watch.View;
        OverlayLayout.Group("What the Panel Read");

        if (!view.Open)
        {
            ImGui.TextColored(DimText, view.Status);
            return;
        }

        ImGui.TextColored(view.Priced > 0 ? GoodText : WarnText, view.Status);
        if (view.Named.Length > 0)
        {
            ImGui.TextColored(DimText, view.Named);
            OverlayLayout.Hint(
                "What the game calls the elements the fingerprint walk resolved. A named panel is"
                + " a sturdier anchor than a fingerprint - see RunecraftPanel in the schema.");
        }

        // Where the first row's text was measured, because that is what the price is placed
        // against: a child index that drifts shows here as "not read" or as a spanning
        // element, with the price back at the edge, before anyone wonders why it moved.
        if (view.Rewards.Count > 0)
        {
            RunecraftReward first = view.Rewards[0];
            ImGui.TextColored(
                first.TextAnchors(0f) ? DimText : WarnText,
                first.Text is { } text
                    ? $"row text {text.Width:0} px wide, {text.Left - first.Where.Left:0} px into a {first.Where.Width:0} px row"
                      + (first.TextAnchors(0f) ? " - the price sits before it" : " - not a text inside its row, so the price sits at the row's edge")
                    : "row text element not read - the price sits at the row's edge");
        }

        IReadOnlyList<(RunecraftRow Row, RunecraftPrice Price)> rows = _watch.Studied;
        if (rows.Count == 0)
        {
            return;
        }

        if (!ImGui.BeginTable("##runecraft-rows", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("row");
            ImGui.TableSetupColumn("priced by");
            ImGui.TableSetupColumn("via");
            ImGui.TableSetupColumn("ex");
            ImGui.TableHeadersRow();

            int shown = 0;
            foreach ((RunecraftRow row, RunecraftPrice price) in rows)
            {
                if (shown++ >= MostRows)
                {
                    break;
                }

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.Label);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(DimText, price.Key.Length > 0 ? price.Key : row.RecipeId.Length > 0 ? row.RecipeId : "-");

                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(price.Priced ? DimText : WarnText, price.Via);

                ImGui.TableSetColumnIndex(3);
                if (price.Total is { } total)
                {
                    ImGui.TextColored(MoneyText, RunecraftPrices.Format(total));
                }
                else
                {
                    ImGui.TextColored(DimText, "-");
                }
            }

            if (rows.Count > MostRows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(DimText, $"...and {rows.Count - MostRows} more");
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>Takes a changed setting into use at once; the write waits for the typing to stop.</summary>
    /// <remarks>Both watches read the one record, so a change reaches the panel and the map together.</remarks>
    private void Apply(RunecraftSettings changed)
    {
        RunecraftSettings settings = changed.Normalised();
        _watch.Settings = settings;
        _monoliths.Settings = settings;
        _unsaved = true;
    }

    /// <summary>Writes the settings once nothing is being edited any more.</summary>
    /// <remarks>
    /// A slider is dragged a pixel at a time, and saving on every pixel writes the file for each
    /// of them - the same rule the ritual weights follow.
    /// </remarks>
    private void Flush()
    {
        if (_unsaved && !ImGui.IsAnyItemActive())
        {
            _unsaved = false;
            _saved(_watch.Settings);
        }
    }
}
