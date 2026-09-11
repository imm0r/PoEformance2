using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The second 0.5.5 capture (<c>tests/fixtures/session-2026-09-panels.rec</c>: 1,312 frames,
/// 378 KB, 2026-09-11), made with the re-based ImportantUiElements and reported by its owner
/// as "the overlay draws nothing at all".
/// </summary>
/// <remarks>
/// It answers that report, one gate at a time. The chain resolves and the area is Clearfell,
/// hostile, so the world overlay WANTS markers. No panel is open, and the panel walk reads
/// sensible flags - the UI manager turns out to be a UiElement itself with 124 children, the
/// HUD sits at child 97 and is the one visible thing, the tree and world-map panels are
/// hidden - so the panel gate is open too. What is left is the map: MapParentPtr still leads
/// to two elements, but they read a full-screen size each, a zoom of exactly zero at the
/// reference's 0.5.5 offset, and they sit 0x2E0 bytes apart - too small to be MapUiElements.
/// A zoom of 1E-44 passed the old "greater than zero" check and put every marker on one pixel.
/// That is the overlay drawing nothing.
///
/// The interface tree was not walked here either (UiRootPtr unread), so the UiElement tail is
/// confirmed only as far as Flags at 0x168 - which it is, by the HUD being the visible child
/// and the closed panels not. The map's own fields are the open question, and --maphunt is
/// the capture that closes it.
/// </remarks>
public class PanelsSessionTests
{
    internal static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-panels.rec");
        }
    }

    private static (ReplayMemoryReader Replay, OffsetSchema Schema, GameChainAddresses Chain) Open()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.LiveSchema();
        replay.Seek((uint)(replay.FrameCount - 1));
        return (replay, schema, GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]));
    }

    [Fact]
    public void TheWorldWantsMarkers_SoTheAreaGateIsOpen()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();

        Assert.True(chain.InGame);
        AreaInfo area = new WorldAreaReader(replay, schema).Read(chain.WorldData);
        Assert.Equal("G1_2", area.Id);
        Assert.Equal("Clearfell", area.Name);
        Assert.True(area.WantsMarkers);
    }

    [Fact]
    public void NoPanelIsOpen_AndTheFlagsSayWhichElementsAreShowing()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        var elements = new UiElementReader(replay, schema);
        ulong manager = chain.UiRoot;

        Assert.Equal(GamePanel.None, new PanelReader(replay, schema, elements).Read(manager, null).Panels);

        // The manager is an element in its own right - which is what the reference walks its
        // child paths from - and lists the whole interface as its children.
        Assert.True(elements.IsUiElement(manager));
        StructDef ui = schema.Structs["UiElementBase"];
        ulong first = replay.ReadPointer(manager + (ulong)ui.OffsetOf("ChildrenFirst"));
        ulong last = replay.ReadPointer(manager + (ulong)ui.OffsetOf("ChildrenLast"));
        Assert.Equal(124UL, (last - first) / 8);

        // Flags at 0x168 tell the showing from the hidden exactly as they should: the HUD, at
        // the index the schema records for it, is visible; the tree's node container says it
        // is showing but hangs under a hidden panel; the world map is hidden.
        int hudIndex = (int)schema.Structs["HudElement"].Constants["ChildFromUiRoot"];
        Assert.True(elements.IsShowingItself(elements.Child(manager, hudIndex)));

        StructDef important = schema.Structs["ImportantUiElements"];
        ulong tree = replay.ReadPointer(manager + (ulong)important.OffsetOf("PassiveSkillTreePanel"));
        ulong nodes = elements.Child(tree, (int)schema.Structs["BigPanels"].Constants["SkillTreeNodesChild"]);
        Assert.True(elements.IsShowingItself(nodes));
        Assert.False(elements.IsShowingItself(tree));
        Assert.False(elements.IsVisible(nodes));
        Assert.False(elements.IsShowingItself(replay.ReadPointer(manager + (ulong)important.OffsetOf("WorldMapPanelPtr"))));

        // The re-based panel pointers read null with nothing open - consistent, not yet a
        // confirmation; that needs a capture with the inventory open.
        Assert.Equal(0UL, replay.ReadPointer(manager + (ulong)important.OffsetOf("LeftPanelPtr")));
        Assert.Equal(0UL, replay.ReadPointer(manager + (ulong)important.OffsetOf("RightPanelPtr")));
    }

    [Fact]
    public void TheMapParentChainLeadsToElementsThatAreNotMaps()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        var elements = new UiElementReader(replay, schema);
        StructDef important = schema.Structs["ImportantUiElements"];
        StructDef parentStruct = schema.Structs["MapParentStruct"];
        StructDef map = schema.Structs["MapUiElement"];
        StructDef ui = schema.Structs["UiElementBase"];

        ulong parent = replay.ReadPointer(chain.UiRoot + (ulong)important.OffsetOf("MapParentPtr"));
        Assert.True(elements.IsUiElement(parent));
        ulong large = replay.ReadPointer(parent + (ulong)parentStruct.OffsetOf("LargeMapPtr"));
        ulong mini = replay.ReadPointer(parent + (ulong)parentStruct.OffsetOf("MiniMapPtr"));
        Assert.True(elements.IsUiElement(large));
        Assert.True(elements.IsUiElement(mini));

        // Two objects 0x2E0 apart cannot each carry a field at 0x390.
        Assert.Equal(0x2E0UL, mini - large);
        Assert.True(map.OffsetOf("Zoom") > 0x2E0);

        Span<float> size = stackalloc float[2];
        foreach (ulong element in new[] { large, mini })
        {
            Assert.True(replay.TryRead(element + (ulong)map.OffsetOf("Zoom"), out float zoom));
            Assert.True(zoom < MapRadarReader.LeastPlausibleZoom);

            Assert.True(replay.TryRead(element + (ulong)ui.OffsetOf("UnscaledSize"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(size)));
            Assert.Equal(1600f, size[1], 1); // the base resolution's full height: a screen, not a minimap
        }

        // So the reader no longer believes them - and with the reference's route unread in this
        // file, it falls back to them with a zoom the projection can survive.
        var radar = new MapRadarReader(replay, schema);
        Assert.Equal((large, mini), radar.Resolve(chain.UiRoot));
        Assert.Equal(0.5f, radar.Read(chain.UiRoot, new UiScale(3440, 1440, 0), largeMap: false)!.Value.Zoom, 3);
    }

    [Fact]
    public void TheReferenceRouteWasNeverRead_SoTheHuntIsWhatSettlesTheMap()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        StructDef ui = schema.Structs["UiElementBase"];
        int slot = (int)schema.Structs["MapViewports"].Constants["ChildOfUiManager"];
        ulong first = replay.ReadPointer(chain.UiRoot + (ulong)ui.OffsetOf("ChildrenFirst"));

        Assert.False(replay.TryRead(first + (ulong)(8 * slot), out ulong _));
        Assert.False(replay.TryRead(chain.UiRoot + (ulong)schema.Structs["UiRootStruct"].OffsetOf("UiRootPtr"), out ulong _));

        // The hunt on this file walks the one route it holds and reports the elements as
        // what they are: no resting shift, no zoom-like value at the reference's offset.
        var hunt = new PoEformance.Game.Diagnostics.MapHunt(replay, schema);
        PoEformance.Game.Diagnostics.MapHuntSample? sample = hunt.SampleFrame(replay.ResolvedStatics["GameStates"]);
        Assert.NotNull(sample);
        Assert.Equal(124, sample.ManagerChildren);
        Assert.Contains(sample.Candidates, c => c.Route == "MapParentPtr.MiniMapPtr");
        Assert.DoesNotContain(sample.Candidates, c => c.Route.StartsWith("UiManager/", StringComparison.Ordinal));

        var report = new StringWriter();
        PoEformance.Game.Diagnostics.MapHunt.Report([sample], report);
        Assert.Contains("MapParentPtr.MiniMapPtr", report.ToString());
    }
}
