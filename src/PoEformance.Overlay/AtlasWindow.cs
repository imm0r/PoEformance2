using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What to draw on the atlas, and which kinds of map to find the way to.
/// </summary>
/// <remarks>
/// The groups are the point of this window rather than the switches at the top. Which maps
/// count as towers, whether a citadel is worth a line across the atlas, how far away is still
/// worth walking to - all of that is somebody's own answer, it changes every league, and it was
/// only editable by hand-writing a JSON file before this existed.
///
/// IT SAYS WHAT IT FOUND. An atlas overlay that draws nothing looks identical whether the panel
/// is shut, the offsets are wrong, or every map has been filtered out by a search somebody
/// forgot about - and those want completely different fixes. So the read's own account of
/// itself is at the top, next to the counts.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AtlasWindow
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);

    private readonly AtlasWatch _watch;
    private readonly Action<AtlasSettings> _save;

    /// <param name="save">Writes the settings down, so a change survives a restart.</param>
    public AtlasWindow(AtlasWatch watch, Action<AtlasSettings> save)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(save);
        _watch = watch;
        _save = save;
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

        ImGui.SetNextWindowSize(new Vector2(560f, 520f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(180f, 160f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("The atlas", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End in a finally: an exception between Begin and End leaves ImGui's stack
        // unbalanced, and the assert that follows takes the process down.
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
        AtlasSettings settings = _watch.Settings;
        AtlasView view = _watch.View;

        Account(view);

        // EVERY ATLAS OFFSET IS UNCONFIRMED - they were ported from the reference with the
        // game unavailable - so this button is not a diagnostic tucked away for later, it is
        // the first thing to press when the atlas is open and nothing is drawn on it.
        if (ImGui.Button("check the read"))
        {
            _watch.CheckTheRead();
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "open the atlas first - it reads nothing while the panel is shut");

        foreach (string line in _watch.Checked)
        {
            ImGui.TextColored(DimText, line);
        }

        ImGui.Separator();

        AtlasSettings changed = settings;

        bool enabled = settings.Enabled;
        if (ImGui.Checkbox("draw on the atlas", ref enabled))
        {
            changed = changed with { Enabled = enabled };
        }

        bool names = settings.Names;
        if (ImGui.Checkbox("map names", ref names))
        {
            changed = changed with { Names = names };
        }

        ImGui.SameLine();

        bool contents = settings.Contents;
        if (ImGui.Checkbox("what is in them", ref contents))
        {
            changed = changed with { Contents = contents };
        }

        ImGui.SameLine();

        bool web = settings.Web;
        if (ImGui.Checkbox("every connection", ref web))
        {
            changed = changed with { Web = web };
        }

        bool hideDone = settings.HideCompleted;
        if (ImGui.Checkbox("hide finished maps", ref hideDone))
        {
            changed = changed with { HideCompleted = hideDone };
        }

        ImGui.SameLine();

        bool hideLocked = settings.HideUnreachable;
        if (ImGui.Checkbox("hide maps with no way there", ref hideLocked))
        {
            changed = changed with { HideUnreachable = hideLocked };
        }

        // Said next to the switch that causes it, because this is the one pair whose effect
        // looks like the feature being broken: routes lead to maps nobody has reached, so
        // hiding those and wondering where the lines went is a short trip.
        ImGui.TextColored(DimText, "maps you are routing to stay visible either way");

        string search = settings.Search;
        if (ImGui.InputText("only maps called", ref search, 64))
        {
            changed = changed with { Search = search };
        }

        ImGui.Separator();
        changed = Groups(changed);

        if (!ReferenceEquals(changed, settings))
        {
            _watch.Settings = changed;
            _save(changed);
        }
    }

    /// <summary>What the read made of the atlas, in the words it used.</summary>
    private static void Account(AtlasView view)
    {
        if (view.Status.Length > 0)
        {
            // "atlas closed" is the ordinary state and not a fault, so it is not coloured
            // like one. Anything else here is something that went wrong.
            bool ordinary = view.Status is "atlas closed" or "not in an area" or "atlas overlay off";
            ImGui.TextColored(ordinary ? DimText : WarnText, view.Status);
            return;
        }

        ImGui.TextColored(GoodText, $"{view.Total} maps on the atlas");
        ImGui.SameLine();
        ImGui.TextColored(DimText, $"- {view.Open} you can enter now, {view.Reachable} reachable in all");

        if (view.Marks.Count < view.Total)
        {
            ImGui.TextColored(DimText, $"{view.Marks.Count} of them drawn - the rest are hidden or searched out");
        }
    }

    /// <summary>The groups, each with its colour and whether to draw the way there.</summary>
    /// <remarks>
    /// The hop limit only appears once routing is ON for that group. A number that does
    /// nothing is a number somebody will set and then wonder about.
    /// </remarks>
    private static AtlasSettings Groups(AtlasSettings settings)
    {
        ImGui.TextColored(DimText, "find the way to");

        if (!ImGui.BeginChild("atlas-groups", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return settings;
        }

        try
        {
            IReadOnlyList<AtlasGroup> groups = settings.Sorting;
            List<AtlasGroup>? edited = null;

            for (int i = 0; i < groups.Count; i++)
            {
                AtlasGroup group = groups[i];

                // Changes accumulate into ONE copy of the group rather than each writing its
                // own into the list. Two of these can happen in a frame - a colour picked
                // while a checkbox is toggled - and written separately the second would be
                // built from the unedited original and quietly undo the first.
                AtlasGroup after = group;
                ImGui.PushID(i);

                try
                {
                    // The colour as a swatch rather than as its hex. It is what the group
                    // looks like on the atlas, and reading "#DB00E0" tells nobody that.
                    Vector4 colour = ToVector(OverlaySettings.ParseColour(group.Colour));
                    if (ImGui.ColorEdit4(
                            "##colour", ref colour,
                            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaPreview))
                    {
                        after = after with { Colour = OverlaySettings.FormatColour(FromVector(colour)) };
                    }

                    ImGui.SameLine();

                    bool route = group.Route;
                    if (ImGui.Checkbox(group.Name, ref route))
                    {
                        after = after with { Route = route };
                    }

                    if (group.Route)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(90f);

                        int hops = group.MaxHops;
                        if (ImGui.InputInt("maps away", ref hops, 1))
                        {
                            after = after with { MaxHops = Math.Max(0, hops) };
                        }

                        if (group.MaxHops <= 0)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(DimText, "(any distance)");
                        }
                    }

                    if (after != group)
                    {
                        (edited ??= [.. groups])[i] = after;
                    }

                    // What the group is actually matched on, small and underneath. It is the
                    // answer to "why is that map not in this group", and there is nowhere
                    // else to find it.
                    ImGui.TextColored(DimText, "    " + Rule(group));
                }
                finally
                {
                    ImGui.PopID();
                }
            }

            return edited is null ? settings : settings with { Groups = edited };
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>How a group decides what belongs to it, in words.</summary>
    private static string Rule(AtlasGroup group)
    {
        if (group.SaysNothing)
        {
            return "matches nothing - it names no maps and no tag";
        }

        var said = new List<string>();
        if (group.Tag.Length > 0)
        {
            said.Add($"tagged \"{group.Tag}\"");
        }

        if (group.Maps is { Count: > 0 })
        {
            said.Add(group.Maps.Count == 1 ? "1 named map" : $"{group.Maps.Count} named maps");
        }

        if (group.Unique)
        {
            said.Add("every unique map");
        }

        return string.Join(", ", said);
    }

    /// <summary>An ImGui-packed colour as the four floats a picker wants.</summary>
    private static Vector4 ToVector(uint packed)
        => new(
            (packed & 0xFF) / 255f,
            ((packed >> 8) & 0xFF) / 255f,
            ((packed >> 16) & 0xFF) / 255f,
            ((packed >> 24) & 0xFF) / 255f);

    /// <summary>And back again.</summary>
    private static uint FromVector(Vector4 colour)
        => ((uint)(Math.Clamp(colour.W, 0f, 1f) * 255f) << 24)
           | ((uint)(Math.Clamp(colour.Z, 0f, 1f) * 255f) << 16)
           | ((uint)(Math.Clamp(colour.Y, 0f, 1f) * 255f) << 8)
           | (uint)(Math.Clamp(colour.X, 0f, 1f) * 255f);
}
