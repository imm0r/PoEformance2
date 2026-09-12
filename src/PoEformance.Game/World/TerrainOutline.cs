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
/// The walkable floor at a coarse step, ready to be drawn as a sheet under the outline.
/// </summary>
/// <param name="Coverage">One byte per pixel, 0 to 255: how much of the pixel is floor.</param>
/// <param name="Source">
/// Per pixel, the index - into a grid of this same width - of the coarse cell whose ground
/// landed in it, or -1 where none did. NOT the pixel's own index: a cell is displaced by its
/// height in the picture, and whoever asks whether the ground has been walked has to ask
/// about where the ground is, not where the picture shows it. See TerrainOutline.Floor.
/// </param>
/// <param name="Step">Grid cells to a side of one pixel.</param>
public sealed record FloorPlan(byte[] Coverage, int[] Source, int Width, int Height, int Step);

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
    /// The walkable floor as a picture of its own, <paramref name="step"/> cells to a pixel.
    /// </summary>
    /// <remarks>
    /// The FILL under the line: the floor as a sheet, the way the game's own map draws the
    /// parts it has revealed. Displaced by height exactly as the line's cells are, so the
    /// sheet ends where the line is drawn - at a different height it would peel away from
    /// its own edge on every slope.
    ///
    /// A COVERAGE rather than a flag, because a pixel holds several cells and the edge of the
    /// floor runs through some of them. Counting gives those pixels a proportionate alpha,
    /// which is the edge anti-aliased for nothing; a flag would give them the full sheet and
    /// grow the floor by up to a pixel all round.
    ///
    /// And per pixel, WHERE THE GROUND CAME FROM: the coarse cell, at the same step, that the
    /// cells landing in the pixel belong to. The fill retreats as the map is walked, and
    /// "walked" is recorded against the ground's own position while the picture shows the
    /// ground displaced by its height - on a hill those are tens of cells apart, and a hole
    /// looked up at the picture's position would open beside the player rather than around
    /// them. The first cell to land in a pixel names it; where cells of two heights share one,
    /// that is a pixel's worth of error along a cliff, which nobody can see.
    ///
    /// Every walkable cell is visited, where the outline visits only the boundary, and the
    /// height lookup is the expensive part of a visit. Without sub-tile heights every cell of
    /// a tile has the tile's height, so it is looked up once per tile per row there and only
    /// per cell where the slope inside a tile is actually known. Once per area, on the render
    /// thread, like the outline.
    /// </remarks>
    public static FloorPlan Floor(TerrainGrid grid, int step, bool isoHeightShift)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);

        // Rounded UP, unlike the outline's thinning: the last partial column of cells is
        // floor too, and a sheet that stopped a few cells short of the wall would show it.
        int width = Math.Max(1, (grid.Width + step - 1) / step);
        int height = Math.Max(1, (grid.Height + step - 1) / step);
        var counts = new ushort[width * height];
        var source = new int[width * height];
        Array.Fill(source, -1);

        bool perCell = isoHeightShift && grid.HasSubTileHeights;
        bool perTile = isoHeightShift && !perCell && grid.HasHeights;

        for (int y = 0; y < grid.Height; y++)
        {
            int shift = 0;
            int tileEnd = -1;
            int fromRow = (y / step) * width;
            for (int x = 0; x < grid.Width; x++)
            {
                if (!grid.IsWalkable(x, y))
                {
                    continue;
                }

                if (perCell)
                {
                    shift = grid.IsoHeightShift(x, y);
                }
                else if (perTile && x >= tileEnd)
                {
                    shift = grid.IsoHeightShift(x, y);
                    tileEnd = ((x / TerrainGrid.CellsPerTile) + 1) * TerrainGrid.CellsPerTile;
                }

                // Displaced exactly as the outline's cells are, and dropped at the picture's
                // edge for the same reason - see Build.
                int sx = x - shift;
                int sy = y - shift;
                if (sx < 0 || sy < 0)
                {
                    continue;
                }

                int bx = sx / step;
                int by = sy / step;
                if (bx >= width || by >= height)
                {
                    continue;
                }

                int at = (by * width) + bx;
                if (counts[at] != ushort.MaxValue)
                {
                    counts[at]++;
                }

                if (source[at] < 0)
                {
                    source[at] = fromRow + (x / step);
                }
            }
        }

        // Cells from two heights can land in one pixel where the ground steps up, so the
        // count can exceed a full block; a pixel is never more than entirely floor.
        int full = step * step;
        var coverage = new byte[counts.Length];
        for (int i = 0; i < counts.Length; i++)
        {
            int count = counts[i];
            if (count != 0)
            {
                coverage[i] = (byte)Math.Min(255, ((count * 255) + (full / 2)) / full);
            }
        }

        return new FloorPlan(coverage, source, width, height, step);
    }

    /// <summary>
    /// The mask grown by <paramref name="pixels"/> on every side - the line plus a rim around it.
    /// </summary>
    /// <remarks>
    /// For a contrast rim: a single-colour line is invisible on any ground that happens to
    /// match it, and no colour is far from both a sunlit rock and a cave floor. A dark rim
    /// is what makes the line readable on either; the drawing puts it under the line by
    /// colouring these pixels dark where the line's own are not set.
    ///
    /// Grown from the WIDENED line rather than re-widened from the thin one, so it is the
    /// same shape a little larger whatever the width - and symmetric, so the lean the line
    /// already reports still holds for the pair.
    ///
    /// A square dilation done as two passes, one along the rows and one down the columns,
    /// which is the same result with work proportional to 2(2r+1) per SET pixel instead of
    /// (2r+1)^2 - and the set pixels are a thin line through a texture of a few million,
    /// built once per area on the render thread.
    /// </remarks>
    public static byte[] Rim(OutlineMask mask, int pixels)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 1);

        int width = mask.Width;
        int height = mask.Height;
        byte[] cells = mask.Cells;

        // Horizontal pass: each set source pixel marks the r pixels either side of it.
        var rows = new byte[cells.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (cells[row + x] == 0)
                {
                    continue;
                }

                int from = Math.Max(0, x - pixels);
                int to = Math.Min(width - 1, x + pixels);
                rows.AsSpan(row + from, to - from + 1).Fill(1);
            }
        }

        // Vertical pass over that, which completes the square neighbourhood. Row-major
        // again, marking the rows below and above a set pixel, so the memory access stays
        // sequential instead of striding down a column.
        var rim = new byte[cells.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int firstRow = Math.Max(0, y - pixels);
            int lastRow = Math.Min(height - 1, y + pixels);
            for (int x = 0; x < width; x++)
            {
                if (rows[row + x] == 0)
                {
                    continue;
                }

                for (int ny = firstRow; ny <= lastRow; ny++)
                {
                    rim[(ny * width) + x] = 1;
                }
            }
        }

        return rim;
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
