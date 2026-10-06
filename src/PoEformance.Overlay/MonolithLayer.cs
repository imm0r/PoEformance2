using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Writes each Runecraft monolith's best price on the map, at the monolith.
/// </summary>
/// <remarks>
/// THE MAP IS WHERE THE QUESTION IS ASKED. Which monolith to walk to is decided looking at the
/// map, and a price on the panel answers it only once somebody has already walked there. So
/// the label goes where the game draws the monolith's own icon - projected the way every map
/// marker here is, by the map's own isometric transform, on whichever map is open.
///
/// "[5] 49 ex": the holes first, because they are the first thing that decides whether a
/// monolith is worth the walk, then the best reward it can roll. Tinted against the BEST
/// monolith on screen rather than the median, as the reference does for these: the price
/// distribution is bimodal - a couple of huge ones among many cheap - and a median baseline
/// drifts into the cheap tail and lights an 8 ex monolith green beside a 387 ex one.
///
/// Hidden while the Runeshape panel is open - its rows say it better then - and off a
/// collected monolith, whose price is history.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonolithLayer
{
    private const uint White = 0xFFFF_FFFFu;
    private const uint Green = 0xFF55_FF55u;
    private const uint Yellow = 0xFF55_FFFFu;
    private const uint Red = 0xFF40_40FFu;
    private const uint Shadow = 0xCC00_0000u;
    private const uint Plate = 0xB000_0000u;

    /// <summary>How far below the monolith's point the label's top sits.</summary>
    private const float Below = 6f;

    /// <summary>Draws a label per monolith on the map that is open.</summary>
    public void Draw(ImDrawListPtr draw, MapView map, WorldEntity player, MonolithsView monoliths, RunecraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(monoliths);
        ArgumentNullException.ThrowIfNull(settings);

        if (!monoliths.Any)
        {
            return;
        }

        ImFontPtr font = ImGui.GetFont();
        float natural = ImGui.GetFontSize();
        float size = map.IsLargeMap ? natural : natural * 0.85f;
        float scale = size / natural;
        var pad = new Vector2(3f * scale, 1f * scale);

        foreach (MonolithView view in monoliths.Monoliths)
        {
            if (view.Collected)
            {
                continue;
            }

            string text;
            uint colour;
            if (view.Priced)
            {
                text = RunecraftPrices.Format(view.Best);
                colour = Tint(settings, view.Best, monoliths.MaxBest);
            }
            else if (settings.ShowUnpriced && view.HoleCount > 0)
            {
                text = "?";
                colour = White;
            }
            else
            {
                continue;
            }

            if (settings.MapSockets && view.HoleCount > 0)
            {
                text = $"[{view.HoleCount}] {text}";
            }

            Vector2 at = map.Project(
                view.WorldX, view.WorldY, view.TerrainHeight,
                player.WorldX, player.WorldY, player.TerrainHeight);
            if (!map.Contains(at))
            {
                continue;
            }

            Vector2 measured = ImGui.CalcTextSize(text) * scale;
            var corner = new Vector2(at.X - (measured.X * 0.5f), at.Y + Below);
            draw.AddRectFilled(corner - pad, corner + measured + pad, Plate, 2f);
            draw.AddText(font, size, corner + new Vector2(1f, 1f), Shadow, text);
            draw.AddText(font, size, corner, colour, text);
        }
    }

    /// <summary>The colour a monolith's price is written in, by the chosen mode.</summary>
    /// <remarks>
    /// Public and static so the thresholds are a test: Relative against the best on screen -
    /// half of it or more green, a fifth or less red, between yellow - and Absolute against the
    /// two Exalted figures the settings carry, exactly as the panel's rows are.
    /// </remarks>
    public static uint Tint(RunecraftSettings settings, double best, double maxBest)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.ColourMode)
        {
            case RunecraftColourMode.Absolute:
                return best >= settings.GoodFrom ? Green : best < settings.BadBelow ? Red : Yellow;

            case RunecraftColourMode.Relative:
                if (maxBest <= 0)
                {
                    return White;
                }

                double ratio = best / maxBest;
                return ratio >= 0.5 ? Green : ratio <= 0.2 ? Red : Yellow;

            default:
                return White;
        }
    }
}
