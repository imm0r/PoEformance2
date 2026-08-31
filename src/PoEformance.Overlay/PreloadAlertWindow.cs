using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The watch list, and the switches that decide how it is said.
/// </summary>
/// <remarks>
/// ONE ROW PER PATH, which is what matching exactly costs and what it buys. A fragment list was
/// a dozen lines for the whole game; this is a line per file, and the game loads several per
/// mechanic. What it buys is that a row means exactly one thing, and the column that says
/// whether it is in THIS area turns a wrong path from silence into something visible.
///
/// THE ORDER IS THE PRIORITY, edited with the arrows rather than typed as a number. The
/// reference keeps a priority field and a container that re-indexes on every removal; the two
/// can disagree, and when they do the file says one thing and the window shows another.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PreloadAlertWindow
{
    private static readonly Vector4 Dim = OverlayInk.Quiet;
    private static readonly Vector4 Good = OverlayInk.Good;

    private const ImGuiColorEditFlags ColourFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel;

    private readonly PreloadWatch _watch;
    private readonly Func<PreloadSettings> _settings;
    private readonly Action<PreloadSettings> _switched;
    private readonly Action<IReadOnlyList<PreloadAlertEntry>> _listed;
    private readonly Action _sayItNow;
    private readonly Func<IReadOnlyList<PreloadAlertEntry>>? _starter;

    private string _filter = string.Empty;
    private string _addPath = string.Empty;
    private string _addCalled = string.Empty;
    private string _tookStarter = string.Empty;

    public PreloadAlertWindow(
        PreloadWatch watch,
        Func<PreloadSettings> settings,
        Action<PreloadSettings> switched,
        Action<IReadOnlyList<PreloadAlertEntry>> listed,
        Action sayItNow,
        Func<IReadOnlyList<PreloadAlertEntry>>? starter = null)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(switched);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(sayItNow);
        _watch = watch;
        _settings = settings;
        _switched = switched;
        _listed = listed;
        _sayItNow = sayItNow;
        _starter = starter;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        Switches();
        ImGui.Spacing();
        OverlayFonts.SectionTitle("what to watch for");
        ImGui.Spacing();
        List();
    }

    private void Switches()
    {
        PreloadSettings had = _settings();
        PreloadSettings wanted = had;

        bool window = had.Window;
        if (ImGui.Checkbox("keep a window in the corner", ref window))
        {
            wanted = wanted with { Window = window };
        }

        ImGui.SameLine();
        bool card = had.Card;
        if (ImGui.Checkbox("and say it on the way in", ref card))
        {
            wanted = wanted with { Card = card };
        }

        bool timer = had.Timer;
        if (ImGui.Checkbox("show time since the area loaded", ref timer))
        {
            wanted = wanted with { Timer = timer };
        }

        ImGui.SameLine();
        bool empty = had.HideWhenEmpty;
        if (ImGui.Checkbox("hide the window when nothing matched", ref empty))
        {
            wanted = wanted with { HideWhenEmpty = empty };
        }

        bool town = had.HideInTown;
        if (ImGui.Checkbox("hide it in town and hideouts", ref town))
        {
            wanted = wanted with { HideInTown = town };
        }

        ImGuiText.Wrapped(
            Dim,
            "The file list is not refreshed in town, so what it holds there is the last real "
            + "area. Left on, the window says so rather than showing it as though it were here.");

        if (wanted != had)
        {
            _switched(wanted);
        }

        if (ImGui.SmallButton("say it again now"))
        {
            _sayItNow();
        }
    }

    private void List()
    {
        IReadOnlyList<PreloadAlertEntry> entries = _watch.Watching;
        HashSet<string>? here = PreloadAlerts.Lookup(_watch.All);

        Adding();
        Starter();

        if (entries.Count == 0)
        {
            ImGuiText.Wrapped(
                Dim,
                _starter is null
                    ? "Nothing is being watched for, and the shipped list was not found beside the "
                      + "program. Build one from the \"In this area\" tab - walk into a map and add "
                      + "the rows that mean something."
                    : "Nothing is being watched for. The button above starts you off with six "
                      + "mechanics measured across twenty captured maps; everything else is built "
                      + "from the \"In this area\" tab, one row per path that means something.");
            return;
        }

        OverlayLayout.Search("##preload-filter", "filter...", ref _filter, 200);

        // The count on its own line rather than welded to the filter's right with SameLine.
        // It is a wrapping paragraph, so where it started depended on the filter's width and
        // where it ENDED depended on the window's - which is how a caption becomes a layout.
        OverlayLayout.Note($"{entries.Count} watched, {_watch.Found.Count} of them here.");

        if (!ImGui.BeginTable(
                "preload-alerts",
                7,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            return;
        }

        var changed = new List<PreloadAlertEntry>(entries);
        var touched = false;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("on");
            ImGui.TableSetupColumn("order");
            ImGui.TableSetupColumn("log");
            ImGui.TableSetupColumn("colour");
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("path", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##delete");
            ImGui.TableHeadersRow();

            for (var at = 0; at < changed.Count; at++)
            {
                PreloadAlertEntry entry = changed[at];
                if (_filter.Length > 0
                    && !entry.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                    && !entry.Shown.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.PushID(at);
                try
                {
                    Edit edit = Row(ref entry, changed, at, here);
                    if (edit == Edit.None)
                    {
                        continue;
                    }

                    touched = true;
                    if (edit == Edit.Field)
                    {
                        changed[at] = entry;
                        continue;
                    }

                    // A DELETE OR A MOVE ENDS THE FRAME'S DRAWING. Both change what index
                    // every row below sits at, and a loop that carried on would write the row
                    // it has in hand back to a position now holding a different entry - which
                    // is how deleting one line silently overwrites the next. It redraws on the
                    // very next frame, so what is lost is nothing.
                    break;
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

        if (touched)
        {
            _watch.Watch(changed);
            _listed(_watch.Watching);
        }
    }

    /// <summary>What a row's controls did, which decides whether the loop may carry on.</summary>
    private enum Edit
    {
        /// <summary>Nothing was touched.</summary>
        None,

        /// <summary>Something about THIS entry changed, and its position did not.</summary>
        Field,

        /// <summary>The list's shape changed, so every index below this one has moved.</summary>
        Shape,
    }

    /// <summary>One row. Says what happened to it.</summary>
    private Edit Row(
        ref PreloadAlertEntry entry,
        List<PreloadAlertEntry> all,
        int at,
        IReadOnlySet<string>? here)
    {
        var edit = Edit.None;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        bool on = entry.Enabled;
        if (ImGui.Checkbox("##on", ref on))
        {
            entry = entry with { Enabled = on };
            edit = Edit.Field;
        }

        // ORDER BY ARROWS, not by a typed number. A number has to be kept in step with the
        // list's actual order by something, and that something is where the reference's
        // container spends its complexity and its bugs.
        ImGui.TableNextColumn();
        var moved = false;

        ImGui.BeginDisabled(at == 0);
        if (ImGui.SmallButton("^"))
        {
            (all[at - 1], all[at]) = (all[at], all[at - 1]);
            moved = true;
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(at >= all.Count - 1);
        if (ImGui.SmallButton("v"))
        {
            (all[at + 1], all[at]) = (all[at], all[at + 1]);
            moved = true;
        }

        ImGui.EndDisabled();

        // AFTER both blocks are closed, never from inside one. Returning out of a BeginDisabled
        // leaves ImGui's stack one deep, and the assert that eventually catches that takes the
        // process down somewhere else entirely.
        if (moved)
        {
            return Edit.Shape;
        }

        ImGui.TableNextColumn();
        bool log = entry.Log;
        if (ImGui.Checkbox("##log", ref log))
        {
            entry = entry with { Log = log };
            edit = Edit.Field;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Also write a line to preloads-found.log when an area has it.");
        }

        ImGui.TableNextColumn();
        Vector4 colour = ImGui.ColorConvertU32ToFloat4(entry.Colour);
        if (ImGui.ColorEdit4("##colour", ref colour, ColourFlags))
        {
            entry = entry with { Colour = ImGui.ColorConvertFloat4ToU32(colour) };
            edit = Edit.Field;
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        string called = entry.Called;
        if (ImGui.InputText("##name", ref called, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            entry = entry with { Called = called };
            edit = Edit.Field;
        }

        // WHETHER THIS ROW IS IN THE AREA YOU ARE STANDING IN. A path that matches nothing is
        // otherwise indistinguishable from an area that simply does not have the thing, and a
        // typo in one would never announce itself.
        //
        // A row can carry several paths - see PreloadAlertEntry.Every - so the cell says how many
        // and the hover lists them. Without the count a two-path row and a one-path row look
        // identical, and the second path is then invisible until it fires.
        ImGui.TableNextColumn();
        bool matching = PreloadAlerts.Anywhere(entry, here);
        int extra = entry.Also?.Count ?? 0;
        ImGui.TextColored(
            matching ? Good : Dim,
            extra > 0 ? $"{entry.Path}  (+{extra})" : entry.Path);

        if (ImGui.IsItemHovered() && (matching || extra > 0))
        {
            var said = new System.Text.StringBuilder();
            if (matching)
            {
                said.Append("this area loaded it\n\n");
            }

            foreach (string one in entry.Every)
            {
                said.Append(PreloadAlerts.Here(one, here) ? "* " : "  ").Append(one).Append('\n');
            }

            ImGui.SetTooltip(said.ToString().TrimEnd());
        }

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("delete"))
        {
            all.RemoveAt(at);
            return Edit.Shape;
        }

        return edit;
    }

    /// <summary>
    /// Offers the shipped list, and says what taking it did.
    /// </summary>
    /// <remarks>
    /// IT ADDS RATHER THAN REPLACES, and the count it reports is what actually went in: rows
    /// already being watched are refused by PreloadWatch.Add, so pressing it twice says "0 added"
    /// instead of drawing everything a second time. A list somebody has curated is not something
    /// to overwrite because they pressed a button to see what was in the shipped one.
    /// </remarks>
    private void Starter()
    {
        if (_starter is null)
        {
            return;
        }

        if (ImGui.SmallButton("add the shipped list"))
        {
            int added = _starter().Count(_watch.Add);
            if (added > 0)
            {
                _listed(_watch.Watching);
            }

            _tookStarter = added > 0
                ? $"added {added}"
                : "already had all of them";
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Breach, Delirium, Abyss, Vaal, Essence and Azmeri, as paths that carry the\n"
                + "mechanic's own name and appeared in no map without it - measured over\n"
                + "twenty captured areas. It adds to your list rather than replacing it.");
        }

        if (_tookStarter.Length > 0)
        {
            ImGui.SameLine();
            ImGuiText.Wrapped(Dim, _tookStarter);
        }
    }

    private void Adding()
    {
        // The short field keeps the shared-line width; the path takes whatever is left, less
        // room for the button. Both were guesses before - 12 lines and 28 - and the second one
        // ran off any window narrower than somebody had happened to have open.
        ImGui.SetNextItemWidth(OverlayLayout.CompactWidth() * 2f);
        ImGui.InputTextWithHint("##add-name", "name", ref _addCalled, 100);
        ImGui.SameLine();
        OverlayLayout.Search(
            "##add-path", "Metadata/...", ref _addPath, 250, OverlayLayout.ButtonRoom("add"));
        ImGui.SameLine();

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_addPath));
        if (ImGui.SmallButton("add"))
        {
            if (_watch.Add(new PreloadAlertEntry(
                    _addPath.Trim(),
                    string.IsNullOrWhiteSpace(_addCalled) ? PreloadMeanings.Suggest(_addPath.Trim()) : _addCalled)))
            {
                _listed(_watch.Watching);
            }

            _addPath = string.Empty;
            _addCalled = string.Empty;
        }

        ImGui.EndDisabled();
    }
}
