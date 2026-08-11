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

        // MORE than half a patch, so neighbours OVERLAP rather than merely touching. Squares
        // that exactly abut still read as squares - the eye finds the seams - and the thing
        // being drawn is a field, not a mosaic. The overlap is what turns it into one.
        float half = MathF.Max(
            2f,
            map.PixelsPerGridCell * HeatMap.Step * 0.8f * Style.Sized(StyleCatalogue.Keys.Heat, 1f));

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
                break;
            }
        }

        // LAST, over the field, so it is never buried by it - and only once anything was
        // actually painted, because a key to an empty map is a label on nothing.
        if (drawn > 0)
        {
            DrawKey(draw, map, hot, hottest);
        }
    }

    /// <summary>
    /// The ramp, cold to hot. Blue, cyan, green, yellow, red.
    /// </summary>
    /// <remarks>
    /// A REAL RAMP RATHER THAN ONE COLOUR FADED, and the reason is not prettiness. The first
    /// version derived both ends from a single chosen colour, which produced a field of
    /// orange-ish squares - indistinguishable at a glance from the loot and monster markers,
    /// which are also small orange-ish squares. Somebody looking at their own map could not
    /// tell the two apart, which is a complete failure of the thing.
    ///
    /// These five stops are what a heat map looks like everywhere, and the low end being BLUE
    /// and GREEN is what does the work: no marker in this overlay is either, so a blue-green
    /// wash is unmistakably data about the ground rather than a thing standing on it.
    /// </remarks>
    private static readonly (float At, float R, float G, float B)[] Ramp =
    [
        (0.00f, 0.16f, 0.20f, 0.85f),   // deep blue - been here, nothing happened
        (0.25f, 0.10f, 0.75f, 0.85f),   // cyan
        (0.50f, 0.20f, 0.85f, 0.25f),   // green
        (0.75f, 0.95f, 0.85f, 0.15f),   // yellow
        (1.00f, 1.00f, 0.20f, 0.10f),   // red - the busiest ground on the map
    ];

    /// <summary>
    /// The colour for one patch.
    /// </summary>
    /// <remarks>
    /// The chosen colour supplies the ALPHA only, which is what a style entry can usefully own
    /// here: the hues are the ramp and the ramp is the whole point, while how strongly the
    /// thing paints over the game is a matter of taste and screen.
    ///
    /// Alpha rises with the value as well as the hue - not squared, as it first was. Squaring
    /// made the quiet nine tenths of a map nearly invisible, and the quiet nine tenths is what
    /// gives the busy tenth something to be busy against. A floor keeps every visited patch
    /// visible, so the picture reads as a filled field rather than as scattered dots.
    /// </remarks>
    private static uint Shade(uint hot, float share)
    {
        float heat = Math.Clamp(share, 0f, 1f);
        float alpha = ((hot >> 24) & 0xFF) / 255f;

        int upper = 1;
        while (upper < Ramp.Length - 1 && heat > Ramp[upper].At)
        {
            upper++;
        }

        (float atLow, float lowR, float lowG, float lowB) = Ramp[upper - 1];
        (float atHigh, float highR, float highG, float highB) = Ramp[upper];
        float along = atHigh > atLow ? (heat - atLow) / (atHigh - atLow) : 0f;

        return Pack(
            Mix(lowR, highR, along),
            Mix(lowG, highG, along),
            Mix(lowB, highB, along),
            alpha * (0.45f + (0.55f * heat)));
    }

    /// <summary>
    /// The key, so nobody has to guess what they are looking at.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF of telling this apart from the markers. The ramp says "this is a field
    /// rather than a thing standing there"; the key says what the field IS - and without it the
    /// first question anybody asks of a coloured map is which of the two it is.
    ///
    /// Drawn in the map's own corner rather than in a window, because it belongs to the picture
    /// and a key somewhere else is a key nobody reads.
    /// </remarks>
    private void DrawKey(ImDrawListPtr draw, MapView map, uint hot, float hottest)
    {
        const float wide = 140f;
        const float tall = 9f;

        var at = new Vector2(map.Left + 12f, map.Top + 12f);
        if (!map.Contains(at) || !map.Contains(at + new Vector2(wide, tall)))
        {
            return;
        }

        // A strip of the ramp, drawn in slices - the draw list has no gradient of its own and
        // twenty slices across this width is already smooth.
        const int slices = 20;
        for (int i = 0; i < slices; i++)
        {
            float from = (float)i / slices;
            draw.AddRectFilled(
                at + new Vector2(from * wide, 0f),
                at + new Vector2((from + (1f / slices)) * wide, tall),
                Shade(hot, from));
        }

        uint text = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.88f, 0.92f, 1f));
        draw.AddText(at + new Vector2(0f, tall + 2f), text, $"heat map: {Label(Showing)}");
        draw.AddText(at + new Vector2(wide + 6f, -2f), text, Number(hottest));
    }

    /// <summary>What the key calls each measurement.</summary>
    private static string Label(HeatOf what) => what switch
    {
        HeatOf.Taken => "damage taken",
        HeatOf.Time => "time spent",
        _ => "damage done",
    };

    /// <summary>The top of the scale, short enough for a corner.</summary>
    private static string Number(float value) => value switch
    {
        >= 1_000_000f => $"{value / 1_000_000f:0.#}M",
        >= 1_000f => $"{value / 1_000f:0.#}k",
        _ => $"{value:0.#}",
    };

    private static float Mix(float from, float to, float by) => from + ((to - from) * by);

    private static uint Pack(float red, float green, float blue, float alpha)
        => ((uint)(Math.Clamp(alpha, 0f, 1f) * 255f) << 24)
           | ((uint)(Math.Clamp(blue, 0f, 1f) * 255f) << 16)
           | ((uint)(Math.Clamp(green, 0f, 1f) * 255f) << 8)
           | (uint)(Math.Clamp(red, 0f, 1f) * 255f);
}
