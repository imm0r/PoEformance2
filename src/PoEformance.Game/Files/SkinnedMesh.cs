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
/// ONLY THE FIRST LOD IS READ. The files seen so far carry one, and a second would be a coarser
/// copy of the same mesh - useful for drawing at a distance and not for a portrait.
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
        at += 4;                                    // How many bytes the name section takes.

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
        at += 2;                                    // The "c0h" word - 1 to 4, meaning unknown.
        int lods = file[at++];
        int blockShapes = Read16(file, ref at);
        uint format = Read32(file, ref at);

        if (lods == 0)
        {
            return Failed("the mesh has no level of detail to read");
        }

        // KEPT UNSIGNED UNTIL THEY HAVE BEEN BOUNDED, which is not fussiness: these are numbers
        // out of a file, and casting four billion to an int gives a NEGATIVE count that sails
        // past a "> 0" check into a different branch entirely. The size check below is the one
        // that has to catch it, so nothing is narrowed before it runs.
        uint triangles = Read32(file, ref at);
        uint vertices = Read32(file, ref at);
        if (triangles == 0 || vertices == 0)
        {
            return Failed("the mesh is empty");
        }

        Shape shape = Shape.Of(format);
        int width = vertices <= ushort.MaxValue ? 2 : 4;

        // BOUNDS FIRST, ARITHMETIC SECOND. Three counts out of the file multiply into the sizes
        // below, and a file that lies about any of them would otherwise be read past its end.
        long needed = (long)at
            + ((long)blockShapes * 8)
            + ((long)triangles * 3 * width)
            + ((long)vertices * shape.Stride);
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

        return new SkinnedMesh
        {
            Positions = positions,
            Normals = normals,
            Coordinates = coordinates,
            Bones = bones,
            Weights = weights,
            Indices = indices,
            Shapes = Named(file, at, shapes, blockShapes, extents, indices.Length),
            Least = least,
            Most = most,
        };
    }

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
        int blockShapes,
        (int From, int Count)[] extents,
        int indices)
    {
        var named = new MeshShape[extents.Length];

        // The four bytes between the geometry and the names, which the spec ties to the "c0h"
        // word being four. Stepped over by measurement: on the file this was checked against they
        // are what makes the name lengths land where the header says the name section begins.
        at += 4;

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
