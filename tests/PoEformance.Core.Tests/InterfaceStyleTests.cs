using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The interface's own size and solidity: that its zero is harmless, and that it survives a
/// restart without dragging the rest of the settings back to their defaults.
/// </summary>
public class InterfaceStyleTests
{
    [Fact]
    public void ZeroMeansAsItComes()
    {
        // The value a settings file written before this existed produces, and the one a
        // hand-edited file can hold. It has to mean the defaults rather than no text in an
        // invisible window - there would be nothing left on screen to undo it with.
        var blank = new InterfaceStyle(0, 0f, 0f);

        Assert.Equal(InterfaceStyle.DefaultTextSize, blank.TextSizeOr);
        Assert.Equal(InterfaceStyle.DefaultPanelOpacity, blank.PanelOpacityOr);
        Assert.Equal(InterfaceStyle.DefaultReadoutOpacity, blank.ReadoutOpacityOr);
    }

    [Theory]
    [InlineData(-40)]
    [InlineData(400)]
    [InlineData(1)]
    public void TextSizeStaysWithinWhatCanBeDrawn(int written)
    {
        int size = new InterfaceStyle(written).TextSizeOr;
        Assert.InRange(size, InterfaceStyle.MinTextSize, InterfaceStyle.MaxTextSize);
    }

    [Theory]
    [InlineData(InterfaceStyle.MinTextSize, 15)]
    [InlineData(InterfaceStyle.DefaultTextSize, 23)]
    [InlineData(InterfaceStyle.MaxTextSize, 38)]
    public void AHeadingIsAQuarterLargerThanWhatItHeads(int body, int expected)
        => Assert.Equal(expected, InterfaceStyle.HeadingSizeFor(body));

    [Fact]
    public void AndIsAlwaysAtLeastOnePixelLarger()
    {
        // The whole point of the second size is that the eye can rank two lines. A ratio that
        // rounded back onto the body size at some setting would leave that setting with a
        // heading that is not one - which is a hierarchy that works everywhere except where
        // somebody happened to put the slider.
        for (int body = InterfaceStyle.MinTextSize; body <= InterfaceStyle.MaxTextSize; body++)
        {
            Assert.True(
                InterfaceStyle.HeadingSizeFor(body) > body,
                $"a heading over {body}px body text came out at {InterfaceStyle.HeadingSizeFor(body)}px");
        }
    }

    [Fact]
    public void TheHeadingFollowsWhateverTheTextSizeSettledOn()
    {
        // Including the bounds: a style asking for something absurd draws its body clamped,
        // and its headings have to be a quarter above THAT rather than above the request.
        Assert.Equal(
            InterfaceStyle.HeadingSizeFor(InterfaceStyle.MaxTextSize),
            new InterfaceStyle(400).HeadingSizeOr);

        Assert.Equal(
            InterfaceStyle.HeadingSizeFor(InterfaceStyle.DefaultTextSize),
            new InterfaceStyle(0).HeadingSizeOr);
    }

    [Fact]
    public void ATintedInkStaysNearerTheInkThanTheAccent()
    {
        // WHAT MAKES IT A TINT rather than a second colour, and the failure this exists to
        // catch: somebody deciding the tab bar could stand out a bit more and walking the ratio
        // up until the labels are gold. Over a game that paints in gold that is not a stronger
        // version of the same idea - it is a second accent arguing with the real one.
        var ink = new Vector4(0.94f, 0.93f, 0.89f, 1f);
        var accent = new Vector4(0.85f, 0.68f, 0.34f, 1f);

        Vector4 tinted = InterfaceStyle.Tinted(ink, accent);

        Assert.True(
            (tinted - ink).Length() < (tinted - accent).Length(),
            $"a tint at {InterfaceStyle.AccentTint} came out closer to the accent than to the ink");
    }

