using System.Text.Json.Serialization;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// The area's outline, packed small enough to hand to a page.
/// </summary>
/// <param name="Step">Grid cells per pixel, so a position can be converted back.</param>
/// <param name="Runs">
/// Alternating run lengths over the pixels, starting with EMPTY. An outline is almost
/// entirely empty space, so the runs are long and few - a dungeon that would be a megabyte
/// of bytes becomes a few thousand numbers.
/// </param>
public sealed record MapLayout(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("runs")] IReadOnlyList<int> Runs)
{
    /// <summary>Nothing to draw - terrain has not loaded yet.</summary>
    public static MapLayout Empty { get; } = new(0, 0, 1, []);

    /// <summary>How many pixels the runs describe. Should equal width times height.</summary>
    public int PixelCount
    {
        get
        {
            int total = 0;
            foreach (int run in Runs)
            {
                total += run;
            }

            return total;
        }
    }

    /// <summary>
    /// Builds the layout from a walkable grid.
    /// </summary>
    /// <param name="maxEdge">
    /// Longest edge in pixels. Small on purpose: this is drawn in a panel a few hundred
    /// pixels wide, and sending more detail than can be shown costs a bigger message for a
    /// picture nobody can see the difference in.
    /// </param>
    public static MapLayout From(TerrainGrid grid, int maxEdge = 768)
    {
        ArgumentNullException.ThrowIfNull(grid);
        OutlineMask mask = TerrainOutline.Build(grid, maxEdge);

        var runs = new List<int>();
        byte current = 0; // runs always start with empty, so a leading 0 may be emitted
        int length = 0;

        foreach (byte cell in mask.Cells)
        {
            byte value = cell != 0 ? (byte)1 : (byte)0;
            if (value == current)
            {
                length++;
                continue;
            }

            runs.Add(length);
            current = value;
            length = 1;
        }

        runs.Add(length);
        return new MapLayout(mask.Width, mask.Height, mask.Step, runs);
    }

    /// <summary>Expands the runs back into one byte per pixel. For tests and for reading it back.</summary>
    public byte[] ToMask()
    {
        var cells = new byte[Width * Height];
        int index = 0;
        byte value = 0;

        foreach (int run in Runs)
        {
            int end = Math.Min(index + run, cells.Length);
            if (value != 0)
            {
                for (int i = index; i < end; i++)
                {
                    cells[i] = 1;
                }
            }

            index = end;
            value = (byte)(value == 0 ? 1 : 0);
        }

        return cells;
    }
}
