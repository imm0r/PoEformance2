using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace PoEformance.Game.Files;

/// <summary>One shape's slice of the index buffer - which triangles belong to it.</summary>
/// <param name="Name">The shape's own name, as the file spells it: <c>SkullShape</c>.</param>
/// <param name="From">Where its indices start, counted in indices rather than triangles.</param>
/// <param name="Count">How many indices are its own. Always a multiple of three.</param>
public readonly record struct MeshShape(string Name, int From, int Count);

/// <summary>
/// What a mesh file's own headers said, kept so a dump can answer "why" rather than "how many".
/// </summary>
/// <remarks>
/// EVERY NUMBER HERE HAS COST TIME ONCE. The reader walks the file by these and nothing else, so
/// when a mesh comes out wrong the first question is always which of them the file actually
/// carried - and a screenshot of a drawn monster cannot answer it. They are read anyway; keeping
/// them costs one struct per mesh and saves a rebuild per question.
///
/// <see cref="NamesSaid"/> BESIDE <see cref="NamesRead"/> IS THE SELF-CHECK. The header says how
/// many bytes the name section takes, and the reader arrives at it after stepping over everything
/// between; if the two disagree, the step was wrong. That is the one invariant in this file that a
/// wrong offset cannot satisfy by accident, and it is how the four-byte error below was found.
/// </remarks>
/// <param name="Version">The file's version byte. Below three the geometry is not in a DOLm block.</param>
/// <param name="Corner">The DOLm <c>c0h</c> word, 1 to 4. It decides what follows the geometry.</param>
/// <param name="Details">How many levels of detail the block carries. Only the first is read.</param>
/// <param name="Format">The vertex format word - a set of flags, not a number.</param>
/// <param name="Stride">How many bytes one vertex works out to under that format.</param>
/// <param name="Shapes">How many shape names the outer header says there are.</param>
/// <param name="BlockShapes">How many shape extents the DOLm block carries.</param>
/// <param name="Triangles">Triangles in the first level of detail.</param>
/// <param name="Vertices">Vertices in the first level of detail.</param>
/// <param name="NamesSaid">Bytes of names the outer header says the name section holds.</param>
/// <param name="NamesRead">Bytes of names this reader found where it went looking for them.</param>
public readonly record struct MeshFacts(
    int Version,
    int Corner,
    int Details,
    uint Format,
    int Stride,
    int Shapes,
    int BlockShapes,
    int Triangles,
    int Vertices,
    int NamesSaid,
    int NamesRead);

/// <summary>One mesh going into <see cref="SkinnedMesh.Joined"/>.</summary>
/// <param name="Mesh">The geometry. Null or unready is left out rather than refused.</param>
/// <param name="Bones">
/// Four bone numbers per vertex to use INSTEAD of the mesh's own, already pointing at the rig
/// the joined mesh will be posed with - or null to keep what the file said. An attachment is
/// rigged to a skeleton of its own, so its numbers mean nothing against the parent's.
/// </param>
/// <param name="Weights">
/// Four weights per vertex to use instead of the mesh's own, matching <paramref name="Bones"/>,
/// or null to keep what the file said.
/// </param>
/// <param name="Place">
/// Where to put the mesh before joining it, or null to take its vertices as they are. A piece a
/// monster wears is modelled in ITS OWN space - measured: every one of Doryani's thirteen has a
/// box a few tens of units across sitting on the origin, with the left and right shoulder pieces
/// mirrored in x rather than standing apart - so it means nothing in the monster's space until
/// the socket bone's rest transform is on it.
/// </param>
public readonly record struct MeshJoin(
    SkinnedMesh? Mesh,
    byte[]? Bones = null,
    byte[]? Weights = null,
    Matrix4x4? Place = null);

