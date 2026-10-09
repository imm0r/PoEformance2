using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>
/// One room's placements on one area, scored: its corner stamp and its slots laid each of the eight ways, against the area's ground and tiles.
/// </summary>
/// <remarks>
/// EVERYTHING A TRY ASKS, WORKED OUT BEFORE THE FIRST ONE. A search tries every tile corner eight
/// ways round, so the stamp is laid each way once into flat offsets from the placement's corner, a
/// slot's cell likewise, and a tile's verdict against a slot is kept per tile file and per kind of
/// slot - a room's slots are a few dozen kinds, an area's tiles a few hundred files - so a try is
/// array reads and nothing else.
///
/// A TILE FILE IS READ WHEN A TRY FIRST MEETS IT, not before: a room that fits in two places
/// should not read every file the area laid. Not thread-safe: one search or probe at a time.
/// </remarks>
internal sealed class RoomPlacements
{
    /// <summary>A verdict not worked out yet.</summary>
    private const byte Unread = 0;

    /// <summary>No definition to judge by - the tile does not count.</summary>
    private const byte Unknown = 1;

    /// <summary>The tile is not what the slot asks for.</summary>
    private const byte Unlike = 2;

    /// <summary>The tile is what the slot asks for.</summary>
    private const byte Alike = 3;

    private readonly int _tilesX;
    private readonly int _tilesY;
    private readonly int _across;
    private readonly int[] _area;
    private readonly (int Wide, int Tall)[] _size = new (int, int)[8];

    /// <summary>Per turn, each stamped corner as an offset into <see cref="_area"/> from the placement's corner.</summary>
    private readonly int[][] _cornerAt = new int[8][];

    /// <summary>Per turn, the type each of those corners must be.</summary>
    private readonly int[][] _wants = new int[8][];

    /// <summary>Per turn, each stamped corner within the footprint, for the map's marks.</summary>
    private readonly (int U, int V)[][] _cornerUv = new (int, int)[8][];

    private readonly TerrainTiles? _tiles;
    private readonly Func<string, TileIdentity?>? _identity;

    /// <summary>Per turn, each slot's cell within the footprint.</summary>
    private readonly (int X, int Y)[][] _cellXy = new (int, int)[8][];

    /// <summary>Per turn, each slot's cell as an offset into the tiles' ids from the placement's corner.</summary>
    private readonly int[][] _cellAt = new int[8][];

    /// <summary>Per slot, what it asks for - one instance per kind of slot.</summary>
    private readonly Wanted[] _slotWants = [];

    /// <summary>Per slot, its kind's verdict on each tile file, by path id - shared by every slot of the kind.</summary>
    private readonly byte[][] _slotVerdicts = [];

    /// <summary>Per slot, whether it is bigger than one tile.</summary>
    private readonly bool[] _large = [];

    /// <summary>Per tile file, what it is, once read.</summary>
    private readonly Wanted?[] _laid = [];

    private readonly bool[] _laidRead = [];

    /// <summary>The room, for its slots' numbers in the diagnostic list.</summary>
    private readonly RoomLayout _room;

    /// <summary>The area's ground type files, for the diagnostic list.</summary>
    private readonly IReadOnlyList<string> _types;

    /// <summary>Each stamped corner as the room has it, in the order every turn's arrays keep.</summary>
    private readonly (int U, int V)[] _stampUv;

    /// <summary>Per slot, its column and line in the room.</summary>
    private readonly (int Column, int Line)[] _slotAt = [];

    /// <summary>Which tiles have walkable ground, row by row, to tell a join among the misses; null where not to hand.</summary>
    private readonly bool[]? _walkable;

