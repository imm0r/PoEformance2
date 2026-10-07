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

    /// <summary>How many of the room's k slots met a laid tile whose definition could be read - nought where the tiles were not to hand.</summary>
    public int Tiles { get; init; }

    /// <summary>How many of those the laid tile agrees with - see <see cref="RoomFinder"/>.</summary>
    public int TilesAgree { get; init; }

    /// <summary>How many of <see cref="Tiles"/> are slots bigger than one tile - the ones the ground stamp leaves out.</summary>
    public int Big { get; init; }

    /// <summary>How many of those agree.</summary>
    public int BigAgree { get; init; }
}

/// <summary>
/// What a tile definition says about itself that a room's slot asks for: its size, its tag, and its edge and corner ground types.
/// </summary>
/// <param name="Width">Tiles across, as authored.</param>
/// <param name="Height">Tiles down.</param>
/// <param name="Tag">Its tag, or empty.</param>
/// <param name="Edges">Its four edge types, down, right, up, left - see TileDefinition.Edges.</param>
/// <param name="Grounds">Its four corner ground types, down-left round to up-left.</param>
public sealed record TileIdentity(int Width, int Height, string Tag, IReadOnlyList<string> Edges, IReadOnlyList<string> Grounds)
{
    /// <summary>A definition's identity, or null where it did not read or does not carry all four of each.</summary>
    public static TileIdentity? Of(TileDefinition? definition)
        => definition is { Ready: true, Width: > 0, Height: > 0 } && definition.Edges.Count == 4 && definition.Grounds.Count == 4
            ? new TileIdentity(definition.Width, definition.Height, definition.Tag, definition.Edges, definition.Grounds)
            : null;
}

/// <summary>Where a candidate and the area part ways, for the map to mark.</summary>
/// <param name="Corners">The area corners whose ground is not the room's, by tile corner.</param>
/// <param name="Tiles">The area tiles whose definition is not what the room's slot there asks for, by tile.</param>
public sealed record RoomMisses(IReadOnlyList<(int X, int Y)> Corners, IReadOnlyList<(int X, int Y)> Tiles)
{
    /// <summary>Nothing missed.</summary>
    public static RoomMisses None { get; } = new([], []);
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

    /// <summary>Where each candidate parts with the area, in the same order - empty for a search that did not run.</summary>
    public IReadOnlyList<RoomMisses> Misses { get; init; } = [];

    /// <summary>Whether the candidates were checked against the tiles actually laid.</summary>
    public bool TileChecked { get; init; }

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
///
/// AND THE TILES, WHERE THEY ARE TO HAND. A ground stamp cannot tell apart rooms that share their
/// border - Atziri's temple is a grid of them, every candidate 76 of 80 - but every k slot asks for
/// a TILE, and the terrain says which tile was laid on every cell (TerrainTiles). So each candidate
/// is checked slot by slot against the definition laid where that slot falls, the big slots
/// included: those are what the stamp leaves out and what makes a room this room. The check is the
/// part of a slot no placement changes - the size as an unordered pair, the tag where the slot
/// names one, and the edge and corner ground types as sets of four - because which side of a turned
/// tile meets which side of a turned room is a convention nothing here has settled for tiles. The
/// cell a slot falls on is the one at its own column and line, which every reading of a slot's
/// footprint covers. Candidates are then ranked by how many tiles agree, and from a wider pool than
/// is shown, so the room among a dozen equal borders can rise to the top.
/// </remarks>
public static class RoomFinder
{
    /// <summary>Most exact candidates listed; the rest are counted.</summary>
    public const int MostCandidates = 200;

    /// <summary>How many of the nearest are listed where nothing matches exactly.</summary>
    public const int Nearest = 5;

    /// <summary>How many of the nearest are kept to be ranked by their tiles before the list is cut to <see cref="Nearest"/>.</summary>
    private const int Pool = 64;

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
    /// <param name="tiles">The tiles actually laid, to check each candidate's slots against; null leaves the check out.</param>
    /// <param name="identity">A tile file's identity - its definition with the inheritance followed - or null where it does not read.</param>
    /// <param name="most">Most exact candidates listed.</param>
    public static RoomSearch Find(
        RoomLayout room,
        TerrainGroundTypes ground,
        int tilesX,
        int tilesY,
        TerrainTiles? tiles = null,
        Func<string, TileIdentity?>? identity = null,
        int most = MostCandidates)
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
        bool checking = tiles is not null && identity is not null;
        int keep = checking ? Pool : Nearest;
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
                        int worst = near.Count < keep ? corners : corners - near[^1].Matched;
                        int misses = 1;
                        for (at++; at < corners && misses < worst; at++)
                        {
                            if (!Agrees(area[((y + vs[at]) * across) + x + us[at]], wants[at], blank))
                            {
                                misses++;
                            }
                        }

