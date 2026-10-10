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
/// AND IT STANDS ON THE FLOOR THE PLAYER SEES, at the tile's middle. Not on the tile file's own
/// "ground" block: on a boss arena that block is the earth under a stone floor built of props, and
/// the first monster set on it stood a storey under the arena. The floor is the highest surface that
/// faces up under the middle, over every solid shape of the tile - props and ground alike, cut-out
/// and mixed shapes (foliage, decals, ground layers) left out - since the game's camera looks down
/// and what it sees at that point is what is stood on. The files' up is minus z. Where nothing faces
/// up under the middle the tile's lowest point stands in, and the line under the picture says so.
/// </remarks>
public static class StagedModels
{
    /// <summary>What the monster's shapes are called in the probe's list, beside the tile's own path.</summary>
    public const string ActorSource = "the monster";

    /// <summary>How far from straight up a surface may face and still be a floor: the z of its normal, the files' up being minus z.</summary>
    /// <remarks>Half, which is sixty degrees from level: a ramp or a stair tread passes, a wall or a cliff face does not.</remarks>
    private const float FacesUp = -0.5f;

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
        MonsterModel tile = TileModels.Of(
            read,
            key.Path,
            ground: key.Ground,
            walls: key.Walls,
            shaded: true,
            swaps: key.Tileset.Length > 0 ? TilesetIndex.Overrides(read, key.Tileset) : null,
            tileset: key.Tileset.Length > 0 ? TilesetIndex.Short(key.Tileset) : string.Empty,
            progress: progress);
        if (!tile.Ready)
        {
            return actor with { Stage = key.Path, StageSaid = "no stage: " + tile.Why };
        }

        return Staged(actor, tile, one?.ModelSize ?? 0, key.Path);
    }

    /// <summary>
    /// The two joined: the monster placed at the tile's middle, on its floor, at the size the game draws it.
    /// </summary>
    /// <param name="actor">The monster, ready.</param>
    /// <param name="tile">The stage, ready.</param>
    /// <param name="modelSize">The variety's ModelSizeMultiplier in percent; anything not positive is a hundred.</param>
    /// <param name="stage">The tile's path, for the lines and the probe.</param>
    public static MonsterModel Staged(MonsterModel actor, MonsterModel tile, int modelSize, string stage)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(stage);

        float scale = modelSize > 0 ? modelSize / 100f : 1f;
        Vector3 middle = (tile.Mesh.Least + tile.Mesh.Most) * 0.5f;
        bool found = Floor(tile.Mesh, tile.Blends, middle.X, middle.Y, out float floor);
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
            $"stage: {Tail(stage)} · the monster stands at its middle at {scale:0.##} of its file's size, {(found ? $"on the floor there ({floor:0} on the tile's z)" : "at the tile's lowest point - nothing faces up under the middle")}");

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

            // THE STAGE'S OWN LIGHTS, where it has any, shine on the monster too: the light is built for
            // the one model, and the monster is in it. A tile carries none; a stage that does will.
            Lights = tile.Lights,
            LightsSaid = tile.LightsSaid,
            Actor = actor,
            ActorFirst = first,
            ActorPlace = place,
            Stage = stage,
            StageSaid = said,
        };
    }

    /// <summary>
    /// How high the floor is under a point: the highest surface facing up there, over the mesh's solid shapes - or false where none does.
    /// </summary>
    /// <remarks>
    /// EVERY SOLID TRIANGLE WHOSE SHADOW COVERS THE POINT AND WHOSE NORMALS FACE UP, and the least z among
    /// them - the files' up is minus z, so the least is the highest, which is the surface the game's camera
    /// sees at that point. A shape drawn cut out or mixed is passed over: foliage, a decal, a ground layer
    /// laid over the floor are not stood on. One walk over the mesh, at load.
    /// </remarks>
    /// <param name="mesh">The tile's mesh.</param>
    /// <param name="blends">Each shape's blend, in the mesh's shape order - or null for every shape solid.</param>
    /// <param name="x">The point, in the mesh's own units.</param>
    /// <param name="y">The point, in the mesh's own units.</param>
    /// <param name="height">The floor's z there, where one was found.</param>
    public static bool Floor(SkinnedMesh? mesh, IReadOnlyList<MaterialBlend>? blends, float x, float y, out float height)
    {
        height = float.PositiveInfinity;
        if (mesh is not { Ready: true })
        {
            return false;
        }

        Vector3[] at = mesh.Positions;
        Vector3[] up = mesh.Normals;
        int[] indices = mesh.Indices;
        var found = false;
        IReadOnlyList<MeshShape> shapes = mesh.Shapes.Count > 0 ? mesh.Shapes : [new MeshShape(string.Empty, 0, indices.Length)];
        for (var which = 0; which < shapes.Count; which++)
        {
            if (blends is not null && which < blends.Count && blends[which] != MaterialBlend.Opaque)
            {
                continue;
            }

            MeshShape shape = shapes[which];
            int end = Math.Min(shape.From + shape.Count, indices.Length);
            for (int one = Math.Max(shape.From, 0); one + 2 < end; one += 3)
            {
                int ia = indices[one], ib = indices[one + 1], ic = indices[one + 2];
                Vector3 a = at[ia], b = at[ib], c = at[ic];

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

                // FACING UP by its normals rather than its winding: the winding's sense is the files' and the
                // picture lights both sides, while a vertex normal is a direction the file wrote down.
                if (ia < up.Length && ib < up.Length && ic < up.Length && (up[ia].Z + up[ib].Z + up[ic].Z) / 3f > FacesUp)
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
