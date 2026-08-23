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
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);
    private static readonly Vector4 ActText = new(0.75f, 0.8f, 0.95f, 1f);

    private readonly QuestWatch _watch;
    private string _search = string.Empty;
    private bool _done;
    private bool _conditions;

    /// <summary>Which quest has its walkthrough open, or -1. One at a time on purpose.</summary>
    private int _walkthrough = -1;

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

        // MESSAGE FIRST, measured rather than assumed: shown side by side against the game's
        // own panel, this column matched it word for word - "Find the Red Vale", "Search for
        // the meaning of the Runes etched into the Tree of Souls" - while Text was the longer
        // sentence every time.
        if (quest.Objective.Length > 0)
        {
            ImGui.TextWrapped($"    {quest.Objective}");
        }
        else if (!quest.Complete)
        {
            ImGui.TextColored(DimText, "    (this step carries no text)");
        }

        // And the long form under it, because it is the half that says WHERE. "Slay the
        // Devourer" is the objective; "The Devourer lives underground in a Mud Burrow" is the
        // part worth having on screen.
        if (quest.Detail.Length > 0)
        {
            ImGui.TextColored(DimText, $"    {quest.Detail}");
        }

        // WHERE, when the step names a place. MapPins carries no coordinates - a name, a world
        // area and an act - so this is "it is over there" and not a marker.
        if (quest.Now is { } now && now.Where.Length > 0)
        {
            ImGui.TextColored(ActText, $"    at: {now.Where}");
        }

        if (quest.Next is { } next && next.Line.Length > 0)
        {
            ImGui.TextColored(DimText, $"    then: {next.Line}");
        }

        // One quest's walkthrough at a time. The steps are the same list every time and there
        // are up to 87 of them, so opening all of them at once is a wall rather than a view.
        bool open = _walkthrough == quest.Row;
        if (ImGui.SmallButton(open ? "hide the steps" : "all the steps"))
        {
            _walkthrough = open ? -1 : quest.Row;
        }

        if (open)
        {
            Walkthrough(quest);
        }

        if (_conditions)
        {
            Steps(quest);
        }

        // Unconditionally. An earlier version returned out of the collapsed path before this,
        // so every frame pushed an id it never popped - the stack grows without bound and ids
        // start colliding, which shows up as the wrong quest's button responding.
        ImGui.PopID();
    }

    /// <summary>
    /// The quest as a route: what is behind, what is now, and what is still ahead.
    /// </summary>
    /// <remarks>
    /// A PATH, NOT A CHECKLIST. QuestStates is a state machine, so a quest with branches carries
    /// a state per branch - The Runeseeker has 87 and most are the same sentence for the
    /// different regions it can be done in. Listed one per line that is a wall, so consecutive
    /// states wording the same objective are folded into one leg and the count rides beside it.
    ///
    /// FOLDING SAYS MORE, NOT LESS. What twenty identical lines differ in is the PLACE, so the
    /// fold gathers those and names them all on one line - "Search the region for more
    /// Runestones - at: The Mire, The Bluff, ...". The wall was hiding the only part that varied.
    ///
    /// What is BEHIND is a count and not a list. Nobody needs to read the eleven things they
    /// already did, and the one line saying how many there were is what places the current step
    /// in the quest.
    /// </remarks>
    private void Walkthrough(QuestState quest)
    {
        if (quest.Passed > 0)
        {
            ImGui.TextColored(DimText, $"      {quest.Passed} steps behind you");
        }

        IReadOnlyList<QuestLeg> route = quest.Route;
        var states = 0;

        for (var at = 0; at < route.Count; at++)
        {
            QuestLeg leg = route[at];
            bool now = at == 0;
            states += leg.States;

            ImGui.TextColored(now ? GoodText : DimText, $"      {(now ? "->" : "  ")} {leg.Line}");

            // The count only where there IS one, because "x1" on every other line is noise that
            // makes the number stop being read at all.
            if (leg.Branches)
            {
                ImGui.SameLine();
                ImGui.TextColored(WarnText, $"x{leg.States}");
            }

            // The place on EVERY leg of the route, unlike the long form below it. Where a step
            // sends you is the half that makes a route worth reading ahead of time - and after a
            // fold it is a list of them, so it wraps rather than running off the window.
            if (leg.Where.Length > 0)
            {
                Wrapped(ActText, $"           at: {leg.Where}");
            }

            // The long form only for the leg in hand. On the ones still ahead it doubles the
            // height of the list to say what its own first line already said.
            if (now && leg.Detail.Length > 0)
            {
                Wrapped(DimText, $"           {leg.Detail}");
            }
        }

        if (route.Count > 1)
        {
            ImGui.TextColored(DimText, $"      {route.Count - 1} steps ahead");
        }

        // Only when folding actually happened, and it is the one line that stops the route being
        // read as a tally of things to go and do: those states are alternatives, so a character
        // walks ONE of each folded run and not all of them.
        if (states > route.Count)
        {
            ImGui.TextColored(
                DimText,
                $"      folded from {states} states - a quest with branches carries one per"
                + " branch, and you walk one of each");
        }
    }

    /// <summary>Coloured text that wraps, which TextColored on its own does not.</summary>
    private static void Wrapped(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
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
