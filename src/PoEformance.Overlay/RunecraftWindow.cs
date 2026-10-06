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
            + " poe.ninja price is written on its row, in Exalted. The row's own name stays the"
            + " game's; only the price is added. Every monolith in the area is priced on the map"
            + " too, from what it can roll, before anybody walks to it.");

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
        }

        OverlayLayout.Hint("\"[5] 49 ex\": five holes, best reward 49 Exalted. The holes decide the walk first.");
    }

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

        if (!ImGui.BeginTable($"##offers{view.EntityId}", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("reward");
            ImGui.TableSetupColumn("x");
            ImGui.TableSetupColumn("unit");
            ImGui.TableSetupColumn("total");
            ImGui.TableHeadersRow();

            var shown = 0;
            foreach (MonolithCandidate candidate in view.Candidates)
            {
                double? total = candidate.Total;
                if (least > 0 && (total is null || total < least))
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
                if (total is { } worth)
                {
                    ImGui.TextColored(MoneyText, RunecraftPrices.Format(worth));
                }
                else
                {
                    ImGui.TextColored(DimText, "-");
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
