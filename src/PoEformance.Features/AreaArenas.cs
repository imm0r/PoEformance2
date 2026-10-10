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
    /// The arena tiles under the folders, distinct and in the order found. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path - or null, and only the tile index is searched.</param>
    /// <param name="folders">The area's terrain folders, each with its trailing slash - see AreaGraphs.FoldersOf.</param>
    /// <param name="tiles">Every <c>.tdt</c> the install has, or empty before its walk.</param>
    /// <param name="masters">Every <c>.tsi</c> the install has, or empty before its walk.</param>
    public static IReadOnlyList<string> Of(
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
            return [];
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
            return found;
        }

        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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

                foreach (string tile in Listed(read, TilesetFile.Beside(master, set), lists, 0))
                {
                    if (TerrainLandmarks.LooksLikeArena(tile) && seen.Add(tile))
                    {
                        found.Add(tile);
                    }
                }
            }
        }

        return found;
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
