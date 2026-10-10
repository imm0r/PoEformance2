using System.Numerics;
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
/// <remarks>
/// AND WHICH OF THOSE ARE A JOIN, where the walkable ground was to hand. Where a room is joined to the
/// rest of the map, the area lays its own tiles over the room's closed rim - in seepage, every miss of
/// two rooms found where they stand lay at a join: a run of tiles turned to walkable floor, their
/// corners with it, and one more tile at each end of the run, the wall stopping there. So a miss is
/// counted as a join by that pattern, read off the area alone: an OPENING is a rim tile that misses,
/// can be walked on and has walkable ground across the rim; a CAP is a rim tile that misses beside an
/// opening; a JOIN CORNER is a missed corner of an opening. The rule comes from one room's screenshot
/// and only says what the map marks - nothing is ranked by it.
/// </remarks>
public sealed record RoomMisses(IReadOnlyList<(int X, int Y)> Corners, IReadOnlyList<(int X, int Y)> Tiles)
{
    /// <summary>Nothing missed.</summary>
    public static RoomMisses None { get; } = new([], []);

    /// <summary>Whether the misses were sorted into joins and the rest - false where the walkable ground was not to hand.</summary>
    public bool Classified { get; init; }

    /// <summary>Of <see cref="Tiles"/>, the openings: rim tiles that miss, can be walked on and have walkable ground across the rim.</summary>
    public IReadOnlyList<(int X, int Y)> Openings { get; init; } = [];

    /// <summary>Of <see cref="Tiles"/>, the caps: rim tiles that miss beside an opening.</summary>
    public IReadOnlyList<(int X, int Y)> Caps { get; init; } = [];

    /// <summary>Of <see cref="Corners"/>, the ones at a corner of an opening.</summary>
    public IReadOnlyList<(int X, int Y)> JoinCorners { get; init; } = [];

    /// <summary>How many separate runs of openings there are.</summary>
    public int Joins { get; init; }

    /// <summary>How many misses, corners and tiles together, are none of a join's.</summary>
    public int Elsewhere => Corners.Count + Tiles.Count - Openings.Count - Caps.Count - JoinCorners.Count;
}

/// <summary>What part of a join a miss is, if any - see <see cref="RoomMisses"/>.</summary>
public enum RoomJoin
{
    /// <summary>No part of a join.</summary>
    None,

    /// <summary>A rim tile turned to walkable ground.</summary>
    Opening,

    /// <summary>A rim tile that misses beside an opening.</summary>
    Cap,

    /// <summary>A missed corner of an opening.</summary>
    Corner,
}

/// <summary>One place a candidate parts with the area, said in full for the diagnostic list.</summary>
/// <param name="IsCorner">A corner, or else a tile.</param>
/// <param name="X">The area tile or corner, across.</param>
/// <param name="Y">The area tile or corner, down.</param>
/// <param name="Join">What part of a join it is.</param>
/// <param name="RoomU">The room's slot column for a tile, the room's grid corner for a corner.</param>
/// <param name="RoomV">The slot's line, or the corner's.</param>
/// <param name="Sides">The room's own sides it lies on - D, R, U, L, the room's down being its grid's first line - or empty inside.</param>
/// <param name="Wanted">What the room asks for there.</param>
/// <param name="Laid">What the area laid there.</param>
/// <param name="Walkable">Whether the tile has walkable ground - for a corner, false.</param>
/// <param name="SideEdges">A tile's slot's edge types, down, right, up, left, as the file writes them; empty for a corner.</param>
/// <param name="SideExits">A tile's slot's exit pairs, two to a side in the same order, as the file writes them; empty for a corner.</param>
public sealed record RoomPart(
    bool IsCorner,
    int X,
    int Y,
    RoomJoin Join,
    int RoomU,
    int RoomV,
    string Sides,
    string Wanted,
    string Laid,
    bool Walkable,
    IReadOnlyList<string> SideEdges,
    IReadOnlyList<int> SideExits);

/// <summary>One placement of a room and where it parts with the area.</summary>
/// <param name="Where">The placement.</param>
/// <param name="Misses">Where its corners and tiles disagree with the area.</param>
public sealed record RoomPlace(RoomCandidate Where, RoomMisses Misses);

