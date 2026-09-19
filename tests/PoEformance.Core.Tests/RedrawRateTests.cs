using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>The model pane's own frame rate, fed a clock.</summary>
public class RedrawRateTests
{
    /// <summary>A steady rate reads as itself once a window has closed, and reads as nothing before.</summary>
    /// <remarks>
    /// EXACT, NOT ROUGHLY: the windows close on frames, so a steady rate's windows each span a
    /// whole number of its frames and the count over the span is the rate to the last digit. The
    /// one and a half a second is the slow case - slower than one frame a window, which is where
    /// a rest measured as one window would have read it as nothing.
    /// </remarks>
    [Theory]
    [InlineData(60.0)]
    [InlineData(30.0)]
    [InlineData(7.0)]
    [InlineData(1.5)]
    public void ASteadyRateReadsAsItself(double perSecond)
    {
        var rate = new RedrawRate();
        double step = 1.0 / perSecond;

        Assert.Equal(0f, rate.At(0.0));
        var now = 0.0;
        for (var frame = 0; now < 3.0; frame++)
        {
            now = frame * step;
            rate.Redrawn(now);
        }

        Assert.InRange(rate.At(now), perSecond - 0.25, perSecond + 0.25);
    }

    /// <summary>The number holds still between two closes rather than flickering with every frame.</summary>
    [Fact]
    public void TheNumberHoldsStillWithinAWindow()
    {
        var rate = new RedrawRate();

        // Two seconds at sixty, to the frame on which the fourth window closes.
        var now = 0.0;
        for (var frame = 0; frame <= 120; frame++)
        {
            now = frame / 60.0;
            rate.Redrawn(now);
        }

        float shown = rate.At(now);
        Assert.Equal(60f, shown);
        for (var later = 0.01; later < RedrawRate.Window - 0.02; later += 0.05)
        {
            rate.Redrawn(now + later);
            Assert.Equal(shown, rate.At(now + later));
        }
    }

    /// <summary>Once nothing is redrawn for a rest's length the rate is zero, not the last number.</summary>
    [Fact]
    public void AtRestTheRateIsZero()
    {
        var rate = new RedrawRate();
        for (var frame = 0; frame < 60; frame++)
        {
            rate.Redrawn(frame / 60.0);
        }

        double stopped = 59 / 60.0;
        Assert.Equal(60f, rate.At(stopped));
        Assert.Equal(60f, rate.At(stopped + (RedrawRate.Rest * 0.9)));
        Assert.Equal(0f, rate.At(stopped + (RedrawRate.Rest * 1.1)));
        Assert.Equal(0f, rate.At(stopped + 30.0));

        // A redraw after the rest starts afresh: neither the stale window nor the stale number.
        rate.Redrawn(stopped + 30.0);
        Assert.Equal(0f, rate.At(stopped + 30.0 + 0.1));
        Assert.Equal(0f, rate.At(stopped + 30.0 + (RedrawRate.Rest * 2)));
    }

    /// <summary>The redraw that opens a window is not one of its frames.</summary>
    /// <remarks>
    /// Seven a second from a standing start: four frames after the opening one close the first
    /// window at four sevenths of a second, which is seven exactly - and eight and three quarters
    /// with the opening frame counted in, which is what the first number of every drag read as.
    /// </remarks>
    [Fact]
    public void TheOpeningRedrawIsNotCounted()
    {
        var rate = new RedrawRate();
        for (var frame = 0; frame <= 4; frame++)
        {
            rate.Redrawn(frame / 7.0);
        }

        Assert.Equal(7f, rate.At(4 / 7.0), 3);
    }
}
