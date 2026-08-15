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
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 MineText = new(0.4f, 0.88f, 1f, 1f);
    private static readonly Vector4 OtherText = new(1f, 0.55f, 0.4f, 1f);

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

        bool drawing = _layer.Enabled;
        if (ImGui.Checkbox("draw them over the game", ref drawing))
        {
            _layer.Enabled = drawing;
            _changed();
        }

        ImGui.SameLine();
        bool trails = _layer.ShowTrails;
        if (ImGui.Checkbox("with the line they came along", ref trails))
        {
            _layer.ShowTrails = trails;
            _changed();
        }

        ImGui.SameLine();
        bool paths = _layer.ShowPaths;
        if (ImGui.Checkbox("and their paths", ref paths))
        {
            _layer.ShowPaths = paths;
            _changed();
        }

        bool mine = _layer.MineOnly;
        if (ImGui.Checkbox("only mine", ref mine))
        {
            _layer.MineOnly = mine;
            _changed();
        }

        // Said next to the switch rather than left to be discovered. "Mine" rests on one byte,
        // and if that byte is not set on the projectiles a build spawns, this switch is the
        // difference between an overlay that works and one that draws nothing - with no hint
        // as to which. The list below is where somebody checks before flipping it.
        ImGui.TextColored(
            DimText,
            "    whose it is comes from one byte - the same one that tells your minions from the"
            + " monsters.");
        ImGui.TextColored(
            DimText,
            "    check the list below says \"mine\" for your own skills before relying on this.");

        ImGui.Separator();

        // The same warning the effects tab carries, for the same reason: "projectiles" invites
        // an expectation nothing in memory can meet, and somebody looking for the fire itself
        // would conclude the read is broken rather than that it was never listed.
        ImGui.TextColored(
            DimText,
            "The game's own particles are rendering, not entities - nothing lists a spark.");
        ImGui.TextColored(
            DimText,
            "What is marked is the projectile ENTITY the skill spawns, one per projectile.");

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
            // Both halves said, because they want different next steps and look identical from
            // here: nothing being listed and nothing being CLASSIFIED as a projectile are not
            // the same problem, and the second one is answered on the effects tab, where every
            // entity nothing else recognises still turns up with its path.
            ImGui.TextColored(DimText, "nothing right now - cast something.");
            ImGui.TextColored(
                DimText,
                "if a skill of yours never appears here, its entity is filed somewhere other than"
                + " Metadata/Projectiles;");
            ImGui.TextColored(DimText, "the Effects tab lists everything else, with paths.");
            return;
        }

        if (!ImGui.BeginTable("##projectile-table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("how many");
            ImGui.TableSetupColumn("mine");
            ImGui.TableSetupColumn("path");
            ImGui.TableHeadersRow();

            foreach (ProjectileSort sort in sorts)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sort.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    sort.Mine > 0 ? MineText : OtherText,
                    sort.Mine > 0 ? $"{sort.Mine}" : "-");

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
