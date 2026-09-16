using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// A monster's model, gathered from the five files it takes to draw one.
/// </summary>
/// <param name="Mesh">The geometry, or <see cref="SkinnedMesh.None"/> where none was found.</param>
/// <param name="Skin">The colour texture, or null to draw the mesh plain.</param>
/// <param name="Mesh_">The <c>.sm</c> that named the geometry, for the report.</param>
/// <param name="Material">The <c>.mat</c> path that was used, for the report.</param>
/// <param name="Why">Where the walk stopped, or empty where it did not.</param>
/// <param name="Paint">
/// Why the monster is drawn in plain ink, or empty where its own texture is on it. A separate
/// answer from <paramref name="Why"/>: a model can be found in full and still have no colour.
/// </param>
public sealed record MonsterModel(
    SkinnedMesh Mesh,
    GamePicture? Skin,
    string Mesh_,
    string Material,
    string Why,
    string Paint = "")
{
    /// <summary>Nothing found.</summary>
    public static MonsterModel None { get; }
        = new(SkinnedMesh.None, null, string.Empty, string.Empty, "nothing was looked for");

    /// <summary>Whether there is a model to draw.</summary>
    public bool Ready => Mesh.Ready;

    /// <summary>Whether the monster is wearing its own texture rather than plain ink.</summary>
    public bool Painted => Paint.Length == 0 && Skin is { Ready: true };
}

/// <summary>
/// Walks a monster from its table row to the triangles and the texture that clothe it.
/// </summary>
/// <remarks>
/// FIVE FILES AND FOUR FORMATS, which is why this exists rather than the window doing it:
///
///     MonsterVarieties.AOFiles   a path, out of the install's own table
///       -> .ao                   text, keyword = value, names a .sm under SkinMesh
///         -> .sm                 text, its own format, names a .smd and a .mat
///           -> .smd              binary, the triangles
///           -> .mat              JSON, names a .dds
///             -> .dds            BC1 or BC3, which GameArt already decodes
///
/// THE EXTENDS CHAIN IS NOT OPTIONAL. Nine of ten monsters checked carry their own SkinMesh and
/// the tenth - BoneRabbleJaguar - carries none at all: its skin is in the file it extends. A
/// walker that read only the monster's own .ao would report that one as having no model, which
/// looks like a gap in the game rather than a gap in the walk.
///
/// THE CHILD WINS. ExpeditionBasicSkeleton carries a skin AND a remove_skin naming the one its
/// base provides, so the nearer file replaces rather than adds. Taking the first skin found while
/// walking from the monster outwards gets that right without reading remove_skin at all.
///
/// READ THROUGH A FUNCTION RATHER THAN AN INSTALL, which is what makes the whole chain testable:
/// the same walk runs over a dictionary of files here and over the game's bundles there. It is
/// also the shape InstalledArt already uses for exactly this reason.
///
/// THE BODY ONLY, FOR NOW. A monster's armour and weapons are attached_object entries naming
/// their own .ao files, each with a mesh of its own - a skeleton warrior without its shield is
/// still a skeleton warrior, and drawing the attachments is a refinement rather than a missing
/// half.
/// </remarks>
public static class MonsterModels
{
    /// <summary>How far the extends chain is followed before giving up.</summary>
    /// <remarks>A file that extends itself is a loop; the visited set catches that, this caps depth.</remarks>
    public const int MostHops = 8;

    /// <summary>The struct that names a monster's mesh, and the entry inside it.</summary>
    private const string Block = "SkinMesh";
    private const string Entry = "skin";

    /// <summary>
    /// Gathers the model for one monster, or says where the walk stopped.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="one">The monster. Its AoFiles come from the install's own table.</param>
    public static MonsterModel Of(Func<string, byte[]?>? read, MonsterVariety? one)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (one?.AoFiles is not { Count: > 0 } named)
        {
            return MonsterModel.None with
            {
                Why = "the monster names no .ao file - the shipped export does not carry that column",
            };
        }

        if (Skinned(read, named) is not { } found)
        {
            return MonsterModel.None with { Why = "no SkinMesh anywhere in the .ao files or what they extend" };
        }

        MeshManifest manifest = Read(read, found.Mesh, MeshManifest.Read);
        if (!manifest.Ready)
        {
            return MonsterModel.None with
            {
                Mesh_ = found.Mesh,
                Why = $"the mesh manifest did not read: {found.Mesh}",
            };
        }

        SkinnedMesh mesh = Read(read, manifest.Geometry, SkinnedMesh.Read);
        if (!mesh.Ready)
        {
            // THE FILE IS NAMED FIRST AND THE READER'S REASON SECOND. A reader answers about the
            // bytes it was handed and says things like "nothing to read", which is true and
            // useless one layer up: a picture that does not appear looks the same whichever file
            // was missing, so the message has to say which.
            return MonsterModel.None with
            {
                Mesh_ = manifest.Geometry,
                Why = $"the geometry did not read: {manifest.Geometry}"
                    + (mesh.Why.Length > 0 ? $" - {mesh.Why}" : string.Empty),
            };
        }

