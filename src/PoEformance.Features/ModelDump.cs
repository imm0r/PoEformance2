using System.Globalization;
using System.Text;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Writes out the files a monster's model is built from, verbatim.
/// </summary>
/// <remarks>
/// BECAUSE A COUNT IS NOT A FORMAT. The model pane can say "15 shapes from 1 texture · named: 0
/// in the .ao, 7 in the .sm" and that sentence is the end of what a derived number can tell
/// anybody: it establishes that the .ao names no material per shape and that the manifest names
/// FEWER materials than the mesh has shapes, and it cannot say what the files put there instead.
/// Three bosses - Veynar, Connal and Count Geonor's human form - sat at exactly that wall across
/// three fixes, and every further step from there would have been a theory about a file format
/// nobody in this project had read.
///
/// SO THIS PRINTS THE BYTES. Both file types are UTF-16 text, both are small, and the parsers
/// above them keep only the keywords they already know - <see cref="MeshManifest.Parse"/> drops
/// every line whose first word it does not recognise, so whatever maps fifteen shapes onto seven
/// materials would be discarded in silence if it were there. A dump cannot be wrong about that
/// the way a summary can.
///
/// AND THE WHOLE extends CHAIN, not the one file the walk stopped at.
/// <see cref="MonsterModel"/>'s search ends at the first .ao that names a skin, so a parent that
/// carries the materials is never read - which is itself one of the candidate explanations. It
/// can only be ruled in or out by a walk that does not stop, and that is what runs here.
///
/// AND EVERYTHING THE MONSTER HANGS OFF ITSELF. An attached_object names its own .ao with its
/// own mesh, and MonsterModels reads the body alone - so Doryani's skirt, belt, necklace and
/// five other pieces are in the game and not in the pane. Following them is how the files get to
/// say whether each brings a skeleton of its own or is skinned to the parent's rig.
///
/// ON DEMAND ONLY, behind a button. It re-reads and decodes the .ao chain and the manifest, and
/// nothing on the drawing path ever calls it.
/// </remarks>
public static class ModelDump
{
    /// <summary>How far the extends chain is followed. The same cap the model walk uses.</summary>
    private const int MostHops = 8;

    /// <summary>
    /// Most .ao files printed, however many the walk names.
    /// </summary>
    /// <remarks>
    /// RAISED WHEN THE WALK LEARNED TO FOLLOW ATTACHMENTS. Doryani's body is three files and he
    /// hangs nine more off it, each of which may extend a parent of its own - so a cap set for
    /// an extends chain alone would cut the dump off in the middle of the thing it was opened
    /// to answer. It is still a cap and not a hope: effects attach effects, and a walk with no
    /// end is how a diagnostic becomes something nobody runs twice.
    /// </remarks>
    private const int MostFiles = 64;

