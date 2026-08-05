namespace PoEformance.Game.World;

/// <summary>
/// The area's outline at a size something can actually draw.
/// </summary>
/// <param name="Cells">One byte per pixel: 1 on the boundary, 0 elsewhere.</param>
/// <param name="Step">How many grid cells each pixel covers.</param>
/// <param name="LeanCells">
/// How far the drawn line sits from the boundary it describes, in grid cells.
/// </param>
/// <remarks>
/// The lean exists because an EVEN number of pixels cannot be centred on one: the widened
/// line has to take one more pixel on one side than the other, so its centre lands half a
/// pixel off the boundary - and at a thinning step of two, half a pixel is a whole grid cell,
/// in a fixed direction, everywhere on the map.
///
/// Reported rather than hidden so whoever DRAWS it can put it back, which is the only place
/// the correction can be made without changing what the pixels mean.
/// </remarks>
public sealed record OutlineMask(byte[] Cells, int Width, int Height, int Step, float LeanCells = 0f)
{
    /// <summary>True when this pixel is on the boundary.</summary>
    public bool IsSet(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height && Cells[(y * Width) + x] != 0;
}

/// <summary>
/// Reduces a walkable grid to a drawable outline.
/// </summary>
/// <remarks>
/// Shared between the in-game overlay, which uploads it as a texture, and the config page,
/// which sends it over the bridge. Both need the same thinning, and two copies of it would
/// eventually disagree about what the map looks like.
/// </remarks>
public static class TerrainOutline
{
    /// <summary>
    /// Builds the outline, thinned until it fits within <paramref name="maxEdge"/>.
    /// </summary>
    /// <remarks>
    /// A block is marked when ANY cell in it is on the boundary. Sampling one cell in N
    /// instead would break the line into dashes - a one-cell-wide boundary is exactly the
    /// thing point-sampling loses - and a dashed outline reads as a damaged map rather than
    /// a smaller one.
    /// </remarks>
    /// <param name="thickness">
    /// How many pixels wide the line is drawn. Applied AFTER thinning, so it means the same
    /// on screen whatever the area's size - thickening before would be scaled away again on
    /// a large map and doubled on a small one.
    /// </param>
    /// <param name="isoHeightShift">
    /// Bakes each cell's ground height into the picture as a diagonal displacement, so a
    /// perfectly FLAT drawing of it shows every wall at its real elevation - see
    /// <see cref="TerrainGrid.IsoHeightShift"/>. For the isometric overlay only: the config
    /// page draws the same outline from directly above, where a height means nothing and this
    /// would only skew it.
    /// </param>
    public static OutlineMask Build(
        TerrainGrid grid, int maxEdge, int thickness = 1, bool isoHeightShift = false)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEdge, 1);

        int step = 1;
        while (grid.Width / step > maxEdge || grid.Height / step > maxEdge)
        {
            step++;
        }

        int width = Math.Max(1, grid.Width / step);
        int height = Math.Max(1, grid.Height / step);
        byte[] full = grid.BuildOutline();
        var cells = new byte[width * height];

        // Scattered from the cells rather than gathered per block, because a height shift
        // MOVES a cell: where it ends up cannot be known from the block it started in. With
        // no shift the two are the same thing - a block is marked when any of its cells is on
        // the boundary, which is what keeps a one-cell line from thinning into dashes.
        for (int y = 0; y < grid.Height; y++)
        {
            int row = y * grid.Width;
            for (int x = 0; x < grid.Width; x++)
            {
                if (full[row + x] == 0)
                {
                    continue;
                }

                int shift = isoHeightShift ? grid.IsoHeightShift(x, y) : 0;
                int bx = (x - shift) / step;
                int by = (y - shift) / step;

                // Off the picture: a wall high enough to displace past the edge is dropped
                // rather than wrapped, which would draw it somewhere it is not.
                if (x - shift >= 0 && y - shift >= 0 && bx < width && by < height)
                {
                    cells[(by * width) + bx] = 1;
                }
            }
        }

        // An even width cannot be centred on a pixel, so the widened line leans by half of
        // one - which at a thinning step of two is a whole grid cell, in a fixed direction,
        // everywhere. Reported so the drawing can put it back.
        float lean = Math.Clamp(thickness, 1, 8) % 2 == 0 ? 0.5f * step : 0f;

        return new OutlineMask(Widen(cells, width, height, thickness), width, height, step, lean);
    }

    /// <summary>
    /// Grows the line to exactly <paramref name="thickness"/> pixels.
    /// </summary>
    /// <remarks>
    /// The window is thickness wide, NOT a radius around each pixel. A radius grows the line
    /// in both directions, so it can only ever produce odd widths - 1, 3, 5 - and a setting
    /// labelled 1 to 6 that actually steps 1, 3, 5, 7 skips exactly the value most people
    /// want. Even widths cannot be centred on a pixel, so they lean by half a pixel; that is
    /// what makes 2 available at all.
    /// </remarks>
    private static byte[] Widen(byte[] cells, int width, int height, int thickness)
    {
        int span = Math.Clamp(thickness, 1, 8);
        if (span <= 1)
        {
            return cells;
        }

        int from = -((span - 1) / 2);
        int to = from + span - 1;

        var widened = new byte[cells.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (cells[(y * width) + x] == 0)
                {
                    continue;
                }

                for (int dy = from; dy <= to; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (int dx = from; dx <= to; dx++)
                    {
                        int nx = x + dx;
                        if (nx >= 0 && nx < width)
                        {
                            widened[(ny * width) + nx] = 1;
                        }
                    }
                }
            }
        }

        return widened;
    }

    private static bool BlockHasBoundary(byte[] full, int gridWidth, int gridHeight, int x0, int y0, int step)
    {
        for (int y = y0; y < y0 + step && y < gridHeight; y++)
        {
            int row = y * gridWidth;
            for (int x = x0; x < x0 + step && x < gridWidth; x++)
            {
                if (full[row + x] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