    public RoomPlacements(
        RoomLayout room,
        Dictionary<(int U, int V), int> stamp,
        TerrainGroundTypes ground,
        int tilesX,
        int tilesY,
        TerrainTiles? tiles,
        Func<string, TileIdentity?>? identity,
        bool[]? walkable = null)
    {
        _room = room;
        _types = ground.Types;
        _stampUv = [.. stamp.Keys];
        _walkable = walkable is not null && walkable.Length >= tilesX * tilesY ? walkable : null;
        _tilesX = tilesX;
        _tilesY = tilesY;
        _across = tilesX + 1;
        _area = new int[_across * (tilesY + 1)];
        for (var y = 0; y <= tilesY; y++)
        {
            for (var x = 0; x <= tilesX; x++)
            {
                _area[(y * _across) + x] = ground.At(x, y);
            }
        }

        int corners = stamp.Count;
        var us = new int[corners];
        var vs = new int[corners];
        for (var turn = 0; turn < 8; turn++)
        {
            var wants = new int[corners];
            _size[turn] = RoomFinder.Placed(stamp, room.Width, room.Height, turn, us, vs, wants);
            var offsets = new int[corners];
            var uv = new (int, int)[corners];
            for (var at = 0; at < corners; at++)
            {
                offsets[at] = (vs[at] * _across) + us[at];
                uv[at] = (us[at], vs[at]);
            }

            _cornerAt[turn] = offsets;
            _wants[turn] = wants;
            _cornerUv[turn] = uv;
        }

        // THE TILES ONLY ON THE GRID THE GROUND IS ON: both come from the same terrain read, and a
        // grid of another size would put every slot on some other cell.
        if (tiles is null || identity is null || tiles.Width != tilesX || tiles.Height != tilesY)
        {
            return;
        }

        _tiles = tiles;
        _identity = identity;
        int files = tiles.Paths.Count;
        _laid = new Wanted?[files];
        _laidRead = new bool[files];

        var slots = new List<(int Column, int Line, Wanted Wants)>();
        var kinds = new Dictionary<Wanted, byte[]>();
        for (var line = 0; line < room.Height; line++)
        {
            for (var column = 0; column < room.Width; column++)
            {
                RoomSlot slot = room.SlotAt(column, line);
                if (!slot.IsTile)
                {
                    continue;
                }

                var wanted = new Wanted(
                    slot.Width, slot.Height, room.Named(slot.Tag),
                    Sorted(room, slot.Edge(0), slot.Edge(1), slot.Edge(2), slot.Edge(3)),
                    Sorted(room, slot.Ground(0), slot.Ground(1), slot.Ground(2), slot.Ground(3)));
                if (!kinds.ContainsKey(wanted))
                {
                    kinds[wanted] = new byte[files];
                }

                slots.Add((column, line, wanted));
            }
        }

        _slotWants = new Wanted[slots.Count];
        _slotVerdicts = new byte[slots.Count][];
        _large = new bool[slots.Count];
        _slotAt = new (int, int)[slots.Count];
        for (var one = 0; one < slots.Count; one++)
        {
            Wanted wanted = slots[one].Wants;
            _slotAt[one] = (slots[one].Column, slots[one].Line);
            _slotVerdicts[one] = kinds[wanted];
            _slotWants[one] = wanted;
            _large[one] = wanted.Width > 1 || wanted.Height > 1;
        }

        for (var turn = 0; turn < 8; turn++)
        {
            var cells = new (int, int)[slots.Count];
            var offsets = new int[slots.Count];
            for (var one = 0; one < slots.Count; one++)
            {
                cells[one] = RoomFinder.CellOf(slots[one].Column, slots[one].Line, room.Width, room.Height, turn);
                offsets[one] = (cells[one].Item2 * tilesX) + cells[one].Item1;
            }

            _cellXy[turn] = cells;
            _cellAt[turn] = offsets;
        }
    }

    /// <summary>Whether the placements are checked against the tiles laid.</summary>
    public bool Checks => _tiles is not null;

    /// <summary>The area's tiles across, as scored.</summary>
    public int TilesX => _tilesX;

    /// <summary>The area's tiles down, as scored.</summary>
    public int TilesY => _tilesY;

    /// <summary>The footprint's size laid one way.</summary>
    public (int Wide, int Tall) Size(int turn) => _size[turn];

    /// <summary>
    /// How many stamped corners agree with the area for the placement whose corner is at an area offset, or -1 as soon as more than <paramref name="allowed"/> do not.
    /// </summary>
    public int Matched(int at, int turn, int allowed)
    {
        int[] offsets = _cornerAt[turn];
        int[] wants = _wants[turn];
        int[] area = _area;
        int misses = 0;
        for (var one = 0; one < offsets.Length; one++)
        {
            if (area[at + offsets[one]] != wants[one] && ++misses > allowed)
            {
                return -1;
            }
        }

        return offsets.Length - misses;
    }

