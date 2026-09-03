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
        // BOTH ENCODINGS, and the first version of this only looked at one. Decoding UTF-16 as
        // UTF-8 puts a NUL between every letter, which makes a text file read as binary AND
        // hides every string in it from a search for ASCII - so "binary, 0 mentions of .tdt"
        // came back for all 32 rooms and would have closed the question on an artefact of the
        // check rather than on the file. A test a wrong answer passes is worse than none.
        int tiles = Mentions(content, TileExtension);
        IReadOnlyList<string> strings = Strings(content);
        string shape = Shape(content);

        string what = $"{content.Length} bytes, {shape}, {tiles} mentions of {TileExtension}";

        // WHAT IS ACTUALLY IN IT, which is the thing worth reporting when the answer is no.
        // A room that does not name tiles still names something, and those names are the next
        // question - so they are printed rather than summarised away.
        return strings.Count == 0
            ? what
            : $"{what}\n      {string.Join("  |  ", strings)}";
    }

    /// <summary>Whether the bytes read as ASCII text, UTF-16 text, or neither.</summary>
    private static string Shape(byte[] content)
    {
        int printable = 0;
        int nulEven = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                printable++;
            }
            else if (content[i] == 0 && i % 2 == 1)
            {
                nulEven++;
            }
        }

        if (printable * 10 >= content.Length * 9)
        {
            return "text";
        }

        // Every second byte a NUL, with the others printable, IS a UTF-16 file - and saying
        // "binary" about one is how its contents stay invisible.
        return printable + nulEven >= content.Length * 9 / 10 && nulEven > content.Length / 4
            ? "text (utf-16)"
            : "binary";
    }

    /// <summary>
    /// The printable strings in a file, ASCII or UTF-16, longest first.
    /// </summary>
    /// <remarks>
    /// This is the diagnostic's real payload once the tile question comes back no: a compiled
    /// format still carries the names of what it references, and which names those are decides
    /// where to look next. Longest first because a path is longer than a field name, and paths
    /// are what this is hunting.
    /// </remarks>
    private static IReadOnlyList<string> Strings(byte[] content, int least = 6, int most = 8)
    {
        var found = new List<string>();
        var run = new StringBuilder();

        // ASCII runs.
        foreach (byte value in content)
        {
            if (value is >= 0x20 and < 0x7F)
            {
                run.Append((char)value);
                continue;
            }

            Keep();
        }

        Keep();

        // UTF-16 runs: a printable byte followed by a NUL, repeatedly. BOTH ALIGNMENTS, because
        // a string inside a compiled file sits wherever the writer put it - scanning only even
        // offsets finds nothing at all in a file whose text happens to start on an odd one, and
        // reports that as "no strings" rather than as "not looked at".
        foreach (int start in (int[])[0, 1])
        {
            for (int i = start; i + 1 < content.Length; i += 2)
            {
                if (content[i] is >= 0x20 and < 0x7F && content[i + 1] == 0)
                {
                    run.Append((char)content[i]);
                    continue;
                }

                Keep();
            }

            Keep();
        }

        return [.. found.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).Take(most)];

        void Keep()
        {
            if (run.Length >= least)
            {
                found.Add(run.ToString());
            }

            run.Clear();
        }
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
    private static int Mentions(byte[] content, string what)
        => Occurrences(content, Encoding.UTF8.GetBytes(what))
           + Occurrences(content, Encoding.Unicode.GetBytes(what));

    /// <summary>How often a byte sequence appears in another.</summary>
    private static int Occurrences(byte[] content, byte[] pattern)
    {
        int count = 0;
        for (int i = 0; i + pattern.Length <= content.Length; i++)
        {
            if (content.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                count++;
                i += pattern.Length - 1;
            }
        }

        return count;
    }

    /// <summary>The file's own name, since the directory is the same for all of them.</summary>
    private static string Name(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
