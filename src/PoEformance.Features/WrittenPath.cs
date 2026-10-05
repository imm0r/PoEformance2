namespace PoEformance.Features;

/// <summary>
/// Where in a line of status text a file or folder the tool just wrote is named.
/// </summary>
/// <remarks>
/// THE LINES ARE ALREADY WRITTEN EVERYWHERE - "wrote C:\...\cliffcvmstroma1.files.txt", "rooms written
/// to ...", "saved to ..." - by a dozen call sites in their own words, so the path is FOUND in the
/// line rather than handed over separately: every one of them becomes a link without each having
/// to change what it says.
///
/// FOUND BY ASKING THE DISK, not by guessing where a path ends. A Windows path may hold spaces
/// ("C:\Program Files\..."), so no pattern can say where it stops - "nothing in C:\...\exports to
/// send" has no separator at all. So the line is cut at every space, comma and bracket after the
/// path's start and at its end, longest first, and the first cut that names something that exists
/// is the path. One that does not exist yet, such as "writing ... - reading every tileset", is no
/// link until the line says it was written. A handful of disk questions per line, asked once.
/// </remarks>
public static class WrittenPath
{
    /// <summary>
    /// The first absolute path in a line that names something existing, as a start and a length; (0, 0) where none.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="exists">Whether a path names a file or a folder - the disk, outside a test.</param>
    public static (int Start, int Length) Find(string? text, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        int start = Start(text);
        if (start < 0)
        {
            return (0, 0);
        }

        // LONGEST FIRST: the end of the line, then every break before it, back towards the start.
        for (int end = text.Length; end > start + 3; end--)
        {
            if (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or '(' or ';'))
            {
                continue;
            }

            string candidate = text[start..end].TrimEnd(' ', '.', ',', ';');
            if (candidate.Length > 3 && exists(candidate))
            {
                return (start, candidate.Length);
            }
        }

        return (0, 0);
    }

    /// <summary>Where a drive-letter path begins - <c>C:\</c> not inside a longer word - or -1.</summary>
    private static int Start(string text)
    {
        for (var at = 1; at + 1 < text.Length; at++)
        {
            if (text[at] == ':' && text[at + 1] == '\\' && char.IsAsciiLetter(text[at - 1])
                && (at == 1 || !char.IsLetterOrDigit(text[at - 2])))
            {
                return at - 1;
            }
        }

        return -1;
    }
}