/// <summary>What a search for a room came to: the places, and what it rested on.</summary>
/// <param name="Candidates">Best first: ranked by the tiles laid and then the corners where the tiles were to hand; else the places that fit every corner, in grid order, or where none does the nearest few.</param>
/// <param name="Corners">How many corners the room's stamp holds.</param>
/// <param name="More">Placements that fit every corner and are not listed.</param>
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

    /// <summary>Whether placements covering no walkable tile were left out.</summary>
    public bool Standing { get; init; }

    /// <summary>Whether the room's file has a line of ground overrides.</summary>
    public bool Overrides { get; init; }

    /// <summary>How many of the stamp's corners the file's ground overrides name.</summary>
    public int Overridden { get; init; }

    /// <summary>Why the file's tail did not read as far as the ground overrides, or empty.</summary>
    public string OverridesWhy { get; init; } = string.Empty;

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

    /// <summary>Every place one candidate parts with the area, said in full - tiles, then corners - or empty for a search that did not run.</summary>
    /// <remarks>Safe beside a running <see cref="Around"/>: it reads nothing either keeps.</remarks>
    public IReadOnlyList<RoomPart> Parts(RoomCandidate candidate) => Placements?.Parts(candidate) ?? [];
}

/// <summary>
/// Scores single placements of one room against the area's ground and tiles - for a place found some other way, such as by the room's doodads. See <see cref="RoomFinder.Scorer"/>.
/// </summary>
/// <remarks>Not thread-safe against itself: it reads each tile file's identity once and keeps it, so one call at a time.</remarks>
public sealed class RoomScorer
{
    private readonly RoomPlacements _placements;

    internal RoomScorer(RoomPlacements placements)
    {
        _placements = placements;
    }

    /// <summary>Whether the tiles laid are to hand - without them a score counts corners alone and agrees with no tile.</summary>
    public bool Checks => _placements.Checks;

    /// <summary>The footprint's size laid one of the eight ways.</summary>
    public (int Wide, int Tall) Size(int turn) => _placements.Size(turn);

    /// <summary>
    /// One placement scored in full - its corners and tiles against the area, and where they part - or null where the footprint would lie outside the area.
    /// </summary>
    public RoomPlace? Score(int x, int y, int turn)
    {
        (int wide, int tall) = _placements.Size(turn & 7);
        if (x < 0 || y < 0 || x + wide > _placements.TilesX || y + tall > _placements.TilesY)
        {
            return null;
        }

        RoomCandidate scored = _placements.Scored(x, y, turn & 7);
        return new RoomPlace(scored, _placements.Misses(scored));
    }
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
/// THE TILES, WHERE THEY ARE TO HAND. A ground stamp cannot tell apart rooms that share their
/// border - Atziri's temple is a grid of them - but every k slot asks for a TILE, and the terrain
/// says which tile was laid on every cell (TerrainTiles). So each placement is checked slot by slot
/// against the definition laid where that slot falls, the big slots included: those are what the
/// stamp leaves out and what makes a room this room. The check is the part of a slot no placement
/// changes - the size as an unordered pair, the tag where the slot names one, and the edge and corner
/// ground types as sets of four, a type the slot leaves unnamed meeting any (SlotWants has the rule
/// and the measurement behind it) - because which side of a turned tile meets which side of a turned
/// room is a convention nothing here has settled for tiles. The cell a slot falls on is the one at
/// its own column and line, which every reading of a slot's footprint covers.
///
/// A ROOM OF BIG SLOTS ALONE has no stamp and is not searched here: its tiles place it instead, see
/// RoomTileFinder.
///
/// RANKED BY THE TILES, THEN THE CORNERS - and not by whether every corner fits, because a room
/// does not fit every corner where it lies. Seepage's boss arena, measured where it stands: 208 of
/// its 218 corners and 191 of its 205 tiles, every miss along the one side where the arena joins
/// the rest of the map, the area having laid its own tiles there. The void past the map's edge,
/// meanwhile, fits all 218 corners hundreds of times - a room's named corners are mostly its rim,
/// its floor being left at nought - with 150 tiles. Exactness picked the void; the tiles pick the
/// arena. Where the tiles are not to hand, the corners are all there is: the places that fit every
/// one are listed, or where none does the nearest few, because "nothing" and "nearly here" are
/// different answers to a person checking the map.
///
/// EVERY PLACEMENT IS SCORED, bounded by the worst one still on the list so the scoring stays
/// cheap, and a tile's verdict against a slot is worked out once per tile file and slot kind, not
/// once per try. 0.1.97 kept the first two hundred exact fits in grid order and ranked only those,
/// which in seepage were all void at the map's top.
///
/// ONLY OVER WALKABLE GROUND, where asked: a placement whose footprint covers no tile anybody can
/// stand on is left out. That is the void past the map's edge, and also a room of pure scenery -
/// the terrain's own grouping says areas are full of rooms nobody walks in (see
/// TerrainGrid.WalkableTileMask) - which is why it is a choice and not a rule.
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
    /// Where on the area the room was laid, best first - see the class remarks. Never throws.
    /// </summary>
    /// <param name="room">The room, read with its slots.</param>
    /// <param name="ground">The area's ground types per tile corner.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    /// <param name="tiles">The tiles actually laid, to check each placement's slots against; null leaves the check out.</param>
    /// <param name="identity">A tile file's identity - its definition with the inheritance followed - or null where it does not read. Called from the search's thread.</param>
    /// <param name="most">Most candidates listed.</param>
    /// <param name="walkable">Which tiles have ground anybody can stand on, row by row - TerrainGrid.WalkableTileMask - to leave out every placement covering none, and to tell a join among the misses; null leaves both out, and a mask of no walkable tile at all leaves nothing out.</param>
    /// <param name="anywhere">Whether to keep the placements covering no walkable tile too - the mask still telling the joins.</param>
    public static RoomSearch Find(
        RoomLayout room,
        TerrainGroundTypes ground,
        int tilesX,
        int tilesY,
        TerrainTiles? tiles = null,
        Func<string, TileIdentity?>? identity = null,
        int most = MostCandidates,
        bool[]? walkable = null,
        bool anywhere = false)
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

