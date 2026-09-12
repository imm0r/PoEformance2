using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The layout's picture: how it is coloured, how it is shrunk, and which size a map gets.
/// </summary>
public class TerrainPictureTests
{
    private static uint Abgr(byte r, byte g, byte b, byte a)
        => ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

    private static (byte R, byte G, byte B, byte A) Texel(TerrainPicture picture, int x, int y)
    {
        int at = ((y * picture.Width) + x) * 4;
        return (picture.Pixels[at], picture.Pixels[at + 1], picture.Pixels[at + 2], picture.Pixels[at + 3]);
    }

    [Theory]
    [InlineData(0xFFEAE3DCu, 0xB32B1B0Bu)]   // the defaults: pale grey line, dark navy fill
    [InlineData(0xFF0000FFu, 0x80FF0000u)]   // a red line over a blue fill - neither is inside the other
    [InlineData(0xFF404040u, 0xFF808080u)]   // a fill brighter than its line
    [InlineData(0xFF000000u, 0xFF000000u)]   // both black: an envelope of nothing
    public void SplittingByTheEnvelopePutsBothColoursBack(uint line, uint fill)
    {
        // The quad is drawn through ONE tint, which multiplies every texel. Two colours under
        // one tint only work because a multiplication can be undone: each is baked as its
        // ratio to the channel-wise maximum, and the tint is that maximum. Both must come back
        // to within a rounding, in every channel, whichever of them is the brighter.
        (uint tint, uint bakedLine, uint bakedFill) = TerrainPicture.Split(line, fill);

        Assert.Equal(0xFFu, tint >> 24);            // the tint is opaque; the alphas are baked
        Assert.Equal(line >> 24, bakedLine >> 24);
        Assert.Equal(fill >> 24, bakedFill >> 24);

        for (int shift = 0; shift < 24; shift += 8)
        {
            uint t = (tint >> shift) & 0xFF;
            uint drawnLine = (((bakedLine >> shift) & 0xFF) * t + 127) / 255;
            uint drawnFill = (((bakedFill >> shift) & 0xFF) * t + 127) / 255;
            Assert.Equal((line >> shift) & 0xFF, drawnLine);
            Assert.Equal((fill >> shift) & 0xFF, drawnFill);
        }
    }

    [Fact]
    public void ASwitchedOffFillDoesNotDarkenTheLine()
    {
        // A fill at zero opacity is not drawn, so its colour must not be in the envelope: a
        // bright colour left in a switched-off fill would pull the tint up and the line down.
        (uint tint, uint bakedLine, _) = TerrainPicture.Split(0xFF202020u, 0x00FFFFFFu);

        Assert.Equal(0xFF202020u, tint);
        Assert.Equal(0xFFFFFFFFu, bakedLine);
    }

