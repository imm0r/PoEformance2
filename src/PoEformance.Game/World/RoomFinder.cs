using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>
/// Where a room could lie in the area: its corner on the tile grid, which way it was laid, and how much of it the ground confirms.
/// </summary>
/// <param name="X">The tile column of the footprint's lowest corner.</param>
/// <param name="Y">The tile row of the footprint's lowest corner.</param>
/// <param name="Turn">Which of the eight ways the room was laid - see <see cref="RoomFinder.Said"/>: quarter turns in the low two bits, mirrored from 4.</param>
/// <param name="Width">Tiles across on the area's grid - the room's width or, turned a quarter, its height.</param>
/// <param name="Height">Tiles down on the area's grid.</param>
/// <param name="Matched">How many of the room's corners the area's ground agrees with.</param>
/// <param name="Corners">How many corners the room says anything about.</param>
public readonly record struct RoomCandidate(int X, int Y, int Turn, int Width, int Height, int Matched, int Corners)
{
    /// <summary>Whether every corner the room names agrees.</summary>
    public bool Exact => Matched == Corners;
}

/// <summary>What a search for a room came to: the places, and what it rested on.</summary>
/// <param name="Candidates">Exact matches in grid order; where there are none, the nearest few, best first.</param>
/// <param name="Corners">How many corners the room's stamp holds.</param>
/// <param name="More">Exact matches past the cap, counted and not listed.</param>
/// <param name="Left">k slots left out of the stamp, being bigger than one tile - see <see cref="RoomFinder"/>.</param>
/// <param name="Why">Why there was no search, or empty.</param>
public sealed record RoomSearch(IReadOnlyList<RoomCandidate> Candidates, int Corners, int More, int Left, string Why)
{
    /// <summary>No search.</summary>
    public static RoomSearch Not(string why) => new([], 0, 0, 0, why);

    /// <summary>Whether any candidate agrees everywhere.</summary>
    public bool Found => Candidates.Count > 0 && Candidates[0].Exact;
}

/// <summary>
/// Finds where a room the area loaded was laid, by its ground.
/// </summary>
/// <remarks>
/// WHY THE GROUND. Nothing in memory says where a room was put - no tile reaches its room, see
/// RoomFiles - but two things say the same in different words: a room's k slots name the ground
/// type at each of their corners, and the terrain holds the ground type the game laid at every tile
/// corner (TerrainGroundTypes, which TileCornerData was identified as by its size and checked
/// against the walkable ground). A room is a STAMP of corner types, and the area an array to find
/// it in - which is what that type's own remarks foresaw.
///
/// THE STAMP IS THE ROOM'S ONE BY ONE SLOTS. Their corners are placed by the convention the files
/// themselves settle - see RoomSlot - and a corner two slots both name is named once; one they
/// disagree on is left out. A slot bigger than one tile is left out and counted: a deserted room
/// writes k slots three by three beside one another, which no footprint reading explains, and a
/// stamp built on a guess about them would be a guess with numbers on it.
///
/// EVERY WAY ROUND. Which way the area's rows run against a room's lines is nothing this has to
/// know: the room is tried in all eight placements, so whatever the area's axes are, one of them
/// is the room's. A corner type nought - "no ground type" - is a type like any other and must meet
/// a corner the area lists with a blank name, as every area's list begins with one.
///
/// EXACT, OR THE NEAREST. A room laid where the ground says it was agrees at every corner, and those
/// are listed; where none does, the few nearest are listed instead with how far off they are,
/// because "nothing" and "nearly here" are different answers to a person checking the map.
/// </remarks>
public static class RoomFinder
{
    /// <summary>Most exact candidates listed; the rest are counted.</summary>
    public const int MostCandidates = 200;

    /// <summary>How many of the nearest are listed where nothing matches exactly.</summary>
    public const int Nearest = 5;

    /// <summary>A stamp value meaning "a corner the area lists with a blank name".</summary>
    private const int Blank = -2;

