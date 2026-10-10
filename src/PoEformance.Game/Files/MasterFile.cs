namespace PoEformance.Game.Files;

/// <summary>
/// An area's master (<c>.tsi</c>): the key-value text beside its graphs that names the area's room set, tile set and fill tiles - the second road to the room set, where the loaded-file list carries the master and not the set.
/// </summary>
/// <remarks>
/// WHY A SECOND ROAD. A fresh instance's loaded-file list carries <c>generate.rs</c> and not the
/// master: the generator reads the set to lay the rooms. A re-entered instance - a tool started
/// inside one, a portal back from town - is not generated again, and its list carried
/// <c>master.tsi</c> and not the set: of five captures of The Assembly, four of one kind and one of
/// the other, no list carried both and none carried neither. The master's <c>RoomSet</c> line names
/// the set, so the two roads together cover every list seen.
///
/// THE FORMAT, as poe_data_tools reads it (<c>file_parsers/tsi</c>): one <c>key value</c> a line, the
/// value quoted or not, with blank lines and <c>//</c> comments skipped; RePoE reads the same file
/// (<c>TSIFile</c>) into the <c>master</c> of its graph JSON, where VaalFactory's says
/// <c>RoomSet generate.rs</c>, <c>TileSet tiles.tst</c> and <c>FillTiles random_fill_tiles.gft</c>.
/// A name is resolved the way RePoE's <c>resolve</c> does: as written first, then beside the master.
/// </remarks>
public sealed class MasterFile
{
    private static readonly IReadOnlyDictionary<string, string> Nothing = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Nothing read.</summary>
    public static MasterFile None { get; } = new() { Why = "nothing to read" };

    /// <summary>Every key with its value, the last of a repeated key kept, as RePoE's dictionary keeps it.</summary>
    public IReadOnlyDictionary<string, string> Values { get; private init; } = Nothing;

    /// <summary>Why the file did not read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether the file read.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>The room set the master names - <c>generate.rs</c> for VaalFactory - or empty where it names none. See <see cref="Candidates"/> for where to read it.</summary>
    public string RoomSet => Values.GetValueOrDefault("RoomSet", string.Empty);

    /// <summary>Reads the file's bytes. Never throws.</summary>
    public static MasterFile Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>Reads the text. Public so the format can be tested without an install. Never throws.</summary>
    public static MasterFile Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // THE KEY UP TO THE FIRST BLANK, the value the rest - unquoted where the rest is one
            // quoted string, as the reference takes it. A key alone names nothing rather than failing.
            int blank = line.IndexOfAny([' ', '\t']);
            string key = blank < 0 ? line : line[..blank];
            string value = blank < 0 ? string.Empty : line[(blank + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values.Count == 0 ? None : new MasterFile { Values = values };
    }

    /// <summary>
    /// Where a name the master gives may be read from, in the order RePoE's resolve tries them: as written, then beside the master. Empty for no name.
    /// </summary>
    /// <param name="master">The master's own path, for its folder.</param>
    /// <param name="named">A value of the master - <see cref="RoomSet"/> or another file name.</param>
    public static IReadOnlyList<string> Candidates(string master, string named)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(named);
        string name = Normalised(named);
        if (name.Length == 0)
        {
            return [];
        }

        string folder = Normalised(master);
        int slash = folder.LastIndexOf('/');
        folder = slash < 0 ? string.Empty : folder[..(slash + 1)];
        string beside = Normalised(folder + name);
        return string.Equals(beside, name, StringComparison.OrdinalIgnoreCase) ? [name] : [name, beside];
    }

    /// <summary>A path with forward slashes, no doubled ones and no blanks at its ends - RePoE's normalize, with the backslashes GGG's files carry turned over too.</summary>
    private static string Normalised(string path)
    {
        string normalised = path.Replace('\\', '/').Trim();
        while (normalised.Contains("//", StringComparison.Ordinal))
        {
            normalised = normalised.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalised;
    }
}
