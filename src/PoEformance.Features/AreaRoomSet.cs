using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Every room that may stand in the current area: the <c>.arm</c> files it loaded, and every room of every room set (<c>.rs</c>) it loaded, its loaded masters (<c>.tsi</c>) name, or the masters the install keeps beside its rooms name.
/// </summary>
/// <remarks>
/// WHY NOT THE LOADED FILES ALONE. The loaded-file list counts files loaded since the area change,
/// and a room already in memory from the last instance of the same map is not loaded again: in The
/// Assembly it named ten rooms of a set of fifty-three, and neither the boss room nor the entrance,
/// both of which stood in the area. The set - <c>generate.rs</c> beside the area's master - names
/// every room the generator may lay, and a fresh instance's list carries it. A league's rooms (an
/// Expedition encounter, an Incursion waygate) are not in the set and come from the list.
///
/// THREE ROADS TO THE SET. A re-entered instance is not generated again, and its list carried the
/// master and not the set - seventeen rooms placed as two, the morning after fifty-five placed as
/// nineteen. The master's RoomSet line names the set, so every <c>.tsi</c> of the list is read for it
/// too - see MasterFile. And The Stone Citadel's list carried neither, thirteen rooms of fifty-five,
/// the set and the master both cached from an earlier visit. The install's index knows the masters
/// regardless: of the 458 map graphs RePoE's data carries, 456 name a master sitting directly in the
/// folder above the rooms' <c>Rooms/</c>, and a map may have several (UberDoryani three, SwampTower
/// six). So every master the index lists in that folder - the tool's start walks the index for
/// every <c>.tsi</c> under Metadata/Terrain, see TileBook - is read as well. A set two roads reach is
/// read once.
///
/// ONLY THE TERRAIN'S: a <c>.rs</c> or <c>.tsi</c> under Metadata/Terrain is the area's, and nothing
/// else with the extension is asked for.
/// </remarks>
public static class AreaRoomSet
{
    /// <summary>The folder a room file sits under - the area's, where its masters live.</summary>
    private const string RoomsFolder = "/Rooms/";

    /// <summary>The rooms, without duplicates and in path order.</summary>
    /// <param name="loaded">The files the area loaded.</param>
    /// <param name="read">How to get a file out of the install, for the room sets and the masters. Null for a file that is not there.</param>
    /// <param name="masters">Every master (<c>.tsi</c>) the install has, as its index lists them - or none, before the index was walked.</param>
    public static List<string> Files(IEnumerable<string> loaded, Func<string, byte[]?> read, IReadOnlyList<string>? masters = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(read);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<string>();
        var named = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in loaded)
        {
            if (path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase))
            {
                string room = path.Replace('\\', '/');
                files.Add(room);
                int rooms = room.IndexOf(RoomsFolder, StringComparison.OrdinalIgnoreCase);
                if (rooms > 0)
                {
                    folders.Add(room[..(rooms + 1)]);
                }
            }
            else if (path.StartsWith("Metadata/Terrain/", StringComparison.OrdinalIgnoreCase))
            {
                if (path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
                {
                    sets.Add(path);
                    folders.Add(Folder(path));
                }
                else if (path.EndsWith(".tsi", StringComparison.OrdinalIgnoreCase))
                {
                    named.Add(path);
                    folders.Add(Folder(path));
                }
            }
        }

        // THE INDEX'S MASTERS IN THE AREA'S FOLDERS, directly in them and not deeper: a map's graphs
        // sit a folder down, and nothing there names a set.
        foreach (string master in masters ?? [])
        {
            foreach (string folder in folders)
            {
                if (master.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && master.IndexOf('/', folder.Length) < 0)
                {
                    named.Add(master);
                    break;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string set in sets)
        {
            if (seen.Add(set))
            {
                Add(files, RoomSetFile.Read(read(set)));
            }
        }

        var read_ = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string master in named)
        {
            if (!read_.Add(master))
            {
                continue;
            }

            // AS WRITTEN, THEN BESIDE THE MASTER, as RePoE resolves it - the first that reads is the set.
            foreach (string candidate in MasterFile.Candidates(master, MasterFile.Read(read(master)).RoomSet))
            {
                if (!seen.Add(candidate))
                {
                    break;
                }

                byte[]? bytes = read(candidate);
                if (bytes is { Length: > 0 })
                {
                    Add(files, RoomSetFile.Read(bytes));
                    break;
                }

                seen.Remove(candidate);
            }
        }

        return [.. files];
    }

    /// <summary>The folder a path sits in, with its trailing slash - empty for a bare name.</summary>
    private static string Folder(string path)
    {
        string straight = path.Replace('\\', '/');
        int slash = straight.LastIndexOf('/');
        return slash < 0 ? string.Empty : straight[..(slash + 1)];
    }

    private static void Add(SortedSet<string> files, RoomSetFile set)
    {
        foreach (RoomSetEntry room in set.Rooms)
        {
            files.Add(room.Arm);
        }
    }
}
