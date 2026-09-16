using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The mesh renderer, measured rather than looked at.
/// </summary>
/// <remarks>
/// THE WHOLE REASON IT DRAWS ON THE PROCESSOR is that its output is an array a test can ask
/// questions of. A renderer talking to the overlay's graphics device could only ever be judged
/// from a screenshot on a machine with the game running; this one answers "is it the right way
/// up", "does it fill the frame", "is the far side hidden" as arithmetic.
///
/// THE ORIENTATION TESTS ARE THE ONES THAT EARN THEIR KEEP. A model's up axis is its z and it
/// runs NEGATIVE - BasicSkeleton's box is z -189 to -0.4 - so the two ways to get this wrong both
/// produce a picture: read y as up and the skeleton lies on its face, forget the sign and it
/// hangs upside down. Neither throws, neither looks empty, and both are obvious on screen and
/// invisible to a test that only counts pixels.
/// </remarks>
public class MeshPictureTests
{
    [Fact]
    public void NothingToDrawIsAnEmptyPictureRatherThanAThrow()
    {
        GamePicture said = MeshPicture.Of(SkinnedMesh.None, 64);

        Assert.Equal(64, said.Width);
        Assert.Equal(64, said.Height);
        Assert.All(said.Rgba, one => Assert.Equal(0, one));

        Assert.Equal(32, MeshPicture.Of(null, 32).Width);
    }

    /// <summary>A size outside what the buffers allow is clamped, not obeyed.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(64, 64)]
    [InlineData(999_999, MeshPicture.Widest)]
    public void TheSizeIsClamped(int asked, int given)
        => Assert.Equal(given, MeshPicture.Of(SkinnedMesh.None, asked).Width);

    /// <summary>
    /// A model four times as long as it is wide is drawn four times as TALL as it is wide.
    /// </summary>
    /// <remarks>
    /// THE TEST FOR THE UP AXIS. The long side is z, so an upright drawing is tall; a renderer
    /// treating y as up draws the same mesh four times as wide as it is tall, which is a picture
    /// of a lying-down monster and passes every check that only counts lit pixels.
    /// </remarks>
    [Fact]
    public void TheLongAxisOfTheModelIsTheTallAxisOfThePicture()
    {
        GamePicture said = MeshPicture.Of(Post(), 200);
        (int Top, int Foot, int Left, int Right, int Lit) seen = Silhouette(said);

        int tall = seen.Foot - seen.Top + 1;
        int wide = seen.Right - seen.Left + 1;

        Assert.True(seen.Lit > 0, "nothing was drawn at all");
        Assert.True(
            tall > wide * 2,
            $"the model is four times longer than it is wide and came out {tall} by {wide}");
    }

    /// <summary>
    /// Geometry in the top half of the model lands in the top half of the picture.
    /// </summary>
    /// <remarks>
    /// THE TEST FOR THE SIGN. A model's z grows downwards from its head, so the half nearest the
    /// far end of the box is what a viewer calls up. Get the sign wrong and every monster hangs
    /// from its feet - a rotation no test measuring only proportions can see.
    /// </remarks>
    [Fact]
    public void TheTopOfTheModelIsTheTopOfThePicture()
    {
        // A post whose upper half alone carries a wide crossbar: on screen, the widest row must
        // be above the middle.
        GamePicture said = MeshPicture.Of(Post(crossbar: true), 200);

        var widest = 0;
        var at = 0;
        for (var y = 0; y < said.Height; y++)
        {
            var wide = 0;
            for (var x = 0; x < said.Width; x++)
            {
                if (said.Rgba[(((y * said.Width) + x) * 4) + 3] != 0)
                {
                    wide++;
                }
            }

            if (wide > widest)
            {
                widest = wide;
                at = y;
            }
        }

        Assert.True(widest > 0, "nothing was drawn at all");
        Assert.True(
            at < said.Height / 2,
            $"the crossbar is on the model's upper half and came out at row {at} of {said.Height}");
    }