    /// <summary>
    /// The placement's slots against the tiles laid under them; false, and the counts unfinished, as soon as fewer than <paramref name="least"/> could agree.
    /// </summary>
    /// <remarks>The placement must lie inside the area, as every one the search and the probe try does: the ids are read without a bounds check of their own.</remarks>
    public bool Tiles(int x, int y, int turn, int least, out int tiles, out int agree, out int big, out int bigAgree)
    {
        tiles = agree = big = bigAgree = 0;
        if (_tiles is null)
        {
            return least <= 0;
        }

        int[] offsets = _cellAt[turn];
        int[] ids = _tiles.Ids;
        int at = (y * _tilesX) + x;
        int count = offsets.Length;
        int files = _laid.Length;
        for (var one = 0; one < count; one++)
        {
            if (agree + (count - one) < least)
            {
                return false;
            }

            int id = ids[at + offsets[one]];
            if ((uint)id >= (uint)files)
            {
                continue;
            }

            byte verdict = _slotVerdicts[one][id];
            if (verdict == Unread)
            {
                verdict = Judged(one, id);
            }

            if (verdict == Unknown)
            {
                continue;
            }

            tiles++;
            bool large = _large[one];
            big += large ? 1 : 0;
            if (verdict == Alike)
            {
                agree++;
                bigAgree += large ? 1 : 0;
            }
        }

        return agree >= least;
    }

    /// <summary>One placement scored in full.</summary>
    public RoomCandidate Scored(int x, int y, int turn)
    {
        (int wide, int tall) = _size[turn];
        int corners = _cornerAt[turn].Length;
        int matched = Matched((y * _across) + x, turn, corners);
        Tiles(x, y, turn, 0, out int tiles, out int agree, out int big, out int bigAgree);
        return new RoomCandidate(x, y, turn, wide, tall, matched, corners)
        {
            Tiles = tiles,
            TilesAgree = agree,
            Big = big,
            BigAgree = bigAgree,
        };
    }

    /// <summary>Where one placement's corners and tiles part with the area, the joins among them told where the walkable ground is to hand.</summary>
    public RoomMisses Misses(RoomCandidate candidate)
    {
        var corners = new List<(int X, int Y)>();
        int at = (candidate.Y * _across) + candidate.X;
        int[] offsets = _cornerAt[candidate.Turn];
        int[] wants = _wants[candidate.Turn];
        (int U, int V)[] uv = _cornerUv[candidate.Turn];
        for (var one = 0; one < offsets.Length; one++)
        {
            if (_area[at + offsets[one]] != wants[one])
            {
                corners.Add((candidate.X + uv[one].U, candidate.Y + uv[one].V));
            }
        }

        var tiles = new List<(int X, int Y)>();
        if (_tiles is not null)
        {
            (int X, int Y)[] cells = _cellXy[candidate.Turn];
            for (var one = 0; one < cells.Length; one++)
            {
                int x = candidate.X + cells[one].X;
                int y = candidate.Y + cells[one].Y;
                int id = _tiles.IdAt(x, y);
                if ((uint)id < (uint)_laid.Length)
                {
                    byte verdict = _slotVerdicts[one][id];
                    if ((verdict == Unread ? Judged(one, id) : verdict) == Unlike)
                    {
                        tiles.Add((x, y));
                    }
                }
            }
        }

        return Classified(candidate, corners, tiles);
    }