/// <summary>
/// A monster's geometry, out of the game's own <c>.smd</c> files.
/// </summary>
/// <remarks>
/// THE END OF THE CHAIN THAT STARTS AT A MONSTER ROW. MonsterVarieties hands out .ao paths, an .ao
/// names a .sm through SkinMesh, a .sm names this - and this is where the triangles are. Nothing
/// above it holds a single vertex.
///
/// READ FROM A SPEC AND THEN CHECKED AGAINST THE GAME, which is the only way this was ever going
/// to be trustworthy. The layout comes from poe_data_tools' smd_spec and dolm_spec diagrams; what
/// makes it believable is that a real file - BasicSkeleton's rig, 122101 bytes - accounts for
/// every one of its bytes under it, and that four things inside AGREE WITH SOURCES OUTSIDE IT:
///
///   - the vertex positions fill the bounding box in the header exactly, and that same box is
///     written a third time in the .sm text file beside it;
///   - the fifteen shape names are the fifteen SkinMesh entries the .ao lists;
///   - every index is inside the vertex count, and the shape extents cover the index buffer with
///     no gap and no overlap - 12966 indices for 4322 triangles;
///   - the skin weights on a vertex sum to 255.
///
/// A WRONG VERTEX STRIDE PASSES NONE OF THOSE. It scatters positions outside the box and leaves
/// the last vertex half-read, which is exactly the failure a byte-count check alone would miss.
///
/// THE POSITIONS ARE BIND POSE, in model space, which is what makes a still picture possible
/// without reading the skeleton: the bones and weights are kept for later rather than used here.
/// See <see cref="Bones"/> and <see cref="Weights"/>.
///
/// ONLY THE FIRST LOD IS KEPT, but every one of them is STEPPED OVER - a distinction that cost a
/// fix. A coarser copy of the same mesh is useless in a portrait, and skipping it is not the same
/// as pretending it is not in the file: the counts for all of them are written as one table before
/// the first mesh, and the shape names sit past the last. A reader that stops after the first
/// walks into the second's counts.
///
/// WHAT FOLLOWS THE GEOMETRY IS CONDITIONAL - see <see cref="Trailing"/>. Taking the four bytes
/// after it unconditionally is what put this reader four bytes into the name table on every mesh
/// whose <c>c0h</c> is not four, and that was found only because a monster's shape names came back
/// nearly right rather than obviously wrong.
/// </remarks>
public sealed class SkinnedMesh
{
    /// <summary>Nothing read - a missing file, or one that is not a mesh.</summary>
    public static SkinnedMesh None { get; } = new();

    private SkinnedMesh()
    {
        Positions = [];
        Normals = [];
        Coordinates = [];
        Bones = [];
        Weights = [];
        Indices = [];
        Shapes = [];
    }

    /// <summary>Every vertex's place in model space, in bind pose.</summary>
    public Vector3[] Positions { get; private init; }

    /// <summary>Every vertex's normal, unpacked from the signed bytes the file stores.</summary>
    public Vector3[] Normals { get; private init; }

    /// <summary>Every vertex's texture coordinate, for when a material is put on it.</summary>
    public Vector2[] Coordinates { get; private init; }

    /// <summary>Four bone numbers per vertex, flat. Kept for a later pose, unused for a still.</summary>
    public byte[] Bones { get; private init; }

    /// <summary>Four weights per vertex, flat, summing to 255.</summary>
    public byte[] Weights { get; private init; }

    /// <summary>Three indices per triangle, into the vertex arrays.</summary>
    public int[] Indices { get; private init; }

    /// <summary>Which stretch of the index buffer belongs to which named shape.</summary>
    public IReadOnlyList<MeshShape> Shapes { get; private init; }

    /// <summary>The box the file says the mesh lives in: min then max, per axis.</summary>
    public Vector3 Least { get; private init; }

    /// <summary>The far corner of that box.</summary>
    public Vector3 Most { get; private init; }

    /// <summary>What the file's headers said, for a dump. Default where the mesh was assembled.</summary>
    public MeshFacts Facts { get; private init; }

