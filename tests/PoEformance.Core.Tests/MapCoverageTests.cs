using PoEformance.Core.Memory;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// How much of a map has been walked - measured against what can actually be REACHED.
/// </summary>
/// <remarks>
/// The denominator is the whole difficulty, and it is what the reference tool got wrong first.
/// Path of Exile 2's walkable grid holds a great deal of ground nobody can get to - scenery
/// floors, the far side of walls, every storey of a multi-level map flattened into one plane -
/// so dividing by all of it makes a finished map read as a few per cent, and sends whoever
/// sees that looking for a bug in the marking.
/// </remarks>
public class MapCoverageTests
{
    /// <summary>
    /// A grid where walkable ground is described by a predicate, one byte per two cells.
    /// </summary>
    private static TerrainGrid Ground(int width, int height, Func<int, int, bool> walkable)
    {
        int bytesPerRow = (width + 1) / 2;
        var cells = new byte[bytesPerRow * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!walkable(x, y))
                {
                    continue;
                }

                int index = (y * bytesPerRow) + (x >> 1);
                cells[index] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
            }
        }

        return new TerrainGrid(cells, bytesPerRow, height, width / TerrainGrid.CellsPerTile, height / TerrainGrid.CellsPerTile);
    }

    private static WorldSnapshot At(TerrainGrid grid, int cellX, int cellY, uint area = 1)
    {
        var player = new WorldEntity(
            1, 0x100, "Metadata/Characters/Player", EntityKind.Player,
            cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid, 0f);

        return new WorldSnapshot(
            true, player, [], new float[16],
            Area: new AreaInfo("MapRiverhold", "Riverhold", 10, false, false),
            Terrain: grid,
            AreaHash: area);
    }

    /// <summary>Two rooms with no way between them - the shape the denominator is about.</summary>
    private static TerrainGrid TwoRooms()
        => Ground(400, 200, (x, y) => x is >= 10 and < 190 || x is >= 210 and < 390);

    [Fact]
    public void AFreshAreaIsNothingSeen()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        Assert.Equal(0f, coverage.Percent);
        Assert.False(coverage.Measuring);
    }

    [Fact]
    public void WalkingCoversGround()
    {
        TerrainGrid grid = Ground(400, 200, (x, y) => true);
        var coverage = new MapCoverage(MapCoverage.Immediate);

        coverage.Look(At(grid, 20, 100));
        float afterOne = coverage.Percent;

        for (int x = 20; x < 380; x += 40)
        {
            coverage.Look(At(grid, x, 100));
        }

        Assert.True(afterOne > 0f, "standing somewhere should cover something");
        Assert.True(coverage.Percent > afterOne, "walking should cover more");
    }

    [Fact]
    public void GroundYouCannotREACHDoesNotCount()
    {
        // THE test. Two rooms, no way between them, and the player never leaves the first.
        // Counting the far room makes a fully cleared room read as half a map, which is the
        // reading that makes the whole feature useless.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = TwoRooms();

        for (int y = 10; y < 190; y += 20)
        {
            for (int x = 12; x < 188; x += 20)
            {
                coverage.Look(At(grid, x, y));
            }
        }

        Assert.True(coverage.RegionKnown);
        Assert.True(coverage.Percent > 95f, $"the reachable room was cleared, and it reads as {coverage.Percent:F0}%");
    }

    [Fact]
    public void AndTheSameWalkAgainstTheWHOLEGridWouldReadAsHalf()
    {
        // The counter-example, so the test above is measuring the right thing rather than
        // passing for some other reason. Without the region the same walk reads as ~half,
        // which is the number the reference shipped and then had to explain.
        TerrainGrid grid = TwoRooms();

        int walkable = 0;
        int reachable = 0;
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                if (!grid.IsWalkable(x, y))
                {
                    continue;
                }

                walkable++;
                if (x < 200)
                {
                    reachable++;
                }
            }
        }

        Assert.True(reachable * 2 < walkable + (walkable / 10), "the fixture should have two rooms of roughly equal size");
    }

    [Fact]
    public void SightThatREACHESTHROUGHAWallDoesNotCount()
    {
        // The sight disc is a circle, so standing beside the wall between two rooms marks
        // cells in BOTH - and the far one is not reachable. Counted without the region
        // filter, the numerator overruns its own denominator; the percentage is clamped, so
        // the symptom is not a number above a hundred but a map that reads as finished while
        // half of it has not been walked.
        // A SMALL sealed room beside a large open area, so what the disc reaches through the
        // wall dwarfs what it can legitimately see. A large room next door would still be
        // over-counted and the total would stay under the denominator, which is a bug the
        // arithmetic hides rather than one it reports.
        //
        // The wall is fourteen cells thick on purpose. Coarse cells are four to a side and
        // count as ground when ANY of their sixteen can be stood on, so a wall thinner than
        // two coarse cells leaves a walkable column bridging it - and the room would not be
        // sealed at all.
        TerrainGrid grid = Ground(400, 200, (x, y) =>
            (x is >= 12 and < 42 && y is >= 80 and < 112)   // the room
            || (x is >= 56 and < 390 && y is >= 10 and < 190));   // everything beyond the wall

        var coverage = new MapCoverage(MapCoverage.Immediate);
        coverage.Look(At(grid, 26, 96));

        Assert.True(coverage.RegionKnown);
        Assert.True(coverage.ReachableCells < 200, $"the sealed room should be small, not {coverage.ReachableCells} cells");
        Assert.True(
            coverage.SeenCells <= coverage.ReachableCells,
            $"seen {coverage.SeenCells} of a reachable {coverage.ReachableCells} - the disc counted through the wall");
    }

    [Fact]
    public void TheRunningCountAgreesWithCountingItAgain()
    {
        // The count is kept as cells are marked rather than worked out on each ask, because
        // whoever is drawing asks several times a frame and a coarse grid on a large map runs
        // to a hundred thousand cells. What that buys in speed it can lose in truth, so the
        // two are compared: mark a lot, in an area where the sight disc reaches through a
        // wall, and the running figure has to match a fresh count of the same cells.
        TerrainGrid grid = TwoRooms();
        var coverage = new MapCoverage(MapCoverage.Immediate);

        for (int y = 20; y < 180; y += 15)
        {
            for (int x = 20; x < 185; x += 15)
            {
                coverage.Look(At(grid, x, y));
            }
        }

        // Counted independently here: every coarse cell of the reachable region that the
        // walk above could have marked. Room A is x below 190, so a cell is in the region
        // exactly when it is walkable and left of the wall.
        int reachable = 0;
        for (int cy = 0; cy < grid.Height / MapCoverage.CoarseStep; cy++)
        {
            for (int cx = 0; cx < grid.Width / MapCoverage.CoarseStep; cx++)
            {
                if (cx * MapCoverage.CoarseStep < 190 && AnyWalkable(grid, cx, cy))
                {
                    reachable++;
                }
            }
        }

        Assert.Equal(reachable, coverage.ReachableCells);
        Assert.True(coverage.SeenCells <= coverage.ReachableCells);
        Assert.True(coverage.Percent > 90f, $"a thorough walk of the room reads as {coverage.Percent:F0}%");
    }

    private static bool AnyWalkable(TerrainGrid grid, int coarseX, int coarseY)
    {
        for (int dy = 0; dy < MapCoverage.CoarseStep; dy++)
        {
            for (int dx = 0; dx < MapCoverage.CoarseStep; dx++)
            {
                if (grid.IsWalkable((coarseX * MapCoverage.CoarseStep) + dx, (coarseY * MapCoverage.CoarseStep) + dy))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void ItNeverGoesPastEverything()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = Ground(200, 200, (x, y) => true);

        for (int y = 0; y < 200; y += 5)
        {
            for (int x = 0; x < 200; x += 5)
            {
                coverage.Look(At(grid, x, y));
            }
        }

        Assert.InRange(coverage.Percent, 0f, 100f);
    }

    [Fact]
    public void ComingBackFromTownRESUMES()
    {
        // Leaving a half-explored map to sell and coming back is ordinary play. A figure that
        // resets on the way back is one nobody trusts afterwards.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid map = Ground(400, 200, (x, y) => true);
        TerrainGrid town = Ground(100, 100, (x, y) => true);

        for (int x = 20; x < 200; x += 20)
        {
            coverage.Look(At(map, x, 100, area: 7));
        }

        float before = coverage.Percent;
        Assert.True(before > 0f);

        coverage.Look(At(town, 50, 50, area: 99));
        coverage.Look(At(map, 200, 100, area: 7));

        Assert.True(coverage.Percent >= before, $"came back to {coverage.Percent:F0}% having left at {before:F0}%");
    }

    [Fact]
    public void ADIFFERENTRunOfTheSameMapStartsOver()
    {
        // Keyed by the instance hash, not by the layout: two runs of the same map are two
        // maps, and carrying the first one's progress into the second would be a lie.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = Ground(400, 200, (x, y) => true);

        for (int x = 20; x < 380; x += 20)
        {
            coverage.Look(At(grid, x, 100, area: 1));
        }

        float first = coverage.Percent;
        coverage.Look(At(grid, 20, 100, area: 2));

        Assert.True(coverage.Percent < first, "a fresh instance should not inherit the last one's progress");
    }

    [Fact]
    public void ItDoesNotRememberEveryAreaForever()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = Ground(120, 120, (x, y) => true);

        for (uint area = 1; area <= MapCoverage.MaxRemembered + 20; area++)
        {
            coverage.Look(At(grid, 60, 60, area));
        }

        Assert.Equal(MapCoverage.MaxRemembered, coverage.Remembered);
    }

    [Fact]
    public void GroundWalkedWHILETheFloodRanStillCounts()
    {
        // The real case, and the one the immediate scheduler cannot show: the flood takes a
        // moment and the player is already moving. Everything walked in the meantime was
        // counted against the whole grid, so adopting the region has to count it again -
        // otherwise the figure DROPS to whatever was walked after the flood landed, and the
        // longer the flood took the more progress silently disappears.
        var later = new HeldBack();
        var coverage = new MapCoverage(later);
        TerrainGrid grid = TwoRooms();

        for (int y = 20; y < 180; y += 15)
        {
            for (int x = 20; x < 185; x += 15)
            {
                coverage.Look(At(grid, x, y));
            }
        }

        Assert.False(coverage.RegionKnown);
        Assert.True(coverage.SeenCells > 100, "the walk should have covered a good deal of the room");

        later.RunIt();
        coverage.Look(At(grid, 100, 100));   // the next look is when the region is adopted

        Assert.True(coverage.RegionKnown);

        // The raw count is EXPECTED to fall here, and that is the region doing its job:
        // cells seen through the wall into the far room stop counting. What must survive is
        // the progress inside the region - so the claim is about the percentage, which is
        // what anybody actually reads.
        Assert.True(
            coverage.Percent > 90f,
            $"a thorough walk read as {coverage.Percent:F0}% once the region landed - progress made during the flood was lost");
    }

    /// <summary>A scheduler that holds the work until a test asks for it.</summary>
    private sealed class HeldBack
    {
        private Action? _work;

        public static implicit operator Action<Action>(HeldBack held) => work => held._work = work;

        public void RunIt()
        {
            Action? work = _work;
            _work = null;
            work?.Invoke();
        }
    }

    [Fact]
    public void BeforeTheRegionIsKnownItUNDERReports()
    {
        // While the flood is still running the figure is against the whole grid. That is on
        // purpose: a number that starts low and jumps up is better than one that starts at
        // ninety and falls, which reads as the tool losing track.
        var coverage = new MapCoverage(_ => { });   // never runs the flood
        TerrainGrid grid = TwoRooms();

        for (int y = 10; y < 190; y += 20)
        {
            for (int x = 12; x < 188; x += 20)
            {
                coverage.Look(At(grid, x, y));
            }
        }

        Assert.False(coverage.RegionKnown);
        Assert.InRange(coverage.Percent, 1f, 70f);
    }

    [Fact]
    public void NoTerrainIsNotAnError()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        coverage.Look(WorldSnapshot.Empty);

        Assert.False(coverage.Measuring);
        Assert.Equal(0f, coverage.Percent);
    }

    [Fact]
    public void ACORRIDORNarrowerThanACoarseCellIsStillGround()
    {
        // Coarse cells are four terrain cells to a side, and a cell counts as ground when ANY
        // of its sixteen can be stood on. Requiring all sixteen would erase every thin passage
        // in the area, and thin passages are most of a map - the region flood would then stop
        // at the first corridor and call a whole map unreachable.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = Ground(400, 200, (x, y) => y is >= 98 and < 101);

        coverage.Look(At(grid, 20, 99));
        coverage.Look(At(grid, 380, 99));

        Assert.True(coverage.RegionKnown);
        Assert.True(coverage.Percent > 0f, "a three-cell corridor should be walkable ground");
    }
}

