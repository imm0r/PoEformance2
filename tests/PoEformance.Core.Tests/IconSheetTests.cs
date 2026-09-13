using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The grid the one sprite sheet is cut on, and what happens at its edges.
/// </summary>
/// <remarks>
/// The numbers here are the sheet that ships - 896x4928 at 64 pixels, which is 14 by 77 - and
/// they are MEASURED rather than declared: the sheet has no gutters, so the grid was read off
/// the fully transparent rows, every one of which lands on or beside a multiple of 64. See the
/// remarks on <see cref="IconSheet"/>. A guess of 32 or 128 divides the width just as neatly
/// and shreds every icon, so it is worth a test that says which one this is.
/// </remarks>
public class IconSheetTests
{
    private const float SheetWide = 896f;
    private const float SheetTall = 4928f;

    [Fact]
    public void TheShippedSheetIsFourteenBySeventySeven()
    {
        Assert.Equal(14, IconSheet.ColumnsIn(SheetWide));
        Assert.Equal(77, IconSheet.RowsIn(SheetTall));
        Assert.Equal(1078, IconSheet.CountIn(SheetWide, SheetTall));
    }

    [Fact]
    public void ACellNumberAndItsColumnAndRowAreTheSameThing()
    {
        Assert.Equal((0, 0), IconSheet.CellAt(0, SheetWide, SheetTall));
        Assert.Equal((13, 0), IconSheet.CellAt(13, SheetWide, SheetTall));
        Assert.Equal((0, 1), IconSheet.CellAt(14, SheetWide, SheetTall));
        Assert.Equal((3, 7), IconSheet.CellAt((7 * 14) + 3, SheetWide, SheetTall));

        // And back again, for every one of them - which is what the status rules rely on,
        // since they store a column and a row where a marker stores a number.
        for (int index = 0; index < 1078; index += 37)
        {
            (int column, int row) = IconSheet.CellAt(index, SheetWide, SheetTall);
            Assert.Equal(index, IconSheet.IndexOf(column, row, SheetWide, SheetTall));
        }
    }

    /// <summary>
    /// A cell number from a settings file cannot reach past the sheet.
    /// </summary>
    /// <remarks>
    /// THE FAILURE THIS PREVENTS IS NOT A CRASH. Sampling past the edge of a texture draws
    /// whatever the sampler decides to repeat, which looks like the wrong icon rather than like
    /// a broken one - so it gets blamed on the choice, and the sheet having got shorter between
    /// releases is never suspected.
    /// </remarks>
    [Fact]
    public void ACellPastTheEndIsPulledBackOntoTheSheet()
    {
        Assert.Equal((13, 76), IconSheet.CellAt(99_999, SheetWide, SheetTall));
        Assert.Equal((0, 0), IconSheet.CellAt(-5, SheetWide, SheetTall));
        Assert.Equal(1077, IconSheet.IndexOf(99, 99, SheetWide, SheetTall));
        Assert.Equal(0, IconSheet.IndexOf(-3, -3, SheetWide, SheetTall));
    }

    /// <summary>A sheet smaller than one cell still divides by something.</summary>
    /// <remarks>
    /// The column count is a DIVISOR. A sheet that did not ship measures zero by zero, and the
    /// floor of that over 64 is zero - which on the render thread is a division by zero, not a
    /// badly placed icon.
    /// </remarks>
    [Fact]
    public void ASheetTooSmallToHoldACellStillHasOne()
    {
        Assert.Equal(1, IconSheet.ColumnsIn(0f));
        Assert.Equal(1, IconSheet.RowsIn(0f));
        Assert.Equal(1, IconSheet.CountIn(0f, 0f));
        Assert.Equal((0, 0), IconSheet.CellAt(7, 0f, 0f));
    }
}

