using System.Globalization;

namespace PoEformance.Game.Files;

/// <summary>One doodad a room places: a model, where, turned how far, and how big.</summary>
/// <param name="X">Across the room, in the file's own unit - see <see cref="RoomLayout"/>.</param>
/// <param name="Y">Down the room, in the same unit.</param>
/// <param name="Turn">poe_data_tools' "radians" - an angle, 0 to 2 pi in older files and -pi to pi in later ones.</param>
/// <param name="Scale">Mostly 1; poe_data_tools sees 0 to 250.</param>
/// <param name="Ao">The doodad's <c>.ao</c> - the same kind of file a monster or an item is drawn from.</param>
/// <param name="Stub">The virtual file stub after it. Informational.</param>
public readonly record struct RoomDoodad(int X, int Y, float Turn, float Scale, string Ao, string Stub);

/// <summary>
/// A room file (<c>.arm</c>), read as far as its doodads.
/// </summary>
/// <remarks>
/// WHAT A ROOM SAYS AND WHAT IT DOES NOT. Its slot grid names edge and ground TYPES - which tile goes
/// there is chosen when the area is generated, by a rule no reference writes down - so the grid is
/// not geometry and is read here only for its size. Its doodads ARE explicit: a model, a position, an
/// angle and a scale, which is the part that can be drawn exactly.
///
/// THE LAYOUT IS poe_data_tools' <c>arm</c> parser, line for line - UTF-16 text, one record per line,
/// blank lines skipped:
///
///     version N
///     count, then that many quoted strings
///     dimensions            side length [again, below 31] [one more, from 22]
///     numbers               one or more - their sum, twice, is how many "thingy" lines follow
///     "tag"
///     bools                 one or more
///     root slot             k w h ... | f n | s | o | n - a k slot's first two numbers are the grid
///     thingies              sum(numbers) * 2 lines
///     point-of-interest groups, 9 / 10 / 5 / 6 of them by version, each a group (below)
///     "string"              from 35
///     grid                  h lines of w slots
///     doodads               a group
///
/// A GROUP is a count line and that many lines below version 32, and lines up to a line reading
/// <c>-1</c> from 32. A doodad line, by version:
///
///     x y [n, then n pairs of floats: 34+] radians [four floats: 18+] bool [bool: 25+]
///     n, then n floats   scale   "file.ao"   "stub"   [n key=value: 36+]
///
/// zao's <c>arm.hpp</c> keeps the same fields - his own doodad reader is commented out - and names
/// the four floats a quaternion. Only the angle is used for the turn; the four are stepped over.
///
/// THE UNIT OF x AND y IS WRITTEN DOWN NOWHERE. Both are whole numbers, and neither reference says
/// whether they count the 23 cells of a tile or the 250 world units of one; see RoomModels for how
/// the view lets the file settle it.
/// </remarks>
public sealed class RoomLayout
{
    /// <summary>What a room's file name ends with.</summary>
    public const string Extension = ".arm";

    /// <summary>Nothing read.</summary>
    public static RoomLayout None { get; } = new() { Why = "nothing to read" };

    /// <summary>The file's version.</summary>
    public int Version { get; private init; }

    /// <summary>Tiles across, from the root slot - one where the root is not a k slot.</summary>
    public int Width { get; private init; } = 1;

    /// <summary>Tiles down.</summary>
    public int Height { get; private init; } = 1;

    /// <summary>The doodads, in the file's order.</summary>
    public IReadOnlyList<RoomDoodad> Doodads { get; private init; } = [];

    /// <summary>Why nothing was read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether it read.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>Reads a file's bytes, through the game's shared text decode.</summary>
    public static RoomLayout Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>Reads the text. Public so the format can be tested without an install. Never throws.</summary>
    public static RoomLayout Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        string[] lines = [.. text.Split('\n').Select(one => one.TrimEnd('\r')).Where(one => one.Trim().Length > 0)];
        var at = 0;

