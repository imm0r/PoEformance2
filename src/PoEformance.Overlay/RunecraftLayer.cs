using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Writes each reward's poe.ninja price onto its row of the Runeshape Combinations panel.
/// </summary>
/// <remarks>
/// ON THE GAME'S OWN PANEL, not in a window of this tool's: the reward's name is already on the
/// row, in the client's language, so the price goes beside it at the row's right edge and
/// nothing is said twice. That is the reference plugin's design and the reason it is worth
/// porting - the alternative, a list of the same rows in a second window, is a window that
/// steals focus from the panel it duplicates.
///
/// CLIPPED TO THE VIEWPORT BY HAND. Rows scrolled out of the list keep their visible bit; the
/// game clips them with a scissor rectangle, so without this every scrolled-off row's price would
/// be painted over whatever sits above and below the list. Only the vertical extent is clipped:
/// the sideways offset is the user's to set and may deliberately put the price outside the frame.
///
/// THE WRITING FOLLOWS THE ROW, half its height, so it reads at any interface scale and never
/// outgrows the row it is on. A plate behind it, because the panel's own art is busy exactly
/// where a price lands.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RunecraftLayer
{
    private const uint White = 0xFFFF_FFFFu;
    private const uint Green = 0xFF55_FF55u;
    private const uint Yellow = 0xFF55_FFFFu;
    private const uint Red = 0xFF40_40FFu;
    private const uint Shadow = 0xCC00_0000u;
    private const uint Plate = 0xE600_0000u;

    /// <summary>What the price is written as a share of the row's height, before the user's scale.</summary>
    private const float Share = 0.5f;

    private const float SmallestText = 12f;
    private const float LargestText = 48f;

    /// <summary>Draws every placed row's price.</summary>
    public void Draw(ImDrawListPtr draw, RunecraftView view, RunecraftSettings settings)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(settings);

        if (!view.Anything)
        {
            return;
        }

        ImFontPtr font = ImGui.GetFont();
        float natural = ImGui.GetFontSize();
        float writing = settings.Writing;

        foreach (RunecraftReward reward in view.Rewards)
        {
            ScreenRect row = reward.Where;

            // The vertical clip: a row whose centre is above or below the viewport is one the
            // game is not drawing, whatever its flag says.
            if (view.Viewport is { } clip)
            {
                float centre = (row.Top + row.Bottom) * 0.5f;
                if (centre < clip.Top || centre > clip.Bottom)
                {
                    continue;
                }
            }

            string text;
            uint colour;
            if (reward.Total is { } total)
            {
                text = RunecraftPrices.Format(total);
                colour = Tint(settings, total, view.Median);
            }
            else if (settings.ShowUnpriced)
            {
                text = "?";
                colour = White;
            }
            else
            {
                continue;
            }

            float size = Math.Clamp(row.Height * Share * writing, SmallestText, LargestText);
            float scale = size / natural;
            Vector2 measured = ImGui.CalcTextSize(text) * scale;
            float padding = 6f * scale;
            var pad = new Vector2(4f * scale, 2f * scale);

            var at = new Vector2(
                row.Right - measured.X - padding + settings.XOffset,
                row.Top + ((row.Height - measured.Y) * 0.5f));

            draw.AddRectFilled(at - pad, at + measured + pad, Plate, 3f * scale);

            // The best row's ring sits OUTSIDE the plate, so it never covers the figure it is
            // pointing at and still reads as belonging to it.
            if (settings.FrameBest && reward.Best && reward.Total is not null)
            {
                Vector2 ring = pad + new Vector2(2f * scale, 2f * scale);
                draw.AddRect(at - ring, at + measured + ring, Green, 4f * scale, ImDrawFlags.None, 2f * scale);
            }

            draw.AddText(font, size, at + new Vector2(1f, 1f), Shadow, text);
            draw.AddText(font, size, at, colour, text);
        }
    }

    /// <summary>The colour a price is written in, by the chosen mode.</summary>
    /// <remarks>
    /// Public and static so the thresholds are a test rather than a screenshot: Relative tints
    /// against the median of the rows on screen - a third above it green, a third below red -
    /// and Absolute against the two Exalted figures the settings carry.
    /// </remarks>
    public static uint Tint(RunecraftSettings settings, double total, double median)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.ColourMode)
        {
            case RunecraftColourMode.Absolute:
                return total >= settings.GoodFrom ? Green : total < settings.BadBelow ? Red : Yellow;

            case RunecraftColourMode.Relative:
                if (median <= 0)
                {
                    return White;
                }

                double ratio = total / median;
                return ratio >= 1.3 ? Green : ratio <= 0.7 ? Red : Yellow;

            default:
                return White;
        }
    }
}
