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

    /// <summary>The skeleton the monster's .ao names, or <see cref="AnimationSkeleton.None"/>.</summary>
    public AnimationSkeleton Rig { get; init; } = AnimationSkeleton.None;

    /// <summary>The <c>.ast</c> path that was used, for the report.</summary>
    public string Rig_ { get; init; } = string.Empty;

    /// <summary>
    /// Why the monster cannot be animated, or empty where it can.
    /// </summary>
    /// <remarks>
    /// A THIRD ANSWER BESIDE <see cref="Why"/> AND <see cref="Paint"/>, for the same reason those
    /// two are apart: a model can be found and coloured and still have no skeleton to move it, and
    /// a picture that holds still looks the same whichever of four files was the missing one.
    /// </remarks>
    public string Move { get; init; } = string.Empty;

    /// <summary>Whether there is a skeleton with animations on it that fits this mesh.</summary>
    public bool Moves => Move.Length == 0 && Rig.Ready && Rig.Animations.Count > 0;

    /// <summary>How many bytes the walk read out of the install for this monster, over every file it touched.</summary>
    /// <remarks>
    /// COUNTED, NOT ESTIMATED: every file goes through one function and it adds up what came back.
    /// The .ao files and what they extend, the manifest, the geometry, the material, the texture and
    /// the signpost it may sit behind, the skeleton - and nothing that was asked for and not there.
    /// It is what one click in the book costs, and the pane says so under the picture.
    /// </remarks>
    public long Bytes { get; init; }

    /// <summary>How many files those bytes came out of.</summary>
    public int Files { get; init; }
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

    /// <summary>The struct that names a monster's skeleton, and the entry inside it.</summary>
    /// <remarks>
    /// INSIDE THE FILE'S client BLOCK, which AnimatedObject folds into the same list of structs
    /// with a flag. Over a whole install this is the one key that ever names an .ast - 2581 times,
    /// and nothing else once - so a second spelling is not looked for.
    /// </remarks>
    private const string RigBlock = "ClientAnimationController";
    private const string RigEntry = "skeleton";

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

        // COUNTED AS THEY ARE READ, through the one function everything below reads with, so a
        // file the walk asks for is a file the count saw - the signpost a texture may sit behind
        // included, which GameArt follows on its own. A file that was not there comes back null
        // and counts for nothing.
        var tally = new Tally(read);
        Func<string, byte[]?> counted = tally.Read;

        if (Skinned(counted, named) is not { } found)
        {
            return tally.Failed("no SkinMesh anywhere in the .ao files or what they extend");
        }

        MeshManifest manifest = Read(counted, found.Mesh, MeshManifest.Read);
        if (!manifest.Ready)
        {
            return tally.Failed($"the mesh manifest did not read: {found.Mesh}", found.Mesh);
        }

        SkinnedMesh mesh = Read(counted, manifest.Geometry, SkinnedMesh.Read);
        if (!mesh.Ready)
        {
            // THE FILE IS NAMED FIRST AND THE READER'S REASON SECOND. A reader answers about the
            // bytes it was handed and says things like "nothing to read", which is true and
            // useless one layer up: a picture that does not appear looks the same whichever file
            // was missing, so the message has to say which.
            return tally.Failed(
                $"the geometry did not read: {manifest.Geometry}"
                    + (mesh.Why.Length > 0 ? $" - {mesh.Why}" : string.Empty),
                manifest.Geometry);
        }

        // THE .ao's MATERIAL WINS WHERE THERE IS ONE. The manifest names a default for the mesh
        // and the monster's own file overrides it per shape - which is how one skeleton mesh
        // serves an expedition skeleton and a bone rabble, in different colours.
        string material = found.Material.Length > 0
            ? found.Material
            : manifest.Materials.FirstOrDefault(said => said.Length > 0) ?? string.Empty;

        (GamePicture? skin, string paint) = Painted(counted, mesh, material);
        (AnimationSkeleton rig, string move) = Rigged(counted, found.Skeleton, mesh);

        return new MonsterModel(mesh, skin, manifest.Geometry, material, string.Empty, paint)
        {
            Rig = rig,
            Rig_ = found.Skeleton,
            Move = move,
            Bytes = tally.Bytes,
            Files = tally.Files,
        };
    }

    /// <summary>Reads through another function and adds up what comes back.</summary>
    private sealed class Tally
    {
        private readonly Func<string, byte[]?> _read;

        public Tally(Func<string, byte[]?> read) => _read = read;

        public long Bytes { get; private set; }

        public int Files { get; private set; }

        public byte[]? Read(string path)
        {
            byte[]? said = _read(path);
            if (said is not null)
            {
                Bytes += said.Length;
                Files++;
            }

            return said;
        }

        /// <summary>A model that was not found, still carrying what was read looking for it.</summary>
        public MonsterModel Failed(string why, string mesh = "")
            => MonsterModel.None with { Mesh_ = mesh, Why = why, Bytes = Bytes, Files = Files };
    }

    /// <summary>
    /// The monster's skeleton, and - when it cannot be animated - which of the ways that is.
    /// </summary>
    /// <remarks>
    /// THE LAST CHECK IS THE ONE THAT NEEDS THE GAME. A vertex names its bones by index and the
    /// only bone list in the whole chain is the skeleton's, so the indices must be into it - but
    /// that is an argument from there being nothing else, and this is where it is tested against
    /// every real mesh somebody opens: a highest weighted bone at or past the rig's count means
    /// the argument was wrong, and the model says so instead of skinning garbage.
    /// </remarks>
    private static (AnimationSkeleton Rig, string Why) Rigged(
        Func<string, byte[]?> read, string skeleton, SkinnedMesh mesh)
    {
        if (skeleton.Length == 0)
        {
            return (AnimationSkeleton.None, "no .ao names a skeleton under ClientAnimationController");
        }

        AnimationSkeleton rig = Read(read, skeleton, AnimationSkeleton.Read);
        if (!rig.Ready)
        {
            return (rig, $"the skeleton did not read: {skeleton}"
                + (rig.Why.Length > 0 ? $" - {rig.Why}" : string.Empty));
        }

        if (rig.Animations.Count == 0)
        {
            return (rig, $"the skeleton carries no animations: {skeleton}"
                + (rig.Why.Length > 0 ? $" - {rig.Why}" : string.Empty));
        }

        int highest = SkeletonPose.Highest(mesh);
        if (highest >= rig.Bones.Count)
        {
            return (rig, $"the mesh weights bone {highest} and the skeleton has only {rig.Bones.Count}");
        }

        return (rig, string.Empty);
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
    private static (string Mesh, string Material, string Skeleton)? Skinned(
        Func<string, byte[]?> read, IReadOnlyList<string> named)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        foreach (string one in named)
        {
            queue.Enqueue((one, 0));
        }

        // THE SKIN AND THE SKELETON ARE FOUND SEPARATELY, each by the nearest file that names it,
        // and the walk carries on past the first until it has both or runs out of files. They are
        // usually in the same .ao; a monster whose base supplies the rig and whose own file only
        // swaps the skin is the case that stopping at the skin would leave unable to move.
        string? mesh = null;
        var material = string.Empty;
        string? skeleton = null;

        while (queue.Count > 0 && (mesh is null || skeleton is null))
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

            if (mesh is null)
            {
                foreach (AoStruct block in ao.Named(Block))
                {
                    foreach (AoEntry entry in block.Entries)
                    {
                        if (!string.Equals(entry.Key, Entry, StringComparison.Ordinal)
                            || entry.Value.Length == 0)
                        {
                            continue;
                        }

                        // The material sits UNDER the skin, one child per shape, and they name
                        // the same file with different indices - so the first is as good as any.
                        material = entry.Children
                            .Select(child => child.Value)
                            .FirstOrDefault(said => said.Contains(".mat", StringComparison.OrdinalIgnoreCase))
                            ?? string.Empty;

                        mesh = entry.Value;
                        break;
                    }

                    if (mesh is not null)
                    {
                        break;
                    }
                }
            }

            if (skeleton is null)
            {
                foreach (AoStruct block in ao.Named(RigBlock))
                {
                    foreach (AoEntry entry in block.Entries)
                    {
                        if (string.Equals(entry.Key, RigEntry, StringComparison.Ordinal)
                            && entry.Value.Length > 0)
                        {
                            skeleton = entry.Value;
                            break;
                        }
                    }

                    if (skeleton is not null)
                    {
                        break;
                    }
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

        return mesh is null ? null : (mesh, material, skeleton ?? string.Empty);
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
