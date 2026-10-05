namespace PoEformance.Features;

/// <summary>
/// The key the tile book loads a tile under: its path, then what is left out and the tileset it is drawn as.
/// </summary>
/// <remarks>
/// THE CHOICES RIDE IN THE KEY, so changing one is a different key and the portrait reloads without
/// knowing about tiles - the way the item book's drop or held choice does. After
/// <see cref="Mark"/>: <see cref="Bare"/> leaves the ground out, <see cref="Unwalled"/> the black
/// walls, and <see cref="SetWord"/> followed by a path names the tileset whose overrides are drawn,
/// the words joined by <see cref="WordMark"/>. A room's key uses the same mark for its unit.
///
/// THE FIRST MARK SPLITS, since a word may hold a path and paths hold neither mark.
/// </remarks>
/// <param name="Path">The tile's <c>.tdt</c>.</param>
/// <param name="Ground">Whether its ground block is drawn.</param>
/// <param name="Walls">Whether its black walls are drawn.</param>
/// <param name="Tileset">The <c>.tsi</c> it is drawn as, or empty for its own materials.</param>
public readonly record struct TileKey(string Path, bool Ground = true, bool Walls = true, string Tileset = "")
{
    /// <summary>What separates the path from the words.</summary>
    public const char Mark = '|';

    /// <summary>What separates the words.</summary>
    public const char WordMark = '+';

    /// <summary>The word for a tile drawn without its ground.</summary>
    public const string Bare = "bare";

    /// <summary>The word for a tile drawn without its black walls. See TileModels.BlackWall.</summary>
    public const string Unwalled = "unwalled";

    /// <summary>What starts the word naming the tileset; the tileset's path follows.</summary>
    public const string SetWord = "set=";

    /// <summary>The key as a string - the bare path where nothing is chosen.</summary>
    public override string ToString()
    {
        if (Ground && Walls && Tileset.Length == 0)
        {
            return Path;
        }

        var key = new System.Text.StringBuilder(Path).Append(Mark);
        int words = key.Length;
        if (!Ground)
        {
            key.Append(Bare);
        }

        if (!Walls)
        {
            Joined(key, words).Append(Unwalled);
        }

        if (Tileset.Length > 0)
        {
            Joined(key, words).Append(SetWord).Append(Tileset);
        }

        return key.ToString();
    }

    /// <summary>A key read back - every word it does not know is ignored.</summary>
    public static TileKey Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int mark = key.IndexOf(Mark, StringComparison.Ordinal);
        if (mark < 0)
        {
            return new TileKey(key);
        }

        bool ground = true;
        bool walls = true;
        string tileset = string.Empty;
        ReadOnlySpan<char> words = key.AsSpan(mark + 1);
        foreach (Range one in words.Split(WordMark))
        {
            ReadOnlySpan<char> word = words[one];
            if (word.Equals(Bare, StringComparison.Ordinal))
            {
                ground = false;
            }
            else if (word.Equals(Unwalled, StringComparison.Ordinal))
            {
                walls = false;
            }
            else if (word.StartsWith(SetWord, StringComparison.Ordinal))
            {
                tileset = word[SetWord.Length..].ToString();
            }
        }

        return new TileKey(key[..mark], ground, walls, tileset);
    }

    private static System.Text.StringBuilder Joined(System.Text.StringBuilder key, int words)
        => key.Length > words ? key.Append(WordMark) : key;
}
