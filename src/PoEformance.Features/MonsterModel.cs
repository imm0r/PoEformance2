using System.Numerics;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// A monster's model, gathered from the five files it takes to draw one.
/// </summary>
/// <param name="Mesh">The geometry, or <see cref="SkinnedMesh.None"/> where none was found.</param>
/// <param name="Skin">The colour texture with its halved levels, or null to draw the mesh plain.</param>
/// <param name="Mesh_">The <c>.sm</c> that named the geometry, for the report.</param>
/// <param name="Material">The <c>.mat</c> path that was used, for the report.</param>
/// <param name="Why">Where the walk stopped, or empty where it did not.</param>
/// <param name="Paint">
/// Why the monster is drawn in plain ink, or empty where its own texture is on it. A separate
/// answer from <paramref name="Why"/>: a model can be found in full and still have no colour.
/// </param>
public sealed record MonsterModel(
    SkinnedMesh Mesh,
    Mipmaps? Skin,
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
    public bool Painted => Paint.Length == 0 && Skin is not null;

    /// <summary>
    /// One texture per shape of the mesh, in its order - what the renderer paints each part with.
    /// </summary>
    /// <remarks>
    /// A MONSTER IS BUILT OF PARTS AND THEY DO NOT SHARE A SHEET. Body, cloak, wings: the .ao
    /// names a material per shape and each shape's coordinates address ITS OWN texture, so one
    /// texture over the whole mesh puts the body's pixels on the wings. Reported from the live
    /// client by Bahlak the Sky Seer, who came out black with red patches against a game that
    /// draws him in feathers.
    ///
    /// A SHAPE WITH NO MATERIAL OF ITS OWN GETS <see cref="Skin"/>, which is what the whole mesh
    /// used to get. That keeps every monster whose .ao names one material exactly as it was, and
    /// confines the change to the ones that name several - where the old answer was wrong.
    ///
    /// Empty where there is nothing to paint with, and then the renderer draws in ink.
    /// </remarks>
    public IReadOnlyList<Mipmaps?> Skins { get; init; } = [];

    /// <summary>The distinct materials the pictures came out of, for the report.</summary>
    public IReadOnlyList<string> Materials { get; init; } = [];

    /// <summary>
    /// How many materials the files NAMED: the .ao's own, and the mesh manifest's.
    /// </summary>
    /// <remarks>
    /// THE DIFFERENCE BETWEEN "ONE SHEET IS THE TRUTH" AND "WE FOUND ONE". A monster reported
    /// as thirty-five shapes from one texture is either painted from one atlas - which is
    /// ordinary and right - or is a monster whose per-shape materials this walk did not match,
    /// and the picture looks the same either way. The counts tell those apart: one named in the
    /// .ao and none in the .sm is a monster with one material; thirty-five named and one used
    /// is a bug in the matching.
    /// </remarks>
    public int NamedInAo { get; init; }

    /// <inheritdoc cref="NamedInAo"/>
    public int NamedInMesh { get; init; }

    /// <summary>
    /// Whether the manifest's per-material numbers added up and were used to spread its short
    /// material list over the mesh's shapes.
    /// </summary>
    /// <remarks>
    /// SHOWN BECAUSE THE READING IS NOT PROVEN, only tested: the number after each material path
    /// is unidentified - see <see cref="MeshManifest.Spread"/> - and this says, per monster,
    /// whether the file passed the test and the picture depends on it. A line that says so is
    /// what turns "the boss looks right" into evidence about the format.
    /// </remarks>
    public bool Runs { get; init; }

    /// <summary>The distinct colour textures actually put on the mesh, for the report.</summary>
    /// <remarks>
    /// NAMED BECAUSE A WRONG ONE LOOKS LIKE A MISSING ONE. A mask or an occlusion map drawn as
    /// colour is a monster in greyscale, which reads as "no texture" - and the file name says
    /// in a word which of the two it is. See <see cref="Guessed"/>.
    /// </remarks>
    public IReadOnlyList<string> Textures { get; init; } = [];

    /// <summary>
    /// Whether any of those textures was chosen without a slot name saying it is the colour map.
    /// </summary>
    /// <remarks>
    /// The fallback is "the first texture that is not a normal map", which is a guess this
    /// project has always marked as one in its comments and never on screen. Where it is in
    /// force the pane says so, because it is the difference between a monster the game paints
    /// dark and a monster painted from the wrong map.
    /// </remarks>
    public bool Guessed { get; init; }

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

    /// <summary>
    /// How many pieces the monster wears that were joined onto the body.
    /// </summary>
    /// <remarks>
    /// SHOWN UNDER THE PICTURE because a monster with none and a monster whose attachments were
    /// switched off look the same, and so does one whose pieces all failed to read. See
    /// MonsterModels.Dressing.
    /// </remarks>
    public int Parts { get; init; }

    /// <summary>
    /// How many <c>.sm</c> files the body itself is, before anything worn over it.
    /// </summary>
    /// <remarks>
    /// ONE IS ORDINARY AND SEVEN IS THE CASE THAT WAS BEING LOST. A SkinMesh block may name
    /// several manifests, each a section of the same body on the same rig: Zar Wali, the Bone
    /// Tyrant is arms, chest, head, neck and tail in seven files, and while only the first was
    /// read the pane drew two floating arms. Shown under the picture because a monster drawn in
    /// pieces and a monster that really IS two arms look the same.
    /// </remarks>
    public int Sections { get; init; } = 1;
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
    /// <param name="wearing">
    /// Whether the pieces the monster hangs off itself are read and joined on. Off gives exactly
    /// the body this returned before there was a choice - see <see cref="Dressing"/> for what it
    /// costs, which is the reason there is one.
    /// </param>
    public static MonsterModel Of(Func<string, byte[]?>? read, MonsterVariety? one, bool wearing = true)
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

        MeshManifest manifest = Read(counted, found.Meshes[0], MeshManifest.Read);
        if (!manifest.Ready)
        {
            return tally.Failed($"the mesh manifest did not read: {found.Meshes[0]}", found.Meshes[0]);
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

        // THE .ao's MATERIALS WIN WHERE THERE ARE ANY. The manifest names a default for the mesh
        // and the monster's own file overrides it per shape - which is how one skeleton mesh
        // serves an expedition skeleton and a bone rabble, in different colours.
        //
        // ALL OF THEM, IN ORDER, AND NOT THE FIRST. A monster built out of parts names a
        // material per shape and they are not all the same file: the first can perfectly well
        // be a cloth or a glow with no colour map in it while the body's skin is the second.
        // Taking the first was measured on monsters cut from one sheet and reported wrong from
        // the live client by Veynar the Frostbane, who is plainly painted in the game and came
        // out here in plain ink. The renderer puts ONE texture on the mesh, so the first
        // material that actually carries a colour map is the one it gets.
        List<string> materials =
        [
            .. found.Materials.Select(one => one.Material),
            .. manifest.Materials.Where(one => one.Path.Length > 0).Select(one => one.Path),
        ];

        // ONE CACHE FOR THE WHOLE WALK, keyed by FILE rather than by what named it: a material
        // is read once however many shapes point at it, and a texture is decoded once however
        // many materials name it. Without it the model's own skin and the shape wearing the
        // same material were two decodes of one 2048-square sheet - and two objects, which is
        // also two uploads to the renderer.
        var paints = new Paints();
        (Mipmaps? skin, string paint, string material) = Painted(counted, mesh, materials, paints);

        // AND ONE PER SHAPE, over the same reads: the walk above already decoded every material
        // that has a colour in it, so this is a lookup rather than a second pass over the
        // bundles. See MonsterModel.Skins for why a monster needs more than one.
        Dress dress = Dressed(counted, mesh, found.Materials, manifest, skin, material, paints);

        (AnimationSkeleton rig, string move) = Rigged(counted, found.Skeleton, mesh);

        // AND WHAT THE MONSTER WEARS. Doryani's skirt, belt, necklace and six more pieces are
        // attached_object entries naming their own .ao, mesh and rig - see Dressing. Joined into
        // one mesh so the renderer, the per-shape palette and the pose go on working unchanged.
        // THE BODY ITSELF CAN BE SEVERAL FILES, before anything is worn over it. Joined the same
        // way, and always - a section is not an accessory and switching the parts off must not
        // take half the monster with it. See Sections.
        List<Part> parts = Sections(counted, found.Meshes, found.Materials, paints, skin);
        int sections = parts.Count;

        if (wearing)
        {
            parts.AddRange(Dressing(counted, found.Files, rig, paints, skin));
        }

        if (parts.Count > 0)
        {
            List<MeshJoin> join =
            [
                new MeshJoin(mesh),
                .. parts.Select(one => new MeshJoin(one.Mesh, one.Bones, one.Weights, one.Place)),
            ];
            List<Mipmaps?> worn = [.. dress.Skins, .. parts.SelectMany(one => one.Skins)];
            mesh = SkinnedMesh.Joined(join);
            dress = dress with { Skins = worn };
        }

        return new MonsterModel(mesh, skin, manifest.Geometry, material, string.Empty, paint)
        {
            Parts = parts.Count - sections,
            Sections = found.Meshes.Count,
            Skins = dress.Skins,
            Materials = dress.Materials,
            NamedInAo = found.Materials.Count,
            NamedInMesh = manifest.Materials.Count,
            Runs = dress.Runs,
            Textures = dress.Textures,
            Guessed = dress.Guessed,
            Rig = rig,
            Rig_ = found.Skeleton,
            Move = move,
            Bytes = tally.Bytes,
            Files = tally.Files,
        };
    }

    /// <summary>One piece a monster hangs off itself, ready to be joined onto the body.</summary>
    /// <param name="Mesh">Its geometry.</param>
    /// <param name="Bones">Its vertex bones, remapped onto the PARENT's rig, or null where they could not be.</param>
    /// <param name="Skins">One texture per shape of it, in its order - the same rule the body follows.</param>
    private readonly record struct Part(
        SkinnedMesh Mesh,
        byte[]? Bones,
        byte[]? Weights,
        Matrix4x4? Place,
        IReadOnlyList<Mipmaps?> Skins);

    /// <summary>The entry keys whose value is another .ao. From the format diagram; see AoSurvey.</summary>
    private static readonly string[] Hangs =
        ["ao", "fixed_ao", "attached_object", "attached_slaved_animation_object"];

    /// <summary>How deep the attachment chain is followed. Doryani's belt hangs a dagger; that is two.</summary>
    private const int MostDeep = 4;

    /// <summary>
    /// Most pieces joined onto one monster. A guard on a walk whose shape nobody has measured.
    /// </summary>
    /// <remarks>
    /// EFFECTS ATTACH EFFECTS, which is the failure this is against: an .ao chain followed without
    /// a cap is how a click in the book becomes a minute. Doryani wears nine with four more under
    /// his belt, so this is well clear of what a dressed monster needs.
    /// </remarks>
    private const int MostParts = 48;

    /// <summary>
    /// The body's other sections, where its SkinMesh block names more than one manifest.
    /// </summary>
    /// <remarks>
    /// A SKIN BLOCK IS A LIST, NOT A FIELD, and reading it as a field cost a boss his whole body.
    /// Reported from the live client: Zar Wali, the Bone Tyrant came out as two arms floating in
    /// the air, painted from <c>GiantSnakeSkeletonBossArm_colour.dds</c> - and his .ao says why
    /// in seven consecutive lines, <c>GSSBArmA</c> through <c>GSSBTail</c>, of which this walk
    /// read the first.
    ///
    /// THEY ARE SECTIONS OF ONE BODY ON ONE RIG, which is what makes them cheap to join: each
    /// carries its own vertex bones and they already index the SAME skeleton, so unlike a worn
    /// piece there is nothing to remap and nowhere to put it. Its own manifest names its own
    /// materials, so each section is dressed on its own and the palette comes out per shape as
    /// before.
    ///
    /// NOT UNDER THE PARTS SWITCH. Turning attachments off is meant to undress a monster, not
    /// to behead one.
    /// </remarks>
    private static List<Part> Sections(
        Func<string, byte[]?> read,
        IReadOnlyList<string> skins,
        IReadOnlyList<(string Shape, string Material)> named,
        Paints paints,
        Mipmaps? fallback)
    {
        var parts = new List<Part>();
        for (var one = 1; one < skins.Count && parts.Count < MostParts; one++)
        {
            MeshManifest manifest = Read(read, skins[one], MeshManifest.Read);
            if (!manifest.Ready)
            {
                continue;
            }

            SkinnedMesh mesh = Read(read, manifest.Geometry, SkinnedMesh.Read);
            if (!mesh.Ready)
            {
                continue;
            }

            Dress dress = Dressed(read, mesh, named, manifest, fallback, string.Empty, paints);

            // NO BONES AND NO PLACE: the section's own vertex bones index the body's rig
            // already, so handing null keeps them and the join leaves the geometry where it is.
            parts.Add(new Part(mesh, null, null, null, dress.Skins));
        }

        return parts;
    }

    /// <summary>
    /// Everything the monster wears, read from the attachments its own files name.
    /// </summary>
    /// <remarks>
    /// WHAT THIS IS FOR. Reported from the live client: Doryani stands in the game in a skirt and
    /// the pane drew him bare-legged. The paint was never the problem - his runs add up and every
    /// shape has its sheet - the geometry simply was not there, because a skirt is not part of the
    /// body mesh. It is <c>attached_object = "hip_jntBnd …/attachments/Skirt.ao"</c>, with a mesh,
    /// a rig and an idle animation of its own, and the body's files say nothing else about it.
    ///
    /// A PIECE IS EITHER A PROP OR A SKIN, and its own rig says which - see
    /// <see cref="Retargeted"/>. A prop shares only <c>root_jntBnd</c> with the parent, is
    /// modelled around its own origin, and goes rigidly at its socket. A skin carries the body's
    /// own bone names, is already in the monster's space, and wants its bone NUMBERS translated
    /// and no transform at all.
    ///
    /// GETTING THAT WRONG IS INVISIBLE AT REST, which is what made it expensive: a piece bound
    /// rigidly to one bone stands in the right place until something moves, and then follows that
    /// one bone while the body deforms around it.
    ///
    /// WHAT IT COSTS is the reason the pane has a switch for it. Doryani's body is 32 MB across
    /// 15 files; his nine pieces bring their own meshes, rigs and sheets, and the belt hangs three
    /// more under itself. That is the price of a monster that looks like itself, and it is paid on
    /// every click in a list somebody scrolls.
    /// </remarks>
    private static List<Part> Dressing(
        Func<string, byte[]?> read,
        IReadOnlyList<string> files,
        AnimationSkeleton parent,
        Paints paints,
        Mipmaps? fallback)
    {
        var parts = new List<Part>();
        if (SkeletonPose.Of(parent) is not { } rest)
        {
            // Without the parent's rest pose there is nowhere to put a piece, and a pile of
            // clothing at the monster's feet is worse than a monster in its underwear.
            return parts;
        }

        var where = new Dictionary<string, int>(parent.Bones.Count, StringComparer.OrdinalIgnoreCase);
        for (var one = 0; one < parent.Bones.Count; one++)
        {
            where.TryAdd(parent.Bones[one].Name, one);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, Matrix4x4 Place, int Bone, int Depth, bool Rooted)>();

        // THE MONSTER ITSELF IS THE OUTERMOST CARRIER, at the identity: a piece socketed to
        // "<root>" at the top level is already in the monster's own space and wants no transform
        // at all. Bone 0 is the rig's root, so the piece still follows the monster.
        foreach (string one in files)
        {
            foreach ((string socket, string path, Matrix4x4 local) in Hung(Object(read, one)))
            {
                if (Socketed(socket, where, rest, Matrix4x4.Identity, 0, top: true, local) is { } put)
                {
                    queue.Enqueue((path, put.Place, put.Bone, 1, Rooted(socket)));
                }
            }
        }

        while (queue.Count > 0 && parts.Count < MostParts)
        {
            (string path, Matrix4x4 place, int bone, int depth, bool rooted) = queue.Dequeue();
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            AnimatedObject ao = Object(read, path);
            if (!ao.Ready)
            {
                continue;
            }

            if (depth < MostDeep)
            {
                foreach ((string inner, string under, Matrix4x4 local) in Hung(ao))
                {
                    if (Socketed(inner, where, rest, place, bone, top: false, local) is { } put)
                    {
                        queue.Enqueue((under, put.Place, put.Bone, depth + 1, Rooted(inner)));
                    }
                }
            }

            if (Worn(read, ao, place, bone, paints, fallback, where, rooted) is { } part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    /// <summary>
    /// Where a socket puts a piece: the transform to model space, and the bone it then follows.
    /// </summary>
    /// <remarks>
    /// THREE CASES, AND THE GAME'S OWN SPELLING SEPARATES THEM.
    ///
    /// "&lt;root&gt;" IS NOT A BONE NAME. It is how the game says "at my carrier's own origin", so
    /// the piece is already in whatever space its carrier is in and wants that carrier's
    /// transform unchanged - which at the top level is the identity. Reported from the live
    /// client: Bahlak the Sky Seer wears one piece, socketed "&lt;root&gt;", and treating that as an
    /// unknown bone put the parent rig's ROOT transform on a piece already in model space. The
    /// root carries the rig's own orientation, so his feathers were tipped over and laid out
    /// flat on the floor at his feet.
    ///
    /// A NAME THE PARENT RIG HAS is the ordinary case: the bone's rest transform places the
    /// piece, and binding to that bone is what makes it follow an arm that lifts.
    ///
    /// A NAME IT DOES NOT HAVE means different things inside and outside. On a piece hung off
    /// another piece it is a bone of the CARRIER's rig - Doryani's dagger and mirror name
    /// phys_skinned_L_1_jntBnd and _2_, which belong to his belt - so the carrier's own place is
    /// the nearest thing the body knows. At the TOP level there is no carrier to fall back to,
    /// and bone 0 is not an answer, it is the floor: the piece is left out, on the same rule that
    /// leaves a monster with no rig undressed.
    /// </remarks>
    private static (Matrix4x4 Place, int Bone)? Socketed(
        string socket,
        IReadOnlyDictionary<string, int> where,
        SkeletonPose rest,
        Matrix4x4 carrier,
        int bone,
        bool top,
        Matrix4x4 local)
    {
        if (socket.Length == 0 || socket.StartsWith('<'))
        {
            return (local * carrier, bone);
        }

        if (where.TryGetValue(socket, out int at) && at < rest.BindModel.Count)
        {
            return (local * rest.BindModel[at], at);
        }

        return top ? null : (local * carrier, bone);
    }

    /// <summary>
    /// The turn and the shift an attachment line asks for on top of its socket.
    /// </summary>
    /// <remarks>
    /// TWO LINES THAT WERE BEING THROWN AWAY. An attached_object entry can carry children, and
    /// the Frostborn Fiend's block of ice carries both:
    ///
    ///     attached_object = "R_Weapon …/QuadrillaArcticIceHeld.ao"
    ///         attached_object_translation = "0 0 -55"
    ///         attached_object_rotation = "-3.141 -0 0"
    ///
    /// That is half a turn about x and a shift of fifty-five, and without it the ice he is
    /// holding stands upright on the floor beside him - which is what the pane drew.
    ///
    /// THE AXIS ORDER IS NOT SETTLED. The one sample in hand turns about a single axis, where
    /// order cannot matter, and no open reader of this format reads these lines at all - so x
    /// then y then z is what this applies and what the dump prints, and a monster that turns
    /// about two axes at once is what would settle it. Radians, as written.
    /// </remarks>
    private static Matrix4x4 Local(AoEntry entry)
    {
        Matrix4x4 said = Matrix4x4.Identity;
        foreach (AoEntry child in entry.Children)
        {
            if (string.Equals(child.Key, Turn, StringComparison.Ordinal) && Three(child.Value) is { } turn)
            {
                said *= Matrix4x4.CreateRotationX(turn.X)
                    * Matrix4x4.CreateRotationY(turn.Y)
                    * Matrix4x4.CreateRotationZ(turn.Z);
            }
            else if (string.Equals(child.Key, Shift, StringComparison.Ordinal) && Three(child.Value) is { } shift)
            {
                said *= Matrix4x4.CreateTranslation(shift);
            }
        }

        return said;
    }

    /// <summary>Three numbers separated by spaces, or null where the line is not that.</summary>
    private static Vector3? Three(string said)
    {
        string[] parts = said.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, Invariant, out float x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, Invariant, out float y)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, Invariant, out float z)
                ? new Vector3(x, y, z)
                : null;
    }

    private static System.Globalization.CultureInfo Invariant
        => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>The children of an attachment line that move it off its socket.</summary>
    private const string Turn = "attached_object_rotation";

    /// <inheritdoc cref="Turn"/>
    private const string Shift = "attached_object_translation";

    /// <summary>
    /// Whether an attachment line names no socket at all - <c>&lt;root&gt;</c>, or nothing.
    /// </summary>
    /// <remarks>
    /// THE GAME'S OWN SPELLING DECIDES, which is worth more than any threshold this could invent.
    /// A line that names a bone is PLACING the piece there: the anchor at aux_anchor_jntBnd, the
    /// beard at head_jntBnd, the Frostborn Fiend's block of ice at R_Weapon. A line that says
    /// "&lt;root&gt;" places nothing, so the piece has to carry its own placement - and the only
    /// thing it can carry it in is a rig of the parent's bones.
    ///
    /// WHAT THIS REPLACED, and why: two goes at reading the piece's rig INSTEAD of the line, and
    /// each broke a monster the other fixed. Counting shared bone names made the ice - three
    /// bones, of which the parent has two - into a skinned cloak and stood it upright on the
    /// floor. Requiring the shared bones to rest in the same place as the parent's then rejected
    /// Bahlak's feathers, which are the clearest skinned piece there is, and dropped them on the
    /// floor in three flat clumps. The line was carrying the answer the whole time.
    /// </remarks>
    private static bool Rooted(string socket)
        => socket.Length == 0 || socket.StartsWith('<');

    /// <summary>The socket and file of every .ao this one hangs off itself.</summary>
    /// <remarks>
    /// A SOCKET AND THEN A PATH INSIDE ONE PAIR OF QUOTES, which taken whole is a path no install
    /// has - the trap that cost AoSurvey 2289 of its 3262 files. Split on the first space; the
    /// socket is a bone name of the parent's rig, or <c>&lt;root&gt;</c> for the piece's own.
    /// </remarks>
    private static IEnumerable<(string Socket, string Path, Matrix4x4 Local)> Hung(AnimatedObject ao)
    {
        foreach (AoStruct block in ao.Structs)
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (Array.IndexOf(Hangs, entry.Key) < 0 || entry.Kind != AoValueKind.Quoted)
                {
                    continue;
                }

                string said = entry.Value.Trim();
                int space = said.IndexOf(' ', StringComparison.Ordinal);
                string path = space < 0 ? said : said[(space + 1)..].Trim();
                if (path.EndsWith(AnimatedObject.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (space < 0 ? string.Empty : said[..space], path, Local(entry));
                }
            }
        }
    }

    /// <summary>One attachment read whole: its mesh, its textures, and where on the body it goes.</summary>
    /// <remarks>
    /// RIGID AT ITS SOCKET, which is what the boxes said to do. Every one of Doryani's thirteen
    /// pieces has a box a few tens of units across sitting on the origin - the left and right
    /// shoulder pieces mirrored in x rather than standing apart - so each is modelled in its own
    /// space and means nothing in the monster's until the socket bone's rest transform is on it.
    ///
    /// WHY NOT POSE IT WITH ITS OWN RIG. A piece has one, and in the game it is simulated - the
    /// .ao carries bend stiffness, viscosity and an enclosure angle. At rest that rig puts the
    /// vertices exactly where the file already has them, so the rest pose IS the mesh, and
    /// hanging it rigidly off the bone is the same answer with none of the work. What it gives
    /// up is cloth that swings while the monster moves, which a turning picture does not miss.
    /// </remarks>
    private static Part? Worn(
        Func<string, byte[]?> read,
        AnimatedObject ao,
        Matrix4x4 place,
        int bone,
        Paints paints,
        Mipmaps? fallback,
        IReadOnlyDictionary<string, int> where,
        bool rooted)
    {
        string skin = Skin(ao);
        if (skin.Length == 0)
        {
            return Propped(read, ao, place, bone, paints, fallback);
        }

        MeshManifest manifest = Read(read, skin, MeshManifest.Read);
        if (!manifest.Ready)
        {
            return null;
        }

        SkinnedMesh mesh = Read(read, manifest.Geometry, SkinnedMesh.Read);
        if (!mesh.Ready)
        {
            return null;
        }

        Dress dress = Dressed(read, mesh, [], manifest, fallback, string.Empty, paints);

        // EITHER WAY THE PIECE'S OWN RIG SAYS WHICH OF THE PARENT'S BONES EACH VERTEX BELONGS
        // TO - see Retargeted. What the attachment line decides is where the piece SITS: a
        // "<root>" piece is already in the monster's space and wants no transform, a socketed
        // one is in its socket's space and wants that bone's rest transform on it.
        if (Retargeted(read, ao, where, mesh, rooted, bone) is { } map)
        {
            return new Part(mesh, map, null, rooted ? null : place, dress.Skins);
        }

        (byte[] bones, byte[] weights) = Bound(mesh.Positions.Length, bone);
        return new Part(mesh, bones, weights, place, dress.Skins);
    }

    /// <summary>
    /// A piece's vertex bones moved onto the PARENT's rig, or null where it is a rigid prop.
    /// </summary>
    /// <remarks>
    /// TWO KINDS OF ATTACHMENT WEAR THE SAME SPELLING, and the piece's own skeleton is what tells
    /// them apart. Measured, on five pieces across two bosses:
    ///
    ///     Malgor's anchor    12 bones, one shared with the parent - root_jntBnd
    ///     Malgor's beard      8 bones, one shared - root_jntBnd
    ///     Malgor's ship wheel 10 bones, one shared - root_jntBnd
    ///     Malgor's seaweed   23 bones, TWELVE shared - hip, spine_1, spine_2, chest, clavicles…
    ///     Bahlak's feathers  31 bones, ELEVEN shared - spine_1, spine_2, chest, neck, head…
    ///
    /// A PROP SHARES ONLY THE ROOT, which every rig has and which therefore means nothing. Its
    /// bones are its own - phys_tri_chain, jaw_jntBnd_1..8 - it is modelled around its own origin,
    /// and it belongs rigidly at its socket. That is the case <see cref="Bound"/> handles and it
    /// has been right all along.
    ///
    /// A SKINNED PIECE SHARES THE BODY'S OWN BONES, and its rig rests them where the body's rest:
    /// the seaweed's hip_jntBnd at (0,18.7,-97.6), its chest at (0,26.3,-171.6). Its vertices are
    /// ALREADY in the monster's space - the seaweed's box is y 40..131, draped over a spine that
    /// sits at y 19..26 - so it wants no transform at all. What it wants is its bone NUMBERS
    /// translated, because bone 9 in the piece's file is the parent's bone 47.
    ///
    /// WHY THAT WAS THE WHOLE BUG. Binding every vertex to one bone is harmless at rest and wrong
    /// the moment anything moves: the body deforms and the piece rigidly follows a single bone.
    /// For a socketed piece that bone is an arm or a head and it looks right; for a "&lt;root&gt;"
    /// piece it is the RIG ROOT, which is the one bone that does not follow the body at all. So
    /// Bahlak's feathers stayed on the floor while he rose, and Malgor's seaweed hung in the air
    /// behind him - and only the "&lt;root&gt;" pieces ever did, which is exactly what was reported.
    ///
    /// A BONE THE PARENT DOES NOT HAVE goes to its nearest ancestor that the parent does have.
    /// The unshared ones are all simulated - phys_skinned_M_skirt_2, phys_tri_seaweed_belt - so
    /// their nearest real ancestor is the spine or chest they hang off, which is where a tool
    /// that does not simulate cloth should hold them.
    ///
    /// ONLY EVER FOR A PIECE THAT NAMES NO SOCKET - see <see cref="Rooted"/>. The rig's shared
    /// names alone are NOT enough, and two goes at making them enough each broke a monster the
    /// other fixed: counting them turned the Frostborn Fiend's block of ice, three bones of which
    /// the parent has two, into a cloak and stood it on the floor; requiring the shared bones to
    /// rest where the parent's rest then rejected Bahlak's feathers and dropped them in three
    /// flat clumps. The attachment line was carrying the answer the whole time.
    ///
    /// WHAT IS STILL ASKED OF THE RIG is only the other half: a "&lt;root&gt;" piece that shares
    /// nothing but the root has no bones to be moved onto and stays where it is. Malgor's ship's
    /// wheel is that piece, and it is the one thing here still not settled.
    /// </remarks>
    private static byte[]? Retargeted(
        Func<string, byte[]?> read,
        AnimatedObject ao,
        IReadOnlyDictionary<string, int> where,
        SkinnedMesh mesh,
        bool rooted,
        int socket)
    {
        string path = Entryed(ao, RigBlock, RigEntry);
        if (path.Length == 0 || mesh.Bones.Length != mesh.Positions.Length * 4)
        {
            return null;
        }

        AnimationSkeleton own = Read(read, path, AnimationSkeleton.Read);
        if (!own.Ready || SkeletonPose.Of(own) is not { } mine)
        {
            return null;
        }

        // A MESH THAT REACHES PAST ITS OWN RIG WAS NOT RIGGED TO IT, and putting its numbers
        // through a table that short is how the last of Bahlak's feathers stayed on the floor:
        // every bone the table did not cover fell to entry nought, the rig root, while the rest
        // of the piece followed his chest - so the bundle came out stretched between the two.
        //
        // THE ONLY OTHER RIG IN PLAY IS THE PARENT'S, and a piece socketed "<root>" is already in
        // the parent's space, so a mesh indexed past its own rig is a mesh indexed by the
        // parent's: its numbers are already the right ones and want passing through untouched.
        // Mapping them to nought is the one answer that is certainly wrong.
        if (SkeletonPose.Highest(mesh) >= own.Bones.Count)
        {
            // ONLY WHERE NOTHING ELSE IS BEING DONE TO THE GEOMETRY. A socketed piece is moved by
            // its socket's transform, and passing the parent's numbers through as well would
            // apply that move twice - so there the old rigid binding is the safe answer.
            return rooted ? mesh.Bones : null;
        }

        var onto = new byte[own.Bones.Count];
        var shared = 0;
        for (var one = 0; one < own.Bones.Count; one++)
        {
            // UP THE CHAIN BY NAME, NOT BY NUMBER. A first attempt read the parent's answer out
            // of the array on the reasoning that a parent is always earlier in the list, and it
            // is not: SkeletonPose walks the child-and-sibling tree with a stack, so a bone's
            // parent can perfectly well carry a HIGHER number. Bahlak's feathers are the case -
            // phys_skinned_head_feathers_1 is bone 19 and hangs off M_head_jntBnd, bone 22 - and
            // every bone that lost that race fell to the root instead. The root is the one bone
            // that does not follow the body, so the piece came out stretched between the monster
            // and the origin: a long black spike from his chest to the floor.
            // THE PIECE'S OWN ROOT IS THE SOCKET, on a socketed piece, whatever it is called.
            // Measured: the Fallen Knight's bicep fingers carry root_jntBnd AND L_shoulder_jntBnd
            // and both rest at (0,0,0) - the piece's origin - while the parent rests its shoulder
            // at (22.3,7,-157.9). Matching that root by NAME would send every vertex weighted to
            // it across to the monster's own root, which is the far end of him.
            onto[one] = (byte)(rooted ? 0 : socket);

            int at = one;
            for (var hops = 0; at >= 0 && hops <= own.Bones.Count; hops++)
            {
                bool grown = at < mine.Parents.Count && mine.Parents[at] >= 0;
                if (grown
                    && where.TryGetValue(own.Bones[at].Name, out int found)
                    && found <= byte.MaxValue)
                {
                    onto[one] = (byte)found;
                    shared++;
                    break;
                }

                at = at < mine.Parents.Count ? mine.Parents[at] : -1;
            }
        }

        // A "<root>" PIECE WITH NOTHING SHARED HAS NOWHERE TO GO, so it is left where it is -
        // Malgor's ship's wheel, and the one case here still unsettled. A SOCKETED piece always
        // has somewhere: its socket, which is where it used to go wholesale, so the map can only
        // improve on that and is always worth returning.
        if (rooted && shared == 0)
        {
            return null;
        }

        var bones = new byte[mesh.Bones.Length];
        for (var one = 0; one < bones.Length; one++)
        {
            byte said = mesh.Bones[one];
            bones[one] = said < onto.Length ? onto[said] : (byte)0;
        }

        return bones;
    }

    /// <summary>
    /// A piece that is a rigid prop rather than a skin: a <c>FixedMesh</c> naming a <c>.fmt</c>.
    /// </summary>
    /// <remarks>
    /// THE OTHER KIND OF ATTACHMENT, and the one the walk used to throw away. An .ao hangs either
    /// a SkinMesh - a .sm and then a .smd - or a FixedMesh, which names a .fmt and nothing else;
    /// asking only for the first meant every prop came back as "no SkinMesh" and was skipped.
    /// Reported from the live client: Malgor, the Nautilord carries a cannon the pane never drew,
    /// and his attachment's whole .ao is four lines with <c>fixed_mesh</c> in the middle of them.
    ///
    /// NO MANIFEST, BECAUSE A .fmt CARRIES ITS OWN MATERIALS - one .mat per shape, by name. That
    /// is the same list the .ao's own material lines take, so it goes down the path that is
    /// already there: <see cref="Dressed"/> matches a shape to a material by name first, and an
    /// empty manifest simply has nothing to add underneath it.
    /// </remarks>
    private static Part? Propped(
        Func<string, byte[]?> read,
        AnimatedObject ao,
        Matrix4x4 place,
        int bone,
        Paints paints,
        Mipmaps? fallback)
    {
        string path = Entryed(ao, PropBlock, PropEntry);
        if (path.Length == 0)
        {
            // An effect pack or a sound emitter. Ordinary, and nothing to draw.
            return null;
        }

        FixedMesh prop = Read(read, path, FixedMesh.Read);
        if (!prop.Ready)
        {
            return null;
        }

        Dress dress = Dressed(
            read, prop.Mesh, prop.Named, MeshManifest.None, fallback, string.Empty, paints);
        (byte[] bones, byte[] weights) = Bound(prop.Mesh.Positions.Length, bone);
        return new Part(prop.Mesh, bones, weights, place, dress.Skins);
    }

    /// <summary>
    /// One bone, all the weight - the skin a rigid piece gets.
    /// </summary>
    /// <remarks>
    /// The piece moves with its socket and nothing else, so every vertex names that bone four
    /// times over with the whole 255 on the first, which is what SkeletonPose.Move expects and
    /// what makes the piece follow an arm that lifts.
    /// </remarks>
    private static (byte[] Bones, byte[] Weights) Bound(int count, int bone)
    {
        var bones = new byte[count * 4];
        var weights = new byte[count * 4];
        var at = bone is >= 0 and <= byte.MaxValue ? (byte)bone : (byte)0;
        for (var one = 0; one < count; one++)
        {
            bones[one * 4] = at;
            bones[(one * 4) + 1] = at;
            bones[(one * 4) + 2] = at;
            bones[(one * 4) + 3] = at;
            weights[one * 4] = 255;
        }

        return (bones, weights);
    }

    /// <summary>The <c>skin</c> a SkinMesh block names, or empty where the file has none.</summary>
    private static string Skin(AnimatedObject ao) => Entryed(ao, Block, Entry);

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

    /// <summary>The block a rigid prop's file is named in, and the entry inside it.</summary>
    private const string PropBlock = "FixedMesh";

    /// <summary>The entry inside <see cref="PropBlock"/> that names the <c>.fmt</c>.</summary>
    private const string PropEntry = "fixed_mesh";

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
    /// <param name="named">
    /// Every material the files name, best first: the .ao's own, per shape, then the mesh's
    /// default. Tried in order until one carries a colour map - see the remark in Of.
    /// </param>
    private static (Mipmaps? Skin, string Why, string Material) Painted(
        Func<string, byte[]?> read, SkinnedMesh mesh, IReadOnlyList<string> named, Paints paints)
    {
        var tried = new List<string>();
        var reasons = new List<string>();

        foreach (string one in named)
        {
            string bare = MaterialFile.Bare(one);
            if (bare.Length == 0 || tried.Contains(bare, StringComparer.OrdinalIgnoreCase))
            {
                // The same file with a different :n selector is the same file. Reading it twice
                // would only report the same answer twice in a line somebody has to read.
                continue;
            }

            tried.Add(bare);
            (Mipmaps? skin, string why, _, _) = Colour(read, mesh, bare, paints);
            if (skin is not null)
            {
                return (skin, why, bare);
            }

            reasons.Add(why);
        }

        return tried.Count switch
        {
            0 => (null, "neither the .ao nor the .sm names a material", string.Empty),
            1 => (null, reasons[0], tried[0]),

            // SEVERAL, AND EACH ONE'S REASON, because "none of the three had a colour map" and
            // "the one material there is has none" are different findings and only the first
            // says the shape-by-shape walk was tried and came back empty.
            _ => (null, $"none of the {tried.Count} materials has a colour texture - {string.Join(" | ", reasons.Take(Some))}", tried[0]),
        };
    }

    /// <summary>
    /// Which texture each shape of the mesh wears, and the materials they came out of.
    /// </summary>
    /// <remarks>
    /// THE SHAPE'S NAME IS THE JOIN, and it was already being read - the .ao's SkinMesh block
    /// holds one child per shape, keyed by the shape's own name and valued with its material
    /// (<c>HipsShape = ".../Body.mat:0"</c>), and SkinnedMesh.Shapes carries the same names with
    /// the range of indices each covers. Nothing new has to be parsed to paint a monster part by
    /// part; the two sides simply were never put next to each other.
    ///
    /// THE MANIFEST IS THE SECOND SOURCE and only where the counts agree. A .sm lists materials
    /// in what looks like shape order, which is worth using and not worth trusting blind: a list
    /// of a different length is a list this does not understand, and indexing into it anyway
    /// would paint parts from whatever happened to line up.
    ///
    /// WHAT IS NOT NAMED KEEPS THE OLD ANSWER. A shape with no material of its own gets the
    /// single skin the whole mesh used to wear, so every monster whose .ao names one material
    /// draws exactly as it did and only the ones that name several change.
    ///
    /// READ ONCE PER MATERIAL, through a cache: nine shapes over two sheets is two decodes, not
    /// nine, and a boss's sheet is 2048 square.
    /// </remarks>
    private static Dress Dressed(
        Func<string, byte[]?> read,
        SkinnedMesh mesh,
        IReadOnlyList<(string Shape, string Material)> named,
        MeshManifest manifest,
        Mipmaps? fallback,
        string material,
        Paints paints)
    {
        if (mesh.Shapes.Count == 0)
        {
            return new Dress([], fallback is null ? [] : [material], [], false);
        }

        // ONCE, NOT PER SHAPE. The runs are walked to prove they add up before any of them is
        // used, so asking inside the loop would re-prove the same thing for every shape.
        IReadOnlyList<string> spread = manifest.Spread(mesh.Shapes.Count);
        IReadOnlyList<string> paths = [.. manifest.Materials.Select(one => one.Path)];

        var textures = new List<string>();
        var guessed = false;

        var skins = new Mipmaps?[mesh.Shapes.Count];
        var used = new List<string>();
        for (var shape = 0; shape < mesh.Shapes.Count; shape++)
        {
            // AS WRITTEN, colon and all. The number picks a graph INSIDE the material, one
            // per shape - see MaterialFile.Graphs - so two shapes naming the same file are
            // two different textures and the cache has to tell them apart by the whole
            // string rather than by the file.
            string wants = Wanted(mesh.Shapes[shape].Name, shape, mesh.Shapes.Count, named, spread, paths);
            if (wants.Length == 0)
            {
                skins[shape] = fallback;
                continue;
            }

            (Mipmaps? worn, _, string texture, bool said) = Colour(read, mesh, wants, paints);
            if (worn is not null)
            {
                guessed |= !said;
                if (!textures.Contains(texture, StringComparer.OrdinalIgnoreCase))
                {
                    textures.Add(texture);
                }
            }

            skins[shape] = worn ?? fallback;
            string file = MaterialFile.Bare(wants);
            if (worn is not null && !used.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                used.Add(file);
            }
        }

        if (fallback is not null && used.Count == 0 && material.Length > 0)
        {
            used.Add(material);
        }

        return new Dress(skins, used, textures, guessed) { Runs = spread.Count > 0 };
    }

    /// <summary>What the shapes ended up wearing, and how sure the walk is about it.</summary>
    /// <param name="Skins">One texture per shape, in the mesh's order.</param>
    /// <param name="Materials">The distinct materials they came out of.</param>
    /// <param name="Textures">The distinct colour textures, by path.</param>
    /// <param name="Guessed">Whether any was chosen with no slot name saying it is the colour map.</param>
    private readonly record struct Dress(
        IReadOnlyList<Mipmaps?> Skins,
        IReadOnlyList<string> Materials,
        IReadOnlyList<string> Textures,
        bool Guessed)
    {
        /// <summary>Whether the manifest's numbers added up and were used as runs of shapes.</summary>
        public bool Runs { get; init; }
    }

    /// <summary>
    /// The material a shape asks for: its own by name, else the manifest's by position.
    /// </summary>
    /// <remarks>
    /// BY NAME FIRST, because that is the join the files really make - the .ao's child is keyed
    /// by the shape's own name - and it is the only one that survives a mesh whose shapes are
    /// listed in another order.
    ///
    /// THEN BY POSITION, AND ONLY WHERE THERE IS EXACTLY ONE ENTRY PER SHAPE. Reported from the
    /// live client: Count Geonor's human form is fifteen shapes that came back painted from one
    /// texture and visibly in pieces, while his wolf form - whose materials this matched by
    /// name - was right. A list as long as the shape list is a list in shape order; a list of
    /// any other length is one whose order is not established here, and indexing into it anyway
    /// would paint parts from whatever happened to line up.
    ///
    /// THEN THE MANIFEST'S RUNS, which is the case the three above cannot reach: a monster whose
    /// .ao names nothing and whose manifest names FEWER materials than the mesh has shapes. That
    /// is not a rare shape - measured from the live client, it is Veynar (35 shapes, 3 materials),
    /// Connal (11 and 2) and Count Geonor's human form (15 and 7), all three of which came back
    /// painted from a single sheet and visibly wrong. The number on each material line is what
    /// spreads the short list over the long one, and <see cref="MeshManifest.Spread"/> only hands
    /// one back when the numbers add up to exactly the shape count.
    ///
    /// The manifest's bare list is the last of the four, on the same terms as the second.
    /// </remarks>
    private static string Wanted(
        string shape,
        int at,
        int shapes,
        IReadOnlyList<(string Shape, string Material)> named,
        IReadOnlyList<string> spread,
        IReadOnlyList<string> manifest)
    {
        foreach ((string called, string material) in named)
        {
            if (string.Equals(called, shape, StringComparison.OrdinalIgnoreCase))
            {
                return material;
            }
        }

        if (named.Count == shapes && at < named.Count)
        {
            return named[at].Material;
        }

        if (spread.Count == shapes && at < spread.Count)
        {
            return spread[at];
        }

        return manifest.Count == shapes && at < manifest.Count ? manifest[at] : string.Empty;
    }

    /// <summary>One material: its colour texture, or which way this one is missing it.</summary>
    private static (Mipmaps? Skin, string Why, string Texture, bool Named) Colour(
        Func<string, byte[]?> read, SkinnedMesh mesh, string material, Paints paints)
    {
        if (material.Length == 0)
        {
            return (null, "neither the .ao nor the .sm names a material", string.Empty, true);
        }

        string file = MaterialFile.Bare(material);
        if (!paints.Files.TryGetValue(file, out MaterialFile? paint))
        {
            paint = Read(read, file, MaterialFile.Read);
            paints.Files[file] = paint;
        }

        if (!paint.Ready)
        {
            return (null, $"the material did not read: {MaterialFile.Bare(material)}", string.Empty, true);
        }

        // THE NUMBER AFTER THE FILE PICKS THE GRAPH, which this file's own remark said all
        // along and nothing acted on: a monster built of parts names one material per shape
        // as Boss.mat:0, Boss.mat:1, and the colour map is per graph. Taking the first one
        // for every shape paints the head's sheet onto the cloak - patches of the wrong
        // colour, which is how Bahlak, Connal and Count Geonor were reported.
        (string texture, bool named) = paint.AlbedoOf(MaterialFile.SelectorOf(material));
        if (texture.Length == 0)
        {
            // WHAT IT DOES NAME, because this is the one reason here that is a QUESTION rather
            // than an answer. Every other line says what went wrong and where; this one said
            // only that the rule found nothing, and the rule - a slot whose name carries
            // Albedo, Colour or Color, or the first texture that is not a normal map - was
            // measured on the materials that happened to be looked at. A boss drawn in plain
            // ink is then indistinguishable from a boss whose slot is called something this
            // has never seen, and the only way to tell was to read the file with other tools.
            // So it prints the material and what is in it, which is exactly what deciding
            // between those two needs.
            return (
                null,
                $"the material names no colour texture - {Listed(MaterialFile.Bare(material), paint)}",
                string.Empty,
                true);
        }

        if (paints.Skins.TryGetValue(texture, out Mipmaps? kept))
        {
            // DECODED ONCE PER TEXTURE, not once per shape that points at it: two shapes of
            // one material name the same sheet through different graphs, and a boss's sheet
            // is sixteen megabytes of pixels before its levels are built.
            return kept is null
                ? (null, $"the texture did not read: {texture}", texture, named)
                : Fitted(kept, mesh, texture, named);
        }

        // THROUGH ReadRaw AND NOT A BARE READ. A texture in this game is one of three things and
        // only the third is a .dds as it stands: it may be a SIGNPOST - a star and the path of the
        // file that really holds it - or it may sit behind a compressed header. Decoding a plain
        // read handles the third and silently fails the other two, which shows as a monster with a
        // mesh and no colour and says nothing about why.
        if (GameArt.ReadRaw(read, texture) is not { Length: > 0 } bytes)
        {
            paints.Skins[texture] = null;
            return (null, $"the texture did not read: {texture}", texture, named);
        }

        // THE LEVELS ARE BUILT HERE, ON THE LOAD'S TASK, and not where the picture is drawn: a
        // boss's skin is 2048 square, and halving it down is a pass over sixteen megabytes that
        // belongs beside the bundle reads it follows rather than on the frame that first draws it.
        if (Mipmaps.Of(GameArt.Decode(bytes)) is not { } skin)
        {
            paints.Skins[texture] = null;
            return (null, $"the texture did not decode: {texture}", texture, named);
        }

        paints.Skins[texture] = skin;
        return Fitted(skin, mesh, texture, named);
    }

    /// <summary>A decoded texture with the one thing that can still be wrong about it.</summary>
    /// <remarks>
    /// THE COORDINATES ARE CHECKED SEPARATELY AND LAST, because a mesh can have a perfectly
    /// good texture and no way to look it up - the expected state of a bare body whose clothes
    /// are attached objects, and a different answer from "the file would not read".
    /// </remarks>
    private static (Mipmaps? Skin, string Why, string Texture, bool Named) Fitted(
        Mipmaps skin, SkinnedMesh mesh, string texture, bool named)
        => mesh.Coordinated
            ? (skin, string.Empty, texture, named)
            : (skin,
                "the mesh carries no texture coordinates, so the texture cannot be applied",
                texture,
                named);

    /// <summary>What has already been read, so nothing is read or decoded twice.</summary>
    private sealed class Paints
    {
        /// <summary>Material file by its path, with the selector taken off.</summary>
        public Dictionary<string, MaterialFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Decoded texture by its path, with null for one that would not read.</summary>
        public Dictionary<string, Mipmaps?> Skins { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A material named, with the slots and textures it holds - the evidence for "no colour".
    /// </summary>
    /// <remarks>
    /// SHORT ON PURPOSE. This goes in a line under the picture, so it carries the material's own
    /// file name rather than its path, the slot names as written, and the textures by file name
    /// only - enough to recognise a colour map filed under a slot nobody taught this about, and
    /// not so much that the line stops being readable. Three of each, because a material with
    /// more than three of either has already made the point.
    /// </remarks>
    private static string Listed(string material, MaterialFile paint)
    {
        string Few(IEnumerable<string> names)
        {
            string[] some = [.. names.Take(Some)];
            return some.Length == 0 ? "none" : string.Join(", ", some);
        }

        static string Named(string path) => path[(path.LastIndexOf('/') + 1)..];

        string file = material.Length > 0 ? Named(material) : "?";
        string slots = Few(paint.Slots.Select(one => $"{one.Key}={Named(one.Value)}"));
        string textures = Few(paint.Textures.Select(one => Named(one.Path)));

        return $"{file} names slots: {slots}; textures: {textures}";
    }

    /// <summary>How many slots and textures the line above names before it stops.</summary>
    private const int Some = 3;

    /// <summary>
    /// The first SkinMesh found, walking from the monster's own files outwards through extends.
    /// </summary>
    /// <remarks>
    /// BREADTH FIRST, so the monster's own files are all looked at before anything they extend.
    /// A depth-first walk would reach a base file before the monster's second .ao, and the nearer
    /// file is the one whose answer counts.
    /// </remarks>
    private static (IReadOnlyList<string> Meshes, IReadOnlyList<(string Shape, string Material)> Materials,
        string Skeleton, IReadOnlyList<string> Files)? Skinned(
        Func<string, byte[]?> read, IReadOnlyList<string> named)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        var materials = new List<(string Shape, string Material)>();

        // EVERY .ao THE WALK ACTUALLY READ, kept so the dressing can look for attachments in the
        // same files rather than walking them a second time. The walk stops once it has the skin
        // and the rig, so this is the monster's own files and whatever they extend up to there -
        // which is where attached_object entries live.
        var walked = new List<string>();

        foreach (string one in named)
        {
            queue.Enqueue((one, 0));
        }

        // THE SKIN AND THE SKELETON ARE FOUND SEPARATELY, each by the nearest file that names it,
        // and the walk carries on past the first until it has both or runs out of files. They are
        // usually in the same .ao; a monster whose base supplies the rig and whose own file only
        // swaps the skin is the case that stopping at the skin would leave unable to move.
        var meshes = new List<string>();
        string? skeleton = null;

        while (queue.Count > 0 && (meshes.Count == 0 || skeleton is null))
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

            walked.Add(path);

            if (meshes.Count == 0)
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

                        // THE MATERIAL SITS UNDER THE SKIN, ONE CHILD PER SHAPE, and this used
                        // to take the first on the reasoning that they name the same file with
                        // different indices. That holds for a monster cut from one sheet and
                        // NOT for one built out of parts: reported from the live client, Veynar
                        // the Frostbane draws in plain ink here and is plainly painted in the
                        // game, which is what a shape-0 material with no colour map in it looks
                        // like. All of them are kept now and tried in order - see Painted.
                        // KEY AND VALUE BOTH: the child's key is the SHAPE's own name, which
                        // is what joins a material to the part of the mesh it belongs on -
                        // SkinnedMesh.Shapes carries the same names. See Dressed.
                        foreach (AoEntry child in entry.Children)
                        {
                            if (child.Value.Contains(".mat", StringComparison.OrdinalIgnoreCase))
                            {
                                materials.Add((child.Key, child.Value));
                            }
                        }

                        // EVERY SKIN THE BLOCK NAMES, not the first. Reported from the live
                        // client: Zar Wali, the Bone Tyrant is a giant snake skeleton and the
                        // pane drew two floating arms, because his SkinMesh block names SEVEN
                        // manifests - GSSBArmA, ArmB, ArmC, Chest, Head, Neck, Tail - and this
                        // took the first and stopped. They are sections of one body on one rig,
                        // so they are read and joined rather than chosen between.
                        meshes.Add(entry.Value);
                    }

                    if (meshes.Count > 0)
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

        return meshes.Count == 0 ? null : (meshes, materials, skeleton ?? string.Empty, walked);
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
