using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Meshes laid down into one model, every shape keeping its own paint - what a room is built from.
/// </summary>
/// <remarks>
/// ONE ENTRY PER SHAPE, in the order the join lays the shapes down: a placed model's own lists, or its
/// single skin repeated where it never had a list; a plain mesh - a tile's ground - nothing at all,
/// which is how the picture knows to leave it unpainted.
/// </remarks>
internal sealed class ModelPile
{
    public List<MeshJoin> Joins { get; } = [];

    public List<Mipmaps?> Skins { get; } = [];

    public List<string> Modes { get; } = [];

    public List<string> Wearing { get; } = [];

    public List<string> Textures { get; } = [];

    /// <summary>What each shape came from - see MonsterModel.ShapeSources.</summary>
    public List<string> Sources { get; } = [];

    /// <summary>Triangles laid so far.</summary>
    public long Triangles { get; private set; }

    /// <summary>A painted model, placed.</summary>
    /// <param name="model">The model.</param>
    /// <param name="place">Where it goes.</param>
    /// <param name="source">What it came from, for every one of its shapes.</param>
    public void Add(MonsterModel model, Matrix4x4 place, string source)
    {
        Joins.Add(new MeshJoin(model.Mesh, null, null, place));
        for (var shape = 0; shape < model.Mesh.Shapes.Count; shape++)
        {
            Skins.Add(shape < model.Skins.Count ? model.Skins[shape] : model.Skin);
            Modes.Add(shape < model.Modes.Count ? model.Modes[shape] : string.Empty);
            Wearing.Add(shape < model.ShapeMaterials.Count ? model.ShapeMaterials[shape] : string.Empty);
            Textures.Add(shape < model.ShapeTextures.Count ? model.ShapeTextures[shape] : string.Empty);
            Sources.Add(source);
        }

        Triangles += model.Mesh.Triangles;
    }

    /// <summary>An unpainted mesh, placed.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="place">Where it goes.</param>
    /// <param name="source">What it came from, for every one of its shapes.</param>
    public void AddPlain(SkinnedMesh mesh, Matrix4x4 place, string source)
    {
        Joins.Add(new MeshJoin(mesh, null, null, place));
        for (var shape = 0; shape < mesh.Shapes.Count; shape++)
        {
            Skins.Add(null);
            Modes.Add(string.Empty);
            Wearing.Add(string.Empty);
            Textures.Add(string.Empty);
            Sources.Add(source);
        }

        Triangles += mesh.Triangles;
    }

    /// <summary>
    /// The solid triangles laid so far, each vertex where its piece puts it - what hides things from the game's camera before the rest is loaded.
    /// </summary>
    /// <remarks>
    /// NOT A JOIN: only the places and the solid shapes' indices, and a piece with no solid shape is not
    /// so much as moved - normals, coordinates and colours are not needed to hide anything.
    /// </remarks>
    public (Vector3[] Places, int[] Indices) Solid()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        var shape = 0;
        foreach (MeshJoin join in Joins)
        {
            SkinnedMesh mesh = join.Mesh!;
            int first = shape;
            shape += mesh.Shapes.Count;
            int solid = 0;
            for (var one = 0; one < mesh.Shapes.Count; one++)
            {
                solid += MaterialBlends.Of(Modes[first + one]) == MaterialBlend.Opaque ? 1 : 0;
            }

            if (solid == 0)
            {
                continue;
            }

            int start = places.Count;
            Matrix4x4 place = join.Place ?? Matrix4x4.Identity;
            foreach (Vector3 vertex in mesh.Positions)
            {
                places.Add(Vector3.Transform(vertex, place));
            }

            for (var one = 0; one < mesh.Shapes.Count; one++)
            {
                if (MaterialBlends.Of(Modes[first + one]) != MaterialBlend.Opaque)
                {
                    continue;
                }

                MeshShape part = mesh.Shapes[one];
                for (int at = part.From, end = Math.Min(part.From + part.Count, mesh.Indices.Length); at < end; at++)
                {
                    indices.Add(mesh.Indices[at] + start);
                }
            }
        }

        return ([.. places], [.. indices]);
    }

    /// <summary>
    /// Every triangle's blend in a mesh joined from this pile, by the shape it is in - opaque outside every shape, as the picture has it.
    /// </summary>
    public MaterialBlend[] Blends(SkinnedMesh joined)
    {
        ArgumentNullException.ThrowIfNull(joined);
        var blends = new MaterialBlend[joined.Triangles];
        for (var one = 0; one < joined.Shapes.Count && one < Modes.Count; one++)
        {
            MaterialBlend blend = MaterialBlends.Of(Modes[one]);
            if (blend == MaterialBlend.Opaque)
            {
                continue;
            }

            MeshShape shape = joined.Shapes[one];
            for (int t = shape.From / 3, end = Math.Min((shape.From + shape.Count) / 3, blends.Length); t < end; t++)
            {
                blends[t] = blend;
            }
        }

        return blends;
    }
}