        (Dictionary<(int U, int V), int>? stamp, int left, int free, int overridden, string why) = Stamped(room, ground);
        if (stamp is null)
        {
            return RoomSearch.Not(why);
        }

        if (stamp.Count == 0)
        {
            return RoomSearch.Not($"the room names no corner's ground in a one by one k slot - {left} bigger slots left out, {free} corners unnamed")
                with { Left = left, Free = free };
        }

        bool[]? mask = walkable is not null && walkable.Length >= tilesX * tilesY ? walkable : null;
        var placements = new RoomPlacements(room, stamp, ground, tilesX, tilesY, tiles, identity, mask);
        bool checking = placements.Checks;
        int[]? standing = anywhere ? null : Summed(mask, tilesX, tilesY);
        int corners = stamp.Count;
        int capacity = Math.Max(checking ? most : Math.Max(most, Nearest), 1);
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
                    if (standing is not null && Covered(standing, across, x, y, wide, tall) == 0)
                    {
                        continue;
                    }

                    // EVERY CORNER OR NOT, for the count, out at the first miss - the ordinary case by far.
                    int at = (y * across) + x;
                    bool exact = placements.Matched(at, turn, 0) == corners;
                    fits += exact ? 1 : 0;

                    // BOUNDED BY THE WORST ON THE LIST: a placement that cannot beat it is left as soon
                    // as it cannot. Ties keep the earlier one, so equals stay in grid order.
                    bool full = ranked.Count == capacity;
                    RoomCandidate worst = full ? ranked[^1] : default;
                    int laid = 0, agree = 0, big = 0, bigAgree = 0;
                    if (checking && !placements.Tiles(x, y, turn, full ? worst.TilesAgree : 0, out laid, out agree, out big, out bigAgree))
                    {
                        continue;
                    }

                    int matched = exact
                        ? corners
                        : placements.Matched(at, turn, full && agree == worst.TilesAgree ? corners - worst.Matched - 1 : corners);
                    if (matched < 0)
                    {
                        continue;
                    }

