using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The projectile switches, and what is in the air right now.
/// </summary>
/// <remarks>
/// The list is not decoration. Which entity a skill spawns is not written down anywhere this
/// tool can read, so the only way to learn that your fireball is
/// <c>Metadata/Projectiles/Fireball</c> is to cast one and look - and the same list answers
/// the question the "only mine" switch depends on, which is whether the game marks your own
/// projectiles as being on your side at all. Both counts are printed for that reason.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProjectileWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 MineText = OverlayInk.Friendly;
    private static readonly Vector4 OtherText = OverlayInk.Hostile;

    private readonly ProjectileLayer _layer;
    private readonly ProjectileWatch _watch;
    private readonly Action _changed;

    /// <param name="changed">
    /// Called when a switch moved, so the choice is written down. Every one of these is made
    /// once and expected to hold.
    /// </param>
    public ProjectileWindow(ProjectileLayer layer, ProjectileWatch watch, Action changed)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(changed);
        _layer = layer;
        _watch = watch;
        _changed = changed;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // THE WHOLE ROW FIRST, THEN THE WARNING. It used to be drawn between the first switch
        // and the SameLine of the second - so the two that followed were placed beside the
        // WARNING rather than beside the switch, and ran off the right edge of any ordinary
        // window. They were simply not on screen. A block of prose in the middle of a row is
        // not a formatting slip; it moves the controls after it somewhere nobody can reach.
        bool drawing = _layer.Enabled;
        if (OverlayLayout.Toggle("Draw Them over the Game", ref drawing))
        {
            _layer.Enabled = drawing;
            _changed();
        }

        OverlayLayout.Cell(1);
        bool trails = _layer.ShowTrails;
        if (OverlayLayout.Toggle("With the Line They Came Along", ref trails))
        {
            _layer.ShowTrails = trails;
            _changed();
        }

        OverlayLayout.Cell(2);
        bool paths = _layer.ShowPaths;
        if (OverlayLayout.Toggle("And Their Paths", ref paths))
        {
            _layer.ShowPaths = paths;
            _changed();
        }

        // Under the row that carries it, because it is the one control here with a price. The
        // game files projectiles as visual entities and the reader skips those, so this switch
        // turns the entity walk's own filter off as well - there is no way to see a projectile
        // without paying for that.
        if (drawing)
        {
            OverlayLayout.Warning(
                "Also makes the reader read the game's VISUAL entities, which is where"
                + " projectiles are: a recorded Spark session ran 17 gameplay entities against 51"
                + " visuals. Watch the Read cost tab.");
        }

        bool mine = _layer.MineOnly;
        if (OverlayLayout.Toggle("Only Mine", ref mine))
        {
            _layer.MineOnly = mine;
            _changed();
        }

        // "Mine" rests on one byte, and if that byte is not set on the projectiles a build
        // spawns, this switch is the difference between an overlay that works and one that
        // draws nothing - with no hint as to which. The list below is where somebody checks
        // before flipping it.
        OverlayLayout.Hint(
            "Whose it is comes from one byte - the same one that tells your minions from the"
            + " monsters. Check the list below says \"mine\" for your own skills before relying"
            + " on this.");

        ImGui.Separator();

        // The same warning the effects tab carries, for the same reason: "projectiles" invites
        // an expectation nothing in memory can meet, and somebody looking for the fire itself
        // would conclude the read is broken rather than that it was never listed.
        ImGuiText.Wrapped(
            DimText,
            "The game's own particles are rendering, not entities - nothing lists a spark."
            + " What is marked is the projectile ENTITY the skill spawns, one per projectile.");

        ImGui.Separator();
        DrawSorts(snapshot);
    }

    /// <summary>What is in the air right now, commonest first.</summary>
    private void DrawSorts(WorldSnapshot snapshot)
    {
        IReadOnlyList<ProjectileSort> sorts = ProjectileWatch.Sorts(snapshot);

        int all = 0;
        int mine = 0;
        foreach (ProjectileSort sort in sorts)
        {
            all += sort.Count;
            mine += sort.Mine;
        }

        ImGui.TextColored(DimText, $"{all} in the air, {mine} of them yours; {_watch.Tracking} being followed");

        if (sorts.Count == 0)
        {
            // Three different problems that look identical from here, in the order they are
            // worth checking. The first was the real one for a day: the switch above was off,
            // so the reader never listed a projectile and no amount of casting could change it.
            if (!_layer.Enabled)
            {
                ImGui.TextColored(
                    DimText, "nothing is being read - tick \"draw them over the game\" above.");
                return;
            }

            ImGui.TextColored(DimText, "nothing right now - cast something.");
            ImGuiText.Wrapped(
                DimText,
                "if a skill of yours never appears here, its entity is filed somewhere other than"
                + " Metadata/Projectiles; the Effects tab lists everything else, with paths.");
            return;
        }

        if (!ImGui.BeginTable("##projectile-table", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("how many");
            ImGui.TableSetupColumn("mine");
            ImGui.TableSetupColumn("trail");
            ImGui.TableSetupColumn("path");
            ImGui.TableHeadersRow();

            foreach (ProjectileSort sort in sorts)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiText.Mono(sort.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    sort.Mine > 0 ? MineText : OtherText,
                    sort.Mine > 0 ? $"{sort.Mine}" : "-");

                // What the trail will be coloured as. Printed because it is a GUESS read out
                // of the name - the game's files carry no element for a projectile - and a
                // guess nobody can see is one nobody can correct. "-" means the name said
                // nothing, and the trail keeps the mark's own colour.
                ImGui.TableNextColumn();
                ProjectileElement element = ProjectileElements.Of(sort.Path);
                string key = StyleCatalogue.ForElement(element);
                if (key.Length == 0)
                {
                    ImGui.TextColored(DimText, "-");
                }
                else
                {
                    ImGui.TextColored(
                        // Through the layer rather than a copy of its own: there is one style
                        // and the editor writes to it, so a second reference is one that drifts.
                        ImGui.ColorConvertU32ToFloat4(_layer.Style.Colour(key)),
                        element.ToString().ToLowerInvariant());
                }

                ImGui.TableNextColumn();

                // The WHOLE path here, unlike the marks in the world: this is the readout
                // somebody copies a name out of, and a shortened one cannot be searched for.
                ImGui.TextUnformatted(ImGuiText.Escape(sort.Path));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }
}
