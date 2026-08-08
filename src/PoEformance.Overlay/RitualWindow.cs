using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Every way the ritual line could be drawn, and what each one would pay.
/// </summary>
/// <remarks>
/// The list is the feature. A ritual line is five maps out of a few hundred, each of which rolls
/// a reward that can be worked out in advance - so the question is not "what did I get" but
/// "which of these hundreds of routes is worth taking", and that is a question about a LIST.
///
/// Ticking a route draws it on the atlas in its own colour, which is how it stops being a row of
/// text and becomes somewhere to go. The colours are the join between the two, so this owns them
/// and the layer is told which is which.
///
/// The weights are why anybody's answer differs from anybody else's: what a route is worth is
/// what its rewards are worth TO YOU, and a currency you cannot use is not a good route.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RitualWindow
{
    /// <summary>How many routes are listed at once. The rest are counted, not drawn.</summary>
    public const int MostRows = 200;

    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);

    private readonly RitualWatch _watch;
    private readonly Func<IReadOnlyDictionary<string, int>> _worth;
    private readonly Action<IReadOnlyDictionary<string, int>> _apply;
    private readonly Action<IReadOnlyDictionary<string, int>> _save;

    private string _filter = string.Empty;
    private bool _unsaved;

    /// <param name="worth">What each reward is worth now.</param>
    /// <param name="apply">
    /// Takes a changed weighting into use at once, so the routes re-sort while somebody types.
    /// </param>
    /// <param name="save">
    /// Writes it down. SEPARATE from applying, because a weight is typed a digit at a time and
    /// one callback doing both writes the settings file for each of them.
    /// </param>
    public RitualWindow(
        RitualWatch watch,
        Func<IReadOnlyDictionary<string, int>> worth,
        Action<IReadOnlyDictionary<string, int>> apply,
        Action<IReadOnlyDictionary<string, int>> save)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(worth);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(save);
        _watch = watch;
        _worth = worth;
        _apply = apply;
        _save = save;
    }

    /// <summary>Whether the window is on screen.</summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Which routes are drawn, by key, and in which colour slot.
    /// </summary>
    /// <remarks>
    /// Public because the LAYER draws from exactly this - one dictionary rather than one each,
    /// so the row and the line on the atlas can never disagree about a colour.
    /// </remarks>
    public Dictionary<string, int> Picked { get; } = [];

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Visible)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(760f, 520f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(220f, 200f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("Ritual line", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

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
        RitualView view = _watch.View;

        if (!view.Drawing)
        {
            ImGui.TextColored(DimText, "no ritual line is being drawn - open the atlas and start one");
            ImGui.TextColored(DimText, $"{_watch.KnownRewards} rewards known");
            return;
        }

        if (view.Status.Length > 0)
        {
            ImGui.TextColored(WarnText, view.Status);
        }

        ImGui.TextColored(GoodText, $"{view.Chains.Count} routes");
        ImGui.SameLine();
        ImGui.TextColored(DimText, view.Started
            ? $"- from where the line has reached, {view.Length} maps long"
            : $"- from every map you can enter now, {view.Length} maps long");

        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##filter", "only routes paying...", ref _filter, 64);
        ImGui.SameLine();

        if (ImGui.SmallButton("clear picks"))
        {
            Picked.Clear();
        }

        ImGui.Separator();
        Routes(view);
        ImGui.Separator();
        Weights();
    }

    /// <summary>The routes, best first, with a tick to draw one on the atlas.</summary>
    private void Routes(RitualView view)
    {
        // Sized so the weights below always have room: a list that takes the whole window pushes
        // the thing that ORDERS it off the bottom, and then nobody finds it.
        if (!ImGui.BeginChild("ritual-routes", new Vector2(0f, -220f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            int shown = 0;
            int matched = 0;

            foreach (RitualChain chain in view.Chains)
            {
                if (!Matches(chain))
                {
                    continue;
                }

                matched++;
                if (shown >= MostRows)
                {
                    continue;
                }

                shown++;
                ImGui.PushID(chain.Key);

                try
                {
                    bool drawn = Picked.ContainsKey(chain.Key);
                    if (ImGui.Checkbox("##pick", ref drawn))
                    {
                        if (drawn)
                        {
                            // The first free colour, so two routes ticked together never share
                            // one - which would make both of them unreadable at once.
                            Picked[chain.Key] = FreeSlot();
                        }
                        else
                        {
                            Picked.Remove(chain.Key);
                        }
                    }

                    ImGui.SameLine();

                    // In its own colour when it is drawn, so the row and the line match.
                    Vector4 colour = Picked.TryGetValue(chain.Key, out int slot)
                        ? Unpack(RitualLayer.ColourFor(slot))
                        : new Vector4(0.82f, 0.85f, 0.9f, 1f);

                    if (chain.Worth != 0)
                    {
                        ImGui.TextColored(colour, $"[{chain.Worth,4}]");
                        ImGui.SameLine();
                    }

                    ImGui.TextColored(colour, string.Join("   -   ", chain.Rewards));
                }
                finally
                {
                    ImGui.PopID();
                }
            }

            if (matched == 0)
            {
                ImGui.TextColored(DimText, view.Chains.Count == 0
                    ? "nothing to plan"
                    : "no route pays anything matching that");
            }
            else if (matched > shown)
            {
                // Said rather than silently truncated: a list that stops at two hundred without
                // saying so reads as "that is all of them".
                ImGui.TextColored(DimText, $"...and {matched - shown} more, not listed");
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>What each reward is worth, which is what orders the list.</summary>
    private void Weights()
    {
        ImGui.TextColored(DimText, "what a reward is worth to you - routes are sorted by the sum over their own");

        if (!ImGui.BeginChild("ritual-weights", new Vector2(0f, 0f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            IReadOnlyDictionary<string, int> worth = _worth();
            Dictionary<string, int>? changed = null;

            foreach (string reward in _watch.Rewards)
            {
                worth.TryGetValue(reward, out int current);

                ImGui.PushID(reward);
                try
                {
                    ImGui.SetNextItemWidth(120f);
                    int edited = current;
                    if (ImGui.InputInt("##weight", ref edited))
                    {
                        changed ??= new Dictionary<string, int>(worth);
                        if (edited == 0)
                        {
                            changed.Remove(reward);
                        }
                        else
                        {
                            changed[reward] = edited;
                        }
                    }
                }
                finally
                {
                    ImGui.PopID();
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(reward);
            }

            if (changed is not null)
            {
                // Applied at once, so the list re-sorts as a weight is typed.
                _apply(changed);
                _unsaved = true;
            }

            // Written once nothing is being typed in any more. A weight is entered a digit at a
            // time, so applying and saving together writes the settings file for each digit.
            if (_unsaved && !ImGui.IsAnyItemActive())
            {
                _unsaved = false;
                _save(_worth());
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>Whether a route pays something the filter is asking about.</summary>
    private bool Matches(RitualChain chain)
    {
        if (_filter.Trim().Length == 0)
        {
            return true;
        }

        foreach (string reward in chain.Rewards)
        {
            if (reward.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The lowest colour slot nothing is using, so two drawn routes never share one.</summary>
    private int FreeSlot()
    {
        for (int slot = 0; slot < RitualLayer.Palette.Count; slot++)
        {
            if (!Picked.ContainsValue(slot))
            {
                return slot;
            }
        }

        // Every colour is in use. Reusing one is worse than refusing to draw, but only just -
        // so the oldest is taken back rather than the pick being silently dropped.
        return Picked.Count % RitualLayer.Palette.Count;
    }

    /// <summary>An ImGui-packed colour as the four floats a text call wants.</summary>
    private static Vector4 Unpack(uint packed)
        => new(
            (packed & 0xFF) / 255f,
            ((packed >> 8) & 0xFF) / 255f,
            ((packed >> 16) & 0xFF) / 255f,
            ((packed >> 24) & 0xFF) / 255f);
}
