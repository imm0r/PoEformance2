namespace PoEformance.Features;

/// <summary>
/// How one column's numbers are spread, and how much of a bar a value has earned.
/// </summary>
/// <remarks>
/// THE SCALE IS MEASURED OFF THE TABLE RATHER THAN CHOSEN, because both of the obvious scalings
/// are wrong on this data and the shipped export says so out loud. Over its 2733 monsters:
///
///     life        p10 100   p50 115   p90 250   max 2600    -  984 rows share the value 100
///     maxAggro    p10 105   p50 105   p90 135   max  500    - 2023 rows share the value 105
///     modelSize   p10 100   p50 100   p90 130   max  300    - 1816 rows share the value 100
///
/// SCALED TO THE LARGEST VALUE, the middle eighty percent of life occupies 4% to 10% of the bar.
/// Every ordinary monster is a stub, one boss is full, and the column has said nothing at all
/// about the two and a half thousand rows somebody is actually scrolling past.
///
/// SCALED BY PERCENTILE RANK - the obvious fix, and worse - the ties decide everything. Three
/// quarters of the table share one aggro range, so the value one step above it goes from 0.40 of
/// the bar to 0.78: a difference of fifteen units drawn as half the width, while the difference
/// between 135 and 500 is drawn as almost none. Life does the same from 100 to 102, 0.26 to 0.45.
/// A column that draws a difference of 2 as a fifth of its width is not dense, it is wrong.
///
/// SO THE SCALE IS p90, AND WHAT IS ABOVE IT IS DRAWN FULL AND MARKED. The middle eighty percent
/// of life then occupies 40% to 100% of the bar, which is where the rows are; equal values stay
/// equal, which is the truth about a table where a third of the rows agree; and below the scale a
/// ratio is still a ratio. The 8% that overflow are the rows somebody then reads the NUMBER of -
/// which is printed beside the bar and never replaced by it.
/// </remarks>
public sealed class ColumnSpread
{
    /// <summary>Where the bar fills up. Measured at this fraction of the sorted column.</summary>
    private const double Full = 0.90d;

    private readonly double[] _sorted;

    private ColumnSpread(double[] sorted, double scale, int over)
    {
        _sorted = sorted;
        Scale = scale;
        Over = over;
    }

    /// <summary>A column of nothing, which draws no bars.</summary>
    public static ColumnSpread Empty { get; } = new([], 0d, 0);

    /// <summary>The value at which the bar is full. Zero means this column earns no bars at all.</summary>
    public double Scale { get; }

    /// <summary>How many rows are above <see cref="Scale"/> - the ones drawn full and marked.</summary>
    public int Over { get; }

    /// <summary>How many rows were measured.</summary>
    public int Count => _sorted.Length;

    /// <summary>The smallest value in the column.</summary>
    public double Least => _sorted.Length > 0 ? _sorted[0] : 0d;

    /// <summary>The largest value in the column.</summary>
    public double Most => _sorted.Length > 0 ? _sorted[^1] : 0d;

    /// <summary>Measures a column. The values are copied; the caller's array is left alone.</summary>
    public static ColumnSpread Of(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return Empty;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);

        // A COLUMN WITH NO SPREAD EARNS NO BARS. Every row identical draws as a wall of full bars,
        // which carries not one bit of information and reads as "all of these are high".
        if (sorted[0] >= sorted[^1])
        {
            return new ColumnSpread(sorted, 0d, 0);
        }

        double scale = At(sorted, Full);

        // p90 OF A MOSTLY-EMPTY COLUMN IS ZERO, and the few rows that do carry a value are exactly
        // the ones worth seeing. Falling back to the largest keeps them drawn rather than switching
        // the whole column off for being mostly nothing.
        if (scale <= 0d)
        {
            scale = sorted[^1];
        }

        if (scale <= 0d)
        {
            return new ColumnSpread(sorted, 0d, 0);
        }

        var over = 0;
        for (int at = sorted.Length - 1; at >= 0 && sorted[at] > scale; at--)
        {
            over++;
        }

        return new ColumnSpread(sorted, scale, over);
    }

    /// <summary>The value a fraction of the way up the sorted column.</summary>
    public double Percentile(double fraction) => _sorted.Length == 0 ? 0d : At(_sorted, fraction);

    /// <summary>
    /// How much of the bar this value has earned, from none of it to all of it.
    /// </summary>
    /// <remarks>
    /// NOTHING FOR A VALUE AT OR BELOW ZERO, which is not the same as having no bar to draw: a
    /// monster with no life multiplier and a column that cannot be encoded are different facts,
    /// and only the second one is <see cref="Scale"/> being zero.
    /// </remarks>
    public float Bar(double value)
    {
        if (Scale <= 0d || value <= 0d)
        {
            return 0f;
        }

        return value >= Scale ? 1f : (float)(value / Scale);
    }

    private static double At(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)(fraction * sorted.Length), 0, sorted.Length - 1)];
}
