using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// A room as the area laid it: the tiles the game actually put down under a place the room search found, and the room's own doodads set on them.
/// </summary>
/// <remarks>
/// THE TILES ARE THE AREA'S, NOT THE ROOM'S. Where a room is joined to the map the game lays its own
/// tiles over the room's closed rim - seepage's offices, every one of 58 misses a join's - so the
/// room's file says what the room is and the area what was built. Each tile file is put down once
/// per piece (see TerrainTiles.Pieces), turned the way the game laid it (TileOrientation, the decode
/// the heights are read through), about the middle of its own ground, at the middle of the area
/// tiles the piece covers.
///
/// ONE THING THE FILES DO NOT SAY IS SETTLED BY THE GROUND: how a tile's mesh lies on the template
/// the game's turn is read through - as filed, turned, or turned over. All eight ways a square can lie
/// are tried, the turn the game laid each piece applied after, and every piece's ground is asked
/// how high it is at nine points per tile and held against the height the area itself has there. A
/// slope - a stair, a ramp - fits one way only, so the way the sloped pieces fit best is the way
/// every piece is drawn, and the line under the picture says how well, and how well the next did.
/// With no slope in the room nothing is settled and the files are taken as filed. Each piece is then
/// raised to the area's own ground, the doodads with it.
///
/// THE HEIGHTS COUNT THE GAME'S WAY, DOWN, IN BOTH, and are not tried the other way any more. The
/// area's are GameHelper2's terrain heights, which go into the world as its z, and the world's up is
/// minus z (a health bar floats at z less the model's height); the files' up is minus z as well,
/// which is why the tile book stands its walls up. Trying the other sign as well, as 0.1.102 did, is
/// worse than redundant: a slope turned half round and counted upside down is the same slope, so
/// the two answers cannot be told apart on a ramp or an even stair, and a choice between them is a
/// coin toss that shows as pieces turned round. The room that showed it chose "Y turned over,
/// heights the other way" on 6 of its 14 sloped pieces.
///
/// HOW HIGH A PIECE GOES IS NOT SETTLED, and the line says what each answer would do. Fitted, each
/// piece is raised until its ground meets the area's on average - which is exact only where the file's
/// ground has the shape the area's heights have, and the sloped pieces say it often does not. At its
/// tile's level, it goes where the area's own heights are measured from: a tile's sub-tile heights
/// are described relative to its level (TerrainHeightField), and a file's ground counted from the
/// same level would be set there unfitted. Seepage's offices, fitted, came out with their sand over
/// floors the game shows bare - which of the two the game does is what the switch and the line are for.
///
/// AND A PIECE SEVERAL TILES ACROSS IS CHECKED AGAINST THE AREA'S OWN INDEX: every one of its area
/// tiles names the column and row of the file it holds, so where the drawing puts each sub-tile can
/// be compared with where the game says it is - counting the rows from the top, as the files are laid
/// out (TileModels), and from the bottom, so that the line says which one the area agrees with.
///
/// THE DOODADS GO WHERE THE ROOM'S CORNERS WERE FOUND - RoomFinder.Laying, the corners' own
/// arithmetic - a doodad's x and y counting the room's columns and lines, the frame
/// RoomModels.SpreadOf already draws a room's doodads in. That is this project's reading and still
/// waits on the game: annalithic/poeterrain is a Path of Exile 1 importer, and both the line that
/// reads its doodads and the loop that places them are commented out, so it settles nothing here.
/// </remarks>
public static class LaidRoomModels
{
    /// <summary>The cells into a tile the ground is asked at, each way: near both edges and in the middle.</summary>
    private static readonly int[] Asked = [4, 11, 18];

    /// <summary>How far a piece's ground may sit from the area's, in world units, and still be said to fit.</summary>
    private const float Fits = 8f;

    /// <summary>How much the area's own ground must rise within a piece for it to count as sloped.</summary>
    private const float Rise = 4f;

    /// <summary>The eight ways a file can lie on its template, as TileOrientation numbers its placements - as filed first.</summary>
    private static readonly int[] Relations = [3, 0, 1, 2, 4, 5, 6, 7];

