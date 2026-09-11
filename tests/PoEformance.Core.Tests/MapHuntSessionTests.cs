using System.Numerics;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The --maphunt capture from the 0.5.5 client (<c>tests/fixtures/session-2026-09-maphunt.rec</c>:
/// 508 frames, 30 KB, 2026-09-11), made while its owner opened, zoomed and dragged both maps.
/// </summary>
/// <remarks>
/// The capture the two before it asked for, and it closes the map: the reference's route -
/// child 6 of the UI manager, then its children 0 and 1 - leads to the large map (screen
/// centre, no size of its own, DefaultShift (0, -20) at exactly the reference's 0x358) and
/// the minimap (402x402 in the top-right corner), and their visible bits swap as the large
/// map opens and closes. The map-parent chain the schema used to walk leads instead to a
/// marker layer whose children are named after checkpoints. And the StringId question is
/// settled by the same bytes: 0x098 names elements, 0x128 is empty on all eighteen.
///
/// What it did NOT show: Shift or Zoom moving. Both stayed (0, 0) and 0.5 on both maps for
/// the whole run, and no other float in the map's field range changed while the large map's
/// own position did - so either this client has no map zoom and no drag, or they are not
/// where the reference says. The radar reads 0.5 either way, and the test records the fact
/// rather than a conclusion.
/// </remarks>
public class MapHuntSessionTests
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-maphunt.rec");
        }
    }

    private static (ReplayMemoryReader Replay, OffsetSchema Schema) Open()
        => (ReplayMemoryReader.Load(File.OpenRead(FixturePath)), RealSessionTests.LiveSchema());

    private static ulong Manager(ReplayMemoryReader replay, OffsetSchema schema)
        => GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]).UiRoot;

    [Fact]
    public void TheReferenceRouteLeadsToTheTwoMaps()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        replay.Seek((uint)(replay.FrameCount - 1));
        ulong manager = Manager(replay, schema);
        var elements = new UiElementReader(replay, schema);
        StructDef route = schema.Structs["MapViewports"];

        ulong viewports = elements.Child(manager, (int)route.Constants["ChildOfUiManager"]);
        ulong large = elements.Child(viewports, (int)route.Constants["LargeMapChild"]);
        ulong mini = elements.Child(viewports, (int)route.Constants["MiniMapChild"]);
        Assert.NotEqual(0UL, large);
        Assert.NotEqual(0UL, mini);

        // The radar picks exactly those, over the map-parent chain that also resolves.
        var radar = new MapRadarReader(replay, schema);
        Assert.Equal((large, mini), radar.Resolve(manager));

        var scale = new UiScale(3440, 1440, 0);
        MapView miniView = radar.Read(manager, scale, largeMap: false)!.Value;
        MapView largeView = radar.Read(manager, scale, largeMap: true)!.Value;

        // The minimap: a square in the top-right corner, its diagonal the projection's scale.
        Assert.Equal(361.8f, miniView.Width, 1);
        Assert.Equal(361.8f, miniView.Height, 1);
        Assert.True(miniView.Left > 3000f && miniView.Top < 20f);
        Assert.Equal(0.5f, miniView.Zoom, 3);

        // The large map: positioned at the screen centre and sized by the window; its resting
        // shift (0, -20) is the one thing that separates it from every other element here.
        Assert.Equal(1719.9f, largeView.Centre.X, 1);
        Assert.Equal(700f, largeView.Centre.Y, 1);
        Assert.Equal(miniView.Diagonal, largeView.Diagonal, 3);
        Assert.Equal(0.5f, largeView.Zoom, 3);
    }

    [Fact]
    public void TheDefaultShiftPinsTheLayout_AndNothingElseMovedWhenItShouldHave()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        StructDef map = schema.Structs["MapUiElement"];
        var radar = new MapRadarReader(replay, schema);

        var shifts = new HashSet<(float, float)>();
        var zooms = new HashSet<float>();
        int miniShowing = 0, largeShowing = 0;
        var elements = new UiElementReader(replay, schema);
        Span<float> pair = stackalloc float[2];

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            (ulong large, ulong mini) = radar.Resolve(Manager(replay, schema));
            if (large == 0)
            {
                continue; // the first frames precede the interface
            }

            Assert.True(replay.TryRead(large + (ulong)map.OffsetOf("DefaultShift"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair)));
            Assert.Equal(0f, pair[0]);
            Assert.Equal(-20f, pair[1]);

            foreach (ulong element in new[] { large, mini })
            {
                Assert.True(replay.TryRead(element + (ulong)map.OffsetOf("Shift"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair)));
                shifts.Add((pair[0], pair[1]));
                Assert.True(replay.TryRead(element + (ulong)map.OffsetOf("Zoom"), out float zoom));
                zooms.Add(zoom);
            }

            if (elements.IsShowingItself(mini)) miniShowing++;
            if (elements.IsShowingItself(large)) largeShowing++;
        }

        // The maps swap as the large one opens and closes - so the person was working them.
        Assert.Equal(306, miniShowing);
        Assert.Equal(194, largeShowing);

        // And still neither field moved. Recorded, not explained.
        Assert.Equal([(0f, 0f)], shifts);
        Assert.Equal([0.5f], zooms);
    }

    [Fact]
    public void TheHuntReportsTheLargeMapByItsRestingShift_AndTheOldChainAsAMarkerLayer()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        var hunt = new MapHunt(replay, schema);
        var samples = new List<MapHuntSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame += 25)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(replay.ResolvedStatics["GameStates"]) is { } sample)
            {
                samples.Add(sample);
            }
        }

        MapHuntSample last = samples[^1];
        Assert.Equal(124, last.ManagerChildren);

        MapCandidate largeMap = Assert.Single(last.Candidates, c => c.Route == "UiManager/6/0");
        Assert.Equal([0x358], largeMap.ShiftSignatures);
        Assert.Equal(Vector2.Zero, largeMap.UnscaledSize);

        MapCandidate miniMap = Assert.Single(last.Candidates, c => c.Route == "UiManager/6/1");
        Assert.Equal(new Vector2(402, 402), miniMap.UnscaledSize);
        Assert.Empty(miniMap.ShiftSignatures);

        // The chain the schema used to walk: a full-screen layer whose child is a checkpoint
        // marker - the map's icons, not the map.
        MapCandidate oldLarge = Assert.Single(last.Candidates, c => c.Route == "MapParentPtr.LargeMapPtr");
        Assert.Equal(1600f, oldLarge.UnscaledSize.Y, 1);
        Assert.Empty(oldLarge.ShiftSignatures);
        MapCandidate marker = Assert.Single(last.Candidates, c => c.Route == "MapParentPtr.LargeMapPtr/0");
        Assert.Equal("Metadata/MiscellaneousObjects/Checkpoint", marker.IdAt098);

        // StringId: this schema's slot is the one that names elements (the marker above; the
        // owner's console run of the same hunt also saw 'info_stats_layout' and 'info_layout'
        // there), and the reference's 0x128 is empty on every element captured.
        Assert.All(last.Candidates, c => Assert.Equal(string.Empty, c.IdAt128));

        var report = new StringWriter();
        MapHunt.Report(samples, report);
        Assert.Contains("(0, -20) at: 0x358", report.ToString());
        Assert.Contains("no zoom-like float changed", report.ToString());
    }
}
