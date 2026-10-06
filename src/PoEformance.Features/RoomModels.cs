using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// A room's doodads, each drawn from its own .ao and put where the room file says.
/// </summary>
/// <remarks>
/// THE PART OF A ROOM THAT CAN BE DRAWN EXACTLY. Its tiles are chosen when the area is generated -
/// see RoomLayout - but its doodads are named outright: a model, a position, an angle, a scale. Each
/// model is the same .ao walk a monster or an item takes, read ONCE however many times the room
/// places it, and joined into one mesh at each place.
///
/// A POSITION COUNTS CELLS, 23 to a tile. The file does not say so in words; the reference does in
/// its types: poe_data_tools' <c>arm</c> parser reads a doodad's x and y as <c>u32</c> and a decal's
/// as <c>f32</c>, and a whole number is a place on the grid every .tdt's walkability blocks are
/// written in, not a length. The first real room agreed - read as world units its furniture piled
/// onto a fifth of one tile, read as cells it filled the room. <see cref="Spread"/> still reports how
/// far the doodads reach against the room's size in cells, so a room that breaks the rule says so.
///
/// NOTHING HERE IS ANIMATED - a doodad's rig, where it has one, stays in its rest pose - and there
/// are caps, because a room of three hundred trees is three hundred copies of a tree to rasterise.
/// </remarks>
public static class RoomModels
{
    /// <summary>Most doodads placed. Beyond it the rest are counted and left out.</summary>
    public const int MostDoodads = 400;

    /// <summary>Most triangles placed. The renderer is on the processor; past this, turning stutters.</summary>
    public const int MostTriangles = 400_000;

    /// <summary>Cells to a tile - the grid a room's doodad positions and a tile's walkability are written in.</summary>
    public const int CellsPerTile = 23;

    /// <summary>World units in one cell.</summary>
    public static float CellSize => TileModels.Side / CellsPerTile;

    /// <summary>
    /// How far a room's doodads reach, and what that says about the unit.
    /// </summary>
    /// <param name="Most">The largest x and y any doodad has.</param>
    /// <param name="Cells">The room's size in cells - 23 to a tile.</param>
    /// <param name="NotCells">True when a doodad lies past the room's edge read as cells - a room the rule does not hold for.</param>
    public readonly record struct Spread(Vector2 Most, Vector2 Cells, bool NotCells)
    {
        /// <summary>The line the view prints.</summary>
        public string Say => string.Create(
                CultureInfo.InvariantCulture,
                $"doodads reach x {Most.X}, y {Most.Y}; the room is {Cells.X} x {Cells.Y} cells")
            + (NotCells ? " - past its edge, so this room's positions are not cells" : string.Empty);
    }

    /// <summary>How far the doodads reach against the room's size. See <see cref="Spread"/>.</summary>
    public static Spread SpreadOf(RoomLayout room)
    {
        ArgumentNullException.ThrowIfNull(room);

        var most = Vector2.Zero;
        foreach (RoomDoodad one in room.Doodads)
        {
            most = Vector2.Max(most, new Vector2(one.X, one.Y));
        }

        var cells = new Vector2(room.Width * CellsPerTile, room.Height * CellsPerTile);
        return new Spread(most, cells, most.X > cells.X || most.Y > cells.Y);
    }

