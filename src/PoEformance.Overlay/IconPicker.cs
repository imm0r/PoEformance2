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
/// CLIPPED RATHER THAN DRAWN WHOLE, which is not a refinement. The sheet is 14 by 77, so the
/// grid has 1078 places in it; an image button each is 1078 draw calls and 1078 ImGui IDs
/// hashed, every frame the popup is open, for the dozen rows anybody can see. Only the rows the scroll position actually
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

    /// <summary>Smallest a cell is shrunk to when the grid will not otherwise fit.</summary>
    /// <remarks>
    /// Below this the art stops being distinguishable and the picker stops being a picker, so
    /// it is a floor rather than "whatever fits": on a screen too narrow even for this, the
    /// last column is clipped again - which is at least the failure somebody can see and work
    /// around, rather than a grid of identical smudges.
    /// </remarks>
    private const float SmallestCell = 16f;

    /// <summary>How big one cell is drawn in the grid.</summary>
    /// <remarks>
    /// Larger than the markers themselves, because this is the one place the art is being READ
    /// rather than glanced at: several of these cells differ only in their colour or in a small
    /// corner mark, and at marker size they are the same picture.
    /// </remarks>
    private const float CellSize = 32f;

    /// <summary>How many rows of cells the popup shows before it scrolls.</summary>
    private const int VisibleRows = 12;

    /// <summary>
    /// What is being searched for, while a picker is open.
    /// </summary>
    /// <remarks>
    /// One field for the whole tool, which is safe because ImGui allows one popup at a time -
    /// the same reasoning the style rows' path box is written up under. It deliberately SURVIVES
    /// the popup closing: picking two markers out of the same family means typing the same word
    /// twice otherwise.
    /// </remarks>
    private static string _search = string.Empty;

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
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10f);
        ImGui.InputTextWithHint("###find", "find by name...", ref _search, 64);

        ImGui.SameLine();
        string standing = currentTile > 0 ? IconNames.For(currentTile) : string.Empty;
        ImGuiText.Wrapped(
            OverlayInk.Quiet,
            currentTile > 0
                ? standing.Length > 0 ? $"{standing} - cell {currentTile}" : $"cell {currentTile} of {count}"
                : $"{count} to choose from, {IconNames.Count} named");

        ImGui.Separator();

        // THE SEARCH REARRANGES THE GRID rather than greying cells out. A name matches maybe a
        // dozen of a thousand, and left in place those dozen are a dozen rows apart - which is
        // a list you still have to scroll to read, for a question you asked to avoid scrolling.
        // Filtered, the answers are the first row.
        int[]? found = null;
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var hits = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (IconNames.Matches(i + 1, _search))
                {
                    hits.Add(i);
                }
            }

            found = [.. hits];
            if (found.Length == 0)
            {
                ImGuiText.Wrapped(OverlayInk.Quiet, $"nothing named like \"{_search}\".");
                return chosen;
            }
        }

        ImGuiStylePtr style = ImGui.GetStyle();

        // THE GRID IS THE SHEET'S GRID. Laid out at a width of its own, a picker row stops
        // being a sheet row - and the tooltip on each cell reports the column and row it sits
        // at ON THE SHEET, which would then disagree with where it sits in front of you. They
        // happened to be the same number, which is the kind of agreement that holds until
        // somebody widens the sheet.
        int across = columns;
        int shown = found?.Length ?? count;
        int rows = (shown + across - 1) / across;

        // Only when there is actually something to scroll: a sheet short enough to fit would
        // otherwise carry a strip of empty space down its right-hand side.
        float bar = rows > VisibleRows ? style.ScrollbarSize : 0f;

        // EVERYTHING THAT IS NOT PICTURE, and getting this list wrong is what sent the last
        // column off the edge of the box:
        //
        //  - A CELL IS WIDER THAN ITS PICTURE. An image button insets the image by the frame
        //    padding, so it occupies the picture plus that padding on BOTH sides. Fourteen of
        //    them counted as bare pictures come out a whole column too narrow.
        //  - THE SCROLLBAR IS INSIDE THE CHILD. Not asked for on top, it is drawn over the
        //    last column instead of beside it.
        //  - The gaps go BETWEEN the cells, so there is one fewer of them than there are cells.
        //  - The child has padding of its own, on both sides.
        float furniture = (style.WindowPadding.X * 2f) + bar
            + ((across - 1) * style.ItemSpacing.X) + (across * style.FramePadding.X * 2f);

        // AND IT HAS TO FIT ON THE SCREEN. A row of the grid is as wide as the sheet is,
        // however large the interface is set, so on a narrow screen at a big text size the
        // furniture alone can grow past what is there - and a box wider than the viewport is clipped by the viewport instead,
        // which is the same last-column-cut-in-half with a different cause. The pictures shrink
        // rather than the grid losing a column: the grid IS the sheet's, and a row that stopped
        // being a sheet row would put every tooltip's column number somewhere else.
        float room = ImGui.GetMainViewport().WorkSize.X - (style.WindowPadding.X * 4f);
        float picture = Math.Clamp((room - furniture) / across, SmallestCell, CellSize);

        var size = new Vector2(picture, picture);
        float step = picture + (style.FramePadding.Y * 2f) + style.ItemSpacing.Y;
        float content = (across * (picture + (style.FramePadding.X * 2f)))
            + ((across - 1) * style.ItemSpacing.X);

        // Sized to the grid rather than left to ImGui: a child sized from its contents is as
        // tall as 77 rows, which is a popup taller than the screen.
        if (!ImGui.BeginChild(
                "##cells",
                new Vector2(
                    content + (style.WindowPadding.X * 2f) + bar,
                    (VisibleRows * step) + (style.WindowPadding.Y * 2f)),
                ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return chosen;
        }

        try
        {
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
                for (int column = 0; column < across; column++)
                {
                    int at = (row * across) + column;
                    if (at >= shown)
                    {
                        break;
                    }

                    int index = found is null ? at : found[at];

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
            // The NAME first where there is one, because that is what somebody came to read;
            // the cell number stays because half the sheet has no name and the number is what
            // those cells are known by.
            string name = IconNames.For(index + 1);
            ImGui.SetTooltip(
                name.Length > 0
                    ? $"{name}\ncell {index + 1}  (column {index % columns}, row {index / columns})"
                    : $"cell {index + 1}  (column {index % columns}, row {index / columns})");
        }

        return pressed;
    }
}
