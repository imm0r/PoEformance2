using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Every room that may stand in the current area: the <c>.arm</c> files it loaded, and every room of every room set (<c>.rs</c>) it loaded or its master (<c>.tsi</c>) names.
/// </summary>
/// <remarks>
/// WHY NOT THE LOADED FILES ALONE. The loaded-file list counts files loaded since the area change,
/// and a room already in memory from the last instance of the same map is not loaded again: in The
/// Assembly it named ten rooms of a set of fifty-three, and neither the boss room nor the entrance,
/// both of which stood in the area. The set - <c>generate.rs</c> beside the area's master - names
/// every room the generator may lay, and a fresh instance's list carries it. A league's rooms (an
/// Expedition encounter, an Incursion waygate) are not in the set and come from the list.
///
/// TWO ROADS TO THE SET. A re-entered instance is not generated again, and its list carried the
/// master and not the set - seventeen rooms placed as two, the morning after fifty-five placed as
/// nineteen. The master's RoomSet line names the set, so every <c>.tsi</c> of the list is read for
/// it too - see MasterFile. A set both roads reach is read once.
///
/// ONLY THE TERRAIN'S: a <c>.rs</c> or <c>.tsi</c> under Metadata/Terrain is the area's, and nothing
/// else with the extension is asked for.
/// </remarks>
public static class AreaRoomSet
{
    /// <summary>The rooms, without duplicates and in path order.</summary>
    /// <param name="loaded">The files the area loaded.</param>
    /// <param name="read">How to get a file out of the install, for the room sets and the masters. Null for a file that is not there.</param>
    public static List<string> Files(IEnumerable<string> loaded, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(read);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<string>();
        var masters = new List<string>();
        foreach (string path in loaded)
        {
            if (path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(path.Replace('\\', '/'));
            }
            else if (path.StartsWith("Metadata/Terrain/", StringComparison.OrdinalIgnoreCase))
            {
                if (path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
                {
                    sets.Add(path);
                }
                else if (path.EndsWith(".tsi", StringComparison.OrdinalIgnoreCase))
                {
                    masters.Add(path);
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

        foreach (string master in masters)
        {
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

    private static void Add(SortedSet<string> files, RoomSetFile set)
    {
        foreach (RoomSetEntry room in set.Rooms)
        {
            files.Add(room.Arm);
        }
    }
}
