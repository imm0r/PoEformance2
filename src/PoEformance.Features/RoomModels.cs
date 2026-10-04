using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>What a room's doodad positions are counted in. See <see cref="RoomModels"/>.</summary>
public enum RoomUnit
{
    /// <summary>The 23 cells of a tile - 250 / 23 world units each.</summary>
    Cells,

    /// <summary>World units - 250 to a tile.</summary>
    World,
}

/// <summary>
/// A room's doodads, each drawn from its own .ao and put where the room file says.
/// </summary>
/// <remarks>
/// THE PART OF A ROOM THAT CAN BE DRAWN EXACTLY. Its tiles are chosen when the area is generated -
/// see RoomLayout - but its doodads are named outright: a model, a position, an angle, a scale. Each
/// model is the same .ao walk a monster or an item takes, read ONCE however many times the room
/// places it, and joined into one mesh at each place.
///
/// THE UNIT OF A POSITION IS NOT KNOWN, and the view says so rather than choosing in silence. Cells
/// is the default - 23 to a tile, which is the grid every .tdt's walkability blocks are written in -
/// and World is the other reading. <see cref="Spread"/> reports how far the doodads reach against
/// what the room's own size allows under each, so the first real room settles it: a doodad past the
/// room's edge in cells is a file counted in world units.
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

    /// <summary>World units in one of the given unit.</summary>
    public static float Size(RoomUnit unit) => unit == RoomUnit.World ? 1f : TileModels.Side / 23f;

    /// <summary>
    /// How far a room's doodads reach, and what that says about the unit.
    /// </summary>
    /// <param name="Most">The largest x and y any doodad has.</param>
    /// <param name="Cells">The room's size in cells - 23 to a tile.</param>
    /// <param name="NotCells">True when a doodad lies past the room's edge if the positions are cells.</param>
    public readonly record struct Spread(Vector2 Most, Vector2 Cells, bool NotCells)
    {
        /// <summary>The line the view prints.</summary>
        public string Say => string.Create(
                CultureInfo.InvariantCulture,
                $"doodads reach x {Most.X}, y {Most.Y}; the room is {Cells.X} x {Cells.Y} cells")
            + (NotCells ? " - past its edge as cells, so the file counts in world units" : string.Empty);
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

        var cells = new Vector2(room.Width * 23, room.Height * 23);
        return new Spread(most, cells, most.X > cells.X || most.Y > cells.Y);
    }

    /// <summary>
    /// The room's doodads as one model, or why there is none. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The room's <c>.arm</c>.</param>
    /// <param name="unit">What the positions are counted in.</param>
    public static MonsterModel Of(Func<string, byte[]?>? read, string? path, RoomUnit unit)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no room to read" };
        }

        RoomLayout room = RoomLayout.Read(read(path.Replace('\\', '/').Trim()));
        if (!room.Ready)
        {
            return MonsterModel.None with { Why = $"the room did not read: {path} - {room.Why}" };
        }

        if (room.Doodads.Count == 0)
        {
            return MonsterModel.None with { Why = $"the room places no doodads: {path}" };
        }

        // ONE LOAD PER MODEL, keyed by its file: a room places the same tree or rock dozens of times.
        var models = new Dictionary<string, MonsterModel>(StringComparer.OrdinalIgnoreCase);
        var joins = new List<MeshJoin>();
        var skins = new List<Mipmaps?>();
        float size = Size(unit);
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
                model = MonsterModels.OfFiles(read, [one.Ao]);
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

        return new MonsterModel(joined, skins.FirstOrDefault(one => one is not null), path, string.Empty, string.Empty)
        {
            Skins = skins,
            BodyLeast = joined.Least,
            BodyMost = joined.Most,
            Parts = placed,
            Move = string.Join("; ", said),
        };
    }
}
