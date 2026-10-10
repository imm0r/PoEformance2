using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>One place a room's big slots' tiles say it stands - see <see cref="RoomTileFinder"/>.</summary>
/// <param name="Where">The footprint's corner on the tile grid, which way the room was laid, and its size that way. Tiles and TilesAgree count the slots whose tile could be judged and those that agreed, Big and BigAgree the big ones among them; Matched and Corners are nought, no ground having been asked.</param>
/// <param name="Anchor">The tile file on the anchor slot's own cell.</param>
/// <param name="AnchorLaid">How often that file is laid in the area - the cells carrying its first piece.</param>
/// <param name="AnchorX">The anchor slot's own cell, across - what tells places standing on one tile from places on two.</param>
/// <param name="AnchorY">That cell, down.</param>
/// <param name="AnchorWide">The anchor tile's footprint across, laid this way round.</param>
/// <param name="AnchorTall">Its footprint down.</param>
/// <param name="AnchorTagged">Whether the anchor slot names a tag - asks for a feature tile, not a piece of terrain.</param>
public sealed record RoomTilePlace(RoomCandidate Where, string Anchor, int AnchorLaid, int AnchorX, int AnchorY, int AnchorWide, int AnchorTall, bool AnchorTagged);

/// <summary>Where a room's big slots' tiles say it stands - every place - or why nowhere.</summary>
/// <param name="Places">The places, most slots agreeing first, one per footprint.</param>
/// <param name="Why">Why there is no place, or empty.</param>
public sealed record RoomTilePlaces(IReadOnlyList<RoomTilePlace> Places, string Why)
{
    /// <summary>No place, and why.</summary>
    public static RoomTilePlaces Not(string why) => new([], why);

    /// <summary>The kind of slot the places were anchored on, in words - the rarest of the room's big slots.</summary>
    public string Anchor { get; init; } = string.Empty;

    /// <summary>How many cells of the area carry the first piece of a tile alike to the anchor slot - what the candidates came from.</summary>
    public int Anchors { get; init; }

    /// <summary>How many candidates were tried, over every way round.</summary>
    public int Tried { get; init; }
}

/// <summary>
/// Finds where a room was laid by its big slots' tiles - for the rooms the doodads cannot place.
/// </summary>
/// <remarks>
/// WHY THE BIG SLOTS. Sinter Rift's boss, its eight checkpoints and a titan overlay are rooms of slots
/// bigger than one tile and nothing else, so the ground stamp the search needs (RoomFinder: the one by
/// one slots' corners) is empty and the search does not run - while a big slot's tile is the room's
/// fingerprint: boss_01.tdt, forge_entrance.tdt and StMW_TitanFeature02.tdt are each laid once in the
/// area. Held against the boss's doodad place, every one of its 17 big slots had the tile its ask names
/// on the slot's own cell (SlotWants), and that tile's FIRST PIECE there: the slot's corner is the
/// tile's corner. The one by one slots are the dunes - laid 2761 times - and decide nothing. THE
/// TILES' OWN PLACEMENTS DO NOT GIVE THE ROOM'S TURN: under that boss room, laid as written, the
/// seventeen tiles were laid every one of the eight ways between them, first piece on the slot's cell
/// all the same - so a room with one big slot and nothing to fix the way round stays undecided.
///
/// ANCHORED ON THE RAREST. Every cell carrying the first piece of a tile alike to the room's rarest big
/// kind of slot is where that slot's corner could be; each of the eight ways round gives the footprint's
/// corner from it. A candidate is a place when EVERY big slot has a tile alike with its first piece on
/// the slot's cell; the one by one slots are counted and rank the places. The search's checks are kept
/// as they are - this is the road for the rooms it has no stamp for, not a second search.
///
/// WHAT IT CANNOT TELL: a room with one big slot and nothing to fix the way round stands on its tile
/// several ways, and variants of a room - Sinter Rift's four entrances share one ForgeEntrance slot and
/// differ in slots the tiles do not tell apart - stand on one tile together. <see cref="Settled"/> draws
/// such a tile's own footprint once, under the name the rooms share, which is what was asked for.
///
/// A FINGERPRINT IS A FEATURE TILE LAID ONCE - a slot that names a tag, asking for a feature and not a
/// piece of terrain, answered by one tile in the area. Sinter Rift's ledge checkpoints are one untagged
/// three by three ledge tile each, laid seven and ten times: 116 candidates for one room, every one a
/// plain ledge, 26 footprints to draw - for a room the area lays twice at most. Laid once is not enough
/// either: a ledge piece that happens to be laid once stood beside the entrance and would have been drawn
/// as a checkpoint. So a place is kept where a doodad of the room stands in it (<see cref="Confirmed"/>,
/// the checkpoint itself once the player has been near) or where its anchor is tagged and laid once; the
/// rest are reported and not drawn.
/// </remarks>
public static class RoomTileFinder
{
    /// <summary>
    /// Every place the room's big slots' tiles stand in the area - see the class remarks. Never throws for a room that read.
    /// </summary>
    /// <param name="room">The room, read with its slots.</param>
    /// <param name="tiles">The tiles the area laid, with the piece of its file each cell carries.</param>
    /// <param name="identity">A tile file's identity, or null where it does not read.</param>
    public static RoomTilePlaces Find(RoomLayout room, TerrainTiles tiles, Func<string, TileIdentity?> identity)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(identity);
        if (!room.Ready)
        {
            return RoomTilePlaces.Not($"the room did not read: {room.Why}");
        }

