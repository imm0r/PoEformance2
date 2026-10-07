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
/// TWO THINGS THE FILES DO NOT SAY ARE SETTLED BY THE GROUND. Whether a tile's mesh counts its Y the
/// way the game's sub-tile index does, or the other way, and whether its heights count the way the
/// area's do: each is tried, and every piece's ground is asked how high it is at nine points and
/// held against the height the area itself has there. A slope - a stair, a ramp - fits one way
/// only, so the way the sloped pieces fit best is the way every piece is drawn, and the line under
/// the picture says how well, and how well the others did. With no slope in the room nothing is
/// settled and the files are taken as they are. Each piece is then raised to the area's own ground,
/// the doodads with it.
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
    public static MonsterModel Of(
        Func<string, byte[]?>? read,
        string? path,
        TerrainGrid? grid,
        int x,
        int y,
        int turn,
        bool shaded = false,
        int doodads = RoomModels.UsualDoodads,
        bool tools = false)
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

        // EACH TILE FILE ONCE, however many pieces of it the room holds.
        var paints = new MonsterModels.Paints();
        var made = new Dictionary<int, Made>();
        Made Of(int id)
        {
            if (!made.TryGetValue(id, out Made? one))
            {
                (MonsterModel props, SkinnedMesh ground, int across, int down) = TileModels.Apart(Counted, tiles.Paths[id], walls: true, paints);
                one = new Made(props, ground, Math.Max(1, across), Math.Max(1, down), Middle(props, ground));
                made[id] = one;
            }

            return one;
        }

        List<TilePiece> pieces = tiles.Pieces(x, y, wide, tall, id => (Of(id).Width, Of(id).Height));

        // THE WAY THE FILES ARE SET ON THE GROUND, from the sloped pieces - see the remarks.
        var ways = new Way[4];
        for (var way = 0; way < ways.Length; way++)
        {
            ways[way] = new Way(way >= 2, (way & 1) == 1 ? -1f : 1f);
        }

        foreach (TilePiece piece in pieces)
        {
            Made one = Of(piece.Id);
            if (!piece.Whole || !one.Ground.Ready || !Way.Rises(piece, grid))
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

            long cost = (one.Props.Ready ? one.Props.Mesh.Triangles : 0) + (one.Ground.Ready ? one.Ground.Triangles : 0);
            if (pile.Triangles + cost > mostTriangles)
            {
                capped++;
                continue;
            }

            Matrix3x2 flat = chosen.Flat(piece, one, x, y);
            float lift = chosen.Lift(piece, one, grid, x, y, flat);
            Matrix4x4 place = Spatial(flat) * Matrix4x4.CreateTranslation(0f, 0f, lift);
            if (one.Props.Ready)
            {
                pile.Add(one.Props, place);
            }

            if (one.Ground.Ready)
            {
                pile.AddPlain(one.Ground, place);
            }

            used.Add(piece.Id);
            laidPieces++;
            unturned += piece.Placement < 0 ? 1 : 0;
        }

        // THE DOODADS, where the corners were found and on the area's ground under them.
        Matrix3x2 laying = RoomFinder.Laying(room.Width, room.Height, turn);
        Matrix4x4 beyondFlat = Spatial(Matrix3x2.CreateScale(1f / TileModels.Side) * laying * Matrix3x2.CreateScale(TileModels.Side));
        float sign = chosen.Sign;
        RoomModels.Doodads laid = RoomModels.Lay(room, Counted, paints, pile, doodads, tools, one =>
        {
            Vector2 at = Vector2.Transform(new Vector2(one.X, one.Y) / RoomModels.CellsPerTile, laying);
            int cellX = (int)((x + at.X) * RoomModels.CellsPerTile);
            int cellY = (int)((y + at.Y) * RoomModels.CellsPerTile);
            return beyondFlat * Matrix4x4.CreateTranslation(0f, 0f, sign * grid.HeightAt(cellX, cellY));
        });

        SkinnedMesh joined = SkinnedMesh.Joined(pile.Joins);
        if (!joined.Ready)
        {
            return MonsterModel.None with
            {
                Why = $"nothing to draw: {pieces.Count} pieces under the place, {laidPieces} laid, and {laid.Placed} doodads",
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
        };
        said.AddRange(laid.Said());

        MonsterModel model = RoomModels.Piled(joined, pile, path, made.Values.Select(one => one.Props).Concat(laid.Models)) with
        {
            Parts = laidPieces + laid.Placed,
            Bytes = bytes,
            Files = files,
            Move = string.Join("; ", said),
        };

        return shaded ? MonsterModels.Shaded(Counted, model, paints) : model;
    }

    /// <summary>What the ground said about each way of setting the files on it, for the line under the picture.</summary>
    private static string Heights(Way[] ways, Way chosen, bool settled)
    {
        string Each(Way one) => string.Create(CultureInfo.InvariantCulture,
            $"{one.Said} {one.Fitting}/{one.Sloped}");
        string all = string.Join(", ", ways.Select(Each));
        return settled
            ? string.Create(CultureInfo.InvariantCulture,
                $"heights: drawn {chosen.Said} - sloped pieces whose ground meets the area's within {Fits} units, each way: {all}")
            : "heights: no sloped piece to settle how the files' ground counts - drawn as the files have it, each piece raised to the area's ground";
    }

    /// <summary>The middle of a tile file's ground, in its own frame - or of its props where it has no ground.</summary>
    private static Vector2 Middle(MonsterModel props, SkinnedMesh ground)
    {
        if (ground.Ready)
        {
            return new Vector2((ground.Least.X + ground.Most.X) / 2f, (ground.Least.Y + ground.Most.Y) / 2f);
        }

        return props.Ready
            ? new Vector2((props.Mesh.Least.X + props.Mesh.Most.X) / 2f, (props.Mesh.Least.Y + props.Mesh.Most.Y) / 2f)
            : Vector2.Zero;
    }

    /// <summary>A flat placement as a placement in space, the height left alone.</summary>
    private static Matrix4x4 Spatial(Matrix3x2 flat) => new(
        flat.M11, flat.M12, 0f, 0f,
        flat.M21, flat.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        flat.M31, flat.M32, 0f, 1f);

    /// <summary>How high a mesh is at a point, from the first triangle over it - or null where none is.</summary>
    private static float? HeightOf(SkinnedMesh mesh, Vector2 at)
    {
        Vector3[] points = mesh.Positions;
        int[] indices = mesh.Indices;
        for (var one = 0; one + 2 < indices.Length; one += 3)
        {
            Vector3 a = points[indices[one]];
            Vector3 b = points[indices[one + 1]];
            Vector3 c = points[indices[one + 2]];
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

    /// <summary>One tile file, made once: its props, its ground, its size in tiles, and the middle of its ground.</summary>
    private sealed record Made(MonsterModel Props, SkinnedMesh Ground, int Width, int Height, Vector2 Middle);

    /// <summary>
    /// One way of setting the files on the ground - their Y as it is or turned over, their heights counting as the area's or the other way - and how well the sloped pieces fit it.
    /// </summary>
    private sealed class Way(bool overturned, float sign)
    {
        /// <summary>Whether the file's Y is turned over before the game's turn.</summary>
        public bool Overturned { get; } = overturned;

        /// <summary>How the file's heights count against the area's: 1 the same way, -1 the other.</summary>
        public float Sign { get; } = sign;

        /// <summary>Sloped pieces weighed.</summary>
        public int Sloped { get; private set; }

        /// <summary>Of those, the ones whose ground met the area's within <see cref="Fits"/>.</summary>
        public int Fitting { get; private set; }

        /// <summary>The squared misses over every sloped piece, after each was raised to fit as well as it can.</summary>
        public double Residual { get; private set; }

        /// <summary>This way in words.</summary>
        public string Said => (Overturned ? "Y turned over" : "Y as filed") + (Sign > 0 ? ", heights as the area's" : ", heights the other way");

        /// <summary>
        /// Where a piece's file goes, flat: its ground's middle to the origin, its Y turned over where this way says, the game's turn, then out to the middle of the area tiles it covers - from the room's corner, in world units.
        /// </summary>
        public Matrix3x2 Flat(TilePiece piece, Made one, int x, int y)
        {
            Matrix3x2 flat = Matrix3x2.CreateTranslation(-one.Middle);
            if (Overturned)
            {
                flat *= Matrix3x2.CreateScale(1f, -1f);
            }

            if (TileModels.Turned(TileOrientation.OfPlacement(piece.Placement)) is { } turned)
            {
                flat *= new Matrix3x2(turned.M11, turned.M12, turned.M21, turned.M22, 0f, 0f);
            }

            var middle = new Vector2(
                (((piece.MinX + piece.MaxX + 1) / 2f) - x) * TileModels.Side,
                (((piece.MinY + piece.MaxY + 1) / 2f) - y) * TileModels.Side);
            return flat * Matrix3x2.CreateTranslation(middle);
        }

        /// <summary>How far to raise a piece so its ground meets the area's on average, in the files' count.</summary>
        public float Lift(TilePiece piece, Made one, TerrainGrid grid, int x, int y, Matrix3x2 flat)
        {
            if (!one.Ground.Ready || !Matrix3x2.Invert(flat, out Matrix3x2 back))
            {
                return Sign * grid.HeightAt((((piece.MinX + piece.MaxX + 1) * RoomModels.CellsPerTile) / 2), (((piece.MinY + piece.MaxY + 1) * RoomModels.CellsPerTile) / 2));
            }

            double sum = 0;
            int count = 0;
            foreach ((float file, float area) in Samples(piece, one, grid, x, y, back))
            {
                sum += (Sign * area) - file;
                count++;
            }

            return count > 0 ? (float)(sum / count) : Sign * grid.HeightAt(piece.MinX * RoomModels.CellsPerTile, piece.MinY * RoomModels.CellsPerTile);
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
            Matrix3x2 flat = Flat(piece, one, x, y);
            if (!Matrix3x2.Invert(flat, out Matrix3x2 back))
            {
                return;
            }

            var pairs = Samples(piece, one, grid, x, y, back).ToList();
            if (pairs.Count < 3)
            {
                return;
            }

            double lift = pairs.Average(pair => (Sign * pair.Area) - pair.File);
            double squared = pairs.Sum(pair => Math.Pow((Sign * pair.Area) - pair.File - lift, 2));
            Sloped++;
            Residual += squared;
            Fitting += Math.Sqrt(squared / pairs.Count) <= Fits ? 1 : 0;
        }

        /// <summary>The file's ground height and the area's at the points asked, in every tile of the piece.</summary>
        private static IEnumerable<(float File, float Area)> Samples(TilePiece piece, Made one, TerrainGrid grid, int x, int y, Matrix3x2 back)
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
                            if (HeightOf(one.Ground, Vector2.Transform(world, back)) is { } file)
                            {
                                yield return (file, grid.HeightAt((tileX * RoomModels.CellsPerTile) + across, (tileY * RoomModels.CellsPerTile) + down));
                            }
                        }
                    }
                }
            }
        }
    }
}
