using PoEformance.Core.Memory;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Looks for whatever holds the area's ROOM PLACEMENTS, by searching for known addresses.
/// </summary>
/// <remarks>
/// WHY THIS IS A DIFFERENT KIND OF SEARCH from <see cref="RoomProbe"/>, and why it is worth a
/// second attempt at a question that already failed once. That probe asked "does this pointer
/// look like it leads to a room?" - a question about SHAPE, and this project's own record says
/// what shape questions are worth: a matrix invariant that accepted a decoy, a fingerprint that
/// frustum planes and basis blocks both satisfy. This one asks "is this value the address of
/// BonesEntrance_Checkpoint2_St.arm's record?", which nothing can satisfy by accident.
///
/// THE ADDRESSES ARE ALREADY IN HAND. Every loaded file has a FileRecord in the table
/// <see cref="World.PreloadReader"/> walks, and that walk yields the record ADDRESSES, not just
/// the paths. So the .arm files of the current area are a set of perhaps thirty known pointers,
/// and anything in the game that refers to a placed room has to hold one of them - or the path
/// itself. Both are searched for, and a hit names the room it found.
///
/// WHAT IT SWEEPS. AreaInstance's own bytes, which no probe has looked at: the room hunt so far
/// covered the tile struct and the neighbourhood of TerrainMetadata, and the schema maps seven
/// fields of AreaInstance between 0xC4 and 0x8C0 - so most of it is unaccounted for. Every
/// plausible pointer in that window is followed ONE hop and its target searched the same way,
/// because a placement list is far likelier to be a vector hanging off a field than to be
/// inline.
///
/// A DIAGNOSTIC. It reads once per area under --debug, its lines go where the other probes' go,
/// and it draws nothing. Nothing is concluded here: a hit is an address and a name, and a person
/// decides what it means. A MISS IS ALSO A RESULT, and it is reported with the numbers that make
/// it one - how far it swept and how many pointers it followed - because "found nothing" and
/// "looked nowhere" are the two answers that must never read alike.
/// </remarks>
public sealed class RoomPlacementProbe
{
    /// <summary>How much of AreaInstance to sweep. Its mapped fields end at 0x8C0.</summary>
    public const int SweepBytes = 0x2000;

    /// <summary>How much to read at each followed pointer.</summary>
    private const int WindowBytes = 0x200;

    /// <summary>
    /// How many pointers to follow. A guard on a garbage window, not a view about the struct.
    /// </summary>
    /// <remarks>
    /// A window of nonsense is mostly plausible-looking pointers, and following every one turns
    /// one probe into a walk of the heap - and a recording into something nobody can open.
    /// </remarks>
    public const int MostFollowed = 256;

    /// <summary>Lines of hits to print before saying how many more there were.</summary>
    private const int MostHits = 24;

    private readonly IMemoryReader _reader;

    public RoomPlacementProbe(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Sweeps AreaInstance for anything that refers to one of the area's room files.
    /// </summary>
    /// <param name="areaInstance">The AreaInstance struct's address.</param>
    /// <param name="rooms">
    /// The .arm files of this area, by the ADDRESS of their FileRecord. That address is the
    /// thing being searched for, so this is the whole input that makes the search unambiguous.
    /// </param>
    public IReadOnlyList<string> Probe(ulong areaInstance, IReadOnlyDictionary<ulong, string> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        if (!MemoryReaderExtensions.IsPlausiblePointer(areaInstance))
        {
            return ["placements: no area instance"];
        }

        if (rooms.Count == 0)
        {
            // Not a failure of the sweep, and it must not read as one: with no addresses to
            // search for, the sweep cannot say anything either way.
            return ["placements: no .arm files loaded, so there is nothing to search for"];
        }

        var window = new byte[SweepBytes];
        if (!_reader.TryRead(areaInstance, window))
        {
            return [$"placements: AreaInstance unreadable at {areaInstance:X} for {SweepBytes} bytes"];
        }

        var hits = new List<string>();
        var followed = new HashSet<ulong>();
        int follows = 0;
        int more = 0;

        Look(window, areaInstance, "", rooms, hits, ref more);

        for (int at = 0; at + 8 <= window.Length; at += 8)
        {
            ulong target = BitConverter.ToUInt64(window, at);
            if (!MemoryReaderExtensions.IsPlausiblePointer(target)
                || rooms.ContainsKey(target)
                || !followed.Add(target))
            {
                continue;
            }

            if (follows >= MostFollowed)
            {
                break;
            }

            follows++;
            var inner = new byte[WindowBytes];
            if (_reader.TryRead(target, inner))
            {
                Look(inner, target, $"AreaInstance+0x{at:X4} -> ", rooms, hits, ref more);
            }
        }

        var lines = new List<string>(hits.Count + 2)
        {
            $"placements: {rooms.Count} .arm records, swept {SweepBytes} bytes of AreaInstance"
            + $" and followed {follows} pointers",
        };

        lines.AddRange(hits);

        if (more > 0)
        {
            lines.Add($"  and {more} more");
        }

        if (hits.Count == 0)
        {
            // THE MISS, WITH ITS NUMBERS. Without them this line is indistinguishable from a
            // probe that never ran, which is the failure the ground layer already paid for.
            lines.Add("  nothing refers to a room file - not by record address, not by path");
        }

        return lines;
    }

    /// <summary>
    /// Reports every reference to a room in one window: a record address, or the path itself.
    /// </summary>
    /// <remarks>
    /// BOTH FORMS, because which one the game uses is exactly what is unknown. A placement that
    /// points at the FileRecord shows as an address; one that carries the path inline shows as
    /// UTF-16 text ending ".arm". Searching for only the first would report "nothing found"
    /// about a structure sitting in the window in plain sight.
    /// </remarks>
    private static void Look(
        byte[] window,
        ulong at,
        string via,
        IReadOnlyDictionary<ulong, string> rooms,
        List<string> hits,
        ref int more)
    {
        for (int i = 0; i + 8 <= window.Length; i += 8)
        {
            ulong value = BitConverter.ToUInt64(window, i);
            if (rooms.TryGetValue(value, out string? name))
            {
                Add(hits, ref more, $"  {via}+0x{i:X4}  record of {Short(name)}");
            }
        }

        // ".arm" as the game stores a path: UTF-16, so every second byte is zero.
        for (int i = 0; i + 8 <= window.Length; i++)
        {
            if (window[i] == (byte)'.' && window[i + 1] == 0
                && window[i + 2] == (byte)'a' && window[i + 3] == 0
                && window[i + 4] == (byte)'r' && window[i + 5] == 0
                && window[i + 6] == (byte)'m' && window[i + 7] == 0)
            {
                Add(hits, ref more, $"  {via}+0x{i:X4}  the text \".arm\" at {at + (ulong)i:X}");
            }
        }
    }

    private static void Add(List<string> hits, ref int more, string line)
    {
        if (hits.Count < MostHits)
        {
            hits.Add(line);
        }
        else
        {
            more++;
        }
    }

    /// <summary>The room's file name, since a full path leaves no room for the address.</summary>
    private static string Short(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