    /// <summary>Why nothing was read, or empty where something was.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether there is anything to draw.</summary>
    public bool Ready => Indices.Length >= 3 && Positions.Length > 0;

    /// <summary>How many triangles the mesh holds.</summary>
    public int Triangles => Indices.Length / 3;

    /// <summary>
    /// Whether the mesh carries texture coordinates worth looking anything up with.
    /// </summary>
    /// <remarks>
    /// ALL-ZERO IS THE SHAPE THIS TAKES, not an absent array: a mesh whose vertex format omits
    /// coordinates still gets one of them per vertex, every entry (0,0). Painting from that gives
    /// every triangle the SAME corner texel - a monster in one flat colour lifted from an
    /// arbitrary place, which looks deliberate and is not.
    ///
    /// IT LIVES HERE RATHER THAN IN THE RENDERER because it is a fact about the mesh, and because
    /// two callers now need it: the renderer, to decide whether to sample, and the model walk, to
    /// say WHY a monster came out unpainted. Answering that was guesswork from a screenshot while
    /// only the renderer knew.
    ///
    /// Walked rather than cached: it is asked once per drawing, not per pixel, and a cached flag
    /// on a type built three different ways is a field that can disagree with the array beside it.
    /// </remarks>
    public bool Coordinated
    {
        get
        {
            foreach (Vector2 one in Coordinates)
            {
                if (one != Vector2.Zero)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A mesh assembled from geometry rather than read from a file.
    /// </summary>
    /// <remarks>
    /// FOR TESTING THE RENDERER, which needs shapes whose answers are known in advance - a post
    /// four times longer than it is wide, two quads at different depths. Those cannot be carved
    /// out of a real monster, and building a whole .smd byte by byte to describe one would test
    /// the reader rather than the drawing.
    ///
    /// NO SKIN, because nothing that uses this needs one: the bones and weights come back empty
    /// and a still picture does not read them.
    /// </remarks>
    /// <param name="positions">The vertices.</param>
    /// <param name="normals">One per position.</param>
    /// <param name="indices">Three per triangle, each inside the vertex count.</param>
    /// <param name="least">The low corner of the box the camera is placed from.</param>
    /// <param name="most">The high corner.</param>
    /// <param name="coordinates">Texture coordinates, or null for a mesh with no skin.</param>
    /// <param name="shapes">
    /// The parts the indices are divided into, or null for one shape over all of them. A real
    /// monster is built of several and each wears its own material, so a renderer that paints
    /// them apart has to be testable against a mesh that has more than one.
    /// </param>
    public static SkinnedMesh Of(
        Vector3[] positions,
        Vector3[] normals,
        int[] indices,
        Vector3 least,
        Vector3 most,
        Vector2[]? coordinates = null,
        IReadOnlyList<MeshShape>? shapes = null)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(indices);

        if (normals.Length != positions.Length)
        {
            throw new ArgumentException("one normal per position", nameof(normals));
        }

        foreach (int one in indices)
        {
            if (one < 0 || one >= positions.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), "an index points at no vertex");
            }
        }

        return new SkinnedMesh
        {
            Positions = positions,
            Normals = normals,
            Coordinates = coordinates ?? new Vector2[positions.Length],
            Bones = [],
            Weights = [],
            Indices = indices,
            Shapes = shapes is { Count: > 0 } ? shapes : [new MeshShape("shape 0", 0, indices.Length)],
            Least = least,
            Most = most,
        };
    }

