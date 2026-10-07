using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// The key the tile book loads a room under: its path, then how many doodads it may place and whether the editor's tools are among them.
/// </summary>
/// <remarks>
/// THE TILE'S ARRANGEMENT - see <see cref="TileKey"/> - for the choices a room has: changing one is a
/// different key, so the portrait reloads without knowing about rooms. The usual choices write the
/// bare path, which keeps every key written before the choices existed meaning what it meant.
/// </remarks>
/// <param name="Path">The room's <c>.arm</c>.</param>
/// <param name="Doodads">Most doodads placed - see <see cref="RoomModels.UsualDoodads"/>.</param>
/// <param name="Tools">Whether the level editor's tools are placed too - see <see cref="RoomModels.IsTool"/>.</param>
/// <param name="Laid">Where the room is drawn as the area laid it - "x,y,turn,area" - or empty for the room as its file has it; see LaidRoomModels.</param>
/// <param name="AtLevel">Whether a laid room's pieces are set at their tiles' own levels rather than fitted to the area's ground - see LaidRoomModels.</param>
public readonly record struct RoomKey(string Path, int Doodads = RoomModels.UsualDoodads, bool Tools = false, string Laid = "", bool AtLevel = false)
{
    /// <summary>What starts the word naming the place the room is laid at.</summary>
    public const string LaidWord = "laid=";

    /// <summary>What starts the word naming the cap; its number follows.</summary>
    public const string DoodadsWord = "doodads=";

    /// <summary>The word saying the editor's tools are placed.</summary>
    public const string ToolsWord = "tools";

    /// <summary>The word saying a laid room's pieces are set at their tiles' own levels.</summary>
    public const string LevelWord = "level";

    /// <summary>The key as a string - the bare path at the usual choices.</summary>
    public override string ToString()
    {
        if (Doodads == RoomModels.UsualDoodads && !Tools && Laid.Length == 0 && !AtLevel)
        {
            return Path;
        }

        var words = new List<string>(4);
        if (Doodads != RoomModels.UsualDoodads)
        {
            words.Add(string.Create(CultureInfo.InvariantCulture, $"{DoodadsWord}{Doodads}"));
        }

        if (Tools)
        {
            words.Add(ToolsWord);
        }

        if (Laid.Length > 0)
        {
            words.Add(LaidWord + Laid);
        }

        if (AtLevel)
        {
            words.Add(LevelWord);
        }

        return string.Concat(Path, TileKey.Mark.ToString(), string.Join(TileKey.WordMark, words));
    }

    /// <summary>The place the room is laid at, read out of <see cref="Laid"/> - false where it names none or does not read.</summary>
    public bool TryLaid(out int x, out int y, out int turn, out int area)
    {
        x = y = turn = area = 0;
        string[] parts = Laid.Split(',');
        return parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out y)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out turn)
            && turn is >= 0 and < 8
            && int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out area);
    }

    /// <summary>A place written the way <see cref="Laid"/> holds it.</summary>
    public static string LaidAt(int x, int y, int turn, int area)
        => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{turn},{area}");

    /// <summary>A key read back. A cap outside the slider's ends, or a word this does not know, leaves the usual.</summary>
    public static RoomKey Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int mark = key.IndexOf(TileKey.Mark, StringComparison.Ordinal);
        if (mark < 0)
        {
            return new RoomKey(key);
        }

        int doodads = RoomModels.UsualDoodads;
        var tools = false;
        var level = false;
        string laid = string.Empty;
        ReadOnlySpan<char> words = key.AsSpan(mark + 1);
        foreach (Range one in words.Split(TileKey.WordMark))
        {
            ReadOnlySpan<char> word = words[one];
            if (word.StartsWith(DoodadsWord, StringComparison.Ordinal)
                && int.TryParse(word[DoodadsWord.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number is >= RoomModels.LeastDoodads and <= RoomModels.MostDoodads)
            {
                doodads = number;
            }
            else if (word.SequenceEqual(ToolsWord))
            {
                tools = true;
            }
            else if (word.StartsWith(LaidWord, StringComparison.Ordinal))
            {
                laid = word[LaidWord.Length..].ToString();
            }
            else if (word.SequenceEqual(LevelWord))
            {
                level = true;
            }
        }

        RoomKey read = new(key[..mark], doodads, tools, laid, level);
        return read.Laid.Length == 0 || read.TryLaid(out _, out _, out _, out _) ? read : read with { Laid = string.Empty };
    }
}