    /// <summary>
    /// Every place one placement parts with the area, said in full - tiles, then corners.
    /// </summary>
    /// <remarks>
    /// WORKED OUT AFRESH, from the identities and the ground alone, and none of the verdicts kept for
    /// the search: so it can run beside a probe on another thread. It is a list for a person to read,
    /// once per pick, not a step of any search.
    /// </remarks>
    public List<RoomPart> Parts(RoomCandidate candidate)
    {
        var corners = new List<(int X, int Y)>();
        var cornerAt = new List<int>();
        int at = (candidate.Y * _across) + candidate.X;
        int[] offsets = _cornerAt[candidate.Turn];
        int[] wants = _wants[candidate.Turn];
        (int U, int V)[] uv = _cornerUv[candidate.Turn];
        for (var one = 0; one < offsets.Length; one++)
        {
            if (_area[at + offsets[one]] != wants[one])
            {
                corners.Add((candidate.X + uv[one].U, candidate.Y + uv[one].V));
                cornerAt.Add(one);
            }
        }

        var tiles = new List<(int X, int Y)>();
        var tileAt = new List<(int Slot, TileIdentity? Laid, string Path)>();
        if (_tiles is not null && _identity is not null)
        {
            (int X, int Y)[] cells = _cellXy[candidate.Turn];
            for (var one = 0; one < cells.Length; one++)
            {
                int x = candidate.X + cells[one].X;
                int y = candidate.Y + cells[one].Y;
                int id = _tiles.IdAt(x, y);
                if ((uint)id >= (uint)_laid.Length)
                {
                    continue;
                }

                string path = _tiles.Paths[id];
                TileIdentity? identity = _identity(path);
                if (Compare(_slotWants[one], LaidOf(identity)) == Unlike)
                {
                    tiles.Add((x, y));
                    tileAt.Add((one, identity, path));
                }
            }
        }

        RoomMisses misses = Classified(candidate, corners, tiles);
        var openings = new HashSet<(int X, int Y)>(misses.Openings);
        var caps = new HashSet<(int X, int Y)>(misses.Caps);
        var joined = new HashSet<(int X, int Y)>(misses.JoinCorners);
        var parts = new List<RoomPart>(tiles.Count + corners.Count);
        for (var one = 0; one < tiles.Count; one++)
        {
            (int slot, TileIdentity? laid, string path) = tileAt[one];
            (int column, int line) = _slotAt[slot];
            RoomSlot numbers = _room.SlotAt(column, line);
            parts.Add(new RoomPart(
                IsCorner: false,
                tiles[one].X,
                tiles[one].Y,
                openings.Contains(tiles[one]) ? RoomJoin.Opening : caps.Contains(tiles[one]) ? RoomJoin.Cap : RoomJoin.None,
                column,
                line,
                Sides(column == 0, column == _room.Width - 1, line == 0, line == _room.Height - 1),
                Said(_slotWants[slot]),
                laid is null ? Short(path) + " (no definition)" : Short(path) + " " + Said(LaidOf(laid)!),
                Walks(tiles[one].X, tiles[one].Y),
                [_room.Named(numbers.Edge(0)), _room.Named(numbers.Edge(1)), _room.Named(numbers.Edge(2)), _room.Named(numbers.Edge(3))],
                numbers.IsTile ? numbers.Numbers.AsSpan(6, 8).ToArray() : []));
        }

        for (var one = 0; one < corners.Count; one++)
        {
            (int u, int v) = _stampUv[cornerAt[one]];
            parts.Add(new RoomPart(
                IsCorner: true,
                corners[one].X,
                corners[one].Y,
                joined.Contains(corners[one]) ? RoomJoin.Corner : RoomJoin.None,
                u,
                v,
                Sides(u == 0, u == _room.Width, v == 0, v == _room.Height),
                TypeSaid(wants[cornerAt[one]]),
                TypeSaid(_area[at + offsets[cornerAt[one]]]),
                Walkable: false,
                [],
                []));
        }

        return parts;
    }

