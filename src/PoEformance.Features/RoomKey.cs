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
public readonly record struct RoomKey(string Path, int Doodads = RoomModels.UsualDoodads, bool Tools = false)
{
    /// <summary>What starts the word naming the cap; its number follows.</summary>
    public const string DoodadsWord = "doodads=";

    /// <summary>The word saying the editor's tools are placed.</summary>
    public const string ToolsWord = "tools";

    /// <summary>The key as a string - the bare path at the usual choices.</summary>
    public override string ToString()
    {
        if (Doodads == RoomModels.UsualDoodads && !Tools)
        {
            return Path;
        }

        string doodads = Doodads == RoomModels.UsualDoodads
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{DoodadsWord}{Doodads}");
        string tools = Tools ? ToolsWord : string.Empty;
        string between = doodads.Length > 0 && tools.Length > 0 ? TileKey.WordMark.ToString() : string.Empty;
        return string.Concat(Path, TileKey.Mark.ToString(), doodads, between, tools);
    }

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
        }

        return new RoomKey(key[..mark], doodads, tools);
    }
}
