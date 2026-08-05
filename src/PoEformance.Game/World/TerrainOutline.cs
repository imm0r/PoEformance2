namespace PoEformance.Game.World;

/// <summary>
/// The area's outline at a size something can actually draw.
/// </summary>
/// <param name="Cells">One byte per pixel: 1 on the boundary, 0 elsewhere.</param>
/// <param name="Step">How many grid cells each pixel covers.</param>
public sealed record OutlineMask(byte[] Cells, int Width, int Height, int Step)
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
    public static OutlineMask Build(TerrainGrid grid, int maxEdge, int thickness = 1)
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

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                cells[row + x] = (byte)(BlockHasBoundary(full, grid.Width, grid.Height, x * step, y * step, step) ? 1 : 0);
            }
        }

        return new OutlineMask(Widen(cells, width, height, thickness), width, height, step);
    }

    /// <summary>Grows the line to the requested width, leaving it centred on the boundary.</summary>
    private static byte[] Widen(byte[] cells, int width, int height, int thickness)
    {
        int radius = Math.Clamp(thickness, 1, 8) - 1;
        if (radius <= 0)
        {
            return cells;
        }

        var widened = new byte[cells.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (cells[(y * width) + x] == 0)
                {
                    continue;
                }

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (int dx = -radius; dx <= radius; dx++)
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