    /// <summary>
    /// A placement's misses with its joins told apart - see RoomMisses for the rule.
    /// </summary>
    private RoomMisses Classified(RoomCandidate candidate, List<(int X, int Y)> corners, List<(int X, int Y)> tiles)
    {
        if (corners.Count == 0 && tiles.Count == 0)
        {
            return RoomMisses.None;
        }

        if (_walkable is null)
        {
            return new RoomMisses(corners, tiles);
        }

        int left = candidate.X;
        int right = candidate.X + candidate.Width - 1;
        int bottom = candidate.Y;
        int top = candidate.Y + candidate.Height - 1;
        bool OnRim((int X, int Y) tile) => tile.X == left || tile.X == right || tile.Y == bottom || tile.Y == top;

        // AN OPENING: a rim tile that misses, has walkable ground, and walkable ground across the rim.
        var openings = new List<(int X, int Y)>();
        foreach ((int x, int y) in tiles)
        {
            if (OnRim((x, y)) && Walks(x, y)
                && ((x == left && Walks(x - 1, y)) || (x == right && Walks(x + 1, y))
                    || (y == bottom && Walks(x, y - 1)) || (y == top && Walks(x, y + 1))))
            {
                openings.Add((x, y));
            }
        }

        var opened = new HashSet<(int X, int Y)>(openings);

        // A CAP: a rim tile that misses beside an opening, not being one.
        var caps = new List<(int X, int Y)>();
        foreach ((int x, int y) in tiles)
        {
            if (!opened.Contains((x, y)) && OnRim((x, y))
                && (opened.Contains((x - 1, y)) || opened.Contains((x + 1, y)) || opened.Contains((x, y - 1)) || opened.Contains((x, y + 1))))
            {
                caps.Add((x, y));
            }
        }

        // A JOIN CORNER: a missed corner of an opening - an opening at (x, y) has its corners at x..x+1, y..y+1.
        var joinCorners = new List<(int X, int Y)>();
        foreach ((int x, int y) in corners)
        {
            if (opened.Contains((x, y)) || opened.Contains((x - 1, y)) || opened.Contains((x, y - 1)) || opened.Contains((x - 1, y - 1)))
            {
                joinCorners.Add((x, y));
            }
        }

        return new RoomMisses(corners, tiles)
        {
            Classified = true,
            Openings = openings,
            Caps = caps,
            JoinCorners = joinCorners,
            Joins = Runs(opened),
        };
    }

    /// <summary>How many separate runs - side by side, not across a corner - a set of tiles makes.</summary>
    private static int Runs(HashSet<(int X, int Y)> tiles)
    {
        var seen = new HashSet<(int X, int Y)>();
        var next = new Stack<(int X, int Y)>();
        int runs = 0;
        foreach ((int X, int Y) start in tiles)
        {
            if (!seen.Add(start))
            {
                continue;
            }

            runs++;
            next.Push(start);
            while (next.Count > 0)
            {
                (int x, int y) = next.Pop();
                foreach ((int X, int Y) beside in (ReadOnlySpan<(int, int)>)[(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)])
                {
                    if (tiles.Contains(beside) && seen.Add(beside))
                    {
                        next.Push(beside);
                    }
                }
            }
        }

        return runs;
    }

    /// <summary>Whether a tile has walkable ground; false outside the area or where the mask is not to hand.</summary>
    private bool Walks(int x, int y)
        => _walkable is not null && (uint)x < (uint)_tilesX && (uint)y < (uint)_tilesY && _walkable[(y * _tilesX) + x];

    /// <summary>The room's sides a slot or corner lies on, in the room's own words.</summary>
    private static string Sides(bool left, bool right, bool down, bool up)
        => (down ? "D" : string.Empty) + (right ? "R" : string.Empty) + (up ? "U" : string.Empty) + (left ? "L" : string.Empty);

    /// <summary>An area ground type by its index, short.</summary>
    private string TypeSaid(int type) => (uint)type < (uint)_types.Count
        ? _types[type].Length == 0 ? "(blank)" : Short(_types[type])
        : "#" + type.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A file's name without its folder or extension.</summary>
    private static string Short(string path) => path.Length == 0 ? "-" : Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    /// <summary>What a slot asks for, or what a tile is, in a line's words.</summary>
    private static string Said(Wanted one)
        => System.FormattableString.Invariant($"{one.Width}x{one.Height}{(one.Tag.Length > 0 ? " " + one.Tag : string.Empty)} edges [{string.Join(", ", one.Edges.Select(Short))}] grounds [{string.Join(", ", one.Grounds.Select(Short))}]");