    [Fact]
    public void TheLineIsOverTheFill_AndTheRimOnlyWhereThereIsNone()
    {
        // Three pixels: the line, floor beside it, and floor at half coverage. The rim reaches
        // the second pixel too, but the fill is the ground there and gets it - the rim exists
        // to separate the line from the WORLD, and it is drawn only where the world shows.
        var mask = new OutlineMask([1, 0, 0], 3, 1, 1);
        byte[] rim = [1, 1, 0];
        byte[] fill = [255, 255, 128];
        uint line = Abgr(255, 0, 0, 255);
        uint fillColour = Abgr(0, 0, 255, 128);

        TerrainPicture picture = TerrainPicture.Paint(mask, rim, fill, line, fillColour, rimAlpha: 127);

        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Texel(picture, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)128), Texel(picture, 1, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)64), Texel(picture, 2, 0));

        // Without a fill the rim is back where it was, and the rest is clear.
        TerrainPicture unfilled = TerrainPicture.Paint(mask, rim, null, line, fillColour, rimAlpha: 127);

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)127), Texel(unfilled, 1, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), Texel(unfilled, 2, 0));
    }

    [Fact]
    public void TheRimIsNeverMoreSolidThanItsLine()
    {
        // A faint line with a firm black edge reads as the edge.
        var mask = new OutlineMask([1, 0], 2, 1, 1);
        TerrainPicture picture = TerrainPicture.Paint(mask, [1, 1], null, Abgr(255, 255, 255, 60), 0, rimAlpha: 127);

        Assert.Equal((byte)60, Texel(picture, 1, 0).A);
    }

    [Fact]
    public void HalvingAveragesWhatIsSeen_NotTheNumbers()
    {
        // The renderer blends straight alpha, so that is what the picture holds - but a
        // straight average weights a clear texel's colour as much as an opaque one's, and a
        // white line next to clear black comes out grey. Weighted by alpha instead, one white
        // texel in four is white at a quarter of the alpha, which is what a quarter of a white
        // line looks like.
        var oneWhite = new TerrainPicture(
        [
            255, 255, 255, 255, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
        ], 2, 2);

        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)64), Texel(oneWhite.Halved(), 0, 0));

        // A white line against its own dark rim: the rim's colour counts for as much as its
        // alpha, and the line, four times as solid, keeps four fifths of the say. A straight
        // average would call this mid-grey.
        var lineAndRim = new TerrainPicture(
        [
            255, 255, 255, 255, 0, 0, 0, 51,
            0, 0, 0, 0, 0, 0, 0, 0,
        ], 2, 2);

        Assert.Equal(((byte)213, (byte)213, (byte)213, (byte)77), Texel(lineAndRim.Halved(), 0, 0));

        // Two solid texels of different colours, equal weight, meet in the middle.
        var whiteAndBlack = new TerrainPicture(
        [
            255, 255, 255, 255, 0, 0, 0, 255,
            0, 0, 0, 0, 0, 0, 0, 0,
        ], 2, 2);

        Assert.Equal(((byte)128, (byte)128, (byte)128, (byte)128), Texel(whiteAndBlack.Halved(), 0, 0));
    }

    [Fact]
    public void HalvingPadsAnOddEdgeWithClear()
    {
        // Three texels become two, and the second holds one real texel and one of padding - so
        // it is half as solid, which is right: half of it is not the picture. The drawing
        // covers the padded width, or the picture would be squeezed toward its far edge.
        var picture = new TerrainPicture(
        [
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        ], 3, 1);

        TerrainPicture halved = picture.Halved();

        Assert.Equal(2, halved.Width);
        Assert.Equal(1, halved.Height);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)128), Texel(halved, 0, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)64), Texel(halved, 1, 0));
    }

    [Theory]
    [InlineData(2.0f, 4, 0)]      // magnified: the finest picture
    [InlineData(1.0f, 4, 0)]
    [InlineData(0.71f, 4, 0)]     // just above the switch
    [InlineData(0.70f, 4, 1)]     // just below it: doubling brings it to 1.4
    [InlineData(0.36f, 4, 1)]
    [InlineData(0.35f, 4, 2)]
    [InlineData(0.05f, 4, 3)]     // further out than the coarsest picture reaches: the coarsest
    [InlineData(0.05f, 1, 0)]     // with one picture there is nothing to choose
    [InlineData(0.0f, 4, 0)]      // an unmeasured map draws what it always drew
    [InlineData(float.NaN, 4, 0)]
    public void TheLevelIsTheOneWhoseTexelIsNearestAPixel(float texelPixels, int levels, int expected)
    {
        // Below one pixel per texel the sampler starts skipping texels, and a line becomes
        // dashes - which is the whole reason the coarser pictures exist. The switch sits at
        // 1/sqrt(2) so that the picture drawn is always within a factor of 1.41 of one texel
        // per pixel, in either direction.
        Assert.Equal(expected, TerrainPicture.LevelFor(texelPixels, levels));
    }
}
