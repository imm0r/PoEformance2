using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The mesh readers - the <c>.sm</c> manifest and the <c>.smd</c> geometry behind it.
/// </summary>
/// <remarks>
/// NO REAL MESH IS COMMITTED HERE, so these tests settle whether the readers implement the format
/// as the poe_data_tools smd_spec and dolm_spec diagrams describe it. What settled that the format
/// was read CORRECTLY was a real file - BasicSkeleton's rig, 122101 bytes, fetched from the game's
/// own bundles - and four checks against sources outside it:
///
///   - 4322 triangles and 2986 vertices at a 32-byte stride account for every byte of the file;
///   - the vertex positions fill the header's bounding box exactly, and the .sm text file beside
///     it writes that same box a third time;
///   - the fifteen shape names are the fifteen SkinMesh entries the monster's .ao lists;
///   - the shape extents cover the index buffer with no gap and no overlap, 12966 for 4322
///     triangles, and the skin weights sum to 255 on all but 20 vertices, which are 254 or 256.
///
/// THE INVARIANTS ARE WHAT IS TESTED HERE, because those are what a wrong stride breaks. A
/// synthetic file proves the reader agrees with its own writer and nothing more, so it is built to
/// carry the same properties the real one has and they are asserted the same way.
/// </remarks>
public class SkinnedMeshTests
{
    /// <summary>The manifest as the game writes it - the real BasicSkeleton file's shape.</summary>
    private const string Manifest = """
        version 6
        SkinnedMeshData "Art/Models/MONSTERS/BasicSkeleton/rig_b70e7afb.smd"
        Materials 1
        	"Art/Textures/Monsters/KatarinaSkeleton/SkeletonVarc.mat" 15
        BoundingBox -77.204 -14.5222 -189.211 77.204 14.4987 -0.4236
        BoneGroups 3
        	"grp_head" 2 "head_jntBnd" "aux_head_jntBnd"
        	"grp_neck" 2 "neck_jntBnd" "head_jntBnd"
        	"grp_chest" 2 "chest_jntBnd" "neck_jntBnd"
        """;

    [Fact]
    public void TheManifestSaysWhereTheGeometryIs()
    {
        MeshManifest said = MeshManifest.Parse(Manifest);

        Assert.True(said.Ready);
        Assert.Equal(6, said.Version);
        Assert.Equal("Art/Models/MONSTERS/BasicSkeleton/rig_b70e7afb.smd", said.Geometry);
        Assert.Equal(
            "Art/Textures/Monsters/KatarinaSkeleton/SkeletonVarc.mat",
            Assert.Single(said.Materials));
        Assert.Equal(["grp_head", "grp_neck", "grp_chest"], said.Bones);
    }

    /// <summary>
    /// The manifest's box is the low corner then the high one - NOT the order the .smd uses.
    /// </summary>
    /// <remarks>
    /// THE TWO FILES HOLD THE SAME SIX NUMBERS IN DIFFERENT ORDERS, which is the sort of thing
    /// that produces a mesh rather than an error: this writes min x, min y, min z, max x, max y,
    /// max z while the binary header pairs them per axis. Read as each other, a skeleton comes out
    /// 154 units tall and 189 wide instead of the other way round.
    /// </remarks>
    [Fact]
    public void TheManifestsBoxIsTwoCornersAndNotPerAxisPairs()
    {
        MeshManifest said = MeshManifest.Parse(Manifest);

        Assert.Equal(new Vector3(-77.204f, -14.5222f, -189.211f), said.Least);
        Assert.Equal(new Vector3(77.204f, 14.4987f, -0.4236f), said.Most);
    }

    /// <summary>An .ao is not a .sm, and pointing one reader at the other yields nothing.</summary>
    /// <remarks>
    /// BOTH ARE UTF-16 TEXT OUT OF THE SAME GAME and they look alike at a glance. Failing loudly
    /// is the wanted behaviour; a reader that found half a manifest in an .ao would be worse than
    /// one that found none.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("version 6")]
    [InlineData("version 3\nAnimationController\n{\n\tmetadata = \"a/b.amd\"\n}")]
    [InlineData("@@@\n###")]
    public void AManifestThatIsNotOneComesBackEmpty(string text)
        => Assert.False(MeshManifest.Parse(text).Ready);