        if (room.Slots.Count == 0)
        {
            return RoomTilePlaces.Not(room.SlotsWhy.Length > 0 ? $"the room's grid did not read: {room.SlotsWhy}" : "the room has no slot grid");
        }

        // THE SLOTS, AND THEIR KINDS - a verdict is per kind and per tile file, as in the search.
        var kinds = new List<SlotWant>();
        var kindOf = new Dictionary<SlotWant, int>();
        var slots = new List<Slot>();
        for (var line = 0; line < room.Height; line++)
        {
            for (var column = 0; column < room.Width; column++)
            {
                RoomSlot slot = room.SlotAt(column, line);
                if (!slot.IsTile)
                {
                    continue;
                }

                SlotWant want = SlotWants.Of(room, slot);
                if (!kindOf.TryGetValue(want, out int kind))
                {
                    kind = kinds.Count;
                    kinds.Add(want);
                    kindOf[want] = kind;
                }

                slots.Add(new Slot(column, line, kind, slot.Width > 1 || slot.Height > 1, slot.Width, slot.Height));
            }
        }

        var bigKinds = new List<int>();
        foreach (Slot one in slots)
        {
            if (one.Big && !bigKinds.Contains(one.Kind))
            {
                bigKinds.Add(one.Kind);
            }
        }

        if (bigKinds.Count == 0)
        {
            return RoomTilePlaces.Not("no slot bigger than one tile to anchor on - a room of one by one slots is the search's");
        }

        int files = tiles.Paths.Count;
        var laid = new SlotWant?[files];
        var laidRead = new bool[files];
        var verdicts = new byte[kinds.Count][];
        for (var kind = 0; kind < kinds.Count; kind++)
        {
            verdicts[kind] = new byte[files];
        }

        byte Verdict(int kind, int id)
        {
            byte verdict = verdicts[kind][id];
            if (verdict != 0)
            {
                return verdict;
            }

            if (!laidRead[id])
            {
                laid[id] = SlotWants.Of(identity(tiles.Paths[id]));
                laidRead[id] = true;
            }

            verdict = SlotWants.Verdict(kinds[kind], laid[id]);
            verdicts[kind][id] = verdict;
            return verdict;
        }

        // THE ANCHOR CELLS of every big kind, and how often each file is laid - one pass over the area.
        var anchorCells = new List<(int X, int Y)>[kinds.Count];
        foreach (int kind in bigKinds)
        {
            anchorCells[kind] = [];
        }