        // THE .ao's MATERIAL WINS WHERE THERE IS ONE. The manifest names a default for the mesh
        // and the monster's own file overrides it per shape - which is how one skeleton mesh
        // serves an expedition skeleton and a bone rabble, in different colours.
        string material = found.Material.Length > 0
            ? found.Material
            : manifest.Materials.FirstOrDefault(said => said.Length > 0) ?? string.Empty;

        (GamePicture? skin, string paint) = Painted(read, mesh, material);
        return new MonsterModel(mesh, skin, manifest.Geometry, material, string.Empty, paint);
    }

    /// <summary>
    /// The monster's colour texture, and - when there is none - which of the ways it can be
    /// missing this one is.
    /// </summary>
    /// <remarks>
    /// THE REASON IS THE POINT, not a nicety. A monster drawn without its texture comes out in a
    /// pale warm grey that is all but indistinguishable from bare skin, so "is this monster
    /// untextured or is it just pale" is a question a picture CANNOT answer - it was asked from
    /// the live client and could only be settled by reading code. Everything needed to answer it
    /// passes through here and used to be thrown away.
    ///
    /// THE COORDINATES ARE CHECKED LAST AND SEPARATELY, because a mesh can have a perfectly good
    /// texture and no way to look it up. That is the expected state of a bare body whose clothes
    /// are attached objects, and it is a different answer from "the file would not read".
    /// </remarks>
    private static (GamePicture? Skin, string Why) Painted(
        Func<string, byte[]?> read, SkinnedMesh mesh, string material)
    {
        if (material.Length == 0)
        {
            return (null, "neither the .ao nor the .sm names a material");
        }

        MaterialFile paint = Read(read, MaterialFile.Bare(material), MaterialFile.Read);
        if (!paint.Ready)
        {
            return (null, $"the material did not read: {MaterialFile.Bare(material)}");
        }

        if (paint.Albedo is not { Length: > 0 } texture)
        {
            return (null, "the material names no colour texture");
        }

        // THROUGH ReadRaw AND NOT A BARE READ. A texture in this game is one of three things and
        // only the third is a .dds as it stands: it may be a SIGNPOST - a star and the path of the
        // file that really holds it - or it may sit behind a compressed header. Decoding a plain
        // read handles the third and silently fails the other two, which shows as a monster with a
        // mesh and no colour and says nothing about why.
        if (GameArt.ReadRaw(read, texture) is not { Length: > 0 } bytes)
        {
            return (null, $"the texture did not read: {texture}");
        }

        if (GameArt.Decode(bytes) is not { Ready: true } skin)
        {
            return (null, $"the texture did not decode: {texture}");
        }

        return mesh.Coordinated
            ? (skin, string.Empty)
            : (skin, "the mesh carries no texture coordinates, so the texture cannot be applied");
    }

    /// <summary>
    /// The first SkinMesh found, walking from the monster's own files outwards through extends.
    /// </summary>
    /// <remarks>
    /// BREADTH FIRST, so the monster's own files are all looked at before anything they extend.
    /// A depth-first walk would reach a base file before the monster's second .ao, and the nearer
    /// file is the one whose answer counts.
    /// </remarks>
    private static (string Mesh, string Material)? Skinned(
        Func<string, byte[]?> read, IReadOnlyList<string> named)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        foreach (string one in named)
        {
            queue.Enqueue((one, 0));
        }

        while (queue.Count > 0)
        {
            (string path, int depth) = queue.Dequeue();
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            AnimatedObject ao = Object(read, path);
            if (!ao.Ready)
            {
                continue;
            }

            foreach (AoStruct block in ao.Named(Block))
            {
                foreach (AoEntry entry in block.Entries)
                {
                    if (!string.Equals(entry.Key, Entry, StringComparison.Ordinal)
                        || entry.Value.Length == 0)
                    {
                        continue;
                    }

                    // The material sits UNDER the skin, one child per shape, and they name the
                    // same file with different indices - so the first is as good as any.
                    string material = entry.Children
                        .Select(child => child.Value)
                        .FirstOrDefault(said => said.Contains(".mat", StringComparison.OrdinalIgnoreCase))
                        ?? string.Empty;

                    return (entry.Value, material);
                }
            }

            if (depth >= MostHops)
            {
                continue;
            }

            foreach (string parent in ao.Extends)
            {
                queue.Enqueue((parent, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an .ao, adding the extension where the game left it off.
    /// </summary>
    /// <remarks>
    /// AN EXTENDS LINE CARRIES NO EXTENSION - "extends Metadata/Parent" - while an attached object
    /// carries its .ao in full. The same rule AnimatedObject.Read applies against an install, kept
    /// here too because this reads through a function rather than one.
    /// </remarks>
    private static AnimatedObject Object(Func<string, byte[]?> read, string path)
    {
        string said = path.Replace('\\', '/').Trim();
        if (read(said) is { Length: > 0 } content)
        {
            return AnimatedObject.Read(content);
        }

        int slash = said.LastIndexOf('/');
        return said.IndexOf('.', slash + 1) < 0
            ? AnimatedObject.Read(read(said + AnimatedObject.Suffix))
            : AnimatedObject.None;
    }

    private static T Read<T>(Func<string, byte[]?> read, string path, Func<byte[]?, T> into)
        => into(path.Length == 0 ? null : read(path.Replace('\\', '/').Trim()));
}
