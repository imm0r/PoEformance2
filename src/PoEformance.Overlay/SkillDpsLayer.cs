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
/// AT THE FOOT OF THE ICON, inside it, where it collides with neither the mouse glyphs the game
/// draws above the top row nor the key labels it draws below the bottom one. The plate and the
/// figures face are the flask figure's, and for its reasons.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SkillDpsLayer
{
    /// <summary>How tall the figure's line is, as a share of the icon's height.</summary>
    private const float FigureShare = 0.3f;

    /// <summary>Smallest the figure is drawn, whatever the icon and the scale come to.</summary>
    private const float SmallestFigure = 9f;

    /// <summary>How far the figure's ink stands off the icon's bottom edge, as a share of its size.</summary>
    private const float Inset = 0.35f;

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
        if (slots.Count == 0 || dps.Count == 0)
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
            float size = MathF.Max(
                SmallestFigure,
                Style.Sized(StyleCatalogue.Keys.SkillDps, slot.Where.Height * FigureShare));
            float scale = size / natural;

            float wide = ImGui.CalcTextSize(text).X * scale;
            float top = inkTop * scale;
            float tall = inkHeight * scale;

            // The line's origin, placed so that the INK sits just above the icon's foot.
            var at = new Vector2(
                slot.Where.Left + ((slot.Where.Width - wide) / 2f),
                slot.Where.Bottom - (size * Inset) - tall - top);

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
