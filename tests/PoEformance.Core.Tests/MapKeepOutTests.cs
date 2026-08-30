using System.Numerics;
using System.Text.Json;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the game's own interface is, and the map overlay keeping off it.
/// </summary>
/// <remarks>
/// THE BUG THIS EXISTS FOR: the game draws the large map across the WHOLE window and paints the
/// orbs, the flask and skill bars, the experience strip and any open panel on top of it. The
/// map element says so too - its rectangle is the window - so everything projected onto that
/// map landed on the interface as well, and a terrain outline over the life orb hides the one
/// number a player is watching.
/// </remarks>
public class MapKeepOutTests
{
    private const float Wide = 2560f;
    private const float Tall = 1440f;

    private static MapView LargeMap()
        => new(
            new Vector2(Wide / 2f, Tall / 2f), 1000f, 0.5f, IsLargeMap: true, Visible: true,
            0f, 0f, Wide, Tall);

    [Fact]
    public void TheDefaultKeepsTheMapOffTheBottomOfTheScreen()
    {
        // The one guessed number in the whole feature, pinned so a change to it is a decision
        // rather than a drift: the band starts four fifths of the way down, which is where the
        // orbs' upper arc begins.
        ScreenRect band = Assert.Single(MapKeepOut.Default.Blocking(Wide, Tall));

        Assert.Equal(new ScreenRect(0f, 1152f, Wide, Tall), band);
    }

    [Fact]
    public void ZonesAreFractions_SoTheySurviveAResolutionChange()
    {
        // Stored proportionally on purpose: the interface is laid out proportionally, so pixels
        // would be a setting that silently stops meaning what it meant the moment somebody
        // plays windowed or moves to another monitor.
        ScreenRect onSmall = Assert.Single(MapKeepOut.Default.Blocking(1920f, 1080f));
        ScreenRect onLarge = Assert.Single(MapKeepOut.Default.Blocking(3440f, 1440f));

        Assert.Equal(new ScreenRect(0f, 864f, 1920f, 1080f), onSmall);
        Assert.Equal(new ScreenRect(0f, 1152f, 3440f, 1440f), onLarge);
    }

    [Fact]
    public void SwitchingTheWholeThingOffGivesTheWindowBack()
    {
        // What the checkbox has to mean, expressed as the absence of zones rather than as a
        // flag anything downstream has to know about.
        Assert.Empty((MapKeepOut.Default with { On = false }).Blocking(Wide, Tall));
    }

    [Fact]
    public void ASwitchedOffZoneIsKeptButNotHonoured()
    {
        // Kept rather than deleted, because switching one off for a moment is the only way to
        // check that a zone is covering what you think it is.
        MapKeepOut off = MapKeepOut.Default.With(
            0, MapKeepOut.Default.ZonesOrDefault[0] with { On = false });

        Assert.Single(off.ZonesOrDefault);
        Assert.Empty(off.Blocking(Wide, Tall));
    }

    [Fact]
    public void AnEmptyListIsADecisionAndAMissingOneIsNot()
    {
        // A file written before this existed has no key at all and gets the default; a file
        // whose owner deleted every zone gets none. Folding the two together would make "I want
        // the whole window" impossible to save.
        Assert.Single(new MapKeepOut().ZonesOrDefault);
        Assert.Empty(new MapKeepOut(On: true, Zones: []).ZonesOrDefault);
    }

    [Fact]
    public void ADraggedZoneComesBackAsTheSameRectangle()
    {
        // The editor's round trip: the box somebody drags IS the region kept out, with nothing
        // scaled or interpreted in between, so a box that looks right cannot be wrong.
        var dragged = new ScreenRect(640f, 360f, 1280f, 1080f);
        MapKeepOutZone moved = MapKeepOut.Default.ZonesOrDefault[0].MovedTo(dragged, Wide, Tall);

        Assert.Equal(dragged, moved.Placed(Wide, Tall));
    }

