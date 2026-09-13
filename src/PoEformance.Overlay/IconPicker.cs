using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The grid of sheet cells, for choosing which one a thing is drawn as.
/// </summary>
/// <remarks>
/// ONE PICKER FOR EVERY CHOOSER. The marker rows and the status-icon rules both pick a cell off
/// the same sheet, and two grids would be two places to fix when the sheet grows a row.
///
/// CLIPPED RATHER THAN DRAWN WHOLE, which is not a refinement. The sheet holds 1050 cells; an
/// image button each is 1050 draw calls and 1050 ImGui IDs hashed, every frame the popup is
/// open, for the two dozen rows anybody can see. Only the rows the scroll position actually
/// exposes are built, with an empty box standing in for the ones above and below so the
/// scrollbar still measures the whole grid. The cost is what is visible rather than what
/// exists, and it stays that way if the sheet doubles.
///
/// Clipped by hand rather than with ImGui's own ImGuiListClipper, which in these bindings is
/// reached through a raw pointer and a matching Destroy - a lifetime to get right in code that
/// cannot be run on this machine, buying nothing over arithmetic on a fixed row height.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class IconPicker
{
    /// <summary>Nothing was chosen this frame.</summary>
    public const int Unchanged = -1;

    /// <summary>The choice was "no icon".</summary>
    public const int None = 0;

    /// <summary>How big one cell is drawn in the grid.</summary>
    /// <remarks>
    /// Larger than the markers themselves, because this is the one place the art is being READ
    /// rather than glanced at: several of these cells differ only in their colour or in a small
    /// corner mark, and at marker size they are the same picture.
    /// </remarks>
    private const float CellSize = 32f;

    /// <summary>How many cells fit across the popup before it wraps.</summary>
    private const int Across = 14;

    /// <summary>
    /// Draws the grid, and returns the cell chosen - counted from ONE, as a style stores it.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="Unchanged"/> on nearly every frame, because a popup is open for many
    /// frames and chosen in one of them. The caller writes only when it is told to, which is
    /// what keeps an open popup from counting as an edit and re-saving the file every frame.
    /// </remarks>
    public static int Draw(IconCache.Picture sheet, int currentTile)
    {
        if (!sheet.Ready)
        {
            // The sheet is embedded, so this is a build mistake rather than anything the user
            // did - said plainly instead of showing an empty box they might scroll for.
            ImGuiText.Wrapped(OverlayInk.Quiet, $"{IconSheet.Resource} did not ship with this build.");
            return ImGui.SmallButton("None") ? None : Unchanged;
        }

        int columns = IconSheet.ColumnsIn(sheet.Width);
        int count = IconSheet.CountIn(sheet.Width, sheet.Height);
        int chosen = Unchanged;

        if (ImGui.SmallButton("None"))
        {
            chosen = None;
        }

        ImGui.SameLine();
        ImGuiText.Wrapped(
            OverlayInk.Quiet,
            currentTile > 0 ? $"cell {currentTile} of {count}" : $"{count} to choose from");

        ImGui.Separator();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        var size = new Vector2(CellSize, CellSize);

        // Sized to the grid rather than left to ImGui: a child sized from its contents is as
        // tall as 75 rows, which is a popup taller than the screen.
        if (!ImGui.BeginChild(
                "##cells",
                new Vector2((Across * (CellSize + spacing)) + spacing, CellSize * 12f),
                ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return chosen;
        }

        try
        {
            int rows = (count + Across - 1) / Across;

            // The button's own frame padding is part of what a row occupies, so the step is
            // measured rather than assumed to be the cell - out by those few pixels, the
            // placeholders drift from the real rows and the grid scrolls past its own end.
            float step = CellSize + (ImGui.GetStyle().FramePadding.Y * 2f) + ImGui.GetStyle().ItemSpacing.Y;
            float scroll = ImGui.GetScrollY();
            float visible = ImGui.GetWindowSize().Y;

            // One row of slack at each end, so a row half off the edge is built rather than
            // popping in once its top crosses the boundary.
            int first = Math.Max(0, (int)(scroll / step) - 1);
            int last = Math.Min(rows, (int)((scroll + visible) / step) + 2);

            if (first > 0)
            {
                ImGui.Dummy(new Vector2(1f, first * step));
            }

            for (int row = first; row < last; row++)
            {
                for (int column = 0; column < Across; column++)
                {
                    int index = (row * Across) + column;
                    if (index >= count)
                    {
                        break;
                    }

                    if (column > 0)
                    {
                        ImGui.SameLine();
                    }

                    if (Cell(sheet, index, columns, size, currentTile))
                    {
                        chosen = index + 1;
                    }
                }
            }

            if (last < rows)
            {
                ImGui.Dummy(new Vector2(1f, (rows - last) * step));
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return chosen;
    }

    /// <summary>One cell as a button, marked when it is the one already chosen.</summary>
    private static bool Cell(
        IconCache.Picture sheet, int index, int columns, Vector2 size, int currentTile)
    {
        (Vector2 uv0, Vector2 uv1) = SheetIcon.Uv(index, sheet);

        // The chosen one is shown by its BACKGROUND rather than by a tint on the art: tinting
        // changes what the cell looks like, which is the one thing a picker must not do.
        bool current = currentTile == index + 1;
        Vector4 back = current ? OverlayInk.Accent with { W = 0.55f } : new Vector4(0f, 0f, 0f, 0f);

        ImGui.PushID(index);
        bool pressed = ImGui.ImageButton("##cell", sheet.Texture, size, uv0, uv1, back, Vector4.One);
        ImGui.PopID();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"cell {index + 1}  (column {index % columns}, row {index / columns})");
        }

        return pressed;
    }
}