        if (lines.Length == 0 || !lines[0].Trim().StartsWith("version ", StringComparison.Ordinal)
            || !int.TryParse(lines[0].Trim()[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            return new RoomLayout { Why = "no version line" };
        }

        at++;
        try
        {
            return Body(lines, ref at, version);
        }
        catch (FormatException exception)
        {
            return new RoomLayout { Version = version, Why = $"line {at + 1}: {exception.Message}" };
        }
    }

    private static RoomLayout Body(string[] lines, ref int at, int version)
    {
        // The strings: a count, then that many quoted lines. Nothing here needs them by index.
        int strings = Whole(Line(lines, ref at), 0, "the string count");
        at += strings;

        Line(lines, ref at);                                         // Dimensions.
        int thingies = 0;
        foreach (string word in Words(Line(lines, ref at)))
        {
            thingies += Whole(word, "the numbers before the tag");
        }

        Line(lines, ref at);                                         // The tag.
        Line(lines, ref at);                                         // The bools.

        string[] root = Words(Line(lines, ref at));
        int width = 1;
        int height = 1;
        if (root.Length >= 3 && root[0] == "k")
        {
            width = Whole(root[1], "the root slot's width");
            height = Whole(root[2], "the root slot's height");
        }

        if (width is < 1 or > 256 || height is < 1 or > 256)
        {
            throw new FormatException($"the room says it is {width}x{height} tiles");
        }

        at += thingies * 2;

        int groups = version switch
        {
            < 20 => 9,
            < 26 => 10,
            < 29 => 5,
            _ => 6,
        };

        for (var one = 0; one < groups; one++)
        {
            Group(lines, ref at, version);
        }

        if (version >= 35)
        {
            Line(lines, ref at);
        }

        at += height;                                                // The slot grid.

        var doodads = new List<RoomDoodad>();
        foreach (string line in Group(lines, ref at, version))
        {
            doodads.Add(Doodad(line, version));
        }

        return new RoomLayout { Version = version, Width = width, Height = height, Doodads = doodads };
    }

    /// <summary>One group's lines: counted below version 32, ended by a <c>-1</c> line from it.</summary>
    private static List<string> Group(string[] lines, ref int at, int version)
    {
        var kept = new List<string>();
        if (version < 32)
        {
            int count = Whole(Line(lines, ref at), 0, "a group's count");
            for (var one = 0; one < count; one++)
            {
                kept.Add(Line(lines, ref at));
            }

            return kept;
        }

        while (true)
        {
            string line = Line(lines, ref at);
            if (line.Trim() == "-1")
            {
                return kept;
            }

            kept.Add(line);
        }
    }

    /// <summary>
    /// One doodad line, field by field as poe_data_tools reads it.
    /// </summary>
    /// <remarks>
    /// THE TWO QUOTED FIELDS ARE TAKEN BY THEIR QUOTES, not by counting words, so a path with a space
    /// in it cannot shift the scale onto a file name - and a line that does not end in two quoted
    /// strings after the numbers is refused, which is the check that the counts before it held.
    /// </remarks>
    private static RoomDoodad Doodad(string line, int version)
    {
        int quote = line.IndexOf('"', StringComparison.Ordinal);
        if (quote < 0)
        {
            throw new FormatException("a doodad line with no quoted file");
        }

        string[] numbers = Words(line[..quote]);
        var at = 0;

        int x = Whole(Next(numbers, ref at), "a doodad's x");
        int y = Whole(Next(numbers, ref at), "a doodad's y");
        // READ, THEN STEPPED - never "at += Whole(Next(ref at))": C# reads the left side before the
        // call moves it, and the count's own word is lost. The field check below caught exactly that.
        if (version >= 34)
        {
            int pairs = Whole(Next(numbers, ref at), "a doodad's pair count");
            at += pairs * 2;
        }

        float turn = Real(Next(numbers, ref at), "a doodad's angle");
        if (version >= 18)
        {
            at += 4;
        }

        at++;                                                        // A bool.
        if (version >= 25)
        {
            at++;                                                    // Another.
        }

        int floats = Whole(Next(numbers, ref at), "a doodad's float count");
        at += floats;
        float scale = Real(Next(numbers, ref at), "a doodad's scale");

        if (at != numbers.Length)
        {
            throw new FormatException(
                $"a doodad line has {numbers.Length} numbers before its file and version {version} accounts for {at}");
        }

        (string ao, int after) = Quoted(line, quote);
        int next = line.IndexOf('"', after);
        string stub = next >= 0 ? Quoted(line, next).Text : string.Empty;

        return new RoomDoodad(x, y, turn, scale, ao.Replace('\\', '/'), stub);
    }

    private static (string Text, int After) Quoted(string line, int open)
    {
        int close = line.IndexOf('"', open + 1);
        if (close < 0)
        {
            throw new FormatException("an unclosed quote");
        }

        return (line[(open + 1)..close], close + 1);
    }

    private static string Line(string[] lines, ref int at)
    {
        if (at >= lines.Length)
        {
            throw new FormatException("the file ends early");
        }

        return lines[at++];
    }

    private static string Next(string[] words, ref int at)
        => at < words.Length ? words[at++] : throw new FormatException("a line ends early");

    private static string[] Words(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int Whole(string line, int word, string what)
        => Words(line) is { Length: > 0 } words && word < words.Length
            ? Whole(words[word], what)
            : throw new FormatException($"{what} is missing");

    private static int Whole(string word, string what)
        => int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new FormatException($"{what} reads \"{word}\", which is not a whole number");

    private static float Real(string word, string what)
        => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new FormatException($"{what} reads \"{word}\", which is not a number");
}