    /// <summary>
    /// The text of every file behind one monster's model: the .ao chain, then the manifest.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="one">The monster, for the .ao files it names.</param>
    /// <param name="path">The monster's metadata path, as the book has it.</param>
    /// <param name="model">The model already gathered, for the manifest and the shapes.</param>
    public static string Of(
        Func<string, byte[]?>? read, MonsterVariety? one, string path, MonsterModel? model)
    {
        var said = new StringBuilder();
        said.Append("monster: ").Append(one?.Name ?? "?").Append(" [").Append(path).AppendLine("]");

        if (read is null || one?.AoFiles is not { Count: > 0 } named)
        {
            return said.AppendLine("nothing to read: no install, or the monster names no .ao file").ToString();
        }

        // BREADTH FIRST AND PAST THE FIRST ANSWER, unlike the model walk - see the remarks. The
        // visited set is what stops a file that extends itself, the depth cap what stops a long
        // chain, and they catch different things: neither on its own is enough.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var walked = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        foreach (string one_ in named)
        {
            queue.Enqueue((one_, 0));
        }

        var files = 0;
        while (queue.Count > 0 && files < MostFiles)
        {
            (string next, int depth) = queue.Dequeue();
            if (next.Length == 0 || !seen.Add(next))
            {
                continue;
            }

            (string at, byte[]? content) = Find(read, next);
            said.AppendLine().Append("=== .ao ").Append(at).Append(" (depth ").Append(Say(depth)).AppendLine(")");
            if (content is not { Length: > 0 })
            {
                said.AppendLine("(not in the install)");
                continue;
            }

            files++;
            walked.Add(at);
            said.AppendLine(StatDescriptionFiles.Decode(content).TrimEnd());

            if (depth >= MostHops)
            {
                continue;
            }

            AnimatedObject ao = AnimatedObject.Read(content);
            foreach (string parent in ao.Extends)
            {
                queue.Enqueue((parent, depth + 1));
            }

            // AND WHAT IT HANGS OFF ITSELF. Reported from the live client: Doryani stands in the
            // game in a skirt, a belt, a necklace and two shoulder danglers, and the model pane
            // drew a bare-legged Doryani - because every one of those is an attached_object
            // naming its OWN .ao with its own mesh, and MonsterModels reads the body alone.
            // Following them here is what says whether each one brings a skeleton of its own
            // (posed rigidly at the socket) or is skinned to the parent's rig, which is the
            // question that decides how they get drawn - and it cannot be answered from the
            // body's file.
            foreach (string hung in Attached(ao))
            {
                queue.Enqueue((hung, depth + 1));
            }
        }

        Manifest(read, said, model, walked);
        Shapes(said, model);
        Fitting(read, said, model, walked);
        return said.ToString();
    }

    /// <summary>
    /// Where each piece SITS, and whether its socket is a bone the parent's rig really has.
    /// </summary>
    /// <remarks>
    /// THE QUESTION THE PICTURE ASKED. With the pieces joined on, Doryani's shoulder danglers came
    /// out symmetrical about him and at knee height - right in x, wrong in y - and his skirt hung
    /// far below his feet. Symmetric-but-sunken is the signature of a piece whose bones did not
    /// match and fell back to the root, and it cannot be told apart from a piece modelled in its
    /// own space by looking at it.
    ///
    /// SO BOTH HALVES ARE PRINTED. The socket, and whether the PARENT rig carries a bone of that
    /// name at all - a socket the parent does not have means every vertex of that piece falls
    /// back. And the piece's own bounding box beside the body's: a box around the origin says the
    /// mesh is modelled in its own space and needs the socket's transform on it, while a box up at
    /// shoulder height says it is already in the parent's space and only the bones are wrong.
    ///
    /// Those are different fixes, and this is the difference.
    /// </remarks>
    private static void Fitting(
        Func<string, byte[]?> read, StringBuilder said, MonsterModel? model, IReadOnlyList<string> walked)
    {
        said.AppendLine().AppendLine("=== fitting");

        if (model?.Rig is not { Ready: true } rig)
        {
            said.AppendLine("(the monster has no rig, so nothing here can be matched)");
            return;
        }

        var bones = new HashSet<string>(rig.Bones.Select(one => one.Name), StringComparer.OrdinalIgnoreCase);
        said.Append("parent rig: ").Append(Say(rig.Bones.Count)).AppendLine(" bones");

        // THE MOUNTS THE MONSTER OFFERS. The game's own name for an attachment point is aux_ -
        // Malgor's anchor hangs off aux_anchor_jntBnd and his cannon off aux_cannon_jntBnd - so
        // this says in one line which of them a rig has. It is what answers "is this piece meant
        // for a socket nobody wrote down", which a piece socketed "<root>" makes worth asking.
        string[] mounts =
        [
            .. rig.Bones.Select(one => one.Name)
                .Where(one => one.StartsWith("aux", StringComparison.OrdinalIgnoreCase)),
        ];

        said.Append("mounts: ").AppendLine(mounts.Length > 0 ? string.Join(", ", mounts) : "(none named aux_)");

        // THE PARENT'S OWN REST POSE, so a piece's bones can be held up against it by name. Where
        // the two rigs put a shared bone in the same place, the piece is modelled in the
        // monster's space; where they do not, it is not - and that was decided three times by
        // guessing before it was printed once.
        var named_ = new Dictionary<string, int>(rig.Bones.Count, StringComparer.OrdinalIgnoreCase);
        for (var one = 0; one < rig.Bones.Count; one++)
        {
            named_.TryAdd(rig.Bones[one].Name, one);
        }

        IReadOnlyList<System.Numerics.Matrix4x4> over_ = SkeletonPose.Of(rig)?.BindModel ?? [];

        foreach (string one in walked)
        {
            (string _, byte[]? content) = Find(read, one);
            if (content is not { Length: > 0 })
            {
                continue;
            }

            AnimatedObject ao = AnimatedObject.Read(content);
            foreach (AoStruct block in ao.Structs)
            {
                foreach (AoEntry entry in block.Entries)
                {
                    if (Array.IndexOf(Hangs, entry.Key) < 0 || entry.Kind != AoValueKind.Quoted)
                    {
                        continue;
                    }

                    Hung(read, said, bones, named_, over_, entry);
                }
            }
        }
    }

