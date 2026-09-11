using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The in-game map's own projection - the second of the game's two screen-space systems,
/// and the one the camera matrix cannot substitute for.
/// </summary>
public class MapRadarTests
{
    private const ulong UiRoot = 0x60_0000;
    private const ulong MapParent = 0x61_0000;
    private const ulong LargeMap = 0x62_0000;
    private const ulong MiniMap = 0x63_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    private static MapView View(float zoom = 0.5f, float diagonal = 1000f)
        => new(new Vector2(500, 400), diagonal, zoom, IsLargeMap: true);

    /// <summary>Places a map UI element with its zoom and shifts.</summary>
    private static void PlaceMap(
        FakeMemoryReader fake, OffsetSchema schema, ulong address,
        float relX, float relY, float sizeX, float sizeY,
        float zoom, float shiftX = 0, float shiftY = 0, float defShiftX = 0, float defShiftY = 0)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        StructDef map = schema.Structs["MapUiElement"];

        fake.Place(address, new byte[0x500]);
        fake.Place(address + (ulong)ui.OffsetOf("Self"), address);
        fake.Place(address + (ulong)ui.OffsetOf("RelativePosition"), relX);
        fake.Place(address + (ulong)ui.OffsetOf("RelativePosition") + 4, relY);
        fake.Place(address + (ulong)ui.OffsetOf("LocalScaleMultiplier"), 1f);
        fake.Place<uint>(address + (ulong)ui.OffsetOf("Flags"), 0x800);
        fake.Place(address + (ulong)ui.OffsetOf("ScaleIndex"), (byte)9); // unscaled: UI units are pixels
        fake.Place(address + (ulong)ui.OffsetOf("UnscaledSize"), sizeX);
        fake.Place(address + (ulong)ui.OffsetOf("UnscaledSize") + 4, sizeY);

