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
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 HostileText = OverlayInk.Hostile;
    private static readonly Vector4 FriendlyText = OverlayInk.Friendly;

    private readonly EffectLayer _layer;
    private readonly NoiseFilter? _noise;
    private readonly Action _changed;

    private string _filter = string.Empty;

    /// <param name="layer">
    /// Holds all three of its own switches, including the one that is not about drawing at all:
    /// the reader's dropping of hostile effects. It lives there so it can be SAVED - the window
    /// is attached long after the settings are applied, and a switch owned here would start from
    /// nothing on every launch. <c>EntityOverlay</c> pushes it at the reader every frame.
    /// </param>
    /// <param name="changed">
    /// Called when a switch moved, so the choice is WRITTEN DOWN. Somewhere to keep the value
    /// and something to notice it moved are two separate things, and having only the first is
    /// indistinguishable from having neither: the settings file is written when this fires and
    /// at no other time, so a switch that does not call it is saved only by whatever unrelated
    /// change happens to fire next. That is what "still not saved" was, after the fields, the
    /// Apply and the round-trip test were all in place and all correct.
    /// </param>
    public EffectWindow(EffectLayer layer, NoiseFilter? noise, Action changed)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(changed);
        _layer = layer;
        _noise = noise;
        _changed = changed;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // One wrapped sentence rather than two lines broken by hand: the hand break assumed
        // a window width, and at any other width it read as two half-lines.
        ImGuiText.Wrapped(
            DimText,
            "For looking, not for playing. Everything here is normally thrown away on purpose,"
            + " and every switch below undoes one of those decisions.");
        ImGui.Separator();

        bool drawing = _layer.Enabled;
        if (OverlayLayout.Toggle("Draw them in the world", ref drawing))
        {
            _layer.Enabled = drawing;
            _changed();
        }

        OverlayLayout.Cell(1);
        bool paths = _layer.ShowPaths;
        if (OverlayLayout.Toggle("With their paths", ref paths))
        {
            _layer.ShowPaths = paths;
            _changed();
        }

        bool keeping = _layer.KeepHostile;
        if (OverlayLayout.Toggle("Keep the hostile ground effects", ref keeping))
        {
            _layer.KeepHostile = keeping;
            _changed();
        }

        // Said next to the switch, because the reason it is off is the reason a screen full of
        // enemy markers once looked like a broken overlay rather than a working Firewall.
        OverlayLayout.Hint(
            "A flame wall carries Life and a position, so kept as monsters they were drawn as"
            + " enemies and given health bars.");

        if (_noise is NoiseFilter noise)
        {
            bool engine = !noise.IsOn(NoiseKind.Engine) || !noise.Enabled;
            if (OverlayLayout.Toggle("Let the engine's own nodes through", ref engine))
            {
                noise.Set(NoiseKind.Engine, !engine);
                _changed();
            }

            if (engine)
            {
                OverlayLayout.Warning(
                    "These are the most numerous entities in the game and each costs a component"
                    + " read - expect the read cost to climb.");
            }
        }

        ImGui.Separator();

        // Said out loud, because "particles" invites an expectation nothing in memory can
        // meet, and somebody looking for a spark that is not there would conclude the read is
        // broken rather than that the thing was never listed.
        ImGuiText.Wrapped(
            DimText,
            "The game's actual particles are rendering and are not entities - nothing lists a"
            + " spark. A screen of fire is ONE entity here, and that entity is what gets a mark.");

        ImGui.Separator();
        DrawSorts(snapshot);
    }

    /// <summary>What is out there right now, commonest first.</summary>
    private void DrawSorts(WorldSnapshot snapshot)
    {
        IReadOnlyList<EffectSort> sorts = EffectCensus.Sorts(snapshot);

        OverlayLayout.Search("##effect-filter", "filter by path", ref _filter, 128);

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

        bool keepingNothing() => !_layer.KeepHostile && (_noise is null || _noise.IsOn(NoiseKind.Engine));
    }
}