    /// <summary>
    /// Several meshes as one, for a monster whose clothes are separate files.
    /// </summary>
    /// <remarks>
    /// WHY MERGE RATHER THAN DRAW EACH IN TURN. A monster's skirt, belt and necklace are
    /// <c>attached_object</c> entries naming their own .ao, mesh and rig - Doryani has nine, and
    /// the model pane drew him bare-legged for as long as it read the body alone. Drawing them
    /// would otherwise mean taking <see cref="MeshPicture"/> apart: it clears the canvas, fits
    /// the camera and fills the depth buffer per call, all of which must happen ONCE across the
    /// parts. Merging leaves the renderer, the per-shape palette and the pose untouched, and
    /// everything downstream keeps working by construction rather than by a second code path.
    ///
    /// THE BONES ARE REMAPPED BY THE CALLER, not here. An attachment is rigged to a skeleton of
    /// its own whose bones carry the PARENT'S names - <c>spine_1_jntBnd</c>, <c>chest_jntBnd</c> -
    /// so a vertex's bone number means something different in each file. Handing the remapped
    /// array in keeps this function about geometry and puts the naming question where the rig is
    /// known; see MonsterModels.
    ///
    /// THE BOX IS THE UNION, which is what puts the camera round the whole dressed monster
    /// rather than round the body with its skirt out of frame.
    ///
    /// A part with no bones of its own gets zeroes, and zero weights with them, so it holds still
    /// under a pose instead of collapsing onto whatever bone 0 happens to be.
    /// </remarks>
    /// <param name="parts">The meshes to join, the body first. Empty gives <see cref="None"/>.</param>
    public static SkinnedMesh Joined(IReadOnlyList<MeshJoin> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var usable = new List<MeshJoin>(parts.Count);
        var points = 0;
        var indices = 0;
        var shapes = 0;
        foreach (MeshJoin part in parts)
        {
            if (part.Mesh is not { Ready: true } mesh)
            {
                continue;
            }

            usable.Add(part);
            points += mesh.Positions.Length;
            indices += mesh.Indices.Length;
            shapes += mesh.Shapes.Count;
        }

        if (usable.Count == 0)
        {
            return None;
        }

        if (usable.Count == 1 && usable[0].Bones is null && usable[0].Place is null)
        {
            return usable[0].Mesh!;
        }

        var positions = new Vector3[points];
        var normals = new Vector3[points];
        var coordinates = new Vector2[points];
        var bones = new byte[points * 4];
        var weights = new byte[points * 4];
        var joined = new int[indices];
        var named = new List<MeshShape>(shapes);

        Vector3 least = usable[0].Mesh!.Least;
        Vector3 most = usable[0].Mesh!.Most;

        var at = 0;
        var wrote = 0;
        foreach (MeshJoin part in usable)
        {
            SkinnedMesh mesh = part.Mesh!;
            int count = mesh.Positions.Length;

            if (part.Place is { } put)
            {
                for (var one = 0; one < count; one++)
                {
                    positions[at + one] = Vector3.Transform(mesh.Positions[one], put);

                    // TransformNormal and not Transform: a normal is a direction, and carrying
                    // the translation into it would tip every face by however far the bone sits
                    // from the origin - which at a shoulder is the whole model.
                    normals[at + one] = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[one], put));
                }
            }
            else
            {
                mesh.Positions.CopyTo(positions.AsSpan(at));
                mesh.Normals.CopyTo(normals.AsSpan(at));
            }

            mesh.Coordinates.CopyTo(coordinates.AsSpan(at));

            // FOUR PER VERTEX, and only where the file really carried them: a mesh read without
            // bones has empty arrays rather than short ones, and copying a short span here would
            // be the crash a still picture never needed to risk.
            byte[]? which = part.Bones ?? (mesh.Bones.Length == count * 4 ? mesh.Bones : null);
            byte[]? much = part.Weights ?? (mesh.Weights.Length == count * 4 ? mesh.Weights : null);
            if (which is { } sure && much is { } held
                && sure.Length == count * 4 && held.Length == count * 4)
            {
                sure.CopyTo(bones.AsSpan(at * 4));
                held.CopyTo(weights.AsSpan(at * 4));
            }

            foreach (MeshShape shape in mesh.Shapes)
            {
                named.Add(shape with { From = shape.From + wrote });
            }