        fake.Place(address + (ulong)map.OffsetOf("Shift"), shiftX);
        fake.Place(address + (ulong)map.OffsetOf("Shift") + 4, shiftY);
        fake.Place(address + (ulong)map.OffsetOf("DefaultShift"), defShiftX);
        fake.Place(address + (ulong)map.OffsetOf("DefaultShift") + 4, defShiftY);
        fake.Place(address + (ulong)map.OffsetOf("Zoom"), zoom);
    }

    private static FakeMemoryReader BuildUi(OffsetSchema schema, out FakeMemoryReader fake)
    {
        fake = new FakeMemoryReader();
        fake.Place(UiRoot, new byte[0x900]);
        fake.Place(UiRoot + (ulong)schema.Structs["ImportantUiElements"].OffsetOf("MapParentPtr"), MapParent);

        StructDef parent = schema.Structs["MapParentStruct"];
        fake.Place(MapParent, new byte[0x40]);
        fake.Place(MapParent + (ulong)parent.OffsetOf("LargeMapPtr"), LargeMap);
        fake.Place(MapParent + (ulong)parent.OffsetOf("MiniMapPtr"), MiniMap);
        return fake;
    }

    [Fact]
    public void Project_PutsThePlayerOnTheMapCentre()
    {
        // Everything on this map is a delta FROM the player, so the player is the origin by
        // construction - no camera, no matrix.
        MapView map = View();
        Assert.Equal(map.Centre, map.Project(1000, 2000, 50, 1000, 2000, 50));
    }

    [Fact]
    public void Project_MirrorsOppositeDirectionsAboutTheCentre()
    {
        MapView map = View();
        Vector2 east = map.Project(1500, 2000, 0, 1000, 2000, 0);
        Vector2 west = map.Project(500, 2000, 0, 1000, 2000, 0);

        Assert.Equal(map.Centre.X - (west.X - map.Centre.X), east.X, 3);
        Assert.Equal(map.Centre.Y - (west.Y - map.Centre.Y), east.Y, 3);
    }

    [Fact]
    public void Project_SpreadsFurtherAsTheMapIsZoomedIn()
    {
        // THE point of this whole transform: the map's zoom changes the scale, and nothing in
        // the world-to-screen matrix knows about it. That is why markers projected with the
        // matrix drift away from the game's map markers the moment the map is zoomed.
        Vector2 far = View(zoom: 1.0f).Project(2000, 2000, 0, 1000, 2000, 0);
        Vector2 near = View(zoom: 0.5f).Project(2000, 2000, 0, 1000, 2000, 0);

        float farDistance = Vector2.Distance(far, View().Centre);
        float nearDistance = Vector2.Distance(near, View().Centre);
        Assert.True(farDistance > nearDistance * 1.9f, $"{farDistance} vs {nearDistance}");
    }

    [Fact]
    public void Project_MovesWithTerrainHeight()
    {
        // On the map an entity is placed by the GROUND it stands on, so a ledge shifts it.
        MapView map = View();
        Vector2 low = map.Project(1200, 2000, 0, 1000, 2000, 0);
        Vector2 high = map.Project(1200, 2000, 500, 1000, 2000, 0);

        Assert.Equal(low.X, high.X, 3);            // height is a vertical-only effect
        Assert.NotEqual(low.Y, high.Y, 3);
    }

    [Fact]
    public void MiniMap_CentreIsItsMiddle()
    {
        OffsetSchema schema = Schema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 100, relY: 50, sizeX: 400, sizeY: 300,
            zoom: 0.6f, shiftX: 7, shiftY: 3, defShiftX: 0, defShiftY: -20);

        var scale = new UiScale(2560, 1600, 0);
        MapView view = new MapRadarReader(fake, schema).Read(UiRoot, scale, largeMap: false)!.Value;

        // Top-left plus half the size, then the shifts.
        Assert.Equal(new Vector2(100 + 200 + 7, 50 + 150 + 3 - 20), view.Centre);
        Assert.Equal(500f, view.Diagonal, 3);   // 3-4-5 triangle
        Assert.Equal(0.6f, view.Zoom, 3);
        Assert.False(view.IsLargeMap);
    }

    [Fact]
    public void LargeMap_CentreIsItsPosition_AndItBorrowsTheMinimapDiagonal()
    {
        // Both PoE2 deviations at once: the large map's accumulated position is ALREADY its
        // centre (adding half a size would push it off by that much), and its own size reads
        // zero so the diagonal has to come from the minimap.
        OffsetSchema schema = Schema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 100, relY: 50, sizeX: 400, sizeY: 300, zoom: 0.6f);
        PlaceMap(fake, schema, LargeMap, relX: 1280, relY: 800, sizeX: 0, sizeY: 0,
            zoom: 0.9f, shiftX: 12, shiftY: -8, defShiftX: 0, defShiftY: -20);

        var scale = new UiScale(2560, 1600, 0);
        MapView view = new MapRadarReader(fake, schema).Read(UiRoot, scale, largeMap: true)!.Value;

        Assert.Equal(new Vector2(1280 + 12, 800 - 8 - 20), view.Centre);
        Assert.Equal(500f, view.Diagonal, 3);   // the minimap's, not the window's
        Assert.Equal(0.9f, view.Zoom, 3);
        Assert.True(view.IsLargeMap);
    }

    [Fact]
    public void LargeMap_FallsBackToTheWindowDiagonal_WhenNoMinimapWasSeen()
    {
        OffsetSchema schema = Schema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 0, relY: 0, sizeX: 0, sizeY: 0, zoom: 0.5f); // unusable
        PlaceMap(fake, schema, LargeMap, relX: 1280, relY: 800, sizeX: 0, sizeY: 0, zoom: 0.5f);

        var scale = new UiScale(1600, 1200, 0);
        MapView view = new MapRadarReader(fake, schema).Read(UiRoot, scale, largeMap: true)!.Value;

        Assert.Equal(2000f, view.Diagonal, 3);   // sqrt(1600^2 + 1200^2)
    }

    [Fact]
    public void Zoom_FallsBackWhenItReadsImplausibly()
    {
        // The zoom offset has drifted before. A zero would divide the projection into
        // nonsense, so an out-of-range read degrades to the resting value instead of
        // scattering every marker.
        OffsetSchema schema = Schema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 0, relY: 0, sizeX: 400, sizeY: 300, zoom: 0f);

        var scale = new UiScale(2560, 1600, 0);
        Assert.Equal(0.5f, new MapRadarReader(fake, schema).Read(UiRoot, scale, false)!.Value.Zoom, 3);

        var wild = new FakeMemoryReader();
        BuildUi(schema, out wild);
        PlaceMap(wild, schema, MiniMap, relX: 0, relY: 0, sizeX: 400, sizeY: 300, zoom: 5000f);
        Assert.Equal(0.5f, new MapRadarReader(wild, schema).Read(UiRoot, scale, false)!.Value.Zoom, 3);
    }

    [Fact]
    public void Resolve_YieldsNothingWithoutTheChain()
    {
        OffsetSchema schema = Schema();
        var empty = new FakeMemoryReader();
        var reader = new MapRadarReader(empty, schema);

        Assert.Equal((0UL, 0UL), reader.Resolve(0));
        Assert.Equal((0UL, 0UL), reader.Resolve(UiRoot));
        Assert.Null(reader.Read(UiRoot, new UiScale(2560, 1600, 0), largeMap: true));
    }

    // ── The second route: the reference's 0.5.5 walk from the UI manager ──────────────

    private const ulong Viewports = 0x64_0000;
    private const ulong LargeViewport = 0x65_0000;
    private const ulong MiniViewport = 0x66_0000;
    private const ulong ManagerChildren = 0x67_0000;
    private const ulong ViewportChildren = 0x68_0000;

    /// <summary>Makes the UI manager an element whose child 6 holds the two viewports.</summary>
    private static void PlaceViewportRoute(FakeMemoryReader fake, OffsetSchema schema)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        StructDef route = schema.Structs["MapViewports"];
        int slot = (int)route.Constants["ChildOfUiManager"];

        fake.Place(UiRoot + (ulong)ui.OffsetOf("Self"), UiRoot);
        fake.Place(ManagerChildren, new byte[8 * (slot + 1)]);
        fake.Place(ManagerChildren + (ulong)(8 * slot), Viewports);
        fake.Place(UiRoot + (ulong)ui.OffsetOf("ChildrenFirst"), ManagerChildren);
        fake.Place(UiRoot + (ulong)ui.OffsetOf("ChildrenLast"), ManagerChildren + (ulong)(8 * (slot + 1)));

        fake.Place(Viewports, new byte[0x300]);
        fake.Place(Viewports + (ulong)ui.OffsetOf("Self"), Viewports);
        fake.Place(ViewportChildren, new byte[16]);
        fake.Place(ViewportChildren + (ulong)(8 * route.Constants["LargeMapChild"]), LargeViewport);
        fake.Place(ViewportChildren + (ulong)(8 * route.Constants["MiniMapChild"]), MiniViewport);
        fake.Place(Viewports + (ulong)ui.OffsetOf("ChildrenFirst"), ViewportChildren);
        fake.Place(Viewports + (ulong)ui.OffsetOf("ChildrenLast"), ViewportChildren + 16);
    }

    [Fact]
    public void Resolve_TakesTheReferenceRoute_WhenTheMapParentChainHandsBackNoZoom()
    {
        // The 0.5.5 shape: MapParentPtr still leads to two elements, but they read a zoom of
        // zero - so they are not believed, and the manager's child 6 is asked instead.
        OffsetSchema schema = RealSessionTests.LiveSchema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, LargeMap, relX: 0, relY: 0, sizeX: 3822, sizeY: 1600, zoom: 0f);
        PlaceMap(fake, schema, MiniMap, relX: 0, relY: 0, sizeX: 3822, sizeY: 1600, zoom: 0f);
        PlaceViewportRoute(fake, schema);
        PlaceMap(fake, schema, LargeViewport, relX: 1280, relY: 800, sizeX: 0, sizeY: 0, zoom: 0.9f);
        PlaceMap(fake, schema, MiniViewport, relX: 100, relY: 50, sizeX: 400, sizeY: 300, zoom: 0.6f);

        var reader = new MapRadarReader(fake, schema);
        Assert.Equal((LargeViewport, MiniViewport), reader.Resolve(UiRoot));
        Assert.Equal(0.6f, reader.Read(UiRoot, new UiScale(2560, 1600, 0), largeMap: false)!.Value.Zoom, 3);
    }

    [Fact]
    public void Resolve_PrefersTheReferenceRoute_WhenBothCarryAZoom()
    {
        // The route confirmed on the current client goes first; the map-parent chain is
        // only asked when the viewports do not answer.
        OffsetSchema schema = RealSessionTests.LiveSchema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 100, relY: 50, sizeX: 400, sizeY: 300, zoom: 0.6f);
        PlaceViewportRoute(fake, schema);
        PlaceMap(fake, schema, MiniViewport, relX: 0, relY: 0, sizeX: 400, sizeY: 300, zoom: 1.2f);

        Assert.Equal((LargeViewport, MiniViewport), new MapRadarReader(fake, schema).Resolve(UiRoot));
    }

    [Fact]
    public void Resolve_KeepsTheMapParentChain_WhenTheManagerHasNoViewports()
    {
        // The 0.5.4 shape, and the one every committed recording holds: the manager is not
        // walked as an element there, so the map-parent chain answers on its own evidence.
        OffsetSchema schema = RealSessionTests.LiveSchema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 100, relY: 50, sizeX: 400, sizeY: 300, zoom: 0.6f);

        Assert.Equal((LargeMap, MiniMap), new MapRadarReader(fake, schema).Resolve(UiRoot));
    }

    [Fact]
    public void Resolve_FallsBackToTheMapParentChain_WhenNeitherRouteCarriesAZoom()
    {
        // Nothing to believe: the answer is the old one, and the zoom's own fallback keeps the
        // projection finite rather than putting every marker on one pixel.
        OffsetSchema schema = RealSessionTests.LiveSchema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 0, relY: 0, sizeX: 400, sizeY: 300, zoom: 0f);

        var reader = new MapRadarReader(fake, schema);
        Assert.Equal((LargeMap, MiniMap), reader.Resolve(UiRoot));
        Assert.Equal(0.5f, reader.Read(UiRoot, new UiScale(2560, 1600, 0), largeMap: false)!.Value.Zoom, 3);
    }

    [Fact]
    public void Zoom_ADenormalIsNotAZoom()
    {
        // 1E-44 is what the 0.5.5 client reads at the old offset: greater than zero, so the old
        // test let it through, and every marker landed on the map centre.
        OffsetSchema schema = RealSessionTests.LiveSchema();
        BuildUi(schema, out FakeMemoryReader fake);
        PlaceMap(fake, schema, MiniMap, relX: 0, relY: 0, sizeX: 400, sizeY: 300, zoom: 1E-44f);

        Assert.Equal(0.5f, new MapRadarReader(fake, schema).Read(UiRoot, new UiScale(2560, 1600, 0), false)!.Value.Zoom, 3);
    }
}
