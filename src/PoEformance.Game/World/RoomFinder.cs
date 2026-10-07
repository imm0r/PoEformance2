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

/// <summary>One placement of a room and where it parts with the area.</summary>
/// <param name="Where">The placement.</param>
/// <param name="Misses">Where its corners and tiles disagree with the area.</param>
public sealed record RoomPlace(RoomCandidate Where, RoomMisses Misses);

/// <summary>What a search for a room came to: the places, and what it rested on.</summary>
/// <param name="Candidates">Where any placement fits every corner, those - ranked by the tiles laid where the tiles were to hand, else in grid order; where none does, the nearest few, best first.</param>
/// <param name="Corners">How many corners the room's stamp holds.</param>
/// <param name="More">Placements that fit every corner past the cap, counted and not listed.</param>
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

    /// <summary>Corners the room's slots leave unnamed - nought, no ground stated - and so free to be anything.</summary>
    public int Free { get; init; }

    /// <summary>How many placements fit every corner the room names, listed or not.</summary>
    public int Fits { get; init; }

    /// <summary>Whether any candidate agrees everywhere.</summary>
    public bool Found => Candidates.Count > 0 && Candidates[0].Exact;

    /// <summary>What the search scored with, kept for <see cref="Around"/>; null for a search that did not run.</summary>
    internal RoomPlacements? Placements { get; init; }

    /// <summary>
    /// The best placement whose footprint covers one tile - most tiles agreeing, then most corners - or null outside the area or for a search that did not run.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES OF THE COMPARISON where the room is known to be: a person standing in it asks this
    /// of the tile under them, and sees how the room's own corners and slots meet what the area laid
    /// there - whether or not that place made the list. Not thread-safe against itself: one call at a time.
    /// </remarks>
    public RoomPlace? Around(int tileX, int tileY) => Placements?.Around(tileX, tileY);
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
/// is the room's.
///
/// A CORNER TYPE NOUGHT NAMES NOTHING, and is left free. poe_data_tools reads it as no ground at all
/// (None), and the rooms bear that out: the channel's 1open_01.arm writes nought at exactly its 36
/// inner corners - the walkable floor - while the area gives that floor a named type. Read as "must
/// meet a corner the area lists blank", as the first version did, every inner corner of every room
/// missed: Atziri's temple rooms agreed 76 of 80 everywhere and none exactly, a seepage room 109 of
/// 239, and the nearest of those lay in the void past the map's edge, where blank ground is.
///
/// EXACT, OR THE NEAREST. A room laid where the ground says it was agrees at every corner, and those
/// are listed; where none does, the few nearest are listed instead with how far off they are,
/// because "nothing" and "nearly here" are different answers to a person checking the map.
///
/// AND THE TILES, WHERE THEY ARE TO HAND. A ground stamp cannot tell apart rooms that share their
/// border - Atziri's temple is a grid of them - but every k slot asks for a TILE, and the terrain
/// says which tile was laid on every cell (TerrainTiles). So each placement is checked slot by slot
/// against the definition laid where that slot falls, the big slots included: those are what the
/// stamp leaves out and what makes a room this room. The check is the part of a slot no placement
/// changes - the size as an unordered pair, the tag where the slot names one, and the edge and corner
/// ground types as sets of four - because which side of a turned tile meets which side of a turned
/// room is a convention nothing here has settled for tiles. The cell a slot falls on is the one at
/// its own column and line, which every reading of a slot's footprint covers.
///
/// RANKED ACROSS EVERY PLACEMENT, corners first and the tiles breaking ties. The first version kept
/// the first two hundred exact fits in grid order and ranked only those, and a seepage area showed
/// what that does: with most corners free, a room's few named ones are its rim, the void past the
/// map's edge fits a rim thousands of times over (4696 for one room), and those thousands come first
/// in grid order - every room was "found" in the void at the map's top, its real place never ranked.
/// Every placement is scored now, bounded by the worst one still on the list so the scoring stays
/// cheap, and a tile's verdict against a slot is worked out once per tile file and slot kind, not
/// once per try.
/// </remarks>
public static class RoomFinder
{
    /// <summary>Most exact candidates listed; the rest are counted.</summary>
    public const int MostCandidates = 200;

    /// <summary>How many of the nearest are listed where nothing matches exactly.</summary>
    public const int Nearest = 5;

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
    /// <param name="tiles">The tiles actually laid, to check each placement's slots against; null leaves the check out.</param>
    /// <param name="identity">A tile file's identity - its definition with the inheritance followed - or null where it does not read. Called from the search's thread.</param>
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

        (Dictionary<(int U, int V), int>? stamp, int left, int free, string why) = Stamped(room, ground);
        if (stamp is null)
        {
            return RoomSearch.Not(why);
        }

        if (stamp.Count == 0)
        {
            return RoomSearch.Not($"the room names no corner's ground in a one by one k slot - {left} bigger slots left out, {free} corners unnamed")
                with { Left = left, Free = free };
        }

