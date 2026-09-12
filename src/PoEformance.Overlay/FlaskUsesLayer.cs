using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Writes on each flask how many times it can still be used.
/// </summary>
/// <remarks>
/// The game shows a flask's charges as a liquid level and prints the number only in the
/// tooltip, and what a player wants mid-fight is not the level but the count - two more, or
/// none. The count is charges over the cost of one use, both already read for the auto-flask
/// (the cost with the item's own rolls applied, which is the number the tooltip prints), so
/// nothing new is read for this: the belt says what each flask holds, the interface says where
/// each slot is drawn, and the two are joined on the item entity the slot points at.
///
/// ON THE HUD, so it takes none of the gates the world layers stand behind: a town has flasks
/// too, and an open inventory leaves the belt on screen. It goes away with the bar, which the
/// reader stops reporting while the interface hides it.
///
/// CHARMS ARE LEFT ALONE. They arrive through the same belt and the same bar, but the game
/// prints their charges itself, and a second number beside the game's own would read as a
/// disagreement.
///
/// ON A PLATE, the same one the room names stand on. The first build set the figure pale with
/// a dark edge and nothing else, and the edge was not enough: what is under the figure is
/// whatever colour the flask is, and a life flask's glare swallowed the pale digit. The plate
/// has a style key of its own, so how much of the flask shows through is the user's to set,
/// and a belt of dark flasks can hide it outright.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FlaskUsesLayer
{
    /// <summary>How tall the figure is, as a share of the slot's height.</summary>
    /// <remarks>
    /// About a third: large enough to be read from the middle of the screen at a glance, and
    /// small enough that the flask's own picture and its liquid level stay legible around it.
    /// The style's scale multiplies it.
    /// </remarks>
    private const float FigureShare = 0.34f;

    /// <summary>Smallest the figure is drawn, whatever the slot and the scale come to.</summary>
    private const float SmallestFigure = 10f;

    /// <summary>How far the plate reaches beyond the figure, beside it and above and below, as shares of its size.</summary>
    /// <remarks>
    /// More beside than above: a line of text already carries room for ascenders and
    /// descenders that a digit does not use, so the plate would look tall before it looked wide.
    /// </remarks>
    private const float PlateBeside = 0.2f;
    private const float PlateAbove = 0.02f;

    /// <summary>The plate's corner radius, as a share of the figure's size.</summary>
    private const float PlateRounding = 0.2f;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    /// <remarks>
    /// Also where this is switched off, rather than a flag of its own - the same reason the
    /// unwalked marks give: two switches for one thing is how a setting ends up not doing what
    /// it says, and the style's one is already persisted and already in the editor.
    /// </remarks>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Writes the count on every flask whose slot is on screen.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Style.Visible(StyleCatalogue.Keys.FlaskUses)
            || snapshot.FlaskBelt is not FlaskBelt belt || belt.IsUnknown)
        {
            return;
        }

        IReadOnlyList<FlaskSlotOnScreen> slots = snapshot.FlaskSlotsOnScreen;
        if (slots.Count == 0)
        {
            return;
        }

        uint colour = Style.Colour(StyleCatalogue.Keys.FlaskUses);
        uint empty = Empty(colour);
        uint plate = Style.Visible(StyleCatalogue.Keys.FlaskUsesPlate)
            ? Style.Colour(StyleCatalogue.Keys.FlaskUsesPlate)
            : 0;

        // In the heading face where the machine has one: the figure is drawn at two or three
        // times the interface's text size, and a glyph rasterised larger is magnified less on
        // the way there, which is the difference between a soft digit and a sharp one. ImGui
        // measures in whatever font is pushed, so the measuring happens inside the pair too.
        OverlayFonts.PushHeading();
        ImFontPtr font = ImGui.GetFont();
        float natural = ImGui.GetFontSize();

        foreach (FlaskSlotOnScreen slot in slots)
        {
            if (slot.Item == 0 || Held(belt, slot.Item) is not EquippedFlask flask
                || flask.IsCharm || flask.ChargesPerUse <= 0)
            {
                continue;
            }

            int uses = flask.Uses;
            string text = uses.ToString(CultureInfo.InvariantCulture);
            float size = MathF.Max(
                SmallestFigure,
                Style.Sized(StyleCatalogue.Keys.FlaskUses, slot.Where.Height * FigureShare));

            Vector2 extent = ImGui.CalcTextSize(text) * (size / natural);
            var at = new Vector2(
                slot.Where.Left + ((slot.Where.Width - extent.X) / 2f),
                slot.Where.Top + ((slot.Where.Height - extent.Y) / 2f));

            if (plate != 0)
            {
                var reach = new Vector2(size * PlateBeside, size * PlateAbove);
                draw.AddRectFilled(at - reach, at + extent + reach, plate, size * PlateRounding);
            }

            Written(draw, font, at, text, size, uses > 0 ? colour : empty);
        }

        OverlayFonts.PopHeading();
    }

    /// <summary>The belt's flask with this item, or null when the belt does not list it.</summary>
    /// <remarks>
    /// A loop over a list of at most five rather than a lookup: joined every frame on two slots,
    /// and a dictionary built per snapshot would cost more than the ten comparisons it saves.
    /// </remarks>
    private static EquippedFlask? Held(FlaskBelt belt, ulong item)
    {
        foreach (EquippedFlask flask in belt.Flasks)
        {
            if (flask.Entity == item)
            {
                return flask;
            }
        }

        return null;
    }

    /// <summary>The interface's "bad" ink at the figure's own alpha - the colour of an empty flask.</summary>
    private static uint Empty(uint colour)
    {
        Vector4 bad = OverlayInk.Bad;
        bad.W = ((colour >> 24) & 0xFF) / 255f;
        return ImGui.ColorConvertFloat4ToU32(bad);
    }

    /// <summary>The figure with a dark edge under it, so it reads on the flask's own picture.</summary>
    /// <remarks>
    /// Four extra draws, the same edge the entry card gives its names and for the same reason:
    /// the colour is the user's and the ground is the game's, and nothing checks that the two
    /// go together. The edge grows with the figure, since one pixel disappears under a figure
    /// drawn at three times the interface's size.
    /// </remarks>
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