                    Ranked(ranked, new RoomCandidate(x, y, turn, wide, tall, matched, corners)
                    {
                        Tiles = laid,
                        TilesAgree = agree,
                        Big = big,
                        BigAgree = bigAgree,
                    }, capacity);
                }
            }
        }

        // WITHOUT THE TILES, the places that fit every corner where there are any - they lead the
        // list, having the most corners - and the nearest few where there are none.
        List<RoomCandidate> found = ranked;
        if (!checking)
        {
            int exact = 0;
            while (exact < ranked.Count && ranked[exact].Exact)
            {
                exact++;
            }

            found = fits > 0
                ? ranked.GetRange(0, Math.Min(exact, most))
                : ranked.GetRange(0, Math.Min(ranked.Count, Nearest));
        }

        var parted = new RoomMisses[found.Count];
        var listedExact = 0;
        for (var one = 0; one < found.Count; one++)
        {
            parted[one] = placements.Misses(found[one]);
            listedExact += found[one].Exact ? 1 : 0;
        }

        return new RoomSearch(found, corners, fits - listedExact, left, string.Empty)
        {
            Misses = parted,
            TileChecked = checking,
            Free = free,
            Fits = fits,
            Standing = standing is not null,
            Overrides = room.GroundOverrides.Count > 0,
            Overridden = overridden,
            OverridesWhy = room.OverridesWhy,
            Placements = placements,
        };
    }

    /// <summary>
    /// A scorer for single placements of the room - the same yardstick <see cref="Find"/> ranks by, without the search - or null and why where the room cannot be stamped against this area.
    /// </summary>
    /// <remarks>
    /// FOR A PLACE THE DOODADS FOUND. The search tries every tile of the area eight ways round and
    /// takes seconds a room; with the area's whole room set to place - fifty rooms and more - that is
    /// minutes nobody waits. The doodads say where a room stands in milliseconds, and the tiles are
    /// then asked about that one place: how many of its slots the laid tiles agree with, which is
    /// what tells two variants apart that share every doodad (RoomDoodadFinder.Settle).
    /// </remarks>
    /// <param name="room">The room, read with its slots.</param>
    /// <param name="ground">The area's ground types per tile corner.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    /// <param name="tiles">The tiles actually laid; null scores corners alone.</param>
    /// <param name="identity">A tile file's identity, or null where it does not read.</param>
    /// <param name="walkable">Which tiles have walkable ground, to tell a join among the misses; null leaves that out.</param>
    public static (RoomScorer? Scorer, string Why) Scorer(
        RoomLayout room,
        TerrainGroundTypes ground,
        int tilesX,
        int tilesY,
        TerrainTiles? tiles = null,
        Func<string, TileIdentity?>? identity = null,
        bool[]? walkable = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(ground);
        if (!room.Ready)
        {
            return (null, $"the room did not read: {room.Why}");
        }

        if (room.Slots.Count == 0)
        {
            return (null, room.SlotsWhy.Length > 0 ? $"the room's grid did not read: {room.SlotsWhy}" : "the room has no slot grid");
        }

        if (!ground.Trusted)
        {
            return (null, $"the area's ground types are not to be trusted: {ground.Note}");
        }

        if (tilesX <= 0 || tilesY <= 0)
        {
            return (null, "no grid to lay it on");
        }

        (Dictionary<(int U, int V), int>? stamp, int left, int free, _, string why) = Stamped(room, ground);
        if (stamp is null)
        {
            return (null, why);
        }

        if (stamp.Count == 0)
        {
            return (null, $"the room names no corner's ground in a one by one k slot - {left} bigger slots left out, {free} corners unnamed");
        }

        return (new RoomScorer(new RoomPlacements(room, stamp, ground, tilesX, tilesY, tiles, identity, walkable)), string.Empty);
    }

    /// <summary>
    /// Counts of walkable tiles over every rectangle from the area's corner, one row and column wider than the area - or null where nothing is to be left out.
    /// </summary>
    private static int[]? Summed(bool[]? walkable, int tilesX, int tilesY)
    {
        if (walkable is null || walkable.Length < tilesX * tilesY || Array.IndexOf(walkable, true) < 0)
        {
            return null;
        }

        int across = tilesX + 1;
        var sums = new int[across * (tilesY + 1)];
        for (var y = 0; y < tilesY; y++)
        {
            int row = 0;
            for (var x = 0; x < tilesX; x++)
            {
                row += walkable[(y * tilesX) + x] ? 1 : 0;
                sums[((y + 1) * across) + x + 1] = sums[(y * across) + x + 1] + row;
            }
        }

        return sums;
    }

    /// <summary>How many walkable tiles a footprint covers, from the sums.</summary>
    private static int Covered(int[] sums, int across, int x, int y, int wide, int tall)
        => sums[((y + tall) * across) + x + wide] - sums[(y * across) + x + wide] - sums[((y + tall) * across) + x] + sums[(y * across) + x];

    /// <summary>
    /// The area cell a room's slot falls on, for a candidate laid one of the eight ways: its centre, laid that way, floored.
    /// </summary>
    /// <remarks>The corners' own arithmetic on a grid twice as fine, so a centre is a whole number. Public for the tile placing (RoomTileFinder) and the capture's replay, which hold a slot's cell against the tile on it.</remarks>
    public static (int X, int Y) CellOf(int column, int line, int width, int height, int turn)
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
    /// The area cell a slot's footprint starts on, for a candidate laid one of the eight ways: the lowest corner of the slot's rectangle laid that way - where its tile's first piece sits.
    /// </summary>
    /// <remarks>
    /// NOT THE SLOT'S OWN CELL TURNED. A tile's first piece is the lowest corner of its LAID footprint
    /// whichever way it went - measured over three Sinter Rift captures on every laid file at every
    /// placement, oblong ones included, the pieces running right and down from it in the area's own
    /// axes - while the origin cell of a three by three slot turned a quarter lands on the far side of
    /// its footprint. Two opposite corners of the rectangle go to two opposite corners of its image,
    /// so the lower of their two images is the image's corner.
    /// </remarks>
    public static (int X, int Y) CornerOf(int column, int line, int slotWidth, int slotHeight, int width, int height, int turn)
    {
        (int ax, int ay) = CellOf(column, line, width, height, turn);
        if (slotWidth == 1 && slotHeight == 1)
        {
            return (ax, ay);
        }

        (int bx, int by) = CellOf(column + slotWidth - 1, line + slotHeight - 1, width, height, turn);
        return (Math.Min(ax, bx), Math.Min(ay, by));
    }

    /// <summary>
    /// The matrix taking a point of the room as its file has it - (u, v) in tiles, u along its columns and v along its lines - to the footprint of a candidate laid one of the eight ways, in tiles from the footprint's corner.
    /// </summary>
    /// <remarks>
    /// THE CORNERS' OWN ARITHMETIC, made a matrix - see <see cref="Placed"/>: mirrored first, u to
    /// w - u, then each quarter turn (u, v) to (h - v, u). A point goes where the corner it sits on
    /// goes, so a room's doodads land where its corners were found. Rows, as System.Numerics
    /// multiplies: a point times the matrix.
    /// </remarks>
    public static Matrix3x2 Laying(int width, int height, int turn)
    {
        Matrix3x2 laying = Matrix3x2.Identity;
        float w = width;
        float h = height;
        if (turn >= 4)
        {
            laying *= new Matrix3x2(-1f, 0f, 0f, 1f, w, 0f);
        }

        for (var quarter = 0; quarter < (turn & 3); quarter++)
        {
            laying *= new Matrix3x2(0f, 1f, -1f, 0f, h, 0f);
            (w, h) = (h, w);
        }

        return laying;
    }

    /// <summary>Whether one placement ranks above another: more tiles agreeing, then more corners - without the tiles, corners alone.</summary>
    internal static bool Above(in RoomCandidate one, in RoomCandidate other)
        => one.TilesAgree != other.TilesAgree ? one.TilesAgree > other.TilesAgree : one.Matched > other.Matched;

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
    private static (Dictionary<(int U, int V), int>? Stamp, int Left, int Free, int Overridden, string Why) Stamped(RoomLayout room, TerrainGroundTypes ground)
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
                        return (null, left, 0, 0, name.Length == 0
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

        // THE FILE'S GROUND OVERRIDES LAST: a type one names at an inner corner is that corner's,
        // whatever the slots round it say - see RoomLayout.GroundOverrides.
        int overridden = 0;
        for (var v = 1; v < room.Height; v++)
        {
            for (var u = 1; u < room.Width; u++)
            {
                int index = room.OverrideAt(u, v);
                if (index == 0)
                {
                    continue;
                }

                string name = room.Named(index);
                int wants = TypeOf(ground, name);
                if (wants < 0)
                {
                    return (null, left, 0, 0, name.Length == 0
                        ? $"a ground override names type {index}, past the room's {room.Strings.Count} strings"
                        : $"the room's overriding ground type {name} is not among this area's - it is not laid here");
                }

                stamp[(u, v)] = wants;
                torn.Remove((u, v));
                overridden++;
            }
        }

        // A CORNER ONE SLOT LEAVES FREE AND ANOTHER NAMES is named; only the ones nobody names are free.
        unnamed.ExceptWith(stamp.Keys);
        unnamed.ExceptWith(torn);
        return (stamp, left, unnamed.Count, overridden, string.Empty);
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
