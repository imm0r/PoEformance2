using System.Numerics;
using PoEformance.Game.Files;
using PoEformance.Game.Ui;

namespace PoEformance.Game.World;

/// <summary>One place a room's doodads say it stands - see <see cref="RoomDoodadFinder"/>.</summary>
/// <param name="Where">The place: the footprint's corner on the tile grid, which way the room was laid, and its size that way. Matched and Corners carry <see cref="Hits"/> and <see cref="Lines"/>.</param>
/// <param name="Hits">How many of the room's matchable doodad lines have an entity of their path and model within a tile of where this place puts them.</param>
/// <param name="Lines">How many lines were matchable at all - those whose path and model stand somewhere in the area.</param>
/// <param name="MeanOff">How far, on average in world units, a hit's entity stands from where its line puts it.</param>
/// <param name="Missing">Where the matchable lines without an entity would stand, in world units - for the map to mark.</param>
public sealed record RoomDoodadPlace(RoomCandidate Where, int Hits, int Lines, float MeanOff, IReadOnlyList<Vector2> Missing);

/// <summary>Where a room's doodads say it stands - every place, since the area may lay a room more than once.</summary>
/// <param name="Places">The places, most hits first.</param>
/// <param name="Lines">How many of the room's doodad lines name a stub at all.</param>
/// <param name="Matchable">How many of those have an entity of their path and model somewhere in the area.</param>
/// <param name="ByModel">How many of the matchable lines were matched by their model as well as their path - nought says the models did not compare, see the remarks.</param>
/// <param name="Why">Why there is no place, or empty.</param>
public sealed record RoomDoodadPlaces(IReadOnlyList<RoomDoodadPlace> Places, int Lines, int Matchable, int ByModel, string Why)
{
    /// <summary>No place, and why.</summary>
    public static RoomDoodadPlaces Not(int lines, int matchable, string why) => new([], lines, matchable, 0, why);
}

/// <summary>
/// Finds where a room was laid by its doodads: the entities of the area that stand where the room's lines put them.
/// </summary>
/// <remarks>
/// A VOTE OVER PLACES. A room's doodad lines are a point pattern - each a model at a place in the room's
/// own frame - and the area's doodad entities are the point cloud (SleepingDoodads). For each of the eight
/// ways the room can be laid, every pair of a line and an entity of the same path and model says where
/// the room's corner would have to be for the two to coincide; that corner is a whole tile, since rooms
/// are laid on the tile grid, so the pairs vote on tiles and the sub-tile part of a line's place
/// (RoomDoodad.Exact, half a cell past the cell's corner) cannot split a vote. A tile that collects at
/// least two thirds of the room's matchable lines is a place.
///
/// EVERY PLACE, NOT THE BEST: The Assembly's wall modules stand four and more times each - 684 plain
/// doodad entities against some 160 lines across the wall rooms - and a map that draws each room once
/// was wrong for them by construction. The ground search this replaces on the map ranked one place
/// per room and pushed a room off the places a surer one held; this asks the entities, which cannot be
/// two rooms' at once.
///
/// THE MODEL DECIDES WHERE THE PATH CANNOT: most lines carry the plain Doodad path, so a line and an
/// entity match on the model where both have one; where the entity's chain did not read, the path
/// alone has to do. ByModel says how many lines the model settled, and a room with many matchable
/// lines and nought by model says the two paths did not compare - the thing to look at before the
/// vote is believed.
///
/// WHAT IS NOT CONCLUDED: a line whose path and model stand nowhere in the area - the boss room's
/// controllers and markers, which the game does not make visible entities of - is left out of the
/// count rather than counted against the room.
/// </remarks>
public static class RoomDoodadFinder
{
    /// <summary>Fewest matchable lines a room needs before a vote means anything - one entity is a place for every room that names its model.</summary>
    public const int FewestLines = 3;

    /// <summary>How far an entity may stand from where its line puts it and still be the line's - a tile, the same as LaidRoomModels pairs them by.</summary>
    public static float WithinUnits => MapView.WorldToGrid * TerrainGrid.CellsPerTile;

