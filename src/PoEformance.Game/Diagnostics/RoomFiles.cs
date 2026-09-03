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
    /// The extensions a room DOES name, which is why the dump follows them.
    /// </summary>
    /// <remarks>
    /// Thirty-two rooms of one area named not a single tile between them. What each one names
    /// instead is its edge type and its ground type - <c>bones_edge.et</c>, <c>bone_fill.gt</c> -
    /// and those live in the same directories as the tiles do. So whether the layout can be
    /// recovered comes down to whether one of THESE lists its tiles, and the cheapest way to
    /// find out is to write them out beside the rooms that named them.
    /// </remarks>
    public static readonly string[] TypeExtensions = [".et", ".gt"];

    /// <summary>The verdict that decides how a file is decoded, named so it cannot drift.</summary>
    private const string Utf16 = "text (utf-16)";

    /// <summary>How much of a file that is not text to show. Enough for a header and a magic.</summary>
    private const int PreviewBytes = 512;

    /// <summary>How many strings to list out of one that is not text. The names are the payload.</summary>
    private const int PreviewStrings = 64;

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

    /// <summary>
    /// Every room file of an area, decoded, as one document.
    /// </summary>
    /// <remarks>
    /// WHAT THE READOUT CANNOT DO. A room turned out to be a grid of characters with a list of
    /// ground and edge types beside it, and the diagnostic's eight longest strings are enough
    /// to establish that and nothing more: the grid's dimensions, its alphabet and how it
    /// refers to those types are all questions about the whole file. Reading one on the machine
    /// running the game is not how anybody answers them - so this writes them out, the way the
    /// loaded-file list already goes out, and the format can be worked out away from the game.
    ///
    /// ALL of them rather than one, because the variation between rooms is itself the evidence:
    /// a field that is constant across thirty-two files is a header, and one that tracks the
    /// grid's size is a dimension.
    ///
    /// AND ONE LEVEL DEEPER, which is what the first dump turned out to need. Thirty-two rooms
    /// named not a single tile between them - only their edge and ground types - so the chain
    /// room-to-tile now hangs entirely on what one of THOSE contains. They are named right there
    /// in the room text and read from the same bundles, so following them costs one pass and
    /// settles the question either way.
    /// </remarks>
    /// <returns>The file written, or null when it could not be.</returns>
    public static string? Dump(
        uint area, IEnumerable<string> loaded, Func<string, byte[]?> read, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(read);

        List<string> rooms =
            [.. loaded.Where(p => p.EndsWith(RoomExtension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];

        if (rooms.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        var referenced = new SortedSet<string>(StringComparer.Ordinal);

        text.AppendLine($"# area {area}");
        text.AppendLine($"# {rooms.Count} room files");
        text.AppendLine("#");

        foreach (string room in rooms)
        {
            Append(room, referenced);
        }

        // The types the rooms named, in the same document. One file to send rather than two,
        // because the question is the RELATION between them: which rooms declared this type,
        // and does the type name the tiles they would not.
        if (referenced.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"# {referenced.Count} type files declared by the rooms above");
            text.AppendLine("#");

            foreach (string type in referenced)
            {
                Append(type, into: null);
            }
        }

        try
        {
            // Beside the loaded-file dump and named the same way, because they are two halves
            // of one capture: area-<n>.txt says what loaded, rooms-<n>.txt says what is in it.
            string file = path
                ?? Path.Combine(AppContext.BaseDirectory, "preloads", $"rooms-{area}.txt");

            string? folder = Path.GetDirectoryName(file);
            if (folder is { Length: > 0 })
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(file, text.ToString());
            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }

        // One file into the document, and - when asked - what that file declares. Shared by both
        // passes so a type file is reported exactly as a room is; the shapes differ and being
        // able to compare them at a glance is the point.
        void Append(string what, SortedSet<string>? into)
        {
            text.AppendLine();
            text.AppendLine($"### {what}");

            byte[]? content;
            try
            {
                content = read(what);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or NotSupportedException or UnauthorizedAccessException)
            {
                // PER FILE, so one bad room does not cost the other thirty-one. The whole point
                // of the dump is the variation between them.
                text.AppendLine($"### could not be read: {exception.Message}");
                return;
            }

            if (content is null || content.Length == 0)
            {
                text.AppendLine("### not in the bundles");
                return;
            }

            // Once, and handed on: the shape test sweeps every byte, and these are the biggest
            // files this tool opens.
            string shape = Shape(content);

            // THE NUMBER THE WHOLE EXERCISE TURNS ON, on every file rather than on the rooms
            // alone: a type that names tiles closes the chain, and one that does not ends it.
            text.AppendLine(
                $"### {content.Length} bytes, {shape}, "
                + $"{Mentions(content, TileExtension)} mentions of {TileExtension}");

            if (shape == "binary")
            {
                // Not decoded. A compiled file rendered as characters is a page of noise that
                // can break the document it lands in; its head and its strings are what a person
                // reads a format out of.
                text.AppendLine(Preview(content));
                foreach (string found in Strings(content, most: PreviewStrings))
                {
                    text.AppendLine($"    {found}");
                }

                return;
            }

            string decoded = Decode(content, shape);
            text.AppendLine(decoded);
            if (into is not null)
            {
                References(decoded, into);
            }
        }
    }

    /// <summary>
    /// The type files a room's own text declares, added to a set.
    /// </summary>
    /// <remarks>
    /// From the QUOTED runs, because that is how the format writes a path and because a room's
    /// doodad lines quote paths too - matching on the extension is what keeps the .ao models out
    /// without needing to know where the string table ends.
    /// </remarks>
    private static void References(string text, SortedSet<string> into)
    {
        int at = 0;
        while (at < text.Length)
        {
            int open = text.IndexOf('"', at);
            if (open < 0)
            {
                return;
            }

            int close = text.IndexOf('"', open + 1);
            if (close < 0)
            {
                return;
            }

            ReadOnlySpan<char> quoted = text.AsSpan(open + 1, close - open - 1);
            foreach (string extension in TypeExtensions)
            {
                if (quoted.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    into.Add(quoted.ToString());
                    break;
                }
            }

            at = close + 1;
        }
    }

    /// <summary>The head of a file that is not text, as hex and as characters.</summary>
    private static string Preview(byte[] content)
    {
        int show = Math.Min(content.Length, PreviewBytes);
        var preview = new StringBuilder(show * 4);

        for (int row = 0; row < show; row += 16)
        {
            int width = Math.Min(16, show - row);
            preview.Append("    ").Append(row.ToString("x4", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");

            for (int i = 0; i < 16; i++)
            {
                preview.Append(i < width
                    ? content[row + i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                    : "  ").Append(' ');
            }

            preview.Append(' ');
            for (int i = 0; i < width; i++)
            {
                byte value = content[row + i];
                preview.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }

            preview.AppendLine();
        }

        return preview.ToString();
    }

    /// <summary>
    /// The file's own text, in whichever encoding it turned out to be written in.
    /// </summary>
    /// <remarks>
    /// The byte-order mark decides where there is one, and the shape test decides otherwise -
    /// getting this wrong is what produced a page of NULs and the conclusion that a text file
    /// was binary.
    /// </remarks>
    private static string Decode(byte[] content, string shape)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        return shape == Utf16
            ? Encoding.Unicode.GetString(content)
            : Encoding.UTF8.GetString(content);
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
            ? Utf16
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
