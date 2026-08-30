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
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 WarnText = OverlayInk.Warn;

    private readonly AtlasWatch _watch;
    private readonly Action<AtlasSettings> _save;

    // Something was changed and not yet written down. See where it is written, below.
    private bool _unsaved;

    /// <param name="save">Writes the settings down, so a change survives a restart.</param>
    public AtlasWindow(AtlasWatch watch, Action<AtlasSettings> save)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(save);
        _watch = watch;
        _save = save;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab() => Draw();

    /// <summary>While the tab is not in front: a change made and left behind still lands.</summary>
    public void Idle() => Settle();

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

        bool ratings = settings.Ratings;
        if (ImGui.Checkbox("map ratings", ref ratings))
        {
            changed = changed with { Ratings = ratings };
        }

        ImGui.SameLine();
        ImGuiText.Wrapped(
            DimText,
            _watch.Ratings.Count > 0
                ? $"{_watch.Ratings.Count} maps rated out of {_watch.Ratings.Best}"
                  + "  -  edit data/atlas-ratings.json, it is an opinion rather than a fact"
                : "none - data/atlas-ratings.json is missing or empty");

        // A name in the file that matches no map is a rating that silently never appears, which
        // is indistinguishable from having forgotten to write it. Named here, it is a typo.
        if (_watch.Ratings.Unmatched.Count > 0)
        {
            ImGui.TextColored(
                WarnText,
                ImGuiText.Escape(
                    $"    {_watch.Ratings.Unmatched.Count} rated names match no map: "
                    + string.Join(", ", _watch.Ratings.Unmatched.Take(6))
                    + (_watch.Ratings.Unmatched.Count > 6 ? " ..." : string.Empty)));
        }

        bool biomes = settings.Biomes;
        if (ImGui.Checkbox("biome borders", ref biomes))
        {
            changed = changed with { Biomes = biomes };
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "- the ring around each name says what terrain the map is");

        // The key is drawn IN THE COLOURS, because a list of biome names in grey answers none of
        // what somebody looking at a green ring on the atlas is asking.
        if (biomes)
        {
            BiomeKey();
        }

        bool hideOnHover = settings.HideOnHover;
        if (ImGui.Checkbox("hide everything if a hovered map's panel cannot be measured", ref hideOnHover))
        {
            changed = changed with { HideOnHover = hideOnHover };
        }

        // The setting no longer describes what usually happens, so the line under it does. The
        // game's panel about a hovered map is measured like the rest of the interface and the
        // drawing goes round it; this is only the fallback for the frame where it is not found,
        // and leaving it looking like the main behaviour would have somebody switch it off to
        // stop a blanking that is not happening.
        ImGui.TextColored(
            DimText,
            "    the panel over a hovered map is normally kept off like any other part of the"
            + " interface - this is what happens when it cannot be found");

        // Said out loud, because "the overlay vanished" is what this looks like from the
        // outside - and if a node's rectangle ever reads too big it would be permanent, so
        // the line that names it is the difference between a setting and a mystery.
        if (view.Hovering)
        {
            ImGui.TextColored(DimText, "    a map is hovered now");
        }

        string search = settings.Search;
        if (ImGui.InputText("only maps called", ref search, 64))
        {
            changed = changed with { Search = search };
        }

        // Whether the default reads at a real resolution is not something that could be
        // decided while writing it, so it is here to be turned up rather than guessed at.
        float writing = settings.Writing;
        if (ImGui.SliderFloat(
                "size of the writing", ref writing,
                AtlasSettings.SmallestText, AtlasSettings.LargestText, "%.2fx"))
        {
            changed = changed with { TextScale = writing };
        }

        ImGui.Separator();
        changed = Groups(changed);

        if (!ReferenceEquals(changed, settings))
        {
            // Applied at once, so the atlas follows a colour as it is dragged.
            _watch.Settings = changed;
            _unsaved = true;
        }

        Settle();
    }

    /// <summary>Writes down a change once nothing is being dragged or typed in.</summary>
    /// <remarks>
    /// Applied and saved separately on purpose: a colour picker held down writes the
    /// settings file on every frame of the drag, and a search box writes it on every
    /// keystroke, so the save waits for quiet while the atlas follows the drag live.
    /// </remarks>
    private void Settle()
    {
        if (_unsaved && !ImGui.IsAnyItemActive())
        {
            _unsaved = false;
            _save(_watch.Settings);
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
                        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);

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

    /// <summary>Every biome the game numbers, each written in the colour it rings a map in.</summary>
    /// <remarks>
    /// Wrapped by hand rather than with the text wrapper, because each name is its own coloured
    /// item and ImGui wraps within one - a row of thirteen items has to be broken between them.
    /// </remarks>
    private static void BiomeKey()
    {
        // Taken at the start of a line, where what is left to the right IS the content width.
        float edge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        float gap = ImGui.GetStyle().ItemSpacing.X;

        ImGui.TextColored(DimText, "    ");

        foreach (AtlasBiome biome in AtlasBiomes.All.Values)
        {
            ImGui.SameLine(0f, gap);

            if (ImGui.GetCursorPosX() + ImGui.CalcTextSize(biome.Name).X > edge)
            {
                ImGui.NewLine();
                ImGui.TextColored(DimText, "    ");
                ImGui.SameLine(0f, gap);
            }

            ImGui.TextColored(ToVector(biome.Colour), biome.Name);
        }
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