    /// <summary>The drawing fills the frame without spilling out of it.</summary>
    /// <remarks>
    /// BOTH HALVES MATTER. A model drawn at a fixed scale is a speck for a rat and clipped for a
    /// boss, so the box decides the scale - and a scale that overshoots crops the monster's head
    /// off against the frame, which reads as a mesh that is missing triangles.
    /// </remarks>
    [Fact]
    public void TheModelFillsTheFrameAndStaysInside()
    {
        const int Size = 200;
        GamePicture said = MeshPicture.Of(Post(crossbar: true), Size);
        (int Top, int Foot, int Left, int Right, int Lit) seen = Silhouette(said);

        Assert.True(seen.Top > 0 && seen.Foot < Size - 1, "the drawing touches the top or bottom edge");
        Assert.True(seen.Left > 0 && seen.Right < Size - 1, "the drawing touches the left or right edge");

        int tall = seen.Foot - seen.Top + 1;
        Assert.True(
            tall > Size * 0.7f,
            $"the model should fill most of the frame and filled {tall} of {Size}");
    }

    /// <summary>
    /// The nearer surface wins, whatever order the triangles are listed in.
    /// </summary>
    /// <remarks>
    /// A DEPTH BUFFER AND NOT A SORT. Two facing quads at different distances are the smallest
    /// case where painting in file order gives the wrong answer: listed far-last, the far one
    /// covers the near one. The shading differs between them because their normals do, so the
    /// pixel says which won.
    ///
    /// WHICH ONE WINS IS THE ASSERTION, not merely that the two orders agree. They would also
    /// agree if the renderer always kept the LAST triangle - painting in file order - so the
    /// weaker check passes the very implementation it exists to rule out. The quad drawn alone
    /// supplies the value to expect, rather than a number written down here that would go stale
    /// the moment the lighting is touched.
    /// </remarks>
    [Fact]
    public void TheNearSurfaceHidesTheFarOneWhicheverIsListedLast()
    {
        byte alone = Middle(MeshPicture.Of(Single(near: true), 64));
        byte other = Middle(MeshPicture.Of(Single(near: false), 64));

        Assert.NotEqual(alone, other);

        Assert.Equal(alone, Middle(MeshPicture.Of(Pair(farLast: false), 64)));
        Assert.Equal(alone, Middle(MeshPicture.Of(Pair(farLast: true), 64)));
    }

    /// <summary>Turning the model changes the picture, and a whole turn puts it back.</summary>
    [Fact]
    public void AWholeTurnComesBackToWhereItStarted()
    {
        GamePicture start = MeshPicture.Of(Post(crossbar: true), 96);
        GamePicture quarter = MeshPicture.Of(Post(crossbar: true), 96, MathF.PI / 2f);
        GamePicture whole = MeshPicture.Of(Post(crossbar: true), 96, MathF.Tau);

        Assert.NotEqual(Silhouette(start).Right - Silhouette(start).Left, Silhouette(quarter).Right - Silhouette(quarter).Left);
        Assert.Equal(Silhouette(start), Silhouette(whole));
    }

    /// <summary>
    /// A skin is sampled where the mesh has coordinates, and ignored where it has none.
    /// </summary>
    /// <remarks>
    /// THE SECOND HALF IS THE ONE WORTH HAVING. A mesh with no texture coordinates has all of them
    /// at zero, so sampling it paints every triangle with ONE corner pixel of the texture - a
    /// monster in a flat colour taken from an arbitrary place, which looks deliberate and is not.
    /// Falling back to the grey is both honest and obviously a fall-back.
    /// </remarks>
    [Fact]
    public void ASkinIsUsedOnlyWhereThereAreCoordinatesToUseIt()
    {
        GamePicture red = Sheet(220, 30, 30);

        // The post carries no coordinates, so the skin cannot be looked up and is left alone.
        Assert.Equal(
            Middle(MeshPicture.Of(Post(), 64)),
            Middle(MeshPicture.Of(Post(), 64, skin: red)));

        // Given coordinates, the same mesh takes the skin's colour instead of the ink.
        GamePicture plain = MeshPicture.Of(Coated(), 64);
        GamePicture skinned = MeshPicture.Of(Coated(), 64, skin: red);

        Assert.NotEqual(Middle(plain), Middle(skinned));
        Assert.True(
            Channel(skinned, 0) > Channel(skinned, 1) * 2,
            "a red skin should come out red, and came out "
                + $"{Channel(skinned, 0)},{Channel(skinned, 1)},{Channel(skinned, 2)}");
    }

