using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The panel offsets against a REAL session with a panel open, which is the only thing that can
/// vouch for them.
/// </summary>
/// <remarks>
/// WHAT THIS FIXTURE SETTLES, and it is the half that was previously unverifiable: the schema
/// calls <c>RightPanelPtr</c> unverified, ported from GameHelper2, and every other test builds
/// the interface from that same schema - so they can only ever show the code agreeing with
/// itself. Here the game had its inventory open, and the pointer resolves to a UiElement whose
/// visibility walk says showing while the left panel is null and the skill tree and world map
/// are present but hidden. That is a coherent picture of one panel being open, from real memory.
///
/// WHAT IT CANNOT SETTLE, because a recording holds only the reads the recording build made.
/// This one was captured before the panel's EXTENT was ever asked for, so it carries three spans
/// of that element and no more: <c>Self</c> at +0x008, <c>ParentPtr</c> at +0x0B8 and four bytes
/// of <c>Flags</c> at +0x180 - exactly what <see cref="UiElementReader.IsUiElement"/> and
/// <see cref="UiElementReader.IsVisible"/> read. Position, size and the child vector are absent,
/// so the reader falls all the way through to assuming the whole window, and this test pins that
/// fallback rather than a measurement. Whether PoE2's inventory panel carries its own size, or
/// has to be measured from its children, needs a fresh recording from a build that asks.
/// </remarks>
public class PanelSessionTests
{
    /// <summary>A session with the inventory panel open, 553 frames.</summary>
    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-panel.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    /// <summary>The window the session was played in, near enough: only the scaling depends on it.</summary>
    private static UiScale Viewport => new(2560, 1440, 0);

    private static (PanelReader Panels, UiElementReader Elements, ulong UiRoot) Attach(
        ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        Assert.Equal(GameStateKind.InGame, chain.State);
        Assert.NotEqual(0UL, chain.UiRoot);

        var elements = new UiElementReader(replay, schema);
        return (new PanelReader(replay, schema, elements), elements, chain.UiRoot);
    }

    [Fact]
    public void THEInventoryBeingOpenIsWhatTheRightPanelPointerReports()
    {
        // The offset itself, confirmed rather than ported. The recording was taken with the
        // inventory open, and RIGHT is what comes back - not None, which is what a wrong offset
        // gives, and not a second panel, which is what a path landing on something always
        // showing would give.
        using ReplayMemoryReader replay = Load();
        (PanelReader panels, _, ulong uiRoot) = Attach(replay);

        PanelsOnScreen read = panels.Read(uiRoot, Viewport);

        Assert.Equal(GamePanel.Right, read.Panels);
    }

    [Fact]
    public void ANDTheOtherPanelsAreQuietInTheSameFrame()
    {
        // The other half of that confirmation: a pointer field that reports open whatever the
        // game is doing proves nothing. Nothing was open on the left, and the tree and the world
        // map exist as elements but are not showing - so the visibility walk is what is being
        // asked, rather than whether a pointer happens to be non-null.
        using ReplayMemoryReader replay = Load();
        (_, UiElementReader elements, ulong uiRoot) = Attach(replay);
        StructDef root = RealSessionTests.Schema().Structs["ImportantUiElements"];

        Assert.Equal(0UL, replay.ReadPointer(uiRoot + (ulong)root.OffsetOf("LeftPanelPtr")));

        foreach (string field in new[] { "WorldMapPanelPtr", "PassiveSkillTreePanel" })
        {
            ulong element = replay.ReadPointer(uiRoot + (ulong)root.OffsetOf(field));
            Assert.True(elements.IsUiElement(element), $"{field} did not resolve to a UiElement");
            Assert.False(elements.IsVisible(element), $"{field} reported showing");
        }
    }

