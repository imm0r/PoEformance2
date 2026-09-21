using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The <c>.fmt</c> reader - a rigid prop's geometry and the materials it carries itself.
/// </summary>
/// <remarks>
/// WHY THE FORMAT IS WORTH READING AT ALL: an .ao hangs either a SkinMesh, which names a .sm and
/// then a .smd, or a FixedMesh, which names a .fmt and nothing else. Only the first was read, so
/// every rigid prop in the game came back from the model walk as "no SkinMesh - an effect or a
/// sound". Malgor, the Nautilord's cannon is one of them, reported missing from the live client.
///
/// WHAT THESE TESTS CAN AND CANNOT SETTLE. No real .fmt is committed here, so what is checked is
/// that the reader implements the layout poe_data_tools' fmt parser describes - and, more
/// usefully, that the reader's OWN self-check works: the shape names and materials are offsets
/// into a string pool at the very end of the file, past the geometry, past the per-piece records
/// and past a run of records whose width depends on the version. A walk that arrives one step
/// out lands those offsets in the middle of a word or past the end, and the names come back
/// wrong in a way a count would never show. That is precisely the failure that hid in the .smd
/// reader for months, so it is the one tested hardest here.
/// </remarks>
public class FixedMeshTests
{
    [Fact]
    public void APropCarriesItsGeometryAndAMaterialForEveryShape()
    {
        FixedMesh prop = FixedMesh.Read(Built());

        Assert.True(prop.Ready);
        Assert.Equal(string.Empty, prop.Why);
        Assert.Equal(4, prop.Mesh.Positions.Length);
        Assert.Equal(2, prop.Mesh.Triangles);

        Assert.Equal(["BarrelShape", "WheelShape"], prop.Mesh.Shapes.Select(one => one.Name));
        Assert.Equal(
            [("BarrelShape", "Art/Textures/Cannon.mat"), ("WheelShape", "Art/Textures/Wood.mat")],
            prop.Named);
    }

    /// <summary>The box is the file's, and the vertices are inside it.</summary>
    /// <remarks>
    /// THE SAME CHECK THE .smd READER IS HELD TO. A wrong vertex stride scatters positions
    /// outside the header's box, which is a failure a byte count alone does not catch.
    /// </remarks>
    [Fact]
    public void TheVerticesFillTheBoxTheHeaderWrote()
    {
        FixedMesh prop = FixedMesh.Read(Built());

        Assert.Equal(new Vector3(-1f, -2f, -3f), prop.Mesh.Least);
        Assert.Equal(new Vector3(1f, 2f, 3f), prop.Mesh.Most);

        foreach (Vector3 one in prop.Mesh.Positions)
        {
            Assert.InRange(one.X, prop.Mesh.Least.X, prop.Mesh.Most.X);
            Assert.InRange(one.Y, prop.Mesh.Least.Y, prop.Mesh.Most.Y);
            Assert.InRange(one.Z, prop.Mesh.Least.Z, prop.Mesh.Most.Z);
        }
    }

    /// <summary>
    /// The records before the string pool are counted, not guessed, and their width is the
    /// version's.
    /// </summary>
    /// <remarks>
    /// THE ONLY THING BETWEEN THE GEOMETRY AND THE NAMES. Nothing reads what is in those records
    /// - their content is unknown to every reader of this format there is - but their width has
    /// to be exact, because the shape names are offsets into what follows them. Getting it wrong
    /// does not throw and does not change a single count: it changes the names, and only the
    /// names. So the test is that the names survive every version whose width differs.
    /// </remarks>
    [Theory]
    [InlineData(9, 1)]
    [InlineData(9, 3)]
    [InlineData(12, 2)]
    public void TheNamesSurviveWhateverSitsBetweenThemAndTheGeometry(int version, int records)
    {
        FixedMesh prop = FixedMesh.Read(Built(version, records));

        Assert.True(prop.Ready);
        Assert.Equal(["BarrelShape", "WheelShape"], prop.Mesh.Shapes.Select(one => one.Name));
    }

    /// <summary>The per-piece records are counted the same way, from each piece's own number.</summary>
    [Fact]
    public void ThePiecesOwnRecordsAreCountedTooAndTheNamesStillLand()
    {
        FixedMesh prop = FixedMesh.Read(Built(pieces: [2, 0, 5]));

        Assert.True(prop.Ready);
        Assert.Equal(["BarrelShape", "WheelShape"], prop.Mesh.Shapes.Select(one => one.Name));
    }

