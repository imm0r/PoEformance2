using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which screen-filling panels are open and where they are - the first decides whether the
/// world overlay draws at all, the second which of the tool's own windows are in the way.
/// </summary>
/// <remarks>
/// The offsets are UNVERIFIED, ported from GameHelper2, and a fixture built from the schema
/// follows the schema anywhere - so nothing here can vouch for one. What it covers is the
/// part that is a decision rather than an address, and the decision that matters most is
/// which way a WRONG answer falls: a check that misses a panel leaves the overlay drawn over
/// it, which is what happens with no check at all, while one that invents a panel takes the
/// overlay away and gives no reason. So everything unreadable has to read as shut.
/// </remarks>
public class PanelReaderTests
{
    private const ulong UiRoot = 0x10_0000;
    private const ulong Element = 0x20_0000;

    private const uint IsVisible = 0x800;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>An element that is a UiElement, showing or not, with no parent above it.</summary>
    private static void PlaceElement(FakeMemoryReader fake, OffsetSchema schema, ulong address, bool showing)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        fake.Place(address, new byte[0x300]);
        fake.Place(address + (ulong)ui.OffsetOf("Self"), address);
        fake.Place<uint>(address + (ulong)ui.OffsetOf("Flags"), showing ? IsVisible : 0u);
    }

    private static PanelReader ReaderFor(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    /// <summary>Points one of the interface root's panel fields at an element.</summary>
    private static FakeMemoryReader WithPanel(OffsetSchema schema, string field, bool showing)
    {
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        PlaceElement(fake, schema, Element, showing);
        fake.Place(UiRoot + (ulong)schema.Structs["ImportantUiElements"].OffsetOf(field), Element);
        return fake;
    }

    [Fact]
    public void APANELPointingAtNothingIsNotOpen()
    {
        // Which is the ordinary case and the reason these are pointers at all: the game nulls
        // them while no panel is open, so this is the state the overlay spends a map in.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);

        Assert.Equal(GamePanel.None, ReaderFor(fake, schema).Read(UiRoot, null).Panels);
    }

    [Fact]
    public void ASHOWINGLeftPanelIsOpenAndAHIDDENOneIsNot()
    {
        OffsetSchema schema = Schema();

        Assert.Equal(
            GamePanel.Left,
            ReaderFor(WithPanel(schema, "LeftPanelPtr", showing: true), schema).Read(UiRoot, null).Panels);
        Assert.Equal(
            GamePanel.None,
            ReaderFor(WithPanel(schema, "LeftPanelPtr", showing: false), schema).Read(UiRoot, null).Panels);
    }

    [Fact]
    public void ANDTheOthersAreTheirOwnBitsRatherThanOneSharedYes()
    {
        // Named separately so a stuck one can be told from a working one. With a single bool
        // a wrong path is indistinguishable from a player who left a panel open.
        OffsetSchema schema = Schema();

        Assert.Equal(
            GamePanel.Right,
            ReaderFor(WithPanel(schema, "RightPanelPtr", showing: true), schema).Read(UiRoot, null).Panels);
        Assert.Equal(
            GamePanel.WorldMap,
            ReaderFor(WithPanel(schema, "WorldMapPanelPtr", showing: true), schema).Read(UiRoot, null).Panels);
    }

    [Fact]
    public void TWOAtOnceIsBothOfThemRatherThanWhicheverWasCheckedFirst()
    {
        OffsetSchema schema = Schema();
        StructDef root = schema.Structs["ImportantUiElements"];
        const ulong second = 0x30_0000;

        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        PlaceElement(fake, schema, Element, showing: true);
        PlaceElement(fake, schema, second, showing: true);
        fake.Place(UiRoot + (ulong)root.OffsetOf("LeftPanelPtr"), Element);
        fake.Place(UiRoot + (ulong)root.OffsetOf("RightPanelPtr"), second);

        Assert.Equal(GamePanel.Left | GamePanel.Right, ReaderFor(fake, schema).Read(UiRoot, null).Panels);
    }

    [Fact]
    public void ANADDRESSThatIsNotAUiElementReadsAsSHUTRatherThanOPEN()
    {
        // The way a wrong offset fails, and the direction it has to fail in. A pointer into
        // nothing has no self-marker, so the visibility walk refuses it - and the overlay
        // stays drawn rather than disappearing for a reason nobody can see.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        fake.Place(UiRoot + (ulong)schema.Structs["ImportantUiElements"].OffsetOf("LeftPanelPtr"), 0x7FFF_0000UL);

        Assert.Equal(GamePanel.None, ReaderFor(fake, schema).Read(UiRoot, null).Panels);
    }

    [Fact]
    public void ANDNoInterfaceAtAllIsNoPanels()
    {
        // Between areas the root does not resolve. Reporting a panel then would blank the
        // overlay on every loading screen and leave it blank if the read never came back.
        OffsetSchema schema = Schema();
        Assert.Equal(GamePanel.None, ReaderFor(new FakeMemoryReader(), schema).Read(0, null).Panels);
    }

    /// <summary>The window the panels are placed in: base UI size, so nothing is scaled.</summary>
    /// <remarks>
    /// 2560x1600 with no letterbox makes both scale factors exactly 1, so a UI coordinate and
    /// a window pixel are the same number and a test says what it means. The scaling itself is
    /// UiElementReader's and is covered where the map radar exercises it against the game.
    /// </remarks>
    private static UiScale Viewport => new(2560, 1600, 0);

    private const ulong ChildArray = 0x40_0000;
    private const ulong FirstChild = 0x41_0000;
    private const ulong SecondChild = 0x42_0000;

    /// <summary>Hangs a child vector off an element, with each child a showing UiElement.</summary>
    private static void PlaceChildren(
        FakeMemoryReader fake, OffsetSchema schema, ulong parent, ulong array, params ulong[] children)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        fake.Place(parent + (ulong)ui.OffsetOf("ChildrenFirst"), array);
        fake.Place(parent + (ulong)ui.OffsetOf("ChildrenLast"), array + (ulong)(children.Length * 8));

        for (int i = 0; i < children.Length; i++)
        {
            fake.Place(array + (ulong)(i * 8), children[i]);
            PlaceElement(fake, schema, children[i], showing: true);
        }
    }

    /// <summary>Gives an element a position and a size, in the interface's own space.</summary>
    private static void Measure(
        FakeMemoryReader fake, OffsetSchema schema, ulong address, float x, float y, float width, float height)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        int position = ui.OffsetOf("RelativePosition");
        int size = ui.OffsetOf("UnscaledSize");

        fake.Place(address + (ulong)position, x);
        fake.Place(address + (ulong)(position + 4), y);
        fake.Place(address + (ulong)size, width);
        fake.Place(address + (ulong)(size + 4), height);
    }

    [Fact]
    public void ANOPENPanelSaysWHEREItIsAndNotJustTHAT()
    {
        // The half a WINDOW of the tool's own needs. A marker layer is underneath a panel
        // wherever it is, but the readout in the top-left corner is only in the way of a panel
        // that reaches the top-left corner.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 1700f, 100f, 800f, 900f);

        PanelsOnScreen read = ReaderFor(fake, schema).Read(UiRoot, Viewport);

        Assert.Equal(GamePanel.Right, read.Panels);
        PanelArea area = Assert.Single(read.Areas);
        Assert.Equal(GamePanel.Right, area.Panel);
        Assert.Equal((1700f, 100f, 2500f, 1000f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void WITHOUTAViewportThereIsNothingToSayAboutWhere()
    {
        // A diagnostic run with no overlay has no window to place a UI coordinate in. The bits
        // still arrive, because that is what the world overlay asks for; the rectangles do not.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 1700f, 100f, 800f, 900f);

        PanelsOnScreen read = ReaderFor(fake, schema).Read(UiRoot, null);

        Assert.Equal(GamePanel.Right, read.Panels);
        Assert.Empty(read.Areas);
    }

    [Fact]
    public void ACONTAINERWithNoSizeIsMeasuredByWhatItsChildrenCover()
    {
        // A size of zero is a real reading in this game - the large map's is, and so is PoE2's
        // inventory panel - and it means the element lays its children out without claiming any
        // extent itself. What it draws is therefore where its children are, and the union of
        // those is the panel. Left unanswered, an open panel with no rectangle hides nothing,
        // which is the bug this replaced.
        //
        // The panel sits at the origin so the arithmetic is the children's alone: a child's
        // position is relative to its parent, so a panel with an offset would move both.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 0f, 0f, 0f, 0f);
        PlaceChildren(fake, schema, Element, ChildArray, FirstChild, SecondChild);
        Measure(fake, schema, FirstChild, 100f, 200f, 300f, 400f);
        Measure(fake, schema, SecondChild, 1000f, 100f, 200f, 200f);

        PanelsOnScreen read = ReaderFor(fake, schema).Read(UiRoot, Viewport);

        Assert.Equal(GamePanel.Right, read.Panels);
        PanelArea area = Assert.Single(read.Areas);
        Assert.Equal(PanelExtent.Children, area.From);
        Assert.Equal((100f, 100f, 1200f, 600f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void ACHILDTheGameIsNotShowingDoesNotDecideWhereThePanelIs()
    {
        // The other tab of the same panel, sitting behind the open one at the same size. Its own
        // flags stay set while it is hidden, so counting it would make what is on screen and
        // what is merely loaded look alike - and the panel would measure the same either way.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 0f, 0f, 0f, 0f);
        PlaceChildren(fake, schema, Element, ChildArray, FirstChild, SecondChild);
        Measure(fake, schema, FirstChild, 100f, 200f, 300f, 400f);

        // Placed again without the visible bit, and given the run of the screen: counted, it
        // would swallow the sibling that IS showing.
        PlaceElement(fake, schema, SecondChild, showing: false);
        Measure(fake, schema, SecondChild, 0f, 0f, 2560f, 1600f);

        PanelArea area = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, Viewport).Areas);
        Assert.Equal((100f, 200f, 400f, 600f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void ASIDEPanelThatNothingCanMeasureFallsBackToTheWholeScreen()
    {
        // No size of its own and no children with one either. The assumption is the safe
        // direction for a panel the player is looking AT - and the area says it WAS an
        // assumption, so the readout can say so too rather than showing a number nobody measured.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 0f, 0f, 0f, 0f);

        PanelsOnScreen read = ReaderFor(fake, schema).Read(UiRoot, Viewport);

        Assert.Equal(GamePanel.Right, read.Panels);
        PanelArea area = Assert.Single(read.Areas);
        Assert.Equal(PanelExtent.Unmeasured, area.From);
        Assert.Equal((0f, 0f, 2560f, 1600f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void ASCREENFILLINGPanelIsTheScreenEvenWhenItClaimsLess()
    {
        // The atlas's own numbers, and why this kind is not measured at all: it states
        // 3008x1600 at ScaleIndex 2, which is BOTH axes by the height factor - 2707x1440 on a
        // 3440x1440 screen, leaving the right 733 pixels uncovered while the atlas is plainly
        // drawn across them. Believing it left a window sitting on the open atlas. On a 16:9
        // screen the same numbers overflow the width and nothing looks wrong, which is why this
        // test states the ultrawide.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "WorldMapPanelPtr", showing: true);
        Measure(fake, schema, Element, 0f, 0f, 3008f, 1600f);
        fake.Place<byte>(Element + (ulong)schema.Structs["UiElementBase"].OffsetOf("ScaleIndex"), 2);

        var ultrawide = new UiScale(3440, 1440, 0);
        PanelArea area = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, ultrawide).Areas);

        Assert.Equal(PanelExtent.Kind, area.From);
        Assert.Equal((0f, 0f, 3440f, 1440f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void ARECTANGLEOffTheScreenIsNotSomewhereAWindowCanBe()
    {
        // Clipped to the window, so what comes out is always a piece of screen somebody could
        // point at - and a panel resolved somewhere impossible covers nothing rather than
        // covering a region reaching past the edge, which is the same fail-towards-visible
        // rule the visibility check follows.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, 4000f, 100f, 500f, 500f);

        PanelsOnScreen read = ReaderFor(fake, schema).Read(UiRoot, Viewport);

        Assert.Equal(GamePanel.Right, read.Panels);
        Assert.Empty(read.Areas);
    }

    [Fact]
    public void ANDANAbsurdOneIsTheScreenRatherThanTheHorizon()
    {
        // The clip's other job. A wrong offset can read a size in the millions, and a window
        // compared against that is hidden wherever it sits; clipped, the worst it can say is
        // "the whole screen", which for a panel whose visible bit IS set is the honest reading.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = WithPanel(schema, "RightPanelPtr", showing: true);
        Measure(fake, schema, Element, -5000f, -5000f, 40_000f, 40_000f);

        PanelArea area = Assert.Single(ReaderFor(fake, schema).Read(UiRoot, Viewport).Areas);
        Assert.Equal((0f, 0f, 2560f, 1600f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void OVERLAPPINGIsAnyOfItAndTouchingEdgesIsNone()
    {
        // The question a window asks, and it is not "most of me": a corner over an open stash
        // is a corner over an open stash. A window resting exactly against a panel's edge
        // covers none of it, though, or a window parked beside a panel would vanish.
        var area = new PanelArea(GamePanel.Right, 1000f, 500f, 2000f, 1400f, PanelExtent.Element);

        Assert.True(area.Overlaps(1200f, 600f, 1300f, 700f));   // inside
        Assert.True(area.Overlaps(900f, 400f, 1100f, 600f));    // a corner across it
        Assert.True(area.Overlaps(0f, 0f, 2560f, 1600f));       // swallowed by a bigger one
        Assert.False(area.Overlaps(0f, 0f, 1000f, 1600f));      // edge to edge, nothing covered
        Assert.False(area.Overlaps(2000f, 500f, 2400f, 1400f)); // the same on the other side
        Assert.False(area.Overlaps(1000f, 0f, 2000f, 500f));    // above it
    }

    [Fact]
    public void THEAtlasIsAPanelTheToolWorksOnRatherThanHidesFrom()
    {
        // The bug this fixes, and it had an unusually annoying shape: the atlas is reported as
        // screen-filling, screen-filling panels hid every window the tool has, and the tool's
        // ATLAS windows are the ones that were disappearing - the map names, the routes, the
        // ritual plan, each with a tab that went dark exactly when it became useful.
        //
        // It took the interface browser with it too, which is the circle that made this worth
        // fixing: the browser walks the game's UI tree, most of what is worth walking lives
        // inside a panel, and a browser that hides itself whenever a panel opens can never be
        // pointed at one.
        var atlas = new PanelArea(GamePanel.Atlas, 0f, 0f, 2560f, 1600f, PanelExtent.Kind);

        Assert.False(atlas.HidesWindows);
        Assert.True(atlas.Overlaps(20f, 20f, 400f, 300f));
    }

    [Theory]
    [InlineData(GamePanel.Right)]
    [InlineData(GamePanel.Left)]
    [InlineData(GamePanel.SkillTree)]
    [InlineData(GamePanel.WorldMap)]
    [InlineData(GamePanel.AtlasSkills)]
    [InlineData(GamePanel.Temple)]
    [InlineData(GamePanel.Exchange)]
    [InlineData(GamePanel.Trial)]
    public void ANDEveryOtherPanelStillTakesTheWindowsWithIt(GamePanel panel)
    {
        // The exception is ONE panel, listed rather than inferred. A stash or a passive tree is
        // what the player is looking at and a readout across it is right information in the way;
        // nothing about that argument changed. AtlasSkills is in this list on purpose: whether
        // it belongs with the atlas is a real question and not one to settle by guessing.
        var area = new PanelArea(panel, 0f, 0f, 2560f, 1600f, PanelExtent.Kind);

        Assert.True(area.HidesWindows);
    }
}