    /// <summary>
    /// The best placement whose footprint covers a tile - most tiles agreeing, then most corners, the earliest on a tie - or null.
    /// </summary>
    /// <remarks>
    /// THE LIST'S OWN ORDER, over the placements that cover one tile: whatever the list ranks first
    /// elsewhere, this is what the same yardstick says about the place a person knows the room to be.
    /// </remarks>
    public RoomPlace? Around(int tileX, int tileY)
    {
        if ((uint)tileX >= (uint)_tilesX || (uint)tileY >= (uint)_tilesY)
        {
            return null;
        }

        RoomCandidate? best = null;
        for (var turn = 0; turn < 8; turn++)
        {
            (int wide, int tall) = _size[turn];
            for (int y = Math.Max(0, tileY - tall + 1); y <= Math.Min(tileY, _tilesY - tall); y++)
            {
                for (int x = Math.Max(0, tileX - wide + 1); x <= Math.Min(tileX, _tilesX - wide); x++)
                {
                    int least = best?.TilesAgree ?? 0;
                    if (!Tiles(x, y, turn, least, out int tiles, out int agree, out int big, out int bigAgree))
                    {
                        continue;
                    }

                    int corners = _cornerAt[turn].Length;
                    int matched = Matched((y * _across) + x, turn, corners);
                    var candidate = new RoomCandidate(x, y, turn, wide, tall, matched, corners)
                    {
                        Tiles = tiles,
                        TilesAgree = agree,
                        Big = big,
                        BigAgree = bigAgree,
                    };
                    if (best is not { } had
                        || candidate.TilesAgree > had.TilesAgree
                        || (candidate.TilesAgree == had.TilesAgree && candidate.Matched > had.Matched))
                    {
                        best = candidate;
                    }
                }
            }
        }

        return best is { } found ? new RoomPlace(found, Misses(found)) : null;
    }

    /// <summary>A slot kind's verdict on a tile file, worked out and kept.</summary>
    private byte Judged(int slot, int id)
    {
        if (!_laidRead[id])
        {
            _laid[id] = LaidOf(_identity!(_tiles!.Paths[id]));
            _laidRead[id] = true;
        }

        byte verdict = Compare(_slotWants[slot], _laid[id]);
        _slotVerdicts[slot][id] = verdict;
        return verdict;
    }

    /// <summary>A tile file's identity in the terms a slot asks in, or null where it carries too little to judge by.</summary>
    private static Wanted? LaidOf(TileIdentity? tile)
        => tile is { Edges.Count: 4, Grounds.Count: 4 }
            ? new Wanted(tile.Width, tile.Height, tile.Tag, Sorted(tile.Edges), Sorted(tile.Grounds))
            : null;

    /// <summary>A slot's verdict on a tile: the size either way round, the tag where the slot names one, and the edges and grounds as sets.</summary>
    private static byte Compare(Wanted wanted, Wanted? laid)
        => laid is null
            ? Unknown
            : ((laid.Width == wanted.Width && laid.Height == wanted.Height) || (laid.Width == wanted.Height && laid.Height == wanted.Width))
                && (wanted.Tag.Length == 0 || string.Equals(wanted.Tag, laid.Tag, StringComparison.OrdinalIgnoreCase))
                && laid.Edges.AsSpan().SequenceEqual(wanted.Edges, StringComparer.OrdinalIgnoreCase)
                && laid.Grounds.AsSpan().SequenceEqual(wanted.Grounds, StringComparer.OrdinalIgnoreCase)
                    ? Alike
                    : Unlike;

    private static string[] Sorted(RoomLayout room, int a, int b, int c, int d)
        => Sorted([room.Named(a), room.Named(b), room.Named(c), room.Named(d)]);

    private static string[] Sorted(IReadOnlyList<string> four)
    {
        string[] sorted = [.. four.Select(one => one.Replace('\\', '/'))];
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    /// <summary>What a slot asks of the tile under it, or what a tile is, in the terms no placement changes - equal by value, so slots of a kind share one verdict row.</summary>
    private sealed record Wanted(int Width, int Height, string Tag, string[] Edges, string[] Grounds)
    {
        public bool Equals(Wanted? other)
            => other is not null
                && Width == other.Width
                && Height == other.Height
                && string.Equals(Tag, other.Tag, StringComparison.OrdinalIgnoreCase)
                && Edges.AsSpan().SequenceEqual(other.Edges, StringComparer.OrdinalIgnoreCase)
                && Grounds.AsSpan().SequenceEqual(other.Grounds, StringComparer.OrdinalIgnoreCase);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Width);
            hash.Add(Height);
            hash.Add(Tag, StringComparer.OrdinalIgnoreCase);
            foreach (string edge in Edges)
            {
                hash.Add(edge, StringComparer.OrdinalIgnoreCase);
            }

            foreach (string one in Grounds)
            {
                hash.Add(one, StringComparer.OrdinalIgnoreCase);
            }

            return hash.ToHashCode();
        }
    }

}
