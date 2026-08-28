using System.Numerics;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// That the interface's air scales with its text, and that nothing moved at the default size.
/// </summary>
public class InterfaceMetricsTests
{
    [Fact]
    public void AtTheDefaultSizeNothingMoved()
    {
        // THE WHOLE REASON THIS IS SAFE TO LAND. Every ratio was picked so that the size almost
        // everybody is on reproduces the pixel values the theme has had since it was tuned by
        // eye. What changes is what happens when somebody DRAGS the slider, which is the case
        // that was wrong; at 18 pixels the interface is byte-for-byte the one it was.
        InterfaceMetrics room = InterfaceMetrics.For(InterfaceStyle.DefaultTextSize);

        Assert.Equal(new Vector2(10f, 8f), room.WindowPadding);
        Assert.Equal(new Vector2(7f, 4f), room.FramePadding);
        Assert.Equal(new Vector2(8f, 5f), room.ItemSpacing);
        Assert.Equal(new Vector2(6f, 4f), room.ItemInnerSpacing);
        Assert.Equal(new Vector2(6f, 3f), room.CellPadding);
        Assert.Equal(14f, room.ScrollbarSize);
        Assert.Equal(12f, room.GrabMinSize);
    }

    [Fact]
    public void AnIndentIsOneLineOfText()
    {
        // The one ratio here that is a rule rather than a transcription of what was already
        // there. ImGui's own 21 pixels comes from an interface set at 13, and at this tool's
        // sizes it steps a hint in further than the hint is tall.
        for (int body = InterfaceStyle.MinTextSize; body <= InterfaceStyle.MaxTextSize; body++)
        {
            Assert.Equal(body, InterfaceMetrics.For(body).IndentSpacing);
        }
    }

    [Fact]
    public void BiggerTextGetsMoreAirAndSmallerTextGetsLess()
    {
        // The failure this exists to fix, at both ends: thirty-pixel letters inside padding
        // chosen for eighteen have their own borders against their descenders, and twelve-pixel
        // letters in the same padding swim in a window twice as tall as it needs to be.
        InterfaceMetrics small = InterfaceMetrics.For(InterfaceStyle.MinTextSize);
        InterfaceMetrics usual = InterfaceMetrics.For(InterfaceStyle.DefaultTextSize);
        InterfaceMetrics large = InterfaceMetrics.For(InterfaceStyle.MaxTextSize);

        Assert.True(small.WindowPadding.X < usual.WindowPadding.X);
        Assert.True(usual.WindowPadding.X < large.WindowPadding.X);
        Assert.True(small.FramePadding.Y < usual.FramePadding.Y);
        Assert.True(usual.FramePadding.Y < large.FramePadding.Y);
        Assert.True(small.ScrollbarSize < large.ScrollbarSize);
    }

    [Fact]
    public void AndNothingEverRoundsAwayToNothing()
    {
        // At the small end the vertical insets - three pixels of cell padding at eighteen -
        // would otherwise round to zero, and a table whose rows have no padding at all is a
        // table whose rows touch. Checked across the whole range rather than at the floor,
        // because which value is smallest is a fact about the current tuning.
        for (int body = InterfaceStyle.MinTextSize; body <= InterfaceStyle.MaxTextSize; body++)
        {
            foreach ((string name, float value) in Every(InterfaceMetrics.For(body)))
            {
                Assert.True(value >= 1f, $"{name} came out at {value} for {body}px text");
            }
        }
    }

    [Fact]
    public void EveryMeasurementLandsOnAWholePixel()
    {
        // ImGui draws a one-pixel border on every control and a one-pixel rule between table
        // rows. Padding that lands on a half pixel puts those on a half pixel too, where they
        // are rasterised as two dim rows instead of one lit one - the interface looks slightly
        // out of focus and there is nothing on screen to point at.
        for (int body = InterfaceStyle.MinTextSize; body <= InterfaceStyle.MaxTextSize; body++)
        {
            foreach ((string name, float value) in Every(InterfaceMetrics.For(body)))
            {
                Assert.True(value == MathF.Round(value), $"{name} is {value} at {body}px text");
            }
        }
    }

    [Fact]
    public void AStyleIsMeasuredAtTheSizeItActuallyDrawsAt()
    {
        // Including the bounds: a style asking for something absurd draws its body clamped, and
        // its spacing has to follow THAT rather than the request - the same rule the heading
        // size already follows.
        Assert.Equal(
            InterfaceMetrics.For(InterfaceStyle.MaxTextSize),
            InterfaceMetrics.Of(new InterfaceStyle(400)));

        Assert.Equal(
            InterfaceMetrics.For(InterfaceStyle.DefaultTextSize),
            InterfaceMetrics.Of(new InterfaceStyle(0)));
    }

    /// <summary>Every number in the set, named, so a failure says which one.</summary>
    private static IEnumerable<(string Name, float Value)> Every(InterfaceMetrics room)
    {
        yield return ("window padding across", room.WindowPadding.X);
        yield return ("window padding down", room.WindowPadding.Y);
        yield return ("frame padding across", room.FramePadding.X);
        yield return ("frame padding down", room.FramePadding.Y);
        yield return ("item spacing across", room.ItemSpacing.X);
        yield return ("item spacing down", room.ItemSpacing.Y);
        yield return ("inner spacing across", room.ItemInnerSpacing.X);
        yield return ("inner spacing down", room.ItemInnerSpacing.Y);
        yield return ("cell padding across", room.CellPadding.X);
        yield return ("cell padding down", room.CellPadding.Y);
        yield return ("indent", room.IndentSpacing);
        yield return ("scrollbar", room.ScrollbarSize);
        yield return ("grab", room.GrabMinSize);
    }
}
