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
    /// <remarks>
    /// THE HALF TURN IS THE ONE THAT EARNS ITS KEEP: seen from behind, every triangle of the post
    /// winds the other way on the picture, and a rasteriser that only fills triangles wound one
    /// way draws nothing at all of it. The format's winding is not established, so both ways
    /// have to fill - and the tests' own meshes happen to wind one way from the front.
    /// </remarks>
    [Fact]
    public void AWholeTurnComesBackToWhereItStarted()
    {
        GamePicture start = MeshPicture.Of(Post(crossbar: true), 96);
        GamePicture quarter = MeshPicture.Of(Post(crossbar: true), 96, MathF.PI / 2f);
        GamePicture half = MeshPicture.Of(Post(crossbar: true), 96, MathF.PI);
        GamePicture whole = MeshPicture.Of(Post(crossbar: true), 96, MathF.Tau);

        Assert.NotEqual(Silhouette(start).Right - Silhouette(start).Left, Silhouette(quarter).Right - Silhouette(quarter).Left);
        Assert.Equal(Silhouette(start).Lit, Silhouette(half).Lit);
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
        Mipmaps red = Sheet(220, 30, 30);

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
    /// Each shape of a mesh is painted with its own texture.
    /// </summary>
    /// <remarks>
    /// REPORTED FROM THE LIVE CLIENT, and it is the failure one texture per MODEL produces on a
    /// monster built out of parts: Bahlak the Sky Seer came out black with red patches where the
    /// game draws him in feathers. Every shape's coordinates address ITS OWN sheet, so painting
    /// the wings from the body's texture samples whatever happens to sit at those coordinates -
    /// a picture that is wrong rather than missing, which is the harder kind to notice.
    ///
    /// THE TWO HALVES ARE THE ASSERTION. The mesh is two quads, one shape each, and the two
    /// sheets are flat colours - so "the left half is red and the right half is blue" is the
    /// whole claim, and a renderer that kept one texture for the mesh paints both the same.
    /// </remarks>
    [Fact]
    public void EveryShapeWearsItsOwnTexture()
    {
        const int Size = 128;

        Mipmaps red = Sheet(220, 30, 30);
        Mipmaps blue = Sheet(30, 30, 220);
        SkinnedMesh parts = Parts();

        GamePicture apart = MeshPicture.Of(parts, Size, skin: red, skins: [red, blue]);

        (byte leftRed, byte leftBlue) = Spot(apart, Size / 4, Size / 2);
        (byte rightRed, byte rightBlue) = Spot(apart, Size * 3 / 4, Size / 2);

        Assert.True(leftRed > leftBlue * 2, $"the left shape should be red: {leftRed},{leftBlue}");
        Assert.True(rightBlue > rightRed * 2, $"the right shape should be blue: {rightRed},{rightBlue}");

        // WITHOUT THE LIST, NOTHING CHANGES. Every monster whose .ao names one material has to
        // draw exactly as it did, so the single skin still covers the whole mesh.
        GamePicture whole = MeshPicture.Of(parts, Size, skin: red);
        (byte wasRed, byte wasBlue) = Spot(whole, Size * 3 / 4, Size / 2);
        Assert.True(wasRed > wasBlue * 2, $"one skin should cover both shapes: {wasRed},{wasBlue}");

        // A shape with no texture of its own falls back to ink rather than to a neighbour's
        // sheet - the caller decides what to hand over, and null means "not this one".
        GamePicture half = MeshPicture.Of(parts, Size, skin: null, skins: [red, null]);
        (byte inkRed, byte inkBlue) = Spot(half, Size * 3 / 4, Size / 2);
        Assert.True(
            inkRed > 0 && Math.Abs(inkRed - inkBlue) < 40,
            $"an unpainted shape should be plain ink: {inkRed},{inkBlue}");
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
        Mipmaps halves = Halved();

        GamePicture below = MeshPicture.Of(Coated(-0.25f), 64, skin: halves);
        GamePicture above = MeshPicture.Of(Coated(0.75f), 64, skin: halves);

        Assert.Equal(Middle(below), Middle(above));
        Assert.NotEqual(Middle(MeshPicture.Of(Coated(0.25f), 64, skin: halves)), Middle(below));
    }

    /// <summary>
    /// A finely patterned skin shrunk onto a small picture comes out as its average, not as grain.
    /// </summary>
    /// <remarks>
    /// THE TEST FOR THE REPORT FROM THE LIVE CLIENT: "grisselig", like poor reception. A skin of
    /// alternating black and white texels is the harshest case of detail below the pixel - every
    /// pixel of the quad covers several of each - and a sampler that picks one texel per pixel
    /// paints it as noise, black or white by whichever texel the pixel centre happened to land on.
    /// Read from the right level, every pixel is the same grey, because the average of that
    /// pattern is the same everywhere.
    ///
    /// THE SPREAD IS THE ASSERTION, not the grey itself: the shade the quad is lit at multiplies
    /// the colour, so the value is what it is, but every covered pixel must have the SAME one. One
    /// texel picked per pixel puts the spread at the whole range.
    /// </remarks>
    [Fact]
    public void AFinelyPatternedSkinShrunkComesOutEvenRatherThanGrainy()
    {
        GamePicture drawn = MeshPicture.Of(Papered(), 64, skin: Checkered(256));

        (byte least, byte most, int covered) = Spread(drawn);
        Assert.True(covered > 500, $"the quad should cover most of the picture and covered {covered} pixels");
        Assert.True(
            most - least <= 6,
            $"the shrunk checkerboard came out between {least} and {most}, which is grain rather than its average");
        Assert.True(least > 20, $"and the average of black and white is a grey, not {least}");
    }

    /// <summary>A skin magnified onto a big picture is smooth across a texel, not blocky.</summary>
    /// <remarks>
    /// THE OTHER END OF THE SAME CHANGE. Reading the level nearest a pixel's own size is a shrink's
    /// answer; a two-by-two skin on a quad forty pixels across is a magnification, and there the
    /// texels themselves are what a nearest read shows - four flat blocks with hard edges. Read
    /// bilinearly the quad runs smoothly from one texel's colour to the next, which a count of the
    /// values it takes tells apart: four blocks are four values, a ramp is dozens.
    /// </remarks>
    [Fact]
    public void AMagnifiedSkinIsSmoothAcrossATexel()
    {
        GamePicture drawn = MeshPicture.Of(Papered(), 64, skin: Checkered(2));

        var values = new HashSet<byte>();
        for (var at = 0; at < drawn.Rgba.Length; at += 4)
        {
            if (drawn.Rgba[at + 3] != 0)
            {
                values.Add(drawn.Rgba[at]);
            }
        }

        Assert.True(values.Count > 12, $"a magnified texel should ramp and took {values.Count} values");
    }

    /// <summary>The levels halve down to one texel, each the average of the four above it.</summary>
    [Fact]
    public void TheLevelsHalveDownToOneTexelAndAverage()
    {
        // Four by two, red running 0, 40, 80, 120 along the top row and 200 across the bottom one.
        var rgba = new byte[4 * 2 * 4];
        for (var x = 0; x < 4; x++)
        {
            rgba[x * 4] = (byte)(x * 40);
            rgba[(4 + x) * 4] = 200;
        }

        Mipmaps? levels = Mipmaps.Of(new GamePicture(4, 2, rgba));
        Assert.NotNull(levels);
        Assert.Equal(3, levels.Count);
        Assert.Equal((2, 1), (levels[1].Width, levels[1].Height));
        Assert.Equal((1, 1), (levels[2].Width, levels[2].Height));

        // (0 + 40 + 200 + 200) / 4 and (80 + 120 + 200 + 200) / 4, then the two of those.
        Assert.Equal(110, levels[1].Rgba[0]);
        Assert.Equal(150, levels[1].Rgba[4]);
        Assert.Equal(130, levels[2].Rgba[0]);

        // A level past the last is the last, and nothing makes no levels.
        Assert.Same(levels[2].Rgba, levels[99].Rgba);
        Assert.Null(Mipmaps.Of(null));
        Assert.Null(Mipmaps.Of(new GamePicture(0, 0, [])));
    }

    /// <summary>
    /// A canvas drawn into twice shows the second monster, not the second over the first.
    /// </summary>
    /// <remarks>
    /// THE TEST THAT EARNS THE CANVAS, and the one thing reuse can get wrong that allocating never
    /// could. The pixels a mesh does not cover are precisely where the last drawing shows through,
    /// so what breaks it is a big model followed by a smaller one - or by none, which takes the
    /// early way out before a triangle is ever looked at. Both are ordinary in the book, where one
    /// portrait draws whichever row somebody clicks next.
    ///
    /// AGAINST FRESH BUFFERS RATHER THAN AGAINST A CONSTANT, so the test says what it means:
    /// reusing a canvas is INDISTINGUISHABLE from not reusing one. A check that only counted lit
    /// pixels would pass on a picture with the last monster still standing behind this one.
    /// </remarks>
    [Fact]
    public void ACanvasKeepsNothingOfTheMonsterBeforeIt()
    {
        const int Side = 96;
        var canvas = new MeshPicture.Canvas(Side);

        // Something tall and wide first, so there is as much as possible to leave behind.
        MeshPicture.Of(Post(crossbar: true), canvas);

        foreach (SkinnedMesh next in new[] { Single(near: true), SkinnedMesh.None, Post() })
        {
            byte[] reused = [.. MeshPicture.Of(next, canvas).Rgba];
            byte[] fresh = MeshPicture.Of(next, Side).Rgba;

            Assert.Equal(0, Differing(fresh, reused));
        }
    }

    /// <summary>A canvas draws what buffers of its own would, turn and tilt and skin alike.</summary>
    [Fact]
    public void ACanvasDrawsWhatTheAllocatingWayDraws()
    {
        Mipmaps sheet = Halved();
        var canvas = new MeshPicture.Canvas(64);

        GamePicture lent = MeshPicture.Of(Coated(), canvas, 0.6f, -0.3f, skin: sheet);
        GamePicture made = MeshPicture.Of(Coated(), 64, 0.6f, -0.3f, skin: sheet);

        Assert.Equal(0, Differing(made.Rgba, lent.Rgba));
    }

    /// <summary>A canvas clamps its size the same way the size taken by value is clamped.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(999_999, MeshPicture.Widest)]
    public void ACanvasClampsItsSizeToo(int asked, int given)
        => Assert.Equal(given, new MeshPicture.Canvas(asked).Size);

    /// <summary>However many threads share a canvas, the picture is the one thread's to the byte.</summary>
    /// <remarks>
    /// THE TEST THAT EARNS THE BANDS. Each pixel belongs to one band and each band walks the
    /// triangles in the mesh's order, so the depth test on a pixel never sees two threads - and
    /// the proof is a picture that does not differ by a bit from the sequential one, on a mesh
    /// whose triangles cross band boundaries and each other. A race would show as a scatter of
    /// pixels where the far surface won.
    /// </remarks>
    [Fact]
    public void AnyNumberOfThreadsDrawsTheSamePicture()
    {
        Mipmaps skin = Checkered(64);
        SkinnedMesh mesh = Papered();
        byte[] alone = [.. MeshPicture.Of(mesh, new MeshPicture.Canvas(96, 1), 0.5f, 0.4f, skin: skin).Rgba];

        foreach (int threads in new[] { 2, 3, 7 })
        {
            var canvas = new MeshPicture.Canvas(96, threads);
            Assert.Equal(threads, canvas.Threads);
            Assert.Equal(0, Differing(alone, MeshPicture.Of(mesh, canvas, 0.5f, 0.4f, skin: skin).Rgba));
            Assert.Equal(0, Differing(MeshPicture.Of(Pair(farLast: true), new MeshPicture.Canvas(96, 1)).Rgba, MeshPicture.Of(Pair(farLast: true), canvas).Rgba));
        }

        // Nothing sensible asked for is every processor, and nothing absurd is more than a few dozen.
        Assert.Equal(Environment.ProcessorCount, new MeshPicture.Canvas(8, 0).Threads);
        Assert.Equal(Environment.ProcessorCount, new MeshPicture.Canvas(8, -3).Threads);
        Assert.Equal(64, new MeshPicture.Canvas(8, 1000).Threads);
    }

    /// <summary>No canvas is a mistake in the caller, not a picture of nothing.</summary>
    [Fact]
    public void DrawingWithoutACanvasSaysSo()
        => Assert.Throws<ArgumentNullException>(() => MeshPicture.Of(SkinnedMesh.None, canvas: null!));

    /// <summary>Zooming in paints more of the model, and a runaway wheel stops at the clamp.</summary>
    /// <remarks>
    /// THE CLAMP IS THE HALF WORTH HAVING. Pulling back only wastes frame, but pushing in makes
    /// every triangle cover more pixels - a mesh magnified far enough is a handful of triangles
    /// painting the whole buffer over and over, and a wheel has no end to it.
    /// </remarks>
    [Fact]
    public void ZoomingInFillsMoreOfTheFrameAndStopsAtTheClamp()
    {
        (int Top, int Foot, int Left, int Right, int Lit) fitted
            = Silhouette(MeshPicture.Of(Post(crossbar: true), 200));
        (int Top, int Foot, int Left, int Right, int Lit) close
            = Silhouette(MeshPicture.Of(Post(crossbar: true), 200, zoom: 2f));

        Assert.True(close.Lit > fitted.Lit, $"zoomed in lit {close.Lit} against {fitted.Lit} fitted");
        Assert.True(close.Right - close.Left > fitted.Right - fitted.Left, "and should be wider");

        Assert.Equal(
            Silhouette(MeshPicture.Of(Post(), 200, zoom: MeshPicture.Furthest)),
            Silhouette(MeshPicture.Of(Post(), 200, zoom: 1000f)));

        Assert.Equal(
            Silhouette(MeshPicture.Of(Post(), 200, zoom: MeshPicture.Nearest)),
            Silhouette(MeshPicture.Of(Post(), 200, zoom: -50f)));
    }

    /// <summary>Zooming keeps whatever is under the pointer under it.</summary>
    /// <remarks>
    /// THE MAP'S RULE, checked on the picture rather than on the formula: the corner of the
    /// crossbar is put under the pointer, the zoom doubles towards it, and the corner has to be
    /// drawn where it was to within a pixel. The same zoom into the middle moves it a long way,
    /// which is what shows the check can fail.
    /// </remarks>
    [Fact]
    public void ZoomingKeepsWhatIsUnderThePointerUnderIt()
    {
        const int Side = 200;
        (int Top, int Foot, int Left, int Right, int Lit) fitted
            = Silhouette(MeshPicture.Of(Post(crossbar: true), Side));

        var pointer = new Vector2((fitted.Left + 0.5f) / Side, (fitted.Top + 0.5f) / Side);
        Vector2 pan = MeshPicture.Panned(Vector2.Zero, pointer, 1f, 2f);
        (int Top, int Foot, int Left, int Right, int Lit) closer
            = Silhouette(MeshPicture.Of(Post(crossbar: true), Side, zoom: 2f, pan: pan));

        Assert.InRange(closer.Top, fitted.Top - 1, fitted.Top + 1);
        Assert.InRange(closer.Left, fitted.Left - 1, fitted.Left + 1);

        (int Top, int Foot, int Left, int Right, int Lit) middle
            = Silhouette(MeshPicture.Of(Post(crossbar: true), Side, zoom: 2f));
        Assert.True(
            Math.Abs(middle.Top - fitted.Top) > 5,
            "zooming into the middle should have moved the corner, or this proves nothing");
    }

    /// <summary>Pulling back with the pointer in a corner cannot carry the model out of the frame.</summary>
    [Fact]
    public void PullingBackTowardsACornerKeepsTheModelInTheFrame()
    {
        Vector2 pan = Vector2.Zero;
        var zoom = 1f;
        for (var notch = 0; notch < 12; notch++)
        {
            float next = Math.Max(zoom / 1.18f, MeshPicture.Nearest);
            pan = MeshPicture.Panned(pan, Vector2.Zero, zoom, next);
            zoom = next;
        }

        Assert.Equal(MeshPicture.Nearest, zoom);
        Assert.True(Silhouette(MeshPicture.Of(Post(crossbar: true), 200, zoom: zoom, pan: pan)).Lit > 0);

        // And a pointer that is not a number is taken as the middle rather than believed.
        Assert.Equal(Vector2.Zero, MeshPicture.Panned(Vector2.Zero, new Vector2(float.NaN), 1f, 2f));
    }

    /// <summary>The camera says where a point lands, and the picture draws it there.</summary>
    /// <remarks>
    /// THE FLOOR IS DRAWN FROM THIS CAMERA BY SOMEBODY ELSE - ModelFloor, into the overlay's draw
    /// list - so the camera handed out has to be the one the pixels were drawn with, to the pixel.
    /// The post's own extremes are the reference: its foot, its top and the crossbar's end, each
    /// placed by the camera and each found in the silhouette.
    /// </remarks>
    [Fact]
    public void TheCameraPlacesAPointWhereThePictureDrawsIt()
    {
        const int Side = 200;
        SkinnedMesh post = Post(crossbar: true);
        (int Top, int Foot, int Left, int Right, int Lit) seen = Silhouette(MeshPicture.Of(post, Side));

        MeshPicture.Camera camera = MeshPicture.Camera.Of(post, 0f, 0f, 1f, Vector2.Zero);
        Assert.True(camera.Ready);
        Assert.Equal(40f, camera.Reach);

        Assert.InRange(camera.Place(new Vector3(0f, 0f, -40f)).Y * Side, seen.Top - 1f, seen.Top + 1f);
        Assert.InRange(camera.Place(Vector3.Zero).Y * Side, seen.Foot - 1f, seen.Foot + 1f);
        Assert.InRange(camera.Place(new Vector3(-20f, 0f, -36f)).X * Side, seen.Left - 1f, seen.Left + 1f);
        Assert.InRange(camera.Place(new Vector3(20f, 0f, -36f)).X * Side, seen.Right - 1f, seen.Right + 1f);

        // Nothing to look at is a camera that says so, rather than one that places things anyway.
        Assert.False(MeshPicture.Camera.Of(null, 0f, 0f, 1f, Vector2.Zero).Ready);
        Assert.False(MeshPicture.Camera.Of(SkinnedMesh.None, 0f, 0f, 1f, Vector2.Zero).Ready);
    }

    /// <summary>How many bytes differ, because a failure wants a count rather than two arrays.</summary>
    private static int Differing(byte[] one, byte[] other)
    {
        if (one.Length != other.Length)
        {
            return Math.Max(one.Length, other.Length);
        }

        var apart = 0;
        for (var at = 0; at < one.Length; at++)
        {
            if (one[at] != other[at])
            {
                apart++;
            }
        }

        return apart;
    }

    private static byte Channel(GamePicture said, int part)
        => said.Rgba[((((said.Height / 2) * said.Width) + (said.Width / 2)) * 4) + part];

    /// <summary>A texture of one colour.</summary>
    private static Mipmaps Sheet(byte red, byte green, byte blue)
    {
        var pixels = new byte[8 * 8 * 4];
        for (var one = 0; one < 8 * 8; one++)
        {
            pixels[(one * 4) + 0] = red;
            pixels[(one * 4) + 1] = green;
            pixels[(one * 4) + 2] = blue;
            pixels[(one * 4) + 3] = 255;
        }

        return Levelled(new GamePicture(8, 8, pixels));
    }

    /// <summary>A texture whose lower half differs from its upper one.</summary>
    private static Mipmaps Halved()
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

        return Levelled(new GamePicture(8, 8, pixels));
    }

    /// <summary>A texture of alternating black and white texels, this many each way.</summary>
    private static Mipmaps Checkered(int side)
    {
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                int at = ((y * side) + x) * 4;
                byte tone = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
                pixels[at] = tone;
                pixels[at + 1] = tone;
                pixels[at + 2] = tone;
                pixels[at + 3] = 255;
            }
        }

        return Levelled(new GamePicture(side, side, pixels));
    }

    private static Mipmaps Levelled(GamePicture top)
    {
        Mipmaps? levels = Mipmaps.Of(top);
        Assert.NotNull(levels);
        return levels;
    }

    /// <summary>The least and most red over the covered pixels, and how many there are.</summary>
    private static (byte Least, byte Most, int Covered) Spread(GamePicture drawn)
    {
        byte least = 255;
        byte most = 0;
        var covered = 0;
        for (var at = 0; at < drawn.Rgba.Length; at += 4)
        {
            if (drawn.Rgba[at + 3] == 0)
            {
                continue;
            }

            covered++;
            least = Math.Min(least, drawn.Rgba[at]);
            most = Math.Max(most, drawn.Rgba[at]);
        }

        return (least, most, covered);
    }

    /// <summary>A quad facing the viewer with the whole skin laid across it once, corner to corner.</summary>
    private static SkinnedMesh Papered()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Quad(places, indices, near: true);

        SkinnedMesh bare = Built(places, indices, Least, Most);
        Vector2[] spots = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];

        return SkinnedMesh.Of(bare.Positions, bare.Normals, bare.Indices, Least, Most, spots);
    }

    /// <summary>A quad facing the viewer, with every corner at the same texture coordinate.</summary>
    /// <summary>The red and the blue of one pixel, by its place in the picture.</summary>
    private static (byte Red, byte Blue) Spot(GamePicture said, int x, int y)
    {
        int at = (((y * said.Width) + x) * 4);
        return (said.Rgba[at], said.Rgba[at + 2]);
    }

    /// <summary>
    /// Two quads side by side, one shape each - a monster built out of parts, in miniature.
    /// </summary>
    /// <remarks>
    /// FLAT AND FACING THE CAMERA, so what is being measured is which texture a shape read and
    /// not how it was lit: both halves take the same shade, and only the colour differs. The
    /// coordinates are the same on both, which is the point - a shape's coordinates mean
    /// something only against its own sheet.
    /// </remarks>
    private static SkinnedMesh Parts()
    {
        Vector3[] places =
        [
            new(-10f, 0f, -10f), new(0f, 0f, -10f), new(0f, 0f, 10f), new(-10f, 0f, 10f),
            new(0f, 0f, -10f), new(10f, 0f, -10f), new(10f, 0f, 10f), new(0f, 0f, 10f),
        ];

        var normals = new Vector3[places.Length];
        Array.Fill(normals, new Vector3(0f, 1f, 0f));

        int[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var spots = new Vector2[places.Length];
        Array.Fill(spots, new Vector2(0.5f, 0.5f));

        return SkinnedMesh.Of(
            places,
            normals,
            indices,
            new Vector3(-10f, -1f, -10f),
            new Vector3(10f, 1f, 10f),
            spots,
            [new MeshShape("LeftShape", 0, 6), new MeshShape("RightShape", 6, 6)]);
    }

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
