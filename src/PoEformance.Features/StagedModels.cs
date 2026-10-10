using System.Globalization;
using System.Numerics;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// A monster standing in the middle of a terrain tile - its arena, or any tile the book is pointed at - as one model.
/// </summary>
/// <remarks>
/// ONE MESH, SO THE RENDERER NEED NOT KNOW. The picture draws a mesh and, for an animation, an
/// array of where each vertex is now; a room is already a mesh joined out of many. So the stage
/// and the monster are joined the way a room's doodads are - the tile first, the monster after it
/// at its place - and what moves is the monster's slice of the posed arrays alone: the portrait
/// poses the monster's own mesh and writes the result, placed, over its vertices, while the tile's
/// keep the positions the file gave them. Blends, programs, skins and the probe's sources are per
/// shape and come along in the join, on the processor and on the card alike.
///
/// THE MONSTER IS SCALED THE WAY THE GAME SCALES IT: a variety's ModelSizeMultiplier is a
/// percentage the game draws the mesh at, on the same tiles, so on a stage counted in world units
/// the mesh is that much larger or smaller - the same number ModelFloor.TileOn shrinks the floor's
/// squares by under a bare picture.
///
/// AND IT STANDS ON THE GROUND, at the tile's middle: the tile's ground is asked how high it is
/// there - the highest of its surfaces under that point, since the files' up is minus z - and the
/// monster's lowest point in its bind pose is set on it. Where no ground lies under the middle the
/// tile's lowest point stands in, and the line under the picture says so.
/// </remarks>
public static class StagedModels
{
    /// <summary>What the monster's shapes are called in the probe's list, beside the tile's own path.</summary>
    public const string ActorSource = "the monster";

    /// <summary>
    /// The monster on its stage - or the monster alone where there is no stage to stand on, with
    /// <see cref="MonsterModel.StageSaid"/> saying why. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="one">The monster.</param>
    /// <param name="stage">The stage's tile as a <see cref="TileKey"/> string, or empty for the monster alone.</param>
    /// <param name="wearing">Whether the monster's attachments are joined on - see MonsterModels.Of.</param>
    /// <param name="shaded">Whether the monster's material graphs are read; the stage's always are.</param>
    /// <param name="progress">Where the build says how far it has got, or null.</param>
    public static MonsterModel Of(
        Func<string, byte[]?>? read,
        MonsterVariety? one,
        string stage,
        bool wearing = true,
        bool shaded = false,
        ModelProgress? progress = null)
    {
        MonsterModel actor = MonsterModels.Of(read, one, wearing, shaded, progress);
        if (string.IsNullOrWhiteSpace(stage) || read is null || !actor.Ready)
        {
            return actor;
        }

        TileKey key = TileKey.Read(stage);
        (MonsterModel tile, SkinnedMesh ground) = TileModels.Stage(
            read,
            key.Path,
            key.Walls,
            shaded: true,
            key.Tileset.Length > 0 ? TilesetIndex.Overrides(read, key.Tileset) : null,
            key.Tileset.Length > 0 ? TilesetIndex.Short(key.Tileset) : string.Empty,
            progress);
        if (!tile.Ready)
        {
            return actor with { Stage = key.Path, StageSaid = "no stage: " + tile.Why };
        }

        return Staged(actor, tile, ground, one?.ModelSize ?? 0, key.Path);
    }

    /// <summary>
    /// The two joined: the monster placed at the tile's middle, on its ground, at the size the game draws it.
    /// </summary>
    /// <param name="actor">The monster, ready.</param>
    /// <param name="tile">The stage, ready.</param>
    /// <param name="ground">The stage's ground apart, to ask how high it is - or none, which puts the monster at the tile's lowest point.</param>
    /// <param name="modelSize">The variety's ModelSizeMultiplier in percent; anything not positive is a hundred.</param>
    /// <param name="stage">The tile's path, for the lines and the probe.</param>
    public static MonsterModel Staged(MonsterModel actor, MonsterModel tile, SkinnedMesh? ground, int modelSize, string stage)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(stage);

        float scale = modelSize > 0 ? modelSize / 100f : 1f;
        Vector3 middle = (tile.Mesh.Least + tile.Mesh.Most) * 0.5f;
        bool found = Floor(ground, middle.X, middle.Y, out float floor);
        if (!found)
        {
            floor = tile.Mesh.Most.Z;
        }

        // SCALED ABOUT ITS OWN ORIGIN AND THEN MOVED, as row vectors multiply: the monster's feet - its
        // greatest z, the files' up being minus z - land on the floor.
        Matrix4x4 place = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateTranslation(middle.X, middle.Y, floor - (actor.Mesh.Most.Z * scale));

        var pile = new ModelPile();
        pile.Add(tile, Matrix4x4.Identity, stage);
        int first = tile.Mesh.Positions.Length;
        pile.Add(actor, place, ActorSource);
        SkinnedMesh joined = SkinnedMesh.Joined(pile.Joins);

