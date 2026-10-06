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
/// THE RUNE TAKES THE PRICE'S PLACE where the price would mislead: a player who committed to
/// a recipe paying less than the monolith offered took it for the rune, and a small figure
/// there argues against walking to a monolith worth walking to - "[5] Opulent". A monolith
/// sealed by a reroll shows both, "[5] 49 ex | Opulent", since neither can change and no
/// intent can be read out of a random pick. The label is built as coloured SEGMENTS because
/// the two halves want different tints - the price against the best on screen, the rune by
/// its class - and one colour for both would make one of them lie.
///
/// Scouting puts the runes a monolith could still propagate on a line above, in amber, best
/// first - so the map says which monoliths can seed a strong chain, not only which pay.
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
    private const uint Amber = 0xFF4D_CCFFu;
    private const uint Grey = 0xFF9A_9A9Au;

    /// <summary>How far below the monolith's point the label's top sits.</summary>
    private const float Below = 6f;

    /// <summary>Between the price and the rune on a sealed monolith's label - two kinds of fact, not one phrase.</summary>
    private const string Separator = " | ";

    /// <summary>One label's segments: reused across labels and frames, since this runs in the draw loop.</summary>
    private readonly List<(string Text, uint Colour, float Width)> _parts = new(3);

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

            MonolithRuneLabel mode = settings.MapRune ? view.RuneOnMap : MonolithRuneLabel.None;
            bool scouting = settings.MapScout && mode == MonolithRuneLabel.None && view.Scout.Count > 0;

            _parts.Clear();
            string head = settings.MapSockets && view.HoleCount > 0 ? $"[{view.HoleCount}] " : string.Empty;
            if (mode == MonolithRuneLabel.Replace)
            {
                // The price is absent, not recoloured: it is the thing that misleads.
                Part(head + view.ChosenRune, RunecraftLayer.RuneTint(view.ChosenMult), scale);
            }
            else if (view.Priced)
            {
                Part(head + RunecraftPrices.Format(view.Best), Tint(settings, view.Best, monoliths.MaxBest), scale);
                if (mode == MonolithRuneLabel.Append)
                {
                    Part(Separator, Grey, scale);
                    Part(view.ChosenRune, RunecraftLayer.RuneTint(view.ChosenMult), scale);
                }
            }
            else if (settings.ShowUnpriced && view.HoleCount > 0)
            {
                Part(head + "?", White, scale);
            }

            if (_parts.Count == 0 && !scouting)
            {
                continue;
            }

            Vector2 at = map.Project(
                view.WorldX, view.WorldY, view.TerrainHeight,
                player.WorldX, player.WorldY, player.TerrainHeight);
            if (!map.Contains(at))
            {
                continue;
            }

            float top = at.Y + Below;
            if (_parts.Count > 0)
            {
                float wide = 0f;
                foreach ((_, _, float width) in _parts)
                {
                    wide += width;
                }

                var corner = new Vector2(at.X - (wide * 0.5f), top);
                draw.AddRectFilled(corner - pad, corner + new Vector2(wide, size) + pad, Plate, 2f);
                float pen = corner.X;
                foreach ((string text, uint colour, float width) in _parts)
                {
                    var origin = new Vector2(pen, corner.Y);
                    draw.AddText(font, size, origin + new Vector2(1f, 1f), Shadow, text);
                    draw.AddText(font, size, origin, colour, text);
                    pen += width;
                }
            }

            if (scouting)
            {
                // One line above the price, not a tower: the label sits on the map, and a stack
                // of names over a monolith hides more than it tells.
                string names = string.Join(Separator, view.Scout);
                Vector2 measured = ImGui.CalcTextSize(names) * scale;
                var corner = new Vector2(at.X - (measured.X * 0.5f), top - size - (3f * scale));
                draw.AddRectFilled(corner - pad, corner + measured + pad, Plate, 2f);
                draw.AddText(font, size, corner + new Vector2(1f, 1f), Shadow, names);
                draw.AddText(font, size, corner, Amber, names);
            }
        }
    }

    private void Part(string text, uint colour, float scale)
        => _parts.Add((text, colour, ImGui.CalcTextSize(text).X * scale));

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
