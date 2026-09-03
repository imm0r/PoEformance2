using System.Text;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Opens the ROOM files of the current area and says what is in them.
/// </summary>
/// <remarks>
/// WHY THIS IS THE REMAINING WAY. An area is assembled from rooms - files under a
/// <c>Rooms/</c> directory, ending <c>.arm</c> - and three recordings established that the
/// game does not keep the placement anywhere this tool can see it: no tile of 2336 references
/// a room by any slot, one hop, or through the vector it carries, and the terrain struct's
/// first 0x400 bytes hold the tile array, a per-corner array, and the ground types, but no
/// room. What memory DOES have is which rooms the area loaded, because the file table names
/// them - and the game's own bundles have the files themselves.
///
/// So the question this asks is the one that decides whether the whole approach works: does a
/// room file say which TILES it is built from? If it does, the layout can be recovered without
/// a single new offset - the rooms of the area are known, their tile patterns would be known,
/// and the tile grid is already read - and if it does not, that is worth knowing before
/// anybody builds on the idea.
///
/// It reports rather than parses, deliberately. Nobody here has seen one of these files, and a
/// parser written against a guess about the format is how an evening disappears; the shape,
/// the size and whether tile names appear in it are what a person needs to decide what to
/// write next.
/// </remarks>
public static class RoomFiles
{
    /// <summary>How many rooms to open. Enough to see a pattern, few enough to read.</summary>
    private const int MostRooms = 6;

    /// <summary>How much of a file to show, when it turns out to be readable text.</summary>
    private const int PreviewChars = 200;

    /// <summary>The extension the game gives a room.</summary>
    public const string RoomExtension = ".arm";

    /// <summary>The extension it gives a tile - what a room would be built from.</summary>
    public const string TileExtension = ".tdt";

    /// <summary>
    /// Describes the room files among a set of loaded paths.
    /// </summary>
    /// <param name="loaded">Everything the area loaded, as the file table named it.</param>
    /// <param name="read">
    /// Reads one path out of the game's own files, or returns null. Handed in rather than
    /// taken as a dependency: this walks and reports, and where the bytes come from is the
    /// App's business - which also lets a test answer with bytes of its own.
    /// </param>
    public static IReadOnlyList<string> Describe(
        IEnumerable<string> loaded, Func<string, byte[]?> read, int most = MostRooms)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(read);

        List<string> rooms =
            [.. loaded.Where(p => p.EndsWith(RoomExtension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];

        if (rooms.Count == 0)
        {
            return ["no room files among the loaded paths - either this area has none, or the"
                + " list is not the current area's"];
        }

        var lines = new List<string> { $"{rooms.Count} room files loaded for this area" };

        foreach (string room in rooms.Take(most))
        {
            byte[]? content = null;
            try
            {
                content = read(room);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or NotSupportedException or UnauthorizedAccessException)
            {
                lines.Add($"  {Name(room)}  could not be read: {exception.Message}");
                continue;
            }

            lines.Add(content is null || content.Length == 0
                ? $"  {Name(room)}  not in the bundles"
                : $"  {Name(room)}  {Describe(content)}");
        }

        if (rooms.Count > most)
        {
            lines.Add($"  ...and {rooms.Count - most} more");
        }

        return lines;
    }

    /// <summary>
    /// What one file turned out to be: its size, its shape, and whether it names tiles.
    /// </summary>
    /// <remarks>
    /// THE TILE COUNT IS THE ANSWER the whole diagnostic exists for. A room file that mentions
    /// .tdt paths is a room file that says what it is built from, and that is the thing the
    /// layout could be recovered from; one that mentions none is a dead end whatever else it
    /// contains.
    /// </remarks>
    private static string Describe(byte[] content)
    {
        string text = Encoding.UTF8.GetString(content);
        bool readable = Printable(content);
        int tiles = Mentions(text, TileExtension);

        string what = $"{content.Length} bytes, {(readable ? "text" : "binary")}"
            + $", {tiles} mentions of {TileExtension}";

        // The bytes themselves when they are text, and the first line of them when they are
        // not - a preview of binary as characters is noise, and a length plus a verdict is
        // what a person can act on.
        return readable
            ? $"{what}\n      {Preview(text)}"
            : what + (tiles > 0 ? $"\n      {Preview(Readable(text))}" : string.Empty);
    }

    /// <summary>True when the bytes read as text rather than as a compiled file.</summary>
    /// <remarks>
    /// Nine in ten printable, not all of them: PoE's text formats end lines with a mix of
    /// terminators and carry the odd byte-order mark, and demanding purity would call every
    /// one of them binary.
    /// </remarks>
    private static bool Printable(byte[] content)
    {
        int printable = 0;
        foreach (byte value in content)
        {
            if (value is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                printable++;
            }
        }

        return content.Length > 0 && printable * 10 >= content.Length * 9;
    }

    /// <summary>How often a string appears, which is what says the file references tiles.</summary>
    private static int Mentions(string text, string what)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += what.Length;
        }

        return count;
    }

    /// <summary>The printable runs of a binary file, which is where its paths would be.</summary>
    private static string Readable(string text)
    {
        var kept = new StringBuilder();
        foreach (char value in text)
        {
            kept.Append(value is >= ' ' and < (char)127 ? value : ' ');
        }

        return kept.ToString();
    }

    /// <summary>One line of a file, short enough to sit in a readout.</summary>
    private static string Preview(string text)
    {
        string flat = text.ReplaceLineEndings(" | ").Trim();
        return flat.Length <= PreviewChars ? flat : flat[..PreviewChars] + "...";
    }

    /// <summary>The file's own name, since the directory is the same for all of them.</summary>
    private static string Name(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