        var firsts = new int[files];
        for (var y = 0; y < tiles.Height; y++)
        {
            for (var x = 0; x < tiles.Width; x++)
            {
                int id = tiles.IdAt(x, y);
                if ((uint)id >= (uint)files || tiles.SubAt(x, y) is not (0, 0))
                {
                    continue;
                }

                firsts[id]++;
                foreach (int kind in bigKinds)
                {
                    if (Verdict(kind, id) == SlotWants.Alike)
                    {
                        anchorCells[kind].Add((x, y));
                    }
                }
            }
        }

        // A BIG SLOT WHOSE TILE IS LAID NOWHERE is a room the area did not lay.
        int anchor = -1;
        foreach (int kind in bigKinds)
        {
            if (anchorCells[kind].Count == 0)
            {
                return RoomTilePlaces.Not($"its {SlotWants.Said(kinds[kind])} slot's tile is laid nowhere in the area") with { Anchor = SlotWants.Said(kinds[kind]) };
            }

            if (anchor < 0 || anchorCells[kind].Count < anchorCells[anchor].Count)
            {
                anchor = kind;
            }
        }

        // THE CANDIDATES: each anchor slot on each anchor cell, each of the eight ways round; a candidate
        // stands when every big slot has a tile alike with its first piece on the slot's own cell.
        RoomCandidate? Scored(int x, int y, int turn, int wide, int tall)
        {
            int count = 0, agree = 0, big = 0, bigAgree = 0;
            foreach (Slot one in slots)
            {
                (int sx, int sy) = RoomFinder.CellOf(one.Column, one.Line, room.Width, room.Height, turn);
                int id = tiles.IdAt(x + sx, y + sy);
                byte verdict = (uint)id < (uint)files ? Verdict(one.Kind, id) : SlotWants.Unknown;
                if (verdict == SlotWants.Unknown)
                {
                    if (one.Big)
                    {
                        return null;
                    }

                    continue;
                }

                count++;
                big += one.Big ? 1 : 0;
                bool alike = verdict == SlotWants.Alike && (!one.Big || tiles.SubAt(x + sx, y + sy) is (0, 0));
                if (alike)
                {
                    agree++;
                    bigAgree += one.Big ? 1 : 0;
                }
                else if (one.Big)
                {
                    return null;
                }
            }

            return new RoomCandidate(x, y, turn, wide, tall, 0, 0) { Tiles = count, TilesAgree = agree, Big = big, BigAgree = bigAgree };
        }

        var tried = new HashSet<(int X, int Y, int Turn)>();
        var places = new List<RoomTilePlace>();
        foreach (Slot anchoring in slots)
        {
            if (anchoring.Kind != anchor)
            {
                continue;
            }

            foreach ((int cx, int cy) in anchorCells[anchor])
            {
                for (var turn = 0; turn < 8; turn++)
                {
                    (int dx, int dy) = RoomFinder.CellOf(anchoring.Column, anchoring.Line, room.Width, room.Height, turn);
                    int x = cx - dx;
                    int y = cy - dy;
                    (int wide, int tall) = (turn & 1) == 0 ? (room.Width, room.Height) : (room.Height, room.Width);
                    if (x < 0 || y < 0 || x + wide > tiles.Width || y + tall > tiles.Height || !tried.Add((x, y, turn)))
                    {
                        continue;
                    }

                    if (Scored(x, y, turn, wide, tall) is not { } where)
                    {
                        continue;
                    }

                    (int anchorWide, int anchorTall) = (turn & 1) == 0 ? (anchoring.Width, anchoring.Height) : (anchoring.Height, anchoring.Width);
                    places.Add(new RoomTilePlace(where, tiles.PathAt(cx, cy), firsts[tiles.IdAt(cx, cy)], cx, cy, anchorWide, anchorTall, kinds[anchor].Tag.Length > 0));
                }
            }
        }

        // MOST SLOTS AGREEING FIRST, then the grid's order, so an area settles the same way every time;
        // one place per footprint, as the doodads keep them: a room symmetric enough to stand one place
        // several ways round is one place.
        places.Sort((a, b) =>
        {
            int order = b.Where.TilesAgree.CompareTo(a.Where.TilesAgree);
            order = order != 0 ? order : a.Where.Y.CompareTo(b.Where.Y);
            order = order != 0 ? order : a.Where.X.CompareTo(b.Where.X);
            return order != 0 ? order : a.Where.Turn.CompareTo(b.Where.Turn);
        });

