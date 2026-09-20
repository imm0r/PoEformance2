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

        return new MonsterModel(mesh, skin, manifest.Geometry, material, string.Empty, paint)
        {
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
    private static (string Mesh, IReadOnlyList<(string Shape, string Material)> Materials, string Skeleton)? Skinned(
        Func<string, byte[]?> read, IReadOnlyList<string> named)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        var materials = new List<(string Shape, string Material)>();

        foreach (string one in named)
        {
            queue.Enqueue((one, 0));
        }

        // THE SKIN AND THE SKELETON ARE FOUND SEPARATELY, each by the nearest file that names it,
        // and the walk carries on past the first until it has both or runs out of files. They are
        // usually in the same .ao; a monster whose base supplies the rig and whose own file only
        // swaps the skin is the case that stopping at the skin would leave unable to move.
        string? mesh = null;
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

        return mesh is null ? null : (mesh, materials, skeleton ?? string.Empty);
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