    [Fact]
    public void ButIsNotSimplyTheInkAgain()
    {
        // The other end of it. A ratio rounded down to nothing leaves the tab bar written in
        // exactly the body colour, which is the state this was added to get out of - and it
        // would look like the setting had simply not been applied.
        var ink = new Vector4(0.94f, 0.93f, 0.89f, 1f);
        var accent = new Vector4(0.85f, 0.68f, 0.34f, 1f);

        Vector4 tinted = InterfaceStyle.Tinted(ink, accent);

        Assert.NotEqual(ink, tinted);

        // And far enough to actually be seen. A twentieth of the way is arithmetically not the
        // ink and visually indistinguishable from it, which would pass the line above while
        // failing the thing it is there to protect.
        Assert.True(
            (tinted - ink).Length() > 0.05f,
            $"a tint at {InterfaceStyle.AccentTint} is too small a step from the ink to be seen");
    }

    [Fact]
    public void AndLandsBetweenTheTwoOnEveryChannel()
    {
        // A mix, not a recolour: no channel may overshoot either end. This is what lets the
        // palette treat the result as "the ink, warmer" - including the alpha, since a tint
        // that quietly changed how solid the text is would be a different bug wearing this
        // one's clothes.
        var ink = new Vector4(0.94f, 0.93f, 0.89f, 1f);
        var accent = new Vector4(0.85f, 0.68f, 0.34f, 1f);

        Vector4 tinted = InterfaceStyle.Tinted(ink, accent);

        Assert.InRange(tinted.X, accent.X, ink.X);
        Assert.InRange(tinted.Y, accent.Y, ink.Y);
        Assert.InRange(tinted.Z, accent.Z, ink.Z);
        Assert.Equal(ink.W, tinted.W);
    }

    [Fact]
    public void OpacityNeverGoesFaintEnoughToLosePanelIn()
    {
        // A panel at nothing is still there, still takes the mouse and cannot be seen, and
        // the control that undoes it is inside it.
        Assert.Equal(InterfaceStyle.MinOpacity, new InterfaceStyle(PanelOpacity: 0.01f).PanelOpacityOr);
        Assert.Equal(1f, new InterfaceStyle(PanelOpacity: 4f).PanelOpacityOr);
    }

    [Fact]
    public void NormalisedSaysTheSameNumbersItDraws()
    {
        // Otherwise the file keeps saying 400 back to whoever opens the slider while the
        // screen shows 30, which gets reported as the slider being broken.
        InterfaceStyle settled = new InterfaceStyle(400, 0f, 9f).Normalised();

        Assert.Equal(settled.TextSizeOr, settled.TextSize);
        Assert.Equal(settled.PanelOpacityOr, settled.PanelOpacity);
        Assert.Equal(settled.ReadoutOpacityOr, settled.ReadoutOpacity);
    }

    [Fact]
    public void UnsetInAFileMeansUnsetAfterLoading()
    {
        // Null rather than a copy of the defaults, so a default corrected in a release still
        // reaches somebody who never touched the sliders.
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-interface-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(OverlaySettingsStore.Save(new OverlaySettings(ItemRarity.Rare), path));

            OverlaySettings loaded = OverlaySettingsStore.Load(path);
            Assert.Null(loaded.Interface);
            Assert.Equal(InterfaceStyle.DefaultTextSize, loaded.InterfaceOrDefault.TextSizeOr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ChosenSizeSurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-interface-{Guid.NewGuid():N}.json");
        try
        {
            var chosen = new OverlaySettings(ItemRarity.Rare) { Interface = new InterfaceStyle(24, 0.6f, 0.5f) };
            Assert.True(OverlaySettingsStore.Save(chosen, path));

            InterfaceStyle back = OverlaySettingsStore.Load(path).InterfaceOrDefault;
            Assert.Equal(24, back.TextSize);
            Assert.Equal(0.6f, back.PanelOpacity);
            Assert.Equal(0.5f, back.ReadoutOpacity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePageDoesNotResetIt()
    {
        // The configuration window sends the four settings it shows. Deserialising that into
        // a whole record gives everything else its defaults, and saving it then quietly
        // discards whatever was set on the overlay itself - see MergeFromPage.
        var kept = new OverlaySettings(ItemRarity.Magic) { Interface = new InterfaceStyle(26) };

        OverlaySettings merged = kept.MergeFromPage(new OverlaySettings(ItemRarity.Rare));

        Assert.Equal(26, merged.InterfaceOrDefault.TextSize);
        Assert.Equal(ItemRarity.Rare, merged.MinLootRarity);
    }
}
