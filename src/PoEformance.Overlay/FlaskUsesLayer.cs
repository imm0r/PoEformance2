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
        ImFontPtr font = ImGui.GetFont();

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

            // Measured at the interface's size and scaled, which is what ImGui offers without
            // pushing a font - a bitmap face scales linearly anyway.
            Vector2 extent = ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
            var at = new Vector2(
                slot.Where.Left + ((slot.Where.Width - extent.X) / 2f),
                slot.Where.Top + ((slot.Where.Height - extent.Y) / 2f));

            Written(draw, font, at, text, size, uses > 0 ? colour : empty);
        }
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
