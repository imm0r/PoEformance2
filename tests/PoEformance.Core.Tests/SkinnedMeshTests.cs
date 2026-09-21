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
            new MeshMaterial("Art/Textures/Monsters/KatarinaSkeleton/SkeletonVarc.mat", 15),
            Assert.Single(said.Materials));
        Assert.Equal(["grp_head", "grp_neck", "grp_chest"], said.Bones);
    }

    /// <summary>
    /// The number after a material is how many shapes it covers, and this file proves it.
    /// </summary>
    /// <remarks>
    /// THE ONE SAMPLE THAT SETTLES IT, and it was sitting in this file the whole time. The
    /// number after a .mat path is <c>unk1</c> to every other reader of this format there is,
    /// so its meaning could only come from a file where the answer is known independently -
    /// and this manifest is BasicSkeleton's, whose geometry <see cref="SkinnedMesh"/> accounts
    /// for byte by byte and whose fifteen shape names match the fifteen SkinMesh entries of the
    /// .ao beside it. One material, the number 15, fifteen shapes.
    ///
    /// WHICH IS WHY <see cref="MeshManifest.Spread"/> REFUSES ANYTHING THAT DOES NOT ADD UP.
    /// One file agreeing is a reading, not a proof, and the sum is the check that makes every
    /// other file test the reading again before the renderer acts on it.
    /// </remarks>
    [Fact]
    public void TheNumberAfterAMaterialIsHowManyShapesItCovers()
    {
        MeshManifest said = MeshManifest.Parse(Manifest);

        Assert.Equal(
            Enumerable.Repeat("Art/Textures/Monsters/KatarinaSkeleton/SkeletonVarc.mat", 15),
            said.Spread(15));
    }

    /// <summary>A file whose numbers do not account for every shape spreads nothing.</summary>
    /// <remarks>
    /// THE HALF THAT KEEPS THE READING HONEST. Painting from runs that do not add up would put
    /// parts on whatever happened to line up - the failure this whole line of work is fixing -
    /// so a mismatch answers empty and the caller keeps what it had.
    /// </remarks>
    [Theory]
    [InlineData(14)]
    [InlineData(16)]
    public void RunsThatDoNotCoverTheShapesAreNotUsed(int shapes)
        => Assert.Empty(MeshManifest.Parse(Manifest).Spread(shapes));

    /// <summary>A material with an empty path is still an entry, and its run still counts.</summary>
    /// <remarks>
    /// THE OLD READER DROPPED THESE, which was harmless while only the paths were read and
    /// shifts every run after it now. The format allows an empty material and the game writes
    /// them.
    /// </remarks>
    [Fact]
    public void AnEmptyMaterialKeepsItsPlaceInTheRuns()
    {
        MeshManifest said = MeshManifest.Parse(
            "version 5\nSkinnedMeshData \"a.smd\"\nMaterials 2\n\t\"\" 1\n\t\"art/skin.mat\" 2\n");

        Assert.Equal(2, said.Materials.Count);
        Assert.Equal(["", "art/skin.mat", "art/skin.mat"], said.Spread(3));
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
    /// The four bytes after the geometry belong to <c>c0h == 4</c> and to nothing else.
    /// </summary>
    /// <remarks>
    /// FOUND IN THE GAME, NOT IN A SPEC. A monster's attached piece came back with shape names
    /// that were NEARLY right - <c>athers_headpieceSh</c> where the file says
    /// <c>feathers_headpieceShape</c>, then <c>apefeathers_neckSha</c> - and nearly right is what
    /// made it expensive: the geometry was fine, so it read as a wrongly placed piece rather than
    /// a wrongly stepped file. Four bytes taken that were not there meant the FIRST length word
    /// read was the second shape's, and every name after it drifted further.
    ///
    /// The reference's dolm parser gates those four bytes on <c>c0h</c> being four. This is that
    /// gate, and the file it is checked against is the one that has no such word.
    /// </remarks>
    [Fact]
    public void AMeshWhoseCornerWordIsNotFourHasNothingToStepOverBeforeItsNames()
    {
        foreach (int corner in new[] { 1, 2, 3 })
        {
            SkinnedMesh mesh = SkinnedMesh.Read(Built(corner));

            Assert.True(mesh.Ready);
            Assert.Equal(["HipsShape", "SkullShape"], mesh.Shapes.Select(one => one.Name));
        }
    }

    /// <summary>
    /// A mesh with several levels of detail still reads - the first one, and its real names.
    /// </summary>
    /// <remarks>
    /// THE COUNTS ARE ONE TABLE AND THE MESHES FOLLOW IT. A reader that takes the first pair and
    /// walks straight into the geometry reads the SECOND detail's counts as the first shape's
    /// extents, so nothing about the file survives - and the names, which sit past the last
    /// detail, are gone with it. Only the first mesh is kept; every one of them is counted.
    /// </remarks>
    [Fact]
    public void OnlyTheFirstLevelOfDetailIsKeptAndTheRestAreStillCounted()
    {
        SkinnedMesh mesh = SkinnedMesh.Read(Built(details: 3));

        Assert.True(mesh.Ready);
        Assert.Equal(4, mesh.Positions.Length);
        Assert.Equal(2, mesh.Triangles);
        Assert.Equal(["HipsShape", "SkullShape"], mesh.Shapes.Select(one => one.Name));
    }

    /// <summary>
    /// The vertex format's seventh bit costs four bytes a vertex AND thirty-six a shape.
    /// </summary>
    /// <remarks>
    /// BOTH, NOT EITHER. This reader had only the per-vertex half, which is enough for a file
    /// that does not set the bit and silently short by thirty-six times the shape count for one
    /// that does. The reference's dolm parser writes both, and where they land - the block after
    /// the LAST level of detail, before the names - is what decides whether the names read.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TheSeventhBitOfTheVertexFormatCostsAVertexBlockAndAShapeBlock(int corner)
    {
        SkinnedMesh mesh = SkinnedMesh.Read(Built(corner, sixth: true));

        Assert.True(mesh.Ready);
        Assert.Equal(36, mesh.Facts.Stride);
        Assert.Equal(["HipsShape", "SkullShape"], mesh.Shapes.Select(one => one.Name));

        // The positions still fill the box, which a stride that had drifted would not manage.
        foreach (Vector3 one in mesh.Positions)
        {
            Assert.InRange(one.X, mesh.Least.X, mesh.Most.X);
            Assert.InRange(one.Z, mesh.Least.Z, mesh.Most.Z);
        }
    }

    /// <summary>
    /// The header says how many bytes of names there are, and the reader has to find that many.
    /// </summary>
    /// <remarks>
    /// THE ONE INVARIANT A WRONG STEP CANNOT SATISFY BY ACCIDENT, and the reason it is reported
    /// rather than derived: the count is written at the top of the file, before anything the
    /// reader walks, so the two agreeing means every step between them landed. It is what the
    /// dump prints, and it is how the four-byte error above turned from a theory into a number.
    /// </remarks>
    [Fact]
    public void WhatTheHeaderSaysTheNamesWeighIsWhatTheReaderFinds()
    {
        foreach (int corner in new[] { 1, 2, 4 })
        {
            foreach (bool sixth in new[] { false, true })
            {
                MeshFacts facts = SkinnedMesh.Read(Built(corner, sixth: sixth)).Facts;

                Assert.Equal(facts.NamesSaid, facts.NamesRead);
                Assert.Equal(corner, facts.Corner);
            }
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
    /// <summary>
    /// A mesh that can be painted is told from one that cannot.
    /// </summary>
    /// <remarks>
    /// ANY ONE COORDINATE IS ENOUGH, and that is the case worth pinning. A mesh with no
    /// coordinates does not have an absent array - it has a full one of zeroes, because the vertex
    /// format still reserves the slot - so the question is "are they ALL zero", not "is the array
    /// there". A check that looked only at the first vertex would call a real mesh uncoordinated
    /// whenever its first corner happened to sit at the texture's origin, which is a place real
    /// corners sit.
    ///
    /// WHAT RIDES ON IT: the renderer skips the texture when this is false, and the model walk
    /// reports it as the reason a monster came out in plain ink. Answering it wrongly either
    /// paints every triangle with one corner texel, or sends somebody looking for a missing file.
    /// </remarks>
    [Fact]
    public void AMeshThatCanBePaintedIsToldFromOneThatCannot()
    {
        Vector3[] places = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)];
        Vector3[] facing = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        int[] indices = [0, 1, 2];

        SkinnedMesh Made(Vector2[]? spots)
            => SkinnedMesh.Of(places, facing, indices, Vector3.Zero, Vector3.One, spots);

        Assert.False(Made(null).Coordinated, "no coordinates at all");
        Assert.False(Made(new Vector2[3]).Coordinated, "an array of zeroes is no coordinates");
        Assert.False(SkinnedMesh.None.Coordinated, "and nothing at all carries nothing");

        Assert.True(
            Made([Vector2.Zero, Vector2.Zero, new Vector2(0f, 0.5f)]).Coordinated,
            "one corner away from the origin is a mesh that can be painted");
    }

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
    /// <param name="corner">
    /// The DOLm <c>c0h</c> word. Only a four puts four bytes between the geometry and the names,
    /// and taking them regardless is the error these tests exist to keep out.
    /// </param>
    /// <param name="details">
    /// How many levels of detail. Their counts are written as ONE table before the first mesh and
    /// the meshes follow it, so a reader that stops after the first walks into the second's counts.
    /// </param>
    /// <param name="sixth">
    /// Whether the vertex format's seventh bit is set, which costs four bytes on every vertex AND
    /// thirty-six per shape after the last level of detail.
    /// </param>
    private static byte[] Built(int corner = 4, int details = 1, bool sixth = false)
    {
        const int Vertices = 4;
        const int Triangles = 2;
        uint format = sixth ? 0x23Cu | (1u << 6) : 0x23Cu;
        int stride = sixth ? 36 : 32;

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
        U16(corner);                // "c0h"
        U8(details);
        U16(names.Length);
        U32(format);

        // EVERY DETAIL'S COUNTS FIRST, as one table, and only then the meshes.
        for (var lod = 0; lod < details; lod++)
        {
            U32(Triangles);
            U32(Vertices);
        }

        Vector3[] places =
        [
            new(-1f, -2f, -3f), new(1f, 2f, 3f), new(0f, 0f, 0f), new(0.5f, 1f, 1.5f),
        ];

        for (var lod = 0; lod < details; lod++)
        {
            U32(0); U32(3);             // HipsShape:  indices 0..3
            U32(3); U32(3);             // SkullShape: indices 3..6

            foreach (int one in new[] { 0, 1, 2, 1, 2, 3 })
            {
                U16(one);
            }

            foreach (Vector3 place in places)
            {
                F32(place.X); F32(place.Y); F32(place.Z);
                U8(0); U8(0); U8(127); U8(0);       // normal, pointing along z
                U8(127); U8(0); U8(0); U8(0);       // tangent
                F16(0.25f); F16(0.75f);             // texture coordinate
                U8(1); U8(0); U8(0); U8(0);         // bones
                U8(255); U8(0); U8(0); U8(0);       // weights, summing to 255
                if (sixth)
                {
                    U32(0);                         // whatever the seventh bit adds per vertex
                }
            }
        }

        if (sixth)
        {
            file.AddRange(new byte[names.Length * 36]);
            if (corner == 2)
            {
                file.AddRange(new byte[names.Length * 4]);
            }
        }

        if (corner == 4)
        {
            U32(0);                 // the four bytes that follow the geometry when "c0h" is four
        }

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
        if (corner == 4 && details == 1 && !sixth)
        {
            Assert.Equal(Vertices * stride, file.Count - VertexBufferStarts - AfterVertices(names));
        }

        return [.. file];
    }

    /// <summary>
    /// A mesh of version one carries its geometry in the file, and reads.
    /// </summary>
    /// <remarks>
    /// THE SWEEP IS WHAT MADE THIS WORTH DOING. Refused, these were 373 pieces on 115 monsters -
    /// and the corpus could only say that once it carried the reader's own reason per piece,
    /// which turned "113 monsters have something unreadable" into "one format, 373 times".
    ///
    /// THE HEADER IS LONGER BEFORE THE FORMAT BYTE than version three's - version, TRIANGLES,
    /// VERTICES, format, shapes, where the newer file has version, format, shapes. Read as the
    /// newer layout the triangle count's low bytes become the format and its high bytes the
    /// shape count, and everything after is rubbish that still has the shape of a mesh.
    /// </remarks>
    [Fact]
    public void AMeshOfVersionOneCarriesItsGeometryInTheFileItself()
    {
        Packed.Vertex[] points =
        [
            new(new Vector3(-5f, 0f, 1f), [3, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(7f, 2f, -3f), [3, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(0f, -4f, 9f), [3, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(2f, 6f, -8f), [3, 0, 0, 0], [255, 0, 0, 0]),
        ];

        SkinnedMesh said = SkinnedMesh.Read(Packed.OldMesh(points));

        Assert.True(said.Ready, said.Why);
        Assert.Equal(1, said.Facts.Version);
        Assert.Equal(4, said.Positions.Length);
        Assert.Equal(2, said.Triangles);

        // THE POSITIONS THEMSELVES, because a stride that is wrong by even four bytes still
        // produces a mesh of the right SIZE - it just reads each vertex out of the middle of
        // the one before it.
        Assert.Equal(points.Length, said.Positions.Length);
        for (var one = 0; one < points.Length; one++)
        {
            Assert.Equal(points[one].Place, said.Positions[one]);
        }

        // And the box the header wrote is the box the vertices fill - the same invariant the
        // real BasicSkeleton file settled the newer format with.
        Assert.Equal(new Vector3(-5f, -4f, -8f), said.Least);
        Assert.Equal(new Vector3(7f, 6f, 9f), said.Most);

        // The bones and weights are where the older vertex puts them, which is not where the
        // DOLm one does: unconditional rather than gated on the format word.
        Assert.Equal<byte>(3, said.Bones[0]);
        Assert.Equal<byte>(255, said.Weights[0]);
    }

    /// <summary>
    /// Version two writes one more word than version one, and only version two.
    /// </summary>
    /// <remarks>
    /// THE REFERENCE GATES IT <c>cond(version == 2, le_u32)</c> and nothing here knows what it
    /// is. Four bytes taken that are not there - or left that are - puts the shape table four
    /// bytes out, and this project has already paid for that exact mistake once: the names came
    /// back NEARLY right, which reads as a wrongly placed piece rather than a wrongly stepped
    /// file.
    ///
    /// The two files below hold the SAME geometry, so anything but the same answer is the gate.
    /// </remarks>
    [Fact]
    public void VersionTwoHasOneMoreWordBeforeItsShapesAndVersionOneDoesNot()
    {
        Packed.Vertex[] points =
        [
            new(new Vector3(1f, 2f, 3f), [1, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(4f, 5f, 6f), [1, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(7f, 8f, 9f), [1, 0, 0, 0], [255, 0, 0, 0]),
        ];

        SkinnedMesh one_ = SkinnedMesh.Read(Packed.OldMesh(points, version: 1));
        SkinnedMesh two = SkinnedMesh.Read(Packed.OldMesh(points, version: 2));

        Assert.True(two.Ready, two.Why);
        Assert.Equal(2, two.Facts.Version);
        Assert.Equal(one_.Positions, two.Positions);
        Assert.Equal(
            one_.Shapes.Select(shape => shape.Name),
            two.Shapes.Select(shape => shape.Name));
    }

    /// <summary>
    /// An older shape writes where it STARTS, so it runs to where the next one begins.
    /// </summary>
    /// <remarks>
    /// VERSION THREE WRITES A START AND A COUNT; this writes one number. Read as a pair the
    /// second shape would take the third's start as its own length, which is a mesh that draws
    /// the right triangles in the wrong groups - and since the groups are what carry the
    /// per-shape textures, the symptom is a monster painted with the wrong sheets rather than a
    /// monster that fails to appear.
    /// </remarks>
    [Fact]
    public void AnOlderShapeRunsToWhereTheNextOneBegins()
    {
        Packed.Vertex[] points =
        [
            .. Enumerable.Range(0, 8).Select(one =>
                new Packed.Vertex(new Vector3(one, 0f, 0f), [0, 0, 0, 0], [255, 0, 0, 0])),
        ];

        // Six triangles over eight vertices, in three groups starting at 0, 2 and 5.
        SkinnedMesh said = SkinnedMesh.Read(Packed.OldMesh(
            points, shapes: [("HipsShape", 0), ("CloakShape", 2), ("HeadShape", 5)]));

        Assert.True(said.Ready, said.Why);
        Assert.Equal(["HipsShape", "CloakShape", "HeadShape"], said.Shapes.Select(one => one.Name));

        Assert.Equal((0, 6), (said.Shapes[0].From, said.Shapes[0].Count));
        Assert.Equal((6, 9), (said.Shapes[1].From, said.Shapes[1].Count));
        Assert.Equal((15, 3), (said.Shapes[2].From, said.Shapes[2].Count));

        // No gap and no overlap over the whole index buffer, which is the invariant the real
        // file settled the newer format with.
        Assert.Equal(said.Indices.Length, said.Shapes.Sum(one => one.Count));
    }

    /// <summary>
    /// The older vertex gates two fields where the DOLm one gates four.
    /// </summary>
    /// <remarks>
    /// A FORMAT WORD OF NOUGHT IS THE CASE THAT SEPARATES THEM. In a DOLm block that means no
    /// texture coordinate and no bones - a 20-byte vertex. Here the reference writes all three
    /// unconditionally and gates only skin extra and a second coordinate, so the same nought is
    /// a 32-byte vertex. Twelve bytes short does not fail; it reads the next vertex's position
    /// out of the middle of this one and produces confetti.
    ///
    /// Both bits set is the other end of it: 40 bytes, and a reader that ignores them runs eight
    /// bytes further out of step with every vertex it takes.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheOlderVertexGatesOnlySkinExtraAndASecondCoordinate(byte format)
    {
        Packed.Vertex[] points =
        [
            new(new Vector3(11f, 12f, 13f), [2, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(21f, 22f, 23f), [2, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(31f, 32f, 33f), [2, 0, 0, 0], [255, 0, 0, 0]),
        ];

        SkinnedMesh said = SkinnedMesh.Read(Packed.OldMesh(points, format: format));

        Assert.True(said.Ready, said.Why);
        Assert.Equal(points.Select(one => one.Place), said.Positions);
        Assert.All(said.Bones.Where((_, at) => at % 4 == 0), one => Assert.Equal<byte>(2, one));
    }

    /// <summary>A file that lies about its counts is refused rather than read past its end.</summary>
    /// <remarks>
    /// THE SAME RULE THE NEWER PATH FOLLOWS. These counts come out of the file, which is to say
    /// from anywhere, and they multiply into every size below them.
    /// </remarks>
    [Fact]
    public void AnOlderMeshThatLiesAboutItsCountsIsRefused()
    {
        Packed.Vertex[] points =
        [
            new(new Vector3(1f, 1f, 1f), [0, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(2f, 2f, 2f), [0, 0, 0, 0], [255, 0, 0, 0]),
            new(new Vector3(3f, 3f, 3f), [0, 0, 0, 0], [255, 0, 0, 0]),
        ];

        byte[] file = Packed.OldMesh(points);

        // The vertex count sits at offset 5, right after the version and the triangle count.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(5), 40_000_000);

        SkinnedMesh said = SkinnedMesh.Read(file);

        Assert.False(said.Ready);
        Assert.Contains("bigger than the file", said.Why, StringComparison.Ordinal);
    }
}