/// <summary>
/// What the recorded session can and cannot say about this.
/// </summary>
/// <remarks>
/// A WARNING FOR WHOEVER TRIES NEXT, and the second time this has cost someone an hour - see
/// MonsterRarityTests for the first. A recording only holds the bytes that were read while it
/// was being made, and this fixture was captured before anything read the terrain: the
/// snapshot comes back with no grid at all. So a coverage test written against it does not
/// fail loudly, it simply measures nothing, and the pass means nothing either way.
///
/// The denominator - which is all this class really is - is checked against synthetic shapes
/// above, where "two rooms with no way between them" can be stated exactly. What the fixture
/// is used for here is the one thing it can still answer: that asking for coverage on a
/// snapshot with no terrain is quiet rather than a crash.
/// </remarks>
public class MapCoverageAgainstARealSessionTests
{
    private static WorldSnapshot Scene()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        return new WorldReader(replay, RealSessionTests.Schema())
            .Read(replay.ResolvedStatics["GameStates"]);
    }

    [Fact]
    public void TheFixtureHasNoTerrainToMeasure()
        => Assert.Null(Scene().Terrain);

    [Fact]
    public void WhichIsQuietRatherThanACrash()
    {
        // The real case this covers is not the fixture, it is the first seconds in any area:
        // the terrain read has its own cadence, so several snapshots arrive with entities and
        // no grid before the first one that has both.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        coverage.Look(Scene());

        Assert.False(coverage.Measuring);
        Assert.Equal(0f, coverage.Percent);
    }
}

