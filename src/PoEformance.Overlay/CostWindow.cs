using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What every read cost, over a whole map.
/// </summary>
/// <remarks>
/// The status line answers "is it slow right now", which is the question you can already
/// answer by looking at the screen. The ones worth asking need the shape over time: did it get
/// worse as the map filled up, was that stutter the terrain or the entities, does this
/// mechanic cost anything - and an instant cannot say.
///
/// A graph per PHASE rather than one for the total, because the total moving is not a finding.
/// Which phase moved with it is.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CostWindow
{
    /// <summary>Every phase gets a plot, in the order the read performs them.</summary>
    private static readonly (CostPhase Phase, string Label)[] Phases =
    [
        (CostPhase.Total, "total"),
        (CostPhase.Entities, "entities"),
        (CostPhase.Player, "player"),
        (CostPhase.Terrain, "terrain"),
        (CostPhase.Maps, "maps"),
        (CostPhase.Other, "other"),
    ];

    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 WorstText = OverlayInk.Warn;

    private readonly CostHistory _history;
    private bool _thisMapOnly = true;
    private float _height = 60f;

    public CostWindow(CostHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    /// <summary>The area being read right now, so "this map only" has something to mean.</summary>
    public uint CurrentArea { get; set; }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab() => Draw();

    private void Draw()
    {
        uint scope = _thisMapOnly ? CurrentArea : 0;

        ImGui.Checkbox("this map only", ref _thisMapOnly);
        ImGui.SameLine();
        if (ImGui.Button("Start Over"))
        {
            _history.Clear();
        }

        ImGui.SameLine();
        OverlayLayout.Slider("Plot Height", ref _height, 30f, 160f, "%.0f px");

        double seconds = _history.SecondsIn(scope);
        ImGui.TextColored(
            DimText,
            $"{_history.Count} reads held, {_history.Areas().Count} areas"
            + (seconds > 0 ? $"   -   {seconds / 60:F1} minutes in this scope" : string.Empty));

        if (_thisMapOnly && CurrentArea == 0)
        {
            ImGui.TextColored(DimText, "not in an area - nothing to scope to");
            return;
        }

        ImGui.Separator();

        foreach ((CostPhase phase, string label) in Phases)
        {
            float[] series = _history.Series(phase, scope);
            if (series.Length == 0)
            {
                continue;
            }

            CostSummary summary = _history.Summarise(phase, scope);

            // The scale is the phase's OWN worst rather than the total's, or every plot but
            // the first is a flat line along the bottom and the breakdown says nothing.
            ImGui.PlotLines(
                $"##plot{phase}",
                ref series[0],
                series.Length,
                0,
                label,
                0f,
                (float)Math.Max(summary.Worst * 1.1, 0.1),
                new Vector2(0f, _height));

            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextColored(DimText, $"avg {Ms(summary.Typical)}");
            ImGui.TextColored(DimText, $"p95 {Ms(summary.NinetyFifth)}");

            // The worst reading in its own colour, because it is the one that was felt. An
            // average hides the spike, and the spike is what anybody is here about.
            ImGui.TextColored(WorstText, $"max {Ms(summary.Worst)}");
            ImGui.EndGroup();
        }
    }

    private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture) + " ms";
}