    /// <summary>
    /// The room's doodads as one model, or why there is none. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The room's <c>.arm</c>.</param>
    /// <param name="shaded">Whether each material's shader graphs are read - see MonsterModels.Shaded.</param>
    public static MonsterModel Of(Func<string, byte[]?>? read, string? path, bool shaded = false)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no room to read" };
        }

        // COUNTED, as a tile's and a monster's files are: a room is the heaviest thing the book
        // opens - every distinct doodad's .ao walk, materials and textures - and the line under
        // the picture should say so rather than read as nothing.
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

        if (room.Doodads.Count == 0)
        {
            return MonsterModel.None with { Why = $"the room places no doodads: {path}", Bytes = bytes, Files = files };
        }

        // ONE LOAD PER MODEL, keyed by its file: a room places the same tree or rock dozens of times.
        // AND ONE TEXTURE CACHE FOR ALL OF THEM, because two different doodads sharing a material -
        // the rock and the stump on one sheet - are one decode and one upload, not one each.
        var models = new Dictionary<string, MonsterModel>(StringComparer.OrdinalIgnoreCase);
        var paints = new MonsterModels.Paints();
        var joins = new List<MeshJoin>();
        var skins = new List<Mipmaps?>();
        var modes = new List<string>();
        var wearing = new List<string>();
        float size = CellSize;
        var triangles = 0;
        int placed = 0, missing = 0, capped = 0;
        string firstMissing = string.Empty;

        foreach (RoomDoodad one in room.Doodads)
        {
            if (one.Ao.Length == 0)
            {
                continue;
            }

            if (!models.TryGetValue(one.Ao, out MonsterModel? model))
            {
                model = MonsterModels.OfFiles(Counted, [one.Ao], wearing: true, paints);
                models[one.Ao] = model;
            }

            if (!model.Ready)
            {
                missing++;
                if (firstMissing.Length == 0)
                {
                    firstMissing = $"{one.Ao} - {model.Why}";
                }

                continue;
            }

            if (placed >= MostDoodads || triangles + model.Mesh.Triangles > MostTriangles)
            {
                capped++;
                continue;
            }

            float scale = one.Scale > 0f ? one.Scale : 1f;
            Matrix4x4 place = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationZ(one.Turn)
                * Matrix4x4.CreateTranslation(one.X * size, one.Y * size, 0f);
            joins.Add(new MeshJoin(model.Mesh, null, null, place));

            // ONE SKIN PER SHAPE, in the order the join lays the shapes down - the model's own list,
            // or its single skin repeated where it never had a list.
            for (var shape = 0; shape < model.Mesh.Shapes.Count; shape++)
            {
                skins.Add(shape < model.Skins.Count ? model.Skins[shape] : model.Skin);
                modes.Add(shape < model.Modes.Count ? model.Modes[shape] : string.Empty);
                wearing.Add(shape < model.ShapeMaterials.Count ? model.ShapeMaterials[shape] : string.Empty);
            }

            triangles += model.Mesh.Triangles;
            placed++;
        }

        SkinnedMesh joined = SkinnedMesh.Joined(joins);
        if (!joined.Ready)
        {
            return MonsterModel.None with
            {
                Why = firstMissing.Length > 0
                    ? $"none of the room's doodads drew; the first: {firstMissing}"
                    : $"the room's doodads hold no geometry: {path}",
                Bytes = bytes,
                Files = files,
            };
        }

        var said = new List<string> { SpreadOf(room).Say };
        if (missing > 0)
        {
            said.Add(string.Create(CultureInfo.InvariantCulture, $"{missing} doodads did not draw; the first: {firstMissing}"));
        }

        if (capped > 0)
        {
            said.Add(string.Create(CultureInfo.InvariantCulture,
                $"{capped} more left out to keep it turnable - {MostDoodads} doodads or {MostTriangles} triangles"));
        }

        var laid = new MonsterModel(joined, skins.FirstOrDefault(one => one is not null), path, string.Empty, string.Empty)
        {
            Skins = skins,
            Modes = modes,
            ShapeMaterials = wearing,
            BodyLeast = joined.Least,
            BodyMost = joined.Most,
            Parts = placed,
            Kind = ModelKind.Room,
            Bytes = bytes,
            Files = files,
            Move = string.Join("; ", said),
            Shaders = [.. models.Values.SelectMany(one => one.Shaders).Distinct(StringComparer.OrdinalIgnoreCase)],
            Meshes = [.. models.Values.Where(one => one.Ready).Select(one => new MeshNamed(one.Mesh_, one.BodyFacts)).Distinct()],
        };

        // THE GRAPHS ONCE, OVER THE JOINED ROOM: every doodad's materials are in the one list, and a
        // material two doodads share is compiled once through the shared cache.
        return shaded ? MonsterModels.Shaded(Counted, laid, paints) : laid;
    }
}