    /// <summary>
    /// The game's texture coordinates run NEGATIVE, and are wrapped rather than clamped.
    /// </summary>
    /// <remarks>
    /// MEASURED ON THE REAL MESH: BasicSkeleton's coordinates run -0.997 to -0.002. A sampler that
    /// clamped instead of wrapping would paint every monster with the single row of texels along
    /// one edge of its texture - one colour, no pattern, and no error anywhere.
    /// </remarks>
    [Fact]
    public void ANegativeCoordinateWrapsRatherThanClamping()
    {
        // A sheet whose halves differ, sampled at -0.25, which wraps to 0.75 - the far half.
        GamePicture halves = Halved();

        GamePicture below = MeshPicture.Of(Coated(-0.25f), 64, skin: halves);
        GamePicture above = MeshPicture.Of(Coated(0.75f), 64, skin: halves);

        Assert.Equal(Middle(below), Middle(above));
        Assert.NotEqual(Middle(MeshPicture.Of(Coated(0.25f), 64, skin: halves)), Middle(below));
    }

    private static byte Channel(GamePicture said, int part)
        => said.Rgba[((((said.Height / 2) * said.Width) + (said.Width / 2)) * 4) + part];

    /// <summary>A texture of one colour.</summary>
    private static GamePicture Sheet(byte red, byte green, byte blue)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var one = 0; one < 8 * 8; one++)
        {
            pixels[(one * 4) + 0] = red;
            pixels[(one * 4) + 1] = green;
            pixels[(one * 4) + 2] = blue;
            pixels[(one * 4) + 3] = 255;
        }

        return new GamePicture(8, 8, pixels);
    }

    /// <summary>A texture whose lower half differs from its upper one.</summary>
    private static GamePicture Halved()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                int at = ((y * 8) + x) * 4;
                pixels[at] = (byte)(y < 4 ? 40 : 230);
                pixels[at + 1] = pixels[at];
                pixels[at + 2] = pixels[at];
                pixels[at + 3] = 255;
            }
        }

        return new GamePicture(8, 8, pixels);
    }

    /// <summary>A quad facing the viewer, with every corner at the same texture coordinate.</summary>
    private static SkinnedMesh Coated(float v = 0.5f)
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Quad(places, indices, near: true);

        SkinnedMesh bare = Built(places, indices, Least, Most);
        var spots = new Vector2[bare.Positions.Length];
        Array.Fill(spots, new Vector2(0.5f, v));

        return SkinnedMesh.Of(bare.Positions, bare.Normals, bare.Indices, Least, Most, spots);
    }

    private static (int Top, int Foot, int Left, int Right, int Lit) Silhouette(GamePicture said)
    {
        int top = -1, foot = -1, left = said.Width, right = -1, lit = 0;

        for (var y = 0; y < said.Height; y++)
        {
            for (var x = 0; x < said.Width; x++)
            {
                if (said.Rgba[(((y * said.Width) + x) * 4) + 3] == 0)
                {
                    continue;
                }

                lit++;
                if (top < 0)
                {
                    top = y;
                }

                foot = y;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        }

        return (top, foot, left, right, lit);
    }

    private static byte Middle(GamePicture said)
        => said.Rgba[((((said.Height / 2) * said.Width) + (said.Width / 2)) * 4) + 0];

    /// <summary>
    /// A post four times longer than it is wide, lying along the model's up axis.
    /// </summary>
    /// <remarks>
    /// SHAPED LIKE THE GAME'S. Its z runs from -40 to 0, the way a monster's does - the head at
    /// the far end and the feet near zero - so a renderer that gets either the axis or the sign
    /// wrong draws it differently and the tests above catch which.
    /// </remarks>
    private static SkinnedMesh Post(bool crossbar = false)
    {
        var places = new List<Vector3>();
        var indices = new List<int>();

        void Quad(float x0, float x1, float z0, float z1)
        {
            int at = places.Count;
            places.Add(new Vector3(x0, 0f, z0));
            places.Add(new Vector3(x1, 0f, z0));
            places.Add(new Vector3(x1, 0f, z1));
            places.Add(new Vector3(x0, 0f, z1));
            indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }

        Quad(-5f, 5f, -40f, 0f);
        if (crossbar)
        {
            // Only on the upper half - the end away from zero, which is the model's head.
            Quad(-20f, 20f, -36f, -30f);
        }

        return Built(places, indices);
    }

    /// <summary>Two quads facing the viewer at different distances, in either order.</summary>
    private static SkinnedMesh Pair(bool farLast)
    {
        var places = new List<Vector3>();
        var indices = new List<int>();

        if (farLast)
        {
            Quad(places, indices, near: false);
            Quad(places, indices, near: true);
        }
        else
        {
            Quad(places, indices, near: true);
            Quad(places, indices, near: false);
        }

        return Built(places, indices, Least, Most);
    }

    /// <summary>One of the two quads on its own, in the SAME frame the pair is drawn in.</summary>
    /// <remarks>
    /// THE BOX IS HANDED IN rather than worked out from the geometry, and that is the whole point:
    /// the camera is placed from the box, so a quad drawn alone inside its own tight box would sit
    /// at a different distance and shade differently for a reason that has nothing to do with
    /// depth. Same box, same camera, so the only thing that can differ is which surface won.
    /// </remarks>
    private static SkinnedMesh Single(bool near)
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Quad(places, indices, near);
        return Built(places, indices, Least, Most);
    }

    private static readonly Vector3 Least = new(-10f, -9f, -10f);
    private static readonly Vector3 Most = new(10f, 15f, 10f);

    /// <summary>One quad. The near one is tilted, so the two shade differently.</summary>
    private static void Quad(List<Vector3> places, List<int> indices, bool near)
    {
        float y = near ? 9f : -9f;
        float tilt = near ? 6f : 0f;

        int at = places.Count;
        places.Add(new Vector3(-10f, y, -10f));
        places.Add(new Vector3(10f, y, -10f));
        places.Add(new Vector3(10f, y + tilt, 10f));
        places.Add(new Vector3(-10f, y + tilt, 10f));
        indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
    }

    /// <summary>Turns loose geometry into a mesh, with normals worked out per triangle.</summary>
    /// <param name="places">The vertices.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="box">
    /// The bounding box to claim, or null to take the geometry's own. Handed in where two meshes
    /// have to be drawn from the same camera - see <see cref="Single"/>.
    /// </param>
    private static SkinnedMesh Built(
        List<Vector3> places, List<int> indices, Vector3? box = null, Vector3? far = null)
    {
        var normals = new Vector3[places.Count];
        for (var one = 0; one + 2 < indices.Count; one += 3)
        {
            Vector3 face = Vector3.Cross(
                places[indices[one + 1]] - places[indices[one]],
                places[indices[one + 2]] - places[indices[one]]);

            if (face.LengthSquared() > 1e-6f)
            {
                face = Vector3.Normalize(face);
            }

            for (var part = 0; part < 3; part++)
            {
                normals[indices[one + part]] = face;
            }
        }

        var least = new Vector3(float.MaxValue);
        var most = new Vector3(float.MinValue);
        foreach (Vector3 place in places)
        {
            least = Vector3.Min(least, place);
            most = Vector3.Max(most, place);
        }

        return SkinnedMesh.Of(
            [.. places], normals, [.. indices], box ?? least, far ?? most);
    }
}