    /// <summary>
    /// The room laid where the search found it, or why not. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The room's <c>.arm</c>.</param>
    /// <param name="grid">The area the room was found in - its tiles and its ground.</param>
    /// <param name="x">The place's lowest area tile across.</param>
    /// <param name="y">The place's lowest area tile down.</param>
    /// <param name="turn">How the room was laid - see RoomFinder.Said.</param>
    /// <param name="shaded">Whether each material's shader graphs are read.</param>
    /// <param name="doodads">Most doodads placed, and the measure of the tiles' triangles too - see RoomModels.UsualDoodads.</param>
    /// <param name="tools">Whether the level editor's tools are placed too.</param>
    /// <param name="atLevel">Whether each piece is set at its tiles' own level rather than fitted to the area's ground - see the remarks.</param>
    /// <param name="progress">Where the build says how far it has got, or null - see <see cref="ModelProgress"/>.</param>
    /// <param name="heights">How high a doodad whose line carries a height is set - see <see cref="DoodadHeight"/>.</param>
    /// <param name="entities">The area's entities in memory, to hold the room's doodads against - see <see cref="HeightsSaid"/> - or null.</param>
    /// <param name="camera">The game's camera as last read, for what it can see of the room - see CameraSight - or null.</param>
    /// <param name="sight">Whether what the camera cannot see from anywhere the player can stand is left out - see <see cref="Hiding"/>.</param>
    /// <param name="cut">How far under the area's ground, in world units, everything is left out; nought or less leaves nothing out - see <see cref="Hiding"/>.</param>
    public static MonsterModel Of(
        Func<string, byte[]?>? read,
        string? path,
        TerrainGrid? grid,
        int x,
        int y,
        int turn,
        bool shaded = false,
        int doodads = RoomModels.UsualDoodads,
        bool tools = false,
        bool atLevel = false,
        ModelProgress? progress = null,
        DoodadHeight heights = DoodadHeight.File,
        IReadOnlyList<WorldEntity>? entities = null,
        CameraShot? camera = null,
        bool sight = false,
        int cut = 0)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no room to read" };
        }

        if (grid?.Tiles is not { } tiles)
        {
            return MonsterModel.None with { Why = "the area's tiles are not read - the room cannot be laid from them" };
        }

        long bytes = 0;
        var files = 0;
        byte[]? Counted(string one)
        {
            byte[]? got = read(one.Replace('\\', '/').Trim());
            if (got is not null)
            {
                bytes += got.Length;
                files++;
            }

            return got;
        }

        RoomLayout room = RoomLayout.Read(Counted(path));
        if (!room.Ready)
        {
            return MonsterModel.None with { Why = $"the room did not read: {path} - {room.Why}", Bytes = bytes, Files = files };
        }

        (int wide, int tall) = (turn & 1) == 0 ? (room.Width, room.Height) : (room.Height, room.Width);

        // EACH TILE FILE ONCE, however many pieces of it the room holds - and counted for the bar as
        // each is read, which is while the pieces are found: that is where the files are opened.
        var paints = new MonsterModels.Paints { Progress = progress };
        var made = new Dictionary<int, Made>();
        ModelProgress.Step reading = ModelProgress.Begin(progress, "reading the tile files", Distinct(tiles, x, y, wide, tall));
        Made Of(int id)
        {
            if (!made.TryGetValue(id, out Made? one))
            {
                (MonsterModel props, SkinnedMesh ground, int across, int down) = TileModels.Apart(Counted, tiles.Paths[id], walls: true, paints);
                one = Made.Of(props, ground, across, down);
                made[id] = one;
                reading.Advance();
            }

            return one;
        }

        List<TilePiece> pieces = tiles.Pieces(x, y, wide, tall, id => (Of(id).Width, Of(id).Height));
        reading.Dispose();
        reading = default;

        // THE WAY THE FILES LIE ON THE TEMPLATE, from the sloped pieces - see the remarks. As filed
        // comes first, so it is the one kept wherever the ground cannot tell two ways apart.
        var ways = new Way[Relations.Length];
        for (var way = 0; way < ways.Length; way++)
        {
            ways[way] = new Way(Relations[way]);
        }

        foreach (TilePiece piece in pieces)
        {
            Made one = Of(piece.Id);
            if (!piece.Whole || one.Lookup is null || !Way.Rises(piece, grid))
            {
                continue;
            }

            foreach (Way way in ways)
            {
                way.Weigh(piece, one, grid, x, y);
            }
        }

        Way chosen = ways[0];
        bool settled = ways.Any(one => one.Sloped > 0);
        if (settled)
        {
            chosen = ways.MinBy(one => one.Residual)!;
        }

        // LAID, the room's corner at the picture's origin and each piece on the area's own ground.
        var pile = new ModelPile();
        long mostTriangles = (long)(doodads > 0 ? doodads : RoomModels.UsualDoodads) * RoomModels.TrianglesPerDoodad;
        int laidPieces = 0, broken = 0, bare = 0, capped = 0, unturned = 0;
        int several = 0, fromTop = 0, fromBottom = 0;
        int fitsFitted = 0, fitsAtLevel = 0;
        var above = new List<float>(pieces.Count);
        var used = new HashSet<int>();
        foreach (TilePiece piece in pieces)
        {
            Made one = Of(piece.Id);
            if (!piece.Whole)
            {
                broken++;
                continue;
            }

            if (!one.Props.Ready && !one.Ground.Ready)
            {
                bare++;
                continue;
            }

            Matrix3x2 flat = chosen.Flat(piece, one, x, y);
            if (piece.Width * piece.Height > 1)
            {
                several++;
                fromTop += Named(piece, one, tiles, x, y, flat, fromTheTop: true) ? 1 : 0;
                fromBottom += Named(piece, one, tiles, x, y, flat, fromTheTop: false) ? 1 : 0;
            }

            long cost = (one.Props.Ready ? one.Props.Mesh.Triangles : 0) + (one.Ground.Ready ? one.Ground.Triangles : 0);
            if (pile.Triangles + cost > mostTriangles)
            {
                capped++;
                continue;
            }

            float level = Level(piece, grid);
            Height height = Way.Measure(piece, one, grid, x, y, flat, level);
            if (height.Samples >= 3)
            {
                above.Add(level - height.Fitted);
                fitsFitted += height.MissFitted <= Fits ? 1 : 0;
                fitsAtLevel += height.MissAtLevel <= Fits ? 1 : 0;
            }

            float lift = atLevel ? level : height.Fitted;
            Matrix4x4 place = Spatial(flat) * Matrix4x4.CreateTranslation(0f, 0f, lift);

            // NAMED BY FILE AND TILE, so the probe can say which piece a pixel belongs to - one string
            // per piece, shared by all of its shapes.
            string called = string.Create(CultureInfo.InvariantCulture, $"{tiles.Paths[piece.Id]} at tile {piece.MinX}, {piece.MinY}");
            if (one.Props.Ready)
            {
                pile.Add(one.Props, place, called);
            }

            if (one.Ground.Ready)
            {
                pile.AddPlain(one.Ground, place, "ground of " + called);
            }

            used.Add(piece.Id);
            laidPieces++;
            unturned += piece.Placement < 0 ? 1 : 0;
        }

        // WHAT CAN BE SEEN, worked out before a doodad is read - see Hiding.
        var origin = new Vector2(x, y) * TileModels.Side;
        Hiding hiding = Hiding.Of(pile, grid, origin, new Vector2(wide, tall) * TileModels.Side, camera, sight, cut, progress);

        // THE DOODADS, where the corners were found and on the area's ground under them.
        Matrix3x2 laying = RoomFinder.Laying(room.Width, room.Height, turn);
        Matrix4x4 beyondFlat = Spatial(Matrix3x2.CreateScale(1f / TileModels.Side) * laying * Matrix3x2.CreateScale(TileModels.Side));
        var lights = new RoomLights.Gathered(Counted);
        RoomModels.Doodads laid = RoomModels.Lay(room, Counted, paints, pile, doodads, tools, one =>
        {
            Vector2 at = Vector2.Transform(new Vector2(one.X, one.Y) / RoomModels.CellsPerTile, laying);
            int cellX = (int)((x + at.X) * RoomModels.CellsPerTile);
            int cellY = (int)((y + at.Y) * RoomModels.CellsPerTile);
            float ground = grid.HeightAt(cellX, cellY);
            float z = heights switch
            {
                DoodadHeight.File when one.Height is { } height => height,
                DoodadHeight.Added when one.Height is { } height => ground + height,
                _ => ground,
            };
            return beyondFlat * Matrix4x4.CreateTranslation(0f, 0f, z);
        }, heights, lights, hiding.Sieve);

        SkinnedMesh joined = hiding.Keep(SkinnedMesh.Joined(pile.Joins), pile);
        if (!joined.Ready)
        {
            return MonsterModel.None with
            {
                Why = $"nothing to draw: {pieces.Count} pieces under the place, {laidPieces} laid, and {laid.Placed} doodads"
                    + string.Concat(hiding.Said().Select(one => "; " + one)),
                Bytes = bytes,
                Files = files,
            };
        }

        var said = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"laid as the area laid it at tile {x}, {y}, {RoomFinder.Said(turn)}: {laidPieces} pieces of {used.Count} tile files")
                + (broken > 0 ? string.Create(CultureInfo.InvariantCulture, $", {broken} not whole and left out") : string.Empty)
                + (bare > 0 ? string.Create(CultureInfo.InvariantCulture, $", {bare} with nothing to draw") : string.Empty)
                + (capped > 0 ? string.Create(CultureInfo.InvariantCulture, $", {capped} left out to keep it turnable - the doodads slider raises it") : string.Empty)
                + (unturned > 0 ? string.Create(CultureInfo.InvariantCulture, $", {unturned} laid no way the game's tables said - drawn as their files have them") : string.Empty),
            Heights(ways, chosen, settled),
            Levels(above, fitsFitted, fitsAtLevel, atLevel),
        };
        if (several > 0)
        {
            said.Add(string.Create(CultureInfo.InvariantCulture,
                $"sub-tiles: of {several} pieces several tiles across, {fromTop} have every tile where the area's own index puts it counting the file's rows from the top, {fromBottom} counting them from the bottom"));
        }

        said.AddRange(laid.Said());
        said.AddRange(hiding.Said());
        string held = HeightsSaid(room, laying, x, y, grid, entities);
        if (held.Length > 0)
        {
            said.Add(held);
        }

        MonsterModel model = RoomModels.Piled(joined, pile, path, made.Values.Select(one => one.Props).Concat(laid.Models)) with
        {
            Parts = laidPieces + laid.Placed,
            Bytes = bytes,
            Files = files,
            Move = string.Join("; ", said),
            Lights = lights.Lights,
            LightsSaid = lights.Said(),
            AreaOrigin = origin,
        };

        return shaded ? MonsterModels.Shaded(Counted, model, paints) : model;
    }

    /// <summary>What the ground said about the ways a file can lie, for the line under the picture.</summary>
    private static string Heights(Way[] ways, Way chosen, bool settled)
    {
        if (!settled)
        {
            return "heights: no sloped piece to settle how the files lie on the area's ground - drawn as filed, each piece raised to the area's ground";
        }

        Way[] ranked = [.. ways.OrderBy(one => one.Residual)];
        Way next = ranked[0] == chosen ? ranked[1] : ranked[0];
        int rest = ways.Where(one => one != chosen && one != next).Max(one => one.Fitting);
        return string.Create(CultureInfo.InvariantCulture,
            $"heights: drawn with the files {chosen.Said} before the game's own turn - {chosen.Fitting}/{chosen.Sloped} sloped pieces meet the area's ground within {Fits} units; next {next.Said} {next.Fitting}/{next.Sloped}; the other {ways.Length - 2} at most {rest}/{chosen.Sloped}");
    }

    /// <summary>What the two ways of setting a piece's height said, for the line under the picture.</summary>
    private static string Levels(List<float> above, int fitsFitted, int fitsAtLevel, bool atLevel)
    {
        string drawn = atLevel ? "drawn at the tiles' levels" : "drawn fitted";
        if (above.Count == 0)
        {
            return "level: no piece with a ground to measure - " + drawn;
        }

        above.Sort();
        float Share(float share) => above[Math.Clamp((int)MathF.Round(share * (above.Count - 1)), 0, above.Count - 1)];
        return string.Create(CultureInfo.InvariantCulture,
            $"level: fitted to the area's ground, a piece sits {Share(0.5f):+0;-0;0} units above its tile's own level (the middle half {Share(0.25f):+0;-0;0} to {Share(0.75f):+0;-0;0}, all {above[0]:+0;-0;0} to {above[^1]:+0;-0;0}), and its ground meets the area's within {Fits} units on {fitsAtLevel}/{above.Count} pieces at the tile's level, {fitsFitted}/{above.Count} fitted - {drawn}");
    }

    /// <summary>The level of the tiles a piece covers, which their sub-tile heights are described relative to - their mean, should they differ.</summary>
    private static float Level(TilePiece piece, TerrainGrid grid)
    {
        float sum = 0f;
        for (int tileY = piece.MinY; tileY <= piece.MaxY; tileY++)
        {
            for (int tileX = piece.MinX; tileX <= piece.MaxX; tileX++)
            {
                sum += grid.LevelAt(tileX, tileY);
            }
        }

        return sum / (piece.Width * piece.Height);
    }

    /// <summary>
    /// Whether every area tile of a piece holds the sub-tile the drawing puts over it, by the area's own index: its column, and its row counted from the file's top or its bottom.
    /// </summary>
    private static bool Named(TilePiece piece, Made one, TerrainTiles tiles, int x, int y, Matrix3x2 flat, bool fromTheTop)
    {
        if (!Matrix3x2.Invert(flat, out Matrix3x2 back))
        {
            return false;
        }

        for (int tileY = piece.MinY; tileY <= piece.MaxY; tileY++)
        {
            for (int tileX = piece.MinX; tileX <= piece.MaxX; tileX++)
            {
                Vector2 file = Vector2.Transform(new Vector2(tileX - x + 0.5f, tileY - y + 0.5f) * TileModels.Side, back);
                int column = (int)MathF.Floor((file.X - one.Least.X) / TileModels.Side);
                int row = (int)MathF.Floor((fromTheTop ? one.Most.Y - file.Y : file.Y - one.Least.Y) / TileModels.Side);
                if (tiles.SubAt(tileX, tileY) != (column, row))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Where a piece's height comes out each way: the lift that fits its ground to the area's, how far its ground then misses the area's, and how far it misses at its tile's level - root mean squares, in world units.
    /// </summary>
    private readonly record struct Height(float Fitted, int Samples, double MissFitted, double MissAtLevel);

    /// <summary>Most doodads named one by one in the heights line; the rest are only counted.</summary>
    private const int MostHeldNamed = 6;

    /// <summary>
    /// The room's doodads held against the entities the game made of them: the height each line carries beside the entity's own z, its terrain height and the area's ground there, and how far it is from where the room puts it.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. A doodad line carries a value no reference names - RoomDoodad.Height - and the
    /// one pot read by hand from memory sat at exactly its line's -115. That one cannot tell "the value
    /// is the height" from "the ground plus the value", because the ground under it was where the value
    /// put it. The game settles it wherever the two differ, and every doodad it makes an entity of is a
    /// case: found by its stub, which is the entity's own path, and by being the nearest of that path to
    /// where the room puts the doodad - within a tile, or it is somebody else's.
    ///
    /// BOTH HALVES PRINTED, AND COUNTED, NOT CONCLUDED: the z and each reading's prediction, so the line
    /// is read rather than trusted - the same stance as the level line above it.
    /// </remarks>
    internal static string HeightsSaid(RoomLayout room, Matrix3x2 laying, int x, int y, TerrainGrid grid, IReadOnlyList<WorldEntity>? entities)
    {
        if (entities is null)
        {
            return string.Empty;
        }

        var byPath = new Dictionary<string, List<WorldEntity>>(StringComparer.OrdinalIgnoreCase);
        foreach (WorldEntity entity in entities)
        {
            if (entity.Path.Length == 0)
            {
                continue;
            }

            if (!byPath.TryGetValue(entity.Path, out List<WorldEntity>? named))
            {
                named = [];
                byPath[entity.Path] = named;
            }

            named.Add(entity);
        }

        // EACH ENTITY TO ONE DOODAD, the nearest pairs first: two pots a few cells apart would otherwise
        // both claim the one entity nearer to either, and the second pot report a height it never had.
        var pairs = new List<(float Distance, int Doodad, WorldEntity Entity)>();
        var cells = new Vector2[room.Doodads.Count];
        float nearest = float.PositiveInfinity;
        for (var at = 0; at < room.Doodads.Count; at++)
        {
            RoomDoodad one = room.Doodads[at];
            cells[at] = InArea(laying, x, y, new Vector2(one.X, one.Y) / RoomModels.CellsPerTile);
            if (one.Stub.Length == 0 || !byPath.TryGetValue(one.Stub, out List<WorldEntity>? candidates))
            {
                continue;
            }

            foreach (WorldEntity entity in candidates)
            {
                float distance = Vector2.Distance(new Vector2(entity.WorldX, entity.WorldY), cells[at]);
                nearest = MathF.Min(nearest, distance);
                if (distance <= TileModels.Side)
                {
                    pairs.Add((distance, at, entity));
                }
            }
        }

        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var matched = new WorldEntity?[room.Doodads.Count];
        var taken = new HashSet<WorldEntity>(ReferenceEqualityComparer.Instance);
        foreach ((_, int doodad, WorldEntity entity) in pairs)
        {
            if (matched[doodad] is null && taken.Add(entity))
            {
                matched[doodad] = entity;
            }
        }

        int found = 0, carrying = 0, asHeight = 0, asAdded = 0, asGround = 0;
        var listed = new List<string>(MostHeldNamed);
        for (var at = 0; at < room.Doodads.Count; at++)
        {
            if (matched[at] is not { } entity)
            {
                continue;
            }

            RoomDoodad one = room.Doodads[at];
            var place = new Vector2(entity.WorldX, entity.WorldY);
            found++;
            float ground = grid.HeightAt((int)(entity.WorldX / RoomModels.CellSize), (int)(entity.WorldY / RoomModels.CellSize));
            bool height = one.Height is { } value && MathF.Abs(entity.WorldZ - value) < 1f;
            bool added = one.Height is { } plus && MathF.Abs(entity.WorldZ - (ground + plus)) < 1f;
            bool onGround = MathF.Abs(entity.WorldZ - ground) < 1f;
            carrying += one.Height.HasValue ? 1 : 0;
            asHeight += height ? 1 : 0;
            asAdded += added ? 1 : 0;
            asGround += onGround ? 1 : 0;

            if (listed.Count < MostHeldNamed)
            {
                string exact = one.Exact is { } spot
                    ? string.Create(CultureInfo.InvariantCulture, $", {Vector2.Distance(place, InArea(laying, x, y, spot / TileModels.Side)):0.0} from its exact place")
                    : string.Empty;
                listed.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{Tail(one.Ao)}: line {(one.Height is { } said ? said.ToString("0.#", CultureInfo.InvariantCulture) : "none")}, z {entity.WorldZ:0.#}, its terrain height {entity.TerrainHeight:0.#}, the area's ground {ground:0.#}, {Vector2.Distance(place, cells[at]):0.0} from its cell{exact}"));
            }
        }

        if (found == 0)
        {
            return Missed(room, entities, byPath, nearest);
        }

        string counted = string.Create(CultureInfo.InvariantCulture,
            $"doodad heights: {found} of the room's doodads are in memory as entities, {carrying} with a height in their line - z is that height on {asHeight}, the ground plus it on {asAdded}, the ground on {asGround}");
        return counted + "; " + string.Join("; ", listed) + (found > listed.Count ? string.Create(CultureInfo.InvariantCulture, $"; and {found - listed.Count} more") : string.Empty);
    }

    /// <summary>
    /// Why no doodad was found: how many entities were read, how many carry a path the room names as a stub and how near the nearest came, and how many carry a stub's name in another path.
    /// </summary>
    /// <remarks>
    /// THE NEAR MISSES, because "none" alone cannot say whether the place was wrong, the path was
    /// spelt another way, or the game simply holds none of them - the first report of this line was
    /// exactly that, beside a pot read by hand from memory minutes before.
    /// </remarks>
    private static string Missed(
        RoomLayout room, IReadOnlyList<WorldEntity> entities, Dictionary<string, List<WorldEntity>> byPath, float nearest)
    {
        var stubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RoomDoodad one in room.Doodads)
        {
            if (one.Stub.Length > 0 && stubs.Add(one.Stub))
            {
                names.Add(Tail(one.Stub));
            }
        }

        int sharing = 0;
        foreach (string stub in stubs)
        {
            sharing += byPath.TryGetValue(stub, out List<WorldEntity>? same) ? same.Count : 0;
        }

        int alike = 0;
        string example = string.Empty;
        foreach (WorldEntity entity in entities)
        {
            if (entity.Path.Length == 0 || stubs.Contains(entity.Path))
            {
                continue;
            }

            foreach (string name in names)
            {
                if (entity.Path.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    alike++;
                    if (example.Length == 0)
                    {
                        example = entity.Path;
                    }

                    break;
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"doodad heights: none of the room's doodads is in memory as an entity - {entities.Count} entities read, {sharing} with a path the room names as a stub")
            + (sharing > 0 ? string.Create(CultureInfo.InvariantCulture, $", the nearest {nearest:0} units from such a doodad (a tile is {TileModels.Side:0})") : string.Empty)
            + (alike > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {alike} with a stub's name in another path, such as {example}") : string.Empty);
    }

    /// <summary>A place in the room, in tiles from its corner, as a place in the area in world units.</summary>
    private static Vector2 InArea(Matrix3x2 laying, int x, int y, Vector2 inTiles)
    {
        Vector2 at = Vector2.Transform(inTiles, laying);
        return new Vector2(x + at.X, y + at.Y) * TileModels.Side;
    }

    private static string Tail(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>How many different tile files the block names - what the bar counts the reading against.</summary>
    private static int Distinct(TerrainTiles tiles, int x, int y, int wide, int tall)
    {
        var seen = new HashSet<int>();
        for (int row = y; row < y + tall; row++)
        {
            for (int column = x; column < x + wide; column++)
            {
                int id = tiles.IdAt(column, row);
                if (id >= 0)
                {
                    seen.Add(id);
                }
            }
        }

        return seen.Count;
    }

    /// <summary>A flat placement as a placement in space, the height left alone.</summary>
    private static Matrix4x4 Spatial(Matrix3x2 flat) => new(
        flat.M11, flat.M12, 0f, 0f,
        flat.M21, flat.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        flat.M31, flat.M32, 0f, 1f);

    /// <summary>One tile file, made once: its props, its ground and where to ask it a height, its size in tiles, and its box in its own frame - the ground's, or the props' where it has none.</summary>
    private sealed record Made(MonsterModel Props, SkinnedMesh Ground, GroundLookup? Lookup, int Width, int Height, Vector2 Least, Vector2 Most)
    {
        /// <summary>The middle of the box, which a piece is turned about.</summary>
        public Vector2 Middle => (Least + Most) / 2f;

        public static Made Of(MonsterModel props, SkinnedMesh ground, int across, int down)
        {
            (Vector3 least, Vector3 most) = ground.Ready
                ? (ground.Least, ground.Most)
                : props.Ready ? (props.Mesh.Least, props.Mesh.Most) : (Vector3.Zero, Vector3.Zero);
            return new Made(
                props,
                ground,
                ground.Ready ? new GroundLookup(ground) : null,
                Math.Max(1, across),
                Math.Max(1, down),
                new Vector2(least.X, least.Y),
                new Vector2(most.X, most.Y));
        }
    }

    /// <summary>
    /// One way a file can lie on the template the game's turn is read through, and how well the sloped pieces fit it.
    /// </summary>
    private sealed class Way
    {
        private readonly Matrix3x2 _lies;

        /// <param name="relation">The way, numbered as TileOrientation numbers its placements.</param>
        public Way(int relation)
        {
            TileOrientation lies = TileOrientation.OfPlacement(relation);
            _lies = TileModels.Turned(lies) is { } turned
                ? new Matrix3x2(turned.M11, turned.M12, turned.M21, turned.M22, 0f, 0f)
                : Matrix3x2.Identity;
            (int xx, int xy, int yx, int yy) = lies.Turn;
            Said = (lies.Degrees == 0 && !lies.Mirrored ? "as filed" : lies.ToString()) + " (" + Axis(xx, xy) + ", " + Axis(yx, yy) + ")";
        }

        /// <summary>This way in words, and where the file's x and y go: "mirrored, turned 180° (x, -y)".</summary>
        public string Said { get; }

        /// <summary>Sloped pieces weighed.</summary>
        public int Sloped { get; private set; }

        /// <summary>Of those, the ones whose ground met the area's within <see cref="Fits"/>.</summary>
        public int Fitting { get; private set; }

        /// <summary>The squared misses over every sloped piece, after each was raised to fit as well as it can.</summary>
        public double Residual { get; private set; }

        /// <summary>
        /// Where a piece's file goes, flat: its middle to the origin, laid on the template this way, the game's turn, then out to the middle of the area tiles it covers - from the room's corner, in world units.
        /// </summary>
        public Matrix3x2 Flat(TilePiece piece, Made one, int x, int y)
        {
            Matrix3x2 flat = Matrix3x2.CreateTranslation(-one.Middle) * _lies;
            if (TileModels.Turned(TileOrientation.OfPlacement(piece.Placement)) is { } turned)
            {
                flat *= new Matrix3x2(turned.M11, turned.M12, turned.M21, turned.M22, 0f, 0f);
            }

            var middle = new Vector2(
                (((piece.MinX + piece.MaxX + 1) / 2f) - x) * TileModels.Side,
                (((piece.MinY + piece.MaxY + 1) / 2f) - y) * TileModels.Side);
            return flat * Matrix3x2.CreateTranslation(middle);
        }

        /// <summary>
        /// The lift that meets a piece's ground with the area's on average, and how far the ground misses the area's fitted that way and set at <paramref name="level"/> - one pass over the same points.
        /// </summary>
        public static Height Measure(TilePiece piece, Made one, TerrainGrid grid, int x, int y, Matrix3x2 flat, float level)
        {
            int middleX = ((piece.MinX + piece.MaxX + 1) * RoomModels.CellsPerTile) / 2;
            int middleY = ((piece.MinY + piece.MaxY + 1) * RoomModels.CellsPerTile) / 2;
            if (one.Lookup is null || !Matrix3x2.Invert(flat, out Matrix3x2 back))
            {
                return new Height(grid.HeightAt(middleX, middleY), 0, double.NaN, double.NaN);
            }

            double sum = 0;
            double squares = 0;
            double atLevel = 0;
            int count = 0;
            foreach ((float file, float area) in Samples(piece, one.Lookup, grid, x, y, back))
            {
                double miss = area - file;
                sum += miss;
                squares += miss * miss;
                atLevel += (miss - level) * (miss - level);
                count++;
            }

            if (count == 0)
            {
                return new Height(grid.HeightAt(middleX, middleY), 0, double.NaN, double.NaN);
            }

            double fitted = sum / count;
            return new Height(
                (float)fitted,
                count,
                Math.Sqrt(Math.Max(0, (squares / count) - (fitted * fitted))),
                Math.Sqrt(atLevel / count));
        }

        /// <summary>Whether the area's own ground rises within a piece - asked of the area alone, before any mesh is.</summary>
        public static bool Rises(TilePiece piece, TerrainGrid grid)
        {
            float low = float.MaxValue;
            float high = float.MinValue;
            for (int tileY = piece.MinY; tileY <= piece.MaxY; tileY++)
            {
                for (int tileX = piece.MinX; tileX <= piece.MaxX; tileX++)
                {
                    foreach (int down in Asked)
                    {
                        foreach (int across in Asked)
                        {
                            float area = grid.HeightAt((tileX * RoomModels.CellsPerTile) + across, (tileY * RoomModels.CellsPerTile) + down);
                            low = Math.Min(low, area);
                            high = Math.Max(high, area);
                        }
                    }
                }
            }

            return high - low >= Rise;
        }

        /// <summary>Weighs one sloped piece against this way.</summary>
        public void Weigh(TilePiece piece, Made one, TerrainGrid grid, int x, int y)
        {
            if (one.Lookup is null || !Matrix3x2.Invert(Flat(piece, one, x, y), out Matrix3x2 back))
            {
                return;
            }

            double sum = 0;
            double squares = 0;
            int count = 0;
            foreach ((float file, float area) in Samples(piece, one.Lookup, grid, x, y, back))
            {
                double miss = area - file;
                sum += miss;
                squares += miss * miss;
                count++;
            }

            if (count < 3)
            {
                return;
            }

            // The squared misses about their mean, which is what is left once the piece is raised.
            double squared = Math.Max(0, squares - (sum * sum / count));
            Sloped++;
            Residual += squared;
            Fitting += Math.Sqrt(squared / count) <= Fits ? 1 : 0;
        }

        /// <summary>The file's ground height and the area's at the points asked, in every tile of the piece.</summary>
        private static IEnumerable<(float File, float Area)> Samples(TilePiece piece, GroundLookup ground, TerrainGrid grid, int x, int y, Matrix3x2 back)
        {
            float cell = TileModels.Side / RoomModels.CellsPerTile;
            for (int tileY = piece.MinY; tileY <= piece.MaxY; tileY++)
            {
                for (int tileX = piece.MinX; tileX <= piece.MaxX; tileX++)
                {
                    foreach (int down in Asked)
                    {
                        foreach (int across in Asked)
                        {
                            var world = new Vector2(
                                ((tileX - x) * TileModels.Side) + ((across + 0.5f) * cell),
                                ((tileY - y) * TileModels.Side) + ((down + 0.5f) * cell));
                            if (ground.HeightAt(Vector2.Transform(world, back)) is { } file)
                            {
                                yield return (file, grid.HeightAt((tileX * RoomModels.CellsPerTile) + across, (tileY * RoomModels.CellsPerTile) + down));
                            }
                        }
                    }
                }
            }
        }

        /// <summary>One file axis's place in words.</summary>
        private static string Axis(int onX, int onY) => (onX, onY) switch
        {
            (1, 0) => "x",
            (-1, 0) => "-x",
            (0, 1) => "y",
            (0, -1) => "-y",
            _ => "?",
        };
    }

    /// <summary>
    /// A ground's triangles filed by the squares they reach, so a height is asked of the few under a point rather than of every one.
    /// </summary>
    /// <remarks>
    /// EIGHT WAYS ASK EIGHT TIMES as many heights as one did, of every sloped piece and then of every
    /// piece laid, and a scan of the whole ground per point grew with the ground's size on top. Filed
    /// once per tile file, a point looks only at its own square's list, in the mesh's order - so the
    /// triangle that answers is the same first one the full scan found.
    /// </remarks>
    private sealed class GroundLookup
    {
        /// <summary>A square's side in world units: a cell and a half, about the size of a ground triangle.</summary>
        private const float Step = 16f;

        /// <summary>The most squares each way, whatever the ground's size.</summary>
        private const int MostSquares = 256;

        private readonly Vector3[] _points;
        private readonly int[] _indices;
        private readonly Vector2 _least;
        private readonly float _stepX;
        private readonly float _stepY;
        private readonly int _across;
        private readonly int _down;
        private readonly int[] _starts;
        private readonly int[] _filed;

        public GroundLookup(SkinnedMesh mesh)
        {
            _points = mesh.Positions;
            _indices = mesh.Indices;
            _least = new Vector2(mesh.Least.X, mesh.Least.Y);
            float wide = MathF.Max(mesh.Most.X - mesh.Least.X, 1e-3f);
            float tall = MathF.Max(mesh.Most.Y - mesh.Least.Y, 1e-3f);
            _across = Math.Clamp((int)MathF.Ceiling(wide / Step), 1, MostSquares);
            _down = Math.Clamp((int)MathF.Ceiling(tall / Step), 1, MostSquares);
            _stepX = wide / _across;
            _stepY = tall / _down;

            // Counted, summed, then filed: two passes and no list per square.
            _starts = new int[(_across * _down) + 1];
            for (var one = 0; one + 2 < _indices.Length; one += 3)
            {
                (int fromX, int fromY, int toX, int toY) = Reach(one);
                for (int squareY = fromY; squareY <= toY; squareY++)
                {
                    for (int squareX = fromX; squareX <= toX; squareX++)
                    {
                        _starts[(squareY * _across) + squareX + 1]++;
                    }
                }
            }

            for (var square = 1; square < _starts.Length; square++)
            {
                _starts[square] += _starts[square - 1];
            }

            _filed = new int[_starts[^1]];
            int[] next = _starts[..^1];
            for (var one = 0; one + 2 < _indices.Length; one += 3)
            {
                (int fromX, int fromY, int toX, int toY) = Reach(one);
                for (int squareY = fromY; squareY <= toY; squareY++)
                {
                    for (int squareX = fromX; squareX <= toX; squareX++)
                    {
                        _filed[next[(squareY * _across) + squareX]++] = one;
                    }
                }
            }
        }

        /// <summary>How high the ground is at a point, from the first triangle over it - or null where none is.</summary>
        public float? HeightAt(Vector2 at)
        {
            int squareX = Square(at.X - _least.X, _stepX, _across);
            int squareY = Square(at.Y - _least.Y, _stepY, _down);
            if (squareX < 0 || squareY < 0)
            {
                return null;
            }

            int square = (squareY * _across) + squareX;
            for (int filed = _starts[square]; filed < _starts[square + 1]; filed++)
            {
                int one = _filed[filed];
                Vector3 a = _points[_indices[one]];
                Vector3 b = _points[_indices[one + 1]];
                Vector3 c = _points[_indices[one + 2]];
                float d = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
                if (MathF.Abs(d) < 1e-6f)
                {
                    continue;
                }

                float wa = (((b.Y - c.Y) * (at.X - c.X)) + ((c.X - b.X) * (at.Y - c.Y))) / d;
                float wb = (((c.Y - a.Y) * (at.X - c.X)) + ((a.X - c.X) * (at.Y - c.Y))) / d;
                float wc = 1f - wa - wb;
                if (wa >= -1e-4f && wb >= -1e-4f && wc >= -1e-4f)
                {
                    return (wa * a.Z) + (wb * b.Z) + (wc * c.Z);
                }
            }

            return null;
        }

        /// <summary>The squares a triangle's box reaches, a hair wider so a point on its edge finds it.</summary>
        private (int FromX, int FromY, int ToX, int ToY) Reach(int one)
        {
            Vector3 a = _points[_indices[one]];
            Vector3 b = _points[_indices[one + 1]];
            Vector3 c = _points[_indices[one + 2]];
            const float hair = 1e-2f;
            float leastX = MathF.Min(a.X, MathF.Min(b.X, c.X)) - _least.X - hair;
            float mostX = MathF.Max(a.X, MathF.Max(b.X, c.X)) - _least.X + hair;
            float leastY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - _least.Y - hair;
            float mostY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - _least.Y + hair;
            return (
                Math.Clamp((int)MathF.Floor(leastX / _stepX), 0, _across - 1),
                Math.Clamp((int)MathF.Floor(leastY / _stepY), 0, _down - 1),
                Math.Clamp((int)MathF.Floor(mostX / _stepX), 0, _across - 1),
                Math.Clamp((int)MathF.Floor(mostY / _stepY), 0, _down - 1));
        }

        /// <summary>Which square an offset falls in, the far edge counted in the last - or -1 outside the ground.</summary>
        private static int Square(float offset, float step, int count)
        {
            if (!float.IsFinite(offset) || offset < -1e-2f)
            {
                return -1;
            }

            int square = (int)MathF.Floor(offset / step);
            return square < count ? Math.Max(square, 0) : offset <= (count * step) + 1e-2f ? count - 1 : -1;
        }
    }

    /// <summary>
    /// What of a laid room is left out unseen: what the game's camera cannot see from anywhere the player can stand, and what lies further under the area's ground than the cut.
    /// </summary>
    /// <remarks>
    /// ASKED FOR FROM THE LIVE CLIENT, over Azmerian Ranges: a room there is large enough that drawing
    /// all of it takes thousands of doodads, and much of what was drawn lay under the ground the game
    /// shows - rock bodies, cliff feet, doodads sunk past the surface. None of it reaches the game's
    /// screen, and all of it cost loading, drawing, lighting and the sun's shadow map.
    ///
    /// IN TWO STEPS, the first before a doodad is read. The tiles are laid first, so the camera's
    /// views are drawn from their solid shapes alone and every doodad's place is asked against them -
    /// see RoomModels.Lay and CameraSight.BoxSeen - and a doodad file none of whose places can be seen
    /// is never loaded. Then the whole room, doodads and all, is drawn into the views again, every
    /// triangle asked, and the mesh rebuilt from what is seen - which is what the picture, its light
    /// and its shadows are then worked out from.
    ///
    /// THE MARGIN IS A TILE, because before a doodad is read nothing says how big it is: a place
    /// counts as seen where anything within a tile of it - times the doodad's scale - can be. A doodad
    /// standing further than that from its own origin, every place of it under ground the tiles show,
    /// is the one this can lose. A file that is read anyway has every place asked again with its own box.
    ///
    /// THE CUT COUNTS FROM THE AREA'S OWN GROUND under each vertex, the terrain heights a laid room is
    /// set on, and down is plus z. A triangle goes where all three corners lie further under it than
    /// the cut; a doodad before it is read where the top of its margin's box does, under the deepest
    /// ground at its box's corners and middle.
    ///
    /// A SHADOW-ONLY CASTER IS KEPT UNLESS IT IS UNDER THE CUT: it is never seen, and casting where
    /// it is not seen is what it is for. Anything else hidden from every view casts no shadow here
    /// any more - something the camera can never see, that the sun would throw a shadow from onto
    /// ground the camera does see, is the one shadow this can lose; the switch puts it back.
    /// </remarks>
    private sealed class Hiding
    {
        /// <summary>How far round a doodad's place the box reaches before the doodad is read, in world units, times its scale.</summary>
        private const float Margin = TileModels.Side;

        private readonly TerrainGrid _grid;
        private readonly Vector2 _origin;
        private readonly CameraSight? _sight;
        private readonly bool _asked;
        private readonly int _cut;
        private readonly string _why;
        private readonly ModelProgress? _progress;
        private long _took;
        private int _total;
        private int _hidden;
        private int _below;

        private Hiding(TerrainGrid grid, Vector2 origin, CameraSight? sight, bool asked, int cut, string why, long took, ModelProgress? progress)
        {
            _progress = progress;
            _grid = grid;
            _origin = origin;
            _sight = sight;
            _asked = asked;
            _cut = Math.Max(0, cut);
            _why = why;
            _took = took;
            Sieve = sight is null && _cut == 0 ? null : new DoodadSieve(Kept, Margin);
        }

        /// <summary>What asks each doodad before it is laid, or null where nothing is left out.</summary>
        public DoodadSieve? Sieve { get; }

        /// <summary>
        /// The camera's views drawn from what the pile holds so far - the tiles - or nothing where neither is asked for.
        /// </summary>
        /// <param name="pile">The tiles laid.</param>
        /// <param name="grid">The area.</param>
        /// <param name="origin">Where the room's corner lies in the world.</param>
        /// <param name="size">How far the room reaches from it, x and y.</param>
        /// <param name="camera">The game's camera, or null.</param>
        /// <param name="sight">Whether what it cannot see is left out.</param>
        /// <param name="cut">How far under the ground everything is left out; nought for nothing.</param>
        /// <param name="progress">Where the build says how far it has got, or null.</param>
        public static Hiding Of(
            ModelPile pile, TerrainGrid grid, Vector2 origin, Vector2 size, CameraShot? camera, bool sight, int cut, ModelProgress? progress)
        {
            if (!sight)
            {
                return new Hiding(grid, origin, null, asked: false, cut, string.Empty, 0, progress);
            }

            using ModelProgress.Step step = ModelProgress.Begin(progress, "finding what the game's camera sees", 1);
            long started = Environment.TickCount64;
            CameraSight? seeing = CameraSight.Over(camera, grid, origin, Vector3.Zero, new Vector3(size, 0f), out string why);
            if (seeing is not null)
            {
                (Vector3[] places, int[] indices) = pile.Solid();
                seeing.See(places, indices, Environment.ProcessorCount);
            }

            step.Advance();
            return new Hiding(grid, origin, seeing, asked: true, cut, why, Environment.TickCount64 - started, progress);
        }

        /// <summary>
        /// The room's mesh with only what is kept - see the remarks - and the counts for the line under the picture.
        /// </summary>
        public SkinnedMesh Keep(SkinnedMesh joined, ModelPile pile)
        {
            if (!joined.Ready || (_sight is null && _cut == 0))
            {
                return joined;
            }

            using ModelProgress.Step step = ModelProgress.Begin(_progress, _sight is null ? "cutting under the ground" : "leaving out what the game's camera cannot see", 1);
            long started = Environment.TickCount64;
            int triangles = joined.Triangles;
            MaterialBlend[] blends = pile.Blends(joined);
            bool[]? below = _cut > 0 ? Below(joined) : null;
            var hides = new bool[triangles];
            var kept = new bool[triangles];
            for (var t = 0; t < triangles; t++)
            {
                bool gone = below is not null && below[t];
                hides[t] = !gone && blends[t] == MaterialBlend.Opaque;
                kept[t] = !gone && (blends[t] == MaterialBlend.ShadowOnly || _sight is null);
            }

            bool[] keep = _sight is null ? kept : _sight.Seen(joined.Positions, joined.Indices, hides, kept, Environment.ProcessorCount);
            int left = 0, under = 0;
            for (var t = 0; t < triangles; t++)
            {
                if (below is not null && below[t])
                {
                    keep[t] = false;
                    under++;
                }
                else if (!keep[t])
                {
                    left++;
                }
            }

            _total = triangles;
            _hidden = left;
            _below = under;
            _took += Environment.TickCount64 - started;
            step.Advance();
            return joined.Keeping(keep);
        }

        /// <summary>The lines under the picture: how much was left out, and why nothing was where it was asked for and could not be.</summary>
        public IEnumerable<string> Said()
        {
            if (_asked)
            {
                yield return _sight is null
                    ? $"nothing hidden left out: {_why}"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"hidden from the game's camera: {_hidden} of {_total} triangles left out - its view from {_sight.Views} places the player can stand within its reach, {_sight.Step:0} units apart; {_took / 1000.0:0.0} s");
            }

            if (_cut > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"{_below} triangles further than {_cut} units under the area's ground left out");
            }
        }

        /// <summary>Whether anything in a box may be seen and lies above the cut.</summary>
        private bool Kept(Vector3 least, Vector3 most)
        {
            if (_cut > 0)
            {
                // DOWN IS PLUS Z, so the box's top is its least z, and the deepest ground its most.
                float deepest = MathF.Max(
                    MathF.Max(Ground(least.X, least.Y), Ground(most.X, least.Y)),
                    MathF.Max(MathF.Max(Ground(least.X, most.Y), Ground(most.X, most.Y)), Ground((least.X + most.X) * 0.5f, (least.Y + most.Y) * 0.5f)));
                if (least.Z > deepest + _cut)
                {
                    return false;
                }
            }

            return _sight is null || _sight.BoxSeen(least, most);
        }

        /// <summary>Per triangle, whether all three corners lie further under the area's ground than the cut.</summary>
        private bool[] Below(SkinnedMesh mesh)
        {
            Vector3[] places = mesh.Positions;
            var under = new bool[places.Length];
            Parallel.For(0, places.Length, one =>
            {
                Vector3 place = places[one];
                under[one] = place.Z > Ground(place.X, place.Y) + _cut;
            });

            int[] indices = mesh.Indices;
            var below = new bool[mesh.Triangles];
            for (var t = 0; t < below.Length; t++)
            {
                below[t] = under[indices[t * 3]] && under[indices[(t * 3) + 1]] && under[indices[(t * 3) + 2]];
            }

            return below;
        }

        /// <summary>The area's ground under a point of the room's own frame.</summary>
        private float Ground(float x, float y)
            => _grid.HeightAt((int)MathF.Floor((x + _origin.X) / RoomModels.CellSize), (int)MathF.Floor((y + _origin.Y) / RoomModels.CellSize));
    }
}