/// <summary>The flood has to be a flood, at the size a real area is.</summary>
public class MapCoverageCostTests
{
    [Fact]
    public void AMapSizedGridFloodsQuickly()
    {
        // Once per area and off the drawing thread, so this is not a frame budget - it is a
        // check that nothing here is accidentally quadratic. A real grid runs to a few
        // hundred cells a side, and the coarse grid is a quarter of that again.
        int bytesPerRow = 800;
        var cells = new byte[bytesPerRow * 1_400];
        Array.Fill(cells, (byte)0x11);
        var grid = new TerrainGrid(cells, bytesPerRow, 1_400, 1_600 / TerrainGrid.CellsPerTile, 1_400 / TerrainGrid.CellsPerTile);

        var player = new WorldEntity(
            1, 0x100, "Metadata/Characters/Player", EntityKind.Player,
            700 * MapView.WorldToGrid, 700 * MapView.WorldToGrid, 0f);
        var snapshot = new WorldSnapshot(
            true, player, [], new float[16],
            Area: new AreaInfo("MapBig", "Big", 10, false, false), Terrain: grid, AreaHash: 3);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var coverage = new MapCoverage(MapCoverage.Immediate);
        coverage.Look(snapshot);
        watch.Stop();

        Assert.True(coverage.RegionKnown);
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"the flood took {watch.ElapsedMilliseconds} ms");
    }
}

