using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// What effects are out there, and the switches that make them visible at all.
/// </summary>
/// <remarks>
/// A DEBUGGING TOOL and not a playing one, which is why everything here is off by default and
/// says what it costs. The three things it can show are thrown away at three different points
/// of the read, so turning them on means undoing three separate decisions - and each of those
/// decisions is right for playing.
///
/// The list is the half that matters. A ring on the screen says something is there; the path
/// says WHAT, and an unexplained death or an unnamed mechanic is a question about what.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EffectWindow
{
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 WarnText = new(1f, 0.7f, 0.35f, 1f);
    private static readonly Vector4 HostileText = new(1f, 0.55f, 0.4f, 1f);
    private static readonly Vector4 FriendlyText = new(0.5f, 0.85f, 1f, 1f);

    private readonly EffectLayer _layer;
    private readonly Func<bool> _keepingEffects;
    private readonly Action<bool> _keepEffects;
    private readonly NoiseFilter? _noise;

    private string _filter = string.Empty;

    /// <param name="keepEffects">
    /// Turns the reader's own dropping of hostile effects off and on. A pair of callbacks
    /// rather than the reader itself: the window has no business with anything else on it, and
    /// the reader lives a layer below where this can reach.
    /// </param>
    public EffectWindow(
        EffectLayer layer, Func<bool> keepingEffects, Action<bool> keepEffects, NoiseFilter? noise)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(keepingEffects);
        ArgumentNullException.ThrowIfNull(keepEffects);
        _layer = layer;
        _keepingEffects = keepingEffects;
        _keepEffects = keepEffects;
        _noise = noise;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ImGui.TextColored(
            DimText,
            "For looking, not for playing. Everything here is normally thrown away on purpose,");
        ImGui.TextColored(DimText, "and every switch below undoes one of those decisions.");
        ImGui.Separator();

        bool drawing = _layer.Enabled;
        if (ImGui.Checkbox("draw them in the world", ref drawing))
        {
            _layer.Enabled = drawing;
        }

        ImGui.SameLine();
        bool paths = _layer.ShowPaths;
        if (ImGui.Checkbox("with their paths", ref paths))
        {
            _layer.ShowPaths = paths;
        }

        bool keeping = _keepingEffects();
        if (ImGui.Checkbox("keep the hostile ground effects  (the reader drops them)", ref keeping))
        {
            _keepEffects(keeping);
        }

        // Said next to the switch, because the reason it is off is the reason a screen full of
        // enemy markers once looked like a broken overlay rather than a working Firewall.
        ImGui.TextColored(
            DimText,
            "    a flame wall carries Life and a position, so kept as monsters they were drawn"
            + " as enemies and given health bars.");

        if (_noise is NoiseFilter noise)
        {
            bool engine = !noise.IsOn(NoiseKind.Engine) || !noise.Enabled;
            if (ImGui.Checkbox("let the engine's own nodes through  (/fx/, /mat/, /epk/ ...)", ref engine))
            {
                noise.Set(NoiseKind.Engine, !engine);
            }

            if (engine)
            {
                ImGui.TextColored(
                    WarnText,
                    "    these are the most numerous entities in the game and each costs a"
                    + " component read - expect the read cost to climb.");
            }
        }

        ImGui.Separator();

        // Said out loud, because "particles" invites an expectation nothing in memory can
        // meet, and somebody looking for a spark that is not there would conclude the read is
        // broken rather than that the thing was never listed.
        ImGui.TextColored(
            DimText,
            "The game's actual particles are rendering and are not entities - nothing lists a spark.");
        ImGui.TextColored(
            DimText,
            "A screen of fire is ONE entity here, and that entity is what gets a mark.");

        ImGui.Separator();
        DrawSorts(snapshot);
    }

    /// <summary>What is out there right now, commonest first.</summary>
    private void DrawSorts(WorldSnapshot snapshot)
    {
        IReadOnlyList<EffectSort> sorts = EffectCensus.Sorts(snapshot);

        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##effect-filter", "filter by path", ref _filter, 128);

        int shown = 0;
        int all = 0;
        foreach (EffectSort sort in sorts)
        {
            all += sort.Count;
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"{all} in {sorts.Count} sorts");

        if (sorts.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                keepingNothing()
                    ? "nothing kept - turn a switch above on"
                    : "none nearby right now");
            return;
        }

        if (!ImGui.BeginTable("##effect-table", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("how many");
            ImGui.TableSetupColumn("path");
            ImGui.TableHeadersRow();

            foreach (EffectSort sort in sorts)
            {
                if (_filter.Length > 0
                    && !sort.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(sort.Friendly ? FriendlyText : HostileText, sort.Count.ToString());

                ImGui.TableNextColumn();

                // The WHOLE path here, unlike the marks in the world: this is the readout
                // somebody copies an id out of, and a shortened one cannot be searched for.
                ImGui.TextUnformatted(ImGuiText.Escape(sort.Path));

                if (++shown >= 200)
                {
                    break;
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        bool keepingNothing() => !_keepingEffects() && (_noise is null || _noise.IsOn(NoiseKind.Engine));
    }
}
