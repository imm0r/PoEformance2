using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Draws one cell of the icon sheet, wherever something wants a picture instead of a shape.
/// </summary>
/// <remarks>
/// ONE PLACE THAT CUTS THE SHEET UP. Four callers draw from it - the place markers, the entity
/// dots, the status icons over a monster's head, and the picker that chooses between them - and
/// the cut is texture-coordinate arithmetic off a tile size in pixels, which is the kind of
/// thing that is nearly right in three of four copies. The one that is nearly right samples a
/// strip of its neighbour along one edge, which reads as the art being sloppy.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class SheetIcon
{
    /// <summary>
    /// The texture coordinates of one cell, clamped to what the sheet actually holds.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted, because the number comes out of a settings file: a cell
    /// chosen against a taller sheet, or typed by hand, otherwise samples past the edge and
    /// draws whatever the sampler decides to repeat. That looks like a wrong icon rather than
    /// a missing one, so it gets blamed on the choice instead of on the sheet.
    /// </remarks>
    public static (Vector2 Uv0, Vector2 Uv1) Uv(int index, IconCache.Picture sheet)
    {
        if (!sheet.Ready)
        {
            return (Vector2.Zero, Vector2.One);
        }

        (int column, int row) = IconSheet.CellAt(index, sheet.Width, sheet.Height);
        var step = new Vector2((float)IconSheet.Tile / sheet.Width, (float)IconSheet.Tile / sheet.Height);
        var uv0 = new Vector2(column * step.X, row * step.Y);
        return (uv0, uv0 + step);
    }

    /// <summary>
    /// Draws a cell into a box, and says whether it drew anything.
    /// </summary>
    /// <remarks>
    /// False means "draw it the ordinary way", which covers both no icon chosen and a sheet
    /// that did not ship. Those are deliberately the same answer: a marker that vanished
    /// because its picture was missing reads as there being nothing there, which is the one
    /// thing a map must never say by accident.
    /// </remarks>
    public static bool Draw(
        ImDrawListPtr draw, IconCache.Picture sheet, LayerStyle style, Vector2 min, Vector2 max, uint tint)
    {
        if (!style.HasIcon || !sheet.Ready)
        {
            return false;
        }

        (Vector2 uv0, Vector2 uv1) = Uv(style.IconIndex, sheet);
        draw.AddImage(sheet.Texture, min, max, uv0, uv1, tint);
        return true;
    }
}