    /// <summary>
    /// A version below nine is refused by name rather than read out of the wrong bytes.
    /// </summary>
    /// <remarks>
    /// IT PUTS THE VERTICES IN A LAYOUT OF ITS OWN - no DOLm block at all - so reading it as this
    /// one would produce a prop-shaped answer from bytes that mean something else. Nothing in the
    /// game seen so far is one, and a reader that guessed would say so only by drawing rubbish.
    /// </remarks>
    [Fact]
    public void AnOlderVersionSaysSoRatherThanGuessing()
    {
        byte[] bytes = Built();
        bytes[0] = 8;

        FixedMesh prop = FixedMesh.Read(bytes);

        Assert.False(prop.Ready);
        Assert.Contains("outside a DOLm block", prop.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that is not a fixed mesh comes back with a reason rather than throwing.
    /// </summary>
    /// <remarks>
    /// THE CALLER IS A WINDOW DRAWING A MONSTER SOMEBODY CLICKED ON, and the counts in here
    /// multiply into buffer sizes - so a file claiming four billion of anything has to come back
    /// as a prop that does not appear, not as a throw on the draw thread.
    /// </remarks>
    [Fact]
    public void RubbishComesBackWithAReasonRatherThanAThrow()
    {
        foreach (byte[] bytes in Rubbish())
        {
            FixedMesh prop = FixedMesh.Read(bytes);
            Assert.False(prop.Ready);
            Assert.NotEqual(string.Empty, prop.Why);
        }

        Assert.False(FixedMesh.Read((byte[]?)null).Ready);
        Assert.False(FixedMesh.Read(null, "a/b.fmt").Ready);
        Assert.False(FixedMesh.None.Ready);
    }

    /// <summary>A pool the offsets do not land in costs the names and nothing else.</summary>
    /// <remarks>
    /// A PROP WITH UNNAMED SHAPES IS STILL A PROP. The geometry is already in hand by the time
    /// the pool is read, so an offset pointing past it falls back to "shape 0" and the piece is
    /// drawn plain rather than left out of the picture.
    /// </remarks>
    [Fact]
    public void AnOffsetThatLandsNowhereCostsItsOwnNameAndNothingElse()
    {
        FixedMesh prop = FixedMesh.Read(Built(names: [9999, 9999]));

        Assert.True(prop.Ready);
        Assert.Equal(["shape 0", "shape 1"], prop.Mesh.Shapes.Select(one => one.Name));
    }

    private static IEnumerable<byte[]> Rubbish()
    {
        yield return [];
        yield return new byte[20];
        yield return new byte[400];

        var random = new byte[5000];
        new Random(11).NextBytes(random);
        yield return random;
    }

    /// <summary>
    /// A version 9 <c>.fmt</c>: two shapes over four vertices, each shape naming a material.
    /// </summary>
    /// <param name="version">Which version byte to write. It decides the trailing record width.</param>
    /// <param name="records">How many of those trailing records sit before the string pool.</param>
    /// <param name="pieces">How many records each piece owns, one entry per piece.</param>
    /// <param name="names">
    /// Offsets into the string pool to write for the two shapes' names, or null for the real
    /// ones. Used to check that an offset landing nowhere costs only that name.
    /// </param>
    private static byte[] Built(
        int version = 9, int records = 0, int[]? pieces = null, int[]? names = null)
    {
        const int Vertices = 4;
        const int Triangles = 2;
        const uint Format = 0x8;        // A texture coordinate and no skin - a prop has no bones.

        pieces ??= [];
        var file = new List<byte>();

        void U8(int v) => file.Add((byte)v);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); file.AddRange(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); file.AddRange(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); file.AddRange(b); }
        void F16(float v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, BitConverter.HalfToUInt16Bits((Half)v)); file.AddRange(b); }

        // The pool, with every string zero-terminated, and where each one begins IN CHARACTERS.
        string[] pool = ["BarrelShape", "Art/Textures/Cannon.mat", "WheelShape", "Art/Textures/Wood.mat"];
        var starts = new int[pool.Length];
        var chars = 0;
        for (var one = 0; one < pool.Length; one++)
        {
            starts[one] = chars;
            chars += pool[one].Length + 1;
        }

        U8(version);
        U16(2);                     // two shapes
        U8(pieces.Length);
        U16(pieces.Sum());          // how many per-piece records there are in total
        U8(records);

        // The box, PER AXIS: min x, max x, min y, max y, min z, max z.
        F32(-1f); F32(1f); F32(-2f); F32(2f); F32(-3f); F32(3f);

        file.AddRange("DOLm"u8);
        U16(1);                     // "c0h" - not four, so no four bytes follow the geometry
        U8(1);                      // one level of detail
        U16(2);                     // two shapes
        U32(Format);

        U32(Triangles);
        U32(Vertices);

        U32(0); U32(3);             // BarrelShape: indices 0..3
        U32(3); U32(3);             // WheelShape:  indices 3..6

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
        }

        // One pair of offsets per shape: the name, then the material.
        U32((uint)(names is not null ? names[0] : starts[0]));
        U32((uint)starts[1]);
        U32((uint)(names is not null ? names[1] : starts[2]));
        U32((uint)starts[3]);

        foreach (int owned in pieces)
        {
            U8(0);                  // unknown
            U8(owned);
            U32(0);                 // the piece's own tag, into the pool
        }

        foreach (int owned in pieces)
        {
            file.AddRange(new byte[owned * 12]);
        }

        int wide = version switch { < 3 => 45, 3 => 70, 4 or 5 => 78, 6 => 83, _ => 87 };
        file.AddRange(new byte[records * wide]);

        U32((uint)chars);
        foreach (string one in pool)
        {
            file.AddRange(Encoding.Unicode.GetBytes(one));
            U16(0);
        }

        return [.. file];
    }
}
