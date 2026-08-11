using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Paints the map by where the damage and the time actually went.
/// </summary>
/// <remarks>
/// A total says a map was hard. A picture says WHERE it was hard, and only the second one is
/// worth acting on - which room the pack was in, which corner took half the life bar, which
/// third of the layout the time disappeared into.
///
/// LARGE MAP ONLY, like the unwalked marks. The minimap is a few hundred pixels with the
/// player at its centre; a wash of colour over it would bury the thing it is for.
///
/// UNDER EVERYTHING. It is the ground the rest is read against, and a monster dot lost behind
/// a heat patch is a marker that did not work.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HeatLayer
{
    /// <summary>
    /// Most patches drawn in one frame.
    /// </summary>
    /// <remarks>
    /// A backstop rather than a budget: a map fills a few thousand patches, and this is here
    /// because the count comes from how long somebody has been running rather than from
    /// anything bounded.
    /// </remarks>
    private const int MostPatches = 8_000;

    /// <summary>How every drawn thing looks - and where this is switched off.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Which of the three measurements is being painted.</summary>
    public HeatOf Showing { get; set; } = HeatOf.Dealt;

    /// <summary>Whether to paint at all. Off by default: it is a thing you go and look at.</summary>
    /// <remarks>
    /// A picture of a whole map is for AFTERWARDS - opened over the map screen when a run is
    /// done or a death wants explaining. Painted by default it would be a wash of colour under
    /// every marker for the whole of every map, which is the opposite of what it is for.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Paints one patch per place something happened.</summary>
    public void Draw(ImDrawListPtr draw, MapView map, HeatMap heat, WorldEntity player, uint area)
    {
        ArgumentNullException.ThrowIfNull(heat);
        ArgumentNullException.ThrowIfNull(player);

        if (!Enabled || !map.IsLargeMap || area == 0 || !Style.Visible(StyleCatalogue.Keys.Heat))
        {
            return;
        }

        float hottest = heat.Hottest(area, Showing);
        if (hottest <= 0f)
        {
            return;
        }

        uint hot = Style.Colour(StyleCatalogue.Keys.Heat);

        // Half a patch on screen, so the marks meet rather than leaving a grid of gaps between
        // them - a heat map with holes in it reads as sparse data rather than as a smooth one.
        float half = MathF.Max(
            1.5f, map.PixelsPerGridCell * HeatMap.Step * 0.5f * Style.Sized(StyleCatalogue.Keys.Heat, 1f));

        int drawn = 0;
        foreach ((int x, int y, HeatCell cell) in heat.In(area))
        {
            float value = cell.Of(Showing);
            if (value <= 0f)
            {
                continue;
            }

            (float worldX, float worldY) = HeatMap.WorldOf(x, y);
            Vector2 at = map.Project(
                worldX, worldY, player.TerrainHeight, player.WorldX, player.WorldY, player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            draw.AddRectFilled(
                at - new Vector2(half, half), at + new Vector2(half, half), Shade(hot, value / hottest));

            if (++drawn >= MostPatches)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The colour for one patch, from cool at nothing to the chosen colour at the top.
    /// </summary>
    /// <remarks>
    /// ONE COLOUR CHOSEN, both ends derived. A ramp needs two and asking for two means somebody
    /// has to match them by eye; deriving the cool end by rotating the hot one back through the
    /// spectrum keeps the pair related whatever is picked.
    ///
    /// ALPHA RISES WITH THE VALUE as well as the hue, which is what keeps this readable over a
    /// game map rather than over a white page: a cool patch has to be nearly transparent or the
    /// quiet nine tenths of a map becomes a solid sheet, and the map underneath is the thing
    /// being annotated.
    /// </remarks>
    private static uint Shade(uint hot, float share)
    {
        float heat = Math.Clamp(share, 0f, 1f);

        // ImGui packs ABGR, alpha in the high byte.
        float red = (hot & 0xFF) / 255f;
        float green = ((hot >> 8) & 0xFF) / 255f;
        float blue = ((hot >> 16) & 0xFF) / 255f;
        float alpha = ((hot >> 24) & 0xFF) / 255f;

        // The cool end: the hot colour's channels swung towards blue. At heat 0 it is that
        // swung colour, at 1 it is the colour as chosen.
        float coolRed = red * 0.25f;
        float coolGreen = green * 0.35f;
        float coolBlue = MathF.Max(blue, 0.75f);

        return Pack(
            Mix(coolRed, red, heat),
            Mix(coolGreen, green, heat),
            Mix(coolBlue, blue, heat),

            // Squared, so the quiet majority of a map stays faint and the busy patches carry
            // the picture. Linear alpha makes a map that was mostly quiet look mostly painted.
            alpha * ((0.18f * (1f - heat)) + (heat * heat)));
    }

    private static float Mix(float from, float to, float by) => from + ((to - from) * by);

    private static uint Pack(float red, float green, float blue, float alpha)
        => ((uint)(Math.Clamp(alpha, 0f, 1f) * 255f) << 24)
           | ((uint)(Math.Clamp(blue, 0f, 1f) * 255f) << 16)
           | ((uint)(Math.Clamp(green, 0f, 1f) * 255f) << 8)
           | (uint)(Math.Clamp(red, 0f, 1f) * 255f);
}