/// <summary>
/// Pinning a marker that fell off the map to the edge of it, on the bearing it lies along.
/// </summary>
public class MapEdgeTests
{
    /// <summary>A 200x100 map at the origin, so the arithmetic is readable.</summary>
    private static MapView Map(bool large = false)
        => new(Vector2.Zero, 100f, 1f, large, Visible: true, Left: 0f, Top: 0f, Width: 200f, Height: 100f);

    [Fact]
    public void APointOnTheMapIsNotAnEdgeCase()
    {
        Assert.Null(Map().EdgeFor(new Vector2(100f, 50f)));
        Assert.Null(Map().EdgeFor(new Vector2(1f, 1f)));
    }

    /// <summary>
    /// The pinned point keeps the bearing, which is the only thing it is carrying.
    /// </summary>
    /// <remarks>
    /// CLAMPED PER AXIS, everything beyond a corner lands IN that corner - three things in
    /// three directions stack on one point and the direction is lost. This is the test that
    /// says which of the two this is: a point out past the corner at 45 degrees from a map
    /// twice as wide as it is tall comes back on the TOP edge, not at the corner, because the
    /// vertical half-extent runs out first.
    /// </remarks>
    [Fact]
    public void APointBeyondACornerKeepsItsBearing()
    {
        // Centre is (100, 50). Straight right, well past the edge.
        Vector2? right = Map().EdgeFor(new Vector2(1000f, 50f));
        Assert.NotNull(right);
        Assert.Equal(200f, right!.Value.X, 3);
        Assert.Equal(50f, right.Value.Y, 3);

        // Out at 45 degrees. Half-width is 100 and half-height 50, so the vertical runs out
        // first and the point lands on the top edge rather than in the corner.
        Vector2? diagonal = Map().EdgeFor(new Vector2(100f + 400f, 50f - 400f));
        Assert.NotNull(diagonal);
        Assert.Equal(0f, diagonal!.Value.Y, 3);
        Assert.Equal(150f, diagonal.Value.X, 3);
    }

    /// <summary>The inset pulls the marker back inside by the room it needs to be drawn in.</summary>
    [Fact]
    public void TheInsetKeepsTheWholeMarkerOnTheMap()
    {
        Vector2? pinned = Map().EdgeFor(new Vector2(1000f, 50f), inset: 8f);
        Assert.NotNull(pinned);
        Assert.Equal(192f, pinned!.Value.X, 3);
    }

    /// <summary>
    /// A map with no measured rectangle has no edge to pin anything to.
    /// </summary>
    /// <remarks>
    /// Such a map accepts every point - see <see cref="MapView.Contains"/> - so nothing is ever
    /// outside it, and answering anything else here would pin markers onto a frame that was
    /// never measured.
    /// </remarks>
    [Fact]
    public void AMapThatWasNeverMeasuredPinsNothing()
    {
        var unmeasured = new MapView(Vector2.Zero, 100f, 1f, IsLargeMap: false);
        Assert.Null(unmeasured.EdgeFor(new Vector2(9999f, 9999f)));
    }

    /// <summary>Each map has its own switch, because they are looked at for different things.</summary>
    [Fact]
    public void EachMapHasItsOwnSwitch()
    {
        Assert.Equal(StyleCatalogue.Keys.EdgeSmall, StyleCatalogue.EdgeKey(largeMap: false));
        Assert.Equal(StyleCatalogue.Keys.EdgeLarge, StyleCatalogue.EdgeKey(largeMap: true));
    }

    /// <summary>Both switches are real catalogue entries, so they appear in the editor.</summary>
    [Fact]
    public void BothSwitchesAreInTheCatalogue()
    {
        foreach (string key in new[] { StyleCatalogue.Keys.EdgeSmall, StyleCatalogue.Keys.EdgeLarge })
        {
            StyleEntry entry = Assert.Single(StyleCatalogue.Entries.Where(e => e.Key == key));
            Assert.Equal("Map", entry.Group);
            Assert.True(entry.Traits.HasFlag(StyleTraits.Scale));
        }
    }
}