    /// <summary>
    /// The geometry reads, and everything inside it agrees with everything else.
    /// </summary>
    /// <remarks>
    /// THE ASSERTIONS ARE THE ONES THE REAL FILE PASSED, in the same order: a wrong stride puts
    /// positions outside the box, leaves indices pointing past the vertex count, and breaks the
    /// extents' coverage of the index buffer. Any one of them failing means the reader is walking
    /// the file at the wrong step, which is the failure a byte count alone would not catch.
    /// </remarks>
    [Fact]
    public void TheGeometryReadsAndAgreesWithItself()
    {
        SkinnedMesh mesh = SkinnedMesh.Read(Built());

        Assert.True(mesh.Ready);
        Assert.Equal(string.Empty, mesh.Why);
        Assert.Equal(2, mesh.Triangles);
        Assert.Equal(4, mesh.Positions.Length);

        foreach (Vector3 one in mesh.Positions)
        {
            Assert.InRange(one.X, mesh.Least.X, mesh.Most.X);
            Assert.InRange(one.Y, mesh.Least.Y, mesh.Most.Y);
            Assert.InRange(one.Z, mesh.Least.Z, mesh.Most.Z);
        }

        Assert.All(mesh.Indices, one => Assert.InRange(one, 0, mesh.Positions.Length - 1));
        Assert.All(mesh.Normals, one => Assert.Equal(1f, one.Length(), 0.02f));

        for (var one = 0; one < mesh.Positions.Length; one++)
        {
            Assert.Equal(
                255,
                mesh.Weights[one * 4] + mesh.Weights[(one * 4) + 1]
                    + mesh.Weights[(one * 4) + 2] + mesh.Weights[(one * 4) + 3]);
        }
    }

    /// <summary>The shapes are named and their slices cover the index buffer with no gap.</summary>
    [Fact]
    public void TheShapesCoverTheIndexBuffer()
    {
        SkinnedMesh mesh = SkinnedMesh.Read(Built());

        Assert.Equal(["HipsShape", "SkullShape"], mesh.Shapes.Select(one => one.Name));
        Assert.Equal(mesh.Indices.Length, mesh.Shapes.Sum(one => one.Count));

        var next = 0;
        foreach (MeshShape one in mesh.Shapes)
        {
            Assert.Equal(next, one.From);
            next += one.Count;
        }
    }

    /// <summary>
    /// A file that is not a mesh says so rather than throwing, whatever is in it.
    /// </summary>
    /// <remarks>
    /// THE CALLER IS A WINDOW DRAWING SOMETHING SOMEBODY CLICKED ON. A count out of a file is a
    /// number from anywhere, and the ones here multiply into buffer sizes - so a file claiming
    /// four billion vertices has to come back as a picture that does not appear, not as a throw
    /// on the draw thread.
    /// </remarks>
    [Fact]
    public void RubbishComesBackWithAReasonRatherThanAThrow()
    {
        foreach (byte[] bytes in Rubbish())
        {
            SkinnedMesh mesh = SkinnedMesh.Read(bytes);
            Assert.False(mesh.Ready);
            Assert.NotEqual(string.Empty, mesh.Why);
        }

        Assert.False(SkinnedMesh.Read((byte[]?)null).Ready);
        Assert.False(SkinnedMesh.Read(null, "a/b.smd").Ready);
    }

    /// <summary>A mesh claiming to be bigger than its file is refused before anything is sized.</summary>
    [Fact]
    public void AMeshCannotClaimToBeBiggerThanItsFile()
    {
        byte[] bytes = Built();

        // The vertex count at offset 49 - header 32, DOLm header 13, triangle count 4 - which
        // multiplies by the stride into the buffer this would otherwise allocate.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(49), 4_000_000_000);

        SkinnedMesh mesh = SkinnedMesh.Read(bytes);
        Assert.False(mesh.Ready);
        Assert.Contains("bigger than the file", mesh.Why, StringComparison.Ordinal);
    }

