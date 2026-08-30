using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding the panel the game puts over a hovered map, so the atlas overlay keeps off that
/// rectangle instead of switching itself off.
/// </summary>
/// <remarks>
/// The whole point of this class is a DECISION taken from two lists, so it is tested by handing
/// it lists. Nothing here needs the game: what a part is called and where it sits are measured
/// elsewhere, and what is checked here is only whether "this part came up with the hover" is
/// worked out from a baseline that means something.
/// </remarks>
public sealed class AtlasHoverPanelTests
{
    private static InterfacePart Part(ulong address, string name)
        => new(address, name, new ScreenRect(10, 10, 210, 110), PanelExtent.Element);

    private static readonly InterfacePart Header = Part(0x1000, "header");
    private static readonly InterfacePart Search = Part(0x2000, "search_bar_frame");
    private static readonly InterfacePart Popup = Part(0x3000, "map_content_list");

    [Fact]
    public void APARTThatOnlyAppearsWhileAMapIsHoveredIsTheGamesPanel()
    {
        var hover = new AtlasHoverPanel();

        // A tick with nothing hovered is the baseline: this is what the atlas screen looks like
        // by itself.
        hover.Look(open: true, hovering: false, [Header, Search]);
        Assert.False(hover.Found);
        Assert.True(hover.Settled);

        // And then the cursor goes onto a map and the game puts something up.
        hover.Look(open: true, hovering: true, [Header, Search, Popup]);

        Assert.True(hover.Found);
        Assert.Equal("map_content_list", Assert.Single(hover.Shown).Name);
    }

    [Fact]
    public void ANDNothingNewMEANSTheOverlayHasToHideAsItUsedTo()
    {
        // The failure this has to fail towards. If the game's panel is not among the parts being
        // kept off - it lives somewhere this does not measure, or it is not an element at all -
        // then drawing across it is exactly the thing being fixed, so the old blanking stands.
        var hover = new AtlasHoverPanel();

        hover.Look(open: true, hovering: false, [Header, Search]);
        hover.Look(open: true, hovering: true, [Header, Search]);

        Assert.False(hover.Found);
        Assert.True(hover.Settled);
        Assert.Contains("hides", hover.Describe(new ScreenRect(0, 0, 40, 20)), StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAHoverBeforeAnythingHasBeenSeenIsNotAFinding()
    {
        // Pressing the atlas key without moving the mouse opens it with the cursor already on a
        // map, and there is then no baseline to compare against. Taking the whole screen's
        // furniture for the hover panel would have the overlay draw straight across it, so this
        // hides until the cursor leaves a node once - a frame of the old behaviour, not a fault.
        var hover = new AtlasHoverPanel();

        hover.Look(open: true, hovering: true, [Header, Search, Popup]);

        Assert.False(hover.Found);
        Assert.False(hover.Settled);
    }

    [Fact]
    public void ANDOpeningTheAtlasThrowsTheBaselineAway()
    {
        // The baseline is per SCREEN. The HUD's parts - the orbs, the flask bar - are nothing
        // like the atlas screen's, so a baseline taken in the world and compared against on the
        // atlas would report every piece of the atlas as the panel the hover brought up.
        var hover = new AtlasHoverPanel();

        hover.Look(open: false, hovering: false, [Part(0x9000, "HUD")]);
        Assert.False(hover.Settled);   // a baseline is only worth keeping while the atlas is open

        hover.Look(open: true, hovering: true, [Header, Search, Popup]);
        Assert.False(hover.Found);
    }

    [Fact]
    public void ANDTHEBaselineIsTakenAfreshRatherThanOnce()
    {
        // The atlas screen's own furniture comes and goes with what the player is doing: the pin
        // editor, the search box, a region's buttons. Held from the first tick, every one of
        // those would read as the panel the hover put up.
        var hover = new AtlasHoverPanel();

        hover.Look(open: true, hovering: false, [Header]);
        hover.Look(open: true, hovering: false, [Header, Search]);
        hover.Look(open: true, hovering: true, [Header, Search]);

        Assert.False(hover.Found);
    }

    [Fact]
    public void ANDAPartSomebodySwitchedOffIsNotCoveringAnything()
    {
        // The caller hands over what is ACTUALLY being kept off, not what was measured, and this
        // is why that distinction matters: a part switched off in the keep-out editor is drawn
        // over like any other pixel. Finding the panel there and going on drawing would put the
        // labels straight across it - which is the state this whole thing exists to prevent.
        var hover = new AtlasHoverPanel();

        hover.Look(open: true, hovering: false, [Header, Search]);
        hover.Look(open: true, hovering: true, [Header, Search]);   // the popup is off, so absent

        Assert.False(hover.Found);
    }

    [Fact]
    public void ANDTheReadoutNamesWhatItFound()
    {
        // The one line that says what the game calls its hover panel. Nobody here has read that
        // StringId - it is found by appearing - so this readout is how it gets read, and it is
        // also how "hidden because a map is hovered" is told from "hidden because something
        // broke", which look identical on a screenshot.
        var hover = new AtlasHoverPanel();

        hover.Look(open: true, hovering: false, [Header]);
        hover.Look(open: true, hovering: true, [Header, Popup]);

        string said = hover.Describe(new ScreenRect(100, 100, 140, 120));

        Assert.Contains("map_content_list", said, StringComparison.Ordinal);
        Assert.Contains("100,100", said, StringComparison.Ordinal);
        Assert.Equal("no map under the cursor", new AtlasHoverPanel().Describe(null));
    }
}
