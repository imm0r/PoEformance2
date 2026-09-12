using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Writes on each skill-bar icon the DPS the Skills panel last showed for that skill.
/// </summary>
/// <remarks>
/// WHAT THE FIGURE IS AND IS NOT. The game computes a skill's DPS for its Skills window and
/// writes it nowhere else, so this is that number, read off the panel while it was open and
/// remembered - see SkillPanelReader. It is as fresh as the last look at the panel, not live:
/// a buff that lands mid-fight changes what the panel would say and not what is written here
/// until the panel is opened again. Written short (54k rather than 53.838), because an icon is
/// sixty pixels wide and a glance is all it gets.
///
/// IN THE ICON'S TOP-RIGHT CORNER, inside it, as a badge. The first build set it at the foot,
/// centred, to keep clear of the mouse glyphs the game draws above the top row and the key
/// labels it draws below the bottom one - but both of those sit OUTSIDE the icon, so nothing
/// inside it is contested, and after a look in game the corner was preferred: it leaves the
/// icon's picture readable under it and reads as a count does elsewhere in the interface. The
/// plate and the figures face are the flask figure's, and for its reasons.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SkillDpsLayer
{
    /// <summary>How tall the figure's line is, as a share of the icon's height - the flask figure's share.</summary>
    /// <remarks>
    /// Three tenths read too small in game beside the flask figure, which is four tenths of its
    /// slot; the same four tenths here puts a three-glyph figure ("77k") at the flask digit's
    /// size. A skill icon is square, though, and a four-glyph figure ("538k", "1.2M") at this
    /// height would run past the icon's left edge into the neighbour's corner, so the width caps
    /// what the share asks for - see <see cref="Draw"/>. The style's scale multiplies the share,
    /// up to that same cap.
    /// </remarks>
    private const float FigureShare = 0.4f;

    /// <summary>Smallest the figure is drawn, whatever the icon and the scale come to.</summary>
    private const float SmallestFigure = 9f;

    /// <summary>
    /// How far the figure's ink stands off the icon's right and top edges, as shares of its
    /// size - each the plate's own reach that way plus a hair of clearance, so the plate sits
    /// in the corner rather than near it. A look in game asked for it tighter than the first
    /// build's even margin.
    /// </summary>
    private const float InsetBeside = 0.24f;
    private const float InsetAbove = 0.16f;

    /// <summary>How far the plate reaches beyond the ink, as shares of the figure's size.</summary>
    private const float PlateBeside = 0.22f;
    private const float PlateAbove = 0.14f;
    private const float PlateRounding = 0.2f;

    /// <summary>How every drawn thing looks. Shared with the overlay; also where this is switched off.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Writes the figure on every slot whose skill has one.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Style.Visible(StyleCatalogue.Keys.SkillDps))
        {
            return;
        }

        IReadOnlyList<SkillSlotOnScreen> slots = snapshot.SkillSlotsOnScreen;
        IReadOnlyDictionary<ulong, int> dps = snapshot.SkillDpsByKey;
        if (slots.Count == 0)
        {
            return;
        }

        // The slot rectangles as the tool has them, so a screenshot shows whether they sit on
        // the icons the game draws: the figure is placed against these, and a figure that lands
        // short of the corner is either the placing or the rectangle - which is a question no
        // amount of moving the figure answers.
        if (Style.Visible(StyleCatalogue.Keys.SkillSlots))
        {
            uint frame = Style.Colour(StyleCatalogue.Keys.SkillSlots);
            float thickness = Style.Width(StyleCatalogue.Keys.SkillSlots, 1f);
            foreach (SkillSlotOnScreen slot in slots)
            {
                draw.AddRect(
                    new Vector2(slot.Where.Left, slot.Where.Top),
                    new Vector2(slot.Where.Right, slot.Where.Bottom),
                    frame, 0f, ImDrawFlags.None, thickness);
            }
        }

        if (dps.Count == 0)
        {
            return;
        }

        uint colour = Style.Colour(StyleCatalogue.Keys.SkillDps);
        uint plate = Style.Visible(StyleCatalogue.Keys.SkillDpsPlate)
            ? Style.Colour(StyleCatalogue.Keys.SkillDpsPlate)
            : 0;

        OverlayFonts.PushFigures();
        ImFontPtr font = ImGui.GetFont();
        float natural = ImGui.GetFontSize();

        // The ink's place in its line, from a digit: a short count is digits and a letter no
        // taller than one, so centring and plating the digit's box fits all of it.
        ImFontGlyphPtr zero = font.FindGlyph('0');
        float inkTop = zero.Y0;
        float inkHeight = zero.Y1 - zero.Y0;
        if (inkHeight <= 0f)
        {
            inkTop = 0f;
            inkHeight = natural;
        }

        foreach (SkillSlotOnScreen slot in slots)
        {
            if (slot.Key == 0 || !dps.TryGetValue(slot.Key, out int figure))
            {
                continue;
            }

            string text = ShortFigure.Format(figure);
            float widePerSize = ImGui.CalcTextSize(text).X / natural;

            // As tall as the share says and no wider than the icon allows: the ink stands off
            // the right edge by its inset, and the same clearance is kept from the left edge,
            // so a figure that would not fit between the two is drawn smaller rather than over
            // the neighbouring icon's corner. "77k" comes out at the share; "538k" a shade under.
            float size = MathF.Max(
                SmallestFigure,
                MathF.Min(
                    Style.Sized(StyleCatalogue.Keys.SkillDps, slot.Where.Height * FigureShare),
                    slot.Where.Width / (widePerSize + (2f * InsetBeside))));
            float scale = size / natural;

            float wide = widePerSize * size;
            float top = inkTop * scale;
            float tall = inkHeight * scale;

            // The line's origin, placed so that the INK sits in the icon's top-right corner.
            var at = new Vector2(
                slot.Where.Right - (size * InsetBeside) - wide,
                slot.Where.Top + (size * InsetAbove) - top);

            if (plate != 0)
            {
                var ink = new Vector2(at.X, at.Y + top);
                var reach = new Vector2(size * PlateBeside, size * PlateAbove);
                draw.AddRectFilled(
                    ink - reach, ink + new Vector2(wide, tall) + reach, plate, size * PlateRounding);
            }

            Written(draw, font, at, text, size, colour);
        }

        OverlayFonts.PopFigures();
    }

    /// <summary>The figure with a dark edge under it, the flask figure's way.</summary>
    private static void Written(
        ImDrawListPtr draw, ImFontPtr font, Vector2 at, string text, float size, uint colour)
    {
        uint edge = OverlaySettings.Fade(0xFF00_0000, ((colour >> 24) & 0xFF) / 255f * 0.9f);
        float step = MathF.Max(1f, size / 14f);

        draw.AddText(font, size, at + new Vector2(step, 0f), edge, text);
        draw.AddText(font, size, at + new Vector2(-step, 0f), edge, text);
        draw.AddText(font, size, at + new Vector2(0f, step), edge, text);
        draw.AddText(font, size, at + new Vector2(0f, -step), edge, text);
        draw.AddText(font, size, at, colour, text);
    }
}