    /// <summary>Where the vertex buffer begins: header, DOLm header, counts, extents, indices.</summary>
    private const int VertexBufferStarts = 32 + 13 + 8 + (2 * 8) + (2 * 3 * 2);

    /// <summary>What follows the vertex buffer: the four bytes, the name table, and the tail.</summary>
    private static int AfterVertices(string[] names)
        => 4 + (names.Length * 4) + names.Sum(one => one.Length * 2) + 4 + (7 * 4);

    private static IEnumerable<byte[]> Rubbish()
    {
        yield return [];
        yield return new byte[10];
        yield return new byte[400];

        var random = new byte[5000];
        new Random(7).NextBytes(random);
        yield return random;
    }

    /// <summary>
    /// A version-3 mesh with the same shape as the game's: two triangles over four vertices.
    /// </summary>
    /// <remarks>
    /// THE STRIDE IS THE POINT. The vertex format word is 0x23C, which is what the real file
    /// carries, and it works out to 32 bytes - position, normal, tangent, one texture coordinate,
    /// bones and weights. Bits 4, 5 and 9 are set in it and add nothing; if the reader ever starts
    /// giving them a width, every assertion above fails at once rather than quietly.
    /// </remarks>
    private static byte[] Built()
    {
        const int Vertices = 4;
        const int Triangles = 2;
        const uint Format = 0x23C;
        const int Stride = 32;

        string[] names = ["HipsShape", "SkullShape"];
        var file = new List<byte>();

        void U8(int v) => file.Add((byte)v);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); file.AddRange(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); file.AddRange(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); file.AddRange(b); }
        void F16(float v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, BitConverter.HalfToUInt16Bits((Half)v)); file.AddRange(b); }

        U8(3);                      // version
        U8(4);                      // the outer vertex format, which DOLm overrides
        U16(names.Length);
        U32((uint)names.Sum(one => one.Length * 2));

        // The box, PER AXIS: min x, max x, min y, max y, min z, max z.
        F32(-1f); F32(1f); F32(-2f); F32(2f); F32(-3f); F32(3f);

        file.AddRange("DOLm"u8);
        U16(4);                     // "c0h"
        U8(1);                      // one level of detail
        U16(names.Length);
        U32(Format);

        U32(Triangles);
        U32(Vertices);

        U32(0); U32(3);             // HipsShape:  indices 0..3
        U32(3); U32(3);             // SkullShape: indices 3..6

        foreach (int one in new[] { 0, 1, 2, 1, 2, 3 })
        {
            U16(one);
        }

        Vector3[] places =
        [
            new(-1f, -2f, -3f), new(1f, 2f, 3f), new(0f, 0f, 0f), new(0.5f, 1f, 1.5f),
        ];

        foreach (Vector3 place in places)
        {
            F32(place.X); F32(place.Y); F32(place.Z);
            U8(0); U8(0); U8(127); U8(0);       // normal, pointing along z
            U8(127); U8(0); U8(0); U8(0);       // tangent
            F16(0.25f); F16(0.75f);             // texture coordinate
            U8(1); U8(0); U8(0); U8(0);         // bones
            U8(255); U8(0); U8(0); U8(0);       // weights, summing to 255
        }

        U32(0);                     // the four bytes that follow the geometry when "c0h" is four

        foreach (string name in names)
        {
            U32((uint)(name.Length * 2));
        }

        foreach (string name in names)
        {
            file.AddRange(Encoding.Unicode.GetBytes(name));
        }

        U32(4);                     // tail version
        for (var one = 0; one < 7; one++)
        {
            U32(0);
        }

        // The one check worth making here: the vertex buffer really is Vertices * Stride long, so
        // a reader that walks it at a different step runs into the name section rather than past
        // the end of the file - which is how the real file's stride was settled in the first place.
        Assert.Equal(Vertices * Stride, file.Count - VertexBufferStarts - AfterVertices(names));
        return [.. file];
    }
}