        var kept = new List<RoomTilePlace>(places.Count);
        var footprints = new HashSet<(int X, int Y, int Wide, int Tall)>();
        foreach (RoomTilePlace place in places)
        {
            if (footprints.Add((place.Where.X, place.Where.Y, place.Where.Width, place.Where.Height)))
            {
                kept.Add(place);
            }
        }

        string said = SlotWants.Said(kinds[anchor]);
        return new RoomTilePlaces(
            kept,
            kept.Count > 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"none of {tried.Count} candidates on its {said} slot's tile has every big slot alike"))
        {
            Anchor = said,
            Anchors = anchorCells[anchor].Count,
            Tried = tried.Count,
        };
    }

    /// <summary>
    /// The places a room's few doodad lines confirm: one with an entity of a line's path and model within a tile of where the place puts the line - the lines too few for RoomDoodadFinder to place the room by, enough to pick among places its tiles found. Every place, unconfirmed, where no line has an entity anywhere, since a scripted object's absence says nothing; every place too where lines have entities and none stands at any place, the entities being some other instance's.
    /// </summary>
    /// <param name="places">The places the tiles found.</param>
    /// <param name="doodads">The room's doodad lines.</param>
    /// <param name="width">The room's grid across, in tiles.</param>
    /// <param name="height">The room's grid down.</param>
    /// <param name="sightings">The area's doodad entities.</param>
    /// <returns>The places kept, and whether a doodad picked them.</returns>
    public static (IReadOnlyList<RoomTilePlace> Places, bool Confirmed) Confirmed(
        IReadOnlyList<RoomTilePlace> places, IReadOnlyList<RoomDoodad> doodads, int width, int height, IReadOnlyList<DoodadSighting> sightings)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(doodads);
        ArgumentNullException.ThrowIfNull(sightings);
        if (places.Count == 0 || width <= 0 || height <= 0)
        {
            return (places, false);
        }

        // THE LINES WITH AN ENTITY SOMEWHERE: by path, and by model where both have one - as the finder pairs them.
        var lines = new List<(RoomDoodad Line, List<int> Entities)>();
        foreach (RoomDoodad doodad in doodads)
        {
            if (doodad.Stub.Length == 0)
            {
                continue;
            }

            string model = doodad.Ao.Replace('\\', '/');
            var mine = new List<int>();
            for (var one = 0; one < sightings.Count; one++)
            {
                DoodadSighting sighting = sightings[one];
                if (string.Equals(sighting.Path, doodad.Stub, StringComparison.OrdinalIgnoreCase)
                    && (sighting.Model.Length == 0 || model.Length == 0 || string.Equals(sighting.Model, model, StringComparison.OrdinalIgnoreCase)))
                {
                    mine.Add(one);
                }
            }

            if (mine.Count > 0)
            {
                lines.Add((doodad, mine));
            }
        }

        if (lines.Count == 0)
        {
            return (places, false);
        }

        float side = RoomDoodadFinder.WithinUnits;
        var confirmed = new List<RoomTilePlace>();
        foreach (RoomTilePlace place in places)
        {
            Matrix3x2 laying = RoomFinder.Laying(width, height, place.Where.Turn);
            var stands = false;
            foreach ((RoomDoodad line, List<int> entities) in lines)
            {
                Vector2 at = Vector2.Transform(new Vector2(line.X, line.Y) / TerrainGrid.CellsPerTile, laying);
                var where = new Vector2(place.Where.X + at.X, place.Where.Y + at.Y) * side;
                foreach (int one in entities)
                {
                    if (Vector2.Distance(new Vector2(sightings[one].X, sightings[one].Y), where) <= side)
                    {
                        stands = true;
                        break;
                    }
                }

                if (stands)
                {
                    break;
                }
            }

            if (stands)
            {
                confirmed.Add(place);
            }
        }

        return confirmed.Count > 0 ? (confirmed, true) : (places, false);
    }

    /// <summary>
    /// Every room the doodads could not place, placed by its tiles and settled: a place standing on a doodad place's tiles past the rims yields to it; a place on a tile laid more than once is kept only where a doodad of the room picks it; the places on ONE anchor tile that are not told apart - one room several ways round, or several variants of a room - become that tile's own footprint, drawn once under the name the rooms share. See the class remarks.
    /// </summary>
    /// <param name="placed">Every room with the places its doodads found, settled - returned with the tile places added, and a room for each shared tile after them.</param>
    /// <param name="tilePlaces">Each room's places by its tiles, by file and layout - cached by the caller, the terrain not changing under a placing again.</param>
    /// <param name="sightings">The area's doodad entities, for the lines too few to place a room and enough to confirm a place.</param>
    public static List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> Settled(
        IReadOnlyList<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> placed,
        Func<string, RoomLayout, RoomTilePlaces> tilePlaces,
        IReadOnlyList<DoodadSighting> sightings)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(tilePlaces);
        ArgumentNullException.ThrowIfNull(sightings);

        // THE DOODADS' FOOTPRINTS, which a tile place may not stand on: the doodads are the surer witness.
        var taken = new List<RoomCandidate>();
        foreach ((_, _, RoomDoodadPlaces places) in placed)
        {
            foreach (RoomDoodadPlace place in places.Places)
            {
                taken.Add(place.Where);
            }
        }

        // EACH ROOM'S PLACES, standing clear of the doodads' and confirmed where a line can - by the
        // cell of the anchor slot, so the places on one tile are seen together.
        var byAnchor = new Dictionary<(int X, int Y), List<(int Room, RoomTilePlace Place)>>();
        var whys = new string?[placed.Count];
        for (var one = 0; one < placed.Count; one++)
        {
            (string room, RoomLayout layout, RoomDoodadPlaces places) = placed[one];
            if (places.Places.Count > 0 || places.Matchable >= RoomDoodadFinder.FewestLines)
            {
                // THE DOODADS SPOKE: a place, or enough props standing somewhere to have made one - a room
                // whose props stand in the area and not in its pattern is a room the area did not lay.
                continue;
            }

            RoomTilePlaces found = tilePlaces(room, layout);
            if (found.Places.Count == 0)
            {
                whys[one] = found.Why;
                continue;
            }

            var clear = new List<RoomTilePlace>(found.Places.Count);
            foreach (RoomTilePlace place in found.Places)
            {
                var held = false;
                foreach (RoomCandidate theirs in taken)
                {
                    if (RoomDoodadFinder.Overlap(place.Where, theirs))
                    {
                        held = true;
                        break;
                    }
                }

                if (!held)
                {
                    clear.Add(place);
                }
            }

            if (clear.Count == 0)
            {
                whys[one] = string.Create(CultureInfo.InvariantCulture, $"every one of the {found.Places.Count} places its tiles found stands on a room placed by its doodads");
                continue;
            }

            // A DOODAD PICKS, OR THE ANCHOR MUST BE A FEATURE TILE LAID ONCE - see the class remarks.
            (IReadOnlyList<RoomTilePlace> confirmed, bool byDoodad) = Confirmed(clear, layout.Doodads, layout.Width, layout.Height, sightings);
            IReadOnlyList<RoomTilePlace> picked = byDoodad ? confirmed : [.. confirmed.Where(place => place.AnchorTagged && place.AnchorLaid == 1)];
            if (picked.Count == 0)
            {
                whys[one] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the tiles alike to its {found.Anchor} slot stand {clear.Count} times in the area, none of them a tagged feature tile laid once, and no doodad of its picks one");
                continue;
            }

            foreach (RoomTilePlace place in picked)
            {
                if (!byAnchor.TryGetValue((place.AnchorX, place.AnchorY), out List<(int Room, RoomTilePlace Place)>? standing))
                {
                    standing = [];
                    byAnchor[(place.AnchorX, place.AnchorY)] = standing;
                }

                standing.Add((one, place));
            }
        }

        // EACH ANCHOR TILE SETTLED: one room one way round keeps its footprint; else the tile's own, once.
        var kept = new List<RoomDoodadPlace>?[placed.Count];
        var under = new List<string>?[placed.Count];
        var shared = new List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>();
        foreach (((int anchorX, int anchorY), List<(int Room, RoomTilePlace Place)> standing) in byAnchor.OrderBy(one => one.Key.Y).ThenBy(one => one.Key.X))
        {
            if (standing.Count == 1)
            {
                (int room, RoomTilePlace place) = standing[0];
                (kept[room] ??= []).Add(Place(place));
                continue;
            }

            var rooms = new List<int>();
            foreach ((int room, _) in standing)
            {
                if (!rooms.Contains(room))
                {
                    rooms.Add(room);
                }
            }

            RoomTilePlace first = standing[0].Place;
            var outline = new RoomDoodadPlace(new RoomCandidate(anchorX, anchorY, first.Where.Turn, first.AnchorWide, first.AnchorTall, 0, 0), 0, 0, 0f, [])
            {
                Anchor = first.Anchor,
                AnchorLaid = first.AnchorLaid,
                Variants = [.. rooms.Select(room => placed[room].Room)],
            };

            if (rooms.Count == 1)
            {
                (kept[rooms[0]] ??= []).Add(outline);
                continue;
            }

            // VARIANTS ON ONE TILE: a room of their shared name, beside its own folder, carrying the one outline.
            string stem = Stem([.. rooms.Select(room => TerrainRooms.NameFor(placed[room].Room))]);
            string path = placed[rooms[0]].Room.Replace('\\', '/');
            shared.Add((path[..(path.LastIndexOf('/') + 1)] + stem, placed[rooms[0]].Layout, new RoomDoodadPlaces([outline], 0, 0, 0, string.Empty)));
            foreach (int room in rooms)
            {
                (under[room] ??= []).Add(string.Create(CultureInfo.InvariantCulture, $"'{stem}' on the {SlotWants.Short(first.Anchor)} tile at {anchorX}, {anchorY}"));
            }
        }

        var result = new List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>(placed.Count + shared.Count);
        for (var one = 0; one < placed.Count; one++)
        {
            (string room, RoomLayout layout, RoomDoodadPlaces places) = placed[one];
            if (kept[one] is { } mine)
            {
                result.Add((room, layout, places with { Places = mine, Why = string.Empty }));
            }
            else if (under[one] is { } names)
            {
                result.Add((room, layout, places with { Why = places.Why + " - by its tiles one of the rooms drawn once as " + string.Join(" and as ", names) }));
            }
            else if (whys[one] is { } why)
            {
                result.Add((room, layout, places with { Why = places.Why + " - by its tiles, " + why }));
            }
            else
            {
                result.Add(placed[one]);
            }
        }

        result.AddRange(shared);
        return result;
    }

    /// <summary>
    /// What a set of room names share, for the one outline drawn for them all: their common start, less a trailing number and its underscore - entrance_01 to entrance_04 are "entrance" - or the names themselves where they share nothing.
    /// </summary>
    public static string Stem(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return string.Empty;
        }

        string stem = names[0];
        foreach (string name in names)
        {
            var same = 0;
            while (same < stem.Length && same < name.Length && char.ToLowerInvariant(stem[same]) == char.ToLowerInvariant(name[same]))
            {
                same++;
            }

            stem = stem[..same];
        }

        while (stem.Length > 0 && (char.IsAsciiDigit(stem[^1]) || stem[^1] is '_' or '-'))
        {
            stem = stem[..^1];
        }

        return stem.Length > 0 ? stem : string.Join("/", names);
    }

    /// <summary>A tile place as the map and the report take it, beside the doodads' places.</summary>
    private static RoomDoodadPlace Place(RoomTilePlace place)
        => new(place.Where, 0, 0, 0f, []) { Anchor = place.Anchor, AnchorLaid = place.AnchorLaid, TilesAgree = place.Where.TilesAgree };

    /// <summary>One slot of the room: where it is, which kind it is, and whether it is bigger than one tile.</summary>
    private readonly record struct Slot(int Column, int Line, int Kind, bool Big, int Width, int Height);
}
