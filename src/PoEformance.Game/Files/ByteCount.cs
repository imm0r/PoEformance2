using System.Globalization;

namespace PoEformance.Game.Files;

/// <summary>
/// A byte count as a person reads it at a glance.
/// </summary>
/// <remarks>
/// ONE SPELLING FOR EVERY PLACE THAT SAYS A SIZE. The survey's report and the line under the model
/// pane read the same bytes the same way, so "12.0 MB" in one is "12.0 MB" in the other. Binary
/// units, as the survey has always reported them.
/// </remarks>
public static class ByteCount
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>"512 B", "48.0 KB", "3.4 MB": whole bytes below a kilobyte, one decimal above.</summary>
    public static string Said(long count)
    {
        if (count < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count} B");
        }

        double size = count;
        foreach (string unit in Units)
        {
            size /= 1024d;
            if (size < 1024d || string.Equals(unit, Units[^1], StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{size:F1} {unit}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{count} B");
    }
}
