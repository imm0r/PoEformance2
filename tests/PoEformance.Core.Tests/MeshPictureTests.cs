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

    /// <summary>
    /// The ground is drawn only when asked, and is edge on at a level view.
    /// </summary>
    /// <remarks>
    /// EDGE ON IS RIGHT RATHER THAN BROKEN, and worth pinning down so nobody "fixes" it: a floor
    /// seen from its own height is a line. Counting the ROWS it adds is what tells the two apart -
    /// a level view touches one or two, a tilted one opens the floor out over dozens.
    /// </remarks>
    [Fact]
    public void TheGroundIsDrawnOnlyWhenAskedAndIsEdgeOnAtALevelView()
    {
        GamePicture bare = MeshPicture.Of(Post(), 200);

        (int Pixels, int Rows) level = Extra(bare, MeshPicture.Of(Post(), 200, ground: true));
        (int Pixels, int Rows) tilted = Extra(
            bare, MeshPicture.Of(Post(), 200, tilt: 0.6f, ground: true));

        Assert.Equal((0, 0), Extra(bare, MeshPicture.Of(Post(), 200)));
        Assert.True(level.Pixels > 0, "asking for the ground should draw something");
        Assert.True(level.Rows <= 3, $"a level view should see the floor edge on and saw {level.Rows} rows");
        Assert.True(tilted.Rows > 20, $"a tilted view should open the floor out and saw {tilted.Rows} rows");
    }

    /// <summary>
    /// The model is never painted over by the floor it stands on.
    /// </summary>
    /// <remarks>
    /// THE CASE THAT BREAKS IT IS THE SOLE OF THE FOOT. The floor sits at the model's own lowest
    /// point, so drawn exactly there it shares a depth with the lowest triangles - and a depth
    /// test that keeps the first arrival keeps the FLOOR, which draws grid lines across a
    /// monster's feet. MeshPicture.Under is the hair of clearance that settles it, and this is
    /// what notices if it is ever removed.
    ///
    /// A MODEL WITH A FLAT SOLE IS WHAT IT TAKES TO SHOW IT, which the first version of this test
    /// did not have. A post standing on its end meets the floor along one edge, and the pixel
    /// centres either side of that edge interpolate to depths that are never exactly equal - so it
    /// passed with the clearance removed and proved nothing. A quad LYING IN the foot plane is
    /// coplanar with the floor over its whole area, which is the ordinary shape of a base or a
    /// shadow plate, and every pixel of it is a tie.
    ///
    /// LOOKING DOWN ON PURPOSE: from below the floor is genuinely between the eye and the model
    /// and covering it is then correct, so the assertion would be wrong at a negative tilt.
    /// </remarks>
    [Fact]
    public void TheModelIsNeverPaintedOverByTheFloorItStandsOn()
    {
        GamePicture bare = MeshPicture.Of(Soled(), 200, tilt: 0.5f);
        GamePicture floored = MeshPicture.Of(Soled(), 200, tilt: 0.5f, ground: true);

        var over = 0;
        for (var at = 0; at < bare.Rgba.Length; at += 4)
        {
            // Only where the model itself was drawn. The floor may paint the background freely,
            // which is the entire point of it.
            if (bare.Rgba[at + 3] == 0)
            {
                continue;
            }

            if (bare.Rgba[at] != floored.Rgba[at]
                || bare.Rgba[at + 1] != floored.Rgba[at + 1]
                || bare.Rgba[at + 2] != floored.Rgba[at + 2])
            {
                over++;
            }
        }

        Assert.Equal(0, over);
    }

    /// <summary>
    /// The floor runs off the frame at the steepest tilt, and has faded to nothing by the time it gets there.
    /// </summary>
    /// <remarks>
    /// THE OPPOSITE OF WHAT THIS TEST FIRST PINNED. The floor used to be sized to stay inside the
    /// frame at every tilt, and it was reported from the live client as a small plate turning with
    /// the model rather than a floor it stands on. Now it runs off the frame, and what is pinned
    /// instead is the fade: nothing solid on the frame's own border, and stronger near the middle
    /// than out by the edge, so the floor ends in air and never in a hard line. The portrait clamps
    /// tilt to a third of a turn either way, so those are the two angles that have to hold.
    /// </remarks>
    [Theory]
    [InlineData(1.047f)]
    [InlineData(-1.047f)]
    public void TheFloorRunsOffTheFrameAndFadesOutBeforeIt(float tilt)
    {
        const int Side = 240;
        GamePicture bare = MeshPicture.Of(Post(crossbar: true), Side, tilt: tilt);
        GamePicture floored = MeshPicture.Of(Post(crossbar: true), Side, tilt: tilt, ground: true);

        // The floor is broad, and reaches the bottom of the frame rather than stopping short.
        (int Pixels, int Rows) extra = Extra(bare, floored);
        Assert.True(extra.Pixels > 500, $"the floor should be broad and added {extra.Pixels} pixels");
        Assert.True(Silhouette(floored).Foot >= Side - 3, "the floor should run to the frame's edge");

        var border = 0;
        var near = 0;
        var far = 0;
        float radius = Side * 0.5f;
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                int at = ((y * Side) + x) * 4;
                byte alpha = floored.Rgba[at + 3];
                if (bare.Rgba[at + 3] != 0 || alpha == 0)
                {
                    continue;
                }

                if (x == 0 || y == 0 || x == Side - 1 || y == Side - 1)
                {
                    border = Math.Max(border, alpha);
                }

                float dx = x + 0.5f - radius;
                float dy = y + 0.5f - radius;
                float away = MathF.Sqrt((dx * dx) + (dy * dy)) / radius;
                if (away < 0.4f)
                {
                    near = Math.Max(near, alpha);
                }
                else if (away > 0.85f)
                {
                    far = Math.Max(far, alpha);
                }
            }
        }

        Assert.True(border <= 8, $"the floor reaches the frame's border at alpha {border}");
        Assert.True(near > 200, $"the floor should be solid near the middle, not {near}");
        Assert.True(near > far, $"and fade outwards, but is {near} near the middle against {far} by the edge");
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

    /// <summary>Where and how much two pictures of the same model differ.</summary>
    private static (int Pixels, int Rows) Extra(GamePicture bare, GamePicture floored)
    {
        var pixels = 0;
        var rows = 0;

        for (var y = 0; y < bare.Height; y++)
        {
            var any = false;
            for (var x = 0; x < bare.Width; x++)
            {
                int at = (((y * bare.Width) + x) * 4);
                if (bare.Rgba[at] == floored.Rgba[at]
                    && bare.Rgba[at + 1] == floored.Rgba[at + 1]
                    && bare.Rgba[at + 2] == floored.Rgba[at + 2]
                    && bare.Rgba[at + 3] == floored.Rgba[at + 3])
                {
                    continue;
                }

                pixels++;
                any = true;
            }

            if (any)
            {
                rows++;
            }
        }

        return (pixels, rows);
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

    /// <summary>
    /// A post standing on a flat base, the base lying exactly in the plane the ground is drawn on.
    /// </summary>
    /// <remarks>
    /// COPLANAR ON PURPOSE. The base sits at the box's own Most.Z, which is where the floor goes,
    /// so every pixel of it is at the same depth as the floor under it - the tie that decides
    /// whether a monster's feet come out with grid lines across them. Real monsters carry shapes
    /// like this; a post on its end does not.
    /// </remarks>
    private static SkinnedMesh Soled()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();

        int at = places.Count;
        places.Add(new Vector3(-5f, 0f, -40f));
        places.Add(new Vector3(5f, 0f, -40f));
        places.Add(new Vector3(5f, 0f, 0f));
        places.Add(new Vector3(-5f, 0f, 0f));
        indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);

        at = places.Count;
        places.Add(new Vector3(-8f, -8f, 0f));
        places.Add(new Vector3(8f, -8f, 0f));
        places.Add(new Vector3(8f, 8f, 0f));
        places.Add(new Vector3(-8f, 8f, 0f));
        indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);

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
