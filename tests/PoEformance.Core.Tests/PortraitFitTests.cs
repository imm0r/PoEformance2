using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the monster's picture goes beside the words.
/// </summary>
/// <remarks>
/// THE COMPLAINT THIS ANSWERS, from the live client: a band of nothing between the text and the
/// model, wide enough that the window had to be dragged much wider than the content needed. The
/// old rule was "half the pane, flush right", which on a 1030-pixel pane gave a 515-pixel picture
/// hard against the edge while the words used about 210 - so the pane paid for the empty middle
/// AND for the picture.
///
/// IT IS TESTED AT ALL because the sum moved out of the overlay to get here. The last version of
/// this arithmetic lived in MonsterBookWindow, where nothing could reach it, and a sibling of it
/// shipped a cap of zero that hid every model in the book. See PictureLadderTests.
/// </remarks>
public class PortraitFitTests
{
    /// <summary>
    /// The reported case: the picture follows the words instead of hugging the right edge.
    /// </summary>
    /// <remarks>
    /// THE NUMBERS ARE THE ONES FROM THE SCREENSHOT. A pane of 1030 with words about 210 wide used
    /// to give a 515-pixel picture starting at 515 - a gap of about 300 between them. The same
    /// pane now gives the full 768 starting just after the words.
    /// </remarks>
    [Fact]
    public void ThePictureFollowsTheWordsRatherThanTheRightEdge()
    {
        const float Pane = 1030f;
        PortraitFit fit = PortraitFit.Of(Pane, words: 210f, most: PictureLadder.Usual);

        Assert.True(fit.Shown);

        // EVERYTHING THE WORDS DID NOT NEED, which at this pane is 766 - two short of the cap, and
        // no loss at all: the ladder rounds 766 UP to 768 to draw at, so the only difference is
        // that ImGui shows it two pixels smaller. The old rule handed back 515.
        Assert.Equal(Pane - PortraitFit.LeastColumn - PortraitFit.Gutter, fit.Side);
        Assert.True(fit.Side > Pane * 0.5f, "and more than the half-a-pane rule it replaces");
        Assert.Equal(PictureLadder.Usual, new PictureLadder().For(fit.Side));

        // Where it starts: the column it left for the words, plus the gutter. The column is
        // floored, so a monster with short names still gets the floor rather than 210.
        Assert.Equal(PortraitFit.LeastColumn + PortraitFit.Gutter, fit.Left);
    }

    /// <summary>A wider column pushes the picture along, and it is the words that decide.</summary>
    [Theory]
    [InlineData(0f, PortraitFit.LeastColumn)]
    [InlineData(100f, PortraitFit.LeastColumn)]
    [InlineData(PortraitFit.LeastColumn, PortraitFit.LeastColumn)]
    [InlineData(400f, 400f)]
    [InlineData(600f, 600f)]
    public void TheColumnIsWhatTheWordsMeasuredWithAFloor(float words, float column)
    {
        PortraitFit fit = PortraitFit.Of(pane: 1400f, words: words, most: PictureLadder.Usual);

        Assert.True(fit.Shown);
        Assert.Equal(column, fit.Column);
        Assert.Equal(column + PortraitFit.Gutter, fit.Left);
    }

    /// <summary>
    /// One very long line cannot squeeze the picture away.
    /// </summary>
    /// <remarks>
    /// WITHOUT THE CEILING, how big a monster's portrait came out would depend on the wording of
    /// its modifiers - and a single mod sentence is easily half a pane. Past the ceiling the words
    /// run under the picture, which is ugly for one row rather than wrong for the whole pane.
    /// </remarks>
    [Fact]
    public void AVeryLongLineDoesNotSqueezeThePictureAway()
    {
        PortraitFit fit = PortraitFit.Of(pane: 1200f, words: 5000f, most: PictureLadder.Usual);

        Assert.True(fit.Shown);
        Assert.Equal(1200f * PortraitFit.MostColumn, fit.Column);
        Assert.True(fit.Side >= PortraitFit.LeastPortrait, $"the picture came out at {fit.Side}");
    }

    /// <summary>The picture never overruns the pane, whatever the column and the cap.</summary>
    [Theory]
    [InlineData(400f, 0f)]
    [InlineData(600f, 210f)]
    [InlineData(1030f, 210f)]
    [InlineData(2400f, 800f)]
    [InlineData(4000f, 0f)]
    public void ThePictureAlwaysFitsInsideThePane(float pane, float words)
    {
        PortraitFit fit = PortraitFit.Of(pane, words, PictureLadder.Usual);
        if (!fit.Shown)
        {
            return;
        }

        Assert.True(
            fit.Left + fit.Side <= pane + 0.01f,
            $"a {fit.Side} picture at {fit.Left} runs past a pane of {pane}");

        Assert.True(fit.Side <= PictureLadder.Usual, "never above the cap");
        Assert.True(fit.Side >= PortraitFit.LeastPortrait, "never below what is worth drawing");
    }

    /// <summary>A pane with no room for a picture has none, rather than a sliver of one.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-100f)]
    [InlineData(100f)]
    [InlineData(PortraitFit.LeastColumn)]
    [InlineData(PortraitFit.LeastColumn + PortraitFit.Gutter + PortraitFit.LeastPortrait - 1f)]
    public void ANarrowPaneHasNoPictureAtAll(float pane)
        => Assert.False(PortraitFit.Of(pane, words: 0f, most: PictureLadder.Usual).Shown);

    /// <summary>And one pixel wider than that, it has one.</summary>
    [Fact]
    public void OnePixelWiderAndThePictureAppears()
    {
        const float Enough = PortraitFit.LeastColumn + PortraitFit.Gutter + PortraitFit.LeastPortrait;

        Assert.True(PortraitFit.Of(Enough, words: 0f, most: PictureLadder.Usual).Shown);
        Assert.Equal(
            PortraitFit.LeastPortrait,
            PortraitFit.Of(Enough, words: 0f, most: PictureLadder.Usual).Side);
    }

    /// <summary>
    /// A width that is not a number is not a narrow pane.
    /// </summary>
    /// <remarks>
    /// ImGui's content region can report one before a pane has been laid out. Every comparison in
    /// here answers false against a NaN, so without the guard the answer would come out as "there
    /// is room" - the most confident possible reading of no information at all.
    /// </remarks>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void APaneThatIsNotANumberHasNoPicture(float pane)
        => Assert.False(PortraitFit.Of(pane, words: 210f, most: PictureLadder.Usual).Shown);

    /// <summary>A column that is not a number falls back to the floor rather than spreading.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-50f)]
    public void AColumnThatIsNotANumberFallsBackToTheFloor(float words)
    {
        PortraitFit fit = PortraitFit.Of(pane: 1030f, words: words, most: PictureLadder.Usual);

        Assert.True(fit.Shown);
        Assert.Equal(PortraitFit.LeastColumn, fit.Column);
    }

    /// <summary>A smaller cap is obeyed, and the words simply keep the rest.</summary>
    [Fact]
    public void ASmallerCapIsObeyed()
    {
        PortraitFit fit = PortraitFit.Of(pane: 2000f, words: 300f, most: 384);

        Assert.True(fit.Shown);
        Assert.Equal(384f, fit.Side);
        Assert.Equal(300f, fit.Column);
    }
}
