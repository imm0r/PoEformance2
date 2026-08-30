using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the chosen ritual routes over the atlas, with what each map would pay.
/// </summary>
/// <remarks>
/// A route is only useful if you can see WHERE it goes and WHAT each step is worth, so both are
/// drawn: the line through the maps, and each map's reward written on it. Several routes at once,
/// each in its own colour - a route is unreadable if you cannot tell which line belongs to which
/// row in the list, so the colour is the join between the two.
///
/// NOTHING is drawn until somebody ticks a route. The atlas in ritual mode already has a plan
/// with hundreds of routes in it, and drawing them all would be a solid mesh.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RitualLayer
{
    /// <summary>
    /// The colours routes are drawn in, in order.
    /// </summary>
    /// <remarks>
    /// Chosen to be told apart at a glance rather than to be pretty, and deliberately not from
    /// the style catalogue: these are not a look, they are labels, and letting somebody set two
    /// of them the same would break the one thing they are for.
    /// </remarks>
    public static IReadOnlyList<uint> Palette { get; } =
    [
        Packed(255, 217, 51),    // yellow
        Packed(255, 128, 38),    // orange
        Packed(77, 217, 255),    // cyan
        Packed(115, 255, 115),   // green
        Packed(230, 115, 255),   // violet
        Packed(255, 77, 77),     // red
    ];

    /// <summary>How thick a route is drawn.</summary>
    public float Width { get; set; } = 5f;

    /// <summary>How big the writing is, against the interface's own size.</summary>
    public float TextScale { get; set; } = 1f;

    /// <summary>Write each map's reward along the route.</summary>
    public bool ShowRewards { get; set; } = true;

    /// <summary>
    /// Where on the screen this may draw: everything, less whatever the game paints on top.
    /// </summary>
    /// <remarks>
    /// The same region the atlas layer takes, and for the same reason - these lines are drawn
    /// over the same panel, so they land on the orbs and any open side panel just as readily.
    /// See <see cref="AtlasLayer.Region"/>.
    /// </remarks>
    public ScreenRegion? Region { get; set; }

    /// <summary>Where a clipped line ends up. Reused, like the atlas layer's.</summary>
    private readonly List<(Vector2 From, Vector2 To)> _pieces = [];

    /// <summary>The colour a route drawn Nth takes.</summary>
    public static uint ColourFor(int index) => Palette[((index % Palette.Count) + Palette.Count) % Palette.Count];

    /// <summary>
    /// Draws the picked routes.
    /// </summary>
    /// <param name="picked">
    /// Which routes to draw, by their key, and in which colour slot - exactly what the window
    /// holds, so the two cannot disagree about which line is which.
    /// </param>
    public void Draw(ImDrawListPtr draw, RitualView view, IReadOnlyDictionary<string, int> picked)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(picked);

        if (!view.Drawing || picked.Count == 0)
        {
            return;
        }

        foreach (RitualChain chain in view.Chains)
        {
            if (picked.TryGetValue(chain.Key, out int slot))
            {
                DrawRoute(draw, view, chain, ColourFor(slot));
            }
        }
    }

    /// <summary>One route: the line through its maps, then what each of them pays.</summary>
    private void DrawRoute(ImDrawListPtr draw, RitualView view, RitualChain chain, uint colour)
    {
        if (!view.Where.TryGetValue(chain.From, out Vector2 at))
        {
            return;
        }

        // The start marked with a ring rather than a dot, because on a busy atlas a dot is
        // whatever the game already drew there.
        var reach = new Vector2(Width * 2f, Width * 2f);
        if (Fits(at - reach, at + reach))
        {
            draw.AddCircle(at, Width * 2f, colour, 0, Width * 0.6f);
        }

        Vector2 previous = at;
        float font = ImGui.GetFontSize() * (TextScale > 0f ? TextScale : 1f);

        for (int i = 0; i < chain.Stops.Count; i++)
        {
            RitualStop stop = chain.Stops[i];
            if (!view.Where.TryGetValue(stop.At, out Vector2 next))
            {
                continue;
            }

            Line(draw, previous, next, colour);
            previous = next;

            if (!ShowRewards)
            {
                continue;
            }

            // Under the map rather than over it: the atlas's own labels sit above a node, and
            // the map-name overlay is nudged up there too.
            string said = stop.Twice ? $"{stop.First} + {stop.Second}" : stop.First;
            Vector2 size = ImGui.CalcTextSize(said) * (font / ImGui.GetFontSize());
            var where = new Vector2(next.X - (size.X * 0.5f), next.Y + (Width * 2f));
            var pad = new Vector2(4f, 1f);

            if (Fits(where - pad, where + size + pad))
            {
                draw.AddRectFilled(where - pad, where + size + pad, Packed(0, 0, 0, 205), 3f);
                draw.AddText(ImGui.GetFont(), font, where, colour, said);
            }
        }
    }

    /// <summary>Draws a line in as many pieces as the game's interface leaves room for.</summary>
    private void Line(ImDrawListPtr draw, Vector2 from, Vector2 to, uint colour)
    {
        if (Region is not ScreenRegion region)
        {
            draw.AddLine(from, to, colour, Width);
            return;
        }

        _pieces.Clear();
        region.ClipSegment(from, to, _pieces);
        foreach ((Vector2 start, Vector2 end) in _pieces)
        {
            draw.AddLine(start, end, colour, Width);
        }
    }

    /// <summary>Whether a box of writing may be drawn where it wants to go.</summary>
    private bool Fits(Vector2 from, Vector2 to)
        => Region is not ScreenRegion region
           || region.Clear(new ScreenRect(from.X, from.Y, to.X, to.Y));

    /// <summary>A colour as ImGui packs it - ABGR, alpha in the high byte.</summary>
    private static uint Packed(byte r, byte g, byte b, byte a = 255)
        => ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
}
