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

    public List<string> LastModes { get; } = [];

    public List<string> Wearing { get; } = [];

    public List<string> Textures { get; } = [];

    /// <summary>Triangles laid so far.</summary>
    public long Triangles { get; private set; }

    /// <summary>A painted model, placed.</summary>
    public void Add(MonsterModel model, Matrix4x4 place)
    {
        Joins.Add(new MeshJoin(model.Mesh, null, null, place));
        for (var shape = 0; shape < model.Mesh.Shapes.Count; shape++)
        {
            Skins.Add(shape < model.Skins.Count ? model.Skins[shape] : model.Skin);
            Modes.Add(shape < model.Modes.Count ? model.Modes[shape] : string.Empty);
            LastModes.Add(shape < model.LastModes.Count ? model.LastModes[shape] : Modes[^1]);
            Wearing.Add(shape < model.ShapeMaterials.Count ? model.ShapeMaterials[shape] : string.Empty);
            Textures.Add(shape < model.ShapeTextures.Count ? model.ShapeTextures[shape] : string.Empty);
        }

        Triangles += model.Mesh.Triangles;
    }

    /// <summary>An unpainted mesh, placed.</summary>
    public void AddPlain(SkinnedMesh mesh, Matrix4x4 place)
    {
        Joins.Add(new MeshJoin(mesh, null, null, place));
        for (var shape = 0; shape < mesh.Shapes.Count; shape++)
        {
            Skins.Add(null);
            Modes.Add(string.Empty);
            LastModes.Add(string.Empty);
            Wearing.Add(string.Empty);
            Textures.Add(string.Empty);
        }

        Triangles += mesh.Triangles;
    }
}
