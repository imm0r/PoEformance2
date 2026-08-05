using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The layout that travels to the config page: it has to survive the trip unchanged, and
/// it has to be small enough to make in the first place.
/// </summary>
public class MapLayoutTests
{
    private static TerrainGrid Grid(params string[] rows)
    {
        int width = rows[0].Length;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (rows[y][x] == '.')
                {
                    cells[(y * stride) + (x / 2)] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
                }
            }
        }

        return new TerrainGrid(cells, stride, rows.Length);
    }

    [Fact]
    public void RunsRoundTripToTheSameOutline()
    {
        // The whole contract: what the page reconstructs is what the host encoded. A
        // half-right map is worse than none, because it still looks like a map.
        TerrainGrid grid = Grid(
            "#####",
            "#...#",
            "#.#.#",
            "#...#",
            "#####");

        MapLayout layout = MapLayout.From(grid, maxEdge: 64);

        Assert.Equal(grid.BuildOutline(), layout.ToMask());
        Assert.Equal(grid.Width, layout.Width);
        Assert.Equal(grid.Height, layout.Height);
        Assert.Equal(1, layout.Step);
    }

    [Fact]
    public void RunsCoverEveryPixelExactly()
    {
        // An encoding that is one pixel short shifts the entire rest of the image, which
        // shows up as a map that is subtly sheared rather than obviously broken.
        MapLayout layout = MapLayout.From(Grid("#.#.", ".##.", "...#"), maxEdge: 64);

        Assert.Equal(layout.Width * layout.Height, layout.PixelCount);
    }

    [Fact]
    public void RunsStartWithEmpty_EvenWhenThePixelIsSet()
    {
        // The decoder alternates from empty, so an image whose first pixel is set has to
        // begin with a zero-length run. Dropping it inverts the whole picture.
        MapLayout layout = MapLayout.From(Grid("..", ".."), maxEdge: 64);

        Assert.Equal(0, layout.Runs[0]);
        Assert.Equal(4, layout.PixelCount);
        Assert.All(layout.ToMask(), cell => Assert.Equal(1, cell));
    }

    [Fact]
    public void ABigAreaIsThinnedToFit()
    {
        // A real grid is thousands of cells across and this is drawn in a panel a few
        // hundred pixels wide, so it is thinned before it is sent, not after.
        var rows = new string[600];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string(y is 0 or 599 ? '#' : '.', 600);
        }

        MapLayout layout = MapLayout.From(Grid(rows), maxEdge: 100);

        Assert.True(layout.Width <= 100, $"width {layout.Width}");
        Assert.True(layout.Height <= 100, $"height {layout.Height}");
        Assert.True(layout.Step > 1);
        Assert.Equal(layout.Width * layout.Height, layout.PixelCount);
    }

    [Fact]
    public void ThinningKeepsTheOutlineConnected()
    {
        // A one-cell boundary is exactly what point-sampling loses. Taking any set cell in
        // the block is what keeps the line solid instead of dashed.
        var rows = new string[40];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = y is 0 or 39 ? new string('#', 40) : "#" + new string('.', 38) + "#";
        }

        MapLayout layout = MapLayout.From(Grid(rows), maxEdge: 10);
        byte[] mask = layout.ToMask();

        // Every row of the thinned image must still carry part of the wall.
        for (int y = 0; y < layout.Height; y++)
        {
            bool any = false;
            for (int x = 0; x < layout.Width && !any; x++)
            {
                any = mask[(y * layout.Width) + x] != 0;
            }

            Assert.True(any, $"row {y} lost the outline");
        }
    }

    [Fact]
    public void AnEmptyAreaEncodesToNothing()
    {
        MapLayout layout = MapLayout.From(Grid("####", "####"), maxEdge: 64);

        Assert.All(layout.ToMask(), cell => Assert.Equal(0, cell));
        Assert.Equal(layout.Width * layout.Height, layout.PixelCount);
    }
}