    [Fact]
    public void AZoneDraggedOffTheEdgeIsClampedToTheWindow()
    {
        // Otherwise it is stored as a fraction outside 0..1, which reads as a corrupt settings
        // file - and describes nothing an edge exactly on the boundary could not.
        MapKeepOutZone moved = MapKeepOut.Default.ZonesOrDefault[0]
            .MovedTo(new ScreenRect(-400f, -200f, Wide + 900f, Tall + 50f), Wide, Tall);

        Assert.Equal(0f, moved.Left);
        Assert.Equal(0f, moved.Top);
        Assert.Equal(1f, moved.Right);
        Assert.Equal(1f, moved.Bottom);
    }

    [Fact]
    public void AddingAndRemovingZonesLeavesTheRestAlone()
    {
        MapKeepOut two = MapKeepOut.Default.Plus();
        Assert.Equal(2, two.ZonesOrDefault.Count);

        MapKeepOut back = two.Less(1);
        Assert.Equal(MapKeepOut.Default.ZonesOrDefault[0], Assert.Single(back.ZonesOrDefault));

        // Out-of-range edits are ignored rather than throwing: the editor removes a zone at the
        // end of a frame in which the list may already have been replaced.
        Assert.Equal(two.ZonesOrDefault.Count, two.Less(7).ZonesOrDefault.Count);
    }

    [Fact]
    public void TheLargeMapNoLongerAcceptsAPointOnTheInterface()
    {
        // The whole feature in one assertion. The map's rectangle IS the window - that is what
        // the game reports and it is correct - so before the region existed both of these
        // points were on the map and both were drawn on.
        MapView map = LargeMap().Within(MapKeepOut.Default.Blocking(Wide, Tall));

        Assert.True(map.Contains(new Vector2(1280f, 700f)));
        Assert.False(map.Contains(new Vector2(1280f, 1300f)));
    }

    [Fact]
    public void AMapWithNoRegionIsUnchanged()
    {
        // Nothing that draws on a map has to know whether one was attached: the map's own
        // rectangle is the answer when it was not, in the same shape.
        MapView map = LargeMap();

        Assert.True(map.Contains(new Vector2(1280f, 1300f)));
        Assert.Equal(new ScreenRect(0f, 0f, Wide, Tall), Assert.Single(map.Uncovered));
    }

    [Fact]
    public void AZoneMissingTheMinimapDoesNotWidenIt()
    {
        // The zones are described against the WINDOW and the minimap is a small frame somewhere
        // in it, so the region has to be intersected with the map rather than replace it -
        // otherwise a keep-out at the bottom of the screen would hand the minimap the whole
        // window to draw on.
        var minimap = new MapView(
            new Vector2(2300f, 200f), 300f, 0.5f, IsLargeMap: false, Visible: true,
            2150f, 50f, 300f, 300f);

        MapView placed = minimap.Within(MapKeepOut.Default.Blocking(Wide, Tall));

        Assert.Equal(new ScreenRect(2150f, 50f, 2450f, 350f), Assert.Single(placed.Uncovered));
        Assert.False(placed.Contains(new Vector2(1280f, 200f)));
    }

    [Fact]
    public void TheZonesSurviveTheSettingsFile()
    {
        // Saved with everything else, because the value of saying where the interface is, is
        // not having to say it again next launch.
        var settings = new OverlaySettings(PoEformance.Game.Components.ItemRarity.Magic)
        {
            MapKeepOut = MapKeepOut.Default.Plus(),
        };

        string json = JsonSerializer.Serialize(settings, OverlayJsonContext.Default.OverlaySettings);
        OverlaySettings back = JsonSerializer.Deserialize(
            json, OverlayJsonContext.Default.OverlaySettings)!;

        Assert.Equal(settings.MapKeepOut.ZonesOrDefault, back.MapKeepOutOrDefault.ZonesOrDefault);
    }

    [Fact]
    public void ASettingsFileFromBeforeThisExistedGetsTheDefault()
    {
        // The upgrade path, and the reason the key is null rather than written eagerly: an
        // untouched file gains no key, and the default stays where a release can correct it.
        OverlaySettings back = JsonSerializer.Deserialize(
            """{"minLootRarity":"Magic"}""", OverlayJsonContext.Default.OverlaySettings)!;

        Assert.Null(back.MapKeepOut);
        Assert.Equal(MapKeepOut.Default.ZonesOrDefault, back.MapKeepOutOrDefault.ZonesOrDefault);
    }
}
