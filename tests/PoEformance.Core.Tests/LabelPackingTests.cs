using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Fitting labels onto a map without stacking them.
/// </summary>
/// <remarks>
/// What this replaced is a number nobody can pick. Thinning the room names by SIZE cannot work,
/// because a zone is built from one module repeated: at nine tiles the map was solid text and at
/// ten there were four labels left, with nothing in between. Space on screen is the honest
/// constraint, and it moves with the zoom.
/// </remarks>
public class LabelPackingTests
{
    private static ScreenRect At(float left, float top, float width = 40f, float height = 10f)
        => new(left, top, left + width, top + height);

    private static List<int> Kept(IReadOnlyList<ScreenRect> candidates, float padding = 2f)
    {
        var kept = new List<int>();
        LabelPacking.Keep(candidates, kept, padding);
        return kept;
    }

    [Fact]
    public void LabelsThatDoNotTouchAreAllKept()
        => Assert.Equal([0, 1, 2], Kept([At(0, 0), At(100, 0), At(0, 100)]));

    [Fact]
    public void TheLaterOfTwoOverlappingLabelsIsDropped()
    {
        // FIRST OFFERED WINS, which is what makes the caller's order the priority: the rooms
        // arrive rarest first, so the name that survives is the more informative one.
        Assert.Equal([0], Kept([At(0, 0), At(10, 0)]));
    }

    [Fact]
    public void ALabelIsMeasuredAgainstWhatWasKeptRatherThanWhatWasOffered()
    {
        // The middle one is dropped, so the third is compared against the FIRST - and it clears
        // it. Comparing against everything offered would drop a label for colliding with one
        // that is not on the screen.
        Assert.Equal([0, 2], Kept([At(0, 0), At(20, 0), At(60, 0)], padding: 0f));
    }

    [Fact]
    public void PaddingIsTheGapBetweenTwoLabelsRatherThanTwiceIt()
    {
        // Grown on one side of the comparison only. Growing both would leave twice the padding
        // and a map with far fewer names on it than there is room for.
        Assert.Equal([0, 1], Kept([At(0, 0), At(43, 0)], padding: 2f));
        Assert.Equal([0], Kept([At(0, 0), At(41, 0)], padding: 2f));
    }

    [Fact]
    public void TouchingEdgesAreNotAnOverlap()
        => Assert.Equal([0, 1], Kept([At(0, 0), At(40, 0)], padding: 0f));

    [Fact]
    public void NothingOfferedKeepsNothing()
        => Assert.Empty(Kept([]));

    [Fact]
    public void TheListIsClearedBeforeItIsFilled()
    {
        // Reused frame after frame, so a stale index left in it would draw last frame's label
        // at this frame's position.
        var kept = new List<int> { 7, 9 };
        LabelPacking.Keep([At(0, 0)], kept);

        Assert.Equal([0], kept);
    }
}