    /// <summary>One attachment line: its socket, whether the parent has that bone, and its box.</summary>
    private static void Hung(
        Func<string, byte[]?> read,
        StringBuilder said,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        AoEntry entry)
    {
        string value = entry.Value.Trim();
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        string socket = space < 0 ? string.Empty : value[..space];
        string path = space < 0 ? value : value[(space + 1)..].Trim();
        if (!path.EndsWith(AnimatedObject.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        said.Append("  ").Append(path[(path.LastIndexOf('/') + 1)..])
            .Append("  socket ").Append(socket.Length > 0 ? socket : "(none)")
            .Append(socket.Length > 0 && bones.Contains(socket) ? " [in the parent rig]" : " [NOT in the parent rig]");

        // THE CHILDREN OF THE ATTACHMENT LINE, which move the piece off its socket and which the
        // walk threw away until the Frostborn Fiend's block of ice turned up upside down on the
        // floor. Printed as written, because the axis order of a rotation with two non-zero
        // components is still unsettled and the next monster that has one settles it.
        foreach (AoEntry child in entry.Children)
        {
            said.Append("  ").Append(child.Key).Append(" = \"").Append(child.Value).Append('"');
        }

        (string _, byte[]? content) = Find(read, path);
        if (content is not { Length: > 0 })
        {
            said.AppendLine("  (not in the install)");
            return;
        }

        AnimatedObject piece = AnimatedObject.Read(content);
        string skin = Skin(piece);
        if (skin.Length == 0)
        {
            // A RIGID PROP IS NOT AN EFFECT, and calling it one is what hid Malgor's cannon: it
            // names a .fmt through FixedMesh, carries its own materials, and has no .sm at all.
            string prop = Entryed(piece, "FixedMesh", "fixed_mesh");
            if (prop.Length == 0)
            {
                said.AppendLine("  (no mesh - an effect or a sound)");
                return;
            }

            FixedMesh fixture = FixedMesh.Read(read(prop.Replace('\\', '/').Trim()));
            said.Append("  fixed mesh ").Append(prop[(prop.LastIndexOf('/') + 1)..]);
            said.AppendLine(fixture.Ready
                ? $"  {Say(fixture.Mesh.Shapes.Count)} shapes"
                : $"  (did not read: {fixture.Why})");

            foreach ((string shape, string material) in fixture.Named)
            {
                said.Append("    ").Append(shape).Append("  ->  ")
                    .AppendLine(material.Length > 0 ? material : "(no material)");
            }

            return;
        }

        MeshManifest manifest = MeshManifest.Read(read(skin.Replace('\\', '/').Trim()));
        said.Append("  box ").Append(Box(manifest.Least)).Append("..").AppendLine(Box(manifest.Most));

        SkinnedMesh geometry = SkinnedMesh.Read(read(manifest.Geometry.Replace('\\', '/').Trim()));
        Facts(said, "    ", geometry.Facts);
        Rigged(read, said, bones, parent, over_, SkeletonPose.Highest(geometry), content);
    }

    /// <summary>How many of a piece's own bones are printed. Enough to see whose names they are.</summary>
    private const int MostBones = 24;

    /// <summary>
    /// A piece's OWN rig, beside the parent's: whose names its bones carry and where they rest.
    /// </summary>
    /// <remarks>
    /// THE QUESTION TWO MONSTERS HAVE NOW ASKED. Bahlak's feather bundle and Malgor's ship's wheel
    /// and seaweed are all socketed <c>&lt;root&gt;</c> - no bone of the parent to hang them from -
    /// and all three come out at the monster's origin instead of on him. Every one of them brings
    /// a rig of its own, and the seaweed's .ao goes further and lists PARENT bone names in
    /// <c>attachment_bones</c>: <c>hip_jntBnd spine_1_jntBnd … R_arm_tentacle_jntBnd_1</c>.
    ///
    /// SO THE ANSWER IS IN THE PIECE'S OWN SKELETON, and this prints it rather than assuming it.
    /// If its bones carry the parent's names, the piece is skinned to the parent's rig and belongs
    /// in the monster's space through each bone's rest transform - which is a different fix from
    /// the rigid socket the anchor and the beard get. If they carry names of their own, it is not,
    /// and the answer is elsewhere. The two cases look identical in a picture and are told apart
    /// here, in one file, without another build.
    /// </remarks>
    private static void Rigged(
        Func<string, byte[]?> read,
        StringBuilder said,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        int highest,
        byte[] content)
    {
        string path = string.Empty;
        foreach (AoStruct block in AnimatedObject.Read(content).Named("ClientAnimationController"))
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (string.Equals(entry.Key, "skeleton", StringComparison.Ordinal) && entry.Value.Length > 0)
                {
                    path = entry.Value;
                }
            }
        }

        if (path.Length == 0)
        {
            said.AppendLine("    (no rig of its own)");
            return;
        }

        AnimationSkeleton own = AnimationSkeleton.Read(read(path.Replace('\\', '/').Trim()));
        if (!own.Ready)
        {
            said.Append("    rig ").Append(path).AppendLine(" (did not read)");
            return;
        }

        var shared = 0;
        foreach (SkeletonBone one in own.Bones)
        {
            if (bones.Contains(one.Name))
            {
                shared++;
            }
        }

        said.Append("    rig ").Append(Say(own.Bones.Count)).Append(" bones, ")
            .Append(Say(shared)).AppendLine(" of them names the parent rig also has");

        // AND WHETHER THE PIECE'S MESH IS INDEXED BY THAT RIG AT ALL. A mesh whose highest
        // weighted bone is past the rig's count was rigged to something else - and the only
        // other rig in play is the parent's - so its numbers must not be put through the
        // piece's table. Printed because a mesh half-mapped and half-dropped looks exactly like
        // a piece in the wrong place.
        if (highest >= 0)
        {
            said.Append("    mesh weights reach bone ").Append(Say(highest))
                .Append(" of ").Append(Say(own.Bones.Count))
                .AppendLine(highest >= own.Bones.Count
                    ? "  [PAST this rig - it is the parent's numbering]"
                    : "  [inside this rig]");
        }

        SkeletonPose? pose = SkeletonPose.Of(own);
        IReadOnlyList<System.Numerics.Matrix4x4> rest = pose?.BindModel ?? [];
        IReadOnlyList<int> over = pose?.Parents ?? [];

        for (var one = 0; one < own.Bones.Count && one < MostBones; one++)
        {
            said.Append("      ").Append(Say(one)).Append(' ').Append(own.Bones[one].Name)
                .Append(bones.Contains(own.Bones[one].Name) ? "  [shared]" : "  [its own]");

            // THE PARENT'S NUMBER, because it is not always lower than the child's - a bone's
            // ancestors are walked to find one the parent rig has, and a walk that assumed the
            // order dropped Bahlak's head feathers on the rig root. Printed so the next reader
            // can see the ordering rather than assume it.
            if (one < over.Count)
            {
                said.Append("  under ").Append(over[one] >= 0 ? Say(over[one]) : "nothing");
            }

            if (one < rest.Count)
            {
                said.Append("  rests at ").Append(Box(rest[one].Translation));
            }

            // AND WHERE THE PARENT RESTS THE SAME BONE, side by side. Whether the two rigs carry
            // the same rest pose is the question every theory about these pieces turned on, and
            // it was answered three times by guessing before it was ever printed.
            if (parent.TryGetValue(own.Bones[one].Name, out int also) && also < over_.Count)
            {
                said.Append("  parent has ").Append(Box(over_[also].Translation));
            }

            said.AppendLine();
        }
    }

