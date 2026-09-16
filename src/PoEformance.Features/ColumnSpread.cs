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

    /// <summary>Where the histogram stops, so that one outlier cannot flatten the rest of it.</summary>
    private const double Edge = 0.99d;

    /// <summary>The most bins a histogram is drawn with.</summary>
    private const int MostBins = 24;

    private readonly double[] _sorted;

    private ColumnSpread(double[] sorted, double scale, int over, int[] bins, double bottom, double width)
    {
        _sorted = sorted;
        Scale = scale;
        Over = over;
        Bins = bins;
        Bottom = bottom;
        Width = width;

        foreach (int count in bins)
        {
            if (count > Tallest)
            {
                Tallest = count;
            }
        }
    }

    /// <summary>A column of nothing, which draws no bars.</summary>
    public static ColumnSpread Empty { get; } = new([], 0d, 0, [], 0d, 0d);

    /// <summary>
    /// How many rows fall in each bin, for the histogram in the column's header.
    /// </summary>
    /// <remarks>
    /// THE BIN COUNT IS MEASURED TOO, and it has to be: twenty-four bins over the modifier column,
    /// which runs 0 to 8, leaves six of them holding anything and the rest empty - a comb, not a
    /// distribution. Where the values are whole numbers and there are fewer of them than there are
    /// bins, each value gets its own. The skills column goes from a comb to twenty-one clean bars
    /// the same way.
    ///
    /// AND IT STOPS AT p99 with the tail folded into the last bin, for the reason the maximum is
    /// not the bar's scale either: life runs to 2600 and ten of twenty-four bins would hold nothing
    /// at all. What is above the edge is still counted, at the end, so nothing goes missing.
    /// </remarks>
    public int[] Bins { get; }

    /// <summary>The value the first bin starts at.</summary>
    public double Bottom { get; }

    /// <summary>How much of the value range one bin covers.</summary>
    public double Width { get; }

    /// <summary>The tallest bin, which is what a drawing scales itself against.</summary>
    public int Tallest { get; }

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
            return new ColumnSpread(sorted, 0d, 0, [], sorted[0], 0d);
        }

        double scale = At(sorted, Full);
        (int[] bins, double bottom, double width) = Histogram(sorted);

        // p90 OF A MOSTLY-EMPTY COLUMN IS ZERO, and the few rows that do carry a value are exactly
        // the ones worth seeing. Falling back to the largest keeps them drawn, and the rows holding
        // nothing still draw nothing, which is the truth about them.
        if (scale <= 0d)
        {
            scale = sorted[^1];
        }

        // A COLUMN MOST OF WHOSE ROWS WOULD DRAW A FULL BAR EARNS NO BARS AT ALL, and poise is why:
        // 2618 of 2733 monsters carry exactly 0.05 and the rest run to 0.25, so a bar scaled at its
        // ninetieth percentile fills for ninety-six percent of the table. That is the wall of full
        // bars this class exists to avoid, reached from the other side - and a bar measured from
        // zero cannot show variation that only begins well above it. Asked as "how many rows would
        // be full" rather than as anything about the minimum, because a handful of rows carrying
        // nothing at all is enough to hide the problem from any test that looks at the bottom.
        // The histogram is kept: what is really there is still worth seeing.
        var full = 0;
        for (int at = sorted.Length - 1; at >= 0 && sorted[at] >= scale; at--)
        {
            full++;
        }

        if (scale <= 0d || full * 2 > sorted.Length)
        {
            return new ColumnSpread(sorted, 0d, 0, bins, bottom, width);
        }

        var over = 0;
        for (int at = sorted.Length - 1; at >= 0 && sorted[at] > scale; at--)
        {
            over++;
        }

        return new ColumnSpread(sorted, scale, over, bins, bottom, width);
    }

    /// <summary>Bins the column, at a bin width the values themselves choose.</summary>
    private static (int[] Bins, double Bottom, double Width) Histogram(double[] sorted)
    {
        double bottom = sorted[0];
        double top = At(sorted, Edge);
        double span = top - bottom;

        if (span <= 0d)
        {
            span = sorted[^1] - bottom;
        }

        if (span <= 0d)
        {
            return ([], bottom, 0d);
        }

        int count = MostBins;
        double width = span / count;

        // WHOLE NUMBERS GET WHOLE BINS, AND NO NARROWER THAN THE COLUMN'S OWN STEP. A bin narrower
        // than the gap between two values a column can actually hold is a bin nothing can ever fall
        // into, and a row of them reads as structure that is not there. Two measurements over the
        // export: the modifier column runs 0 to 8, so twenty-four bins leave eighteen empty; the
        // damage-spread column only ever holds multiples of ten, so sixteen bins of two leave
        // twelve empty. The first is fixed by rounding the width up to 1, the second by rounding it
        // up to what every value in the column is a multiple of.
        if (Whole(sorted))
        {
            width = Math.Max(Math.Ceiling((span + 1d) / count), Step(sorted));
            count = Math.Max(1, (int)Math.Ceiling((span + 1d) / width));
        }

        var bins = new int[count];
        foreach (double value in sorted)
        {
            int at = value <= bottom ? 0 : (int)((value - bottom) / width);
            bins[Math.Clamp(at, 0, count - 1)]++;
        }

        return (bins, bottom, width);
    }

    /// <summary>
    /// The smallest step the column's values actually move in - their greatest common divisor.
    /// </summary>
    /// <remarks>
    /// ONE VALUE OFF THE GRID COLLAPSES IT TO 1, which is the right answer rather than a weakness:
    /// a column of multiples of ten with a single 27 in it cannot be binned in tens without putting
    /// that row somewhere it does not belong.
    /// </remarks>
    private static long Step(double[] sorted)
    {
        long step = 0;
        long first = (long)sorted[0];

        foreach (double value in sorted)
        {
            step = Gcd(step, Math.Abs((long)value - first));
            if (step == 1)
            {
                return 1;
            }
        }

        return Math.Max(1, step);
    }

    private static long Gcd(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static bool Whole(double[] sorted)
    {
        foreach (double value in sorted)
        {
            if (value != Math.Floor(value))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// The range a drag across the histogram picked out, as values.
    /// </summary>
    /// <remarks>
    /// SNAPPED TO BIN EDGES AND OPENED OUT AT EITHER END. A drag that starts at the left edge means
    /// "everything below this", not "everything from exactly the smallest value" - and the last bin
    /// holds the tail the histogram stops short of, so a drag ending in it has to reach the largest
    /// value in the column rather than the edge of the picture. Otherwise selecting the right-hand
    /// bar would quietly drop the very rows it looks like it is selecting.
    /// </remarks>
    public (double Least, double Most) Range(double from, double to)
    {
        if (Bins.Length == 0 || Width <= 0d)
        {
            return (Least, Most);
        }

        double low = Math.Clamp(Math.Min(from, to), 0d, 1d);
        double high = Math.Clamp(Math.Max(from, to), 0d, 1d);

        int first = Math.Clamp((int)(low * Bins.Length), 0, Bins.Length - 1);
        int last = Math.Clamp((int)(high * Bins.Length), first, Bins.Length - 1);

        return (
            first == 0 ? Least : Bottom + (first * Width),
            last >= Bins.Length - 1 ? Most : Bottom + ((last + 1) * Width));
    }

    /// <summary>Whether a bin falls inside a range of values, for shading what is picked.</summary>
    public bool Inside(int bin, double least, double most)
    {
        if (Bins.Length == 0 || Width <= 0d || bin < 0 || bin >= Bins.Length)
        {
            return false;
        }

        double low = Bottom + (bin * Width);
        double high = bin == Bins.Length - 1 ? Math.Max(Most, low + Width) : low + Width;
        return high > least && low <= most;
    }

    private static double At(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)(fraction * sorted.Length), 0, sorted.Length - 1)];
}