            for (var one = 0; one < mesh.Indices.Length; one++)
            {
                joined[wrote + one] = mesh.Indices[one] + at;
            }

            // THE BOX GOES THROUGH THE SAME TRANSFORM, by its eight corners rather than its two:
            // a rotated box's min and max are not the transforms of the old min and max, and a
            // box taken that way is smaller than the geometry inside it - which is a camera
            // framing a monster with its skirt cut off.
            (Vector3 low, Vector3 high) = part.Place is { } moved
                ? Corners(mesh.Least, mesh.Most, moved)
                : (mesh.Least, mesh.Most);

            least = Vector3.Min(least, low);
            most = Vector3.Max(most, high);
            at += count;
            wrote += mesh.Indices.Length;
        }

        return new SkinnedMesh
        {
            Positions = positions,
            Normals = normals,
            Coordinates = coordinates,
            Bones = bones,
            Weights = weights,
            Indices = joined,
            Shapes = named,
            Least = least,
            Most = most,
        };
    }

    /// <summary>A box through a transform, by its eight corners - the only way that holds under rotation.</summary>
    private static (Vector3 Least, Vector3 Most) Corners(Vector3 least, Vector3 most, Matrix4x4 through)
    {
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        for (var one = 0; one < 8; one++)
        {
            var corner = new Vector3(
                (one & 1) == 0 ? least.X : most.X,
                (one & 2) == 0 ? least.Y : most.Y,
                (one & 4) == 0 ? least.Z : most.Z);

            Vector3 put = Vector3.Transform(corner, through);
            low = Vector3.Min(low, put);
            high = Vector3.Max(high, put);
        }

        return (low, high);
    }

    /// <summary>Reads one out of an open install, by the path a <c>.sm</c> named.</summary>
    public static SkinnedMesh Read(GameFiles? files, string? path)
        => files is null || string.IsNullOrWhiteSpace(path)
            ? Failed("no install, or no path")
            : Read(files.Read(path.Replace('\\', '/').Trim()));

    /// <summary>
    /// Reads a file's bytes. A file this cannot make sense of comes back with a reason, not a throw.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS, because the caller is a window drawing a monster somebody clicked on, and a
    /// mesh that will not read is a picture that does not appear rather than a tool that closes.
    /// Every read is bounds-checked against the file's own length first - a count field in a
    /// binary format is a number from a file, which is to say a number from anywhere.
    /// </remarks>
    public static SkinnedMesh Read(byte[]? content)
    {
        if (content is not { Length: > HeaderBytes })
        {
            return Failed("nothing to read");
        }

        try
        {
            return Parse(content);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException
            or IndexOutOfRangeException or OverflowException or DecoderFallbackException)
        {
            return Failed($"the file does not read as a mesh: {exception.Message}");
        }
    }

    /// <summary>The smallest a file can be and still hold a header and a DOLm magic.</summary>
    private const int HeaderBytes = 32 + 4;

    /// <summary>What the geometry block starts with.</summary>
    private static ReadOnlySpan<byte> Magic => "DOLm"u8;

    /// <summary>
    /// How many bytes one vertex takes, and where each part of it sits.
    /// </summary>
    /// <remarks>
    /// THE FORMAT WORD IS A SET OF FLAGS and the parts appear in a fixed order, so the stride is
    /// a sum rather than a constant. On BasicSkeleton it is 0x23C - bits 2, 3, 4, 5 and 9 - which
    /// works out to 32 bytes: position, normal, tangent, one texture coordinate, bones, weights.
    ///
    /// BITS 4, 5 AND 9 ARE SET AND ADD NOTHING, which is not an assumption: with them counted as
    /// zero-width the file's every byte is accounted for, and with any of them carrying even four
    /// bytes the vertex buffer overruns the name section that follows it. The spec marks them
    /// unknown; this says what they measure out to, and the measurement is the test.
    /// </remarks>
    private readonly record struct Shape(int Stride, int Normal, int Coordinate, int Bones)
    {
        public static Shape Of(uint format)
        {
            var at = 12;
            int normal = at;

            at += 4;  // Normal, four signed bytes.
            at += 4;  // Tangent, the same again - read past rather than kept; a still needs neither.

            int coordinate = -1;
            if ((format >> 3 & 1) == 1)
            {
                coordinate = at;
                at += 4;
            }

            int bones = -1;
            if ((format >> 2 & 1) == 1)
            {
                bones = at;
                at += 8;
            }

            if ((format >> 1 & 1) == 1)
            {
                at += 4;
            }

            if ((format & 1) == 1)
            {
                at += 4;
            }

            if ((format >> 6 & 1) == 1)
            {
                at += 4;
            }

            return new Shape(at, normal, coordinate, bones);
        }
    }

    private static SkinnedMesh Parse(byte[] raw)
    {
        ReadOnlySpan<byte> file = raw;
        var at = 0;

        byte version = file[at++];
        at++;                                       // The outer vertex format; DOLm carries its own.
        int shapes = Read16(file, ref at);
        var names = (int)Read32(file, ref at);      // How many bytes the name section takes.

        var least = new Vector3(Float(file, at), Float(file, at + 8), Float(file, at + 16));
        var most = new Vector3(Float(file, at + 4), Float(file, at + 12), Float(file, at + 20));
        at += 24;

        // VERSIONS BELOW THREE PUT THE GEOMETRY HERE RATHER THAN IN A DOLm, and nothing seen so
        // far is one. Refused by name instead of read by guess: an older layout read as this one
        // would produce a mesh-shaped answer out of the wrong bytes.
        if (version < 3)
        {
            return Failed($"version {version.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + " puts its geometry outside a DOLm block, which this does not read");
        }

        if (!file[at..].StartsWith(Magic))
        {
            return Failed("no DOLm block where the geometry should be");
        }

        at += 4;
        int corner = Read16(file, ref at);           // The "c0h" word - 1 to 4, meaning unknown.
        int lods = file[at++];
        int blockShapes = Read16(file, ref at);
        uint format = Read32(file, ref at);

        if (lods == 0)
        {
            return Failed("the mesh has no level of detail to read");
        }

        // EVERY LEVEL OF DETAIL'S COUNTS COME FIRST, AS ONE TABLE, and only then the meshes
        // themselves. A reader that takes one pair and walks straight into the geometry reads a
        // two-detail file's SECOND pair as the first shape's extents, and everything after that
        // is rubbish - so the table is read whole even though only the first mesh is kept.
        if (at + (lods * 8) > file.Length)
        {
            return Failed("the mesh says it is bigger than the file that holds it");
        }

        // KEPT UNSIGNED UNTIL THEY HAVE BEEN BOUNDED, which is not fussiness: these are numbers
        // out of a file, and casting four billion to an int gives a NEGATIVE count that sails
        // past a "> 0" check into a different branch entirely. The size check below is the one
        // that has to catch it, so nothing is narrowed before it runs.
        var counts = new (uint Triangles, uint Vertices)[lods];
        for (var one = 0; one < lods; one++)
        {
            counts[one] = (Read32(file, ref at), Read32(file, ref at));
        }

        (uint triangles, uint vertices) = counts[0];
        if (triangles == 0 || vertices == 0)
        {
            return Failed("the mesh is empty");
        }

        Shape shape = Shape.Of(format);
        int width = Width(vertices);

        // BOUNDS FIRST, ARITHMETIC SECOND. Counts out of the file multiply into the sizes below,
        // and a file that lies about any of them would otherwise be read past its end. Everything
        // up to the name table is measured here, coarser details and the blocks after them
        // included, so the one check covers every step this takes.
        long needed = at;
        foreach ((uint faces_, uint points_) in counts)
        {
            needed += ((long)blockShapes * 8)
                + ((long)faces_ * 3 * Width(points_))
                + ((long)points_ * shape.Stride);
        }

        needed += Trailing(format, corner, blockShapes);
        if (needed > file.Length)
        {
            return Failed("the mesh says it is bigger than the file that holds it");
        }

        // Safe to narrow now: the size check above proved both fit inside the file.
        var faces = (int)triangles;
        var points = (int)vertices;

        var extents = new (int From, int Count)[blockShapes];
        for (var one = 0; one < blockShapes; one++)
        {
            extents[one] = ((int)Read32(file, ref at), (int)Read32(file, ref at));
        }

        var indices = new int[faces * 3];
        for (var one = 0; one < indices.Length; one++)
        {
            indices[one] = width == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(file[(at + (one * 2))..])
                : (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(at + (one * 4))..]);

            if (indices[one] >= points)
            {
                return Failed("the mesh points at a vertex it does not have");
            }
        }

        at += indices.Length * width;

        var positions = new Vector3[points];
        var normals = new Vector3[points];
        var coordinates = new Vector2[points];
        var bones = new byte[points * 4];
        var weights = new byte[points * 4];

        for (var one = 0; one < points; one++)
        {
            int start = at + (one * shape.Stride);

            positions[one] = new Vector3(Float(file, start), Float(file, start + 4), Float(file, start + 8));
            normals[one] = Direction(file, start + shape.Normal);

            if (shape.Coordinate >= 0)
            {
                coordinates[one] = new Vector2(
                    (float)BitConverter.UInt16BitsToHalf(
                        BinaryPrimitives.ReadUInt16LittleEndian(file[(start + shape.Coordinate)..])),
                    (float)BitConverter.UInt16BitsToHalf(
                        BinaryPrimitives.ReadUInt16LittleEndian(file[(start + shape.Coordinate + 2)..])));
            }

            if (shape.Bones >= 0)
            {
                file.Slice(start + shape.Bones, 4).CopyTo(bones.AsSpan(one * 4));
                file.Slice(start + shape.Bones + 4, 4).CopyTo(weights.AsSpan(one * 4));
            }
        }

        at += points * shape.Stride;

        // THE COARSER DETAILS ARE STEPPED OVER, not read: each is the same mesh again with fewer
        // triangles, useful at a distance and never in a portrait. They still have to be counted,
        // because the shape names sit past the LAST of them.
        long past = at;
        for (var one = 1; one < lods; one++)
        {
            (uint faces_, uint points_) = counts[one];
            past += ((long)blockShapes * 8)
                + ((long)faces_ * 3 * Width(points_))
                + ((long)points_ * shape.Stride);
        }

        past += Trailing(format, corner, blockShapes);
        at = (int)past;                             // Bounded by the size check above.

        MeshShape[] named = Named(file, at, shapes, extents, indices.Length, out int read);

        return new SkinnedMesh
        {
            Positions = positions,
            Normals = normals,
            Coordinates = coordinates,
            Bones = bones,
            Weights = weights,
            Indices = indices,
            Shapes = named,
            Least = least,
            Most = most,
            Facts = new MeshFacts(
                version, corner, lods, format, shape.Stride, shapes, blockShapes,
                faces, points, names, read),
        };
    }

    /// <summary>
    /// What DOLm writes between the last level of detail and the shape names.
    /// </summary>
    /// <remarks>
    /// THE FOUR BYTES ARE NOT ALWAYS THERE, and taking them unconditionally is what put this
    /// reader four bytes into the name table on every mesh whose <c>c0h</c> is not four. The
    /// symptom was a monster whose shapes came back with names that were nearly right:
    /// <c>athers_headpieceSh</c> for <c>feathers_headpieceShape</c>, because the first length word
    /// read was the SECOND shape's, and each name after that drifted further. Nothing else broke -
    /// the geometry was already in hand - so it looked like a wrong socket rather than a wrong
    /// step, and was chased as one.
    ///
    /// THE SHAPE OF IT COMES FROM poe_data_tools' dolm parser, which gates all three blocks:
    /// thirty-six bytes per shape when the format's seventh bit is set, four more per shape when
    /// that bit is set AND <c>c0h</c> is two, and the four bytes only when <c>c0h</c> is four.
    /// </remarks>
    private static long Trailing(uint format, int corner, int blockShapes)
    {
        long past = 0;
        if ((format >> 6 & 1) == 1)
        {
            past += (long)blockShapes * 36;
            if (corner == 2)
            {
                past += (long)blockShapes * 4;
            }
        }

        return corner == 4 ? past + 4 : past;
    }

    /// <summary>How wide one index is: two bytes while every vertex number fits in them.</summary>
    private static int Width(uint vertices) => vertices < 0x10000 ? 2 : 4;

    /// <summary>
    /// The shape names, which sit past the geometry, paired with the extents read before it.
    /// </summary>
    /// <remarks>
    /// A NAME THAT WILL NOT READ COSTS ITS OWN NAME AND NOTHING ELSE. The geometry is already in
    /// hand by the time this runs, and a mesh with fifteen shapes called "shape 3" still draws -
    /// so this returns what it has rather than failing the whole read.
    /// </remarks>
    private static MeshShape[] Named(
        ReadOnlySpan<byte> file,
        int at,
        int shapes,
        (int From, int Count)[] extents,
        int indices,
        out int spent)
    {
        var named = new MeshShape[extents.Length];
        int began = at;
        var lengths = new int[shapes];
        var read = true;
        for (var one = 0; one < shapes && read; one++)
        {
            if (at + 4 > file.Length)
            {
                read = false;
                break;
            }

            lengths[one] = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
            at += 4;
        }

        for (var one = 0; one < named.Length; one++)
        {
            (int from, int count) = extents[one];
            string name = $"shape {one.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            if (read && one < lengths.Length && lengths[one] > 0 && at + lengths[one] <= file.Length)
            {
                name = Encoding.Unicode.GetString(file.Slice(at, lengths[one]));
                at += lengths[one];
            }

            // Clamped rather than trusted: the extents come out of the same file as everything
            // else, and a shape reaching past the index buffer would be read as triangles that
            // are not there.
            from = Math.Clamp(from, 0, indices);
            named[one] = new MeshShape(name, from, Math.Clamp(count, 0, indices - from));
        }

        // How many bytes of NAMES were taken, the length words left out - which is what the outer
        // header counts, so a caller can hold the two up against each other.
        spent = read ? at - began - (shapes * 4) : 0;
        return named;
    }

    /// <summary>A normal, out of the four signed bytes the file packs it into.</summary>
    /// <remarks>
    /// THE FOURTH BYTE IS NOT PART OF THE DIRECTION - it is the sign the tangent frame needs, and
    /// a reader that treated the four as a vector would tilt every normal it read. Normalised
    /// here because a byte-packed direction is only approximately unit length.
    /// </remarks>
    private static Vector3 Direction(ReadOnlySpan<byte> file, int at)
    {
        var said = new Vector3(
            (sbyte)file[at] / 127f,
            (sbyte)file[at + 1] / 127f,
            (sbyte)file[at + 2] / 127f);

        return said.LengthSquared() > 0.0001f ? Vector3.Normalize(said) : Vector3.UnitZ;
    }

    private static float Float(ReadOnlySpan<byte> file, int at)
        => BinaryPrimitives.ReadSingleLittleEndian(file[at..]);

    private static int Read16(ReadOnlySpan<byte> file, ref int at)
    {
        int said = BinaryPrimitives.ReadUInt16LittleEndian(file[at..]);
        at += 2;
        return said;
    }

    private static uint Read32(ReadOnlySpan<byte> file, ref int at)
    {
        uint said = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
        at += 4;
        return said;
    }

    private static SkinnedMesh Failed(string why) => new() { Why = why };
}
