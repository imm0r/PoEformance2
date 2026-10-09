using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Every room that may stand in the current area: the <c>.arm</c> files it loaded, and every room of every room set (<c>.rs</c>) it loaded.
/// </summary>
/// <remarks>
/// WHY NOT THE LOADED FILES ALONE. The loaded-file list counts files loaded since the area change,
/// and a room already in memory from the last instance of the same map is not loaded again: in The
/// Assembly it named ten rooms of a set of fifty-three, and neither the boss room nor the entrance,
/// both of which stood in the area. The set - <c>generate.rs</c> beside the area's master - names
/// every room the generator may lay, and it is read anew for every area. A league's rooms (an
/// Expedition encounter, an Incursion waygate) are not in the set and come from the list.
///
/// ONLY THE TERRAIN'S SETS: a <c>.rs</c> under Metadata/Terrain is a room set, and nothing else
/// with the extension is asked for.
/// </remarks>
public static class AreaRoomSet
{
    /// <summary>The rooms, without duplicates and in path order.</summary>
    /// <param name="loaded">The files the area loaded.</param>
    /// <param name="read">How to get a file out of the install, for the room sets.</param>
    public static List<string> Files(IEnumerable<string> loaded, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(read);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<string>();
        foreach (string path in loaded)
        {
            if (path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(path.Replace('\\', '/'));
            }
            else if (path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase) && path.StartsWith("Metadata/Terrain/", StringComparison.OrdinalIgnoreCase))
            {
                sets.Add(path);
            }
        }

        foreach (string set in sets)
        {
            foreach (RoomSetEntry room in RoomSetFile.Read(read(set)).Rooms)
            {
                files.Add(room.Arm);
            }
        }

        return [.. files];
    }
}
