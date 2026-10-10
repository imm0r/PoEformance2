using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// The boss arena tiles of an area, found in the install without the area being loaded: from the area's terrain folders, by name.
/// </summary>
/// <remarks>
/// TWO ROADS FROM A FOLDER, both taken. The install's own tile index, filtered to the folder: a
/// map's arena sits under its folder as often as not (<c>Maps/VaalFactory/.../BossArena_01.tdt</c>).
/// And the folder's masters (<c>.tsi</c>, directly in it, as AreaRoomSet takes them), each naming a
/// tileset whose list - includes followed - names every tile the generator may lay, an arena filed
/// under a shared folder among them. A tile is an arena by the same rule the map's markers use
/// (TerrainLandmarks.LooksLikeArena): its file is named for an arena or a boss.
///
/// WHAT IT DOES NOT KNOW is which of several arena-looking tiles is THE arena, where a map's
/// tileset names a few; it offers all of them, distinct, the folder's own first. The live area, where
/// the game is running in it, says exactly - see the pane's ArenasIn - and the two lists are joined
/// by whoever offers them.
/// </remarks>
public static class AreaArenas
{
    /// <summary>How deep a tile list's includes are followed - the guard TilesetIndex has.</summary>
    private const int MostIncludes = 8;

    /// <summary>
    /// What a search found, and what it searched - so an empty answer says which step was empty.
    /// </summary>
    /// <param name="Folders">The folders searched.</param>
    /// <param name="Tiles">The arena tiles found, distinct and in the order found.</param>
    /// <param name="TilesIndexed">How many tiles the install's index held - nought before its walk, which is the usual reason for nothing found.</param>
    /// <param name="MastersIndexed">How many masters the install's index held.</param>
    /// <param name="MastersRead">How many masters sat in the folders and named a tileset.</param>
    /// <param name="Listed">How many tiles those tilesets listed, includes followed.</param>
    public readonly record struct Search(
        IReadOnlyList<string> Folders,
        IReadOnlyList<string> Tiles,
        int TilesIndexed,
        int MastersIndexed,
        int MastersRead,
        int Listed)
    {
        /// <summary>The search in a line, for under the book's combo.</summary>
        public string Said
            => TilesIndexed == 0 && MastersIndexed == 0
                ? "the install's tile index is not walked yet - open the combo again in a moment"
                : string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{Tiles.Count} arena tile{(Tiles.Count == 1 ? string.Empty : "s")} in {Folders.Count} folder{(Folders.Count == 1 ? string.Empty : "s")} ({string.Join(", ", Folders.Select(Shortened))}) · {MastersRead} master{(MastersRead == 1 ? string.Empty : "s")} listing {Listed} tiles · index: {TilesIndexed} tiles, {MastersIndexed} tilesets");

        private static string Shortened(string folder)
            => folder.StartsWith("Metadata/Terrain/", StringComparison.OrdinalIgnoreCase) ? folder["Metadata/Terrain/".Length..] : folder;
    }

    /// <summary>The arena tiles under the folders, distinct and in the order found. Never throws. See <see cref="Find"/>.</summary>
    public static IReadOnlyList<string> Of(
        Func<string, byte[]?>? read,
        IReadOnlyList<string> folders,
        IReadOnlyList<string> tiles,
        IReadOnlyList<string> masters)
        => Find(read, folders, tiles, masters).Tiles;

    /// <summary>
    /// The arena tiles under the folders, with what was searched to find them. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path - or null, and only the tile index is searched.</param>
    /// <param name="folders">The area's terrain folders, each with its trailing slash - see AreaGraphs.FoldersOf.</param>
    /// <param name="tiles">Every <c>.tdt</c> the install has, or empty before its walk.</param>
    /// <param name="masters">Every <c>.tsi</c> the install has, or empty before its walk.</param>
    public static Search Find(
        Func<string, byte[]?>? read,
        IReadOnlyList<string> folders,
        IReadOnlyList<string> tiles,
        IReadOnlyList<string> masters)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(masters);
        if (folders.Count == 0)
        {
            return new Search([], [], tiles.Count, masters.Count, 0, 0);
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in folders)
        {
            foreach (string tile in tiles)
            {
                if (tile.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && TerrainLandmarks.LooksLikeArena(tile) && seen.Add(tile))
                {
                    found.Add(tile);
                }
            }
        }

        if (read is null)
        {
            return new Search(folders, found, tiles.Count, masters.Count, 0, 0);
        }

        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var mastersRead = 0;
        var listed = 0;
        foreach (string folder in folders)
        {
            foreach (string master in masters)
            {
                // DIRECTLY IN THE FOLDER and not deeper: a map's graphs sit a folder down, and nothing
                // there names a tileset.
                if (!master.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || master.IndexOf('/', folder.Length) >= 0)
                {
                    continue;
                }

                string? set = TilesetFile.Value(Text(read, master), "TileSet");
                if (set is not { Length: > 0 })
                {
                    continue;
                }

                mastersRead++;
                foreach (string tile in Listed(read, TilesetFile.Beside(master, set), lists, 0))
                {
                    listed++;
                    if (TerrainLandmarks.LooksLikeArena(tile) && seen.Add(tile))
                    {
                        found.Add(tile);
                    }
                }
            }
        }

        return new Search(folders, found, tiles.Count, masters.Count, mastersRead, listed);
    }

    /// <summary>Every tile a list names, its includes' tiles among them, as the files spell them - memoised per list.</summary>
    private static List<string> Listed(Func<string, byte[]?> read, string tst, Dictionary<string, List<string>> lists, int depth)
    {
        if (lists.TryGetValue(tst, out List<string>? known))
        {
            return known;
        }

        // SEEN BEFORE IT IS READ, so a list that comes back round to itself finds itself empty.
        var tiles = new List<string>();
        lists[tst] = tiles;
        TileList list = TileList.Read(read(Slashed(tst)));
        tiles.AddRange(list.Tiles);
        if (depth < MostIncludes)
        {
            foreach (string include in list.Includes)
            {
                tiles.AddRange(Listed(read, TilesetFile.Beside(tst, include), lists, depth + 1));
            }
        }

        return tiles;
    }

    private static string? Text(Func<string, byte[]?> read, string path)
        => read(Slashed(path)) is { Length: > 0 } bytes ? StatDescriptionFiles.Decode(bytes) : null;

    private static string Slashed(string path) => path.Replace('\\', '/').Trim();
}