        string said = string.Create(
            CultureInfo.InvariantCulture,
            $"stage: {Tail(stage)} · the monster stands at its middle at {scale:0.##} of its file's size, {(found ? "on the ground there" : "at the tile's lowest point - no ground lies under the middle")}");

        return RoomModels.Piled(joined, pile, actor.Mesh_, [tile, actor]) with
        {
            // THE MONSTER'S OWN ACCOUNT OF ITSELF, kept whole: the lines under the picture, the dump and
            // the sweep read these, and a stage changes where he stands, not what he is.
            Kind = ModelKind.Ao,
            Material = actor.Material,
            Paint = actor.Paint,
            Fitted = actor.Fitted,
            BodyLeast = Vector3.Transform(actor.BodyLeast, place),
            BodyMost = Vector3.Transform(actor.BodyMost, place),
            BodyFacts = actor.BodyFacts,
            NamedInAo = actor.NamedInAo,
            NamedInMesh = actor.NamedInMesh,
            Runs = actor.Runs,
            Guessed = actor.Guessed,
            Rig = actor.Rig,
            Rig_ = actor.Rig_,
            Move = actor.Move,
            Parts = actor.Parts,
            Sections = actor.Sections,
            Materials = Union(tile.Materials, actor.Materials),
            Textures = Union(tile.Textures, actor.Textures),
            Unshaded = Union(tile.Unshaded, actor.Unshaded),
            Clocked = Union(tile.Clocked, actor.Clocked),
            Shades = Shades(tile, actor, gloss: false),
            GlossShades = Shades(tile, actor, gloss: true),
            ShadedBy = tile.ShadedBy + actor.ShadedBy,
            Bytes = tile.Bytes + actor.Bytes,
            Files = tile.Files + actor.Files,
            Actor = actor,
            ActorFirst = first,
            ActorPlace = place,
            Stage = stage,
            StageSaid = said,
        };
    }

    /// <summary>
    /// How high the ground is under a point: the highest of its surfaces there, or false where none lies under it.
    /// </summary>
    /// <remarks>
    /// EVERY TRIANGLE WHOSE SHADOW COVERS THE POINT, and the least z among them - the files' up is
    /// minus z, so the least is the highest, which on a ground that is a slab with sides is its top.
    /// A walk over every triangle once, at load, over a ground of a few thousand of them.
    /// </remarks>
    public static bool Floor(SkinnedMesh? ground, float x, float y, out float height)
    {
        height = float.PositiveInfinity;
        if (ground is not { Ready: true })
        {
            return false;
        }

        Vector3[] at = ground.Positions;
        int[] indices = ground.Indices;
        var found = false;
        for (var one = 0; one + 2 < indices.Length; one += 3)
        {
            Vector3 a = at[indices[one]];
            Vector3 b = at[indices[one + 1]];
            Vector3 c = at[indices[one + 2]];

            // Barycentric in the plane, with a tolerance a little past the edge so a point on a seam
            // between two triangles is under one of them rather than between both.
            float area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
            if (MathF.Abs(area) < 1e-6f)
            {
                continue;
            }

            float u = (((b.X - x) * (c.Y - y)) - ((c.X - x) * (b.Y - y))) / area;
            float v = (((c.X - x) * (a.Y - y)) - ((a.X - x) * (c.Y - y))) / area;
            float w = 1f - u - v;
            const float Edge = -1e-4f;
            if (u < Edge || v < Edge || w < Edge)
            {
                continue;
            }

            float z = (u * a.Z) + (v * b.Z) + (w * c.Z);
            if (z < height)
            {
                height = z;
                found = true;
            }
        }

        return found;
    }

    /// <summary>The two models' programs as one list over the joined shapes, each shape keeping its own or none.</summary>
    /// <remarks>
    /// EMPTY ONLY WHERE BOTH ARE, so a stage drawn from its graphs beside a monster drawn from his
    /// texture still has a list the picture can index by shape. For the gloss list a model with no
    /// gloss of its own lends its flat programs, which is what the model's own lists promise - see
    /// MonsterModel.GlossShades.
    /// </remarks>
    private static IReadOnlyList<ShadeProgram?> Shades(MonsterModel tile, MonsterModel actor, bool gloss)
    {
        IReadOnlyList<ShadeProgram?> first = gloss && tile.GlossShades.Count > 0 ? tile.GlossShades : tile.Shades;
        IReadOnlyList<ShadeProgram?> second = gloss && actor.GlossShades.Count > 0 ? actor.GlossShades : actor.Shades;
        if (first.Count == 0 && second.Count == 0)
        {
            return [];
        }

        int tiles = tile.Mesh.Shapes.Count;
        var all = new ShadeProgram?[tiles + actor.Mesh.Shapes.Count];
        for (var one = 0; one < tiles; one++)
        {
            all[one] = one < first.Count ? first[one] : null;
        }

        for (var one = tiles; one < all.Length; one++)
        {
            all[one] = one - tiles < second.Count ? second[one - tiles] : null;
        }

        return all;
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second)
        => first.Count == 0 ? second : second.Count == 0 ? first : [.. first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string Tail(string path) => path[(path.LastIndexOf('/') + 1)..];
}