    [Fact]
    public void ANDTheEXTENTIsAnAssumptionHereBecauseTheRecordingPredatesTheRead()
    {
        // Not a measurement, and the test says so rather than hiding it: this file holds no
        // position, size or child vector for that element, so the reader falls through to the
        // whole window. A recording carries only what the build that made it read - so the
        // question "how big is PoE2's inventory panel" is still open, and a fresh capture is
        // what closes it. See the remarks on this class.
        using ReplayMemoryReader replay = Load();
        (PanelReader panels, _, ulong uiRoot) = Attach(replay);

        PanelArea area = Assert.Single(panels.Read(uiRoot, Viewport).Areas);

        Assert.Equal(PanelExtent.Unmeasured, area.From);
        Assert.Equal((0f, 0f, 2560f, 1440f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    /// <summary>The same session with the ATLAS open, on a 3440x1440 screen.</summary>
    /// <remarks>
    /// The capture that settles the ultrawide, and it took one: the atlas element states an
    /// extent, so nothing about it looks unreadable - it is simply 733 pixels narrower than the
    /// screen it is drawn across, and only a window at the far right notices.
    /// </remarks>
    private static string AtlasFixturePath => FixturePath.Replace(
        "session-2026-08-panel.rec", "session-2026-08-atlas-panel.rec", StringComparison.Ordinal);

    private static ReplayMemoryReader LoadAtlas()
        => ReplayMemoryReader.Load(File.OpenRead(AtlasFixturePath));

    /// <summary>The screen that session was played on. The width is the whole point of it.</summary>
    private static UiScale Ultrawide => new(3440, 1440, 0);

    [Fact]
    public void THEAtlasElementReportsItselfNarrowerThanTheScreenItCovers()
    {
        // The measurement that would have been believed, kept as a number rather than a story:
        // UnscaledSize 3008x1600 at ScaleIndex 2 - both axes by the height factor - is 2707x1440
        // here. The atlas is drawn over the remaining 733 pixels all the same, which is why the
        // window parked at the far right stayed visible while the one in the corner went.
        using ReplayMemoryReader replay = LoadAtlas();
        OffsetSchema schema = RealSessionTests.Schema();
        (_, UiElementReader elements, ulong uiRoot) = Attach(replay);

        StructDef atlas = schema.Structs["AtlasPanel"];
        ulong element = uiRoot;
        foreach (string step in new[] { "PathFromUiRoot0", "PathFromUiRoot1", "PathFromUiRoot2" })
        {
            element = elements.Child(element, (int)atlas.Constants[step]);
        }

        UiElement panel = Assert.IsType<UiElement>(elements.Read(element, Ultrawide));

        Assert.True(panel.Visible);
        Assert.Equal(2707.2f, panel.Size.X, 1);
        Assert.Equal(1440f, panel.Size.Y, 1);
        Assert.True(panel.Size.X < Ultrawide.WindowWidth - 700f, "the shortfall this test exists for");
    }

    [Fact]
    public void ANDITIsTakenAsTheWholeScreenAnyway()
    {
        // Which is the fix: a panel of this kind is not measured, because the measurement is
        // known to understate it. The area says "by kind" so the readout never presents the
        // decision as a reading.
        using ReplayMemoryReader replay = LoadAtlas();
        (PanelReader panels, _, ulong uiRoot) = Attach(replay);

        PanelsOnScreen read = panels.Read(uiRoot, Ultrawide);

        Assert.Equal(GamePanel.Atlas, read.Panels);
        PanelArea area = Assert.Single(read.Areas);
        Assert.Equal(PanelExtent.Kind, area.From);
        Assert.Equal((0f, 0f, 3440f, 1440f), (area.Left, area.Top, area.Right, area.Bottom));
    }

    [Fact]
    public void ANDTheAtlasNodesSitOutsideTheRectangleTheirParentClaims()
    {
        // Why the element understates itself, rather than that it does. The panel is a viewport
        // onto a map that pans: its own children - the atlas nodes - are placed from well left of
        // it to well right of it, so its size describes a frame and not what is on the screen.
        // This is also what rules out measuring it from its children instead.
        using ReplayMemoryReader replay = LoadAtlas();
        OffsetSchema schema = RealSessionTests.Schema();
        (_, UiElementReader elements, ulong uiRoot) = Attach(replay);

        StructDef atlas = schema.Structs["AtlasPanel"];
        ulong element = uiRoot;
        foreach (string step in new[] { "PathFromUiRoot0", "PathFromUiRoot1", "PathFromUiRoot2" })
        {
            element = elements.Child(element, (int)atlas.Constants[step]);
        }

        List<ulong> children = elements.Children(element, 64);
        Assert.NotEmpty(children);

        var placed = elements.ReadSiblings(element, children, Ultrawide);
        Assert.Contains(placed, child => child.Value.Position.X < -400f);
        Assert.Contains(placed, child => child.Value.Position.Y < -400f);
    }
}