    private static string Box(System.Numerics.Vector3 at)
        => $"({at.X.ToString("0.#", CultureInfo.InvariantCulture)},"
            + $"{at.Y.ToString("0.#", CultureInfo.InvariantCulture)},"
            + $"{at.Z.ToString("0.#", CultureInfo.InvariantCulture)})";

    /// <summary>
    /// The .ao files this one hangs off itself - armour, clothing, weapons, effects.
    /// </summary>
    /// <remarks>
    /// THE FOUR KEYS <see cref="AoSurvey"/> ALREADY FOLLOWS, and its parsing with them: an
    /// attachment's value is a SOCKET AND THEN A PATH inside one pair of quotes -
    /// <c>"hip_jntBnd Metadata/Monsters/Doryani/TrueDoryani/attachments/Skirt.ao"</c> - so taken
    /// whole it is a path no install has. That trap cost the first survey 2289 of its 3262 files
    /// and is not worth falling into twice.
    ///
    /// THE SOCKET IS NOT KEPT HERE, only the file. The socket names a bone of the parent's rig
    /// (<c>_jntBnd</c>, the same suffix the manifest's BoneGroups use) and it is what a renderer
    /// would need; a dump only has to get the file on screen, and the line it came from is
    /// printed above it in full anyway.
    /// </remarks>
    private static IEnumerable<string> Attached(AnimatedObject ao)
    {
        foreach (AoStruct block in ao.Structs)
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (Array.IndexOf(Hangs, entry.Key) < 0)
                {
                    continue;
                }

                foreach (string one in AoSurvey.Referenced(entry))
                {
                    yield return one;
                }
            }
        }
    }

    /// <summary>The entry keys whose value is another .ao. From the format diagram; see AoSurvey.</summary>
    private static readonly string[] Hangs =
        ["ao", "fixed_ao", "attached_object", "attached_slaved_animation_object"];

    /// <summary>
    /// The mesh manifest, verbatim, and then the geometry's own headers.
    /// </summary>
    /// <remarks>
    /// THE .sm IS FOUND FROM THE .ao CHAIN, not from the model - which carries the .smd the
    /// manifest NAMED and not the manifest itself, so this section printed a binary file as text
    /// for as long as it existed. A dump whose own labels are wrong is worse than no dump.
    ///
    /// AND THE HEADERS BESIDE IT, because the manifest cannot say whether the reader walked the
    /// geometry correctly and <see cref="MeshFacts"/> can: what the file says the shape names
    /// weigh against what the reader found where it went looking is an invariant a wrong step
    /// cannot satisfy by accident.
    /// </remarks>
    private static void Manifest(
        Func<string, byte[]?> read, StringBuilder said, MonsterModel? model, IReadOnlyList<string> walked)
    {
        string manifest = string.Empty;
        foreach (string one in walked)
        {
            (string _, byte[]? content) = Find(read, one);
            if (content is { Length: > 0 } && Skin(AnimatedObject.Read(content)) is { Length: > 0 } named)
            {
                manifest = named;
                break;
            }
        }

        if (manifest.Length == 0)
        {
            said.AppendLine().AppendLine("=== .sm (none was named)");
        }
        else
        {
            said.AppendLine().Append("=== .sm ").AppendLine(manifest);
            byte[]? content = read(manifest.Replace('\\', '/').Trim());
            said.AppendLine(content is { Length: > 0 }
                ? StatDescriptionFiles.Decode(content).TrimEnd()
                : "(not in the install)");
        }

        string geometry = model?.Mesh_ ?? string.Empty;
        said.AppendLine().Append("=== .smd ").AppendLine(geometry.Length > 0 ? geometry : "(none was named)");
        if (geometry.Length > 0)
        {
            Facts(said, "  ", SkinnedMesh.Read(read(geometry.Replace('\\', '/').Trim())).Facts);
        }
    }

    /// <summary>The <c>skin</c> a SkinMesh block names, or empty where the file has none.</summary>
    private static string Skin(AnimatedObject ao) => Entryed(ao, "SkinMesh", "skin");

    /// <summary>One entry's value out of one kind of block, or empty where the file has none.</summary>
    private static string Entryed(AnimatedObject ao, string block, string key)
    {
        foreach (AoStruct one in ao.Named(block))
        {
            foreach (AoEntry entry in one.Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal) && entry.Value.Length > 0)
                {
                    return entry.Value;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>What a mesh file's own headers said, and whether they agree with each other.</summary>
    private static void Facts(StringBuilder said, string indent, MeshFacts facts)
    {
        if (facts.Vertices == 0)
        {
            said.Append(indent).AppendLine("(the geometry did not read)");
            return;
        }

        said.Append(indent).Append("version ").Append(Say(facts.Version))
            .Append(" · c0h ").Append(Say(facts.Corner))
            .Append(" · ").Append(Say(facts.Details)).Append(" level(s) of detail")
            .Append(" · format 0x").Append(facts.Format.ToString("X", CultureInfo.InvariantCulture))
            .Append(" · stride ").Append(Say(facts.Stride)).AppendLine();

        said.Append(indent).Append("shapes ").Append(Say(facts.Shapes))
            .Append(" in the header, ").Append(Say(facts.BlockShapes)).Append(" in the block")
            .Append(" · ").Append(Say(facts.Triangles)).Append(" triangles over ")
            .Append(Say(facts.Vertices)).AppendLine(" vertices");

        said.Append(indent).Append("names: ").Append(Say(facts.NamesSaid))
            .Append(" bytes said, ").Append(Say(facts.NamesRead)).Append(" read")
            .AppendLine(facts.NamesSaid == facts.NamesRead ? "  [agree]" : "  [DISAGREE - a step is wrong]");
    }

    /// <summary>
    /// The shapes the geometry actually holds, with their slices of the index buffer.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF THE JOIN. Whatever names the manifest lists, the question is which of
    /// the mesh's shapes each one belongs on - so the shape names are printed beside them. It is
    /// also the only way to tell a mesh whose shapes are genuinely unnamed ("shape 3", which
    /// <see cref="SkinnedMesh"/> falls back to) from one whose names simply do not match.
    /// </remarks>
    private static void Shapes(StringBuilder said, MonsterModel? model)
    {
        if (model?.Mesh.Shapes is not { Count: > 0 } shapes)
        {
            said.AppendLine().AppendLine("=== shapes (none)");
            return;
        }

        said.AppendLine().Append("=== shapes (").Append(Say(shapes.Count)).AppendLine(")");
        for (var one = 0; one < shapes.Count; one++)
        {
            MeshShape shape = shapes[one];
            said.Append(Say(one)).Append('\t').Append(shape.Name)
                .Append("\tfrom ").Append(Say(shape.From))
                .Append("\tcount ").AppendLine(Say(shape.Count));
        }
    }

    /// <summary>
    /// Resolves a path the way the model walk does, and hands back what it read.
    /// </summary>
    /// <remarks>
    /// AN EXTENDS LINE CARRIES NO EXTENSION - <c>extends "Metadata/Parent"</c> - while a monster's
    /// own .ao is named in full. The same rule MonsterModel.Object applies, kept here so the dump
    /// reads the same files the model did rather than a subset of them.
    /// </remarks>
    private static (string Path, byte[]? Content) Find(Func<string, byte[]?> read, string path)
    {
        string said = path.Replace('\\', '/').Trim();
        if (read(said) is { Length: > 0 } content)
        {
            return (said, content);
        }

        int slash = said.LastIndexOf('/');
        if (said.IndexOf('.', slash + 1) >= 0)
        {
            return (said, null);
        }

        string with = said + AnimatedObject.Suffix;
        return (with, read(with));
    }

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);
}
