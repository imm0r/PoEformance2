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

        Assert.Equal(PanelExtent.Screen, area.From);
        Assert.Equal((0f, 0f, 2560f, 1440f), (area.Left, area.Top, area.Right, area.Bottom));
    }
}
