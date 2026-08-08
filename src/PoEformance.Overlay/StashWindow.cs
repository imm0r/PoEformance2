using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Overlay;

/// <summary>
/// Everything in every stash tab, down to each item's stats.
/// </summary>
/// <remarks>
/// A LIST, not an overlay, because the question is one nobody asks while fighting: what have I
/// got, and where. The game answers it one tab at a time and only for the tab in front of you;
/// this answers it for all of them at once, searchable.
///
/// THE STATS ARE THE ITEM'S OWN, as the game resolved them - so what a row says is what the
/// item says, not a recomputation that could drift from it. Unworded stats keep their id, which
/// is what makes this useful for reverse-engineering as well as for sorting through loot.
///
/// IT CANNOT SHOW A TAB NOBODY HAS OPENED. The client asks the server for a tab's contents when
/// it is opened and not before, so an unopened tab reads as an empty one - which is why nothing
/// here calls a total "your stash", and why an empty page says both things it might be.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StashWindow
{
    /// <summary>How many items are listed at once. The rest are counted, not drawn.</summary>
    public const int MostRows = 400;

    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);

    /// <summary>What each rarity looks like, in the game's own colours.</summary>
    private static readonly Vector4[] ByRarity =
    [
        new(0.86f, 0.86f, 0.86f, 1f),   // normal
        new(0.53f, 0.53f, 1f, 1f),      // magic
        new(1f, 1f, 0.46f, 1f),         // rare
        new(0.68f, 0.4f, 0.15f, 1f),    // unique
        new(0.29f, 0.78f, 0.29f, 1f),   // quest
        new(0.67f, 0.55f, 0.4f, 1f),    // currency
    ];

    private readonly StashInspector _inspector;

    private string _search = string.Empty;
    private int _page = -1;
    private readonly HashSet<ulong> _open = [];

    public StashWindow(StashInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>Whether the window is on screen.</summary>
    public bool Visible { get; set; }

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Visible)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(860f, 600f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(160f, 120f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("Stash", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End in a finally: an exception between Begin and End leaves ImGui's stack unbalanced,
        // and the assert that follows takes the process down.
        try
        {
            if (expanded)
            {
                Draw();
            }
        }
        finally
        {
            ImGui.End();
        }

        Visible = open;
    }

    private void Draw()
    {
        StashView view = _inspector.View;

        if (ImGui.Button("read the stash"))
        {
            _inspector.ReadAgain();
        }

        ImGui.SameLine();

        if (_inspector.Reading)
        {
            // Said, because a full read of a big stash takes long enough to look like nothing
            // happening - and pressing the button again would only queue another one.
            ImGui.TextColored(WarnText, "reading every tab - this takes a moment");
        }
        else if (view.Pages.Count > 0)
        {
            ImGui.TextColored(GoodText, $"{view.Items} items in {view.Pages.Count} inventories");
        }
        else
        {
            ImGui.TextColored(DimText, "nothing read yet");
        }

        if (view.Status.Length > 0)
        {
            ImGui.TextColored(DimText, view.Status);
        }

        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("##search", "name, mod or stat...", ref _search, 128);
        ImGui.SameLine();
        ImGui.TextColored(DimText, "searches the stats too");

        ImGui.Separator();

        if (view.Pages.Count == 0)
        {
            return;
        }

        Tabs(view);
        ImGui.SameLine();
        Items(view);
    }

    /// <summary>The tabs down the left, with how much is in each.</summary>
    private void Tabs(StashView view)
    {
        if (!ImGui.BeginChild("stash-tabs", new Vector2(220f, 0f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            // "Everything" first, because searching across the whole stash is the thing this
            // does that the game cannot.
            if (ImGui.Selectable($"Everything ({view.Items})", _page < 0))
            {
                _page = -1;
            }

            for (int i = 0; i < view.Pages.Count; i++)
            {
                StashPage page = view.Pages[i];
                if (ImGui.Selectable($"{page.Called} ({page.Items.Count})###page{i}", _page == i))
                {
                    _page = i;
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>The items, each opening to its full stats.</summary>
    private void Items(StashView view)
    {
        if (!ImGui.BeginChild("stash-items", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            int shown = 0;
            int matched = 0;

            for (int i = 0; i < view.Pages.Count; i++)
            {
                if (_page >= 0 && _page != i)
                {
                    continue;
                }

                StashPage page = view.Pages[i];
                foreach (InspectedItem item in page.Items)
                {
                    if (!Matches(item))
                    {
                        continue;
                    }

                    matched++;
                    if (shown >= MostRows)
                    {
                        continue;
                    }

                    shown++;
                    One(item, _page < 0 ? page.Called : string.Empty);
                }
            }

            if (matched == 0)
            {
                // "Empty" is not the whole truth: a tab that has never been opened in game
                // holds nothing here either, and the two are indistinguishable from this side.
                ImGui.TextColored(DimText, _search.Trim().Length > 0
                    ? "nothing matches"
                    : "empty - or not opened in game yet, which looks the same from here");
            }
            else if (matched > shown)
            {
                // Said rather than silently truncated: a list that stops without saying so
                // reads as "that is all of them".
                ImGui.TextColored(DimText, $"...and {matched - shown} more, not listed");
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>One item: a line, and its stats when it is opened.</summary>
    private void One(InspectedItem item, string where)
    {
        ImGui.PushID((int)(item.Entity & 0x7FFF_FFFF));

        try
        {
            bool open = _open.Contains(item.Entity);
            string arrow = open ? "v" : ">";

            Vector4 colour = item.Rarity >= 0 && item.Rarity < ByRarity.Length
                ? ByRarity[item.Rarity]
                : ByRarity[0];

            string stack = item.Stack > 1 ? $" x{item.Stack}" : string.Empty;

            // The selectable shows only the arrow, and the name is drawn beside it - because a
            // selectable takes one colour and an item's name has to be its rarity's. Written
            // into the label as well it would be drawn twice, one over the other.
            if (ImGui.Selectable(arrow, open, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (!_open.Remove(item.Entity))
                {
                    _open.Add(item.Entity);
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(colour, $"{item.Called}{stack}");

            if (where.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(DimText, $"- {where}");
            }

            if (item.Identified == false)
            {
                ImGui.SameLine();
                ImGui.TextColored(WarnText, "unidentified");
            }

            if (open)
            {
                Detail(item);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>Everything the item says about itself.</summary>
    private static void Detail(InspectedItem item)
    {
        ImGui.Indent();

        try
        {
            ImGui.TextColored(DimText, item.Path);

            if (item.RarityName.Length > 0)
            {
                ImGui.TextColored(DimText, item.StackMax > 0
                    ? $"{item.RarityName}  -  stacks to {item.StackMax}"
                    : item.RarityName);
            }

            foreach (ItemMod mod in item.Mods)
            {
                string rolls = mod.Rolls.Count > 0 ? $"  [{string.Join(", ", mod.Rolls)}]" : string.Empty;
                string called = mod.Name.Length > 0 ? $"{mod.Name} " : string.Empty;

                // The id as well as the name, always. The name is what the game shows and the
                // id is what anything else has to match on - and for a mod nobody wrote down,
                // the id is all there is.
                ImGui.TextColored(DimText, $"{mod.Slot}: {called}({mod.Id}){rolls}");
            }

            if (item.Stats.Count > 0)
            {
                ImGui.Separator();
            }

            foreach (ItemStat stat in item.Stats)
            {
                ImGui.TextUnformatted(stat.Said);
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>Whether an item is what somebody is looking for.</summary>
    /// <remarks>
    /// Searches the STATS as well as the name, which is the point: "what have I got with
    /// movement speed on it" is the question a stash cannot answer for itself.
    /// </remarks>
    private bool Matches(InspectedItem item)
    {
        string looking = _search.Trim();
        if (looking.Length == 0)
        {
            return true;
        }

        if (item.Called.Contains(looking, StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains(looking, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (ItemStat stat in item.Stats)
        {
            if (stat.Said.Contains(looking, StringComparison.OrdinalIgnoreCase)
                || stat.Id.Contains(looking, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (ItemMod mod in item.Mods)
        {
            if (mod.Id.Contains(looking, StringComparison.OrdinalIgnoreCase)
                || mod.Name.Contains(looking, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
