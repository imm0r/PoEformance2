using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The ladder of sizes a model is drawn at.
/// </summary>
/// <remarks>
/// THIS FILE EXISTS BECAUSE THE ARITHMETIC WAS SOMEWHERE A TEST COULD NOT REACH. It lived in
/// MonsterPortrait, in the overlay, which is Windows-only and which this project does not
/// reference - and a field read one line before it was assigned shipped a cap of ZERO. Every
/// monster in the book lost its portrait silently: not a blank picture and not a message, but no
/// picture area at all, because a cap of zero is narrower than the smallest one worth drawing.
///
/// THE FIRST ASSERTION BELOW IS THE ONE THAT WOULD HAVE CAUGHT IT, and it is the dullest thing
/// anybody could write: ask for the default cap, get the default cap. Dull is the point. The
/// interesting tests in this repo are about the game's file formats, where the surprises live;
/// this is about a number going in and coming out, and it is what actually broke.
/// </remarks>
public class PictureLadderTests
{
    /// <summary>A cap that is already a rung comes back exactly as it was asked for.</summary>
    [Theory]
    [InlineData(256)]
    [InlineData(384)]
    [InlineData(512)]
    [InlineData(768)]
    [InlineData(PictureLadder.Usual)]
    [InlineData(1536)]
    [InlineData(MeshPicture.Widest)]
    public void ACapOnARungIsTheCap(int most)
        => Assert.Equal(most, new PictureLadder(most).Most);

    /// <summary>The default ladder allows the default size, and not something else.</summary>
    [Fact]
    public void TheDefaultLadderAllowsTheDefaultSize()
    {
        Assert.Equal(PictureLadder.Usual, new PictureLadder().Most);
        Assert.Equal(PictureLadder.Usual, new PictureLadder(PictureLadder.Usual).Most);
    }

    /// <summary>A cap between rungs is raised to the next one, and a silly one is clamped.</summary>
    [Theory]
    [InlineData(700, 768)]
    [InlineData(900, PictureLadder.Usual)]
    [InlineData(385, 512)]
    [InlineData(0, PictureLadder.Smallest)]
    [InlineData(-1000, PictureLadder.Smallest)]
    [InlineData(1, PictureLadder.Smallest)]
    [InlineData(999_999, MeshPicture.Widest)]
    public void ACapBetweenRungsIsRaisedAndASillyOneIsClamped(int asked, int given)
        => Assert.Equal(given, new PictureLadder(asked).Most);

    /// <summary>
    /// What a picture of a given width is drawn at: the rung that covers it, never above the cap.
    /// </summary>
    /// <remarks>
    /// COVERS rather than approaches. Drawing at a rung BELOW the shown width leaves ImGui
    /// stretching the picture, which is the one thing the ladder exists to avoid - it is there so
    /// the render size is stable while a pane is resized, not so it can be too small.
    /// </remarks>
    [Theory]
    [InlineData(100, 256)]
    [InlineData(256, 256)]
    [InlineData(256.5f, 384)]
    [InlineData(384, 384)]
    [InlineData(500, 512)]
    [InlineData(PictureLadder.Usual, PictureLadder.Usual)]

    // PAST THE CAP AND STILL CLIMBING, which is the point: the cap is on what TURNING may cost,
    // and at rest there is one frame to pay for. Clamping here was a picture that stopped growing
    // with its pane however far the boundary was dragged.
    [InlineData(769, 1024)]
    [InlineData(5000, MeshPicture.Widest)]
    public void APictureIsDrawnAtTheRungThatCoversIt(float side, int drawn)
        => Assert.Equal(drawn, new PictureLadder().For(side));

    /// <summary>
    /// The cap binds a DRAG and nothing else.
    /// </summary>
    /// <remarks>
    /// WHAT WAS MEASURED WAS A COST PER FRAME - 8.0 ms at 768, 25.1 at 1536 - and a cost per frame
    /// only matters where there are frames. Turning is where they are; at rest the picture is drawn
    /// once, when the monster changes or the drag ends. So the cap is the answer to "how much
    /// processor may turning this cost", and applying it at rest bought nothing and showed as a
    /// picture that would not grow with its pane.
    /// </remarks>
    [Fact]
    public void TheCapBindsADragAndNothingElse()
    {
        var small = new PictureLadder(PictureLadder.Smallest);
        var middling = new PictureLadder(512);

        foreach (float side in new[] { 300f, 1000f, 9000f })
        {
            Assert.True(small.Moving(side) <= PictureLadder.Smallest, "a drag stays under the cap");
            Assert.True(middling.Moving(side) <= 512, "and under a larger one");

            // At rest the two ladders agree with each other and with the ladder that has no cap
            // worth speaking of: what is asked for is what is drawn.
            Assert.Equal(PictureLadder.Snap((int)side), small.For(side));
            Assert.Equal(small.For(side), middling.For(side));
        }
    }

    /// <summary>
    /// Moving draws at the pane's own rung, and the cap is the only thing that lowers it.
    /// </summary>
    /// <remarks>
    /// THE RUNG UNDER THE CAP IS GONE, and this is what pins that it stays gone: the four-times
    /// discount a drag used to take was what the live client saw as a blurred monster while an
    /// animation played on a wide pane. With the rasteriser in bands on every core, a moving
    /// picture is drawn at the size it is shown, up to the cap.
    /// </remarks>
    [Theory]
    [InlineData(1000, 1024)]
    [InlineData(500, 512)]
    [InlineData(300, 384)]
    [InlineData(100, 256)]
    [InlineData(2000, MeshPicture.Widest)]
    public void MovingDrawsAtThePanesRungUpToTheCap(float side, int still)
    {
        var open = new PictureLadder(MeshPicture.Widest);
        Assert.Equal(still, open.For(side));
        Assert.Equal(still, open.Moving(side));

        var capped = new PictureLadder(512);
        Assert.Equal(still, capped.For(side));
        Assert.Equal(Math.Min(still, 512), capped.Moving(side));

        // And the usual cap is the one a 1200 px pane plays under near enough to its own size.
        Assert.Equal(1024, PictureLadder.Usual);
        Assert.Equal(1024, new PictureLadder().Moving(1200f));
    }

    /// <summary>Every rung is a real size, in order, and within what the renderer will draw.</summary>
    [Fact]
    public void TheRungsAreOrderedAndDrawable()
    {
        IReadOnlyList<int> steps = PictureLadder.Steps;

        Assert.NotEmpty(steps);
        Assert.Equal(PictureLadder.Smallest, steps[0]);
        Assert.Equal(MeshPicture.Widest, steps[^1]);
        Assert.Contains(PictureLadder.Usual, steps);

        for (var at = 1; at < steps.Count; at++)
        {
            Assert.True(steps[at] > steps[at - 1], $"rung {at} is not above the one before it");
        }
    }

    /// <summary>A width that is not a number falls back rather than reaching the buffers.</summary>
    /// <remarks>
    /// The width comes from ImGui's own content region, and a pane in a frame where it has not
    /// been laid out yet can report something that is not finite. Casting that to int is undefined
    /// and would size a buffer from it.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void AWidthThatIsNotANumberStillGivesARung(float side)
    {
        int drawn = new PictureLadder().For(side);

        Assert.Contains(drawn, PictureLadder.Steps);
        Assert.True(drawn is >= PictureLadder.Smallest and <= PictureLadder.Usual);
    }
}
