using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What every quest is waiting on, in the game's own words.
/// </summary>
/// <remarks>
/// The tracker shows the quest you are tracking. This shows all of them, because the question
/// it exists for is "what have I left behind" rather than "what am I doing" - the act-two side
/// quest nobody remembers starting is exactly the one the in-game tracker will not surface.
///
/// The conditions are behind a fold rather than on the row. A step's flags are the PROOF that
/// the line above it is the right line, which is worth having on the day the column layout
/// drifts and worth nothing on every other day.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class QuestWindow
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);
    private static readonly Vector4 ActText = new(0.75f, 0.8f, 0.95f, 1f);

    private readonly QuestWatch _watch;
    private string _search = string.Empty;
    private bool _done;
    private bool _conditions;

    public QuestWindow(QuestWatch watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        _watch = watch;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        QuestOutlook outlook = _watch.Outlook;

        if (outlook.Quests.Count == 0)
        {
            ImGui.TextColored(WarnText, "no quests read");

            // The flag count separately, because the two halves fail independently: flags out
            // of the process, tables out of the install. "42 flags but no tables" and "nothing
            // at all" want opposite fixes and would otherwise look the same.
            ImGui.TextColored(
                _watch.Flags > 0 ? GoodText : DimText,
                _watch.Flags > 0
                    ? $"{_watch.Flags} flags are set - the tables are what did not load"
                    : "no flags read either - the chain off ServerData did not resolve");

            // The reasons, always. Three tables have to be found, parsed and agree with the
            // column list, and "it shows nothing" has a different fix for each of them.
            foreach (string note in outlook.Notes)
            {
                ImGui.TextWrapped(note);
            }

            return;
        }

        int active = outlook.Active.Count();
        ImGui.TextColored(GoodText, $"{active} quests in progress");
        ImGui.SameLine();
        ImGui.TextColored(DimText, $"of {outlook.Quests.Count}");

        // Ordinary rather than alarming: most states declare only the flags that must be
        // PRESENT, so every step already passed goes on holding and the furthest along is the
        // answer. Kept on screen because a sudden jump in it is what a mis-read condition
        // column would look like, and that is worth noticing on the day it happens.
        int ambiguous = outlook.Ambiguous.Count();
        if (ambiguous > 0)
        {
            ImGui.TextColored(
                DimText,
                $"{ambiguous} quests have several steps holding - normal, the furthest along is shown");
        }

        ImGui.Checkbox("finished too", ref _done);
        ImGui.SameLine();
        ImGui.Checkbox("show the flags", ref _conditions);

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##quest-search", "filter by name", ref _search, 64);

        ImGui.Separator();

        if (!ImGui.BeginChild("quest-list", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var act = -1;
        foreach (QuestState quest in outlook.Quests)
        {
            if (!_done && quest.Complete)
            {
                continue;
            }

            if (quest.Now is null && !_done)
            {
                // No step holds: either the quest has not started or the join failed for it.
                // Hidden by default, shown with the finished ones, because on a fresh install
                // most of the table is in this state and it would bury everything else.
                continue;
            }

            if (_search.Length > 0
                && !quest.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                && !quest.Id.Contains(_search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (quest.Act != act)
            {
                act = quest.Act;
                ImGui.TextColored(ActText, act > 0 ? $"Act {act}" : "no act");
                ImGui.Separator();
            }

            Row(quest);
        }

        ImGui.EndChild();
    }

    private void Row(QuestState quest)
    {
        ImGui.PushID(quest.Row);

        ImGui.TextColored(
            quest.Complete ? DimText : GoodText,
            quest.Name.Length > 0 ? quest.Name : quest.Id);

        if (quest.Complete)
        {
            ImGui.SameLine();
            ImGui.TextColored(DimText, "done");
        }

        // The objective, which is the line this whole window exists to show. Empty is a real
        // answer - some states carry a Message and no tracker Text - so it says which.
        if (quest.Objective.Length > 0)
        {
            ImGui.TextWrapped($"    {quest.Objective}");
        }
        else if (quest.Now is { Message.Length: > 0 })
        {
            ImGui.TextColored(DimText, $"    {quest.Now.Message}");
        }
        else if (!quest.Complete)
        {
            ImGui.TextColored(DimText, "    (this step carries no text)");
        }

        if (quest.Next is { Text.Length: > 0 } next)
        {
            ImGui.TextColored(DimText, $"    then: {next.Text}");
        }

        if (_conditions)
        {
            Steps(quest);
        }

        ImGui.PopID();
    }

    /// <summary>
    /// Every step of a quest, marked with whether it holds - the view that shows WHY the line
    /// above is what it is, and the only way to see several holding at once.
    /// </summary>
    private void Steps(QuestState quest)
    {
        ImGui.TextColored(
            quest.Holding.Count > 1 ? WarnText : DimText,
            $"    {quest.Steps.Count} steps, {quest.Holding.Count} holding");

        foreach (QuestStep step in quest.Steps)
        {
            bool holds = quest.Holding.Contains(step);
            ImGui.TextColored(
                holds ? GoodText : DimText,
                $"    {(holds ? "->" : "  ")} order {step.Order,-4} present {step.Present.Count,-2}"
                + $" missing {step.Missing.Count,-2}  {Short(step.Text)}");

            if (!holds || !_conditions)
            {
                continue;
            }

            foreach (int flag in step.Present)
            {
                ImGui.TextColored(DimText, $"          set: {Name(flag)}");
            }

            foreach (int flag in step.Missing)
            {
                ImGui.TextColored(DimText, $"          not set: {Name(flag)}");
            }
        }
    }

    private static string Short(string text)
        => text.Length <= 60 ? text : text[..57] + "...";

    private string Name(int row)
    {
        string id = _watch.FlagId(row);
        return id.Length > 0 ? id : $"flag row {row}";
    }
}
