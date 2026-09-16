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
    [InlineData(PictureLadder.Usual)]
    [InlineData(1024)]
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
    [InlineData(700, PictureLadder.Usual)]
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

    // Past the DEFAULT ladder's own cap, so these are the cap rather than the next rung up. The
    // uncapped behaviour is in DraggingDrawsOneRungLower, which builds a ladder to the top.
    [InlineData(769, PictureLadder.Usual)]
    [InlineData(5000, PictureLadder.Usual)]
    public void APictureIsDrawnAtTheRungThatCoversIt(float side, int drawn)
        => Assert.Equal(drawn, new PictureLadder().For(side));

    /// <summary>Nothing is ever drawn above the cap, whatever width is asked for.</summary>
    [Fact]
    public void TheCapIsNeverExceeded()
    {
        var small = new PictureLadder(PictureLadder.Smallest);

        foreach (float side in new[] { 1f, 300f, 1000f, 9000f })
        {
            Assert.Equal(PictureLadder.Smallest, small.For(side));
            Assert.True(new PictureLadder(512).For(side) <= 512);
        }
    }

    /// <summary>
    /// Dragging draws one rung lower, and never below the bottom one.
    /// </summary>
    /// <remarks>
    /// THE POINT IS THE AREA. Rungs roughly double, so one down is about a quarter of the work -
    /// which is what buys a smooth drag at a high cap. A step that only shaved a little off would
    /// not be worth the softer picture it costs.
    /// </remarks>
    [Theory]
    [InlineData(1000, 1024, PictureLadder.Usual)]
    [InlineData(500, 512, 384)]
    [InlineData(300, 384, 256)]
    [InlineData(100, 256, 256)]
    public void DraggingDrawsOneRungLower(float side, int still, int moving)
    {
        var ladder = new PictureLadder(MeshPicture.Widest);

        Assert.Equal(still, ladder.For(side));
        Assert.Equal(moving, ladder.Dragging(side));
        Assert.True(ladder.Dragging(side) <= ladder.For(side));
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