                        if (misses < worst || near.Count < keep)
                        {
                            Nearer(near, new RoomCandidate(x, y, turn, wide, tall, corners - misses, corners), keep);
                        }
                    }
                }
            }
        }

        List<RoomCandidate> found = exact.Count > 0 ? exact : near;
        var check = checking ? new TileCheck(room, tiles!, identity!) : null;
        if (check is not null)
        {
            for (var one = 0; one < found.Count; one++)
            {
                found[one] = check.Scored(found[one]);
            }

            // RANKED BY THE TILES, the ground breaking ties - stably, so equals keep the grid's order.
            found = exact.Count > 0
                ? [.. found.OrderByDescending(one => one.TilesAgree)]
                : [.. found.OrderByDescending(one => one.TilesAgree).ThenByDescending(one => one.Matched)];
        }

        if (exact.Count == 0 && found.Count > Nearest)
        {
            found.RemoveRange(Nearest, found.Count - Nearest);
        }

        var parted = new RoomMisses[found.Count];
        for (var one = 0; one < found.Count; one++)
        {
            IReadOnlyList<(int X, int Y)> astray = found[one].Exact ? [] : Astray(found[one], stamp, room, area, across, blank, us, vs, wants);
            IReadOnlyList<(int X, int Y)> unlike = check?.Unlike(found[one]) ?? [];
            parted[one] = astray.Count == 0 && unlike.Count == 0 ? RoomMisses.None : new RoomMisses(astray, unlike);
        }

        return new RoomSearch(found, corners, more, left, string.Empty) { Misses = parted, TileChecked = check is not null };
    }

    /// <summary>The area corners where one candidate's stamp and the ground disagree.</summary>
    private static List<(int X, int Y)> Astray(
        RoomCandidate candidate, Dictionary<(int U, int V), int> stamp, RoomLayout room,
        int[] area, int across, bool[] blank, int[] us, int[] vs, int[] wants)
    {
        Placed(stamp, room.Width, room.Height, candidate.Turn, us, vs, wants);
        var astray = new List<(int X, int Y)>();
        for (var at = 0; at < stamp.Count; at++)
        {
            int x = candidate.X + us[at];
            int y = candidate.Y + vs[at];
            if (!Agrees(area[(y * across) + x], wants[at], blank))
            {
                astray.Add((x, y));
            }
        }

        return astray;
    }

    /// <summary>
    /// The area cell a room's slot falls on, for a candidate laid one of the eight ways: its centre, laid that way, floored.
    /// </summary>
    /// <remarks>The corners' own arithmetic on a grid twice as fine, so a centre is a whole number.</remarks>
    internal static (int X, int Y) CellOf(int column, int line, int width, int height, int turn)
    {
        int u = (2 * column) + 1;
        int v = (2 * line) + 1;
        int w = 2 * width;
        int h = 2 * height;
        if (turn >= 4)
        {
            u = w - u;
        }

        for (var quarter = 0; quarter < (turn & 3); quarter++)
        {
            (u, v) = (h - v, u);
            (w, h) = (h, w);
        }

        return ((u - 1) / 2, (v - 1) / 2);
    }

    /// <summary>
    /// A room's k slots checked against the tiles laid under a candidate - see the class remarks.
    /// </summary>
    private sealed class TileCheck
    {
        private readonly RoomLayout _room;
        private readonly TerrainTiles _tiles;
        private readonly Func<string, TileIdentity?> _identity;
        private readonly List<Wanted> _slots = [];
        private readonly Dictionary<int, Laid?> _laid = [];

        public TileCheck(RoomLayout room, TerrainTiles tiles, Func<string, TileIdentity?> identity)
        {
            _room = room;
            _tiles = tiles;
            _identity = identity;
            for (var line = 0; line < room.Height; line++)
            {
                for (var column = 0; column < room.Width; column++)
                {
                    RoomSlot slot = room.SlotAt(column, line);
                    if (slot.IsTile)
                    {
                        _slots.Add(new Wanted(
                            column, line, slot.Width, slot.Height, room.Named(slot.Tag),
                            Sorted(slot.Edge(0), slot.Edge(1), slot.Edge(2), slot.Edge(3)),
                            Sorted(slot.Ground(0), slot.Ground(1), slot.Ground(2), slot.Ground(3))));
                    }
                }
            }
        }

        /// <summary>The candidate with its tile counts.</summary>
        public RoomCandidate Scored(RoomCandidate candidate)
        {
            int tiles = 0, agree = 0, big = 0, bigAgree = 0;
            foreach (Wanted slot in _slots)
            {
                bool? same = Agrees(candidate, slot, out _);
                if (same is null)
                {
                    continue;
                }

                bool large = slot.Width > 1 || slot.Height > 1;
                tiles++;
                big += large ? 1 : 0;
                if (same == true)
                {
                    agree++;
                    bigAgree += large ? 1 : 0;
                }
            }

            return candidate with { Tiles = tiles, TilesAgree = agree, Big = big, BigAgree = bigAgree };
        }

        /// <summary>The area tiles under a candidate that are not what their slot asks for.</summary>
        public List<(int X, int Y)> Unlike(RoomCandidate candidate)
        {
            var unlike = new List<(int X, int Y)>();
            foreach (Wanted slot in _slots)
            {
                if (Agrees(candidate, slot, out (int X, int Y) cell) == false)
                {
                    unlike.Add(cell);
                }
            }

            return unlike;
        }

        /// <summary>Whether the tile under a slot is what it asks for, or null where there is no tile or no definition to say.</summary>
        private bool? Agrees(RoomCandidate candidate, Wanted slot, out (int X, int Y) cell)
        {
            (int x, int y) = CellOf(slot.Column, slot.Line, _room.Width, _room.Height, candidate.Turn);
            cell = (candidate.X + x, candidate.Y + y);
            int id = _tiles.IdAt(cell.X, cell.Y);
            if (id < 0)
            {
                return null;
            }

            if (!_laid.TryGetValue(id, out Laid? laid))
            {
                laid = _identity(_tiles.Paths[id]) is { Edges.Count: 4, Grounds.Count: 4 } tile
                    ? new Laid(tile.Width, tile.Height, tile.Tag, Sorted(tile.Edges), Sorted(tile.Grounds))
                    : null;
                _laid[id] = laid;
            }

            if (laid is null)
            {
                return null;
            }

            bool sized = (laid.Width == slot.Width && laid.Height == slot.Height) || (laid.Width == slot.Height && laid.Height == slot.Width);
            return sized
                && (slot.Tag.Length == 0 || string.Equals(slot.Tag, laid.Tag, StringComparison.OrdinalIgnoreCase))
                && laid.Edges.SequenceEqual(slot.Edges, StringComparer.OrdinalIgnoreCase)
                && laid.Grounds.SequenceEqual(slot.Grounds, StringComparer.OrdinalIgnoreCase);
        }

        private string[] Sorted(int a, int b, int c, int d) => Sorted([_room.Named(a), _room.Named(b), _room.Named(c), _room.Named(d)]);

        private static string[] Sorted(IReadOnlyList<string> four)
        {
            string[] sorted = [.. four.Select(one => one.Replace('\\', '/'))];
            Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
            return sorted;
        }

        private readonly record struct Wanted(int Column, int Line, int Width, int Height, string Tag, string[] Edges, string[] Grounds);

        private sealed record Laid(int Width, int Height, string Tag, string[] Edges, string[] Grounds);
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

    /// <summary>Puts a candidate into the short list of the nearest, best first, keeping it to <paramref name="keep"/>.</summary>
    private static void Nearer(List<RoomCandidate> near, RoomCandidate candidate, int keep)
    {
        int at = near.Count;
        while (at > 0 && near[at - 1].Matched < candidate.Matched)
        {
            at--;
        }

        near.Insert(at, candidate);
        if (near.Count > keep)
        {
            near.RemoveAt(near.Count - 1);
        }
    }
}