/// <summary>Where there is still ground to cover, as the map layer asks for it.</summary>
/// <remarks>
/// The drawing itself needs a GPU and is not testable here. What IS testable is the answer it
/// draws from, and the two ways it can be wrong are both silent: marking ground that cannot be
/// reached sends somebody across a map for nothing, and marking ground already walked never
/// stops.
/// </remarks>
public class StillToWalkTests
{
    private static TerrainGrid Everything(int width, int height)
    {
        int bytesPerRow = (width + 1) / 2;
        var cells = new byte[bytesPerRow * height];
        Array.Fill(cells, (byte)0x11);
        return new TerrainGrid(cells, bytesPerRow, height, width / TerrainGrid.CellsPerTile, height / TerrainGrid.CellsPerTile);
    }

    private static WorldSnapshot At(TerrainGrid grid, int cellX, int cellY, uint area = 1)
    {
        var player = new WorldEntity(
            1, 0x100, "Metadata/Characters/Player", EntityKind.Player,
            cellX * MapView.WorldToGrid, cellY * MapView.WorldToGrid, 0f);

        return new WorldSnapshot(
            true, player, [], new float[16],
            Area: new AreaInfo("MapRiverhold", "Riverhold", 10, false, false),
            Terrain: grid, AreaHash: area);
    }