    /// <summary>A placement in words, for a list a person reads.</summary>
    public static string Said(int turn)
        => (turn & 3) switch
        {
            0 => "as written",
            1 => "turned 90",
            2 => "turned 180",
            _ => "turned 270",
        } + (turn >= 4 ? ", mirrored" : string.Empty);

    /// <summary>
    /// Every place on the area's ground the room's corner stamp fits. Never throws.
    /// </summary>
    /// <param name="room">The room, read with its slots.</param>
    /// <param name="ground">The area's ground types per tile corner.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    /// <param name="most">Most exact candidates listed.</param>
    public static RoomSearch Find(RoomLayout room, TerrainGroundTypes ground, int tilesX, int tilesY, int most = MostCandidates)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(ground);

        if (!room.Ready)
        {
            return RoomSearch.Not($"the room did not read: {room.Why}");
        }

        if (room.Slots.Count == 0)
        {
            return RoomSearch.Not(room.SlotsWhy.Length > 0 ? $"the room's grid did not read: {room.SlotsWhy}" : "the room has no slot grid");
        }

        if (!ground.Trusted)
        {
            return RoomSearch.Not($"the area's ground types are not to be trusted: {ground.Note}");
        }

        (Dictionary<(int U, int V), int>? stamp, int left, string why) = Stamped(room, ground);
        if (stamp is null)
        {
            return RoomSearch.Not(why);
        }

        if (stamp.Count == 0)
        {
            return RoomSearch.Not($"the room has no one by one k slot to make a stamp of - {left} bigger ones left out") with { Left = left };
        }

        // THE AREA ONCE, as a flat array: the search asks every corner many times over.
        int across = tilesX + 1;
        var area = new int[across * (tilesY + 1)];
        for (var y = 0; y <= tilesY; y++)
        {
            for (var x = 0; x <= tilesX; x++)
            {
                area[(y * across) + x] = ground.At(x, y);
            }
        }

        var blank = new bool[ground.Types.Count];
        for (var type = 0; type < blank.Length; type++)
        {
            blank[type] = ground.Types[type].Length == 0;
        }

        var exact = new List<RoomCandidate>();
        int more = 0;
        var near = new List<RoomCandidate>();
        int corners = stamp.Count;
        var us = new int[corners];
        var vs = new int[corners];
        var wants = new int[corners];

        for (var turn = 0; turn < 8; turn++)
        {
            (int wide, int tall) = Placed(stamp, room.Width, room.Height, turn, us, vs, wants);
            for (var y = 0; y + tall <= tilesY; y++)
            {
                for (var x = 0; x + wide <= tilesX; x++)
                {
                    // EXACT FIRST, out at the first corner that disagrees - the ordinary case by far.
                    int at = 0;
                    while (at < corners && Agrees(area[((y + vs[at]) * across) + x + us[at]], wants[at], blank))
                    {
                        at++;
                    }

                    if (at == corners)
                    {
                        if (exact.Count < most)
                        {
                            exact.Add(new RoomCandidate(x, y, turn, wide, tall, corners, corners));
                        }
                        else
                        {
                            more++;
                        }

                        continue;
                    }

                    // THE NEAREST ONLY WHILE NOTHING IS EXACT, and only while it could still make the list.
                    if (exact.Count == 0)
                    {
                        int worst = near.Count < Nearest ? corners : corners - near[^1].Matched;
                        int misses = 1;
                        for (at++; at < corners && misses < worst; at++)
                        {
                            if (!Agrees(area[((y + vs[at]) * across) + x + us[at]], wants[at], blank))
                            {
                                misses++;
                            }
                        }

                        if (misses < worst || near.Count < Nearest)
                        {
                            Nearer(near, new RoomCandidate(x, y, turn, wide, tall, corners - misses, corners));
                        }
                    }
                }
            }
        }