        var placements = new RoomPlacements(room, stamp, ground, tilesX, tilesY, tiles, identity);
        bool checking = placements.Checks;
        int corners = stamp.Count;
        int capacity = Math.Max(Math.Max(most, Nearest), 1);
        var ranked = new List<RoomCandidate>(capacity + 1);
        int fits = 0;
        int across = tilesX + 1;

        for (var turn = 0; turn < 8; turn++)
        {
            (int wide, int tall) = placements.Size(turn);
            for (var y = 0; y + tall <= tilesY; y++)
            {
                for (var x = 0; x + wide <= tilesX; x++)
                {
                    // BOUNDED BY THE WORST ON THE LIST: a placement that cannot beat it is left at the
                    // first miss too many. Ties keep the earlier one, so equals stay in grid order - and
                    // where the tiles break ties, a tie on corners must still be scored on them.
                    bool full = ranked.Count == capacity;
                    int allowed = !full ? corners : corners - ranked[^1].Matched - (checking ? 0 : 1);
                    int matched = placements.Matched((y * across) + x, turn, allowed);
                    if (matched < 0)
                    {
                        continue;
                    }

                    if (matched == corners)
                    {
                        fits++;
                    }

                    var candidate = new RoomCandidate(x, y, turn, wide, tall, matched, corners);
                    if (checking)
                    {
                        int least = full && matched == ranked[^1].Matched ? ranked[^1].TilesAgree + 1 : 0;
                        if (!placements.Tiles(x, y, turn, least, out int laid, out int agree, out int big, out int bigAgree))
                        {
                            continue;
                        }

                        candidate = candidate with { Tiles = laid, TilesAgree = agree, Big = big, BigAgree = bigAgree };
                    }

                    Ranked(ranked, candidate, capacity);
                }
            }
        }

        // THE FITS ARE THE LIST WHERE THERE ARE ANY - they lead it, having every corner - and the
        // nearest few where there are none.
        int exact = 0;
        while (exact < ranked.Count && ranked[exact].Exact)
        {
            exact++;
        }

        List<RoomCandidate> found = fits > 0
            ? ranked.GetRange(0, Math.Min(exact, most))
            : ranked.GetRange(0, Math.Min(ranked.Count, Nearest));

        var parted = new RoomMisses[found.Count];
        for (var one = 0; one < found.Count; one++)
        {
            parted[one] = placements.Misses(found[one]);
        }

        return new RoomSearch(found, corners, fits > 0 ? fits - found.Count : 0, left, string.Empty)
        {
            Misses = parted,
            TileChecked = checking,
            Free = free,
            Fits = fits,
            Placements = placements,
        };
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

    /// <summary>Whether one placement ranks above another: more corners, then more tiles agreeing.</summary>
    internal static bool Above(in RoomCandidate one, in RoomCandidate other)
        => one.Matched != other.Matched ? one.Matched > other.Matched : one.TilesAgree > other.TilesAgree;

    /// <summary>Puts a candidate into the ranked list after every one it does not beat, keeping the list to <paramref name="capacity"/>.</summary>
    private static void Ranked(List<RoomCandidate> ranked, RoomCandidate candidate, int capacity)
    {
        int low = 0;
        int high = ranked.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (Above(candidate, ranked[middle]))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        if (low >= capacity)
        {
            return;
        }

        ranked.Insert(low, candidate);
        if (ranked.Count > capacity)
        {
            ranked.RemoveAt(ranked.Count - 1);
        }
    }

    /// <summary>
    /// The room's corner stamp - grid corner to the area type it must be - or null and why.
    /// </summary>
    private static (Dictionary<(int U, int V), int>? Stamp, int Left, int Free, string Why) Stamped(RoomLayout room, TerrainGroundTypes ground)
    {
        var stamp = new Dictionary<(int U, int V), int>();
        var torn = new HashSet<(int U, int V)>();
        var unnamed = new HashSet<(int U, int V)>();
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
                    // Down-left at the slot's own column and line, then round: see RoomSlot.
                    (int du, int dv) = corner switch
                    {
                        0 => (0, 0),
                        1 => (1, 0),
                        2 => (1, 1),
                        _ => (0, 1),
                    };
                    (int U, int V) at = (column + du, line + dv);

                    // NOUGHT NAMES NO GROUND - left free; see the class remarks.
                    int index = slot.Ground(corner);
                    if (index == 0)
                    {
                        unnamed.Add(at);
                        continue;
                    }

                    string name = room.Named(index);
                    int wants = TypeOf(ground, name);
                    if (wants < 0)
                    {
                        return (null, left, 0, name.Length == 0
                            ? $"a slot names ground type {index}, past the room's {room.Strings.Count} strings"
                            : $"the room's ground type {name} is not among this area's - it is not laid here");
                    }

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

        // A CORNER ONE SLOT LEAVES FREE AND ANOTHER NAMES is named; only the ones nobody names are free.
        unnamed.ExceptWith(stamp.Keys);
        unnamed.ExceptWith(torn);
        return (stamp, left, unnamed.Count, string.Empty);
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
    internal static (int Wide, int Tall) Placed(
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
}
