using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// The key the tile book loads a room under: its path, then how many doodads it may place.
/// </summary>
/// <remarks>
/// THE TILE'S ARRANGEMENT - see <see cref="TileKey"/> - for the one choice a room has: changing the cap
/// is a different key, so the portrait reloads without knowing about rooms. The usual cap writes the
/// bare path, which keeps every key written before the slider existed meaning what it meant.
/// </remarks>
/// <param name="Path">The room's <c>.arm</c>.</param>
/// <param name="Doodads">Most doodads placed - see <see cref="RoomModels.UsualDoodads"/>.</param>
public readonly record struct RoomKey(string Path, int Doodads = RoomModels.UsualDoodads)
{
    /// <summary>What starts the word naming the cap; its number follows.</summary>
    public const string DoodadsWord = "doodads=";

    /// <summary>The key as a string - the bare path at the usual cap.</summary>
    public override string ToString()
        => Doodads == RoomModels.UsualDoodads
            ? Path
            : string.Create(CultureInfo.InvariantCulture, $"{Path}{TileKey.Mark}{DoodadsWord}{Doodads}");

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
        }

        return new RoomKey(key[..mark], doodads);
    }
}
