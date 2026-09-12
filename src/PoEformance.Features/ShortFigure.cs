using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// A count written short enough to sit on an icon: 538, 5.4k, 54k, 538k, 1.2M.
/// </summary>
/// <remarks>
/// TWO SIGNIFICANT FIGURES UNDER TEN OF A UNIT AND WHOLE NUMBERS ABOVE, which is how the eye
/// reads a number at a glance: the difference between 5.4k and 5.9k is worth a digit, the
/// difference between 54k and 54.3k is not. Rounded rather than cut, so 53,838 reads 54k -
/// the nearer number, and the one the game's own abbreviations give. A value that rounds up
/// to the next unit is written in that unit (999,600 is 1.0M, not 1000k).
/// </remarks>
public static class ShortFigure
{
    private const string Units = "kMBT";

    /// <summary>Writes the count short. Invariant culture: a point, whatever the machine says.</summary>
    public static string Format(long value)
    {
        if (value < 0)
        {
            return "-" + Format(-value);
        }

        if (value < 1000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        double scaled = value;
        int unit = -1;
        while (scaled >= 999.5 && unit < Units.Length - 1)
        {
            scaled /= 1000;
            unit++;
        }

        // 9.95 rather than 10: what would round to "10.0" is already a whole number's worth.
        string figure = scaled < 9.95
            ? scaled.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(scaled).ToString(CultureInfo.InvariantCulture);

        return figure + Units[unit];
    }
}