    [Fact]
    public void GroundWalkedPastStopsBeingMarked()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        TerrainGrid grid = Everything(400, 200);

        coverage.Look(At(grid, 200, 100));

        // The cell underfoot: reached, and definitely seen.
        Assert.False(coverage.StillToWalk(200 / MapCoverage.CoarseStep, 100 / MapCoverage.CoarseStep));

        // Far across the area, well outside the sight disc: reached, and not seen.
        Assert.True(coverage.StillToWalk(20, 10));
    }

    [Fact]
    public void NothingIsMarkedBeforeTheRegionIsKnown()
    {
        // While the denominator is still the whole grid, the marks would cover ground nobody
        // can get to - which is worse than no marks at all, because it is advice.
        var coverage = new MapCoverage(_ => { });
        coverage.Look(At(Everything(400, 200), 200, 100));

        Assert.False(coverage.RegionKnown);
        for (int y = 0; y < coverage.CoarseHeight; y += 5)
        {
            for (int x = 0; x < coverage.CoarseWidth; x += 5)
            {
                Assert.False(coverage.StillToWalk(x, y));
            }
        }
    }

    [Fact]
    public void GroundThatCannotBeReachedIsNeverMarked()
    {
        // The mark says "go there". Ground behind a wall is the one place it must not.
        var coverage = new MapCoverage(MapCoverage.Immediate);
        int bytesPerRow = 200;
        var cells = new byte[bytesPerRow * 200];
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 400; x++)
            {
                if (x is >= 10 and < 190 || x is >= 210 and < 390)
                {
                    cells[(y * bytesPerRow) + (x >> 1)] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
                }
            }
        }

        var grid = new TerrainGrid(cells, bytesPerRow, 200, 400 / TerrainGrid.CellsPerTile, 200 / TerrainGrid.CellsPerTile);
        coverage.Look(At(grid, 100, 100));

        Assert.True(coverage.RegionKnown);
        for (int y = 0; y < coverage.CoarseHeight; y++)
        {
            for (int x = 210 / MapCoverage.CoarseStep; x < coverage.CoarseWidth; x++)
            {
                Assert.False(coverage.StillToWalk(x, y), $"the far room at ({x},{y}) was marked as somewhere to go");
            }
        }
    }

    [Fact]
    public void AskingOutsideTheGridIsNotAnError()
    {
        var coverage = new MapCoverage(MapCoverage.Immediate);
        coverage.Look(At(Everything(200, 200), 100, 100));

        Assert.False(coverage.StillToWalk(-1, 5));
        Assert.False(coverage.StillToWalk(5, -1));
        Assert.False(coverage.StillToWalk(int.MaxValue, 5));
        Assert.False(coverage.StillToWalk(5, int.MaxValue));
    }
}