    /// <summary>
    /// Every place the room's doodads stand in the area - see the class remarks. Never throws.
    /// </summary>
    /// <param name="doodads">The room's doodad lines.</param>
    /// <param name="width">The room's grid across, in tiles.</param>
    /// <param name="height">The room's grid down, in tiles.</param>
    /// <param name="sightings">The area's doodad entities, with their path, model and place.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    public static RoomDoodadPlaces Find(
        IReadOnlyList<RoomDoodad> doodads, int width, int height, IReadOnlyList<DoodadSighting> sightings, int tilesX, int tilesY)
    {
        ArgumentNullException.ThrowIfNull(doodads);
        ArgumentNullException.ThrowIfNull(sightings);
        if (width <= 0 || height <= 0 || tilesX <= 0 || tilesY <= 0)
        {
            return RoomDoodadPlaces.Not(0, 0, "no grid to lay it on");
        }

        // THE ENTITIES BY PATH, once; the model is checked per line below.
        var byPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var one = 0; one < sightings.Count; one++)
        {
            if (!byPath.TryGetValue(sightings[one].Path, out List<int>? same))
            {
                same = [];
                byPath[sightings[one].Path] = same;
            }

            same.Add(one);
        }

        // EACH LINE'S CANDIDATES: the entities of its path whose model is its own, or that have none to say.
        var candidates = new List<int>[doodads.Count];
        int lines = 0, matchable = 0, byModel = 0;
        for (var line = 0; line < doodads.Count; line++)
        {
            RoomDoodad doodad = doodads[line];
            if (doodad.Stub.Length == 0)
            {
                continue;
            }

            lines++;
            if (!byPath.TryGetValue(doodad.Stub, out List<int>? same))
            {
                continue;
            }

            string model = doodad.Ao.Replace('\\', '/');
            var mine = new List<int>();
            var modelled = false;
            foreach (int one in same)
            {
                string theirs = sightings[one].Model;
                if (theirs.Length == 0 || model.Length == 0)
                {
                    mine.Add(one);
                }
                else if (string.Equals(theirs, model, StringComparison.OrdinalIgnoreCase))
                {
                    mine.Add(one);
                    modelled = true;
                }
            }

            if (mine.Count > 0)
            {
                candidates[line] = mine;
                matchable++;
                byModel += modelled ? 1 : 0;
            }
        }

        if (matchable < FewestLines)
        {
            return RoomDoodadPlaces.Not(lines, matchable, matchable == 0
                ? (lines == 0 ? "its doodad lines name no stub" : "none of its doodads stands in the area")
                : $"only {matchable} of its doodads stand in the area - fewer than {FewestLines} say nothing");
        }

        // THE VOTE: for each way round, where each pair of a line and one of its entities puts the corner.
        float side = WithinUnits;
        var votes = new Dictionary<(int Turn, int X, int Y), HashSet<int>>();
        for (var turn = 0; turn < 8; turn++)
        {
            (int wide, int tall) = (turn & 1) == 0 ? (width, height) : (height, width);
            Matrix3x2 laying = RoomFinder.Laying(width, height, turn);
            for (var line = 0; line < doodads.Count; line++)
            {
                if (candidates[line] is not { } mine)
                {
                    continue;
                }

                Vector2 at = Vector2.Transform(new Vector2(doodads[line].X, doodads[line].Y) / TerrainGrid.CellsPerTile, laying);
                foreach (int one in mine)
                {
                    var x = (int)MathF.Round((sightings[one].X / side) - at.X);
                    var y = (int)MathF.Round((sightings[one].Y / side) - at.Y);
                    if (x < 0 || y < 0 || x + wide > tilesX || y + tall > tilesY)
                    {
                        continue;
                    }

                    if (!votes.TryGetValue((turn, x, y), out HashSet<int>? voters))
                    {
                        voters = [];
                        votes[(turn, x, y)] = voters;
                    }

                    voters.Add(line);
                }
            }
        }

        // THE PLACES: every tile with two thirds of the matchable lines behind it, each checked line by
        // line - an entity within a tile of where the line stands, EACH ENTITY TO ONE LINE, the nearest
        // pairs first, as LaidRoomModels pairs them: without a model to tell them apart two lines a
        // tile apart would both claim the one entity between them. Told apart by footprint, so a room
        // symmetric enough to fit one place several ways round is one place.
        int needed = Math.Max(FewestLines, ((matchable * 2) + 2) / 3);
        var places = new List<RoomDoodadPlace>();
        var stands = new Vector2[doodads.Count];
        var pairs = new List<(float Distance, int Line, int Entity)>();
        var lineTaken = new bool[doodads.Count];
        var entityTaken = new HashSet<int>();
        foreach (((int turn, int x, int y), HashSet<int> voters) in votes)
        {
            if (voters.Count < needed)
            {
                continue;
            }

            (int wide, int tall) = (turn & 1) == 0 ? (width, height) : (height, width);
            Matrix3x2 laying = RoomFinder.Laying(width, height, turn);
            pairs.Clear();
            for (var line = 0; line < doodads.Count; line++)
            {
                if (candidates[line] is not { } mine)
                {
                    continue;
                }

                Vector2 at = Vector2.Transform(new Vector2(doodads[line].X, doodads[line].Y) / TerrainGrid.CellsPerTile, laying);
                stands[line] = new Vector2(x + at.X, y + at.Y) * side;
                foreach (int one in mine)
                {
                    float distance = Vector2.Distance(new Vector2(sightings[one].X, sightings[one].Y), stands[line]);
                    if (distance <= side)
                    {
                        pairs.Add((distance, line, one));
                    }
                }
            }

            pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            Array.Clear(lineTaken);
            entityTaken.Clear();
            int hits = 0;
            float off = 0f;
            foreach ((float distance, int line, int one) in pairs)
            {
                if (!lineTaken[line] && entityTaken.Add(one))
                {
                    lineTaken[line] = true;
                    hits++;
                    off += distance;
                }
            }

            if (hits < needed)
            {
                continue;
            }

            var missing = new List<Vector2>(matchable - hits);
            for (var line = 0; line < doodads.Count; line++)
            {
                if (candidates[line] is not null && !lineTaken[line])
                {
                    missing.Add(stands[line]);
                }
            }

            places.Add(new RoomDoodadPlace(new RoomCandidate(x, y, turn, wide, tall, hits, matchable), hits, matchable, off / hits, missing));
        }

        places.Sort((a, b) =>
        {
            int order = b.Hits.CompareTo(a.Hits);
            order = order != 0 ? order : a.MeanOff.CompareTo(b.MeanOff);
            order = order != 0 ? order : a.Where.Y.CompareTo(b.Where.Y);
            order = order != 0 ? order : a.Where.X.CompareTo(b.Where.X);
            return order != 0 ? order : a.Where.Turn.CompareTo(b.Where.Turn);
        });

        var kept = new List<RoomDoodadPlace>(places.Count);
        var footprints = new HashSet<(int X, int Y, int Wide, int Tall)>();
        foreach (RoomDoodadPlace place in places)
        {
            if (footprints.Add((place.Where.X, place.Where.Y, place.Where.Width, place.Where.Height)))
            {
                kept.Add(place);
            }
        }

        return new RoomDoodadPlaces(kept, lines, matchable, byModel, kept.Count > 0 ? string.Empty : "no tile collects two thirds of its doodads");
    }
}