        return new RoomSearch(exact.Count > 0 ? exact : near, corners, more, left, string.Empty);
    }

    /// <summary>
    /// The room's corner stamp - grid corner to the area type it must be - or null and why.
    /// </summary>
    private static (Dictionary<(int U, int V), int>? Stamp, int Left, string Why) Stamped(RoomLayout room, TerrainGroundTypes ground)
    {
        var stamp = new Dictionary<(int U, int V), int>();
        var torn = new HashSet<(int U, int V)>();
        int left = 0;
        for (var line = 0; line < room.Height; line++)
        {
            for (var column = 0; column < room.Width; column++)
            {
                RoomSlot slot = room.SlotAt(column, line);
                if (!slot.IsTile)
                {
                    continue;
                }

                if (slot.Width != 1 || slot.Height != 1)
                {
                    left++;
                    continue;
                }

                for (var corner = 0; corner < 4; corner++)
                {
                    int index = slot.Ground(corner);
                    int wants;
                    if (index == 0)
                    {
                        wants = Blank;
                    }
                    else
                    {
                        string name = room.Named(index);
                        wants = TypeOf(ground, name);
                        if (wants < 0)
                        {
                            return (null, left, name.Length == 0
                                ? $"a slot names ground type {index}, past the room's {room.Strings.Count} strings"
                                : $"the room's ground type {name} is not among this area's - it is not laid here");
                        }
                    }

                    // Down-left at the slot's own column and line, then round: see RoomSlot.
                    (int du, int dv) = corner switch
                    {
                        0 => (0, 0),
                        1 => (1, 0),
                        2 => (1, 1),
                        _ => (0, 1),
                    };
                    (int U, int V) at = (column + du, line + dv);
                    if (torn.Contains(at))
                    {
                        continue;
                    }

                    if (stamp.TryGetValue(at, out int had) && had != wants)
                    {
                        stamp.Remove(at);
                        torn.Add(at);
                        continue;
                    }

                    stamp[at] = wants;
                }
            }
        }

        return (stamp, left, string.Empty);
    }

    /// <summary>The area's index for a ground type file, or -1.</summary>
    private static int TypeOf(TerrainGroundTypes ground, string name)
    {
        string wanted = name.Replace('\\', '/');
        for (var type = 0; type < ground.Types.Count; type++)
        {
            if (string.Equals(ground.Types[type].Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return -1;
    }

    /// <summary>
    /// The stamp laid one of the eight ways, into the three arrays, and the footprint's size that way.
    /// </summary>
    /// <remarks>
    /// Mirrored first where it is, then turned a quarter at a time: a quarter turn takes a corner
    /// (u, v) of a w by h footprint to (h - v, u) of an h by w one.
    /// </remarks>
    private static (int Wide, int Tall) Placed(
        Dictionary<(int U, int V), int> stamp, int width, int height, int turn, int[] us, int[] vs, int[] wants)
    {
        int quarters = turn & 3;
        bool mirrored = turn >= 4;
        int wide = (quarters & 1) == 0 ? width : height;
        int tall = (quarters & 1) == 0 ? height : width;
        int at = 0;
        foreach (((int u0, int v0), int want) in stamp)
        {
            int u = mirrored ? width - u0 : u0;
            int v = v0;
            int w = width;
            int h = height;
            for (var quarter = 0; quarter < quarters; quarter++)
            {
                (u, v) = (h - v, u);
                (w, h) = (h, w);
            }

            us[at] = u;
            vs[at] = v;
            wants[at] = want;
            at++;
        }

        return (wide, tall);
    }

    private static bool Agrees(int area, int wants, bool[] blank)
        => wants == Blank ? (uint)area < (uint)blank.Length && blank[area] : area == wants;

    /// <summary>Puts a candidate into the short list of the nearest, best first, keeping it to <see cref="Nearest"/>.</summary>
    private static void Nearer(List<RoomCandidate> near, RoomCandidate candidate)
    {
        int at = near.Count;
        while (at > 0 && near[at - 1].Matched < candidate.Matched)
        {
            at--;
        }

        near.Insert(at, candidate);
        if (near.Count > Nearest)
        {
            near.RemoveAt(near.Count - 1);
        }
    }
}
