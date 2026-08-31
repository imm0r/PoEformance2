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

    /// <summary>
    /// The page, in the order somebody actually asks its questions.
    /// </summary>
    /// <remarks>
    /// THIS PAGE WAS THE WORST OF THE TOOL'S LAYOUT AND IT IS WORTH SAYING WHY, because every
    /// mistake here was made one reasonable line at a time. Eleven switches ran down it in the
    /// order they were built, with no two explained the same way: some had their aside welded on
    /// with <c>SameLine</c>, some had it on the next line, some had it INDENTED WITH FOUR
    /// SPACES INSIDE THE STRING - "    a map is hovered now" - which is an indent that ignores
    /// the text size, cannot be measured, and lands differently in every face. Three of the
    /// switches were joined into one line by <c>SameLine</c> and the rest were not, without the
    /// grouping meaning anything. The master switch sat in the middle of the list, under a
    /// diagnostic button.
    ///
    /// So the page now says what it is doing: the master switch first, the read's own account of
    /// itself beside the button that re-checks it, and then the settings in three named groups -
    /// what gets drawn, what gets hidden, and where to route. The switches did not change and
    /// nor did what any of them does.
    /// </remarks>
    private void Draw()
    {
        AtlasSettings settings = _watch.Settings;
        AtlasView view = _watch.View;
        AtlasSettings changed = settings;

        // FIRST, because it decides whether any of the rest applies. It used to be the fifth
        // control on the page, below a diagnostic.
        bool enabled = settings.Enabled;
        if (OverlayLayout.Master("Draw on the atlas", ref enabled))
        {
            changed = changed with { Enabled = enabled };
        }

        DrawTheRead(view);

        OverlayLayout.Group("What is drawn");

        // THE THREE THAT REALLY DO BELONG ON ONE LINE - the three halves of a map's label - now
        // spaced so they read as three switches rather than as one sentence.
        bool names = settings.Names;
        if (OverlayLayout.Toggle("Map names", ref names))
        {
            changed = changed with { Names = names };
        }

        OverlayLayout.Next();

        bool contents = settings.Contents;
        if (OverlayLayout.Toggle("What is in them", ref contents))
        {
            changed = changed with { Contents = contents };
        }

        OverlayLayout.Next();

        bool web = settings.Web;
        if (OverlayLayout.Toggle("Every connection", ref web))
        {
            changed = changed with { Web = web };
        }

        bool ratings = settings.Ratings;
        if (OverlayLayout.Toggle("Map ratings", ref ratings))
        {
            changed = changed with { Ratings = ratings };
        }

        OverlayLayout.Hint(
            _watch.Ratings.Count > 0
                ? $"{_watch.Ratings.Count} maps rated out of {_watch.Ratings.Best}. Edit"
                  + " data/atlas-ratings.json - it is an opinion rather than a fact."
                : "None - data/atlas-ratings.json is missing or empty.");

        // A name in the file that matches no map is a rating that silently never appears, which
        // is indistinguishable from having forgotten to write it. Named here, it is a typo.
        // A REAL INDENT rather than four spaces in the string: this one follows the text size.
        if (_watch.Ratings.Unmatched.Count > 0)
        {
            Warn(
                $"{_watch.Ratings.Unmatched.Count} rated names match no map: "
                + string.Join(", ", _watch.Ratings.Unmatched.Take(6))
                + (_watch.Ratings.Unmatched.Count > 6 ? " ..." : string.Empty));
        }

        bool biomes = settings.Biomes;
        if (OverlayLayout.Toggle("Biome borders", ref biomes))
        {
            changed = changed with { Biomes = biomes };
        }

        // THE KEY IS GONE. It listed all thirteen biome names, each in the colour it rings a map
        // in, whenever the switch was on - a wrapped block of coloured words in the middle of a
        // settings page, permanently, for a question asked once. The colours are on the atlas
        // where they are used; a legend in the settings is a reference table nobody was reading.
        OverlayLayout.Hint("The ring around each name says what terrain the map is.");

        // Whether the default reads at a real resolution is not something that could be
        // decided while writing it, so it is here to be turned up rather than guessed at.
        float writing = settings.Writing;
        if (OverlayLayout.Slider(
                "Size of the writing", ref writing,
                AtlasSettings.SmallestText, AtlasSettings.LargestText, "%.2fx"))
        {
            changed = changed with { TextScale = writing };
        }

        OverlayLayout.Group("What is hidden");

        bool hideDone = settings.HideCompleted;
        if (OverlayLayout.Toggle("Hide finished maps", ref hideDone))
        {
            changed = changed with { HideCompleted = hideDone };
        }

        OverlayLayout.Next();

        bool hideLocked = settings.HideUnreachable;
        if (OverlayLayout.Toggle("Hide maps with no way there", ref hideLocked))
        {
            changed = changed with { HideUnreachable = hideLocked };
        }

        // Said next to the switch that causes it, because this is the one pair whose effect
        // looks like the feature being broken: routes lead to maps nobody has reached, so
        // hiding those and wondering where the lines went is a short trip. On its own line
        // rather than in a tooltip for that same reason - it is a warning, not an aside.
        OverlayLayout.Note("Maps you are routing to stay visible either way.");

        bool hideOnHover = settings.HideOnHover;
        if (OverlayLayout.Toggle("Hide everything over an unmeasurable hover panel", ref hideOnHover))
        {
            changed = changed with { HideOnHover = hideOnHover };
        }

        // The setting no longer describes what usually happens, so the line under it does. The
        // game's panel about a hovered map is measured like the rest of the interface and the
        // drawing goes round it; this is only the fallback for the frame where it is not found,
        // and leaving it looking like the main behaviour would have somebody switch it off to
        // stop a blanking that is not happening.
        OverlayLayout.Note(
            "The panel over a hovered map is normally kept off like any other part of the"
            + " interface. This is only what happens when it cannot be found."
            // Said out loud, because "the overlay vanished" is what this looks like from the
            // outside - and if a node's rectangle ever reads too big it would be permanent.
            + (view.Hovering ? "  A map is hovered now." : string.Empty));

        Apply(settings, changed);
    }

    /// <summary>
    /// The routing groups, as a section of their own.
    /// </summary>
    /// <remarks>
    /// ITS OWN SECTION rather than a third group at the bottom of the settings, because it is
    /// not a setting: it is a LIST, one row per group, that grows as somebody adds groups and
    /// that is scrolled and edited rather than glanced at and left. Sitting under the switches
    /// it made the page twice as long as anything on it, and pushed "what is drawn" - which is
    /// checked far more often - above the fold on a short window.
    ///
    /// Registered as a second tool on the atlas page, so it gets a fold of its own beside the
    /// ritual line - see where the pages are wired up.
    /// </remarks>
    public void DrawRouting()
    {
        AtlasSettings settings = _watch.Settings;
        AtlasSettings changed = settings;

        // The filter above the groups, because it decides what any of them can match. Its name
        // is inside the box now - it had a label to its right and a width nobody chose twice.
        string search = settings.Search;
        if (OverlayLayout.Search("##atlas-search", "only maps called...", ref search, 64))
        {
            changed = changed with { Search = search };
        }

        changed = Groups(changed);

        Apply(settings, changed);
    }

    /// <summary>Takes a changed settings record into use, and queues the save.</summary>
    /// <remarks>
    /// Shared by the two sections, which both edit the same record: whichever one is folded
    /// open has to apply and settle on its own, since the other may not draw at all.
    /// </remarks>
    private void Apply(AtlasSettings settings, AtlasSettings changed)
    {
        if (!ReferenceEquals(changed, settings))
        {
            // Applied at once, so the atlas follows a colour as it is dragged.
            _watch.Settings = changed;
            _unsaved = true;
        }

        Settle();
    }

    /// <summary>
    /// What the read made of the atlas, and the button that asks it again.
    /// </summary>
    /// <remarks>
    /// TOGETHER, because they are one thought: the account says whether anything was read and
    /// the button is what you press when it says nothing was. They used to be a button with a
    /// caption, above a status line, above the master switch - three ideas in the space of two,
    /// with the least important one at the top.
    ///
    /// EVERY ATLAS OFFSET IS UNCONFIRMED - they were ported from the reference with the game
    /// unavailable - which is why the check stays this prominent rather than going to the
    /// bottom with the other diagnostics.
    /// </remarks>
    private void DrawTheRead(AtlasView view)
    {
        Account(view);

        ImGui.SameLine();

        // Right-aligned, so it sits out of the account's way however long that runs and does not
        // move as the counts change. Measured from what is LEFT on the line - after a SameLine
        // that is the distance from the cursor to the window's inner right edge.
        float button = ImGui.CalcTextSize("Check the read").X + (ImGui.GetStyle().FramePadding.X * 2f);
        float room = ImGui.GetContentRegionAvail().X;
        if (room > button)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - button);
        }

        if (ImGui.Button("Check the read"))
        {
            _watch.CheckTheRead();
        }

        OverlayLayout.Hint("Open the atlas first - it reads nothing while the panel is shut.");

        foreach (string line in _watch.Checked)
        {
            OverlayLayout.Note(line);
        }
    }

    /// <summary>A warning about the settings, stepped in under what it is about.</summary>
    /// <remarks>
    /// The indent is <see cref="OverlayLayout.Step"/> rather than four spaces in the string,
    /// which is what this page used to do in three places. A space is a glyph: it is a different
    /// width in every face, it does not follow the text size, and it cannot line up with an
    /// indent made any other way.
    /// </remarks>
    private static void Warn(string text)
    {
        float step = OverlayLayout.Step();
        ImGui.Indent(step);
        try
        {
            ImGuiText.Wrapped(WarnText, ImGuiText.Escape(text));
        }
        finally
        {
            ImGui.Unindent(step);
        }
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
        OverlayLayout.Note("A line is drawn across the atlas to the nearest map of every ticked group.");

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
                        // AT THE COLUMN, not after the name. Eleven groups have eleven names of
                        // eleven lengths - "Quest" to "Where a route starts" - so hanging the
                        // number box off the end of each put eleven identical controls at eleven
                        // different x-positions, which is the ragged edge this whole pass is
                        // about. See OverlayLayout.ToColumn.
                        OverlayLayout.ToColumn();

                        int hops = group.MaxHops;
                        if (OverlayLayout.Narrow.Number("maps away", ref hops, 1))
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
                    // else to find it. A measured indent rather than four spaces in the string.
                    OverlayLayout.Note(Rule(group));
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
