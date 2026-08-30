using System.Numerics;
using System.Text.Json;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the map overlay stays off, and the settings that adjust it.
/// </summary>
/// <remarks>
/// THE BUG THIS EXISTS FOR: the game draws the large map across the WHOLE window and paints the
/// orbs, the flask and skill bars, the experience strip and any open panel on top of it. The
/// map element says so too - its rectangle is the window - so everything projected onto that
/// map landed on the interface as well, and a terrain outline over the life orb hides the one
/// number a player is watching.
///
/// The interface itself is MEASURED - see <see cref="InterfaceReaderTests"/>. What is tested here is
/// the part that is still a setting: which measured parts to honour, and the extra boxes for
/// whatever measurement cannot reach.
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
    public void OutOfTheBoxNothingIsDescribedByHand()
    {
        // The correction this type carries: it first shipped with a guessed band across the
        // bottom of the screen, on the belief that the HUD could not be measured. It can, so
        // the default describes nothing at all - every rectangle the map keeps off is read from
        // the game.
        Assert.Empty(MapKeepOut.Default.ZonesOrEmpty);
        Assert.Empty(MapKeepOut.Default.Blocking(Wide, Tall));
        Assert.True(MapKeepOut.Default.Hud);
        Assert.True(MapKeepOut.Default.On);
    }

    [Fact]
    public void EveryMeasuredPartIsHonouredUntilOneIsSwitchedOff()
    {
        // The one thing measurement needs from a setting. Some interface parts are containers,
        // and one reporting a rectangle far larger than it draws would quietly eat the map -
        // the atlas panel has form here. Switching that part off is the whole remedy.
        MapKeepOut settings = MapKeepOut.Default;
        Assert.True(settings.Honours("life_orb"));

        settings = settings.Honouring("life_orb", on: false);
        Assert.False(settings.Honours("life_orb"));
        Assert.True(settings.Honours("mana_orb"));

        settings = settings.Honouring("life_orb", on: true);
        Assert.True(settings.Honours("life_orb"));
        Assert.Empty(settings.HudOffOrEmpty);
    }

    [Fact]
    public void SwitchingOffTheHudLeavesTheBoxesAlone()
    {
        // Two decisions, two switches: somebody who does not trust the measurement may still
        // want the boxes they drew, and the reverse.
        MapKeepOut settings = MapKeepOut.Default.Plus() with { Hud = false };

        Assert.False(settings.Honours("life_orb"));
        Assert.Single(settings.Blocking(Wide, Tall));
    }

    [Fact]
    public void SwitchingTheWholeThingOffGivesTheWindowBack()
    {
        // What the master checkbox has to mean, expressed as the absence of rectangles rather
        // than as a flag anything downstream has to know about.
        Assert.Empty((MapKeepOut.Default.Plus() with { On = false }).Blocking(Wide, Tall));
    }

    [Fact]
    public void BoxesAreFractions_SoTheySurviveAResolutionChange()
    {
        // Stored proportionally on purpose: pixels would be a setting that silently stops
        // meaning what it meant the moment somebody plays windowed or moves to another monitor.
        var half = new MapKeepOutZone("half", 0.5f, 0f, 1f, 0.5f);
        MapKeepOut settings = MapKeepOut.Default with { Zones = [half] };

        Assert.Equal(new ScreenRect(960f, 0f, 1920f, 540f), Assert.Single(settings.Blocking(1920f, 1080f)));
        Assert.Equal(new ScreenRect(1720f, 0f, 3440f, 720f), Assert.Single(settings.Blocking(3440f, 1440f)));
    }

    [Fact]
    public void ASwitchedOffBoxIsKeptButNotHonoured()
    {
        // Kept rather than deleted, because switching one off for a moment is the only way to
        // check that a box is covering what you think it is.
        MapKeepOut added = MapKeepOut.Default.Plus();
        MapKeepOut off = added.With(0, added.ZonesOrEmpty[0] with { On = false });

        Assert.Single(off.ZonesOrEmpty);
        Assert.Empty(off.Blocking(Wide, Tall));
    }

    [Fact]
    public void ADraggedBoxComesBackAsTheSameRectangle()
    {
        // The editor's round trip: the box somebody drags IS the region kept out, with nothing
        // scaled or interpreted in between, so a box that looks right cannot be wrong.
        var dragged = new ScreenRect(640f, 360f, 1280f, 1080f);
        MapKeepOutZone moved = MapKeepOut.Default.Plus().ZonesOrEmpty[0].MovedTo(dragged, Wide, Tall);

        Assert.Equal(dragged, moved.Placed(Wide, Tall));
    }

    [Fact]
    public void ABoxDraggedOffTheEdgeIsClampedToTheWindow()
    {
        // Otherwise it is stored as a fraction outside 0..1, which reads as a corrupt settings
        // file - and describes nothing an edge exactly on the boundary could not.
        MapKeepOutZone moved = MapKeepOut.Default.Plus().ZonesOrEmpty[0]
            .MovedTo(new ScreenRect(-400f, -200f, Wide + 900f, Tall + 50f), Wide, Tall);

        Assert.Equal(0f, moved.Left);
        Assert.Equal(0f, moved.Top);
        Assert.Equal(1f, moved.Right);
        Assert.Equal(1f, moved.Bottom);
    }

    [Fact]
    public void AddingAndRemovingBoxesLeavesTheRestAlone()
    {
        MapKeepOut two = MapKeepOut.Default.Plus().Plus();
        Assert.Equal(2, two.ZonesOrEmpty.Count);

        Assert.Single(two.Less(1).ZonesOrEmpty);

        // Out-of-range edits are ignored rather than throwing: the editor removes a box at the
        // end of a frame in which the list may already have been replaced.
        Assert.Equal(2, two.Less(7).ZonesOrEmpty.Count);
        Assert.Equal(two, two.With(7, new MapKeepOutZone("nowhere", 0f, 0f, 1f, 1f)));
    }

    [Fact]
    public void TheLargeMapNoLongerAcceptsAPointOnTheInterface()
    {
        // The whole feature in one assertion, with the interface standing in as a measured
        // rectangle. The map's own rectangle IS the window - that is what the game reports and
        // it is correct - so before the region existed both of these points were drawn on.
        MapView map = LargeMap().Within([new ScreenRect(0f, 1150f, 300f, Tall)]);

        Assert.True(map.Contains(new Vector2(1280f, 700f)));
        Assert.False(map.Contains(new Vector2(100f, 1300f)));
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
    public void AKeepOutMissingTheMinimapDoesNotWidenIt()
    {
        // The interface is measured against the WINDOW and the minimap is a small frame
        // somewhere in it, so the region has to be intersected with the map rather than replace
        // it - otherwise an orb at the bottom of the screen would hand the minimap the whole
        // window to draw on.
        var minimap = new MapView(
            new Vector2(2300f, 200f), 300f, 0.5f, IsLargeMap: false, Visible: true,
            2150f, 50f, 300f, 300f);

        MapView placed = minimap.Within([new ScreenRect(0f, 1150f, 300f, Tall)]);

        Assert.Equal(new ScreenRect(2150f, 50f, 2450f, 350f), Assert.Single(placed.Uncovered));
        Assert.False(placed.Contains(new Vector2(1280f, 200f)));
    }

    [Fact]
    public void TheSettingsSurviveTheFile()
    {
        // Saved with everything else, because the value of switching a part off is not having
        // to switch it off again next launch.
        var settings = new OverlaySettings(PoEformance.Game.Components.ItemRarity.Magic)
        {
            MapKeepOut = MapKeepOut.Default.Plus().Honouring("HUDRight", on: false),
        };

        string json = JsonSerializer.Serialize(settings, OverlayJsonContext.Default.OverlaySettings);
        OverlaySettings back = JsonSerializer.Deserialize(
            json, OverlayJsonContext.Default.OverlaySettings)!;

        Assert.Equal(settings.MapKeepOut.ZonesOrEmpty, back.MapKeepOutOrDefault.ZonesOrEmpty);
        Assert.False(back.MapKeepOutOrDefault.Honours("HUDRight"));
        Assert.True(back.MapKeepOutOrDefault.Honours("life_orb"));
    }

    [Fact]
    public void ASettingsFileFromBeforeThisExistedGetsTheDefault()
    {
        // The upgrade path, and the reason the key is null rather than written eagerly: an
        // untouched file gains no key, and the default stays where a release can correct it.
        OverlaySettings back = JsonSerializer.Deserialize(
            """{"minLootRarity":"Magic"}""", OverlayJsonContext.Default.OverlaySettings)!;

        Assert.Null(back.MapKeepOut);
        Assert.True(back.MapKeepOutOrDefault.Hud);
        Assert.Empty(back.MapKeepOutOrDefault.ZonesOrEmpty);
    }
}
