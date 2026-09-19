using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Lifting a rendered model to the brightness the game's icons are painted at.
/// </summary>
/// <remarks>
/// THE ONE THING WORTH TESTING IS THAT IT LANDS. The exponent is searched rather than
/// calculated precisely because the obvious formula misses - on a real export it produced 42
/// where 55 was asked for - so a test that only checked "it got brighter" would have passed
/// the version that was wrong. Every case here measures the result against what was asked.
/// </remarks>
public class PictureLightTests
{
    /// <summary>A picture whose pixels are spread over a range, which is what a model is.</summary>
    private static byte[] Spread(int from, int to, int count = 4096)
    {
        var rgba = new byte[count * 4];
        for (var pixel = 0; pixel < count; pixel++)
        {
            var level = (byte)(from + ((to - from) * pixel / Math.Max(1, count - 1)));
            rgba[(pixel * 4) + 0] = level;
            rgba[(pixel * 4) + 1] = level;
            rgba[(pixel * 4) + 2] = level;
            rgba[(pixel * 4) + 3] = 255;
        }

        return rgba;
    }

    [Fact]
    public void ItLandsOnTheBrightnessItWasAskedFor()
    {
        // A dark model, about where the first Varloch export measured.
        byte[] dark = Spread(2, 60);
        Assert.InRange(PictureLight.MeanOf(dark), 28f, 34f);

        PictureLight.Apply(dark, PictureLight.GammaFor(dark, PictureLight.Measured));

        // Within a unit of the target, which is what the search buys over the closed form.
        Assert.InRange(PictureLight.MeanOf(dark), PictureLight.Measured - 1f, PictureLight.Measured + 1f);
    }

    [Fact]
    public void ItDimsAsWellAsLifts()
    {
        // Matching runs both ways: a model brighter than the set it joins is made to fit it,
        // which is what makes a set of icons look like one set.
        byte[] bright = Spread(80, 220);
        PictureLight.Apply(bright, PictureLight.GammaFor(bright, PictureLight.Measured));

        Assert.InRange(PictureLight.MeanOf(bright), PictureLight.Measured - 1f, PictureLight.Measured + 1f);
    }

    [Fact]
    public void BothEndsOfTheRangeStayWhereTheyAre()
    {
        byte[] ends = [0, 0, 0, 255, 255, 255, 255, 255, 128, 128, 128, 255];
        PictureLight.Apply(ends, 0.5f);

        // Black stays black and white stays white - that is the whole argument for a gamma
        // over a multiply: a highlight cannot be clipped away.
        Assert.Equal(0, ends[0]);
        Assert.Equal(255, ends[4]);
        Assert.True(ends[8] > 128, "the middle should have moved");
    }

    [Fact]
    public void TheAlphaIsNotTouched()
    {
        byte[] pixels = [40, 40, 40, 7, 40, 40, 40, 200];
        PictureLight.Apply(pixels, 0.5f);

        Assert.Equal(7, pixels[3]);
        Assert.Equal(200, pixels[7]);
    }

    [Fact]
    public void ClearPixelsAreLeftAlone()
    {
        // The renderer clears to zero and draws into it, so most of a model's picture is this.
        var clear = new byte[4 * 4];
        PictureLight.Apply(clear, 0.4f);

        Assert.All(clear, b => Assert.Equal(0, b));
    }

    [Fact]
    public void WhatIsAlreadyRightIsLeftAlone()
    {
        byte[] right = Spread(20, 95);
        float was = PictureLight.MeanOf(right);

        float gamma = PictureLight.GammaFor(right, was);
        Assert.InRange(gamma, 0.98f, 1.02f);

        PictureLight.Apply(right, gamma);
        Assert.InRange(PictureLight.MeanOf(right), was - 1f, was + 1f);
    }

    [Fact]
    public void AnEmptyOrUnmovablePictureAsksForNothing()
    {
        // Nothing solid to measure.
        Assert.Equal(PictureLight.Untouched, PictureLight.GammaFor(new byte[64], PictureLight.Measured));
        Assert.Equal(0f, PictureLight.MeanOf(new byte[64]));

        // Only black and white, which a curve that fixes both ends cannot move - saying so is
        // better than searching to the end of the range for an exponent that does nothing.
        byte[] ends = [0, 0, 0, 255, 255, 255, 255, 255];
        Assert.Equal(PictureLight.Untouched, PictureLight.GammaFor(ends, PictureLight.Measured));

        // And a target that is not a brightness.
        byte[] normal = Spread(10, 200);
        Assert.Equal(PictureLight.Untouched, PictureLight.GammaFor(normal, 0f));
        Assert.Equal(PictureLight.Untouched, PictureLight.GammaFor(normal, 255f));
    }

    [Fact]
    public void OnlyThePictureIsMeasured()
    {
        // Half the pixels are the space around the model. Counting them would report a
        // picture far darker than it is and lift it into a glare.
        var half = new byte[8 * 4];
        for (var pixel = 0; pixel < 4; pixel++)
        {
            half[(pixel * 4) + 0] = 100;
            half[(pixel * 4) + 1] = 100;
            half[(pixel * 4) + 2] = 100;
            half[(pixel * 4) + 3] = 255;
        }

        Assert.InRange(PictureLight.MeanOf(half), 99f, 101f);
    }

    [Fact]
    public void TheTargetIsInsideTheRangeThePaneOffers()
    {
        // The measured number and the slider have to agree, or the default sits off the end.
        Assert.InRange(PictureLight.Measured, PictureLight.Dimmest, PictureLight.Brightest);
    }
}
